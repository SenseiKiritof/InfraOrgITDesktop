const fs = require("node:fs");
const path = require("node:path");

const VERSION = 1;

function keyOf(instanceUrl, credentialId) {
  return `${instanceUrl}\u001f${credentialId}`;
}

function normalize(value) {
  if (!value || typeof value !== "object") return null;
  const credentialId = Number(value.credentialId);
  const instanceUrl = String(value.instanceUrl || "").trim();
  const payload = value.payload;
  if (!Number.isInteger(credentialId) || credentialId <= 0) return null;
  if (!/^https?:\/\//i.test(instanceUrl)) return null;
  if (!payload || typeof payload !== "object") return null;
  return {
    credentialId,
    instanceUrl,
    name: String(value.name || "").slice(0, 200),
    username: String(value.username || "").slice(0, 200),
    authType: String(value.authType || "").slice(0, 20),
    owner: value.owner === "personal" ? "personal" : "shared",
    downloadedBy: String(value.downloadedBy || "").slice(0, 100),
    allowOffline: Boolean(value.allowOffline),
    version: Number.isInteger(value.version) ? value.version : 0,
    payload,
    downloadedAt: String(value.downloadedAt || new Date().toISOString()),
  };
}

/**
 * Device-encrypted credential payloads. Only entries with AllowOffline reach
 * the safeStorage file; the rest stay in memory for this run. The payload is
 * already encrypted to this device's public key, so a copied file is useless
 * without the private key in the OS store.
 */
class CredentialVault {
  constructor({ file, safeStorage, logger = console }) {
    if (!file) throw new TypeError("CredentialVault richiede un file.");
    this.file = file;
    this.safeStorage = safeStorage;
    this.logger = logger;
    this.entries = new Map();
    this.loaded = false;
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
        "Archivio protetto non disponibile: le credenziali offline restano in memoria.",
      );
    }
    this.load();
    return this;
  }

  encryptionAvailable() {
    if (this.persistenceDisabled !== null) return !this.persistenceDisabled;
    try {
      if (!this.safeStorage || !this.safeStorage.isEncryptionAvailable())
        return false;
      const backend =
        typeof this.safeStorage.getSelectedStorageBackend === "function"
          ? this.safeStorage.getSelectedStorageBackend()
          : "";
      return backend !== "basic_text";
    } catch {
      return false;
    }
  }

  load() {
    if (this.loaded) return this.entries;
    this.loaded = true;
    this.persistenceDisabled = !this.encryptionAvailable();
    if (this.persistenceDisabled) return this.entries;
    let encrypted;
    try {
      encrypted = fs.readFileSync(this.file);
    } catch (error) {
      if (error.code !== "ENOENT")
        this.logger.warn("Lettura delle credenziali offline non riuscita.");
      return this.entries;
    }
    try {
      const payload = JSON.parse(this.safeStorage.decryptString(encrypted));
      if (payload?.version !== VERSION || !Array.isArray(payload.entries))
        return this.entries;
      for (const raw of payload.entries) {
        const entry = normalize(raw);
        if (!entry || !entry.allowOffline) continue;
        this.entries.set(keyOf(entry.instanceUrl, entry.credentialId), entry);
      }
    } catch {
      this.logger.warn("Credenziali offline illeggibili: archivio ricreato.");
    }
    return this.entries;
  }

  put(entry) {
    const value = normalize(entry);
    if (!value) throw new Error("Credenziale non valida.");
    this.load();
    const key = keyOf(value.instanceUrl, value.credentialId);
    this.entries.set(key, value);
    // Always rewrite: a credential switched from AllowOffline to memory-only
    // must disappear from the persisted file too.
    this.persist();
    return value;
  }

  get(instanceUrl, credentialId) {
    this.load();
    return this.entries.get(keyOf(instanceUrl, Number(credentialId))) || null;
  }

  list(instanceUrl = null, downloadedBy = null) {
    this.load();
    return [...this.entries.values()]
      .filter((entry) => !instanceUrl || entry.instanceUrl === instanceUrl)
      .filter(
        (entry) => !downloadedBy || entry.downloadedBy === downloadedBy,
      )
      .map((entry) => ({
        credentialId: entry.credentialId,
        name: entry.name,
        username: entry.username,
        authType: entry.authType,
        owner: entry.owner,
        downloadedBy: entry.downloadedBy,
        allowOffline: entry.allowOffline,
        version: entry.version,
        downloadedAt: entry.downloadedAt,
      }));
  }

  remove(instanceUrl, credentialId) {
    this.load();
    const removed = this.entries.delete(
      keyOf(instanceUrl, Number(credentialId)),
    );
    if (removed) this.persist();
    return removed;
  }

  clearInstance(instanceUrl) {
    this.load();
    let changed = false;
    for (const [key, entry] of [...this.entries]) {
      if (entry.instanceUrl !== instanceUrl) continue;
      this.entries.delete(key);
      changed = true;
    }
    if (changed) this.persist();
  }

  clear() {
    this.load();
    this.entries.clear();
    this.persist();
  }

  persist() {
    if (this.persistenceDisabled) return false;
    this.loaded = true;
    try {
      const entries = [...this.entries.values()].filter(
        (entry) => entry.allowOffline,
      );
      const encrypted = this.safeStorage.encryptString(
        JSON.stringify({ version: VERSION, entries }),
      );
      fs.mkdirSync(path.dirname(this.file), { recursive: true, mode: 0o700 });
      const temporary = `${this.file}.${process.pid}.tmp`;
      fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
      fs.chmodSync(temporary, 0o600);
      fs.renameSync(temporary, this.file);
      return true;
    } catch {
      this.logger.warn("Salvataggio delle credenziali offline non riuscito.");
      return false;
    }
  }
}

module.exports = { CredentialVault, VERSION };
