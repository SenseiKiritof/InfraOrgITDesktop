const crypto = require("crypto");

const CLIENT_ID = "infraorgit-mcp-desktop";
const SCOPE = "openid profile email offline_access mcp:read tunnels:operate";
const DISCOVERY_PATH = "/.well-known/oauth-protected-resource";
const DEVICE_PATH = "/protocol/openid-connect/auth/device";
const TOKEN_PATH = "/protocol/openid-connect/token";
const DEFAULT_LIFETIME_SECONDS = 600;

class DeviceAuthError extends Error {
  constructor(message, code = "device_auth_failed") {
    super(message);
    this.name = "DeviceAuthError";
    this.code = code;
  }
}

function isLoopback(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

/** Instance and issuer URLs are https, or loopback http for a local stack. */
function normalizeUrl(value, label) {
  let url;
  try {
    url = new URL(String(value ?? "").trim());
  } catch {
    throw new DeviceAuthError(`${label} non è un URL valido.`, "invalid_url");
  }
  if (!["http:", "https:"].includes(url.protocol))
    throw new DeviceAuthError(`${label} deve usare http o https.`, "invalid_url");
  if (url.protocol === "http:" && !isLoopback(url.hostname))
    throw new DeviceAuthError(`${label} remoto deve usare https.`, "insecure_url");
  if (url.username || url.password)
    throw new DeviceAuthError(`${label} non può contenere credenziali.`, "invalid_url");
  return url.toString().replace(/\/$/, "");
}

function abortError() {
  const error = new Error("Operazione annullata.");
  error.name = "AbortError";
  return error;
}

function defaultDelay(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(abortError());
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, milliseconds);
    const onAbort = () => {
      clearTimeout(timer);
      reject(abortError());
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

async function readJson(response) {
  const text = await response.text().catch(() => "");
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function describeDeviceError(status, error) {
  switch (error) {
    case "invalid_client":
      return "Il client desktop non è configurato su Keycloak.";
    case "unauthorized_client":
      return "Keycloak non consente l'accesso da questo client.";
    case "invalid_scope":
      return "Keycloak non autorizza gli scope richiesti dal desktop.";
    case "invalid_request":
      return "Richiesta di accesso non valida.";
    default:
      return status === 429
        ? "Troppe richieste al server di autenticazione. Attendi e riprova."
        : `Il server di autenticazione ha risposto ${status}.`;
  }
}

function describeTokenError(error) {
  switch (error) {
    case "expired_token":
      return "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.";
    case "access_denied":
      return "Accesso negato. Riprova e conferma nel browser.";
    case "invalid_grant":
      return "La sessione non è più valida. Accedi di nuovo.";
    case "invalid_client":
      return "Il client desktop non è configurato su Keycloak.";
    case "invalid_scope":
      return "Keycloak non autorizza gli scope richiesti dal desktop.";
    default:
      return "Accesso non riuscito. Riprova.";
  }
}

function tokenRecord(payload, fallbackRefreshToken, now) {
  const expiresIn = Number.isFinite(payload.expires_in) && payload.expires_in > 0
    ? payload.expires_in
    : 60;
  const refreshToken = payload.refresh_token || fallbackRefreshToken || null;
  return {
    accessToken: payload.access_token,
    refreshToken,
    idToken: payload.id_token || null,
    expiresIn,
    expiresAt: now + expiresIn * 1000,
  };
}

/**
 * RFC 8628 device authorization client for the public desktop client. The
 * client only talks to the instance; tokens are returned to the caller and
 * never written to disk or forwarded to a renderer.
 */
class DeviceAuthClient {
  constructor({
    fetchImpl = globalThis.fetch,
    delay = defaultDelay,
    now = Date.now,
  } = {}) {
    if (typeof fetchImpl !== "function")
      throw new TypeError("DeviceAuthClient richiede fetch.");
    this.fetch = fetchImpl;
    this.delay = delay;
    this.now = now;
  }

  /**
   * Reads authorization_servers[0] from the protected-resource metadata and
   * falls back to the standard local realm path for older deployments.
   */
  async discoverIssuer(instanceUrl) {
    const baseUrl = normalizeUrl(instanceUrl, "L'URL dell'istanza");
    let response;
    try {
      response = await this.fetch(baseUrl + DISCOVERY_PATH, {
        headers: { Accept: "application/json" },
      });
    } catch {
      throw new DeviceAuthError(
        "Il server di autenticazione non è raggiungibile.",
        "network",
      );
    }
    if (!response.ok) {
      throw new DeviceAuthError(
        `La scoperta dell'autenticazione ha risposto ${response.status}.`,
        "discovery",
      );
    }
    const payload = await readJson(response);
    const issuer = Array.isArray(payload?.authorization_servers)
      ? payload.authorization_servers.find(
          (value) => typeof value === "string" && value.trim().length > 0,
        )
      : null;
    return issuer
      ? normalizeUrl(issuer, "L'authorization server")
      : `${baseUrl}/realms/infraorgit`;
  }

  /** Starts a device authorization and keeps the PKCE verifier in memory. */
  async start(instanceUrl) {
    const issuer = await this.discoverIssuer(instanceUrl);
    const codeVerifier = crypto.randomBytes(32).toString("base64url");
    const codeChallenge = crypto
      .createHash("sha256")
      .update(codeVerifier, "ascii")
      .digest("base64url");
    const form = new URLSearchParams({
      client_id: CLIENT_ID,
      scope: SCOPE,
      code_challenge: codeChallenge,
      code_challenge_method: "S256",
    });

    let response;
    try {
      response = await this.fetch(issuer + DEVICE_PATH, {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: form.toString(),
      });
    } catch {
      throw new DeviceAuthError(
        "Il server di autenticazione non è raggiungibile.",
        "network",
      );
    }
    const payload = await readJson(response);
    if (!response.ok) {
      throw new DeviceAuthError(
        describeDeviceError(response.status, payload?.error),
        payload?.error || "device_start",
      );
    }
    if (!payload?.device_code || !payload?.user_code || !payload?.verification_uri) {
      throw new DeviceAuthError(
        "Risposta di avvio accesso incompleta.",
        "invalid_response",
      );
    }

    const expiresIn =
      Number.isFinite(payload.expires_in) && payload.expires_in > 0
        ? payload.expires_in
        : DEFAULT_LIFETIME_SECONDS;
    const interval =
      Number.isFinite(payload.interval) && payload.interval > 0
        ? payload.interval
        : 5;
    return {
      issuer,
      deviceCode: payload.device_code,
      userCode: payload.user_code,
      verificationUri: payload.verification_uri,
      verificationUriComplete: payload.verification_uri_complete || null,
      intervalSeconds: interval,
      expiresIn,
      expiresAt: this.now() + expiresIn * 1000,
      codeVerifier,
    };
  }

  /**
   * Polls the token endpoint until approval, expiry or cancellation. The
   * device code and verifier stay in the main process.
   */
  async poll(authorization, { signal } = {}) {
    let interval = Math.max(1, authorization.intervalSeconds || 5);
    while (true) {
      if (signal?.aborted) throw abortError();
      if (this.now() >= authorization.expiresAt) {
        throw new DeviceAuthError(
          "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.",
          "expired_token",
        );
      }

      await this.delay(interval * 1000, signal);

      let response;
      try {
        response = await this.fetch(
          authorization.issuer + TOKEN_PATH,
          {
            method: "POST",
            headers: {
              Accept: "application/json",
              "Content-Type": "application/x-www-form-urlencoded",
            },
            body: new URLSearchParams({
              grant_type: "urn:ietf:params:oauth:grant-type:device_code",
              device_code: authorization.deviceCode,
              client_id: CLIENT_ID,
              code_verifier: authorization.codeVerifier,
            }).toString(),
          },
        );
      } catch (error) {
        if (error?.name === "AbortError") throw error;
        throw new DeviceAuthError(
          "Il server di autenticazione non è raggiungibile.",
          "network",
        );
      }

      const payload = await readJson(response);
      if (payload?.access_token) return tokenRecord(payload, null, this.now());

      switch (payload?.error) {
        case "authorization_pending":
          continue;
        case "slow_down":
          interval += 5;
          continue;
        case "expired_token":
          throw new DeviceAuthError(
            "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.",
            "expired_token",
          );
        case "access_denied":
          throw new DeviceAuthError(
            "Accesso negato. Riprova e conferma nel browser.",
            "access_denied",
          );
        default:
          throw new DeviceAuthError(
            describeTokenError(payload?.error),
            payload?.error || `http_${response.status}`,
          );
      }
    }
  }

  /** Exchanges a refresh token for a fresh access token. */
  async refresh(instanceUrl, refreshToken) {
    if (!refreshToken || !String(refreshToken).trim()) {
      throw new DeviceAuthError(
        "Sessione non valida. Accedi di nuovo con Keycloak.",
        "invalid_grant",
      );
    }
    const issuer = await this.discoverIssuer(instanceUrl);
    return this.refreshAtIssuer(issuer, refreshToken);
  }

  async refreshAtIssuer(issuer, refreshToken) {
    let response;
    try {
      response = await this.fetch(issuer + TOKEN_PATH, {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: new URLSearchParams({
          grant_type: "refresh_token",
          client_id: CLIENT_ID,
          refresh_token: refreshToken,
        }).toString(),
      });
    } catch {
      throw new DeviceAuthError(
        "Keycloak non è raggiungibile. Riprova quando la rete è disponibile.",
        "network",
      );
    }
    const payload = await readJson(response);
    if (!response.ok || !payload?.access_token) {
      throw new DeviceAuthError(
        describeTokenError(payload?.error),
        payload?.error || `http_${response.status}`,
      );
    }
    return tokenRecord(payload, refreshToken, this.now());
  }
}

module.exports = {
  CLIENT_ID,
  SCOPE,
  DeviceAuthClient,
  DeviceAuthError,
  normalizeUrl,
};
