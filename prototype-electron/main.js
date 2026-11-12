const { app, BrowserWindow, ipcMain, session, shell } = require("electron");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { TunnelManager, publicTunnel } = require("./tunnels");

const DEFAULT_INSTANCE = {
  name: "Locale",
  url: process.env.PROTO_URL || "https://localhost:8443",
};

const windows = new Map();
const tokens = new Map();

// Local Caddy uses its own CA; allow it only for loopback in this prototype.
// Production installs use a publicly trusted certificate and never hit this.
app.on("certificate-error", (event, _webContents, url, _error, _certificate, callback) => {
  try {
    const target = new URL(url);
    const local =
      target.hostname === "localhost" ||
      target.hostname === "127.0.0.1" ||
      target.hostname === "::1";
    if (local && process.env.PROTO_ALLOW_INSECURE_LOCALHOST !== "0") {
      event.preventDefault();
      callback(true);
      return;
    }
  } catch {
    // fall through to Chromium
  }
  callback(false);
});

const partitionFor = (url) =>
  `persist:instance-${crypto.createHash("sha256").update(url).digest("hex").slice(0, 16)}`;

const tunnels = new TunnelManager((items) => {
  for (const window of windows.values()) {
    if (!window.isDestroyed())
      window.webContents.send("desktop-message", JSON.stringify({
        type: "tunnels.state",
        items,
      }));
  }
});

let managerWindow = null;
let instancesFile = null;

function loadInstances() {
  try {
    const value = JSON.parse(fs.readFileSync(instancesFile, "utf8"));
    if (Array.isArray(value) && value.length > 0) return value;
  } catch {
    // first run or damaged file
  }
  return [DEFAULT_INSTANCE];
}

function saveInstances(items) {
  const directory = path.dirname(instancesFile);
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(instancesFile, JSON.stringify(items, null, 2));
}

function createInstanceWindow(instance, options = {}) {
  const partition = partitionFor(instance.url);
  const existing = windows.get(partition);
  if (existing && !existing.isDestroyed()) {
    existing.focus();
    return existing;
  }

  const window = new BrowserWindow({
    width: options.width || 1280,
    height: options.height || 860,
    show: options.show !== false,
    title: `InfraOrgIT — ${instance.name}`,
    webPreferences: {
      partition,
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  windows.set(partition, window);
  window.on("closed", () => windows.delete(partition));
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith("https://") || url.startsWith("http://"))
      void shell.openExternal(url);
    return { action: "deny" };
  });
  window.webContents.on("did-fail-load", (_event, code, description, url) => {
    console.error("window load failed", instance.name, code, description, url);
  });
  window.webContents.on("did-finish-load", () => {
    console.log("loaded", instance.name, window.webContents.getURL());
  });
  window.loadURL(instance.url);
  return window;
}

function createManagerWindow() {
  managerWindow = new BrowserWindow({
    width: 520,
    height: 560,
    title: "InfraOrgIT Desktop",
    webPreferences: {
      preload: path.join(__dirname, "manager-preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  managerWindow.loadFile("manager.html");
  managerWindow.on("closed", () => {
    managerWindow = null;
  });
}

function senderUrl(sender) {
  try {
    return new URL(sender.getURL()).origin;
  } catch {
    return null;
  }
}

async function fetchTargets(url, token) {
  if (!token || !token.trim()) throw new Error("Token personale richiesto.");
  const response = await fetch(`${url.replace(/\/$/, "")}/api/tunnels/targets`, {
    headers: { Authorization: `Bearer ${token.trim()}` },
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(
      body.detail || body.title || `API tunnel: HTTP ${response.status}`,
    );
  }
  return Array.isArray(body.items) ? body.items : [];
}

async function handleDesktopMessage(sender, raw) {
  let message;
  try {
    message = JSON.parse(raw);
  } catch {
    return;
  }
  const type = message?.type;
  const origin = senderUrl(sender);

  if (type === "desktop.capabilities") {
    sender.send("desktop-message", JSON.stringify({
      type: "desktop.capabilities",
      downloadMode: "native",
      nativeDownload: true,
    }));
    return;
  }

  if (type === "tunnels.restore") {
    sender.send("desktop-message", JSON.stringify({
      type: "tunnels.auth",
      status: "none",
      url: origin,
    }));
    sender.send("desktop-message", JSON.stringify({
      type: "tunnels.state",
      items: tunnels.list(),
    }));
    return;
  }

  if (type === "tunnels.logout") {
    tokens.delete(origin);
    sender.send("desktop-message", JSON.stringify({
      type: "tunnels.auth",
      status: "logout",
      url: origin,
    }));
    return;
  }

  if (type === "tunnels.login") {
    sender.send("desktop-message", JSON.stringify({
      type: "tunnels.auth",
      status: "error",
      url: origin,
      message: "Il prototipo usa il token personale; il device flow arriva dopo.",
      requiresLogin: false,
    }));
    return;
  }

  if (type === "tunnels.login.cancel") return;

  if (type === "tunnels.list" || type === "tunnels.refresh") {
    const token = message.token || tokens.get(origin);
    if (message.token) tokens.set(origin, message.token);
    try {
      const items = await fetchTargets(origin, token);
      sender.send("desktop-message", JSON.stringify({
        type: "tunnels.targets",
        url: origin,
        items,
      }));
    } catch (error) {
      sender.send("desktop-message", JSON.stringify({
        type: "tunnels.error",
        url: origin,
        message: error.message,
      }));
    }
    return;
  }

  if (type === "tunnels.start") {
    try {
      const targetList = await fetchTargets(origin, tokens.get(origin));
      const server = targetList.find((item) => item.serverId === message.serverId);
      if (!server) throw new Error("Server non presente nell'allowlist.");
      const target = message.targetId
        ? server.targets.find((item) => item.serverApplicationId === message.targetId)
        : null;
      const service = message.serviceId
        ? server.services.find((item) => item.id === message.serviceId)
        : null;
      const started = tunnels.start({
        id: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        url: origin,
        server,
        target,
        service,
        localPort: message.localPort,
      });
      console.log("tunnel started", started.id, started.args.join(" "));
      sender.send("desktop-message", JSON.stringify({
        type: "tunnels.state",
        items: tunnels.list(),
      }));
    } catch (error) {
      sender.send("desktop-message", JSON.stringify({
        type: "tunnels.error",
        url: origin,
        message: error.message,
      }));
    }
    return;
  }

  if (type === "tunnels.stop") {
    tunnels.stop(message.id);
    sender.send("desktop-message", JSON.stringify({
      type: "tunnels.state",
      items: tunnels.list(),
    }));
    return;
  }

  if (type === "tunnels.retries.cancel") return;

  if (type === "desktop.openExternal") {
    try {
      const target = new URL(message.url);
      if (target.protocol !== "https:" && target.protocol !== "http:")
        throw new Error("Protocollo non consentito.");
      await shell.openExternal(target.toString());
    } catch (error) {
      sender.send("desktop-message", JSON.stringify({
        type: "tunnels.error",
        url: origin,
        message: `Apertura link non riuscita: ${error.message}`,
      }));
    }
  }
}

ipcMain.on("desktop-message", (event, raw) => {
  void handleDesktopMessage(event.sender, raw);
});

ipcMain.handle("manager:list", () => loadInstances());
ipcMain.handle("manager:open", (_event, instance) => {
  createInstanceWindow(instance);
});
ipcMain.handle("manager:add", (_event, value) => {
  const name = String(value?.name || "").trim();
  const url = String(value?.url || "").trim().replace(/\/$/, "");
  const parsed = new URL(url);
  if (!name || !["http:", "https:"].includes(parsed.protocol))
    throw new Error("Nome e URL http/https sono obbligatori.");
  const items = loadInstances().filter((item) => item.url !== url);
  items.push({ name, url });
  saveInstances(items);
  return items;
});
ipcMain.handle("manager:remove", (_event, url) => {
  const items = loadInstances().filter((item) => item.url !== url);
  saveInstances(items);
  return items;
});

app.whenReady().then(() => {
  instancesFile = path.join(app.getPath("userData"), "instances.json");
  createManagerWindow();
  const first = loadInstances()[0];
  if (first) {
    const window = createInstanceWindow(first);
    if (process.env.PROTO_TEST === "1") {
      runTest(first, window);
    }
  }
});

async function runTest(instance, window) {
  try {
    await new Promise((resolve) => {
      if (window.webContents.isLoading()) window.webContents.once("did-finish-load", resolve);
      else resolve();
    });
    await new Promise((resolve) => setTimeout(resolve, 2500));
    if (window.webContents.getURL().includes("/realms/")) {
      console.log("TEST_LOGIN", "submitting local dev credentials");
      await window.webContents.executeJavaScript(`(() => {
        const user = document.querySelector("#username");
        const password = document.querySelector("#password");
        if (!user || !password) return false;
        user.value = ${JSON.stringify(process.env.PROTO_USER || "admin")};
        password.value = ${JSON.stringify(process.env.PROTO_PASSWORD || "dev-admin")};
        document.querySelector("#kc-login")?.click();
        return true;
      })()`);
      await new Promise((resolve) => setTimeout(resolve, 3500));
    }
    console.log("TEST_URL", window.webContents.getURL());
    console.log("TEST_TITLE", window.getTitle());
    const text = await window.webContents.executeJavaScript("document.body.innerText");
    console.log("TEST_TEXT", text.slice(0, 240).replace(/\n/g, " | "));
    if (!text.trim()) throw new Error("La pagina principale è vuota");
    const image = await window.webContents.capturePage();
    const screenshot = path.join(__dirname, "test", "screen.png");
    fs.mkdirSync(path.dirname(screenshot), { recursive: true });
    fs.writeFileSync(screenshot, image.toPNG());
    console.log("TEST_SCREENSHOT", screenshot);

    await window.loadURL(`${instance.url.replace(/\/$/, "")}/tunnels`);
    await new Promise((resolve) => setTimeout(resolve, 2000));
    const bridgeInfo = await window.webContents.executeJavaScript("({explicit: typeof window.infraorgitDesktop, legacy: typeof window.external, legacySend: typeof window.external?.sendMessage, explicitSend: typeof window.infraorgitDesktop?.sendMessage})");
    console.log("TEST_BRIDGE", JSON.stringify(bridgeInfo));
    const tunnelText = await window.webContents.executeJavaScript("document.body.innerText");
    console.log("TEST_TUNNELS_TEXT", tunnelText.slice(0, 800).replace(/\n/g, " | "));
    console.log("TEST_HAS_DESKTOP_PANEL", tunnelText.includes("Tunnel dal desktop"));
    if (!tunnelText.includes("Tunnel")) throw new Error("Pannello tunnel non presente");
    const tunnelImage = await window.webContents.capturePage();
    fs.writeFileSync(path.join(__dirname, "test", "tunnels.png"), tunnelImage.toPNG());

    const second = createInstanceWindow({ name: "Second", url: process.env.PROTO_SECOND_URL || `${instance.url}/login-error` });
    await new Promise((resolve) => setTimeout(resolve, 1500));
    console.log("TEST_WINDOWS", BrowserWindow.getAllWindows().length);
    console.log("TEST_PARTITIONS", [...windows.keys()].join(","));
    console.log("TEST_SECOND_TEXT", (await second.webContents.executeJavaScript("document.body.innerText")).slice(0, 160).replace(/\n/g, " | "));
    console.log("TEST_RESULT PASS");
  } catch (error) {
    console.log("TEST_RESULT FAIL", error.stack || error.message);
  } finally {
    setTimeout(() => app.quit(), 500);
  }
}

app.on("window-all-closed", () => app.quit());
