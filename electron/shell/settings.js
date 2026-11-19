const crypto = require("node:crypto");
const fs = require("fs");
const path = require("path");

const THEMES = ["system", "light", "dark"];
const MAX_LOCAL_INSTANCES = 20;

function normalizeTheme(value) {
  return THEMES.includes(value) ? value : "system";
}

class SettingsStore {
  constructor(file) {
    if (!file) throw new TypeError("SettingsStore richiede un file.");
    this.file = file;
    this.data = null;
  }

  load() {
    if (this.data) return { ...this.data };
    this.data = {
      theme: "system",
      downloadDirectory: "",
      localInstances: [],
      allowUnprotectedCredentialKey: false,
    };
    try {
      const raw = JSON.parse(fs.readFileSync(this.file, "utf8"));
      if (raw && typeof raw === "object") {
        this.data.theme = normalizeTheme(raw.theme);
        if (typeof raw.downloadDirectory === "string")
          this.data.downloadDirectory = raw.downloadDirectory.trim();
        const localInstances = normalizeLocalInstances(raw.localInstances);
        if (localInstances !== null) {
          this.data.localInstances = localInstances;
        } else if (raw.standaloneEnabled === true) {
          // Migrazione dalla singola istanza locale precedente.
          this.data.localInstances = [
            { id: "default", name: "InfraOrgIT locale" },
          ];
        }
        this.data.allowUnprotectedCredentialKey =
          raw.allowUnprotectedCredentialKey === true;
      }
    } catch (error) {
      if (error.code !== "ENOENT")
        console.warn("Impostazioni desktop illeggibili: uso i valori predefiniti.");
    }
    return { ...this.data };
  }

  get theme() {
    return this.load().theme;
  }

  setTheme(theme) {
    this.load();
    this.data.theme = normalizeTheme(theme);
    this.persist();
    return this.data.theme;
  }

  get downloadDirectory() {
    return this.load().downloadDirectory;
  }

  setDownloadDirectory(directory) {
    this.load();
    this.data.downloadDirectory = String(directory || "").trim();
    this.persist();
    return this.data.downloadDirectory;
  }

  get localInstances() {
    return this.load().localInstances.map((instance) => ({ ...instance }));
  }

  addLocalInstance(name) {
    this.load();
    const normalized = String(name || "").trim();
    if (!normalized) throw new Error("Il nome dell'istanza locale è obbligatorio.");
    if (normalized.length > 80)
      throw new Error("Il nome dell'istanza locale supera 80 caratteri.");
    if (this.data.localInstances.length >= MAX_LOCAL_INSTANCES)
      throw new Error(`Massimo ${MAX_LOCAL_INSTANCES} istanze locali.`);
    const instance = { id: crypto.randomUUID(), name: normalized };
    this.data.localInstances.push(instance);
    this.persist();
    return { ...instance };
  }

  removeLocalInstance(id) {
    this.load();
    this.data.localInstances = this.data.localInstances.filter(
      (instance) => instance.id !== id,
    );
    this.persist();
    return this.localInstances;
  }

  get standaloneEnabled() {
    return this.localInstances.length > 0;
  }

  /** Compatibilità: abilita o rimuove l'istanza locale predefinita. */
  setStandaloneEnabled(enabled) {
    this.load();
    if (!enabled) {
      this.data.localInstances = [];
    } else if (this.data.localInstances.length === 0) {
      this.data.localInstances = [{ id: "default", name: "InfraOrgIT locale" }];
    }
    this.persist();
    return this.standaloneEnabled;
  }

  get allowUnprotectedCredentialKey() {
    return this.load().allowUnprotectedCredentialKey;
  }

  setAllowUnprotectedCredentialKey(enabled) {
    this.load();
    this.data.allowUnprotectedCredentialKey = Boolean(enabled);
    this.persist();
    return this.data.allowUnprotectedCredentialKey;
  }

  persist() {
    const directory = path.dirname(this.file);
    fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
    const temporary = `${this.file}.${process.pid}.tmp`;
    fs.writeFileSync(temporary, JSON.stringify(this.data, null, 2), {
      mode: 0o600,
    });
    fs.chmodSync(temporary, 0o600);
    fs.renameSync(temporary, this.file);
  }
}

function normalizeLocalInstances(value) {
  if (!Array.isArray(value)) return null;
  const seen = new Set();
  const instances = [];
  for (const item of value) {
    if (!item || typeof item !== "object") continue;
    const id = typeof item.id === "string" ? item.id.trim() : "";
    const name = typeof item.name === "string" ? item.name.trim() : "";
    if (!id || !name || seen.has(id)) continue;
    seen.add(id);
    instances.push({ id, name });
    if (instances.length >= MAX_LOCAL_INSTANCES) break;
  }
  return instances;
}

module.exports = { SettingsStore, THEMES, normalizeTheme };
