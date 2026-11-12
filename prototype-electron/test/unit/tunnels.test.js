const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const { TunnelManager, buildSshArgs } = require("../../tunnels");

const fakeSsh = path.join(__dirname, "..", "fake-ssh.js");
process.env.SSH_BIN = fakeSsh;

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
  targetHost: "127.0.0.1",
  targetPort: 8081,
  suggestedLocalPort: 8081,
};

test("ssh arguments keep the existing local, dynamic and remote contract", () => {
  const args = buildSshArgs(server, target, null, 8081);
  assert.ok(args.includes("-L"));
  assert.ok(args.includes("127.0.0.1:8081:127.0.0.1:8081"));
  assert.ok(args.includes("-J"));
  assert.ok(args.includes("bastion@10.0.0.5:22"));

  const dynamic = buildSshArgs(
    server,
    null,
    { id: 8, mode: "Dynamic", name: "socks", kind: "Tcp" },
    1080,
  );
  assert.ok(dynamic.includes("-D"));
  assert.ok(dynamic.includes("127.0.0.1:1080"));

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
  assert.ok(remote.includes("-R"));
  assert.ok(remote.includes("9000:127.0.0.1:8081"));
});

test("tunnel manager rejects duplicate local ports and keeps stopped rows", async () => {
  const manager = new TunnelManager(() => {});
  const first = manager.start({
    id: "t1",
    url: "https://infra.example.com",
    instanceName: "Produzione",
    server,
    target,
    service: null,
    localPort: 8081,
  });
  assert.equal(first.status, "Active");
  assert.equal(first.ip, "10.0.0.10");
  assert.equal(manager.active().length, 1);

  assert.throws(
    () =>
      manager.start({
        id: "t2",
        url: "https://other.example.com",
        instanceName: "Staging",
        server,
        target,
        service: null,
        localPort: 8081,
      }),
    /già usata/,
  );

  assert.equal(manager.stop("t1", "https://other.example.com"), false);
  assert.equal(manager.stop("t1", "https://infra.example.com"), true);
  const stopped = manager.list();
  assert.equal(stopped.length, 1);
  assert.equal(stopped[0].status, "Stopped");

  const restarted = manager.restart("t1", "https://infra.example.com");
  assert.equal(restarted.status, "Active");
  assert.equal(restarted.localPort, 8081);
  manager.stopAll();
  manager.forget("t1");
  assert.equal(manager.list().length, 0);
});

test("tunnel manager rejects invalid ports and missing targets", async () => {
  const manager = new TunnelManager(() => {});
  assert.throws(
    () =>
      manager.start({
        id: "bad-port",
        url: "https://infra.example.com",
        server,
        target,
        localPort: 80,
      }),
    /1024/,
  );
  assert.throws(
    () =>
      manager.start({
        id: "no-port",
        url: "https://infra.example.com",
        server,
        target: null,
        service: { id: 1, mode: "Local", kind: "Tcp", targetPort: null },
        localPort: null,
      }),
    /porta|Target/i,
  );

  const remote = manager.start({
    id: "remote",
    url: "https://infra.example.com",
    server,
    target: null,
    service: {
      id: 9,
      name: "remote",
      mode: "Remote",
      kind: "Tcp",
      remotePort: 9000,
      targetHost: "127.0.0.1",
      targetPort: 8081,
    },
    localPort: null,
  });
  assert.equal(remote.localPort, null);
  manager.stopAll();
});
