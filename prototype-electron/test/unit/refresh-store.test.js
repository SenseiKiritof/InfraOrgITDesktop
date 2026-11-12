const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { RefreshTokenStore } = require("../../auth/refresh-store");

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-auth-"));
}

function fakeSafeStorage(backend = "keychain") {
  return {
    isEncryptionAvailable: () => true,
    getSelectedStorageBackend: () => backend,
    encryptString: (value) =>
      Buffer.from(`enc:${Buffer.from(value, "utf8").toString("base64")}`),
    decryptString: (buffer) => {
      const value = buffer.toString("utf8");
      if (!value.startsWith("enc:")) throw new Error("bad ciphertext");
      return Buffer.from(value.slice(4), "base64").toString("utf8");
    },
  };
}

const logger = { warn: () => {} };

test("refresh token store persists only encrypted refresh tokens", () => {
  const directory = tempDir();
  const file = path.join(directory, "desktop-auth.bin");
  const safeStorage = fakeSafeStorage();
  const store = new RefreshTokenStore({ file, safeStorage, logger });
  assert.equal(store.set("https://infra.example.com", "refresh-1"), true);
  assert.equal(store.set("https://other.example.com", "refresh-2"), true);

  const raw = fs.readFileSync(file, "utf8");
  assert.ok(!raw.includes("refresh-1"));
  assert.ok(!raw.includes("refresh-2"));
  assert.equal(fs.statSync(file).mode & 0o777, 0o600);

  const reloaded = new RefreshTokenStore({ file, safeStorage, logger });
  assert.equal(reloaded.get("https://infra.example.com"), "refresh-1");
  assert.equal(reloaded.get("https://other.example.com"), "refresh-2");
  assert.equal(reloaded.get("https://missing.example.com"), null);

  reloaded.delete("https://infra.example.com");
  const afterDelete = new RefreshTokenStore({ file, safeStorage, logger });
  assert.equal(afterDelete.get("https://infra.example.com"), null);
  assert.equal(afterDelete.get("https://other.example.com"), "refresh-2");
  fs.rmSync(directory, { recursive: true, force: true });
});

test("refresh token store keeps the session in memory when the OS store is unavailable", () => {
  const directory = tempDir();
  const file = path.join(directory, "desktop-auth.bin");
  const store = new RefreshTokenStore({
    file,
    safeStorage: fakeSafeStorage("basic_text"),
    logger,
  });
  assert.equal(store.set("https://infra.example.com", "refresh-1"), true);
  assert.equal(store.get("https://infra.example.com"), "refresh-1");
  assert.equal(fs.existsSync(file), false);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("refresh token store survives a damaged archive without crashing", () => {
  const directory = tempDir();
  const file = path.join(directory, "desktop-auth.bin");
  fs.writeFileSync(file, "not encrypted");
  const store = new RefreshTokenStore({
    file,
    safeStorage: fakeSafeStorage(),
    logger,
  });
  assert.equal(store.get("https://infra.example.com"), null);
  assert.equal(store.set("https://infra.example.com", "refresh-1"), true);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("refresh token store ignores invalid tokens and non-http keys", () => {
  const directory = tempDir();
  const file = path.join(directory, "desktop-auth.bin");
  const safeStorage = fakeSafeStorage();
  const payload = JSON.stringify({
    version: 1,
    tokens: {
      "file:///tmp/evil": "refresh-evil",
      "https://infra.example.com": "",
      "https://valid.example.com": "refresh-valid",
    },
  });
  fs.writeFileSync(file, safeStorage.encryptString(payload));
  const store = new RefreshTokenStore({ file, safeStorage, logger });
  assert.equal(store.get("file:///tmp/evil"), null);
  assert.equal(store.get("https://infra.example.com"), null);
  assert.equal(store.get("https://valid.example.com"), "refresh-valid");
  fs.rmSync(directory, { recursive: true, force: true });
});
