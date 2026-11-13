const test = require("node:test");
const assert = require("node:assert/strict");

const {
  CLIENT_ID,
  SCOPE,
  DeviceAuthClient,
  DeviceAuthError,
} = require("../../auth/device-client");

function response(status, body, headers = {}) {
  const text = typeof body === "string" ? body : JSON.stringify(body ?? {});
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get: (name) => headers[name.toLowerCase()] || null,
      getSetCookie: () => headers["set-cookie"] || [],
    },
    text: async () => text,
    json: async () => (text ? JSON.parse(text) : null),
  };
}

function recordingFetch(handler) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    const form = init.body ? new URLSearchParams(init.body) : null;
    calls.push({ url, init, form });
    return handler(url, init, calls.length - 1);
  };
  fetch.calls = calls;
  return fetch;
}

test("discoverIssuer follows the protected-resource metadata", async () => {
  const fetch = recordingFetch(async () =>
    response(200, {
      authorization_servers: ["https://auth.example.com/realms/infraorgit"],
    }),
  );
  const client = new DeviceAuthClient({ fetchImpl: fetch });
  assert.equal(
    await client.discoverIssuer("https://infra.example.com"),
    "https://auth.example.com/realms/infraorgit",
  );
  assert.equal(fetch.calls.length, 1);
  assert.equal(
    fetch.calls[0].url,
    "https://infra.example.com/.well-known/oauth-protected-resource",
  );
});

test("discoverIssuer falls back to the local realm and refuses remote http", async () => {
  const fetch = recordingFetch(async () => response(200, {}));
  const client = new DeviceAuthClient({ fetchImpl: fetch });
  assert.equal(
    await client.discoverIssuer("https://localhost:8443"),
    "https://localhost:8443/realms/infraorgit",
  );
  await assert.rejects(
    () => client.discoverIssuer("http://remote.example.com"),
    (error) => error instanceof DeviceAuthError && error.code === "insecure_url",
  );
});

test("start sends the desktop client, scopes and PKCE challenge", async () => {
  const fetch = recordingFetch(async (url) => {
    if (url.includes("oauth-protected-resource"))
      return response(200, {
        authorization_servers: ["https://auth.example.com/realms/infraorgit"],
      });
    return response(200, {
      device_code: "device-1",
      user_code: "ABCD-EFGH",
      verification_uri: "https://auth.example.com/device",
      verification_uri_complete: "https://auth.example.com/device?user_code=ABCD-EFGH",
      expires_in: 600,
      interval: 5,
    });
  });
  const client = new DeviceAuthClient({ fetchImpl: fetch });
  const authorization = await client.start("https://infra.example.com");

  assert.equal(fetch.calls[1].url, "https://auth.example.com/realms/infraorgit/protocol/openid-connect/auth/device");
  assert.equal(fetch.calls[1].form.get("client_id"), CLIENT_ID);
  assert.equal(fetch.calls[1].form.get("scope"), SCOPE);
  assert.equal(fetch.calls[1].form.get("code_challenge_method"), "S256");
  assert.equal(fetch.calls[1].form.get("code_challenge").length, 43);
  assert.equal(authorization.userCode, "ABCD-EFGH");
  assert.equal(authorization.codeVerifier.length, 43);
  assert.match(authorization.verificationUriComplete, /user_code/);
});

test("poll waits, sends the verifier and accepts the first token", async () => {
  let polls = 0;
  const delays = [];
  const fetch = recordingFetch(async () => {
    polls += 1;
    if (polls === 1) return response(400, { error: "authorization_pending" });
    return response(200, {
      access_token: "access-1",
      refresh_token: "refresh-1",
      expires_in: 300,
    });
  });
  const client = new DeviceAuthClient({
    fetchImpl: fetch,
    delay: async (ms) => delays.push(ms),
    now: () => 1_000,
  });
  const authorization = {
    issuer: "https://auth.example.com/realms/infraorgit",
    deviceCode: "device-1",
    intervalSeconds: 2,
    expiresAt: 10_000,
    codeVerifier: "verifier-1",
  };
  const tokens = await client.poll(authorization);
  assert.equal(tokens.accessToken, "access-1");
  assert.equal(tokens.refreshToken, "refresh-1");
  assert.equal(tokens.expiresAt, 301_000);
  assert.deepEqual(delays, [2000, 2000]);
  assert.equal(fetch.calls[0].form.get("code_verifier"), "verifier-1");
  assert.equal(
    fetch.calls[0].form.get("grant_type"),
    "urn:ietf:params:oauth:grant-type:device_code",
  );
});

test("poll honours slow_down and expiry", async () => {
  let polls = 0;
  let clock = 1_000;
  const delays = [];
  const fetch = recordingFetch(async () => {
    polls += 1;
    if (polls === 1) return response(400, { error: "slow_down" });
    return response(400, { error: "authorization_pending" });
  });
  const client = new DeviceAuthClient({
    fetchImpl: fetch,
    delay: async (ms) => {
      delays.push(ms);
      clock += ms;
    },
    now: () => clock,
  });
  await assert.rejects(
    () =>
      client.poll({
        issuer: "https://auth.example.com/realms/infraorgit",
        deviceCode: "device-1",
        intervalSeconds: 2,
        expiresAt: 4_000,
        codeVerifier: "verifier-1",
      }),
    (error) => error.code === "expired_token",
  );
  assert.deepEqual(delays, [2000, 7000]);
});

test("poll propagates access_denied without leaking secrets", async () => {
  const fetch = recordingFetch(async () =>
    response(400, { error: "access_denied" }),
  );
  const client = new DeviceAuthClient({
    fetchImpl: fetch,
    delay: async () => {},
    now: () => 1_000,
  });
  await assert.rejects(
    () =>
      client.poll({
        issuer: "https://auth.example.com/realms/infraorgit",
        deviceCode: "device-secret",
        intervalSeconds: 1,
        expiresAt: 10_000,
        codeVerifier: "verifier-secret",
      }),
    (error) => {
      assert.equal(error.code, "access_denied");
      assert.ok(!error.message.includes("device-secret"));
      assert.ok(!error.message.includes("verifier-secret"));
      return true;
    },
  );
});

test("refresh uses the device grant and keeps a rotated token", async () => {
  const fetch = recordingFetch(async (url) => {
    if (url.includes("oauth-protected-resource"))
      return response(200, {
        authorization_servers: ["https://auth.example.com/realms/infraorgit"],
      });
    return response(200, {
      access_token: "access-2",
      refresh_token: "refresh-2",
      expires_in: 60,
    });
  });
  const client = new DeviceAuthClient({ fetchImpl: fetch, now: () => 5_000 });
  const tokens = await client.refresh("https://infra.example.com", "refresh-1");
  assert.equal(tokens.accessToken, "access-2");
  assert.equal(tokens.refreshToken, "refresh-2");
  assert.equal(tokens.expiresAt, 65_000);
  const form = fetch.calls[1].form;
  assert.equal(form.get("grant_type"), "refresh_token");
  assert.equal(form.get("client_id"), CLIENT_ID);
  assert.equal(form.get("refresh_token"), "refresh-1");
});

test("refresh reports an invalid grant with a re-login message", async () => {
  const fetch = recordingFetch(async (url) => {
    if (url.includes("oauth-protected-resource"))
      return response(200, {
        authorization_servers: ["https://auth.example.com/realms/infraorgit"],
      });
    return response(400, { error: "invalid_grant" });
  });
  const client = new DeviceAuthClient({ fetchImpl: fetch });
  await assert.rejects(
    () => client.refresh("https://infra.example.com", "stale"),
    (error) =>
      error.code === "invalid_grant" &&
      error.message.includes("Accedi di nuovo"),
  );
});
