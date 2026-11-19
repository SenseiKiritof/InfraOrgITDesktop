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
