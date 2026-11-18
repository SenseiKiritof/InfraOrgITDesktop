const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");
const tls = require("node:tls");
const { spawn } = require("node:child_process");
const WebSocket = require("ws");

const MAX_RELAY_CONNECTIONS = 32;
const TLS_TRUST_ERRORS = new Set([
  "UNABLE_TO_GET_ISSUER_CERT_LOCALLY",
  "UNABLE_TO_VERIFY_LEAF_SIGNATURE",
  "SELF_SIGNED_CERT_IN_CHAIN",
  "DEPTH_ZERO_SELF_SIGNED_CERT",
]);

/**
 * Extra CA for the relay WebSocket only. Node's TLS stack ignores the OS
 * keychain, so a private CA trusted by Chromium still fails here. The bundle
 * adds the custom root to the default roots and keeps verification enabled.
 */
function relayCaBundle({ env = process.env, userData = "" } = {}) {
  const candidates = [
    env.DESKTOP_RELAY_CA,
    userData ? path.join(userData, "relay-ca.pem") : "",
  ].filter(Boolean);
  for (const file of candidates) {
    try {
      const custom = fs.readFileSync(file, "utf8").trim();
      if (custom) return [...tls.rootCertificates, custom].join("\n");
    } catch {
      // Missing or unreadable custom CA: keep the default roots.
    }
  }
  return undefined;
}

function relayFailure(error) {
  const message = `relay: ${error?.message || error}`;
  return TLS_TRUST_ERRORS.has(error?.code)
    ? `${message} (CA non attendibile: configura relay-ca.pem)`
    : message;
}

function serverJumps(server) {
  return Array.isArray(server.jumps) && server.jumps.length
    ? server.jumps
    : server.jump
      ? [server.jump]
      : [];
}

function sshTarget(server) {
  const host = server.sshHost || server.ipAddress || server.hostname;
  if (!host) throw new Error("Server senza host SSH.");
  if (!server.username) throw new Error("Server senza utente SSH.");
  return `${server.username}@${host}`;
}

function jumpArgument(server) {
  const jumps = serverJumps(server);
  if (jumps.length === 0) return [];
  return [
    "-J",
    jumps.map((jump) => `${jump.username}@${jump.host}:${jump.port}`).join(","),
  ];
}

function forwardTarget(service, target) {
  const host = service?.targetHost || target?.targetHost || "127.0.0.1";
  const port = service?.targetPort || target?.targetPort;
  if (!port) throw new Error("Target senza porta.");
  return { host, port };
}

function buildSshArgs(server, target, service, localPort) {
  const mode = service?.mode || "Local";
  const args = [
    "-N",
    "-o",
    "ExitOnForwardFailure=yes",
    "-o",
    "ServerAliveInterval=30",
    "-o",
    "ServerAliveCountMax=3",
    "-p",
    String(server.sshPort || 22),
    ...jumpArgument(server),
  ];

  if (mode === "Dynamic") {
    args.push("-D", `127.0.0.1:${localPort}`);
  } else if (mode === "Remote") {
    const remotePort = service?.remotePort;
    if (!remotePort) throw new Error("Servizio remoto senza porta remota.");
    args.push("-R", `${remotePort}:${service.targetHost}:${service.targetPort}`);
  } else {
    const { host, port } = forwardTarget(service, target);
    if (!localPort) throw new Error("Porta locale richiesta.");
    args.push("-L", `127.0.0.1:${localPort}:${host}:${port}`);
  }

  args.push(sshTarget(server));
  return args;
}

function serverHost(server) {
  return server.sshHost || server.ipAddress || server.hostname || "";
}

function canRelay(service, target) {
  if (service)
    return Boolean(service.id) && (service.mode || "Local") === "Local";
  return Boolean(target?.serverApplicationId) && Boolean(target?.targetPort);
}

/**
 * Auto asks the caller whether the SSH target is reachable from this machine;
 * Server never falls back to the client, Client never uses the relay.
 */
function chooseTransport(policy, relayable, reachable) {
  if (policy === "Server") {
    if (!relayable)
      throw new Error(
        "La modalità server supporta solo i servizi tunnel Local. " +
          "Dynamic e Remote richiedono una connessione diretta dal client.",
      );
    return "server";
  }
  if (policy === "Client") return "client";
  return reachable ? "client" : relayable ? "server" : "client";
}

/**
 * Auto can only use the desktop SSH agent or a downloaded credential. When the
 * credential is not a key (password or missing), the relay is the only viable
 * path for Local targets.
 */
function transportFor(policy, relayable, reachable, clientAuth) {
  if (policy === "Auto" && clientAuth === false)
    return relayable ? "server" : "client";
  return chooseTransport(policy, relayable, reachable);
}

function relayUrl(instanceUrl, serverId, target, tunnelId) {
  const url = new URL(instanceUrl);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.pathname = `${url.pathname.replace(/\/$/, "")}/api/tunnels/relay`;
  const params = new URLSearchParams({ serverId: String(serverId) });
  if (target.serviceId)
    params.set("serviceId", String(target.serviceId));
  else
    params.set("serverApplicationId", String(target.serverApplicationId));
  params.set("tunnelId", String(tunnelId));
  url.search = params.toString();
  return url.toString();
}

function publicTunnel(row) {
  return {
    id: row.id,
    url: row.url,
    instanceName: row.instanceName || row.server.serverName,
    environmentLabel: row.server.environmentLabel || "",
    serverId: row.server.serverId ?? null,
    serverName: row.server.serverName,
    ip: serverHost(row.server),
    application: row.service?.name || row.target?.application || "Tunnel",
    source: row.service?.name || row.target?.application || "Tunnel",
    serviceId: row.service?.id || null,
    targetId: row.target?.serverApplicationId || null,
    mode: row.service?.mode || row.mode || "Local",
    transport: row.transport || "Client",
    kind: row.service?.kind || row.target?.kind || "Tcp",
    targetHost: row.service?.targetHost || row.target?.targetHost || "127.0.0.1",
    targetPort: row.service?.targetPort || row.target?.targetPort || null,
    localPort: row.localPort,
    remotePort: row.service?.remotePort || null,
    openUrl: row.openUrl,
    status: row.status,
    startedAt: row.startedAt,
    stoppedAt: row.stoppedAt || null,
    error: row.error || null,
  };
}

function samePort(left, right) {
  return left !== null && right !== null && Number(left) === Number(right);
}

/**
 * Owns SSH processes and server relays for every instance in one window.
 * Definitions are kept after a stop so the global drawer can show active and
 * stopped rows and restart them scoped to the owning instance.
 */
class TunnelManager {
  constructor(
    onState = () => {},
    {
      spawnProcess = spawn,
      relayTarget = null,
      WebSocketClient = WebSocket,
      connectTarget = null,
      forwardOut = null,
      logger = console,
    } = {},
  ) {
    this.onState = onState;
    this.rows = new Map();
    this.ssh = process.env.DESKTOP_SSH_BIN || process.env.SSH_BIN || "ssh";
    this.spawnProcess = spawnProcess;
    this.relayTarget = relayTarget;
    this.WebSocketClient = WebSocketClient;
    this.connectTarget = connectTarget;
    this.forwardOut = forwardOut;
    this.logger = logger;
  }

  list() {
    return [...this.rows.values()]
      .sort((left, right) => {
        const active = Number(right.status === "Active") - Number(left.status === "Active");
        return active || String(right.startedAt).localeCompare(String(left.startedAt));
      })
      .map(publicTunnel);
  }

  active(url) {
    return this.list().filter(
      (row) => row.status === "Active" && (!url || row.url === url),
    );
  }

  get(id) {
    const row = this.rows.get(id);
    return row ? publicTunnel(row) : null;
  }

  duplicatePort(localPort, exceptId = null) {
    if (localPort === null || localPort === undefined) return null;
    return (
      this.list().find(
        (row) =>
          row.status === "Active" &&
          row.id !== exceptId &&
          samePort(row.localPort, localPort),
      ) || null
    );
  }

  async start({
    id,
    url,
    instanceName,
    server,
    target,
    service,
    localPort,
    mode,
    transport = "client",
    credential = null,
  }) {
    if (!id) throw new Error("Identificativo tunnel mancante.");
    if (!url) throw new Error("Istanza del tunnel mancante.");
    if (!server) throw new Error("Server del tunnel mancante.");
    const existing = this.rows.get(id);
    if (existing?.status === "Active") throw new Error("Questo tunnel è già attivo.");

    const tunnelMode = service?.mode || mode || "Local";
    const requested =
      tunnelMode === "Remote"
        ? null
        : Number(localPort) ||
          Number(service?.localPort) ||
          Number(target?.suggestedLocalPort) ||
          Number(target?.targetPort);
    if (tunnelMode !== "Remote") {
      if (!requested || requested < 1024 || requested > 65535)
        throw new Error("La porta locale deve essere tra 1024 e 65535.");
      const duplicate = this.duplicatePort(requested, id);
      if (duplicate) {
        throw new Error(
          `La porta locale ${requested} è già usata da ${duplicate.instanceName} · ${duplicate.serverName}.`,
        );
      }
    }

    const resolvedTransport =
      transport === "server" || transport === "Server" ? "Server" : "Client";
    if (resolvedTransport === "Server" && !canRelay(service, target))
      throw new Error(
        "Il relay server richiede un servizio Local o un deployment con porta target.",
      );
    if (credential && tunnelMode !== "Local")
      throw new Error(
        "Le credenziali scaricate supportano solo i tunnel Local. " +
          "Dynamic e Remote richiedono una chiave SSH nell'agente locale.",
      );

    const row = {
      id,
      url,
      instanceName: instanceName || server.serverName,
      server,
      target: target || null,
      service: service || null,
      mode: tunnelMode,
      transport: resolvedTransport,
      localPort: tunnelMode === "Remote" ? null : requested,
      openUrl:
        tunnelMode === "Local" &&
        (service?.kind === "Http" || service?.kind === "Https")
          ? `http://127.0.0.1:${requested}${service.openPath || ""}`
          : null,
      process: null,
      args: [],
      relayServer: null,
      relaySockets: new Set(),
      relayWs: new Set(),
      forwardServer: null,
      forwardSockets: new Set(),
      sshConnection: null,
      status: "Active",
      startedAt: new Date().toISOString(),
      stoppedAt: null,
      error: null,
    };

    if (resolvedTransport === "Server") {
      await this.startRelay(row, requested);
    } else if (credential) {
      await this.startForward(row, credential, target, service, requested);
    } else {
      this.startSsh(row, target, service, requested);
    }

    this.rows.set(id, row);
    this.onState(this.list());
    return publicTunnel(row);
  }

  startSsh(row, target, service, localPort) {
    const args = buildSshArgs(row.server, target, service, localPort);
    const processHandle = this.spawnProcess(this.ssh, args, {
      stdio: ["ignore", "pipe", "pipe"],
    });
    row.args = args;
    row.process = processHandle;

    let stderr = "";
    processHandle.stderr?.on("data", (chunk) => {
      stderr = String(chunk).trim().slice(-400);
      if (stderr) this.logger.error("ssh", row.id, stderr);
    });
    processHandle.once("error", (error) => {
      row.process = null;
      row.status = "Stopped";
      row.stoppedAt = new Date().toISOString();
      row.error = `ssh non avviato: ${error.message}`;
      this.onState(this.list());
    });
    processHandle.once("exit", (code, signal) => {
      row.process = null;
      row.status = "Stopped";
      row.stoppedAt = new Date().toISOString();
      if (!row.stopping && code !== 0) {
        row.error =
          stderr ||
          `ssh terminato (${signal || `codice ${code}`}).`;
      }
      this.onState(this.list());
    });
  }

  /**
   * Local forwarding over ssh2 for a downloaded credential. The plaintext
   * secret lives only in this call and in the live SSH client; the row keeps
   * metadata only, so a restart must resolve the credential again.
   */
  async startForward(row, credential, target, service, localPort) {
    if (
      typeof this.connectTarget !== "function" ||
      typeof this.forwardOut !== "function"
    )
      throw new Error("Forward SSH locale non configurato in questa build.");
    const forwarded = forwardTarget(service, target);
    if (!localPort) throw new Error("Porta locale richiesta.");

    const connection = await this.connectTarget({
      host: serverHost(row.server),
      port: row.server.sshPort || 22,
      username: row.server.username,
      hostKeyFingerprint: row.server.hostKeyFingerprint || null,
      jumps: serverJumps(row.server),
      credential,
    });
    row.sshConnection = connection;
    row.forwardTarget = forwarded;

    const forwardServer = net.createServer((socket) => {
      void this.openForwardConnection(row, socket);
    });
    try {
      await new Promise((resolve, reject) => {
        const onError = (error) => {
          forwardServer.close();
          reject(
            new Error(
              `Porta locale ${localPort} non disponibile: ${error.message}`,
            ),
          );
        };
        forwardServer.once("error", onError);
        forwardServer.listen(
          { host: "127.0.0.1", port: localPort, exclusive: true },
          () => {
            forwardServer.off("error", onError);
            resolve();
          },
        );
      });
    } catch (error) {
      connection.dispose();
      row.sshConnection = null;
      throw error;
    }
    forwardServer.on("error", (error) => {
      row.error = `forward: ${error.message}`;
      this.onState(this.list());
    });
    row.forwardServer = forwardServer;
  }

  async openForwardConnection(row, socket) {
    const connection = row.sshConnection;
    if (!connection || !row.forwardServer) {
      socket.destroy();
      return;
    }
    if (row.forwardSockets.size >= MAX_RELAY_CONNECTIONS) {
      socket.destroy();
      return;
    }

    row.forwardSockets.add(socket);
    socket.pause();
    let stream;
    try {
      stream = await this.forwardOut(
        connection.client,
        row.forwardTarget.host,
        row.forwardTarget.port,
      );
    } catch (error) {
      row.error = `forward: ${error.message}`;
      row.forwardSockets.delete(socket);
      socket.destroy();
      this.onState(this.list());
      return;
    }

    socket.on("error", () => stream.destroy());
    socket.on("close", () => {
      row.forwardSockets.delete(socket);
      stream.destroy();
    });
    stream.on("error", (error) => {
      row.error = `forward: ${error.message}`;
      socket.destroy();
    });
    stream.on("close", () => socket.destroy());
    socket.pipe(stream).pipe(socket);
    socket.resume();
  }

  async startRelay(row, localPort) {
    if (typeof this.relayTarget !== "function")
      throw new Error("Relay server non configurato in questa build.");
    const relayServer = net.createServer((socket) => {
      void this.openRelayConnection(row, socket);
    });
    await new Promise((resolve, reject) => {
      const onError = (error) => {
        relayServer.close();
        reject(
          new Error(
            `Porta locale ${localPort} non disponibile: ${error.message}`,
          ),
        );
      };
      relayServer.once("error", onError);
      relayServer.listen(
        { host: "127.0.0.1", port: localPort, exclusive: true },
        () => {
          relayServer.off("error", onError);
          resolve();
        },
      );
    });
    relayServer.on("error", (error) => {
      row.error = `relay: ${error.message}`;
      this.onState(this.list());
    });
    row.relayServer = relayServer;
  }

  async openRelayConnection(row, socket) {
    if (!row.relayServer) {
      socket.destroy();
      return;
    }
    if (row.relaySockets.size >= MAX_RELAY_CONNECTIONS) {
      socket.destroy();
      return;
    }

    row.relaySockets.add(socket);
    socket.pause();
    let ws;
    try {
      const target = await this.relayTarget(row);
      ws = new this.WebSocketClient(target.url, {
        headers: target.headers,
        perMessageDeflate: false,
        rejectUnauthorized: target.rejectUnauthorized,
        ca: target.ca,
      });
    } catch (error) {
      row.error = relayFailure(error);
      row.relaySockets.delete(socket);
      socket.destroy();
      this.onState(this.list());
      return;
    }

    row.relayWs.add(ws);
    ws.on("open", () => {
      socket.resume();
    });
    ws.on("message", (data) => {
      if (!socket.destroyed) socket.write(data);
    });
    ws.on("error", (error) => {
      // A single failed connection must not stop the tunnel.
      row.error = relayFailure(error);
      row.relayWs.delete(ws);
      row.relaySockets.delete(socket);
      socket.destroy();
      this.onState(this.list());
    });
    ws.on("close", () => {
      row.relayWs.delete(ws);
      row.relaySockets.delete(socket);
      socket.destroy();
    });
    socket.on("data", (chunk) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(chunk);
    });
    socket.on("error", () => {
      ws.close();
    });
    socket.on("close", () => {
      row.relaySockets.delete(socket);
      if (ws.readyState === WebSocket.OPEN) ws.close();
    });
  }

  /** Stops only the tunnel with this id; the owning tab cannot be crossed. */
  stop(id, instanceUrl = null) {
    const row = this.rows.get(id);
    if (!row || (instanceUrl && row.url !== instanceUrl)) return false;
    if (row.status !== "Active") return false;
    row.stopping = true;
    row.process?.kill();
    row.process = null;
    for (const socket of row.relaySockets || []) socket.destroy();
    for (const ws of row.relayWs || []) ws.close();
    row.relaySockets?.clear();
    row.relayWs?.clear();
    row.relayServer?.close();
    row.relayServer = null;
    for (const socket of row.forwardSockets || []) socket.destroy();
    row.forwardSockets?.clear();
    row.forwardServer?.close();
    row.forwardServer = null;
    row.sshConnection?.dispose();
    row.sshConnection = null;
    row.forwardTarget = null;
    row.status = "Stopped";
    row.stoppedAt = new Date().toISOString();
    this.onState(this.list());
    return true;
  }

  async restart(id, instanceUrl = null, transport = null, credential = null) {
    const row = this.rows.get(id);
    if (!row || (instanceUrl && row.url !== instanceUrl))
      throw new Error("Tunnel non trovato per questa istanza.");
    if (row.status === "Active") throw new Error("Questo tunnel è già attivo.");
    return this.start({
      id: row.id,
      url: row.url,
      instanceName: row.instanceName,
      server: row.server,
      target: row.target,
      service: row.service,
      localPort: row.localPort,
      mode: row.mode,
      transport: transport || row.transport,
      credential,
    });
  }

  stopAll() {
    for (const row of [...this.rows.values()]) {
      if (row.status === "Active") this.stop(row.id);
    }
  }

  forget(id) {
    const row = this.rows.get(id);
    if (!row || row.status === "Active") return false;
    return this.rows.delete(id);
  }

  /** The drawer remove button: stop an active row first, then drop it. */
  remove(id) {
    const row = this.rows.get(id);
    if (!row) return false;
    if (row.status === "Active") this.stop(id);
    return this.rows.delete(id);
  }
}

module.exports = {
  TunnelManager,
  buildSshArgs,
  canRelay,
  chooseTransport,
  transportFor,
  publicTunnel,
  relayCaBundle,
  relayFailure,
  relayUrl,
};
