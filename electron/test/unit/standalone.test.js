const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { EventEmitter } = require("node:events");
const { PassThrough } = require("node:stream");
const { startStandalone } = require("../../standalone");

test("standalone supervisor starts a local sidecar with protected configuration", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-standalone-"));
  const webRoot = path.join(root, "web");
  fs.mkdirSync(webRoot, { recursive: true });
  fs.writeFileSync(path.join(webRoot, "index.html"), "<html></html>");
  const userData = path.join(root, "user-data");
  const previousWeb = process.env.DESKTOP_WEB_ROOT;
  process.env.DESKTOP_WEB_ROOT = webRoot;
  let spawned = null;

  class FakeChild extends EventEmitter {
    constructor() {
      super();
      this.stdout = new PassThrough();
      this.stderr = new PassThrough();
      this.killed = false;
    }
    kill() {
      this.killed = true;
    }
  }

  try {
    const handle = await startStandalone({
      enabled: true,
      app: {
        isPackaged: false,
        getPath: () => userData,
        getVersion: () => "0.3.15",
      },
      safeStorage: {
        isEncryptionAvailable: () => false,
      },
      allowUnprotectedCredentialKey: true,
      logger: { log() {}, warn() {}, error() {} },
      spawnProcess: (command, args, options) => {
        spawned = { command, args, options };
        return new FakeChild();
      },
      healthCheck: async () => true,
    });

    assert.match(handle.url, /^http:\/\/127\.0\.0\.1:\d+$/);
    assert.equal(spawned.command, "dotnet");
    assert.ok(spawned.args.includes("--urls"));
    assert.equal(spawned.options.env.Database__Provider, "Sqlite");
    assert.equal(spawned.options.env.Local__Enabled, "true");
    assert.equal(spawned.options.env.Auth__SecureCookies, "false");
    assert.equal(spawned.options.env.Mcp__Enabled, "false");
    assert.ok(
      fs.existsSync(path.join(userData, "standalone", "credential-key.bin.fallback")),
    );
    assert.equal(handle.keyStorage, "file");
    await handle.stop();
    assert.equal(handle.secret.length, 64);
    await new Promise((resolve) => setTimeout(resolve, 20));
  } finally {
    if (previousWeb === undefined) delete process.env.DESKTOP_WEB_ROOT;
    else process.env.DESKTOP_WEB_ROOT = previousWeb;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("standalone aborts cleanly while quitting", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-abort-"));
  const webRoot = path.join(root, "web");
  fs.mkdirSync(webRoot, { recursive: true });
  fs.writeFileSync(path.join(webRoot, "index.html"), "<html></html>");
  const userData = path.join(root, "user-data");
  const previousWeb = process.env.DESKTOP_WEB_ROOT;
  process.env.DESKTOP_WEB_ROOT = webRoot;

  class FakeChild extends EventEmitter {
    constructor() {
      super();
      this.stdout = new PassThrough();
      this.stderr = new PassThrough();
      this.exitCode = null;
      this.signalCode = null;
      this.killed = false;
    }
    kill() {
      this.killed = true;
      this.exitCode = 0;
      this.emit("exit", 0, null);
    }
  }

  try {
    let child = null;
    let spawns = 0;
    const result = await startStandalone({
      enabled: true,
      app: {
        isPackaged: false,
        getPath: () => userData,
        getVersion: () => "0.4.1",
      },
      safeStorage: { isEncryptionAvailable: () => false },
      allowUnprotectedCredentialKey: true,
      logger: { log() {}, warn() {}, error() {} },
      shouldAbort: () => true,
      spawnProcess: () => {
        spawns += 1;
        child = new FakeChild();
        return child;
      },
      healthCheck: async () => true,
    });
    assert.equal(result, null);
    assert.equal(child.killed, true);
    await new Promise((resolve) => setTimeout(resolve, 700));
    assert.equal(spawns, 1);
  } finally {
    if (previousWeb === undefined) delete process.env.DESKTOP_WEB_ROOT;
    else process.env.DESKTOP_WEB_ROOT = previousWeb;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("standalone requires explicit consent for an unprotected credential key", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-consent-"));
  const webRoot = path.join(root, "web");
  fs.mkdirSync(webRoot, { recursive: true });
  fs.writeFileSync(path.join(webRoot, "index.html"), "<html></html>");
  const userData = path.join(root, "user-data");
  const previousWeb = process.env.DESKTOP_WEB_ROOT;
  process.env.DESKTOP_WEB_ROOT = webRoot;
  try {
    await assert.rejects(
      () =>
        startStandalone({
          enabled: true,
          app: {
            isPackaged: false,
            getPath: () => userData,
            getVersion: () => "0.3.19",
          },
          safeStorage: { isEncryptionAvailable: () => false },
          logger: { log() {}, warn() {}, error() {} },
          spawnProcess: () => {
            throw new Error("il sidecar non deve partire");
          },
          healthCheck: async () => true,
        }),
      /Consenti chiave credenziali/,
    );
    assert.equal(
      fs.existsSync(
        path.join(userData, "standalone", "credential-key.bin.fallback"),
      ),
      false,
    );
  } finally {
    if (previousWeb === undefined) delete process.env.DESKTOP_WEB_ROOT;
    else process.env.DESKTOP_WEB_ROOT = previousWeb;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("standalone instances keep separate data directories", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-multi-"));
  const webRoot = path.join(root, "web");
  fs.mkdirSync(webRoot, { recursive: true });
  fs.writeFileSync(path.join(webRoot, "index.html"), "<html></html>");
  const userData = path.join(root, "user-data");
  const previousWeb = process.env.DESKTOP_WEB_ROOT;
  process.env.DESKTOP_WEB_ROOT = webRoot;

  class FakeChild extends EventEmitter {
    constructor() {
      super();
      this.stdout = new PassThrough();
      this.stderr = new PassThrough();
      this.exitCode = null;
      this.signalCode = null;
    }
    kill() {
      this.exitCode = 0;
      this.emit("exit", 0, null);
    }
  }

  try {
    const start = (name) =>
      startStandalone({
        enabled: true,
        app: {
          isPackaged: false,
          getPath: () => userData,
          getVersion: () => "0.4.0",
        },
        safeStorage: { isEncryptionAvailable: () => false },
        allowUnprotectedCredentialKey: true,
        dataDirectory: path.join(userData, "standalone", "instances", name),
        logName: `standalone-api-${name}.log`,
        logger: { log() {}, warn() {}, error() {} },
        spawnProcess: () => new FakeChild(),
        healthCheck: async () => true,
      });

    const first = await start("cliente-a");
    const second = await start("cliente-b");
    assert.notEqual(first.dataRoot, second.dataRoot);
    assert.notEqual(first.database, second.database);
    assert.ok(
      fs.existsSync(
        path.join(first.dataRoot, "credential-key.bin.fallback"),
      ),
    );
    assert.ok(
      fs.existsSync(
        path.join(second.dataRoot, "credential-key.bin.fallback"),
      ),
    );
    assert.notEqual(first.port, second.port);
    await first.stop();
    await second.stop();
    await new Promise((resolve) => setTimeout(resolve, 30));
  } finally {
    if (previousWeb === undefined) delete process.env.DESKTOP_WEB_ROOT;
    else process.env.DESKTOP_WEB_ROOT = previousWeb;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("standalone supervisor restarts the sidecar once", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-restart-"));
  const webRoot = path.join(root, "web");
  fs.mkdirSync(webRoot, { recursive: true });
  fs.writeFileSync(path.join(webRoot, "index.html"), "<html></html>");
  const userData = path.join(root, "user-data");
  const previousWeb = process.env.DESKTOP_WEB_ROOT;
  process.env.DESKTOP_WEB_ROOT = webRoot;
  const children = [];

  class FakeChild extends EventEmitter {
    constructor() {
      super();
      this.stdout = new PassThrough();
      this.stderr = new PassThrough();
      this.exitCode = null;
      this.signalCode = null;
    }
    kill() {
      this.exitCode = 0;
      this.emit("exit", 0, null);
    }
  }

  try {
    const handle = await startStandalone({
      enabled: true,
      app: {
        isPackaged: false,
        getPath: () => userData,
        getVersion: () => "0.3.15",
      },
      safeStorage: { isEncryptionAvailable: () => false },
      allowUnprotectedCredentialKey: true,
      logger: { log() {}, warn() {}, error() {} },
      spawnProcess: () => {
        const child = new FakeChild();
        children.push(child);
        return child;
      },
      healthCheck: async () => true,
    });

    children[0].exitCode = 1;
    children[0].emit("exit", 1, null);
    await new Promise((resolve) => setTimeout(resolve, 700));
    assert.equal(children.length, 2);

    children[1].exitCode = 1;
    children[1].emit("exit", 1, null);
    await new Promise((resolve) => setTimeout(resolve, 700));
    assert.equal(children.length, 2);
    await handle.stop();
  } finally {
    if (previousWeb === undefined) delete process.env.DESKTOP_WEB_ROOT;
    else process.env.DESKTOP_WEB_ROOT = previousWeb;
    fs.rmSync(root, { recursive: true, force: true });
  }
});
