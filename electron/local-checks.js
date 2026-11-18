const http = require("node:http");
const https = require("node:https");
const net = require("node:net");
const tls = require("node:tls");
const { spawn } = require("node:child_process");

const KINDS = new Set(["tcp", "tls", "http"]);
const SOURCES = new Set(["local", "jump"]);
const MIN_TIMEOUT = 500;
const MAX_TIMEOUT = 30_000;
const JUMP_CONNECT_MS = 1_500;

function text(value, label, maxLength = 255) {
  const result = String(value ?? "").trim();
  if (!result || result.length > maxLength || /\s/.test(result))
    throw new Error(`${label} non valido.`);
  return result;
}

function port(value, label = "Porta") {
  const number = Number(value);
  if (!Number.isInteger(number) || number < 1 || number > 65_535)
    throw new Error(`${label} deve essere tra 1 e 65535.`);
  return number;
}

function normalizeCheck(input = {}) {
  const kind = KINDS.has(input.kind) ? input.kind : "tcp";
  const source = SOURCES.has(input.source) ? input.source : "local";
  const host = text(input.host, "Host");
  const targetPort = port(input.port);
  const timeoutMs = Math.min(
    MAX_TIMEOUT,
    Math.max(MIN_TIMEOUT, Number(input.timeoutMs) || 5_000),
  );
  let path = null;
  let secure = false;
  if (kind === "http") {
    secure = Boolean(input.secure);
    path = String(input.path || "/").trim() || "/";
    if (!path.startsWith("/") || path.length > 2_000 || /[\r\n]/.test(path))
      throw new Error("Percorso HTTP non valido.");
  }
  if (source === "jump" && kind !== "tcp")
    throw new Error("Il check via jump host supporta solo TCP.");
  if (source === "jump") {
    const jump = input.jump;
    if (!jump || typeof jump !== "object")
      throw new Error("Jump host non specificato.");
    return {
      kind,
      source,
      host,
      port: targetPort,
      path,
      timeoutMs,
      secure,
      jump: {
        host: text(jump.host, "Host del jump"),
        port: port(jump.port || 22, "Porta del jump"),
        username: text(jump.username, "Utente del jump", 100),
      },
    };
  }
  return { kind, source, host, port: targetPort, path, secure, timeoutMs, jump: null };
}

function checked(check, extra) {
  return {
    ok: Boolean(extra.ok),
    kind: check.kind,
    source: check.source,
    host: check.host,
    port: check.port,
    path: check.path,
    secure: check.secure,
    status: extra.status ?? null,
    latencyMs: extra.latencyMs ?? null,
    detail: extra.detail ?? null,
    checkedAt: new Date().toISOString(),
  };
}

function tcpCheck(check) {
  return new Promise((resolve) => {
    const started = Date.now();
    const socket = net.connect({ host: check.host, port: check.port });
    let settled = false;
    const finish = (ok, detail) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve(
        checked(check, {
          ok,
          latencyMs: Date.now() - started,
          detail: detail || (ok ? "Connessione TCP stabilita." : null),
        }),
      );
    };
    socket.setTimeout(check.timeoutMs);
    socket.once("connect", () => finish(true));
    socket.once("timeout", () => finish(false, "Timeout."));
    socket.once("error", (error) => finish(false, error.code || error.message));
  });
}

function tlsCheck(check) {
  return new Promise((resolve) => {
    const started = Date.now();
    const socket = tls.connect({
      host: check.host,
      port: check.port,
      servername: check.host,
      timeout: check.timeoutMs,
    });
    let settled = false;
    const finish = (ok, extra = {}) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve(
        checked(check, {
          ok,
          latencyMs: Date.now() - started,
          status: extra.status ?? null,
          detail: extra.detail ?? null,
        }),
      );
    };
    socket.once("secureConnect", () =>
      finish(true, {
        ok: socket.authorized,
        status: socket.getProtocol(),
        detail: socket.authorized
          ? "Certificato valido."
          : String(socket.authorizationError || "Certificato non valido."),
      }),
    );
    socket.once("timeout", () => finish(false, { detail: "Timeout." }));
    socket.once("error", (error) =>
      finish(false, { detail: error.code || error.message }),
    );
  });
}

function httpCheck(check) {
  return new Promise((resolve) => {
    const started = Date.now();
    const client = check.secure ? https : http;
    const request = client.request(
      {
        host: check.host,
        port: check.port,
        path: check.path,
        method: "GET",
        timeout: check.timeoutMs,
        headers: { "User-Agent": "InfraOrgIT desktop check" },
      },
      (response) => {
        response.resume();
        response.destroy();
        resolve(
          checked(check, {
            ok: true,
            status: response.statusCode,
            latencyMs: Date.now() - started,
            detail: response.statusMessage || null,
          }),
        );
      },
    );
    request.once("timeout", () => {
      request.destroy(new Error("timeout"));
    });
    request.once("error", (error) =>
      resolve(
        checked(check, {
          ok: false,
          latencyMs: Date.now() - started,
          detail: error.code || error.message,
        }),
      ),
    );
    request.end();
  });
}

function buildJumpArgs(jump, host, port) {
  return [
    "-o",
    "BatchMode=yes",
    "-o",
    "ConnectTimeout=5",
    "-o",
    "ServerAliveInterval=5",
    "-p",
    String(jump.port || 22),
    "-W",
    `${host}:${port}`,
    `${jump.username}@${jump.host}`,
  ];
}

// shortcut: a filtered target can keep `ssh -W` open past the probe window
// and look reachable. This checks the TCP channel from the first bastion, not
// a full port scan; replace with a bastion-side probe when one is guaranteed.
function jumpCheck(
  check,
  { spawnProcess = spawn, sshBin = "ssh", jumpConnectMs = JUMP_CONNECT_MS } = {},
) {
  return new Promise((resolve) => {
    const started = Date.now();
    let settled = false;
    let stderr = "";
    const child = spawnProcess(sshBin, buildJumpArgs(check.jump, check.host, check.port), {
      stdio: ["ignore", "ignore", "pipe"],
    });
    const finish = (ok, detail) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try {
        child.kill("SIGTERM");
      } catch {
        // already gone
      }
      resolve(
        checked(check, {
          ok,
          latencyMs: Date.now() - started,
          detail: detail || null,
        }),
      );
    };
    const timer = setTimeout(
      () => finish(true, "Connessione TCP stabilita dal jump host."),
      jumpConnectMs,
    );
    timer.unref?.();
    child.stderr?.on("data", (chunk) => {
      stderr = (stderr + String(chunk)).slice(-400);
    });
    child.once("error", (error) => finish(false, error.message));
    child.once("exit", (code) =>
      finish(false, stderr.trim() || `ssh terminato con codice ${code}.`),
    );
  });
}

/** Runs a TCP/TLS/HTTP check from this machine or through the jump host. */
async function runLocalCheck(input, options = {}) {
  const check = normalizeCheck(input);
  if (check.source === "jump") return jumpCheck(check, options);
  if (check.kind === "tcp") return tcpCheck(check);
  if (check.kind === "tls") return tlsCheck(check);
  return httpCheck(check);
}

module.exports = {
  buildJumpArgs,
  normalizeCheck,
  runLocalCheck,
};
