const fs = require("node:fs");
const path = require("node:path");

const VERSION = 1;
const MAX_BYTES = 2 * 1024 * 1024;
const MAX_TARGETS = 1000;
const POLICIES = new Set(["Auto", "Server", "Client"]);

function clone(value) {
  return typeof structuredClone === "function"
    ? structuredClone(value)
    : JSON.parse(JSON.stringify(value));
}

function normalize(value) {
  if (!value || typeof value !== "object") return null;
  const targets = Array.isArray(value.targets)
    ? value.targets
        .filter((item) => item && typeof item === "object")
        .slice(0, MAX_TARGETS)
    : [];
  const policy = POLICIES.has(value.policy) ? value.policy : null;
  const cachedAt =
    typeof value.cachedAt === "string" &&
    !Number.isNaN(Date.parse(value.cachedAt))
      ? value.cachedAt
      : new Date().toISOString();
  const username =
    typeof value.username === "string" && value.username.trim()
      ? value.username.trim().slice(0, 100)
      : null;
  const serverCount = Number.isInteger(value.serverCount)
    ? value.serverCount
    : null;
  const sessionTunnelCount = Number.isInteger(value.sessionTunnelCount)
    ? value.sessionTunnelCount
    : null;
  if (
    targets.length === 0 &&
    policy === null &&
    serverCount === null &&
    sessionTunnelCount === null
  )
    return null;
  return {
    targets,
    policy,
    cachedAt,
    username,
    serverCount,
    sessionTunnelCount,
  };
}

/**
 * Last known tunnel inventory per instance, encrypted with Electron
 * safeStorage. It carries no secrets: hosts, ports, usernames, services and
 * the last connection policy only. Entries survive restarts with no TTL, so
 * an unreachable instance can still be used for local Client tunnels and
 * checks; they are removed on logout or instance removal.
 */
class OfflineCache {
  constructor({ file, safeStorage, logger = console }) {
    if (!file) throw new TypeError("OfflineCache richiede un file.");
    this.file = file;
    this.safeStorage = safeStorage;
    this.logger = logger;
    this.memory = new Map();
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
        "Archivio protetto non disponibile: la cache offline resta in memoria.",
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
    if (this.loaded) return this.memory;
    this.loaded = true;
    this.persistenceDisabled = !this.encryptionAvailable();
    if (this.persistenceDisabled) return this.memory;

    let encrypted;
    try {
      encrypted = fs.readFileSync(this.file);
    } catch (error) {
      if (error.code !== "ENOENT")
        this.logger.warn("Lettura della cache offline non riuscita.");
      return this.memory;
    }

    try {
      const plaintext = this.safeStorage.decryptString(encrypted);
      const payload = JSON.parse(plaintext);
      if (
        payload?.version !== VERSION ||
        !payload.entries ||
        typeof payload.entries !== "object"
      ) {
        this.logger.warn("Cache offline non riconosciuta.");
        return this.memory;
      }
      for (const [url, value] of Object.entries(payload.entries)) {
        const entry = normalize(value);
        if (entry && /^https?:\/\//i.test(url)) this.memory.set(url, entry);
      }
    } catch {
      this.logger.warn("Cache offline illeggibile: verrà ricreata.");
    }
    return this.memory;
  }

  get(instanceUrl) {
    this.load();
    const entry = this.memory.get(instanceUrl);
    return entry ? clone(entry) : null;
  }

  set(instanceUrl, value) {
    if (!/^https?:\/\//i.test(String(instanceUrl || ""))) return false;
    const entry = normalize(value);
    if (!entry) return false;
    if (JSON.stringify(entry).length > MAX_BYTES) {
      this.logger.warn("Cache offline troppo grande: scrittura ignorata.");
      return false;
    }
    this.load();
    this.memory.set(instanceUrl, entry);
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
        entries: Object.fromEntries(this.memory),
      });
      const encrypted = this.safeStorage.encryptString(plaintext);
      fs.mkdirSync(path.dirname(this.file), { recursive: true, mode: 0o700 });
      const temporary = `${this.file}.${process.pid}.tmp`;
      fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
      fs.chmodSync(temporary, 0o600);
      fs.renameSync(temporary, this.file);
      return true;
    } catch {
      this.logger.warn("Salvataggio della cache offline non riuscito.");
      return false;
    }
  }
}

module.exports = { OfflineCache, VERSION };
