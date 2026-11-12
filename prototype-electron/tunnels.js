const { spawn } = require("child_process");

function sshTarget(server) {
  const host = server.sshHost || server.ipAddress || server.hostname;
  if (!host) throw new Error("Server senza host SSH.");
  return `${server.username}@${host}`;
}

function jumpArgument(server) {
  const jumps = Array.isArray(server.jumps) && server.jumps.length
    ? server.jumps
    : server.jump
      ? [server.jump]
      : [];
  if (jumps.length === 0) return [];
  return ["-J", jumps.map((jump) => `${jump.username}@${jump.host}:${jump.port}`).join(",")];
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
    args.push(
      "-R",
      `${remotePort}:${service.targetHost}:${service.targetPort}`,
    );
  } else {
    const targetHost = service?.targetHost || target?.targetHost || "127.0.0.1";
    const targetPort = service?.targetPort || target?.targetPort;
    if (!targetPort) throw new Error("Target senza porta.");
    if (!localPort) throw new Error("Porta locale richiesta.");
    args.push("-L", `127.0.0.1:${localPort}:${targetHost}:${targetPort}`);
  }

  args.push(sshTarget(server));
  return args;
}

function publicTunnel(row) {
  return {
    id: row.id,
    url: row.url,
    instanceName: row.server.serverName,
    serverName: row.server.serverName,
    application: row.service?.name || row.target?.application || "Tunnel",
    serviceId: row.service?.id || null,
    mode: row.service?.mode || "Local",
    kind: row.service?.kind || "Tcp",
    targetHost: row.service?.targetHost || row.target?.targetHost || "127.0.0.1",
    targetPort: row.service?.targetPort || row.target?.targetPort || null,
    localPort: row.localPort,
    remotePort: row.service?.remotePort || null,
    openUrl: row.openUrl,
    status: row.process && !row.process.killed ? "Active" : "Stopped",
  };
}

class TunnelManager {
  constructor(onState) {
    this.onState = onState;
    this.active = new Map();
    this.ssh = process.env.SSH_BIN || "ssh";
  }

  list() {
    return [...this.active.values()].map(publicTunnel);
  }

  start({ id, url, server, target, service, localPort }) {
    const mode = service?.mode || "Local";
    const requested = Number(localPort) || service?.localPort || target?.suggestedLocalPort || target?.targetPort;
    if (mode !== "Remote" && (!requested || requested < 1024 || requested > 65535))
      throw new Error("Porta locale tra 1024 e 65535.");
    const args = buildSshArgs(server, target, service, requested);
    const processHandle = spawn(this.ssh, args, {
      stdio: ["ignore", "pipe", "pipe"],
    });
    const row = {
      id,
      url,
      server,
      target,
      service,
      localPort: mode === "Remote" ? null : requested,
      openUrl:
        mode === "Local" &&
        (service?.kind === "Http" || service?.kind === "Https")
          ? `http://127.0.0.1:${requested}${service.openPath || ""}`
          : null,
      process: processHandle,
      args,
    };
    processHandle.stderr.on("data", (chunk) => {
      const text = String(chunk).trim();
      if (text) console.error("ssh", id, text);
    });
    processHandle.on("exit", (code, signal) => {
      console.log("ssh exited", id, code, signal);
      this.active.delete(id);
      this.onState(this.list());
    });
    this.active.set(id, row);
    this.onState(this.list());
    return row;
  }

  stop(id) {
    const row = this.active.get(id);
    if (!row) return;
    row.process.kill();
    this.active.delete(id);
    this.onState(this.list());
  }

  stopAll() {
    for (const id of [...this.active.keys()]) this.stop(id);
  }
}

module.exports = { TunnelManager, buildSshArgs, publicTunnel };
