const fs = require("fs");
const path = require("path");

const THEMES = ["system", "light", "dark"];

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
    this.data = { theme: "system" };
    try {
      const raw = JSON.parse(fs.readFileSync(this.file, "utf8"));
      if (raw && typeof raw === "object") {
        this.data.theme = normalizeTheme(raw.theme);
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

module.exports = { SettingsStore, THEMES, normalizeTheme };
