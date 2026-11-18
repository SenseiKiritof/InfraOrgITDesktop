#!/usr/bin/env node
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { packager } = require("@electron/packager");

const root = path.resolve(__dirname, "..");
const options = {};
for (const argument of process.argv.slice(2)) {
  const [key, value] = argument.replace(/^--/, "").split("=");
  options[key] = value === undefined ? true : value;
}

const platform = options.platform || process.platform;
const arch = options.arch || process.arch;
const apiDir = options["api-dir"] || process.env.DESKTOP_API_DIR;
const webDir = options["web-dir"] || process.env.DESKTOP_WEB_DIR;
let staged = null;
if (apiDir || webDir) {
  if (!apiDir || !webDir)
    throw new Error("Servono --api-dir e --web-dir insieme.");
  staged = fs.mkdtempSync(path.join(os.tmpdir(), "infraorgit-package-"));
  fs.cpSync(apiDir, path.join(staged, "standalone"), { recursive: true });
  fs.cpSync(webDir, path.join(staged, "web"), { recursive: true });
}
const icon =
  platform === "darwin"
    ? "assets/icon.icns"
    : platform === "win32"
      ? "assets/icon.ico"
      : null;

packager({
  dir: root,
  name: "InfraOrgIT",
  platform,
  arch,
  out: path.resolve(root, options.out || "dist"),
  overwrite: true,
  asar: true,
  appBundleId: "it.infraorgit.desktop",
  appVersion: options["app-version"] || require("../package.json").version,
  icon: icon ? path.join(root, icon) : undefined,
  extraResource: staged
    ? [path.join(staged, "standalone"), path.join(staged, "web")]
    : undefined,
  ignore: [
    /^\/test($|\/)/,
    /^\/README\.md$/,
    /^\/package-lock\.json$/,
    /^\/scripts($|\/)/,
  ],
})
  .then((paths) => console.log(`Packaged: ${paths.join(", ")}`))
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  })
  .finally(() => {
    if (staged) fs.rmSync(staged, { recursive: true, force: true });
  });
