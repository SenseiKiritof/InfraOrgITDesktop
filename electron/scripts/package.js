#!/usr/bin/env node
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
  });
