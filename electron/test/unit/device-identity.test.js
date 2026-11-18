const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { DeviceIdentity } = require("../../auth/device-identity");

function fakeSafeStorage() {
  return {
    isEncryptionAvailable: () => true,
    encryptString: (value) =>
      Buffer.from(Buffer.from(`enc:${value}`, "utf8").toString("base64")),
    decryptString: (value) =>
      Buffer.from(String(value), "base64").toString("utf8").replace(/^enc:/, ""),
    getSelectedStorageBackend: () => "keychain",
  };
}

function sealed(publicPem, value, aad) {
  const key = crypto.randomBytes(32);
  const nonce = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv("aes-256-gcm", key, nonce);
  cipher.setAAD(Buffer.from(aad));
  const ciphertext = Buffer.concat([
    cipher.update(JSON.stringify(value), "utf8"),
    cipher.final(),
  ]);
  return {
    aad,
    wrappedKey: crypto
      .publicEncrypt(
        { key: publicPem, oaepHash: "sha256" },
        key,
      )
      .toString("base64"),
    nonce: nonce.toString("base64"),
    ciphertext: ciphertext.toString("base64"),
    tag: cipher.getAuthTag().toString("base64"),
  };
}

test("device identity persists and decrypts a device-sealed payload", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-device-"));
  const file = path.join(dir, "device.bin");
  const identity = await new DeviceIdentity({
    file,
    safeStorage: fakeSafeStorage(),
    name: "test",
    logger: { warn: () => {} },
  }).initialize();

  assert.match(identity.publicKeyPem, /BEGIN PUBLIC KEY/);
  assert.ok(identity.fingerprint.length > 20);
  const payload = sealed(
    identity.publicKeyPem,
    { name: "cred", secret: "s3cret" },
    "aad-1",
  );
  assert.deepEqual(identity.decrypt(payload), {
    name: "cred",
    secret: "s3cret",
  });

  const reloaded = await new DeviceIdentity({
    file,
    safeStorage: fakeSafeStorage(),
    name: "test",
    logger: { warn: () => {} },
  }).initialize();
  assert.equal(reloaded.fingerprint, identity.fingerprint);
  assert.deepEqual(reloaded.decrypt(payload), {
    name: "cred",
    secret: "s3cret",
  });
  fs.rmSync(dir, { recursive: true, force: true });
});
