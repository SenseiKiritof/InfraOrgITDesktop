const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const net = require("node:net");
const { EventEmitter } = require("node:events");

const {
  buildJumpArgs,
  normalizeCheck,
  runLocalCheck,
} = require("../../local-checks");

async function listen(server) {
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return server.address().port;
}

test("normalizeCheck validates the target and jump", () => {
  assert.deepEqual(
    normalizeCheck({ kind: "tcp", host: "example.com", port: "443" }),
    {
      kind: "tcp",
      source: "local",
      host: "example.com",
      port: 443,
      path: null,
      secure: false,
      timeoutMs: 5000,
      jump: null,
    },
  );
  assert.throws(() => normalizeCheck({ host: "bad host", port: 1 }), /Host/);
  assert.throws(() => normalizeCheck({ host: "a", port: 0 }), /Porta/);
  assert.throws(
    () =>
      normalizeCheck({
        kind: "http",
        source: "jump",
        host: "a",
        port: 80,
        jump: { host: "j", port: 22, username: "u" },
      }),
    /jump host/,
  );
});

test("local TCP and HTTP checks report latency", async () => {
  const tcp = net.createServer((socket) => socket.end());
  const tcpPort = await listen(tcp);
  const httpServer = http.createServer((request, response) => {
    response.writeHead(204);
    response.end();
  });
  const httpPort = await listen(httpServer);
  try {
    const tcpResult = await runLocalCheck({
      kind: "tcp",
      host: "127.0.0.1",
      port: tcpPort,
    });
    assert.equal(tcpResult.ok, true);
    assert.equal(tcpResult.kind, "tcp");

    const httpResult = await runLocalCheck({
      kind: "http",
      host: "127.0.0.1",
      port: httpPort,
      path: "/",
    });
    assert.equal(httpResult.ok, true);
    assert.equal(httpResult.status, 204);
  } finally {
    tcp.close();
    httpServer.close();
  }
});

test("jump checks use ssh -W and finish on a live channel", async () => {
  const args = buildJumpArgs(
    { host: "jump.example.com", port: 2222, username: "ops" },
    "10.0.0.5",
    443,
  );
  assert.deepEqual(args.slice(-5), [
    "-p",
    "2222",
    "-W",
    "10.0.0.5:443",
    "ops@jump.example.com",
  ]);
  assert.equal(args.at(-1), "ops@jump.example.com");

  const child = new EventEmitter();
  child.stderr = new EventEmitter();
  child.kill = () => {};
  const result = await runLocalCheck(
    {
      kind: "tcp",
      source: "jump",
      host: "10.0.0.5",
      port: 443,
      jump: { host: "jump.example.com", port: 22, username: "ops" },
    },
    { spawnProcess: () => child, jumpConnectMs: 20 },
  );
  assert.equal(result.ok, true);
  assert.equal(result.source, "jump");
});
