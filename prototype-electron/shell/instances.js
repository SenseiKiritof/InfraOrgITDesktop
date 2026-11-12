const fs = require("fs");
const path = require("path");

const MAX_NAME = 80;
const MAX_ENVIRONMENT = 40;

function isLoopback(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

/**
 * Production instance rules: https for any host, plain http only for a
 * loopback development stack, no credentials, query or fragment. The URL is
 * the instance id, so it is normalized once and never edited in place.
 */
function normalizeInstanceUrl(value) {
  let url;
  try {
    url = new URL(String(value ?? "").trim());
  } catch {
    throw new Error("L'URL dell'istanza non è valido.");
  }
  if (!["http:", "https:"].includes(url.protocol))
    throw new Error("L'URL dell'istanza deve usare http o https.");
  if (url.protocol === "http:" && !isLoopback(url.hostname))
    throw new Error("Un'istanza remota deve usare https.");
  if (url.username || url.password)
    throw new Error("L'URL dell'istanza non può contenere credenziali.");
  if (url.search || url.hash)
    throw new Error("L'URL dell'istanza non può contenere query o frammenti.");
  if (url.pathname !== "/" && url.pathname !== "")
    throw new Error("L'URL dell'istanza deve puntare alla radice dell'installazione.");
  return url.toString().replace(/\/$/, "");
}

function defaultEnvironment(url) {
  try {
    return isLoopback(new URL(url).hostname) ? "Locale" : "Produzione";
  } catch {
    return "Produzione";
  }
}

function normalizeName(value) {
  const name = String(value ?? "").trim().replace(/\s+/g, " ");
  if (!name) throw new Error("Il nome dell'istanza è obbligatorio.");
  if (name.length > MAX_NAME)
    throw new Error(`Il nome può contenere al massimo ${MAX_NAME} caratteri.`);
  return name;
}

function normalizeEnvironment(value, url) {
  const environment = String(value ?? "").trim().replace(/\s+/g, " ");
  if (!environment) return defaultEnvironment(url);
  if (environment.length > MAX_ENVIRONMENT)
    throw new Error(`L'ambiente può contenere al massimo ${MAX_ENVIRONMENT} caratteri.`);
  return environment;
}

function normalizeDate(value) {
  if (typeof value !== "string" || !value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function normalizeInstance(value, { defaultOpen = false } = {}) {
  if (!value || typeof value !== "object") return null;
  try {
    const url = normalizeInstanceUrl(value.url);
    const name = normalizeName(value.name);
    return {
      url,
      name,
      environment: normalizeEnvironment(value.environment, url),
      openAtStartup:
        typeof value.openAtStartup === "boolean"
          ? value.openAtStartup
          : Boolean(defaultOpen),
      lastUsedAt: normalizeDate(value.lastUsedAt),
    };
  } catch {
    return null;
  }
}

function clone(instance) {
  return { ...instance };
}

class InstanceStore {
  constructor(file) {
    if (!file) throw new TypeError("InstanceStore richiede un file.");
    this.file = file;
    this.instances = null;
  }

  load() {
    if (this.instances) return this.snapshot();
    this.instances = [];
    let raw;
    try {
      raw = JSON.parse(fs.readFileSync(this.file, "utf8"));
    } catch (error) {
      if (error.code !== "ENOENT")
        console.warn("Elenco istanze illeggibile: riparto da un elenco vuoto.");
      return this.snapshot();
    }
    if (!Array.isArray(raw)) {
      console.warn("Formato istanze non riconosciuto: riparto da un elenco vuoto.");
      return this.snapshot();
    }

    const seen = new Set();
    raw.forEach((entry, index) => {
      // The prototype wrote {name,url}; the first entry opened at startup.
      const instance = normalizeInstance(entry, { defaultOpen: index === 0 });
      if (!instance || seen.has(instance.url)) return;
      seen.add(instance.url);
      this.instances.push(instance);
    });
    return this.snapshot();
  }

  snapshot() {
    return (this.instances || []).map(clone);
  }

  list() {
    this.load();
    return this.snapshot();
  }

  get(url) {
    this.load();
    const instance = (this.instances || []).find((item) => item.url === url);
    return instance ? clone(instance) : null;
  }

  save(items) {
    if (!Array.isArray(items)) throw new Error("Elenco istanze non valido.");
    this.instances = items.map(clone);
    this.persist();
    return this.list();
  }

  add(input) {
    this.load();
    const url = normalizeInstanceUrl(input?.url);
    const normalized = normalizeInstance(
      {
        name: input?.name,
        url,
        environment: input?.environment,
        openAtStartup: input?.openAtStartup,
      },
      { defaultOpen: this.instances.length === 0 },
    );
    if (!normalized) throw new Error("Nome e URL dell'istanza non sono validi.");
    if (this.instances.some((item) => item.url === normalized.url))
      throw new Error("Questa istanza è già presente.");
    this.instances.push(normalized);
    this.persist();
    return this.list();
  }

  update(url, input) {
    this.load();
    const index = this.instances.findIndex((item) => item.url === url);
    if (index < 0) throw new Error("Istanza non trovata.");
    const current = this.instances[index];
    const updated = {
      ...current,
      name: input?.name === undefined ? current.name : normalizeName(input.name),
      environment:
        input?.environment === undefined
          ? current.environment
          : normalizeEnvironment(input.environment, current.url),
      openAtStartup:
        input?.openAtStartup === undefined
          ? current.openAtStartup
          : Boolean(input.openAtStartup),
    };
    this.instances[index] = updated;
    this.persist();
    return clone(updated);
  }

  remove(url) {
    this.load();
    const before = this.instances.length;
    this.instances = this.instances.filter((item) => item.url !== url);
    const removed = this.instances.length !== before;
    if (removed) this.persist();
    return removed;
  }

  touch(url, date = new Date()) {
    this.load();
    const instance = this.instances.find((item) => item.url === url);
    if (!instance) return null;
    instance.lastUsedAt = date.toISOString();
    this.persist();
    return clone(instance);
  }

  persist() {
    const directory = path.dirname(this.file);
    fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
    const temporary = `${this.file}.${process.pid}.tmp`;
    fs.writeFileSync(temporary, JSON.stringify(this.instances, null, 2), {
      mode: 0o600,
    });
    fs.chmodSync(temporary, 0o600);
    fs.renameSync(temporary, this.file);
  }
}

module.exports = {
  InstanceStore,
  defaultEnvironment,
  normalizeEnvironment,
  normalizeInstance,
  normalizeInstanceUrl,
  normalizeName,
};
