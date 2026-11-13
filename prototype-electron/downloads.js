const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const MAX_FILE_NAME = 120;
const COMPLETED_TTL_MS = 60_000;

const INVALID_FILE_NAME = /[\u0000-\u001f<>:"/\\|?*]/g;

/** Nome file locale sicuro: niente separatori, caratteri di controllo o dot iniziali. */
function sanitizeFileName(value, fallback = "download") {
  const raw = String(value ?? "")
    .replace(INVALID_FILE_NAME, "_")
    .replace(/\s+/g, " ")
    .trim();
  if (!raw || /^\.+$/.test(raw)) return fallback;
  if (raw.length <= MAX_FILE_NAME) return raw;
  const extension = path.extname(raw).slice(0, 16);
  return `${raw.slice(0, MAX_FILE_NAME - extension.length)}${extension}`;
}

/** Primo nome libero nella cartella, senza sovrascrivere file esistenti. */
function uniqueFilePath(directory, fileName, exists = fs.existsSync) {
  const safe = sanitizeFileName(fileName);
  const extension = path.extname(safe);
  const base = path.basename(safe, extension);
  let candidate = path.join(directory, safe);
  for (let index = 1; exists(candidate) && index < 1000; index += 1)
    candidate = path.join(directory, `${base} (${index})${extension}`);
  return candidate;
}

/** Download nativi e blob condivisi: stato, progresso e scadenza dei completati. */
class DownloadTracker {
  constructor({ now = Date.now } = {}) {
    this.now = now;
    this.items = new Map();
  }

  add({ fileName, totalBytes = 0, instanceUrl = "" }) {
    const id = crypto.randomUUID();
    const entry = {
      id,
      fileName: sanitizeFileName(fileName),
      instanceUrl: String(instanceUrl ?? ""),
      state: "progressing",
      receivedBytes: 0,
      totalBytes: Math.max(0, Number(totalBytes) || 0),
      savePath: "",
      startedAt: this.now(),
      updatedAt: this.now(),
    };
    this.items.set(id, entry);
    return entry;
  }

  update(id, patch) {
    const entry = this.items.get(id);
    if (!entry) return null;
    Object.assign(entry, patch, { updatedAt: this.now() });
    return entry;
  }

  finish(id, state, patch = {}) {
    return this.update(id, { ...patch, state });
  }

  remove(id) {
    return this.items.delete(id);
  }

  list() {
    return [...this.items.values()];
  }

  /** Rimuove i download conclusi dopo la TTL; gli attivi restano. */
  prune(ttlMs = COMPLETED_TTL_MS) {
    const cutoff = this.now() - ttlMs;
    for (const entry of this.items.values()) {
      if (entry.state === "progressing") continue;
      if (entry.updatedAt <= cutoff) this.items.delete(entry.id);
    }
  }
}

module.exports = {
  DownloadTracker,
  MAX_FILE_NAME,
  sanitizeFileName,
  uniqueFilePath,
};
