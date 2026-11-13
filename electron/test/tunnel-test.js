const assert = require("assert");
const fs = require("fs");
const path = require("path");

const log = path.join(__dirname, ".run", "ssh.log");
fs.mkdirSync(path.dirname(log), { recursive: true });
fs.writeFileSync(log, "");
process.env.FAKE_SSH_LOG = log;
process.env.SSH_BIN = path.join(__dirname, "fake-ssh.js");

const { TunnelManager, buildSshArgs } = require("../tunnels");

const server = {
  serverId: 1,
  serverName: "demo-app-01",
  sshHost: "10.0.0.10",
  sshPort: 22,
  username: "demo",
  jumps: [{ host: "10.0.0.5", port: 22, username: "bastion" }],
};
const target = {
  serverApplicationId: 7,
  application: "orders-api",
  version: "2.4.1",
  targetHost: "127.0.0.1",
  targetPort: 8081,
  suggestedLocalPort: 8081,
};

const args = buildSshArgs(server, target, null, 8081);
assert(args.includes("-L"));
assert(args.includes("127.0.0.1:8081:127.0.0.1:8081"));
assert(args.includes("-J"));
assert(args.includes("bastion@10.0.0.5:22"));

const dynamic = buildSshArgs(
  server,
  null,
  { id: 8, mode: "Dynamic", name: "socks", kind: "Tcp" },
  1080,
);
assert(dynamic.includes("-D"));
assert(dynamic.includes("127.0.0.1:1080"));

const remote = buildSshArgs(
  server,
  null,
  {
    id: 9,
    mode: "Remote",
    name: "remote",
    kind: "Tcp",
    remotePort: 9000,
    targetHost: "127.0.0.1",
    targetPort: 8081,
  },
  null,
);
assert(remote.includes("-R"));
assert(remote.includes("9000:127.0.0.1:8081"));

async function main() {
  const manager = new TunnelManager(() => {});
  const started = await manager.start({
    id: "test-1",
    url: "https://localhost:8443",
    instanceName: "Locale",
    server,
    target,
    service: null,
    localPort: 8081,
  });
  assert.strictEqual(manager.list().length, 1);
  assert.strictEqual(manager.list()[0].status, "Active");
  assert.strictEqual(started.localPort, 8081);

  await assert.rejects(
    manager.start({
      id: "test-duplicate",
      url: "https://localhost:8443",
      instanceName: "Locale",
      server,
      target,
      service: null,
      localPort: 8081,
    }),
    /già usata/,
  );

  await new Promise((resolve) => setTimeout(resolve, 800));
  const lines = fs.readFileSync(log, "utf8").trim().split("\n").filter(Boolean);
  assert(lines.length >= 1, "fake ssh did not receive arguments");
  const received = JSON.parse(lines[0]);
  assert(received.includes("-L"));
  assert(received.includes("127.0.0.1:8081:127.0.0.1:8081"));
  manager.stop("test-1");
  assert.strictEqual(manager.list().length, 1);
  assert.strictEqual(manager.list()[0].status, "Stopped");
  manager.forget("test-1");
  assert.strictEqual(manager.list().length, 0);
  console.log("TUNNEL_RESULT PASS");
  console.log("SSH_ARGS", received.join(" "));
}

main().then(
  () => process.exit(0),
  (error) => {
    console.error("TUNNEL_RESULT FAIL", error.message);
    process.exit(2);
  },
);
