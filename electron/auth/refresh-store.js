const fs = require("fs");
const path = require("path");

const VERSION = 1;

function normalizeToken(value) {
  if (typeof value !== "string") return null;
  const token = value.trim();
  if (!token || token.length > 8192) return null;
  return token;
}

/**
 * Stores refresh tokens encrypted with Electron safeStorage. Access tokens
 * never reach this class. A new file contains only instance URL -> refresh
 * token; no host, user or token metadata is written in clear text.
 *
 * Native keychain/Credential Manager/Secret Service access is the documented
 * next hardening step; safeStorage already uses the OS key store on macOS and
 * Windows and refuses plaintext on Linux when no keyring is available.
 */
class RefreshTokenStore {
  constructor({ file, safeStorage, logger = console }) {
    if (!file) throw new TypeError("RefreshTokenStore richiede un file.");
    this.file = file;
    this.safeStorage = safeStorage;
    this.logger = logger;
    this.memory = new Map();
    this.loaded = false;
    this.persistenceDisabled = null;
  }

  /**
   * Electron 45 deprecates the synchronous availability check. The app awaits
   * this once before the first load; unit tests use the synchronous fallback.
   */
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
        "Archivio protetto non disponibile: la sessione desktop resta in memoria.",
      );
    }
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
    if (this.loaded) return this.memory;
    this.loaded = true;
    const available = this.encryptionAvailable();
    this.persistenceDisabled = !available;
    if (!available) {
      if (this.persistenceDisabled)
        this.logger.warn(
          "Archivio protetto non disponibile: la sessione desktop resta in memoria.",
        );
      return this.memory;
    }

    let encrypted;
    try {
      encrypted = fs.readFileSync(this.file);
    } catch (error) {
      if (error.code !== "ENOENT")
        this.logger.warn("Lettura dei token desktop non riuscita.");
      return this.memory;
    }

    try {
      const plaintext = this.safeStorage.decryptString(encrypted);
      const payload = JSON.parse(plaintext);
      if (payload?.version !== VERSION || !payload.tokens || typeof payload.tokens !== "object") {
        this.logger.warn("Archivio token desktop non riconosciuto.");
        return this.memory;
      }
      for (const [url, token] of Object.entries(payload.tokens)) {
        const valid = normalizeToken(token);
        if (valid && /^https?:\/\//i.test(url)) this.memory.set(url, valid);
      }
    } catch {
      this.logger.warn(
        "Archivio token desktop illeggibile: accedi di nuovo dalla pagina di login.",
      );
    }
    return this.memory;
  }

  get(instanceUrl) {
    this.load();
    return this.memory.get(instanceUrl) || null;
  }

  set(instanceUrl, refreshToken) {
    const token = normalizeToken(refreshToken);
    if (!token) return false;
    this.load();
    this.memory.set(instanceUrl, token);
    this.persist();
    return true;
  }

  delete(instanceUrl) {
    this.load();
    const existed = this.memory.delete(instanceUrl);
    if (existed) this.persist();
    return existed;
  }

  clear() {
    this.load();
    this.memory.clear();
    this.persist();
  }

  persist() {
    if (this.persistenceDisabled) return false;
    this.loaded = true;
    try {
      const plaintext = JSON.stringify({
        version: VERSION,
        tokens: Object.fromEntries(this.memory),
      });
      const encrypted = this.safeStorage.encryptString(plaintext);
      const directory = path.dirname(this.file);
      fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
      const temporary = `${this.file}.${process.pid}.tmp`;
      fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
      fs.chmodSync(temporary, 0o600);
      fs.renameSync(temporary, this.file);
      return true;
    } catch {
      this.logger.warn("Salvataggio della sessione desktop non riuscito.");
      return false;
    }
  }
}

module.exports = { RefreshTokenStore, VERSION };
