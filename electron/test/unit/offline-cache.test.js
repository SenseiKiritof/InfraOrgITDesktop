const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { OfflineCache } = require("../../shell/offline-cache");

const instance = "https://infra.example.com";

function fakeSafeStorage({ available = true } = {}) {
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
  return fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-cache-"));
}

test("offline cache persists targets and policy encrypted", async () => {
  const dir = directory();
  const file = path.join(dir, "offline.bin");
  const cache = await new OfflineCache({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();

  assert.equal(
    cache.set(instance, {
      targets: [{ serverId: 1, serverName: "srv", targets: [], services: [] }],
      policy: "Client",
      username: "admin",
      serverCount: 1,
    }),
    true,
  );
  const stored = fs.readFileSync(file, "utf8");
  assert.doesNotMatch(stored, /srv/);

  const reloaded = await new OfflineCache({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();
  const entry = reloaded.get(instance);
  assert.equal(entry.policy, "Client");
  assert.equal(entry.username, "admin");
  assert.equal(entry.targets[0].serverName, "srv");
  assert.equal(entry.serverCount, 1);

  reloaded.delete(instance);
  assert.equal(reloaded.get(instance), null);

  fs.rmSync(dir, { recursive: true, force: true });
});

test("offline cache rejects invalid entries and survives no encryption", async () => {
  const dir = directory();
  const cache = await new OfflineCache({
    file: path.join(dir, "offline.bin"),
    safeStorage: fakeSafeStorage({ available: false }),
    logger: { warn: () => {} },
  }).initialize();
  assert.equal(cache.set(instance, { policy: "Nope" }), false);
  assert.equal(
    cache.set(instance, { targets: [], policy: "Auto", cachedAt: "nope" }),
    true,
  );
  assert.equal(cache.get(instance).policy, "Auto");
  assert.equal(
    fs.existsSync(path.join(dir, "offline.bin")),
    false,
  );
  fs.rmSync(dir, { recursive: true, force: true });
});
