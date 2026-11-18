const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { CredentialVault } = require("../../shell/credential-vault");

const instance = "https://infra.example.com";
const payload = { aad: "a", wrappedKey: "b", nonce: "c", ciphertext: "d", tag: "e" };

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

test("credential vault persists only AllowOffline entries", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-vault-"));
  const file = path.join(dir, "vault.bin");
  const vault = await new CredentialVault({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();

  vault.put({
    instanceUrl: instance,
    credentialId: 1,
    name: "offline",
    username: "deploy",
    authType: "Password",
    owner: "personal",
    allowOffline: true,
    payload,
  });
  vault.put({
    instanceUrl: instance,
    credentialId: 1,
    name: "offline",
    username: "deploy",
    authType: "Password",
    owner: "personal",
    allowOffline: false,
    payload,
  });
  vault.put({
    instanceUrl: instance,
    credentialId: 2,
    name: "memory",
    username: "deploy",
    authType: "Password",
    owner: "shared",
    allowOffline: false,
    payload,
  });
  assert.equal(vault.list(instance).length, 2);

  const reloaded = await new CredentialVault({
    file,
    safeStorage: fakeSafeStorage(),
    logger: { warn: () => {} },
  }).initialize();
  assert.equal(reloaded.list(instance).length, 0);
  assert.equal(vault.list(instance).length, 2);
  assert.equal(vault.get(instance, 1).payload.ciphertext, "d");
  assert.equal(vault.remove(instance, 1), true);
  assert.equal(vault.list(instance).length, 1);
  fs.rmSync(dir, { recursive: true, force: true });
});
