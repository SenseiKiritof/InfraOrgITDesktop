const test = require("node:test");
const assert = require("node:assert/strict");

const { DesktopAuth } = require("../../auth/desktop-auth");
const { DeviceAuthError } = require("../../auth/device-client");

const instance = { url: "https://infra.example.com", name: "Produzione" };

function response(status, body = {}, headers = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get: (name) => headers[name.toLowerCase()] || null,
      getSetCookie: () => headers["set-cookie"] || [],
    },
    json: async () => body,
    text: async () => (typeof body === "string" ? body : JSON.stringify(body)),
  };
}

function tokenStore(initial = new Map()) {
  return {
    values: initial,
    get(url) {
      return this.values.get(url) || null;
    },
    set(url, token) {
      this.values.set(url, token);
      return true;
    },
    delete(url) {
      return this.values.delete(url);
    },
  };
}

function fakeDevice(overrides = {}) {
  return {
    refreshCalls: [],
    startCalls: [],
    async refresh(instanceUrl, refreshToken) {
      this.refreshCalls.push({ instanceUrl, refreshToken });
      return {
        accessToken: "access-new",
        refreshToken: "refresh-new",
        expiresAt: Date.now() + 300_000,
      };
    },
    async start(instanceUrl) {
      this.startCalls.push(instanceUrl);
      return {
        issuer: "https://infra.example.com/realms/infraorgit",
        deviceCode: "device-secret",
        userCode: "ABCD-EFGH",
        verificationUri: "https://infra.example.com/device",
        verificationUriComplete: "https://infra.example.com/device?user_code=ABCD-EFGH",
        intervalSeconds: 1,
        expiresIn: 600,
        expiresAt: Date.now() + 600_000,
        codeVerifier: "verifier-secret",
      };
    },
    async poll() {
      return {
        accessToken: "access-device",
        refreshToken: "refresh-device",
        expiresAt: Date.now() + 300_000,
      };
    },
    ...overrides,
  };
}

function sessionFetch({
  exchangeStatus = 204,
  accountTheme = "dark",
  accountStatus = 200,
} = {}) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, init });
    if (url.endsWith("/auth/desktop-session")) {
      return response(exchangeStatus, exchangeStatus === 204 ? {} : {
        code: "desktop_session_invalid",
        detail: "Sessione desktop non valida",
      }, {
        "set-cookie": ["__Host-infraorgit_session=session-1; Path=/; Secure"],
      });
    }
    if (url.endsWith("/api/session")) {
      if (accountStatus !== 200)
        return response(accountStatus, { code: "unauthorized" });
      return response(200, {
        user: { username: "admin", name: "Ada", surname: "Lovelace" },
        theme: accountTheme,
        csrfToken: "csrf-1",
        expiresIn: 240,
      });
    }
    return response(404, {});
  };
  fetch.calls = calls;
  return fetch;
}

function cookies() {
  const sets = [];
  return {
    sets,
    get: async () => [],
    set: async (cookie) => sets.push(cookie),
  };
}

test("restore refreshes Keycloak, exchanges the BFF session and hides secrets", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const device = fakeDevice();
  const fetch = sessionFetch();
  const cookieJar = cookies();
  const changes = [];
  const auth = new DesktopAuth({
    deviceClient: device,
    tokenStore: store,
    openExternal: async () => {},
    onChange: (url, snapshot) => changes.push(snapshot),
    logger: { warn: () => {} },
  });

  const result = await auth.restore(instance, fetch, cookieJar);
  assert.equal(result.mode, "silent");
  assert.deepEqual(device.refreshCalls, [
    { instanceUrl: instance.url, refreshToken: "refresh-old" },
  ]);
  const exchange = fetch.calls.find((call) =>
    call.url.endsWith("/auth/desktop-session"),
  );
  assert.deepEqual(JSON.parse(exchange.init.body), {
    refreshToken: "refresh-new",
  });
  assert.equal(store.values.get(instance.url), "refresh-new");

  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "ready");
  assert.equal(snapshot.method, "session");
  assert.equal(snapshot.tunnelAccess, true);
  assert.equal(snapshot.theme, "dark");
  assert.equal(snapshot.user.username, "admin");
  const serialized = JSON.stringify(snapshot);
  assert.ok(!serialized.includes("access-new"));
  assert.ok(!serialized.includes("refresh-new"));
  assert.ok(!serialized.includes("csrf-1"));
  assert.ok(!serialized.includes("refresh-old"));
  assert.ok(changes.some((change) => change.status === "restoring"));
  assert.equal(cookieJar.sets.length, 1);
  auth.dispose();
});

test("restore reuses the BFF session without a device token", async () => {
  const store = tokenStore();
  const device = fakeDevice();
  const auth = new DesktopAuth({
    deviceClient: device,
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });

  const result = await auth.restore(instance, sessionFetch(), cookies());
  assert.equal(result.mode, "session");
  assert.deepEqual(device.refreshCalls, []);
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "ready");
  assert.equal(snapshot.method, "session");
  assert.equal(snapshot.tunnelAccess, true);
  assert.equal(snapshot.requiresLogin, false);
  assert.equal(snapshot.saved, false);
  auth.dispose();
});

test("restore falls back to the BFF session when the refresh token is revoked", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice({
      async refresh() {
        throw new DeviceAuthError(
          "La sessione non è più valida. Accedi di nuovo.",
          "invalid_grant",
        );
      },
    }),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });

  const result = await auth.restore(instance, sessionFetch(), cookies());
  assert.equal(result.mode, "session");
  assert.equal(store.values.has(instance.url), false);
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "ready");
  assert.equal(snapshot.tunnelAccess, true);
  auth.dispose();
});

test("fetchTargets prefers the BFF session over the device token", async () => {
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, init });
    assert.equal(init.headers.Authorization, undefined);
    return response(200, { items: [{ serverId: 1, serverName: "target" }] });
  };
  const auth = new DesktopAuth({
    deviceClient: fakeDevice({
      async refresh() {
        throw new Error("token refresh must not run");
      },
    }),
    tokenStore: tokenStore(),
    openExternal: async () => {},
    logger: { warn: () => {} },
  });

  const items = await auth.fetchTargets(instance.url, fetch);
  assert.deepEqual(items, [{ serverId: 1, serverName: "target" }]);
  assert.equal(calls.length, 1);
  auth.dispose();
});

test("restore drops the session when the desktop exchange is rejected", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice(),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });
  const result = await auth.restore(
    instance,
    sessionFetch({ exchangeStatus: 401, accountStatus: 401 }),
    cookies(),
  );
  assert.equal(result.mode, "revoked");
  assert.match(result.message, /non è più valida/);
  assert.equal(store.values.has(instance.url), false);
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "none");
  assert.equal(snapshot.tunnelAccess, false);
  auth.dispose();
});

test("restore falls back without forgetting the token when the exchange is unavailable", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice(),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });
  const result = await auth.restore(
    instance,
    sessionFetch({ exchangeStatus: 502 }),
    cookies(),
  );
  assert.equal(result.mode, "fallback");
  assert.equal(store.values.get(instance.url), "refresh-new");
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "error");
  assert.equal(snapshot.tunnelAccess, true);
  auth.dispose();
});

test("restore drops an invalid refresh token and asks for a new login", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice({
      async refresh() {
        throw new DeviceAuthError(
          "La sessione non è più valida. Accedi di nuovo.",
          "invalid_grant",
        );
      },
    }),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });
  const result = await auth.restore(
    instance,
    sessionFetch({ accountStatus: 401 }),
    cookies(),
  );
  assert.equal(result.mode, "expired");
  assert.equal(store.values.has(instance.url), false);
  assert.equal(auth.snapshot(instance.url).status, "none");
  auth.dispose();
});

test("renew refreshes the token and extends the session without a restoring state", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const device = fakeDevice();
  const fetch = sessionFetch();
  const changes = [];
  const auth = new DesktopAuth({
    deviceClient: device,
    tokenStore: store,
    openExternal: async () => {},
    onChange: (_url, snapshot) => changes.push(snapshot),
    logger: { warn: () => {} },
  });

  const result = await auth.renew(instance, fetch, cookies());
  assert.equal(result.mode, "renewed");
  assert.deepEqual(device.refreshCalls, [
    { instanceUrl: instance.url, refreshToken: "refresh-old" },
  ]);
  const exchange = fetch.calls.find((call) =>
    call.url.endsWith("/auth/desktop-session"),
  );
  assert.deepEqual(JSON.parse(exchange.init.body), {
    refreshToken: "refresh-new",
  });
  assert.equal(store.values.get(instance.url), "refresh-new");
  assert.equal(auth.snapshot(instance.url).status, "ready");
  assert.ok(!changes.some((change) => change.status === "restoring"));
  auth.dispose();
});

test("renew drops the session when Keycloak revokes the refresh token", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice({
      async refresh() {
        throw new DeviceAuthError(
          "La sessione non è più valida. Accedi di nuovo.",
          "invalid_grant",
        );
      },
    }),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });

  const result = await auth.renew(
    instance,
    sessionFetch({ accountStatus: 401 }),
    cookies(),
  );
  assert.equal(result.mode, "revoked");
  assert.equal(store.values.has(instance.url), false);
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "none");
  assert.equal(snapshot.requiresLogin, true);
  auth.dispose();
});

test("renew drops the session when the API rejects the desktop exchange", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice(),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });

  const result = await auth.renew(
    instance,
    sessionFetch({ exchangeStatus: 401, accountStatus: 401 }),
    cookies(),
  );
  assert.equal(result.mode, "revoked");
  assert.equal(store.values.has(instance.url), false);
  assert.equal(auth.snapshot(instance.url).status, "none");
  auth.dispose();
});

test("device login opens the browser, stores only the refresh token and reloads the session", async () => {
  const store = tokenStore();
  const device = fakeDevice();
  const opened = [];
  const changes = [];
  const auth = new DesktopAuth({
    deviceClient: device,
    tokenStore: store,
    openExternal: async (url) => opened.push(url),
    onChange: (_url, snapshot) => changes.push(snapshot),
    logger: { warn: () => {} },
  });
  const snapshot = await auth.login(instance, sessionFetch(), cookies());
  assert.deepEqual(device.startCalls, [instance.url]);
  assert.deepEqual(opened, [
    "https://infra.example.com/device?user_code=ABCD-EFGH",
  ]);
  assert.equal(snapshot.status, "ready");
  assert.equal(snapshot.method, "device");
  assert.equal(snapshot.tunnelAccess, true);
  assert.equal(store.values.get(instance.url), "refresh-device");
  const serialized = JSON.stringify(snapshot);
  assert.ok(!serialized.includes("access-device"));
  assert.ok(!serialized.includes("refresh-device"));
  assert.ok(!serialized.includes("device-secret"));
  assert.ok(changes.some((change) => change.status === "pending"));
  assert.ok(changes.some((change) => change.userCode === "ABCD-EFGH"));
  auth.dispose();
});

test("logout forgets the refresh token and keeps tokens out of the UI snapshot", async () => {
  const store = tokenStore(new Map([[instance.url, "refresh-old"]]));
  const auth = new DesktopAuth({
    deviceClient: fakeDevice(),
    tokenStore: store,
    openExternal: async () => {},
    logger: { warn: () => {} },
  });
  auth.setTokens(
    instance.url,
    {
      accessToken: "access-1",
      refreshToken: "refresh-1",
      expiresAt: Date.now() + 300_000,
    },
    "device",
  );
  await auth.logout(instance.url);
  assert.equal(store.values.has(instance.url), false);
  const snapshot = auth.snapshot(instance.url);
  assert.equal(snapshot.status, "none");
  assert.equal(snapshot.tunnelAccess, false);
  auth.dispose();
});
