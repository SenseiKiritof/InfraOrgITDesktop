const test = require("node:test");
const assert = require("node:assert/strict");

const {
  TunnelProjection,
  cleanDetail,
  eventPayload,
  sessionPayload,
} = require("../../tunnel-projection");

function row(overrides = {}) {
  return {
    id: "tunnel-1",
    url: "https://infra.example.com",
    serverId: 4,
    serverName: "srv",
    targetId: 7,
    serviceId: null,
    mode: "Local",
    targetHost: "127.0.0.1",
    targetPort: 8080,
    localPort: 18080,
    remotePort: null,
    error: null,
    status: "Active",
    ...overrides,
  };
}

test("payloads carry the tunnel target and redact bearer details", () => {
  const event = eventPayload(row(), "error", {
    errorCode: "ssh_failed",
    detail: "failed with Bearer secret-token",
  });
  assert.equal(event.kind, "error");
  assert.equal(event.serverId, 4);
  assert.equal(event.serverApplicationId, 7);
  assert.equal(event.serviceId, null);
  assert.equal(event.correlationId, "tunnel-1");
  assert.equal(event.detail, "failed with Bearer ***");

  const session = sessionPayload(row({ serviceId: 9, mode: "Remote" }), "active");
  assert.equal(session.sessionKey, "tunnel-1");
  assert.equal(session.tunnelServiceId, 9);
  assert.equal(session.mode, "Remote");
  assert.equal(session.status, "active");
  assert.equal(cleanDetail("  "), null);
});

test("sync posts start, active heartbeats, and stop without duplicates", async () => {
  const calls = [];
  const timers = [];
  const cleared = [];
  const projection = new TunnelProjection({
    post: async (_row, kind, payload) => {
      calls.push({ kind, payload });
      return true;
    },
    setIntervalFn: () => {
      const timer = { unref: () => {} };
      timers.push(timer);
      return timer;
    },
    clearIntervalFn: (timer) => cleared.push(timer),
    logger: { warn: () => {} },
  });
  const flush = () => new Promise((resolve) => setImmediate(resolve));

  projection.sync([row()]);
  await flush();
  assert.deepEqual(
    calls.map((call) => `${call.kind}:${call.payload.kind || call.payload.status}`),
    ["event:start", "session:active"],
  );
  assert.equal(timers.length, 1);

  projection.sync([row()]);
  await flush();
  assert.deepEqual(
    calls.map((call) => `${call.kind}:${call.payload.kind || call.payload.status}`),
    ["event:start", "session:active"],
  );

  projection.sync([row({ status: "Stopped", error: "ssh exited" })]);
  await flush();
  assert.deepEqual(
    calls.map(
      (call) => `${call.kind}:${call.payload.kind || call.payload.status}`,
    ),
    ["event:start", "session:active", "event:error", "session:error"],
  );
  assert.equal(cleared.length, 1);
  assert.equal(projection.states.size, 0);

  projection.sync([]);
  await flush();
  assert.equal(calls.length, 4);
  projection.dispose();
});

test("sync posts a stop when the tunnel closes cleanly", async () => {
  const calls = [];
  const projection = new TunnelProjection({
    post: async (_row, kind, payload) => {
      calls.push({ kind, payload });
      return true;
    },
    setIntervalFn: () => ({ unref: () => {} }),
    clearIntervalFn: () => {},
    logger: { warn: () => {} },
  });

  projection.sync([row()]);
  await new Promise((resolve) => setImmediate(resolve));
  projection.sync([row({ status: "Stopped" })]);
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(
    calls.filter((call) => call.payload.kind === "stop").length,
    1,
  );
  assert.equal(
    calls.filter((call) => call.payload.status === "stopped").length,
    1,
  );
  projection.dispose();
});
