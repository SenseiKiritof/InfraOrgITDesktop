const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { Outbox } = require("../../shell/outbox");

const instance = "https://infra.example.com";

function fakeSafeStorage(available = true) {
  return {
    isEncryptionAvailable: () => available,
    encryptString: (value) =>
      Buffer.from(Buffer.from(`enc:${value}`, "utf8").toString("base64")),
    decryptString: (value) =>
      Buffer.from(String(value), "base64").toString("utf8").replace(/^enc:/, ""),
    getSelectedStorageBackend: () => (available ? "keychain" : "basic_text"),
  };
}

function directory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-outbox-"));
}

test("outbox persists a hash-chained queue and removes synced events", async () => {
  const dir = directory();
  const file = path.join(dir, "outbox.bin");
  const outbox = await new Outbox({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();

  const first = outbox.append({
    instanceUrl: instance,
    username: "admin",
    kind: "check.tcp",
    targetHost: "127.0.0.1",
    targetPort: 443,
    durationMs: 12,
  });
  const second = outbox.append({
    instanceUrl: instance,
    username: "admin",
    kind: "tunnel.start",
    serverId: 3,
  });
  assert.ok(first.hash);
  assert.equal(second.prevHash, first.hash);
  assert.equal(outbox.verify(), true);

  const reloaded = await new Outbox({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();
  assert.equal(reloaded.count(instance), 2);
  assert.equal(reloaded.verify(), true);
  assert.equal(reloaded.remove([first.eventId]), 1);
  assert.equal(reloaded.count(instance), 1);

  reloaded.fail([second.eventId], "offline");
  assert.equal(reloaded.list(instance)[0].attempts, 1);
  reloaded.removeInstance(instance);
  assert.equal(reloaded.count(), 0);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("outbox detects tampering and works without encryption", async () => {
  const dir = directory();
  const file = path.join(dir, "outbox.bin");
  const outbox = await new Outbox({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();
  outbox.append({ instanceUrl: instance, kind: "check.tcp" });
  outbox.events[0].targetHost = "evil.example.com";
  assert.equal(outbox.verify(), false);

  const memoryOnly = await new Outbox({
    file: path.join(dir, "memory.bin"),
    safeStorage: fakeSafeStorage(false),
    logger: { warn: () => {} },
  }).initialize();
  memoryOnly.append({ instanceUrl: instance, kind: "check.http" });
  assert.equal(memoryOnly.count(instance), 1);
  assert.equal(fs.existsSync(path.join(dir, "memory.bin")), false);
  fs.rmSync(dir, { recursive: true, force: true });
});
