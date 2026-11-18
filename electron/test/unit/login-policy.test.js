const test = require("node:test");
const assert = require("node:assert/strict");

const { planDeviceLogin } = require("../../auth/login-policy");

test("device flow is the default login path", () => {
  assert.equal(planDeviceLogin({ mode: "none" }), "inline");
  assert.equal(planDeviceLogin({ mode: "expired" }), "inline");
  assert.equal(planDeviceLogin({ mode: "revoked" }), "inline");
  assert.equal(planDeviceLogin({ mode: "session" }), "background");
  assert.equal(
    planDeviceLogin({ mode: "session", saved: true }),
    null,
  );
  assert.equal(planDeviceLogin({ mode: "silent" }), null);
  assert.equal(planDeviceLogin({ mode: "error" }), null);
  assert.equal(planDeviceLogin({ mode: "fallback" }), null);
  assert.equal(planDeviceLogin({ mode: "none", loginMode: "web" }), null);
  assert.equal(planDeviceLogin({ mode: "none", attempted: true }), null);
});
