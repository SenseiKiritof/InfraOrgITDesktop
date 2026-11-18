const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const VERSION = 1;
const MAX_EVENTS = 1000;
const MAX_BYTES = 2 * 1024 * 1024;
const MAX_DETAIL = 2000;

function normalizeText(value, maxLength) {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text ? text.slice(0, maxLength) : null;
}

function normalizePort(value) {
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= 65_535
    ? number
    : null;
}

function canonical(entry) {
  return [
    entry.prevHash || "",
    entry.eventId,
    entry.instanceUrl,
    entry.username || "",
    entry.kind,
    entry.occurredAt,
    entry.outcome,
    entry.serverId ?? "",
    entry.targetHost ?? "",
    entry.targetPort ?? "",
    entry.localPort ?? "",
    entry.durationMs ?? 0,
    entry.errorCode ?? "",
    entry.detail ?? "",
  ].join("|");
}

function hashEntry(entry) {
  return crypto.createHash("sha256").update(canonical(entry)).digest("hex");
}

function normalize(value) {
  if (!value || typeof value !== "object") return null;
  const eventId = normalizeText(value.eventId, 100);
  const instanceUrl = normalizeText(value.instanceUrl, 500);
  const kind = normalizeText(value.kind, 50);
  const outcome = value.outcome === "Error" ? "Error" : "Ok";
  const occurredAt =
    typeof value.occurredAt === "string" &&
    !Number.isNaN(Date.parse(value.occurredAt))
      ? value.occurredAt
      : new Date().toISOString();
  if (!eventId || !instanceUrl || !kind) return null;
  return {
    eventId,
    instanceUrl,
    username: normalizeText(value.username, 100),
    kind,
    occurredAt,
    outcome,
    serverId: Number.isInteger(value.serverId) ? value.serverId : null,
    targetHost: normalizeText(value.targetHost, 255),
    targetPort: normalizePort(value.targetPort),
    localPort: normalizePort(value.localPort),
    durationMs: Math.max(0, Math.min(86_400_000, Number(value.durationMs) || 0)),
    errorCode: normalizeText(value.errorCode, 100),
    detail: normalizeText(value.detail, MAX_DETAIL),
    attempts: Math.max(0, Math.min(100, Number(value.attempts) || 0)),
    lastError: normalizeText(value.lastError, 300),
    prevHash: typeof value.prevHash === "string" ? value.prevHash : "",
    hash: typeof value.hash === "string" ? value.hash : "",
  };
}

/**
 * Encrypted append-only outbox for events observed while the instance was
 * unreachable. Each entry extends a local SHA-256 chain; synced entries are
 * removed only after the API returns their event ids as accepted or duplicate.
 */
class Outbox {
  constructor({ file, safeStorage, logger = console }) {
    if (!file) throw new TypeError("Outbox richiede un file.");
    this.file = file;
    this.safeStorage = safeStorage;
    this.logger = logger;
    this.events = [];
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
        "Archivio protetto non disponibile: la coda eventi resta in memoria.",
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
    if (this.loaded) return this.events;
    this.loaded = true;
    this.persistenceDisabled = !this.encryptionAvailable();
    if (this.persistenceDisabled) return this.events;

    let encrypted;
    try {
      encrypted = fs.readFileSync(this.file);
    } catch (error) {
      if (error.code !== "ENOENT")
        this.logger.warn("Lettura della coda eventi non riuscita.");
      return this.events;
    }

    try {
      const payload = JSON.parse(this.safeStorage.decryptString(encrypted));
      if (payload?.version !== VERSION || !Array.isArray(payload.events)) {
        this.logger.warn("Coda eventi non riconosciuta.");
        return this.events;
      }
      const events = payload.events.map(normalize).filter(Boolean);
      this.events = events;
      if (!this.verify()) {
        this.logger.warn("Coda eventi locale non integra: hash non valido.");
      }
    } catch {
      this.logger.warn("Coda eventi illeggibile: verrà ricreata.");
    }
    return this.events;
  }

  verify() {
    // Entries are removed from the front after sync, so the first remaining
    // hash is the anchor of the stored chain.
    let previous = this.events[0]?.prevHash || "";
    for (const entry of this.events) {
      if (entry.prevHash !== previous) return false;
      const expected = hashEntry(entry);
      if (entry.hash !== expected) return false;
      previous = entry.hash;
    }
    return true;
  }

  append(value) {
    this.load();
    if (this.events.length >= MAX_EVENTS) {
      this.logger.warn("Coda eventi piena: evento non accodato.");
      return null;
    }
    const previous = this.events.at(-1)?.hash || "";
    const entry = normalize({
      ...value,
      eventId: value.eventId || crypto.randomUUID(),
      prevHash: previous,
      hash: "",
    });
    if (!entry) throw new Error("Evento non valido.");
    entry.hash = hashEntry(entry);
    if (JSON.stringify(entry).length > MAX_DETAIL * 3) {
      throw new Error("Evento troppo grande.");
    }
    this.events.push(entry);
    this.persist();
    return entry;
  }

  list(instanceUrl = null) {
    this.load();
    return instanceUrl
      ? this.events.filter((entry) => entry.instanceUrl === instanceUrl)
      : [...this.events];
  }

  count(instanceUrl = null) {
    return this.list(instanceUrl).length;
  }

  remove(eventIds) {
    const drop = new Set(eventIds || []);
    if (drop.size === 0) return 0;
    this.load();
    const before = this.events.length;
    this.events = this.events.filter((entry) => !drop.has(entry.eventId));
    const removed = before - this.events.length;
    if (removed > 0) this.persist();
    return removed;
  }

  removeInstance(instanceUrl) {
    this.load();
    const before = this.events.length;
    this.events = this.events.filter((entry) => entry.instanceUrl !== instanceUrl);
    if (this.events.length !== before) this.persist();
  }

  fail(eventIds, message) {
    const ids = new Set(eventIds || []);
    let changed = false;
    for (const entry of this.events) {
      if (!ids.has(entry.eventId)) continue;
      entry.attempts += 1;
      entry.lastError = String(message || "").slice(0, 300);
      changed = true;
    }
    if (changed) this.persist();
  }

  clear() {
    this.load();
    this.events = [];
    this.persist();
  }

  persist() {
    if (this.persistenceDisabled) return false;
    this.loaded = true;
    try {
      const plaintext = JSON.stringify({
        version: VERSION,
        events: this.events,
      });
      if (plaintext.length > MAX_BYTES) {
        this.logger.warn("Coda eventi troppo grande: scrittura ignorata.");
        return false;
      }
      const encrypted = this.safeStorage.encryptString(plaintext);
      fs.mkdirSync(path.dirname(this.file), { recursive: true, mode: 0o700 });
      const temporary = `${this.file}.${process.pid}.tmp`;
      fs.writeFileSync(temporary, encrypted, { mode: 0o600 });
      fs.chmodSync(temporary, 0o600);
      fs.renameSync(temporary, this.file);
      return true;
    } catch {
      this.logger.warn("Salvataggio della coda eventi non riuscito.");
      return false;
    }
  }
}

module.exports = { Outbox, VERSION };
