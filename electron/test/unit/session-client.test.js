const test = require("node:test");
const assert = require("node:assert/strict");

const {
  SessionExchangeError,
  exchangeDesktopSession,
  fetchAccountSession,
  updateAccountTheme,
} = require("../../auth/session-client");

function response(status, body = {}, headers = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get: (name) => headers[name.toLowerCase()] || null,
      getSetCookie: () => headers["set-cookie"] || [],
    },
    json: async () => body,
    text: async () =>
      typeof body === "string" ? body : JSON.stringify(body),
  };
}

function recordingFetch(handler) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, init });
    return handler(url, init, calls.length - 1);
  };
  fetch.calls = calls;
  return fetch;
}

test("exchange posts the refresh token in the instance partition and stores the cookie", async () => {
  const calls = [];
  const cookieStore = [];
  const fetch = async (url, init) => {
    calls.push({ url, init });
    return response(204, null, {
      "set-cookie": [
        "__Host-infraorgit_session=session-1; Path=/; Secure; HttpOnly; SameSite=Lax",
      ],
    });
  };
  await exchangeDesktopSession({
    instanceUrl: "https://infra.example.com",
    refreshToken: "refresh-secret",
    sessionFetch: fetch,
    cookies: {
      get: async () => [],
      set: async (cookie) => {
        cookieStore.push(cookie);
      },
    },
  });

  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "https://infra.example.com/auth/desktop-session");
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.credentials, "include");
  assert.equal(calls[0].init.headers.Origin, "https://infra.example.com");
  assert.deepEqual(JSON.parse(calls[0].init.body), {
    refreshToken: "refresh-secret",
  });
  assert.equal(cookieStore.length, 1);
  assert.equal(cookieStore[0].name, "__Host-infraorgit_session");
  assert.equal(cookieStore[0].value, "session-1");
  assert.equal(cookieStore[0].secure, true);
  assert.equal(cookieStore[0].httpOnly, true);
});

test("exchange accepts a cookie already committed by Chromium", async () => {
  let sets = 0;
  const fetch = async () =>
    response(204, null, {
      "set-cookie": ["infraorgit_session=session-1; Path=/"],
    });
  await exchangeDesktopSession({
    instanceUrl: "https://infra.example.com",
    refreshToken: "refresh-secret",
    sessionFetch: fetch,
    cookies: {
      get: async () => [{ name: "infraorgit_session", value: "session-1" }],
      set: async () => {
        sets += 1;
      },
    },
  });
  assert.equal(sets, 0);
});

test("exchange falls back with a friendly error when the endpoint is missing", async () => {
  const fetch = async () =>
    response(404, {
      title: "Not found",
      detail: "endpoint assente",
      code: "not_found",
    });
  await assert.rejects(
    () =>
      exchangeDesktopSession({
        instanceUrl: "https://infra.example.com",
        refreshToken: "refresh-secret",
        sessionFetch: fetch,
      }),
    (error) => {
      assert.ok(error instanceof SessionExchangeError);
      assert.equal(error.status, 404);
      assert.ok(error.message.includes("non supporta"));
      assert.ok(!error.message.includes("refresh-secret"));
      return true;
    },
  );
});

test("exchange maps 401, 502 and network failures without exposing the token", async () => {
  const statuses = [
    [401, { code: "desktop_session_invalid" }, "non è più valida"],
    [502, { code: "oidc_error" }, "Keycloak"],
  ];
  for (const [status, body, expected] of statuses) {
    await assert.rejects(
      () =>
        exchangeDesktopSession({
          instanceUrl: "https://infra.example.com",
          refreshToken: "secret-refresh",
          sessionFetch: async () => response(status, body),
        }),
      (error) =>
        error.status === status &&
        error.message.includes(expected) &&
        !error.message.includes("secret-refresh"),
    );
  }

  await assert.rejects(
    () =>
      exchangeDesktopSession({
        instanceUrl: "https://infra.example.com",
        refreshToken: "secret-refresh",
        sessionFetch: async () => {
          throw new Error("offline");
        },
      }),
    (error) =>
      error.code === "network" && !error.message.includes("secret-refresh"),
  );
});

test("exchange refuses a missing refresh token before any request", async () => {
  let called = false;
  await assert.rejects(
    () =>
      exchangeDesktopSession({
        instanceUrl: "https://infra.example.com",
        refreshToken: "",
        sessionFetch: async () => {
          called = true;
          return response(204);
        },
      }),
    (error) => error.code === "missing_refresh_token",
  );
  assert.equal(called, false);
});

test("fetchAccountSession reads user, theme and CSRF token", async () => {
  const fetch = recordingFetch(async () =>
    response(200, {
      user: { username: "admin", name: "Ada", surname: "Lovelace" },
      theme: "light",
      csrfToken: "csrf-1",
      expiresIn: 240,
    }),
  );
  const account = await fetchAccountSession({
    instanceUrl: "https://infra.example.com",
    sessionFetch: fetch,
  });
  assert.deepEqual(account.user, {
    username: "admin",
    name: "Ada",
    surname: "Lovelace",
  });
  assert.equal(account.theme, "light");
  assert.equal(account.csrfToken, "csrf-1");
  assert.equal(fetch.calls[0].init.credentials, "include");
});

test("fetchAccountSession returns null for an anonymous session and validates theme", async () => {
  const anonymous = await fetchAccountSession({
    instanceUrl: "https://infra.example.com",
    sessionFetch: async () => response(401, {}),
  });
  assert.equal(anonymous, null);

  const account = await fetchAccountSession({
    instanceUrl: "https://infra.example.com",
    sessionFetch: async () =>
      response(200, {
        user: { username: "u" },
        theme: "neon",
        csrfToken: 7,
      }),
  });
  assert.equal(account.theme, null);
  assert.equal(account.csrfToken, "");
});

test("updateAccountTheme sends the CSRF token and origin", async () => {
  const fetch = recordingFetch(async () => response(204));
  const saved = await updateAccountTheme({
    instanceUrl: "https://infra.example.com",
    sessionFetch: fetch,
    csrfToken: "csrf-1",
    theme: "dark",
  });
  assert.equal(saved, true);
  assert.equal(fetch.calls[0].url, "https://infra.example.com/api/session/theme");
  assert.equal(fetch.calls[0].init.method, "PATCH");
  assert.equal(fetch.calls[0].init.headers["X-CSRF-Token"], "csrf-1");
  assert.equal(fetch.calls[0].init.headers.Origin, "https://infra.example.com");
  assert.deepEqual(JSON.parse(fetch.calls[0].init.body), { theme: "dark" });

  const offline = await updateAccountTheme({
    instanceUrl: "https://infra.example.com",
    sessionFetch: async () => {
      throw new Error("offline");
    },
    csrfToken: "csrf-1",
    theme: "dark",
  });
  assert.equal(offline, false);

  const noCsrf = await updateAccountTheme({
    instanceUrl: "https://infra.example.com",
    sessionFetch: async () => response(204),
    csrfToken: "",
    theme: "dark",
  });
  assert.equal(noCsrf, false);
});
