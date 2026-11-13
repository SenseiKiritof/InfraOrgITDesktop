const {
  app,
  BrowserWindow,
  WebContentsView,
  dialog,
  ipcMain,
  Menu,
  nativeTheme,
  net,
  powerMonitor,
  safeStorage,
  session,
  shell,
} = require("electron");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const { DeviceAuthClient } = require("./auth/device-client");
const { RefreshTokenStore } = require("./auth/refresh-store");
const { DesktopAuth } = require("./auth/desktop-auth");
const { InstanceStore } = require("./shell/instances");
const { SettingsStore, THEMES } = require("./shell/settings");
const { TunnelManager } = require("./tunnels");
const { TunnelProjection } = require("./tunnel-projection");
const {
  DownloadTracker,
  fileNameFromUrl,
  sanitizeFileName,
  uniqueFilePath,
} = require("./downloads");

const IS_TEST = process.env.PROTO_TEST === "1";
const INSTANCE_PRELOAD = path.join(__dirname, "preload.js");
const MANAGER_PRELOAD = path.join(__dirname, "manager-preload.js");
const AUTH_RENEW_INTERVAL =
  Number(process.env.DESKTOP_AUTH_RENEW_MS) || 6 * 60 * 60 * 1000;

const windows = new Map();
const runtime = new Map();
const tokens = new Map();
const authRenewals = new Map();
const accountThemes = new Map();
const downloadTracker = new DownloadTracker();
const downloadSessions = new Set();
const MAX_BLOB_BYTES = 8 * 1024 * 1024;
const DOWNLOAD_TTL_MS = 60_000;
let downloadStateTimer = null;
let lastThemeUrl = "";
let authRenewTimer = null;
let tabOrder = [];
let activeUrl = null;
let mainWindow = null;
let attachedUrl = null;
let contentBounds = null;
let instanceStore = null;
let settingsStore = null;
let tokenStore = null;
let desktopAuth = null;
let tunnels = null;
let tunnelProjection = null;
let quitting = false;

if (process.env.DESKTOP_USER_DATA) {
  app.setPath("userData", path.resolve(process.env.DESKTOP_USER_DATA));
}

app.setName("InfraOrgIT");

if (IS_TEST && !process.env.DESKTOP_USER_DATA) {
  const testData = path.join(__dirname, "test", ".run", "user-data");
  if (process.env.PROTO_CLEAN === "1") fs.rmSync(testData, { recursive: true, force: true });
  app.setPath("userData", testData);
}

const allowLoopbackCertificates =
  process.env.DESKTOP_ALLOW_INSECURE_LOCALHOST !== "0" &&
  (!app.isPackaged || process.env.DESKTOP_ALLOW_INSECURE_LOCALHOST === "1");

function isLoopback(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function allowLocalCertificate(url) {
  try {
    const target = new URL(url);
    return isLoopback(target.hostname);
  } catch {
    return false;
  }
}

function applyLoopbackPolicy(targetSession) {
  if (!allowLoopbackCertificates) return;
  targetSession.setCertificateVerifyProc((request, callback) => {
    const hostname = request.hostname || "";
    callback(isLoopback(hostname) ? 0 : -3);
  });
}

app.on(
  "certificate-error",
  (event, _webContents, url, _error, _certificate, callback) => {
    if (allowLoopbackCertificates && allowLocalCertificate(url)) {
      event.preventDefault();
      callback(true);
      return;
    }
    callback(false);
  },
);

const partitionFor = (url) =>
  `persist:instance-${crypto
    .createHash("sha256")
    .update(url)
    .digest("hex")
    .slice(0, 16)}`;

function publishDownload(entry, immediate = false) {
  if (!entry) return;
  sendToView(entry.instanceUrl, {
    type: "desktop.download.status",
    id: entry.id,
    state: entry.state,
    fileName: entry.fileName,
    receivedBytes: entry.receivedBytes,
    totalBytes: entry.totalBytes,
  });
  if (immediate) {
    downloadTracker.prune();
    if (downloadStateTimer) clearTimeout(downloadStateTimer);
    downloadStateTimer = null;
    sendState();
    return;
  }
  if (downloadStateTimer) return;
  downloadStateTimer = setTimeout(() => {
    downloadStateTimer = null;
    downloadTracker.prune();
    sendState();
  }, 400);
  downloadStateTimer.unref?.();
}

/** Un listener per partizione: i download nativi restano visibili al manager. */
function trackDownloads(instance, targetSession) {
  const partition = partitionFor(instance.url);
  if (downloadSessions.has(partition)) return;
  downloadSessions.add(partition);
  targetSession.on("will-download", (_event, item) => {
    const fileName = sanitizeFileName(
      fileNameFromUrl(item.getURL()) || item.getFilename(),
    );
    const savePath = uniqueFilePath(app.getPath("downloads"), fileName);
    item.setSavePath(savePath);
    const entry = downloadTracker.add({
      fileName,
      totalBytes: item.getTotalBytes(),
      instanceUrl: instance.url,
    });
    publishDownload(entry);
    let progressAt = 0;
    item.on("updated", (_updatedEvent, state) => {
      const now = Date.now();
      if (now - progressAt < 400) return;
      progressAt = now;
      publishDownload(
        downloadTracker.update(entry.id, {
          state: state === "interrupted" ? "failed" : "progressing",
          receivedBytes: item.getReceivedBytes(),
          totalBytes: item.getTotalBytes() || entry.totalBytes,
          savePath: item.getSavePath() || savePath,
        }),
      );
    });
    item.once("done", (_doneEvent, state) => {
      publishDownload(
        downloadTracker.finish(
          entry.id,
          state === "completed"
            ? "completed"
            : state === "cancelled"
              ? "cancelled"
              : "failed",
          {
            receivedBytes: item.getReceivedBytes(),
            totalBytes: item.getTotalBytes() || entry.totalBytes,
            savePath: item.getSavePath() || savePath,
          },
        ),
        true,
      );
      setTimeout(() => {
        downloadTracker.remove(entry.id);
        sendState();
      }, DOWNLOAD_TTL_MS).unref?.();
    });
  });
}

async function saveBlobDownload(record, message) {
  const fileName = sanitizeFileName(message.filename);
  const base64 = typeof message.base64 === "string" ? message.base64 : "";
  const bytes = Buffer.from(base64.replace(/\s/g, ""), "base64");
  if (bytes.length === 0 || bytes.length > MAX_BLOB_BYTES) return;
  const result = await dialog.showSaveDialog(mainWindow, {
    defaultPath: path.join(app.getPath("downloads"), fileName),
  });
  if (result.canceled || !result.filePath) return;
  await fs.promises.writeFile(result.filePath, bytes);
  const entry = downloadTracker.add({
    fileName,
    totalBytes: bytes.length,
    instanceUrl: record.url,
  });
  publishDownload(
    downloadTracker.finish(entry.id, "completed", {
      receivedBytes: bytes.length,
      savePath: result.filePath,
    }),
    true,
  );
}

function sessionFor(instance) {
  const partition = partitionFor(instance.url);
  const target = session.fromPartition(partition);
  applyLoopbackPolicy(target);
  trackDownloads(instance, target);
  return { partition, session: target };
}

function sessionFetch(targetSession) {
  return (input, init) => targetSession.fetch(input, init);
}

function recordFor(url) {
  return windows.get(url) || null;
}

function sameOrigin(left, right) {
  try {
    return new URL(left).origin === new URL(right).origin;
  } catch {
    return false;
  }
}

function runtimeFor(url) {
  if (!runtime.has(url)) {
    runtime.set(url, {
      serverCount: null,
      sessionTunnelCount: null,
      targets: null,
      targetsAt: 0,
      targetsError: null,
      loading: false,
      prepared: false,
      accountLoaded: false,
      lastAuthSent: "",
      lastCountAt: 0,
    });
  }
  return runtime.get(url);
}

function activeThemePreference() {
  const url = activeUrl || lastThemeUrl;
  if (url && accountThemes.has(url)) return accountThemes.get(url);
  return settingsStore?.theme || "system";
}

function themeState() {
  const preference = activeThemePreference();
  const resolved =
    preference === "system"
      ? nativeTheme.shouldUseDarkColors
        ? "dark"
        : "light"
      : preference;
  return { preference, resolved };
}

function authSnapshot(url) {
  return desktopAuth?.snapshot(url) || {
    status: "unknown",
    requiresLogin: true,
    tunnelAccess: false,
    saved: false,
  };
}

function localTunnelCount(url) {
  return tunnels ? tunnels.active(url).length : 0;
}

function publicTargets(url) {
  const data = runtimeFor(url);
  return data.targets;
}

function publicState() {
  downloadTracker.prune();
  const instances = instanceStore.list().map((instance) => {
    const data = runtimeFor(instance.url);
    const localCount = localTunnelCount(instance.url);
    return {
      ...instance,
      active: instance.url === activeUrl,
      hasTab: windows.has(instance.url),
      loading: data.loading,
      serverCount: data.serverCount,
      localTunnelCount: localCount,
      sessionTunnelCount: data.sessionTunnelCount,
      tunnelCount: localCount + (data.sessionTunnelCount || 0),
      targetsError: data.targetsError,
      auth: authSnapshot(instance.url),
    };
  });
  const targets = {};
  for (const [url, data] of runtime.entries()) {
    if (data.targets) targets[url] = data.targets;
  }
  return {
    instances,
    tabs: tabOrder
      .filter((url) => windows.has(url))
      .map((url) => {
        const instance = instanceStore.get(url);
        const data = runtimeFor(url);
        return {
          url,
          name: instance?.name || url,
          environment: instance?.environment || "",
          active: url === activeUrl,
          loading: data.loading,
          auth: authSnapshot(url),
        };
      }),
    activeUrl,
    theme: themeState(),
    platform: process.platform,
    tunnels: tunnels ? tunnels.list() : [],
    downloads: downloadTracker.list(),
    targets,
    version: app.getVersion(),
  };
}

function sendState() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.webContents.send("shell:state", publicState());
}

function sendCommand(command) {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.webContents.send("shell:command", command);
}

function sendToView(url, message) {
  const record = recordFor(url);
  if (!record || record.view.webContents.isDestroyed()) return;
  record.view.webContents.send("desktop-message", JSON.stringify(message));
}

function activeTunnels() {
  return tunnels.list().filter((tunnel) => tunnel.status === "Active");
}

function broadcastTunnels() {
  const items = activeTunnels();
  for (const url of windows.keys()) {
    sendToView(url, { type: "tunnels.state", items });
  }
  sendState();
}

async function postTunnelProjection(row, kind, payload) {
  if (IS_TEST || !row?.url) return false;
  const instance = instanceStore.get(row.url);
  if (!instance) return false;
  const record = recordFor(row.url);
  const ses = record?.session || sessionFor(instance).session;
  const fetcher = sessionFetch(ses);
  let csrf = desktopAuth?.csrfToken(row.url) || "";
  if (!csrf && desktopAuth) {
    await desktopAuth
      .readAccount(row.url, fetcher, { emit: false })
      .catch(() => null);
    csrf = desktopAuth.csrfToken(row.url) || "";
  }
  if (!csrf) return false;

  const path =
    kind === "event" ? "/api/tunnels/events" : "/api/tunnels/sessions";
  try {
    const response = await fetcher(
      `${instance.url.replace(/\/$/, "")}${path}`,
      {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-Token": csrf,
          Origin: new URL(instance.url).origin,
        },
        body: JSON.stringify(payload),
        credentials: "include",
      },
    );
    if (!response.ok)
      console.warn("tunnel projection rejected", response.status, path);
    return response.ok;
  } catch (error) {
    console.warn("tunnel projection failed", error.message);
    return false;
  }
}

function webAuthStatus(url) {
  const snapshot = authSnapshot(url);
  if (snapshot.status === "pending") return "pending";
  if (snapshot.status === "restoring") return "restoring";
  if (snapshot.status === "error") return "error";
  if (snapshot.tunnelAccess) return "restored";
  return "none";
}

function sendWebAuth(url, { forcedStatus = null, message = null } = {}) {
  const snapshot = authSnapshot(url);
  const status = forcedStatus || webAuthStatus(url);
  sendToView(url, {
    type: "tunnels.auth",
    url,
    status,
    requiresLogin: snapshot.requiresLogin,
    saved: snapshot.saved,
    userCode: snapshot.userCode,
    verificationUri: snapshot.verificationUri,
    verificationUriComplete: snapshot.verificationUriComplete,
    expiresIn: snapshot.expiresIn,
    message: message || snapshot.message || null,
  });
}

function sendWebTargets(url) {
  const targetList = publicTargets(url);
  if (!targetList) return false;
  sendToView(url, { type: "tunnels.targets", url, items: targetList });
  return true;
}

async function loadTargets(instance, { force = false } = {}) {
  const data = runtimeFor(instance.url);
  if (!authSnapshot(instance.url).tunnelAccess) return null;
  if (!force && data.targets && Date.now() - data.targetsAt < 60_000)
    return data.targets;
  const record = recordFor(instance.url);
  const ses = record?.session || sessionFor(instance).session;
  try {
    const items = await desktopAuth.fetchTargets(
      instance.url,
      sessionFetch(ses),
    );
    data.targets = items;
    data.targetsAt = Date.now();
    data.targetsError = null;
    sendWebTargets(instance.url);
    sendState();
    return items;
  } catch (error) {
    data.targetsError = error.message;
    sendToView(instance.url, {
      type: "tunnels.error",
      url: instance.url,
      message: error.message,
    });
    sendState();
    return null;
  }
}

async function primeInstanceCounts(instance) {
  const data = runtimeFor(instance.url);
  if (data.serverCount !== null) return;
  if (Date.now() - data.lastCountAt < 5000) return;
  data.lastCountAt = Date.now();
  const ses = recordFor(instance.url)?.session || sessionFor(instance).session;
  const counts = await desktopAuth.fetchCounts(
    instance.url,
    sessionFetch(ses),
  );
  data.serverCount = counts.serverCount;
  data.sessionTunnelCount = counts.sessionTunnelCount;
  sendState();
}

function primeLandingCounts() {
  for (const instance of instanceStore.list()) {
    if (!recordFor(instance.url)) void primeInstanceCounts(instance);
  }
}

async function refreshInstanceData(instance) {
  const data = runtimeFor(instance.url);
  const record = recordFor(instance.url);
  if (!record) return;
  if (Date.now() - data.lastCountAt < 1500) return;
  data.lastCountAt = Date.now();
  const fetcher = sessionFetch(record.session);

  if (!data.accountLoaded) {
    const account = await desktopAuth.readAccount(
      instance.url,
      fetcher,
      { emit: false },
    );
    if (account) {
      data.accountLoaded = true;
      let preference = account.theme;
      if (!THEMES.includes(preference)) {
        try {
          const stored = await record.view.webContents.executeJavaScript(
            'localStorage.getItem("infraorgit-theme")',
          );
          if (THEMES.includes(stored)) preference = stored;
        } catch {
          // The SPA may still be on a login or error page.
        }
      }
      if (THEMES.includes(preference)) {
        accountThemes.set(instance.url, preference);
        settingsStore.setTheme(preference);
        if (!activeUrl || activeUrl === instance.url)
          lastThemeUrl = instance.url;
        applyThemeToManager();
      }
      desktopAuth.emit(instance.url, {});
    }
  }

  const counts = await desktopAuth.fetchCounts(instance.url, fetcher);
  data.serverCount = counts.serverCount;
  data.sessionTunnelCount = counts.sessionTunnelCount;
  sendState();

  if (authSnapshot(instance.url).tunnelAccess) void loadTargets(instance);
}

async function renewInstanceAuth(instance) {
  if (authRenewals.has(instance.url)) return authRenewals.get(instance.url);
  const record = recordFor(instance.url);
  if (
    !record ||
    !record.prepared ||
    record.loading ||
    !tokenStore?.get(instance.url)
  )
    return null;

  const task = desktopAuth
    .renew(instance, sessionFetch(record.session), record.session.cookies)
    .then(async (result) => {
      if (result.mode === "renewed")
        console.log("desktop auth renewed", instance.url);
      else if (result.mode === "revoked") {
        console.log("desktop auth revoked", instance.url);
        await clearInstanceCookies(instance);
        record.prepared = false;
        if (!record.view.webContents.isDestroyed()) {
          record.view.webContents.loadURL(instance.url).catch((error) => {
            record.lastError = error.message;
            sendState();
          });
        }
      }
      return result;
    })
    .catch((error) => {
      console.warn("desktop auth renewal failed", instance.url, error.message);
      return { mode: "error" };
    })
    .finally(() => authRenewals.delete(instance.url));

  authRenewals.set(instance.url, task);
  return task;
}

async function renewOpenInstanceAuth() {
  const instances = instanceStore
    .list()
    .filter((instance) => recordFor(instance.url));
  await Promise.allSettled(instances.map(renewInstanceAuth));
}

function applyThemeToManager() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  sendState();
}

function applyViewLayout() {
  if (!mainWindow || mainWindow.isDestroyed() || !activeUrl) {
    hideAttachedView();
    return;
  }
  const record = recordFor(activeUrl);
  if (!record || !contentBounds?.visible) {
    hideAttachedView();
    return;
  }
  const bounds = {
    x: Math.max(0, Math.round(contentBounds.x)),
    y: Math.max(0, Math.round(contentBounds.y)),
    width: Math.max(0, Math.round(contentBounds.width)),
    height: Math.max(0, Math.round(contentBounds.height)),
  };
  if (bounds.width < 2 || bounds.height < 2) {
    hideAttachedView();
    return;
  }
  record.view.setBounds(bounds);
  if (attachedUrl !== activeUrl) {
    hideAttachedView();
    mainWindow.contentView.addChildView(record.view);
    attachedUrl = activeUrl;
  }
}

function hideAttachedView() {
  if (!mainWindow || mainWindow.isDestroyed()) {
    attachedUrl = null;
    return;
  }
  if (attachedUrl) {
    const record = recordFor(attachedUrl);
    if (record) {
      try {
        mainWindow.contentView.removeChildView(record.view);
      } catch {
        // already detached
      }
    }
    attachedUrl = null;
  }
}

function createInstanceView(instance) {
  const existing = recordFor(instance.url);
  if (existing) return existing;

  const { partition, session: targetSession } = sessionFor(instance);
  const view = new WebContentsView({
    webPreferences: {
      partition,
      preload: INSTANCE_PRELOAD,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
      additionalArguments: [`--infraorgit-instance=${new URL(instance.url).origin}`],
    },
  });
  view.setBackgroundColor("#0b0e13");

  const record = {
    url: instance.url,
    partition,
    session: targetSession,
    view,
    prepared: false,
    loaded: false,
    loading: false,
    lastError: null,
  };
  windows.set(instance.url, record);
  tabOrder.push(instance.url);
  runtimeFor(instance.url);

  view.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith("https://") || url.startsWith("http://"))
      void shell.openExternal(url);
    return { action: "deny" };
  });
  view.webContents.on("will-navigate", (event, url) => {
    if (!url.startsWith("https://") && !url.startsWith("http://")) {
      event.preventDefault();
      return;
    }
    if (url.startsWith("http://") && !allowLocalCertificate(url)) {
      event.preventDefault();
      void shell.openExternal(url);
    }
  });
  view.webContents.on("did-fail-load", (_event, code, description, url) => {
    if (code === -3) return;
    record.lastError = `${description} (${code})`;
    console.error("window load failed", instance.name, code, description, url);
    sendState();
  });
  view.webContents.on("did-finish-load", () => {
    record.loaded = true;
    record.loading = false;
    record.lastError = null;
    const current = view.webContents.getURL();
    if (sameOrigin(current, instance.url)) {
      void refreshInstanceData(instanceStore.get(instance.url) || instance);
    }
    sendState();
  });
  view.webContents.on("page-title-updated", (_event, title) => {
    if (mainWindow && !mainWindow.isDestroyed() && instance.url === activeUrl)
      mainWindow.setTitle(title ? `${title} — InfraOrgIT` : "InfraOrgIT");
  });
  view.webContents.on("before-input-event", (event, input) => {
    if (input.type !== "keyDown") return;
    const modifier = process.platform === "darwin" ? input.meta : input.control;
    if (!modifier) return;
    const key = String(input.key || "").toLowerCase();
    if (key === "k") {
      event.preventDefault();
      sendCommand("palette");
    } else if (key === "0") {
      event.preventDefault();
      showLanding();
    } else if (key === "t" && input.shift) {
      event.preventDefault();
      sendCommand("toggle-drawer");
    }
  });
  return record;
}

async function clearInstanceCookies(instance) {
  const record = recordFor(instance.url);
  if (!record) return;
  await record.session
    .clearStorageData({ storages: ["cookies"] })
    .catch(() => {});
}

function prepareInstance(instance) {
  const record = recordFor(instance.url);
  if (!record || record.prepared) return;
  record.prepared = true;
  const data = runtimeFor(instance.url);
  data.loading = true;
  record.loading = true;
  sendState();

  void (async () => {
    let mode = "none";
    try {
      mode = await Promise.race([
        desktopAuth.restore(instance, sessionFetch(record.session), record.session.cookies),
        new Promise((resolve) =>
          setTimeout(() => resolve({ mode: "timeout" }), 9000),
        ),
      ]);
    } catch {
      mode = { mode: "error" };
    }
    data.loading = false;
    record.loading = false;
    sendState();
    if (record.view.webContents.isDestroyed()) return;
    // A revoked refresh token must not leave the old BFF cookie usable:
    // clear the partition cookies so the SPA goes through Keycloak again.
    if (mode.mode === "expired" || mode.mode === "revoked")
      await clearInstanceCookies(instance);
    const target = record.pendingUrl || instance.url;
    record.pendingUrl = null;
    record.view.webContents.loadURL(target).catch((error) => {
      record.lastError = error.message;
      sendState();
    });
    if (mode.mode === "silent") {
      // The BFF cookie is in the partition; the SPA loads without Keycloak.
      console.log("desktop auth silent", instance.url);
    } else if (mode.mode === "fallback" || mode.mode === "error") {
      console.log("desktop auth fallback", instance.url);
    }
  })();
}

function openTab(url, { activate = false } = {}) {
  const instance = instanceStore.get(url);
  if (!instance) return null;
  const record = createInstanceView(instance);
  if (activate) {
    if (activeUrl && activeUrl !== url) hideAttachedView();
    activeUrl = url;
    instanceStore.touch(url);
    prepareInstance(instance);
    applyViewLayout();
  }
  sendState();
  return record;
}

function showLanding() {
  hideAttachedView();
  activeUrl = null;
  if (mainWindow && !mainWindow.isDestroyed())
    mainWindow.setTitle("InfraOrgIT");
  primeLandingCounts();
  sendState();
}

function activateTab(url) {
  const instance = instanceStore.get(url);
  if (!instance) throw new Error("Istanza non trovata.");
  openTab(url, { activate: true });
}

function closeTab(url) {
  const record = recordFor(url);
  if (!record) return;
  if (activeUrl === url) showLanding();
  try {
    record.view.webContents.close();
  } catch {
    // already gone
  }
  windows.delete(url);
  tabOrder = tabOrder.filter((item) => item !== url);
  if (attachedUrl === url) attachedUrl = null;
  sendState();
}

function removeInstance(url) {
  const record = recordFor(url);
  const partitionSession =
    record?.session || sessionFor({ url }).session;
  if (record) closeTab(url);
  desktopAuth.logout(url).catch(() => {});
  partitionSession?.clearStorageData().catch(() => {});
  runtime.delete(url);
  tokens.delete(url);
  instanceStore.remove(url);
  sendState();
}

function safeExternal(url) {
  const target = new URL(String(url));
  if (target.protocol !== "https:" && target.protocol !== "http:")
    throw new Error("Protocollo non consentito.");
  return shell.openExternal(target.toString());
}

async function fetchTargetsWithToken(instanceUrl, token) {
  const record = recordFor(instanceUrl);
  const ses = record?.session || sessionFor({ url: instanceUrl }).session;
  const base = instanceUrl.replace(/\/$/, "");
  const response = await sessionFetch(ses)(`${base}/api/tunnels/targets`, {
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${token}`,
    },
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(
      body.detail || body.title || `API tunnel: HTTP ${response.status}`,
    );
  }
  return Array.isArray(body.items) ? body.items : [];
}

function tunnelSelection(targetList, serverId, targetId, serviceId) {
  const server = targetList.find((item) => item.serverId === serverId);
  if (!server) throw new Error("Server non presente nell'allowlist.");
  const target = targetId
    ? server.targets?.find((item) => item.serverApplicationId === targetId)
    : null;
  const service = serviceId
    ? server.services?.find((item) => item.id === serviceId)
    : null;
  if (targetId && !target) throw new Error("Applicazione non presente nell'allowlist.");
  if (serviceId && !service)
    throw new Error("Servizio tunnel non presente nell'allowlist.");
  if (!target && !service) throw new Error("Scegli un'applicazione o un servizio.");
  return { server, target, service };
}

async function startTunnelForInstance(instance, input) {
  const targetList = await loadTargets(instance, { force: true });
  if (!targetList)
    throw new Error("Accedi all'istanza per caricare i server dei tunnel.");
  const selection = tunnelSelection(
    targetList,
    Number(input.serverId),
    input.targetId ? Number(input.targetId) : null,
    input.serviceId ? Number(input.serviceId) : null,
  );
  const id = input.id || `${Date.now()}-${crypto.randomBytes(6).toString("hex")}`;
  return tunnels.start({
    id,
    url: instance.url,
    instanceName: instance.name,
    server: selection.server,
    target: selection.target,
    service: selection.service,
    localPort: input.localPort,
  });
}

function registerIpc() {
  const assertManager = (event) => {
    if (!mainWindow || event.sender !== mainWindow.webContents)
      throw new Error("Richiesta non autorizzata.");
  };
  const handle = (channel, listener) => {
    ipcMain.handle(channel, async (event, ...args) => {
      try {
        assertManager(event);
        return {
          __managerIpc: true,
          ok: true,
          value: await listener(...args),
        };
      } catch (error) {
        return {
          __managerIpc: true,
          ok: false,
          error: error?.message || "Operazione non riuscita.",
        };
      }
    });
  };

  ipcMain.on("manager:content-bounds", (event, bounds) => {
    if (!mainWindow || event.sender !== mainWindow.webContents) return;
    if (!bounds || typeof bounds !== "object") return;
    const size = mainWindow.getContentBounds();
    const finite = (value) => Number.isFinite(value);
    if (
      !finite(bounds.x) ||
      !finite(bounds.y) ||
      !finite(bounds.width) ||
      !finite(bounds.height)
    )
      return;
    contentBounds = {
      x: Math.min(Math.max(0, bounds.x), size.width),
      y: Math.min(Math.max(0, bounds.y), size.height),
      width: Math.min(Math.max(0, bounds.width), size.width),
      height: Math.min(Math.max(0, bounds.height), size.height),
      visible: bounds.visible === true,
    };
    applyViewLayout();
  });

  handle("manager:state", () => publicState());
  handle("manager:list", () => instanceStore.list());
  handle("manager:add", (value) => {
    instanceStore.add(value);
    sendState();
    return publicState();
  });
  handle("manager:update", (url, value) => {
    instanceStore.update(url, value || {});
    sendState();
    return publicState();
  });
  handle("manager:remove", (url) => {
    removeInstance(url);
    return publicState();
  });
  handle("manager:open", (url) => {
    activateTab(url);
    return publicState();
  });
  handle("manager:activate", (url) => {
    activateTab(url);
    return publicState();
  });
  handle("manager:home", () => {
    showLanding();
    return publicState();
  });
  handle("manager:close-tab", (url) => {
    closeTab(url);
    return publicState();
  });
  handle("manager:reload", (url) => {
    const target = url || activeUrl;
    if (!target) return publicState();
    const record = recordFor(target);
    if (record) record.view.webContents.reload();
    return publicState();
  });
  handle("manager:refresh", async (url) => {
    const target = url || activeUrl;
    if (!target) return publicState();
    const instance = instanceStore.get(target);
    if (instance && recordFor(target)) await refreshInstanceData(instance);
    return publicState();
  });
  handle("manager:open-external", (url) => safeExternal(url));

  handle("manager:auth-start", (url) => {
    const instance = instanceStore.get(url);
    if (!instance) throw new Error("Istanza non trovata.");
    const record = recordFor(url);
    const ses = record?.session || sessionFor(instance).session;
    openTab(url, { activate: true });
    void desktopAuth
      .login(instance, sessionFetch(ses), ses.cookies)
      .then((snapshot) => {
        if (snapshot.status === "ready") {
          const current = recordFor(url);
          if (current?.prepared) current.view.webContents.reload();
          void loadTargets(instance, { force: true });
        }
      });
    return authSnapshot(url);
  });
  handle("manager:auth-cancel", (url) => desktopAuth.cancel(url));
  handle("manager:auth-logout", async (url) => {
    await desktopAuth.logout(url);
    const snapshot = authSnapshot(url);
    const data = runtimeFor(url);
    if (!snapshot.tunnelAccess) {
      data.targets = null;
      data.targetsAt = 0;
    }
    sendWebAuth(url);
    sendToView(url, { type: "tunnels.state", items: activeTunnels() });
    sendState();
    return snapshot;
  });
  handle("manager:auth-open-verification", async (url) => {
    const snapshot = authSnapshot(url);
    const target =
      snapshot.verificationUriComplete || snapshot.verificationUri;
    if (!target) throw new Error("Nessuna verifica di accesso in corso.");
    await safeExternal(target);
  });

  handle("manager:start-tunnel", async (input) => {
    const instance = instanceStore.get(input?.url);
    if (!instance) throw new Error("Istanza non trovata.");
    const started = await startTunnelForInstance(instance, input || {});
    sendWebAuth(instance.url);
    sendWebTargets(instance.url);
    return started;
  });
  handle("manager:stop-tunnel", (id, url) => {
    const stopped = tunnels.stop(
      String(id),
      url ? String(url) : null,
    );
    if (!stopped) throw new Error("Tunnel non trovato o già fermo.");
    return publicState();
  });
  handle("manager:restart-tunnel", async (id, url) => {
    tunnels.restart(String(id), url ? String(url) : null);
    return publicState();
  });
  handle("manager:forget-tunnel", (id) => {
    if (!tunnels.forget(String(id)))
      throw new Error("Ferma il tunnel prima di rimuoverlo.");
    return publicState();
  });

  handle("manager:open-tunnels-page", async (url) => {
    const instance = instanceStore.get(url);
    if (!instance) throw new Error("Istanza non trovata.");
    openTab(url, { activate: true });
    const record = recordFor(url);
    const target = `${instance.url}/tunnels`;
    if (!record.prepared) {
      record.pendingUrl = target;
      prepareInstance(instance);
      return publicState();
    }
    await record.view.webContents.loadURL(target);
    return publicState();
  });
  handle("manager:open-server-page", async (url, serverId) => {
    const instance = instanceStore.get(url);
    if (!instance) throw new Error("Istanza non trovata.");
    const id = Number(serverId);
    if (!Number.isInteger(id) || id <= 0) throw new Error("Server non valido.");
    openTab(url, { activate: true });
    const record = recordFor(url);
    const target = `${instance.url}/servers/${id}`;
    if (!record.prepared) {
      record.pendingUrl = target;
      prepareInstance(instance);
      return publicState();
    }
    await record.view.webContents.loadURL(target);
    void focusTunnelServices(record, id);
    return publicState();
  });
  handle("manager:server-list", async (url) => {
    const instance = instanceStore.get(url);
    if (!instance) throw new Error("Istanza non trovata.");
    const record = recordFor(url);
    const ses = record?.session || sessionFor(instance).session;
    const response = await sessionFetch(ses)(
      `${instance.url}/api/servers?pageSize=200&sort=name`,
      { headers: { Accept: "application/json" }, credentials: "include" },
    ).catch(() => null);
    if (!response || response.status === 401 || response.status === 403)
      return { authenticated: false, items: [], message: "Accedi all'istanza per vedere i server." };
    if (!response.ok)
      return { authenticated: false, items: [], message: `API server: HTTP ${response.status}` };
    const body = await response.json().catch(() => ({}));
    return {
      authenticated: true,
      items: Array.isArray(body.items)
        ? body.items.map((server) => ({
            id: server.id,
            name: server.name,
            environmentLabel: server.providerEnvironmentLabel,
            ipAddress: server.ipAddress,
            hostname: server.hostname,
            sshPort: server.sshPort,
          }))
        : [],
      message: null,
    };
  });
}

async function focusTunnelServices(record, serverId) {
  try {
    const found = await record.view.webContents.executeJavaScript(`(() => {
      const headings = [...document.querySelectorAll("h2, h3")];
      const target = headings.find((heading) =>
        ["Tunnel", "Tunnels"].includes(heading.textContent.trim())
      );
      if (!target) return false;
      target.scrollIntoView({ block: "center" });
      return true;
    })()`);
    if (found) console.log("focused tunnel services", serverId);
  } catch {
    // The page may still be rendering; linking to the server page is enough.
  }
}

async function handleDesktopMessage(sender, raw) {
  let message;
  try {
    message = JSON.parse(raw);
  } catch {
    return;
  }
  if (!message || typeof message.type !== "string") return;

  const record = [...windows.values()].find(
    (item) => item.view.webContents.id === sender.id,
  );
  if (!record) return;
  let origin;
  try {
    origin = new URL(sender.getURL()).origin;
  } catch {
    return;
  }
  if (origin !== new URL(record.url).origin) return;
  const instance = instanceStore.get(record.url);
  if (!instance) return;

  switch (message.type) {
    case "theme.changed": {
      const theme = typeof message.theme === "string" ? message.theme : "";
      if (!THEMES.includes(theme)) return;
      accountThemes.set(instance.url, theme);
      settingsStore.setTheme(theme);
      if (!activeUrl || activeUrl === instance.url) lastThemeUrl = instance.url;
      applyThemeToManager();
      return;
    }

    case "desktop.capabilities":
      sender.send(
        "desktop-message",
        JSON.stringify({
          type: "desktop.capabilities",
          downloadMode: "native",
          nativeDownload: true,
        }),
      );
      return;

    case "tunnels.restore": {
      const snapshot = authSnapshot(instance.url);
      if (snapshot.status === "pending") {
        sendWebAuth(instance.url);
      } else if (snapshot.tunnelAccess) {
        sendWebAuth(instance.url, {
          forcedStatus: snapshot.status === "ready" ? "restored" : webAuthStatus(instance.url),
        });
        void loadTargets(instance).then(() => sendWebTargets(instance.url));
      } else {
        sendWebAuth(instance.url, { forcedStatus: "none" });
      }
      sendToView(instance.url, { type: "tunnels.state", items: activeTunnels() });
      return;
    }

    case "tunnels.list":
    case "tunnels.refresh": {
      const personalToken =
        typeof message.token === "string" ? message.token.trim() : "";
      try {
        let items;
        if (personalToken) {
          tokens.set(instance.url, personalToken);
          items = await fetchTargetsWithToken(instance.url, personalToken);
        } else {
          const existing = tokens.get(instance.url);
          items = existing
            ? await fetchTargetsWithToken(instance.url, existing)
            : await desktopAuth.fetchTargets(
                instance.url,
                sessionFetch(record.session),
              );
        }
        runtimeFor(instance.url).targets = items;
        runtimeFor(instance.url).targetsAt = Date.now();
        runtimeFor(instance.url).targetsError = null;
        sendWebTargets(instance.url);
        sendWebAuth(instance.url, { forcedStatus: "success" });
        sendToView(instance.url, { type: "tunnels.state", items: activeTunnels() });
        sendState();
      } catch (error) {
        sendToView(instance.url, {
          type: "tunnels.error",
          url: instance.url,
          message: error.message,
        });
      }
      return;
    }

    case "tunnels.login": {
      void desktopAuth.login(
        instance,
        sessionFetch(record.session),
        record.session.cookies,
      );
      return;
    }

    case "tunnels.login.cancel":
      desktopAuth.cancel(instance.url);
      return;

    case "tunnels.logout":
      void desktopAuth.logout(instance.url).then(() => {
        const data = runtimeFor(instance.url);
        data.targets = null;
        data.targetsAt = 0;
        tokens.delete(instance.url);
        sendWebAuth(instance.url, {
          forcedStatus: "logout",
          message: "Sessione tunnel dimenticata.",
        });
        sendToView(instance.url, {
          type: "tunnels.state",
          items: activeTunnels(),
        });
        sendState();
      });
      return;

    case "tunnels.start": {
      try {
        const personalToken = tokens.get(instance.url);
        const targetList = personalToken
          ? await fetchTargetsWithToken(instance.url, personalToken)
          : await loadTargets(instance, { force: true });
        if (!targetList) throw new Error("Accedi per usare i tunnel.");
        const selection = tunnelSelection(
          targetList,
          Number(message.serverId),
          message.targetId ? Number(message.targetId) : null,
          message.serviceId ? Number(message.serviceId) : null,
        );
        tunnels.start({
          id: `${Date.now()}-${crypto.randomBytes(6).toString("hex")}`,
          url: instance.url,
          instanceName: instance.name,
          server: selection.server,
          target: selection.target,
          service: selection.service,
          localPort: message.localPort,
        });
      } catch (error) {
        sendToView(instance.url, {
          type: "tunnels.error",
          url: instance.url,
          message: error.message,
        });
      }
      return;
    }

    case "tunnels.stop":
      tunnels.stop(String(message.id), instance.url);
      sendToView(instance.url, { type: "tunnels.state", items: activeTunnels() });
      return;

    case "tunnels.retries.cancel":
      return;

    case "desktop.download": {
      const url = typeof message.url === "string" ? message.url : "";
      if (!/^https?:\/\//i.test(url)) return;
      record.view.webContents.downloadURL(url);
      return;
    }

    case "desktop.download.blob":
      await saveBlobDownload(record, message);
      return;

    case "desktop.openExternal":
      try {
        await safeExternal(message.url);
      } catch (error) {
        sendToView(instance.url, {
          type: "tunnels.error",
          url: instance.url,
          message: `Apertura link non riuscita: ${error.message}`,
        });
      }
      return;

    default:
      return;
  }
}

function createMainWindow() {
  mainWindow = new BrowserWindow({
    width: 1380,
    height: 900,
    minWidth: 1024,
    minHeight: 680,
    show: false,
    backgroundColor: "#0b0e13",
    icon: path.join(__dirname, "assets", "icon.png"),
    title: "InfraOrgIT",
    titleBarStyle: process.platform === "darwin" ? "hiddenInset" : "default",
    trafficLightPosition:
      process.platform === "darwin" ? { x: 18, y: 18 } : undefined,
    webPreferences: {
      preload: MANAGER_PRELOAD,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
    },
  });
  mainWindow.loadFile("manager.html");
  mainWindow.once("ready-to-show", () => mainWindow.show());
  mainWindow.on("resize", () => {
    if (contentBounds) applyViewLayout();
  });
  mainWindow.on("closed", () => {
    attachedUrl = null;
    mainWindow = null;
    for (const record of windows.values()) {
      try {
        record.view.webContents.close();
      } catch {
        // already gone
      }
    }
    windows.clear();
    tabOrder = [];
    activeUrl = null;
    tunnels?.stopAll();
  });
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith("https://") || url.startsWith("http://"))
      void shell.openExternal(url);
    return { action: "deny" };
  });
}

function openStartupTabs() {
  const opens = instanceStore
    .list()
    .filter((instance) => instance.openAtStartup);
  for (const instance of opens) openTab(instance.url, { activate: false });
  if (opens.length > 0) activateTab(opens[0].url);
  else showLanding();
}

function buildMenu() {
  const isMac = process.platform === "darwin";
  const template = [
    ...(isMac
      ? [
          {
            label: app.name,
            submenu: [
              { role: "about" },
              { type: "separator" },
              { role: "hide" },
              { role: "hideOthers" },
              { role: "unhide" },
              { type: "separator" },
              { role: "quit" },
            ],
          },
        ]
      : []),
    {
      label: "File",
      submenu: [
        {
          label: "Nuova istanza",
          accelerator: "CmdOrCtrl+N",
          click: () => sendCommand("new-instance"),
        },
        { type: "separator" },
        { role: "close" },
      ],
    },
    { role: "editMenu" },
    {
      label: "Vista",
      submenu: [
        {
          label: "Command palette",
          accelerator: "CmdOrCtrl+K",
          click: () => sendCommand("palette"),
        },
        {
          label: "Istanze",
          accelerator: "CmdOrCtrl+0",
          click: () => showLanding(),
        },
        {
          label: "Tunnel",
          accelerator: "CmdOrCtrl+Shift+T",
          click: () => sendCommand("toggle-drawer"),
        },
        { type: "separator" },
        { role: "reload" },
        { role: "toggleDevTools" },
        { role: "togglefullscreen" },
      ],
    },
    { role: "windowMenu" },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
  if (!isMac && mainWindow) mainWindow.setMenuBarVisibility(false);
}

function seedTestInstances() {
  if (!IS_TEST || instanceStore.list().length > 0) return;
  const base = process.env.PROTO_URL || "https://localhost:8443";
  instanceStore.add({
    name: "Produzione",
    url: base,
    environment: "Produzione",
    openAtStartup: true,
  });
  instanceStore.add({
    name: "Staging",
    url: process.env.PROTO_SECOND_URL || "https://staging.example.com",
    environment: "Staging",
    openAtStartup: false,
  });
  instanceStore.add({
    name: "Lab",
    url: "https://lab.example.com",
    environment: "Test",
    openAtStartup: false,
  });
}

function startTestTunnels() {
  if (!IS_TEST) return;
  process.env.SSH_BIN = path.join(__dirname, "test", "fake-ssh.js");
  process.env.FAKE_SSH_LOG = path.join(__dirname, "test", ".run", "ssh.log");
  fs.mkdirSync(path.dirname(process.env.FAKE_SSH_LOG), { recursive: true });
  fs.writeFileSync(process.env.FAKE_SSH_LOG, "");
  const instance = instanceStore.list()[0];
  if (!instance) return;

  // The shell test exercises the tunnel creation form without a real
  // Keycloak device flow: seed an access token and the allowlist payload.
  desktopAuth.setTokens(
    instance.url,
    {
      accessToken: "test-access-token",
      refreshToken: "test-refresh-token",
      expiresAt: Date.now() + 3_600_000,
    },
    "session",
  );
  desktopAuth.fetchTargets = async (url) =>
    Array.isArray(runtimeFor(url).targets) ? runtimeFor(url).targets : [];
  desktopAuth.restore = async () => ({ mode: "silent" });
  runtimeFor(instance.url).targets = [
    {
      serverId: 1,
      serverName: "demo-app-01",
      environmentLabel: "Produzione",
      sshHost: "10.0.0.10",
      sshPort: 22,
      username: "demo",
      jumps: [],
      targets: [
        {
          serverApplicationId: 7,
          application: "orders-api",
          version: "1.4.2",
          targetHost: "127.0.0.1",
          targetPort: 8080,
          suggestedLocalPort: 18080,
        },
      ],
      services: [
        {
          id: 9,
          name: "postgres",
          mode: "Local",
          kind: "Tcp",
          enabled: true,
          autoStart: false,
          targetHost: "10.0.0.10",
          targetPort: 5432,
          localPort: 15433,
          remotePort: null,
          openPath: null,
        },
      ],
    },
    {
      serverId: 2,
      serverName: "demo-db-01",
      environmentLabel: "Staging",
      sshHost: "10.0.0.20",
      sshPort: 22,
      username: "demo",
      jumps: [],
      targets: [
        {
          serverApplicationId: 8,
          application: "billing-api",
          version: "2.0.0",
          targetHost: "127.0.0.1",
          targetPort: 9090,
          suggestedLocalPort: 19080,
        },
      ],
      services: [
        {
          id: 10,
          name: "redis",
          mode: "Dynamic",
          kind: "Tcp",
          enabled: true,
          autoStart: false,
          targetHost: null,
          targetPort: null,
          localPort: 16379,
          remotePort: null,
          openPath: null,
        },
      ],
    },
    {
      serverId: 3,
      serverName: "demo-app-02",
      environmentLabel: "Produzione",
      sshHost: "10.0.0.30",
      sshPort: 22,
      username: "demo",
      jumps: [],
      targets: [
        {
          serverApplicationId: 9,
          application: "metrics",
          version: "1.0.0",
          targetHost: "127.0.0.1",
          targetPort: 9100,
          suggestedLocalPort: 19100,
        },
      ],
      services: [],
    },
  ];
  const demoServer = (name, ip) => ({
    serverId: name === "demo-app-01" ? 1 : 2,
    serverName: name,
    environmentLabel: "Demo / Produzione",
    sshHost: ip,
    ipAddress: ip,
    sshPort: 22,
    username: "demo",
    jumps: [],
    targets: [],
    services: [],
  });
  try {
    const first = tunnels.start({
      id: "demo-tunnel-active",
      url: instance.url,
      instanceName: instance.name,
      server: demoServer("demo-app-01", "10.0.0.10"),
      target: {
        serverApplicationId: 7,
        application: "orders-api",
        targetHost: "127.0.0.1",
        targetPort: 8081,
        suggestedLocalPort: 8081,
      },
      service: null,
      localPort: 18081,
    });
    console.log("TEST_TUNNEL_ACTIVE", first.id);
    tunnels.start({
      id: "demo-tunnel-stopped",
      url: instance.url,
      instanceName: instance.name,
      server: demoServer("demo-db-01", "10.0.0.20"),
      target: null,
      service: {
        id: 9,
        name: "postgres",
        mode: "Local",
        kind: "Tcp",
        targetHost: "10.0.0.20",
        targetPort: 5432,
        localPort: 15432,
      },
      localPort: 15432,
    });
    setTimeout(() => tunnels.stop("demo-tunnel-stopped", instance.url), 300);
  } catch (error) {
    console.log("TEST_TUNNEL_SEED_FAIL", error.message);
  }
}

async function waitFor(condition, timeout = 15_000, interval = 250) {
  const started = Date.now();
  while (Date.now() - started < timeout) {
    if (await condition()) return true;
    await new Promise((resolve) => setTimeout(resolve, interval));
  }
  return false;
}

async function captureScreenshot(contents, name) {
  const target = path.join(__dirname, "test", name);
  for (let attempt = 0; attempt < 4; attempt += 1) {
    try {
      const image = await contents.capturePage();
      if (!image.isEmpty()) {
        fs.writeFileSync(target, image.toPNG());
        console.log("TEST_SCREENSHOT", target);
        return true;
      }
    } catch {
      // The compositor can be between frames; retry briefly.
    }
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  console.log("TEST_SCREENSHOT_SKIP", target);
  return false;
}

async function runTest() {
  let failed = false;
  const check = (condition, label, detail = "") => {
    if (condition) {
      console.log("TEST_OK", label);
      return true;
    }
    failed = true;
    console.log("TEST_FAIL", label, detail);
    return false;
  };

  try {
    const first = instanceStore.list()[0];
    const record = recordFor(first.url);
    await waitFor(() => record?.loaded, 20_000);
    await new Promise((resolve) => setTimeout(resolve, 1500));

    const login = async () => {
      const url = record.view.webContents.getURL();
      if (!url.includes("/realms/")) return;
      console.log("TEST_LOGIN", "submitting local dev credentials");
      await record.view.webContents.executeJavaScript(`(() => {
        const user = document.querySelector("#username");
        const password = document.querySelector("#password");
        if (!user || !password) return false;
        user.value = ${JSON.stringify(process.env.PROTO_USER || "admin")};
        password.value = ${JSON.stringify(process.env.PROTO_PASSWORD || "dev-admin")};
        document.querySelector("#kc-login")?.click();
        return true;
      })()`);
      await new Promise((resolve) => setTimeout(resolve, 3500));
      await waitFor(
        () => !record.view.webContents.getURL().includes("/realms/"),
        15_000,
      );
    };
    await login();
    await new Promise((resolve) => setTimeout(resolve, 2000));
    await refreshInstanceData(first);
    await new Promise((resolve) => setTimeout(resolve, 1500));

    const toggleWebTheme = () =>
      record.view.webContents.executeJavaScript(`(() => {
        const button = [...document.querySelectorAll("button[aria-label]")].find((node) => /tema|theme/i.test(node.getAttribute("aria-label") || ""));
        if (!button) return false;
        button.click();
        return true;
      })()`);
    const setWebTheme = async (target) => {
      for (let attempt = 0; attempt < 4; attempt += 1) {
        const current = await record.view.webContents.executeJavaScript(
          'localStorage.getItem("infraorgit-theme") || "system"',
        );
        if (current === target) return;
        await toggleWebTheme();
        await new Promise((resolve) => setTimeout(resolve, 500));
      }
    };

    const state = publicState();
    check(state.instances.length === 3, "three demo instances", String(state.instances.length));
    check(state.tabs.length >= 1, "startup tab opened", String(state.tabs.length));
    check(state.instances[0].active === true, "landing tab active");
    check(
      state.instances[0].serverCount !== null,
      "server count loaded",
      String(state.instances[0].serverCount),
    );
    check(
      record.view.webContents.getURL().includes("/"),
      "instance loaded",
      record.view.webContents.getURL(),
    );

    const bridgeInfo = await record.view.webContents.executeJavaScript(
      "({explicit: typeof window.infraorgitDesktop, explicitSend: typeof window.infraorgitDesktop?.sendMessage, legacy: typeof window.external, legacySend: typeof window.external?.sendMessage, node: typeof window.require, process: typeof window.process})",
    );
    check(bridgeInfo.explicit === "object", "instance bridge exposed", JSON.stringify(bridgeInfo));
    check(bridgeInfo.explicitSend === "function", "instance sendMessage exposed");
    check(bridgeInfo.legacySend === "undefined", "no legacy desktop bridge");
    check(bridgeInfo.node === "undefined", "no node require in renderer");
    check(bridgeInfo.process === "undefined", "no process in renderer");

    const managerBridge = await mainWindow.webContents.executeJavaScript(
      "({manager: typeof window.desktopManager, node: typeof window.require, platform: window.desktopManager?.platform, shortcut: document.querySelector('[data-shortcut]')?.textContent})",
    );
    check(managerBridge.manager === "object", "manager bridge exposed", JSON.stringify(managerBridge));
    check(managerBridge.node === "undefined", "no node in manager");
    check(
      process.platform === "darwin"
        ? managerBridge.shortcut?.includes("⌘")
        : managerBridge.shortcut?.includes("Ctrl"),
      "platform command shortcut label",
      String(managerBridge.shortcut),
    );

    const invalid = await mainWindow.webContents.executeJavaScript(
      "window.desktopManager.addInstance({name:'Bad',url:'http://remote.example.com'}).then(() => 'accepted').catch((error) => error.message)",
    );
    check(invalid.includes("https"), "remote http rejected", invalid);

    const initialTheme = publicState().theme.preference;
    check(
      (await toggleWebTheme()) === true,
      "web theme toggle available",
    );
    await new Promise((resolve) => setTimeout(resolve, 600));
    const shellTheme = publicState().theme;
    const webDark = await record.view.webContents.executeJavaScript(
      'document.documentElement.classList.contains("dark")',
    );
    check(
      shellTheme.preference !== initialTheme,
      "shell follows the web theme",
      `${initialTheme} -> ${shellTheme.preference}`,
    );
    check(
      (shellTheme.resolved === "dark") === webDark,
      "shell and web theme agree",
      `${shellTheme.resolved} / webDark=${webDark}`,
    );
    // The README screenshots use the dark control-room shell, but the Web
    // page keeps the system icon visible in the instance screenshot.
    await setWebTheme("system");

    const palette = await mainWindow.webContents.executeJavaScript(`(async () => {
      document.dispatchEvent(new KeyboardEvent("keydown", {key: "k", metaKey: ${process.platform === "darwin"}, ctrlKey: ${process.platform !== "darwin"}, bubbles: true}));
      await new Promise((resolve) => setTimeout(resolve, 120));
      return {visible: !document.querySelector("#palette")?.hidden, items: document.querySelectorAll("#palette-list [role=option]").length};
    })()`);
    check(palette.visible === true, "command palette opens");
    check(palette.items > 2, "command palette has actions", String(palette.items));
    await captureScreenshot(mainWindow.webContents, "palette.png");
    await mainWindow.webContents.executeJavaScript(
      "document.querySelector('#palette-input').dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))",
    );

    showLanding();
    await new Promise((resolve) => setTimeout(resolve, 500));
    await captureScreenshot(mainWindow.webContents, "landing.png");

    await captureScreenshot(record.view.webContents, "instance.png");

    await setWebTheme("dark");
    sendCommand("toggle-drawer");
    await new Promise((resolve) => setTimeout(resolve, 600));
    const drawerState = await mainWindow.webContents.executeJavaScript(
      "({open: document.body.dataset.drawer === 'open', rows: document.querySelectorAll('[data-tunnel-id]').length, groups: document.querySelectorAll('[data-instance-group]').length, port: document.querySelector('[data-tunnel-id] .context-port')?.textContent || ''})",
    );
    check(drawerState.open === true, "tunnel drawer open");
    check(drawerState.rows >= 2, "drawer shows active and stopped rows", String(drawerState.rows));
    check(drawerState.groups >= 1, "drawer groups by instance");
    check(
      drawerState.port.includes("18081") || drawerState.port.includes("15432"),
      "drawer shows the local port",
      drawerState.port,
    );

    const newTunnel = await mainWindow.webContents.executeJavaScript(`(async () => {
      const details = document.querySelector(".new-tunnel");
      if (!details) return {missing: true};
      details.open = true;
      const search = details.querySelector('input[type="search"]');
      search.value = "10.0.0.20";
      search.dispatchEvent(new Event("input", {bubbles: true}));
      await new Promise((resolve) => setTimeout(resolve, 80));
      return {
        environments: [...details.querySelectorAll(".new-tunnel-env-title")].map((node) => node.textContent),
        servers: [...details.querySelectorAll(".new-tunnel-server-name")].map((node) => node.textContent),
        options: [...details.querySelectorAll(".new-tunnel-option")].map((node) => node.textContent),
        selected: details.dataset.selected,
        port: details.querySelector('input[type="number"]')?.value || "",
      };
    })()`);
    check(newTunnel.missing !== true, "new tunnel form present");
    check(
      newTunnel.environments.length === 1 &&
        newTunnel.environments[0] === "Staging",
      "new tunnel groups by environment",
      JSON.stringify(newTunnel.environments),
    );
    check(
      newTunnel.servers.length === 1 && newTunnel.servers[0] === "demo-db-01",
      "new tunnel search filters server",
      JSON.stringify(newTunnel.servers),
    );
    check(
      newTunnel.options.length === 2,
      "new tunnel search filters services",
      String(newTunnel.options.length),
    );
    check(
      newTunnel.selected.startsWith("target:2:"),
      "new tunnel selects a visible result",
      newTunnel.selected,
    );
    check(
      newTunnel.port === "19080",
      "new tunnel uses the suggested port",
      newTunnel.port,
    );

    const beforeNewService = await mainWindow.webContents.executeJavaScript(`(async () => {
      const details = document.querySelector(".new-tunnel");
      const search = details.querySelector('input[type="search"]');
      search.value = "";
      search.dispatchEvent(new Event("input", {bubbles: true}));
      await new Promise((resolve) => setTimeout(resolve, 80));
      return details.querySelectorAll(".new-tunnel-option").length;
    })()`);
    runtimeFor(first.url).targets[0].services.push({
      id: 11,
      name: "backup",
      mode: "Local",
      kind: "Tcp",
      enabled: true,
      autoStart: false,
      targetHost: "10.0.0.10",
      targetPort: 22,
      localPort: 15434,
      remotePort: null,
      openPath: null,
    });
    sendState();
    await new Promise((resolve) => setTimeout(resolve, 300));
    const afterNewService = await mainWindow.webContents.executeJavaScript(
      'document.querySelectorAll(".new-tunnel-option").length',
    );
    check(
      afterNewService === beforeNewService + 1,
      "new tunnel picks up a new service",
      `${beforeNewService} -> ${afterNewService}`,
    );

    const created = await mainWindow.webContents.executeJavaScript(`(async () => {
      const details = document.querySelector(".new-tunnel");
      const search = details.querySelector('input[type="search"]');
      search.value = "orders-api";
      search.dispatchEvent(new Event("input", {bubbles: true}));
      await new Promise((resolve) => setTimeout(resolve, 80));
      details.querySelector('.new-tunnel-option[data-value="target:1:7"]')?.click();
      const port = details.querySelector('input[type="number"]');
      if (port) port.value = "19090";
      details.querySelector(".new-tunnel-form .row button")?.click();
      await new Promise((resolve) => setTimeout(resolve, 800));
      return {
        selected: details.dataset.selected,
        toast: document.querySelector("#toast")?.textContent || "",
      };
    })()`);
    check(
      created.selected === "target:1:7",
      "new tunnel keeps the searched selection",
      created.selected,
    );
    const tunnelCreated = await waitFor(
      () =>
        publicState().tunnels.some(
          (tunnel) => tunnel.localPort === 19090 && tunnel.status === "Active",
        ),
      6000,
    );
    check(
      tunnelCreated,
      "new tunnel started from the searchable form",
      created.toast,
    );

    await mainWindow.webContents.executeJavaScript(`(() => {
      const details = document.querySelector(".new-tunnel");
      const search = details?.querySelector('input[type="search"]');
      if (!search) return;
      search.value = "";
      search.dispatchEvent(new Event("input", {bubbles: true}));
    })()`);
    await new Promise((resolve) => setTimeout(resolve, 100));
    const envGroups = await mainWindow.webContents.executeJavaScript(`(() => {
      const details = document.querySelector(".new-tunnel");
      return details ? [...details.querySelectorAll(".new-tunnel-env-title")].map((node) => node.textContent) : [];
    })()`);
    check(
      new Set(envGroups).size === envGroups.length,
      "environment groups are unique",
      JSON.stringify(envGroups),
    );
    await new Promise((resolve) => setTimeout(resolve, 2600));
    await captureScreenshot(mainWindow.webContents, "drawer.png");

    activateTab(first.url);
    await record.view.webContents.loadURL(`${first.url}/tunnels`);
    await new Promise((resolve) => setTimeout(resolve, 2000));
    const webTunnel = await record.view.webContents.executeJavaScript(
      "({bridge: typeof window.infraorgitDesktop, panel: Boolean(document.querySelector('[data-testid=\"desktop-tunnels-panel\"]')), text: document.body.innerText.slice(0, 120)})",
    );
    check(webTunnel.bridge === "object", "web tunnel page bridge");
    check(webTunnel.panel === true, "web /tunnels desktop panel", JSON.stringify(webTunnel));
    await captureScreenshot(record.view.webContents, "web-tunnels.png");

    const serverList = await mainWindow.webContents.executeJavaScript(
      `window.desktopManager.serverList(${JSON.stringify(first.url)})`,
    );
    check(serverList.authenticated === true, "shell IPC server list authenticated");
    check(serverList.items.length > 0, "shell IPC server list has rows", String(serverList.items.length));

    const added = await mainWindow.webContents.executeJavaScript(
      "window.desktopManager.addInstance({name:'Temporanea',url:'https://temp.example.com',environment:'Test'}).then((next) => next.instances.length)",
    );
    check(added === 4, "shell IPC adds an instance", String(added));
    const duplicate = await mainWindow.webContents.executeJavaScript(
      `window.desktopManager.addInstance({name:'Duplicata',url:${JSON.stringify(first.url)}}).then(() => 'accepted').catch((error) => error.message)`,
    );
    check(duplicate.includes("già presente"), "shell IPC rejects a duplicate", duplicate);
    const removed = await mainWindow.webContents.executeJavaScript(
      "window.desktopManager.removeInstance('https://temp.example.com').then((next) => next.instances.length)",
    );
    check(removed === 3, "shell IPC removes an instance", String(removed));

    const newModal = await mainWindow.webContents.executeJavaScript(`(async () => {
      document.querySelector("#new-instance").click();
      await new Promise((resolve) => setTimeout(resolve, 120));
      const visible = !document.querySelector("#modal").hidden;
      const title = document.querySelector("#modal-title").textContent;
      document.querySelector("#modal-close").click();
      return {visible, title};
    })()`);
    check(newModal.visible === true, "new instance modal opens");
    check(newModal.title === "Nuova istanza", "new instance modal title", newModal.title);

    const manageModal = await mainWindow.webContents.executeJavaScript(`(async () => {
      const card = [...document.querySelectorAll(".instance-card")].find((item) => item.textContent.includes("Produzione"));
      [...card.querySelectorAll("button")].find((button) => button.textContent === "Gestisci").click();
      await new Promise((resolve) => setTimeout(resolve, 3000));
      const visible = !document.querySelector("#modal").hidden;
      const title = document.querySelector("#modal-title").textContent;
      const servers = document.querySelectorAll(".server-item").length;
      document.querySelector("#modal-close").click();
      return {visible, title, servers};
    })()`);
    check(manageModal.visible === true, "manage modal opens");
    check(manageModal.title === "Produzione", "manage modal title", manageModal.title);
    check(manageModal.servers > 0, "manage modal lists server anagrafica", String(manageModal.servers));

    const secondTab = publicState().instances[1];
    activateTab(secondTab.url);
    await new Promise((resolve) => setTimeout(resolve, 1200));
    check(publicState().activeUrl === secondTab.url, "tab switch loads instance");
    showLanding();
    await new Promise((resolve) => setTimeout(resolve, 500));
    await setWebTheme("light");
    await captureScreenshot(mainWindow.webContents, "light.png");
    await setWebTheme("dark");

    console.log(failed ? "TEST_RESULT FAIL" : "TEST_RESULT PASS");
  } catch (error) {
    failed = true;
    console.log("TEST_RESULT FAIL", error.stack || error.message);
  } finally {
    tunnels?.stopAll();
    desktopAuth?.dispose();
    setTimeout(() => app.exit(failed ? 1 : 0), 800);
  }
}

app.on("second-instance", () => {
  if (mainWindow) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  }
});

const hasLock = app.requestSingleInstanceLock();
if (!hasLock) {
  app.quit();
} else {
  app.whenReady().then(async () => {
    if (process.platform === "darwin" && app.dock)
      app.dock.setIcon(path.join(__dirname, "assets", "icon.png"));
    if (allowLoopbackCertificates) applyLoopbackPolicy(session.defaultSession);

    if (IS_TEST) {
      process.env.SSH_BIN = path.join(__dirname, "test", "fake-ssh.js");
      process.env.FAKE_SSH_LOG = path.join(__dirname, "test", ".run", "ssh.log");
    }

    instanceStore = new InstanceStore(
      path.join(app.getPath("userData"), "instances.json"),
    );
    settingsStore = new SettingsStore(
      path.join(app.getPath("userData"), "settings.json"),
    );
    tokenStore = new RefreshTokenStore({
      file: path.join(app.getPath("userData"), "desktop-auth.bin"),
      safeStorage,
    });
    await tokenStore.initialize();
    tokenStore.load();

    const deviceClient = new DeviceAuthClient({
      fetchImpl: (input, init = {}) =>
        net.fetch(input, {
          ...init,
          signal: init.signal || AbortSignal.timeout(15_000),
        }),
    });
    desktopAuth = new DesktopAuth({
      deviceClient,
      tokenStore,
      openExternal: (url) => shell.openExternal(url),
      onChange: (url, snapshot) => {
        if (snapshot.theme && THEMES.includes(snapshot.theme)) {
          accountThemes.set(url, snapshot.theme);
          settingsStore.setTheme(snapshot.theme);
          if (!activeUrl || activeUrl === url) lastThemeUrl = url;
        }
        const record = recordFor(url);
        const fingerprint = JSON.stringify(snapshot);
        if (record) {
          const runtimeData = runtimeFor(url);
          if (fingerprint !== runtimeData.lastAuthSent) {
            const previous = runtimeData.lastAuthSent
              ? JSON.parse(runtimeData.lastAuthSent)
              : null;
            runtimeData.lastAuthSent = fingerprint;
            const justLoggedIn =
              previous?.status === "pending" && snapshot.status === "ready";
            sendWebAuth(url, {
              forcedStatus: justLoggedIn ? "success" : null,
            });
            if (snapshot.status === "ready" && snapshot.tunnelAccess) {
              const instance = instanceStore.get(url);
              if (instance) void loadTargets(instance);
            }
          }
        }
        sendState();
      },
    });
    tunnelProjection = new TunnelProjection({ post: postTunnelProjection });
    tunnels = new TunnelManager(() => {
      broadcastTunnels();
      if (!IS_TEST) tunnelProjection?.sync(tunnels.list());
    });

    seedTestInstances();
    startTestTunnels();
    createMainWindow();
    registerIpc();
    buildMenu();

    ipcMain.on("desktop-message", (event, raw) => {
      void handleDesktopMessage(event.sender, raw);
    });

    nativeTheme.on("updated", () => {
      if (activeThemePreference() === "system") applyThemeToManager();
    });

    openStartupTabs();
    authRenewTimer = setInterval(
      () => void renewOpenInstanceAuth(),
      AUTH_RENEW_INTERVAL,
    );
    powerMonitor.on("resume", () => void renewOpenInstanceAuth());
    setTimeout(() => primeLandingCounts(), 1200);

    if (IS_TEST) {
      if (!activeUrl) activateTab(instanceStore.list()[0]?.url);
      setTimeout(() => void runTest(), 500);
    }
  });

  app.on("before-quit", () => {
    quitting = true;
    if (authRenewTimer) clearInterval(authRenewTimer);
    tunnels?.stopAll();
    tunnelProjection?.dispose();
    desktopAuth?.dispose();
  });

  app.on("window-all-closed", () => {
    if (process.platform !== "darwin" || quitting) app.quit();
  });

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createMainWindow();
      openStartupTabs();
    }
  });
}
