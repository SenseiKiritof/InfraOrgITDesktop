const crypto = require("node:crypto");
const { Client } = require("ssh2");

function agentPath() {
  if (process.env.SSH_AUTH_SOCK) return process.env.SSH_AUTH_SOCK;
  // Windows OpenSSH agent exposes a named pipe; ssh2 accepts it as `agent`.
  if (process.platform === "win32") return "\\\\.\\pipe\\openssh-ssh-agent";
  return undefined;
}

function normalizeFingerprint(value) {
  return String(value ?? "")
    .replace(/^SHA256:/i, "")
    .replace(/=+$/, "");
}

function fingerprintOf(key) {
  return crypto
    .createHash("sha256")
    .update(key)
    .digest("base64")
    .replace(/=+$/, "");
}

function verifyHostKey(expected, key) {
  if (!expected) return true;
  return fingerprintOf(key) === normalizeFingerprint(expected);
}

function connectHop(hop, sock, ClientClass) {
  return new Promise((resolve, reject) => {
    const client = new ClientClass();
    let settled = false;
    let observed = null;
    const fail = (error) => {
      if (settled) return;
      settled = true;
      try {
        client.end();
      } catch {
        // already closed
      }
      reject(error);
    };
    client.once("ready", () => {
      if (settled) return;
      settled = true;
      resolve({ client, fingerprint: observed });
    });
    client.once("error", fail);
    client.once("close", () => {
      if (!settled) fail(new Error("Connessione SSH chiusa."));
    });
    try {
      client.connect({
        host: hop.host,
        port: hop.port || 22,
        username: hop.username,
        agent: agentPath(),
        sock: sock || undefined,
        readyTimeout: 15_000,
        keepaliveInterval: 15_000,
        keepaliveCountMax: 3,
        hostVerifier: (key) => {
          observed = fingerprintOf(key);
          return verifyHostKey(hop.hostKeyFingerprint, key);
        },
      });
    } catch (error) {
      fail(error);
    }
  });
}

function forwardOut(client, host, port) {
  return new Promise((resolve, reject) => {
    client.forwardOut("127.0.0.1", 0, host, port, (error, stream) =>
      error ? reject(error) : resolve(stream),
    );
  });
}

/**
 * Connects to the target through the nearest-first jump chain. Every hop uses
 * the user's SSH agent; no private key is read or stored.
 */
async function connectTarget(target, ClientClass = Client) {
  const clients = [];
  let sock = null;
  let fingerprint = null;
  try {
    const hops = [...(target.jumps || []), target];
    for (let index = 0; index < hops.length; index += 1) {
      const hop = hops[index];
      const connected = await connectHop(hop, sock, ClientClass);
      clients.push(connected.client);
      if (index === hops.length - 1) fingerprint = connected.fingerprint;
      if (index < hops.length - 1) {
        const next = hops[index + 1];
        sock = await forwardOut(
          connected.client,
          next.host,
          next.port || 22,
        );
      }
    }
    return {
      client: clients[clients.length - 1],
      fingerprint,
      dispose() {
        for (const item of clients) {
          try {
            item.end();
          } catch {
            // already closed
          }
        }
      },
    };
  } catch (error) {
    for (const item of clients) {
      try {
        item.end();
      } catch {
        // already closed
      }
    }
    throw error;
  }
}

async function openShell(
  target,
  { cols = 100, rows = 30 } = {},
  ClientClass = Client,
) {
  const connection = await connectTarget(target, ClientClass);
  try {
    const stream = await new Promise((resolve, reject) => {
      connection.client.shell(
        { term: "xterm-256color", cols, rows },
        (error, result) => (error ? reject(error) : resolve(result)),
      );
    });
    return {
      stream,
      fingerprint: connection.fingerprint,
      resize(nextCols, nextRows) {
        try {
          stream.setWindow(nextRows, nextCols, 0, 0);
        } catch {
          // terminal already closed
        }
      },
      close() {
        try {
          stream.end();
        } catch {
          // already closed
        }
        connection.dispose();
      },
    };
  } catch (error) {
    connection.dispose();
    throw error;
  }
}

async function runCommand(
  target,
  script,
  { timeoutSeconds = 300 } = {},
  ClientClass = Client,
) {
  const connection = await connectTarget(target, ClientClass);
  return new Promise((resolve, reject) => {
    connection.client.exec(script, (error, stream) => {
      if (error) {
        connection.dispose();
        reject(error);
        return;
      }
      let output = "";
      let settled = false;
      const finish = (result) => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        connection.dispose();
        resolve(result);
      };
      const fail = (failure) => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        connection.dispose();
        reject(failure);
      };
      const timer = setTimeout(() => {
        try {
          stream.signal("TERM");
        } catch {
          // the channel is already gone
        }
        finish({
          exitCode: null,
          output,
          timedOut: true,
          fingerprint: connection.fingerprint,
        });
      }, Math.max(1, Number(timeoutSeconds) || 300) * 1000);
      stream.on("data", (chunk) => {
        output += String(chunk);
      });
      stream.stderr.on("data", (chunk) => {
        output += String(chunk);
      });
      stream.on("close", (code) =>
        finish({
          exitCode: typeof code === "number" ? code : null,
          output,
          timedOut: false,
          fingerprint: connection.fingerprint,
        }),
      );
      stream.on("error", fail);
    });
  });
}

async function openSftp(target, ClientClass = Client) {
  const connection = await connectTarget(target, ClientClass);
  try {
    const sftp = await new Promise((resolve, reject) => {
      connection.client.sftp((error, result) =>
        error ? reject(error) : resolve(result),
      );
    });
    return {
      sftp,
      fingerprint: connection.fingerprint,
      close() {
        try {
          sftp.end();
        } catch {
          // already closed
        }
        connection.dispose();
      },
    };
  } catch (error) {
    connection.dispose();
    throw error;
  }
}

function remoteJoin(directory, name) {
  if (directory === "/") return `/${name}`;
  return `${directory.replace(/\/$/, "")}/${name}`;
}

function listEntry(directory, entry) {
  const name = entry.filename;
  if (name === "." || name === "..") return null;
  const attrs = entry.attrs || {};
  const isDirectory = attrs.isDirectory
    ? attrs.isDirectory()
    : (Number(attrs.mode) & 0o170000) === 0o040000;
  return {
    name,
    path: remoteJoin(directory, name),
    isDirectory,
    size: isDirectory ? 0 : Number(attrs.size) || 0,
    modifiedUtc: attrs.mtime
      ? new Date(Number(attrs.mtime) * 1000).toISOString()
      : null,
  };
}

async function listDirectory(sftp, path) {
  const directory = await new Promise((resolve, reject) => {
    if (path) {
      resolve(path);
      return;
    }
    sftp.realpath(".", (error, resolved) =>
      error ? reject(error) : resolve(resolved),
    );
  });
  const entries = await new Promise((resolve, reject) => {
    sftp.readdir(directory, (error, result) =>
      error ? reject(error) : resolve(result || []),
    );
  });
  return {
    path: directory,
    items: entries
      .map((entry) => listEntry(directory, entry))
      .filter(Boolean)
      .sort((left, right) => {
        if (left.isDirectory !== right.isDirectory)
          return left.isDirectory ? -1 : 1;
        return left.name.localeCompare(right.name, undefined, {
          sensitivity: "base",
        });
      }),
  };
}

function downloadFile(sftp, remotePath, localPath) {
  return new Promise((resolve, reject) => {
    sftp.fastGet(remotePath, localPath, {}, (error) =>
      error ? reject(error) : resolve(localPath),
    );
  });
}

function uploadFile(sftp, remotePath, buffer) {
  return new Promise((resolve, reject) => {
    sftp.writeFile(remotePath, buffer, (error) =>
      error ? reject(error) : resolve(remotePath),
    );
  });
}

function createDirectory(sftp, path) {
  return new Promise((resolve, reject) => {
    sftp.mkdir(path, (error) => (error ? reject(error) : resolve(path)));
  });
}

function renameFile(sftp, path, newPath) {
  return new Promise((resolve, reject) => {
    sftp.rename(path, newPath, (error) =>
      error ? reject(error) : resolve(newPath),
    );
  });
}

function deleteFile(sftp, path, isDirectory) {
  return new Promise((resolve, reject) => {
    const done = (error) => (error ? reject(error) : resolve());
    if (isDirectory) sftp.rmdir(path, done);
    else sftp.unlink(path, done);
  });
}

module.exports = {
  agentPath,
  connectTarget,
  createDirectory,
  deleteFile,
  downloadFile,
  fingerprintOf,
  listDirectory,
  normalizeFingerprint,
  openSftp,
  openShell,
  remoteJoin,
  renameFile,
  runCommand,
  uploadFile,
  verifyHostKey,
};
