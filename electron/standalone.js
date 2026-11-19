const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const net = require("node:net");
const path = require("node:path");
const { spawn } = require("node:child_process");

const HEALTH_TIMEOUT_MS = 60_000;

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

function health(url) {
  return new Promise((resolve) => {
    const request = http.get(`${url}/health/live`, { timeout: 1000 }, (response) => {
      response.resume();
      resolve(response.statusCode === 200);
    });
    request.on("error", () => resolve(false));
    request.on("timeout", () => {
      request.destroy();
      resolve(false);
    });
  });
}

function localSecret(
  file,
  safeStorage,
  logger = console,
  { allowUnprotected = false } = {},
) {
  const fallback = `${file}.fallback`;
  if (fs.existsSync(file)) {
    try {
      return {
        value: safeStorage.decryptString(fs.readFileSync(file)),
        storage: "safeStorage",
      };
    } catch {
      throw new Error(
        "Chiave delle credenziali locali illeggibile. Ripristina il backup o rimuovi il file solo se accetti di perdere le credenziali cifrate.",
      );
    }
  }
  if (fs.existsSync(fallback)) {
    const stored = fs.readFileSync(fallback, "utf8").trim();
    if (stored) return { value: stored, storage: "file" };
  }
  const secret = crypto.randomBytes(32).toString("base64");
  let available = false;
  try {
    available = Boolean(safeStorage?.isEncryptionAvailable?.());
  } catch {
    available = false;
  }
  if (available) {
    try {
      fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
      fs.writeFileSync(file, safeStorage.encryptString(secret), { mode: 0o600 });
      return { value: secret, storage: "safeStorage" };
    } catch {
      // Fall through to the explicit 0600 fallback.
    }
  }
  if (!allowUnprotected) {
    throw new Error(
      "Archivio protetto non disponibile: abilita «Consenti chiave credenziali non protetta» nelle Impostazioni per usare l'istanza locale.",
    );
  }
  fs.mkdirSync(path.dirname(fallback), { recursive: true, mode: 0o700 });
  fs.writeFileSync(fallback, secret, { mode: 0o600 });
  logger.warn(
    "Archivio protetto non disponibile: chiave credenziali locale salvata in un file 0600.",
  );
  return { value: secret, storage: "file" };
}

function apiCommand({ isPackaged, resourcesPath, root }) {
  if (process.env.DESKTOP_API_DLL) {
    return {
      command: process.env.DOTNET_BIN || "dotnet",
      args: [process.env.DESKTOP_API_DLL],
    };
  }
  if (isPackaged) {
    const binary =
      process.platform === "win32" ? "InfraOrgIt.Api.exe" : "InfraOrgIt.Api";
    return {
      command: path.join(resourcesPath, "standalone", binary),
      args: [],
    };
  }
  return {
    command: process.env.DOTNET_BIN || "dotnet",
    args: [
      path.join(
        root,
        "..",
        "..",
        "InfraOrgITApi",
        "src",
        "InfraOrgIt.Api",
        "bin",
        "Debug",
        "net10.0",
        "InfraOrgIt.Api.dll",
      ),
    ],
  };
}

/**
 * Starts the local API sidecar on loopback. The caller owns the returned
 * handle and must stop it on quit. The bootstrap secret is per launch and never
 * written to instances.json or logs.
 */
async function startStandalone({
  app,
  safeStorage,
  root = __dirname,
  logger = console,
  enabled = process.env.DESKTOP_STANDALONE === "1",
  allowUnprotectedCredentialKey = false,
  dataDirectory = null,
  logName = "standalone-api.log",
  shouldAbort = () => false,
  spawnProcess = spawn,
  healthCheck = health,
} = {}) {
  if (!enabled) return null;

  const dataRoot =
    dataDirectory || path.join(app.getPath("userData"), "standalone");
  const database = path.join(dataRoot, "infraorgit.db");
  const backupDirectory = path.join(dataRoot, "backups");
  const logDirectory = path.join(app.getPath("userData"), "logs");
  fs.mkdirSync(dataRoot, { recursive: true, mode: 0o700 });
  fs.mkdirSync(backupDirectory, { recursive: true, mode: 0o700 });
  fs.mkdirSync(logDirectory, { recursive: true, mode: 0o700 });

  const webRoot = process.env.DESKTOP_WEB_ROOT
    ? path.resolve(process.env.DESKTOP_WEB_ROOT)
    : app.isPackaged
      ? path.join(process.resourcesPath, "web")
      : path.join(root, "..", "..", "InfraOrgITWeb", "dist");
  if (!fs.existsSync(path.join(webRoot, "index.html")))
    throw new Error(`Web bundle locale non trovata: ${webRoot}`);

  const command = apiCommand({
    isPackaged: app.isPackaged,
    resourcesPath: process.resourcesPath,
    root,
  });
  if (!fs.existsSync(command.command) && command.command !== "dotnet")
    throw new Error(`Sidecar locale non trovato: ${command.command}`);

  const port = await freePort();
  const url = `http://127.0.0.1:${port}`;
  const secret = crypto.randomBytes(32).toString("hex");
  const keyInfo = localSecret(
    path.join(dataRoot, "credential-key.bin"),
    safeStorage,
    logger,
    { allowUnprotected: allowUnprotectedCredentialKey },
  );
  const key = keyInfo.value;
  const logFile = path.join(logDirectory, logName);
  const log = fs.createWriteStream(logFile, { flags: "a", mode: 0o600 });

  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "Production",
    APP_VERSION: app.getVersion(),
    Database__Provider: "Sqlite",
    ConnectionStrings__Sqlite: `Data Source=${database}`,
    Local__Enabled: "true",
    Local__BootstrapSecret: secret,
    Local__Username: "local",
    Local__WebRoot: webRoot,
    Local__BackupDirectory: backupDirectory,
    Auth__SecureCookies: "false",
    Auth__BootstrapAdminUsername: "local",
    Frontend__BaseUrl: url,
    ServerAccess__CredentialKeys__1: key,
    ServerAccess__CurrentKeyVersion: "1",
    Mcp__Enabled: "false",
    DOTNET_NOLOGO: "1",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  };

  const spawnOptions = {
    env,
    cwd: dataRoot,
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  };
  const spawnSidecar = () => {
    const processHandle = spawnProcess(
      command.command,
      [...command.args, "--urls", url],
      spawnOptions,
    );
    processHandle.stdout.pipe(log, { end: false });
    processHandle.stderr.pipe(log, { end: false });
    return processHandle;
  };
  let child = spawnSidecar();
  let stopped = false;
  let restarted = false;
  const waitUntilHealthy = async (timeoutMs = HEALTH_TIMEOUT_MS) => {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (shouldAbort()) return false;
      if (await healthCheck(url)) return true;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return false;
  };
  const restartOnce = async () => {
    if (stopped || restarted || shouldAbort()) return;
    restarted = true;
    logger.warn("Sidecar locale terminato: nuovo tentativo.");
    await new Promise((resolve) => setTimeout(resolve, 500));
    if (stopped || shouldAbort()) return;
    child = spawnSidecar();
    child.once("exit", (code, signal) => {
      log.write(`\nsidecar exit code=${code} signal=${signal}\n`);
      if (!stopped) logger.error("Sidecar locale terminato dopo il riavvio.");
    });
    if (!(await waitUntilHealthy())) {
      logger.error("Sidecar locale non disponibile dopo il riavvio.");
    }
  };
  child.once("exit", (code, signal) => {
    log.write(`\nsidecar exit code=${code} signal=${signal}\n`);
    if (!stopped && !shouldAbort()) void restartOnce();
  });

  if (!(await waitUntilHealthy())) {
    try {
      child.kill("SIGTERM");
    } catch {
      // already gone
    }
    log.end();
    if (shouldAbort()) return null;
    throw new Error(
      `Il sidecar locale non risponde su ${url}. Log: ${logFile}`,
    );
  }
  logger.log(`standalone api ready on ${url}`);
  return {
    url,
    secret,
    port,
    dataRoot,
    database,
    keyStorage: keyInfo.storage,
    killNow() {
      try {
        child.kill("SIGKILL");
      } catch {
        // already gone
      }
    },
    stop() {
      if (stopped) return Promise.resolve();
      stopped = true;
      return new Promise((resolve) => {
        let settled = false;
        const finish = () => {
          if (settled) return;
          settled = true;
          log.end();
          resolve();
        };
        if (child.exitCode !== null || child.signalCode !== null) {
          finish();
          return;
        }
        const timer = setTimeout(() => {
          try {
            child.kill("SIGKILL");
          } catch {
            // already gone
          }
          finish();
        }, 3000);
        child.once("exit", () => {
          clearTimeout(timer);
          finish();
        });
        try {
          child.kill("SIGTERM");
        } catch {
          clearTimeout(timer);
          finish();
        }
      });
    },
  };
}

module.exports = { startStandalone, freePort, health };
