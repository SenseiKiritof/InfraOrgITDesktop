const test = require("node:test");
const assert = require("node:assert/strict");
const net = require("node:net");
const { EventEmitter } = require("node:events");
const { WebSocketServer } = require("ws");

const {
  TunnelManager,
  buildSshArgs,
  canRelay,
  chooseTransport,
  relayUrl,
} = require("../../tunnels");

function fakeManager(options = {}) {
  return new TunnelManager(() => {}, {
    spawnProcess: () => {
      const child = new EventEmitter();
      child.stderr = new EventEmitter();
      child.kill = () => {};
      return child;
    },
    ...options,
  });
}

async function freePort() {
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  await new Promise((resolve) => server.close(resolve));
  return port;
}

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
const service = {
  id: 9,
  name: "web",
  mode: "Local",
  kind: "Tcp",
  targetHost: "127.0.0.1",
  targetPort: 8081,
  localPort: 18099,
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

test("transport policy picks client, server or relayable service", () => {
  assert.equal(chooseTransport("Client", true, false), "client");
  assert.equal(chooseTransport("Server", true, true), "server");
  assert.equal(chooseTransport("Auto", true, true), "client");
  assert.equal(chooseTransport("Auto", true, false), "server");
  assert.equal(chooseTransport("Auto", false, false), "client");
  assert.throws(() => chooseTransport("Server", false, false), /Local/);

  assert.equal(canRelay(service, null), true);
  assert.equal(canRelay({ ...service, mode: "Dynamic" }, null), false);
  assert.equal(canRelay(null, target), true);
  assert.equal(canRelay(null, { serverApplicationId: 7 }), false);
  assert.equal(canRelay(null, null), false);
  assert.equal(
    relayUrl("https://infra.example.com", 1, { serviceId: 9 }, "t1"),
    "wss://infra.example.com/api/tunnels/relay?serverId=1&serviceId=9&tunnelId=t1",
  );
  assert.equal(
    relayUrl(
      "https://infra.example.com",
      1,
      { serverApplicationId: 7 },
      "t1",
    ),
    "wss://infra.example.com/api/tunnels/relay?serverId=1&serverApplicationId=7&tunnelId=t1",
  );
});

test("tunnel manager rejects duplicate local ports and keeps stopped rows", async () => {
  const manager = fakeManager();
  const first = await manager.start({
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
  assert.equal(first.transport, "Client");
  assert.equal(manager.active().length, 1);

  await assert.rejects(
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

  const restarted = await manager.restart(
    "t1",
    "https://infra.example.com",
  );
  assert.equal(restarted.status, "Active");
  assert.equal(restarted.localPort, 8081);
  manager.stopAll();
  manager.forget("t1");
  assert.equal(manager.list().length, 0);
});

test("tunnel manager rejects invalid ports and missing targets", async () => {
  const manager = fakeManager();
  await assert.rejects(
    manager.start({
      id: "bad-port",
      url: "https://infra.example.com",
      server,
      target,
      localPort: 80,
    }),
    /1024/,
  );
  await assert.rejects(
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

  const remote = await manager.start({
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

test("server relay bridges a local TCP connection over WebSocket", async () => {
  const wss = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await new Promise((resolve) => wss.once("listening", resolve));
  const wsPort = wss.address().port;
  wss.on("connection", (socket) =>
    socket.on("message", (data) => socket.send(data)),
  );

  const localPort = await freePort();
  const manager = fakeManager({
    relayTarget: async () => ({
      url: `ws://127.0.0.1:${wsPort}`,
      headers: {},
    }),
  });
  try {
    const row = await manager.start({
      id: "relay",
      url: "https://infra.example.com",
      server,
      target: null,
      service,
      localPort,
      transport: "server",
    });
    assert.equal(row.transport, "Server");

    const echoed = await new Promise((resolve, reject) => {
      const socket = net.connect({ host: "127.0.0.1", port: localPort });
      const timer = setTimeout(() => reject(new Error("timeout")), 3000);
      timer.unref?.();
      socket.once("connect", () => socket.write("hello"));
      socket.once("data", (data) => {
        clearTimeout(timer);
        socket.destroy();
        resolve(String(data));
      });
      socket.once("error", reject);
    });
    assert.equal(echoed, "hello");

    await assert.rejects(
      manager.start({
        id: "dynamic",
        url: "https://infra.example.com",
        server,
        target: null,
        service: { ...service, mode: "Dynamic" },
        localPort: await freePort(),
        transport: "server",
      }),
      /Local/,
    );
  } finally {
    manager.stopAll();
    await new Promise((resolve) => wss.close(resolve));
  }
});
