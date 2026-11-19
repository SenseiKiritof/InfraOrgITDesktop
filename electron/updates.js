const fs = require("node:fs");
const fsp = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const crypto = require("node:crypto");

const REPOSITORY = "SenseiKiritof/InfraOrgITDesktop";
const USER_AGENT = "InfraOrgIT-Desktop";
const MAX_DOWNLOAD_BYTES = 300 * 1024 * 1024;
const MANIFEST_NAME = "checksums.txt";
const SIGNATURE_NAME = "checksums.txt.sig";
const PUBLIC_KEY_PATH = path.join(__dirname, "update-public.pem");

function versionParts(value) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/.exec(
    String(value || "").trim(),
  );
  if (!match) return null;
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

/** Numeric semver comparison; build/prerelease suffixes are ignored. */
function compareVersions(left, right) {
  const a = versionParts(left);
  const b = versionParts(right);
  if (!a || !b) return null;
  for (let index = 0; index < 3; index += 1) {
    if (a[index] !== b[index]) return a[index] > b[index] ? 1 : -1;
  }
  return 0;
}

function assetTarget(platform, arch) {
  if (platform === "darwin" && arch === "arm64") return "osx-arm64";
  if (platform === "darwin" && arch === "x64") return "osx-x64";
  if (platform === "win32" && arch === "x64") return "win-x64";
  if (platform === "linux" && arch === "x64") return "linux-x64";
  return null;
}

function archiveExtension(target) {
  return target === "linux-x64" ? "tar.gz" : "zip";
}

function archiveName(version, target) {
  return `InfraOrgIT-Desktop-${version}-${target}.${archiveExtension(target)}`;
}

async function fetchWithTimeout(
  fetchImpl,
  url,
  options = {},
  timeoutMs = 15000,
) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetchImpl(url, { ...options, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

function describeHttpFailure(response) {
  if (response.status === 404) return "Nessuna release pubblicata su GitHub.";
  if (response.status === 403 || response.status === 429) {
    return "Limite di richieste GitHub raggiunto: riprova più tardi.";
  }
  return `GitHub ha risposto ${response.status}.`;
}

/**
 * Queries the latest stable GitHub release and picks the archive for this
 * platform. It never throws for network/HTTP problems: the UI gets a status.
 */
async function checkForUpdates({
  currentVersion,
  platform,
  arch,
  repository = REPOSITORY,
  fetchImpl = fetch,
  timeoutMs = 15000,
} = {}) {
  const target = assetTarget(platform, arch);
  const base = {
    currentVersion,
    checkedAt: new Date().toISOString(),
    platform,
    arch,
    target,
  };
  if (!target) {
    return {
      ...base,
      status: "unsupported",
      message: `Piattaforma non supportata: ${platform}/${arch}.`,
    };
  }
  let response;
  try {
    response = await fetchWithTimeout(
      fetchImpl,
      `https://api.github.com/repos/${repository}/releases/latest`,
      {
        headers: {
          Accept: "application/vnd.github+json",
          "User-Agent": USER_AGENT,
        },
      },
      timeoutMs,
    );
  } catch (error) {
    return {
      ...base,
      status: "error",
      message: `Controllo aggiornamenti non riuscito: ${error.message}`,
    };
  }
  if (!response.ok) {
    return {
      ...base,
      status: "error",
      message: describeHttpFailure(response),
    };
  }

  let release;
  try {
    release = await response.json();
  } catch {
    return {
      ...base,
      status: "error",
      message: "Risposta GitHub non leggibile.",
    };
  }
  const latestVersion = String(release.tag_name || "").replace(/^v/, "");
  const comparison = compareVersions(latestVersion, currentVersion);
  if (comparison === null) {
    return {
      ...base,
      status: "error",
      latestVersion,
      message: "Versione della release non riconosciuta.",
      releaseUrl: release.html_url,
    };
  }

  const name = archiveName(latestVersion, target);
  const assets = Array.isArray(release.assets) ? release.assets : [];
  const archive = assets.find((asset) => asset.name === name);
  const manifest = assets.find((asset) => asset.name === MANIFEST_NAME);
  const signature = assets.find((asset) => asset.name === SIGNATURE_NAME);
  const result = {
    ...base,
    latestVersion,
    releaseUrl: release.html_url,
    notes: String(release.body || "").slice(0, 4000),
    publishedAt: release.published_at,
  };

  if (comparison <= 0) {
    return { ...result, status: "current", message: "Sei già aggiornato." };
  }
  if (!archive || !manifest || !signature) {
    return {
      ...result,
      status: "error",
      message: `La release ${latestVersion} non contiene ${name} con il manifest firmato.`,
    };
  }
  return {
    ...result,
    status: "update",
    message: `Disponibile la versione ${latestVersion}.`,
    archive: { name, url: archive.browser_download_url, size: archive.size },
    manifest: {
      name: manifest.name,
      url: manifest.browser_download_url,
    },
    signature: {
      name: signature.name,
      url: signature.browser_download_url,
    },
  };
}

async function downloadFile(
  url,
  destination,
  { fetchImpl = fetch, timeoutMs = 180000 } = {},
) {
  const response = await fetchWithTimeout(
    fetchImpl,
    url,
    { headers: { "User-Agent": USER_AGENT }, redirect: "follow" },
    timeoutMs,
  );
  if (!response.ok) throw new Error(describeHttpFailure(response));
  const buffer = Buffer.from(await response.arrayBuffer());
  if (buffer.length > MAX_DOWNLOAD_BYTES)
    throw new Error("Il pacchetto supera il limite di 300 MB.");
  await fsp.writeFile(destination, buffer);
  return destination;
}

function parseChecksum(text) {
  const match = /^([0-9a-f]{64})\b/i.exec(String(text || "").trim());
  return match ? match[1].toLowerCase() : null;
}

/** Mappa nome file -> SHA-256 dal manifest `checksums.txt`. */
function parseManifest(text) {
  const entries = new Map();
  for (const line of String(text || "").split(/\r?\n/)) {
    const match = /^([0-9a-f]{64})\s+\*?(.+)$/i.exec(line.trim());
    if (match) entries.set(match[2], match[1].toLowerCase());
  }
  return entries;
}

function verifyManifestSignature(
  manifestText,
  signature,
  publicKey = fs.readFileSync(PUBLIC_KEY_PATH),
) {
  if (!manifestText || !signature?.length) return false;
  return crypto.verify(
    "sha256",
    Buffer.from(String(manifestText)),
    publicKey,
    signature,
  );
}

function manifestChecksum(manifestText, archiveName) {
  return parseManifest(manifestText).get(archiveName) ?? null;
}

async function sha256File(file) {
  const hash = crypto.createHash("sha256");
  await new Promise((resolve, reject) => {
    const stream = fs.createReadStream(file);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("error", reject);
    stream.on("end", resolve);
  });
  return hash.digest("hex");
}

async function verifyChecksum(file, checksumText) {
  const expected = parseChecksum(checksumText);
  if (!expected) throw new Error("Checksum non valido.");
  const actual = await sha256File(file);
  if (actual !== expected) {
    throw new Error("Il pacchetto scaricato non corrisponde al checksum.");
  }
  return true;
}

function shellQuote(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function appRootFor(platform, executable) {
  if (platform === "darwin") {
    const marker = `${path.sep}InfraOrgIT.app${path.sep}`;
    const index = executable.indexOf(marker);
    return index === -1
      ? null
      : executable.slice(0, index + marker.length - 1);
  }
  return path.dirname(executable);
}

/**
 * Builds the detached helper that replaces the app after this process exits.
 * macOS uses ditto (preserves the bundle), Windows PowerShell, Linux tar.
 */
function installPlan({
  platform,
  archivePath,
  appRoot,
  pid = process.pid,
  relaunch = true,
  tempDir = path.join(os.tmpdir(), `infraorgit-update-${Date.now()}`),
}) {
  const wait = `while kill -0 ${pid} 2>/dev/null; do sleep 1; done`;
  if (platform === "darwin") {
    const incoming = `${appRoot}.new`;
    const previous = `${appRoot}.old`;
    const script = [
      "#!/bin/sh",
      "set -e",
      pid > 0 ? wait : "",
      pid > 0 ? "sleep 1" : "",
      `ditto -x -k ${shellQuote(archivePath)} ${shellQuote(tempDir)}`,
      `rm -rf ${shellQuote(incoming)} ${shellQuote(previous)}`,
      `ditto ${shellQuote(path.join(tempDir, "InfraOrgIT.app"))} ${shellQuote(incoming)}`,
      `mv ${shellQuote(appRoot)} ${shellQuote(previous)}`,
      `mv ${shellQuote(incoming)} ${shellQuote(appRoot)}`,
      `rm -rf ${shellQuote(previous)}`,
      relaunch ? `open ${shellQuote(appRoot)}` : "",
      "",
    ]
      .filter(Boolean)
      .join("\n");
    return { command: "/bin/sh", args: [], script, tempDir };
  }
  if (platform === "win32") {
    const incoming = `${appRoot}.new`;
    const previous = `${appRoot}.old`;
    const script = [
      `while (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }`,
      pid > 0 ? "Start-Sleep -Seconds 1" : "",
      `Expand-Archive -LiteralPath ${shellQuote(archivePath)} -DestinationPath ${shellQuote(tempDir)} -Force`,
      `if (Test-Path -LiteralPath ${shellQuote(incoming)}) { Remove-Item -LiteralPath ${shellQuote(incoming)} -Recurse -Force }`,
      `if (Test-Path -LiteralPath ${shellQuote(previous)}) { Remove-Item -LiteralPath ${shellQuote(previous)} -Recurse -Force }`,
      `Move-Item -LiteralPath ${shellQuote(path.join(tempDir, "InfraOrgIT-win32-x64"))} -Destination ${shellQuote(incoming)}`,
      `if (Test-Path -LiteralPath ${shellQuote(appRoot)}) { Move-Item -LiteralPath ${shellQuote(appRoot)} -Destination ${shellQuote(previous)} }`,
      `Move-Item -LiteralPath ${shellQuote(incoming)} -Destination ${shellQuote(appRoot)}`,
      `if (Test-Path -LiteralPath ${shellQuote(previous)}) { Remove-Item -LiteralPath ${shellQuote(previous)} -Recurse -Force }`,
      relaunch
        ? `Start-Process -FilePath ${shellQuote(path.join(appRoot, "InfraOrgIT.exe"))}`
        : "",
    ]
      .filter(Boolean)
      .join("\n");
    return { command: "powershell.exe", args: ["-NoProfile", "-Command", script], script, tempDir };
  }
  const incoming = `${appRoot}.new`;
  const previous = `${appRoot}.old`;
  const script = [
    "#!/bin/sh",
    "set -e",
    pid > 0 ? wait : "",
    pid > 0 ? "sleep 1" : "",
    `tar -xzf ${shellQuote(archivePath)} -C ${shellQuote(tempDir)}`,
    `rm -rf ${shellQuote(incoming)} ${shellQuote(previous)}`,
    `mv ${shellQuote(path.join(tempDir, "InfraOrgIT-linux-x64"))} ${shellQuote(incoming)}`,
    `if [ -e ${shellQuote(appRoot)} ]; then mv ${shellQuote(appRoot)} ${shellQuote(previous)}; fi`,
    `mv ${shellQuote(incoming)} ${shellQuote(appRoot)}`,
    `rm -rf ${shellQuote(previous)}`,
    relaunch ? `nohup ${shellQuote(path.join(appRoot, "InfraOrgIT"))} >/dev/null 2>&1 &` : "",
    "",
  ]
    .filter(Boolean)
    .join("\n");
  return { command: "/bin/sh", args: [], script, tempDir };
}

module.exports = {
  REPOSITORY,
  archiveName,
  assetTarget,
  checkForUpdates,
  compareVersions,
  downloadFile,
  installPlan,
  manifestChecksum,
  parseChecksum,
  parseManifest,
  verifyChecksum,
  verifyManifestSignature,
  versionParts,
  appRootFor,
};
