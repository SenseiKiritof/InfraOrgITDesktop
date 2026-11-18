const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const VERSION = 1;

function fingerprintOf(publicKeyPem) {
  const key = crypto.createPublicKey(publicKeyPem);
  const der = key.export({ type: "spki", format: "der" });
  return crypto.createHash("sha256").update(der).digest("base64").replace(/=+$/, "");
}

/**
 * Per-installation RSA key pair. The private key is encrypted with
 * safeStorage and never leaves the device; the public key is registered with
 * each InfraOrgIT instance to encrypt downloaded credentials for this device.
 */
class DeviceIdentity {
  constructor({ file, safeStorage, name, logger = console }) {
    if (!file) throw new TypeError("DeviceIdentity richiede un file.");
    this.file = file;
    this.safeStorage = safeStorage;
    this.name = name || "Desktop";
    this.logger = logger;
    this.privateKeyPem = "";
    this.publicKeyPem = "";
    this.fingerprint = "";
    this.persistenceDisabled = null;
  }

  async initialize() {
    let available = false;
    try {
      if (
        this.safeStorage &&
        typeof this.safeStorage.isAsyncEncryptionAvailable === "function"
      ) {
        available = await this.safeStorage.isAsyncEncryptionAvailable();
      } else if (this.safeStorage) {
        available = Boolean(this.safeStorage.isEncryptionAvailable());
      }
      const backend =
        typeof this.safeStorage?.getSelectedStorageBackend === "function"
          ? this.safeStorage.getSelectedStorageBackend()
          : "";
      available = available && backend !== "basic_text";
    } catch {
      available = false;
    }
    this.persistenceDisabled = !available;
    if (this.persistenceDisabled) {
      this.logger.warn(
        "Archivio protetto non disponibile: la chiave dispositivo resta in memoria.",
      );
    }
    if (!this.load()) this.generate();
    return this;
  }

  load() {
    if (this.persistenceDisabled) return false;
    let encrypted;
    try {
      encrypted = fs.readFileSync(this.file);
    } catch (error) {
      if (error.code !== "ENOENT")
        this.logger.warn("Lettura chiave dispositivo non riuscita.");
      return false;
    }
    try {
      const payload = JSON.parse(this.safeStorage.decryptString(encrypted));
      if (
        payload?.version !== VERSION ||
        typeof payload.privateKeyPem !== "string" ||
        typeof payload.publicKeyPem !== "string"
      )
        return false;
      this.setKeys(payload.publicKeyPem, payload.privateKeyPem);
      return Boolean(this.publicKeyPem && this.privateKeyPem);
    } catch {
      this.logger.warn("Chiave dispositivo illeggibile: ne verrà generata una nuova.");
      return false;
    }
  }

  generate() {
    const { publicKey, privateKey } = crypto.generateKeyPairSync("rsa", {
      modulusLength: 3072,
      publicKeyEncoding: { type: "spki", format: "pem" },
      privateKeyEncoding: { type: "pkcs8", format: "pem" },
    });
    this.setKeys(publicKey, privateKey);
    this.persist();
  }

  reset() {
    this.generate();
    return this;
  }

  setKeys(publicKeyPem, privateKeyPem) {
    this.publicKeyPem = publicKeyPem;
    this.privateKeyPem = privateKeyPem;
    this.fingerprint = fingerprintOf(publicKeyPem);
  }

  persist() {
    if (this.persistenceDisabled) return false;
    try {
      const plaintext = JSON.stringify({
        version: VERSION,
        publicKeyPem: this.publicKeyPem,
        privateKeyPem: this.privateKeyPem,
      });
      const encrypted = this.safeStorage.encryptString(plaintext);
      fs.mkdirSync(path.dirname(this.file), { recursive: true, mode: 0o700 });
      const temporary = `${this.file}.${process.pid}.tmp`;
      fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
      fs.chmodSync(temporary, 0o600);
      fs.renameSync(temporary, this.file);
      return true;
    } catch {
      this.logger.warn("Salvataggio chiave dispositivo non riuscito.");
      return false;
    }
  }

  /** Decrypts a DeviceSecretDto payload from the API. */
  decrypt(payload) {
    if (!payload || typeof payload !== "object")
      throw new Error("Payload credenziale non valido.");
    const key = crypto.privateDecrypt(
      {
        key: this.privateKeyPem,
        oaepHash: "sha256",
      },
      Buffer.from(String(payload.wrappedKey || ""), "base64"),
    );
    const decipher = crypto.createDecipheriv(
      "aes-256-gcm",
      key,
      Buffer.from(String(payload.nonce || ""), "base64"),
    );
    decipher.setAAD(Buffer.from(String(payload.aad || ""), "utf8"));
    decipher.setAuthTag(Buffer.from(String(payload.tag || ""), "base64"));
    const plaintext = Buffer.concat([
      decipher.update(Buffer.from(String(payload.ciphertext || ""), "base64")),
      decipher.final(),
    ]);
    return JSON.parse(plaintext.toString("utf8"));
  }
}

module.exports = { DeviceIdentity, fingerprintOf };
