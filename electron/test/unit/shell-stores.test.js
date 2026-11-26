const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  InstanceStore,
  localStartupFlag,
  normalizeInstanceUrl,
} = require("../../shell/instances");
const { SettingsStore } = require("../../shell/settings");

function tempFile(name) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-shell-"));
  return { directory, file: path.join(directory, name) };
}

test("instance URL accepts https anywhere and http only on loopback", () => {
  assert.equal(
    normalizeInstanceUrl("https://infra.example.com/"),
    "https://infra.example.com",
  );
  assert.equal(
    normalizeInstanceUrl("http://localhost:8443"),
    "http://localhost:8443",
  );
  assert.equal(
    normalizeInstanceUrl("http://127.0.0.1:3000"),
    "http://127.0.0.1:3000",
  );
  assert.equal(
    normalizeInstanceUrl("http://[::1]:3000"),
    "http://[::1]:3000",
  );
  assert.throws(
    () => normalizeInstanceUrl("http://remote.example.com"),
    /https/,
  );
  assert.throws(
    () => normalizeInstanceUrl("https://user:pass@infra.example.com"),
    /credenziali/,
  );
  assert.throws(
    () => normalizeInstanceUrl("https://infra.example.com/?token=1"),
    /query/,
  );
  assert.throws(
    () => normalizeInstanceUrl("https://infra.example.com/subpath"),
    /radice/,
  );
  assert.throws(() => normalizeInstanceUrl("not a url"), /valido/);
});

test("instance store adds, updates and removes with atomic files", () => {
  const { directory, file } = tempFile("instances.json");
  const store = new InstanceStore(file);
  const first = store.add({ name: "Locale", url: "https://localhost:8443" });
  assert.equal(first.length, 1);
  assert.equal(first[0].environment, "Locale");
  assert.equal(first[0].openAtStartup, true);
  assert.equal(first[0].lastUsedAt, null);

  const second = store.add({
    name: "Produzione",
    url: "https://infra.example.com",
    environment: "Produzione",
  });
  assert.equal(second.length, 2);
  assert.equal(second[1].openAtStartup, false);
  assert.throws(
    () => store.add({ name: "Duplicate", url: "https://infra.example.com" }),
    /già presente/,
  );

  store.update("https://infra.example.com", {
    name: "Produzione EU",
    environment: "Staging",
    openAtStartup: true,
  });
  const updated = store.get("https://infra.example.com");
  assert.equal(updated.name, "Produzione EU");
  assert.equal(updated.environment, "Staging");
  assert.equal(updated.openAtStartup, true);

  store.touch("https://infra.example.com", new Date("2026-11-13T10:00:00Z"));
  assert.equal(
    store.get("https://infra.example.com").lastUsedAt,
    "2026-11-13T10:00:00.000Z",
  );

  assert.equal(store.remove("https://localhost:8443"), true);
  assert.equal(store.list().length, 1);
  assert.equal(fs.existsSync(`${file}.${process.pid}.tmp`), false);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("instance store migrates the old array and skips invalid entries", () => {
  const { directory, file } = tempFile("instances.json");
  fs.writeFileSync(
    file,
    JSON.stringify([
      { name: "Locale", url: "https://localhost:8443/" },
      { name: "Remote http", url: "http://remote.example.com" },
      { name: "Second", url: "https://second.example.com", environment: "Test" },
    ]),
  );
  const store = new InstanceStore(file);
  const instances = store.list();
  assert.equal(instances.length, 2);
  assert.equal(instances[0].url, "https://localhost:8443");
  assert.equal(instances[0].openAtStartup, true);
  assert.equal(instances[1].environment, "Test");
  assert.equal(instances[1].openAtStartup, false);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("local startup flag keeps the user choice across re-creation", () => {
  assert.equal(localStartupFlag([], "InfraOrgIT locale"), false);
  assert.equal(localStartupFlag(null, "InfraOrgIT locale"), false);
  assert.equal(localStartupFlag(undefined, "InfraOrgIT locale"), false);
  assert.equal(localStartupFlag("nope", "InfraOrgIT locale"), false);
  assert.equal(
    localStartupFlag(
      [
        {
          name: "InfraOrgIT locale",
          url: "http://127.0.0.1:4000",
          environment: "Locale",
          openAtStartup: false,
        },
      ],
      "InfraOrgIT locale",
    ),
    false,
  );
  assert.equal(
    localStartupFlag(
      [
        {
          name: "InfraOrgIT locale",
          url: "http://127.0.0.1:4000",
          environment: "Locale",
          openAtStartup: true,
        },
      ],
      "InfraOrgIT locale",
    ),
    true,
  );
  assert.equal(
    localStartupFlag(
      [
        {
          name: "Altro locale",
          url: "http://127.0.0.1:5000",
          environment: "Locale",
          openAtStartup: true,
        },
      ],
      "InfraOrgIT locale",
    ),
    false,
  );
  assert.equal(
    localStartupFlag(
      [
        {
          name: "InfraOrgIT locale",
          url: "https://infra.example.com",
          environment: "Produzione",
          openAtStartup: true,
        },
      ],
      "InfraOrgIT locale",
    ),
    false,
  );
});

test("settings store validates the theme and falls back to system", () => {
  const { directory, file } = tempFile("settings.json");
  const store = new SettingsStore(file);
  assert.equal(store.theme, "system");
  assert.equal(store.setTheme("light"), "light");
  assert.equal(new SettingsStore(file).theme, "light");
  assert.equal(store.setTheme("neon"), "system");
  assert.equal(new SettingsStore(file).theme, "system");
  assert.equal(store.downloadDirectory, "");
  assert.equal(store.setDownloadDirectory("/tmp/infraorgit-downloads"), "/tmp/infraorgit-downloads");
  assert.equal(new SettingsStore(file).downloadDirectory, "/tmp/infraorgit-downloads");
  assert.equal(store.setDownloadDirectory(""), "");
  assert.equal(new SettingsStore(file).downloadDirectory, "");
  assert.equal(store.standaloneEnabled, false);
  assert.equal(store.setStandaloneEnabled(true), true);
  assert.equal(new SettingsStore(file).standaloneEnabled, true);
  assert.equal(store.setStandaloneEnabled(false), false);
  assert.equal(store.allowUnprotectedCredentialKey, false);
  assert.equal(store.setAllowUnprotectedCredentialKey(true), true);
  assert.equal(new SettingsStore(file).allowUnprotectedCredentialKey, true);
  assert.equal(store.setAllowUnprotectedCredentialKey(false), false);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("settings store manages multiple local instances", () => {
  const { directory, file } = tempFile("settings.json");
  const store = new SettingsStore(file);
  const first = store.addLocalInstance("Cliente A");
  const second = store.addLocalInstance("Cliente B");
  assert.notEqual(first.id, second.id);
  assert.deepEqual(
    new SettingsStore(file).localInstances.map((item) => item.name),
    ["Cliente A", "Cliente B"],
  );
  assert.equal(store.standaloneEnabled, true);
  store.removeLocalInstance(first.id);
  assert.deepEqual(
    new SettingsStore(file).localInstances.map((item) => item.name),
    ["Cliente B"],
  );
  assert.throws(() => store.addLocalInstance("   "), /obbligatorio/);
  fs.rmSync(directory, { recursive: true, force: true });
});

test("settings store migrates the old standalone flag", () => {
  const { directory, file } = tempFile("settings.json");
  fs.writeFileSync(
    file,
    JSON.stringify({ theme: "system", standaloneEnabled: true }),
  );
  const store = new SettingsStore(file);
  assert.deepEqual(store.localInstances, [
    { id: "default", name: "InfraOrgIT locale" },
  ]);
  assert.equal(store.standaloneEnabled, true);
  fs.rmSync(directory, { recursive: true, force: true });
});
