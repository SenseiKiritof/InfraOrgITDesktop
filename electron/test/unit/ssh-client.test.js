const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");

const {
  connectTarget,
  fingerprintOf,
  listDirectory,
  normalizeFingerprint,
  openShell,
  remoteJoin,
  runCommand,
  verifyHostKey,
} = require("../../ssh-client");

const key = Buffer.from("host-key-bytes");
const fingerprint = fingerprintOf(key);

test("host key fingerprints accept the OpenSSH and SSH.NET forms", () => {
  assert.equal(normalizeFingerprint(`SHA256:${fingerprint}`), fingerprint);
  assert.equal(normalizeFingerprint(`${fingerprint}===`), fingerprint);
  assert.equal(verifyHostKey(`SHA256:${fingerprint}`, key), true);
  assert.equal(verifyHostKey(fingerprint, key), true);
  assert.equal(verifyHostKey("SHA256:wrong", key), false);
  assert.equal(verifyHostKey(null, key), true);
});

test("jump chain is connected nearest-first through forwardOut", async () => {
  const forwarded = [];
  class FakeClient {
    constructor() {
      this.handlers = {};
    }
    once(name, handler) {
      this.handlers[name] = handler;
      return this;
    }
    connect(options) {
      this.options = options;
      queueMicrotask(() => this.handlers.ready?.());
    }
    forwardOut(source, sourcePort, host, port, callback) {
      forwarded.push({ host, port });
      callback(null, { host, port });
    }
    end() {}
  }

  const target = {
    host: "10.0.0.10",
    port: 22,
    username: "deploy",
    hostKeyFingerprint: null,
    jumps: [
      { host: "10.0.0.5", port: 2222, username: "bastion" },
      { host: "10.0.0.6", port: 22, username: "second" },
    ],
  };
  const connection = await connectTarget(target, FakeClient);
  assert.ok(connection.client);
  assert.deepEqual(forwarded, [
    { host: "10.0.0.6", port: 22 },
    { host: "10.0.0.10", port: 22 },
  ]);
  connection.dispose();
});

test("sftp listing normalizes paths, directories and timestamps", async () => {
  assert.equal(remoteJoin("/", "etc"), "/etc");
  assert.equal(remoteJoin("/var/", "log"), "/var/log");
  const sftp = {
    realpath(_path, callback) {
      callback(null, "/home/deploy");
    },
    readdir(path, callback) {
      assert.equal(path, "/home/deploy");
      callback(null, [
        { filename: "app.log", attrs: { size: 12, mtime: 1_700_000_000, isDirectory: () => false } },
        { filename: "logs", attrs: { mode: 0o040755, size: 0, mtime: null } },
        { filename: ".", attrs: {} },
      ]);
    },
  };
  const result = await listDirectory(sftp, null);
  assert.equal(result.path, "/home/deploy");
  assert.deepEqual(result.items.map((item) => item.name), ["logs", "app.log"]);
  assert.equal(result.items[0].isDirectory, true);
  assert.equal(result.items[1].size, 12);
  assert.match(result.items[1].modifiedUtc, /^2023-/);
});

test("command execution returns combined output and the exit code", async () => {
  class FakeClient {
    constructor() {
      this.handlers = {};
    }
    once(name, handler) {
      this.handlers[name] = handler;
      return this;
    }
    connect() {
      queueMicrotask(() => this.handlers.ready?.());
    }
    exec(script, callback) {
      this.script = script;
      const stream = new EventEmitter();
      stream.stderr = new EventEmitter();
      stream.signal = () => {};
      callback(null, stream);
      queueMicrotask(() => {
        stream.emit("data", Buffer.from("standard "));
        stream.stderr.emit("data", Buffer.from("error"));
        stream.emit("close", 0);
      });
    }
    end() {}
  }

  const result = await runCommand(
    { host: "10.0.0.10", port: 22, username: "deploy", jumps: [] },
    "echo hello",
    {},
    FakeClient,
  );
  assert.equal(result.exitCode, 0);
  assert.equal(result.output, "standard error");
  assert.equal(result.timedOut, false);
});

test("shell uses the requested terminal size and forwards resize", async () => {
  const resized = [];
  const closed = [];
  class FakeClient {
    constructor() {
      this.handlers = {};
    }
    once(name, handler) {
      this.handlers[name] = handler;
      return this;
    }
    connect() {
      queueMicrotask(() => this.handlers.ready?.());
    }
    shell(options, callback) {
      this.shellOptions = options;
      callback(null, {
        setWindow(rows, cols) {
          resized.push({ rows, cols });
        },
        end() {
          closed.push(true);
        },
      });
    }
    end() {}
  }

  const shell = await openShell(
    {
      host: "10.0.0.10",
      port: 22,
      username: "deploy",
      jumps: [],
    },
    { cols: 120, rows: 40 },
    FakeClient,
  );
  assert.equal(shell.stream !== undefined, true);
  shell.resize(132, 43);
  assert.deepEqual(resized, [{ rows: 43, cols: 132 }]);
  shell.close();
  assert.deepEqual(closed, [true]);
});
