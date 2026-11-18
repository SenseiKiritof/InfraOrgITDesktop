const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const net = require("node:net");
const path = require("node:path");
const { spawn } = require("node:child_process");

const HEALTH_TIMEOUT_MS = 25_000;

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

function localSecret(file, safeStorage) {
  try {
    const encrypted = fs.readFileSync(file);
    return safeStorage.decryptString(encrypted);
  } catch {
    const secret = crypto.randomBytes(32).toString("base64");
    if (safeStorage?.isEncryptionAvailable?.()) {
      try {
        fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
        fs.writeFileSync(file, safeStorage.encryptString(secret), { mode: 0o600 });
      } catch {
        // In-memory only when the OS store is unavailable.
      }
    }
    return secret;
  }
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
} = {}) {
  const enabled = process.env.DESKTOP_STANDALONE === "1";
  if (!enabled) return null;

  const dataRoot = path.join(app.getPath("userData"), "standalone");
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
  const key = localSecret(
    path.join(dataRoot, "credential-key.bin"),
    safeStorage,
  );
  const logFile = path.join(logDirectory, "standalone-api.log");
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
    Oidc__Issuer: "https://localhost.invalid/realms/infraorgit",
    Oidc__ClientId: "infraorgit",
    Oidc__DesktopClientId: "infraorgit-mcp-desktop",
    Oidc__RedirectUri: `${url}/auth/callback`,
    ServerAccess__CredentialKeys__1: key,
    ServerAccess__CurrentKeyVersion: "1",
    Mcp__Enabled: "false",
    DOTNET_NOLOGO: "1",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  };

  const child = spawn(command.command, [...command.args, "--urls", url], {
    env,
    cwd: dataRoot,
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });
  child.stdout.pipe(log, { end: false });
  child.stderr.pipe(log, { end: false });
  let stopped = false;
  child.once("exit", (code, signal) => {
    log.write(`\nsidecar exit code=${code} signal=${signal}\n`);
  });

  const deadline = Date.now() + HEALTH_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (await health(url)) {
      logger.log(`standalone api ready on ${url}`);
      return {
        url,
        secret,
        port,
        dataRoot,
        database,
        stop() {
          if (stopped) return;
          stopped = true;
          try {
            child.kill("SIGTERM");
          } catch {
            // already exited
          }
          log.end();
        },
      };
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  child.kill("SIGTERM");
  log.end();
  throw new Error(
    `Il sidecar locale non risponde su ${url}. Log: ${logFile}`,
  );
}

module.exports = { startStandalone, freePort, health };
