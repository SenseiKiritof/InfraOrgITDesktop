const SESSION_COOKIES = [
  "__Host-infraorgit_session",
  "infraorgit_session",
];

class SessionExchangeError extends Error {
  constructor(message, code = "session_exchange_failed", status = 0) {
    super(message);
    this.name = "SessionExchangeError";
    this.code = code;
    this.status = status;
  }
}

function baseUrl(value) {
  const url = new URL(String(value ?? "").trim());
  if (!["http:", "https:"].includes(url.protocol))
    throw new SessionExchangeError("L'URL dell'istanza non è valido.", "invalid_url");
  return url.toString().replace(/\/$/, "");
}

function originOf(value) {
  return new URL(value).origin;
}

function headerValue(headers, name) {
  if (!headers) return "";
  if (typeof headers.get === "function") return headers.get(name) || "";
  return headers[name] || "";
}

function setCookieValues(response) {
  const headers = response?.headers;
  if (!headers) return [];
  if (typeof headers.getSetCookie === "function") {
    const values = headers.getSetCookie();
    if (Array.isArray(values) && values.length > 0) return values;
    if (typeof values === "string" && values) return values.split(/,(?=\s*[^;,]+=)/);
  }
  const raw = headerValue(headers, "set-cookie");
  return raw ? raw.split(/,(?=\s*[^;,]+=)/) : [];
}

function sessionCookie(response) {
  for (const raw of setCookieValues(response)) {
    const pair = String(raw).split(";", 1)[0];
    const separator = pair.indexOf("=");
    if (separator <= 0) continue;
    const name = pair.slice(0, separator).trim();
    if (SESSION_COOKIES.includes(name)) {
      return { name, value: pair.slice(separator + 1) };
    }
  }
  return null;
}

async function problem(response) {
  try {
    const body = await response.json();
    return {
      code: typeof body?.code === "string" ? body.code : "",
      message:
        typeof body?.detail === "string"
          ? body.detail
          : typeof body?.title === "string"
            ? body.title
            : "",
    };
  } catch {
    return { code: "", message: "" };
  }
}

function exchangeMessage(status, code) {
  if (code === "desktop_session_invalid")
    return "La sessione desktop non è più valida. Accedi di nuovo.";
  if (status === 401)
    return "La sessione desktop non è più valida. Accedi di nuovo.";
  if (status === 404)
    return "Il server non supporta ancora la sessione desktop. Accedi dalla pagina di login.";
  if (status === 502 || code === "oidc_error")
    return "Keycloak non è raggiungibile. Accedi dalla pagina di login.";
  if (status === 429)
    return "Troppe richieste di accesso. Attendi e riprova.";
  return "Scambio della sessione desktop non riuscito. Accedi dalla pagina di login.";
}

/**
 * Exchanges the device-flow refresh token for the BFF session cookie. The
 * request runs through the instance partition so Chromium stores the cookie
 * exactly where the instance tab will read it. The refresh token never leaves
 * this function and the caller never receives it back.
 */
async function exchangeDesktopSession({
  instanceUrl,
  refreshToken,
  sessionFetch,
  cookies,
}) {
  if (!refreshToken || !String(refreshToken).trim())
    throw new SessionExchangeError(
      "Refresh token assente. Accedi dalla pagina di login.",
      "missing_refresh_token",
    );
  if (typeof sessionFetch !== "function")
    throw new SessionExchangeError("Sessione Electron non disponibile.");

  const base = baseUrl(instanceUrl);
  let response;
  try {
    response = await sessionFetch(base + "/auth/desktop-session", {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        Origin: originOf(base),
      },
      body: JSON.stringify({ refreshToken }),
      credentials: "include",
      redirect: "manual",
    });
  } catch {
    throw new SessionExchangeError(
      "Istanza non raggiungibile. Accedi dalla pagina di login.",
      "network",
    );
  }

  if (!response.ok) {
    const details = await problem(response);
    throw new SessionExchangeError(
      exchangeMessage(response.status, details.code),
      details.code || `http_${response.status}`,
      response.status,
    );
  }

  if (cookies?.get) {
    const existing = await cookies.get({ url: base }).catch(() => []);
    const found = Array.isArray(existing)
      ? existing.some((cookie) => SESSION_COOKIES.includes(cookie.name))
      : false;
    if (!found) {
      const fromHeader = sessionCookie(response);
      if (fromHeader && cookies.set) {
        await cookies.set({
          url: base,
          name: fromHeader.name,
          value: fromHeader.value,
          path: "/",
          httpOnly: true,
          secure: base.startsWith("https:"),
          sameSite: "lax",
        });
      } else {
        throw new SessionExchangeError(
          "Il server non ha creato la sessione desktop. Accedi dalla pagina di login.",
          "session_cookie_missing",
          response.status,
        );
      }
    }
  }

  return { ok: true };
}

function themeValue(value) {
  return value === "light" || value === "dark" || value === "system" ? value : null;
}

/** Reads the BFF session, including the account theme and CSRF token. */
async function fetchAccountSession({ instanceUrl, sessionFetch }) {
  const base = baseUrl(instanceUrl);
  let response;
  try {
    response = await sessionFetch(base + "/api/session", {
      headers: { Accept: "application/json" },
      credentials: "include",
    });
  } catch {
    throw new SessionExchangeError(
      "Istanza non raggiungibile.",
      "network",
    );
  }
  if (response.status === 401) return null;
  if (!response.ok) {
    const details = await problem(response);
    throw new SessionExchangeError(
      details.message || "Lettura della sessione non riuscita.",
      details.code || `http_${response.status}`,
      response.status,
    );
  }
  const body = await response.json().catch(() => null);
  if (!body || typeof body !== "object")
    throw new SessionExchangeError("Risposta della sessione non valida.");
  return {
    user: body.user && typeof body.user === "object"
      ? {
          username: String(body.user.username || ""),
          name: String(body.user.name || ""),
          surname: String(body.user.surname || ""),
        }
      : null,
    theme: themeValue(body.theme),
    csrfToken: typeof body.csrfToken === "string" ? body.csrfToken : "",
    expiresIn: Number.isFinite(body.expiresIn) ? body.expiresIn : 0,
  };
}

/** Persists the account theme through the API when the session allows it. */
async function updateAccountTheme({
  instanceUrl,
  sessionFetch,
  csrfToken,
  theme,
}) {
  if (!csrfToken) return false;
  const base = baseUrl(instanceUrl);
  let response;
  try {
    response = await sessionFetch(base + "/api/session/theme", {
      method: "PATCH",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "X-CSRF-Token": csrfToken,
        Origin: originOf(base),
      },
      body: JSON.stringify({ theme }),
      credentials: "include",
    });
  } catch {
    return false;
  }
  return response.ok;
}

module.exports = {
  SESSION_COOKIES,
  SessionExchangeError,
  exchangeDesktopSession,
  fetchAccountSession,
  updateAccountTheme,
};
