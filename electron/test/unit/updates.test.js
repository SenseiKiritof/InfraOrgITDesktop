const assert = require("node:assert");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync, spawnSync } = require("node:child_process");
const { test } = require("node:test");
const {
  archiveName,
  assetTarget,
  checkForUpdates,
  compareVersions,
  installPlan,
  parseChecksum,
  verifyChecksum,
} = require("../../updates");

function release(version, overrides = {}) {
  const target = "osx-arm64";
  const name = `InfraOrgIT-Desktop-${version}-${target}.zip`;
  return {
    tag_name: `v${version}`,
    html_url: `https://github.com/SenseiKiritof/InfraOrgITDesktop/releases/tag/v${version}`,
    body: "Note",
    published_at: "2026-11-17T10:00:00Z",
    assets: [
      {
        name,
        browser_download_url: `https://example.test/${name}`,
        size: 10,
      },
      {
        name: `${name}.sha256`,
        browser_download_url: `https://example.test/${name}.sha256`,
      },
    ],
    ...overrides,
  };
}

function fakeFetch(payload, status = 200) {
  return async () => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => payload,
  });
}

test("compareVersions handles tags and ordering", () => {
  assert.equal(compareVersions("v0.3.1", "0.3.1"), 0);
  assert.equal(compareVersions("0.4.0", "0.3.9"), 1);
  assert.equal(compareVersions("0.3.0", "0.3.1"), -1);
  assert.equal(compareVersions("nope", "0.3.1"), null);
});

test("assetTarget picks the published package", () => {
  assert.equal(assetTarget("darwin", "arm64"), "osx-arm64");
  assert.equal(assetTarget("darwin", "x64"), "osx-x64");
  assert.equal(assetTarget("win32", "x64"), "win-x64");
  assert.equal(assetTarget("linux", "x64"), "linux-x64");
  assert.equal(assetTarget("linux", "arm64"), null);
  assert.equal(
    archiveName("0.4.0", "linux-x64"),
    "InfraOrgIT-Desktop-0.4.0-linux-x64.tar.gz",
  );
});

test("checkForUpdates reports a newer release with archive and checksum", async () => {
  const result = await checkForUpdates({
    currentVersion: "0.3.1",
    platform: "darwin",
    arch: "arm64",
    fetchImpl: fakeFetch(release("0.4.0")),
  });
  assert.equal(result.status, "update");
  assert.equal(result.latestVersion, "0.4.0");
  assert.equal(result.archive.name, "InfraOrgIT-Desktop-0.4.0-osx-arm64.zip");
  assert.equal(result.checksum.name.endsWith(".sha256"), true);
});

test("checkForUpdates keeps the current install", async () => {
  const result = await checkForUpdates({
    currentVersion: "0.4.0",
    platform: "darwin",
    arch: "arm64",
    fetchImpl: fakeFetch(release("0.4.0")),
  });
  assert.equal(result.status, "current");
  assert.equal(result.archive, undefined);
});

test("checkForUpdates survives missing assets and HTTP errors", async () => {
  const missing = await checkForUpdates({
    currentVersion: "0.3.1",
    platform: "darwin",
    arch: "arm64",
    fetchImpl: fakeFetch(release("0.4.0", { assets: [] })),
  });
  assert.equal(missing.status, "error");
  assert.match(missing.message, /non contiene/);

  const notFound = await checkForUpdates({
    currentVersion: "0.3.1",
    platform: "darwin",
    arch: "arm64",
    fetchImpl: fakeFetch({}, 404),
  });
  assert.equal(notFound.status, "error");
  assert.match(notFound.message, /Nessuna release/);

  const limited = await checkForUpdates({
    currentVersion: "0.3.1",
    platform: "darwin",
    arch: "arm64",
    fetchImpl: fakeFetch({}, 403),
  });
  assert.equal(limited.status, "error");
  assert.match(limited.message, /Limite/);
});

test("checksum parsing and verification", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-checksum-"));
  const file = path.join(dir, "package.zip");
  fs.writeFileSync(file, "payload");
  const hash = crypto.createHash("sha256").update("payload").digest("hex");
  assert.equal(parseChecksum(`${hash}  package.zip`), hash);
  assert.equal(parseChecksum("invalid"), null);
  await verifyChecksum(file, `${hash}  package.zip`);
  await assert.rejects(
    () => verifyChecksum(file, `${"0".repeat(64)}  package.zip`),
    /checksum/,
  );
  fs.rmSync(dir, { recursive: true, force: true });
});

test("installPlan waits for the app and replaces the bundle", () => {
  const mac = installPlan({
    platform: "darwin",
    archivePath: "/tmp/pkg.zip",
    appRoot: "/Users/test/Applications/InfraOrgIT.app",
    pid: 123,
  });
  assert.equal(mac.command, "/bin/sh");
  assert.match(mac.script, /kill -0 123/);
  assert.match(mac.script, /ditto -x -k/);
  assert.match(mac.script, /open/);

  const linux = installPlan({
    platform: "linux",
    archivePath: "/tmp/pkg.tar.gz",
    appRoot: "/opt/InfraOrgIT",
    pid: 0,
    relaunch: false,
  });
  assert.match(linux.script, /tar -xzf/);
  assert.doesNotMatch(linux.script, /nohup/);

  const win = installPlan({
    platform: "win32",
    archivePath: "C:\\Temp\\pkg.zip",
    appRoot: "C:\\Apps\\InfraOrgIT-win32-x64",
    pid: 0,
  });
  assert.equal(win.command, "powershell.exe");
  assert.match(win.script, /Expand-Archive/);
});

test(
  "installPlan script replaces a macOS bundle",
  { skip: process.platform !== "darwin" },
  () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-install-"));
    const sourceApp = path.join(dir, "source", "InfraOrgIT.app");
    const targetApp = path.join(dir, "target", "InfraOrgIT.app");
    fs.mkdirSync(sourceApp, { recursive: true });
    fs.mkdirSync(targetApp, { recursive: true });
    fs.writeFileSync(path.join(sourceApp, "marker.txt"), "new");
    fs.writeFileSync(path.join(targetApp, "marker.txt"), "old");
    const archive = path.join(dir, "package.zip");
    execFileSync("ditto", [
      "-c",
      "-k",
      "--keepParent",
      sourceApp,
      archive,
    ]);

    const plan = installPlan({
      platform: "darwin",
      archivePath: archive,
      appRoot: targetApp,
      pid: 0,
      relaunch: false,
      tempDir: path.join(dir, "extract"),
    });
    const script = path.join(dir, "install.sh");
    fs.writeFileSync(script, plan.script, { mode: 0o755 });
    const run = spawnSync("/bin/sh", [script], { encoding: "utf8" });
    assert.equal(run.status, 0, run.stderr);
    assert.equal(fs.readFileSync(path.join(targetApp, "marker.txt"), "utf8"), "new");
    fs.rmSync(dir, { recursive: true, force: true });
  },
);
