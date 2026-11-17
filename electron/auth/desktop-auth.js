const { DeviceAuthError } = require("./device-client");
const {
  exchangeDesktopSession,
  fetchAccountSession,
  updateAccountTheme,
} = require("./session-client");

function normalizeConnectionPolicy(body) {
  return ["Server", "Client", "Auto"].includes(body?.mode) ? body.mode : null;
}

function friendly(error) {
  if (error instanceof DeviceAuthError) return error.message;
  if (error instanceof Error && error.message) return error.message;
  return "Accesso non riuscito. Riprova.";
}

function accountSnapshot(account) {
  return account
    ? {
        username: account.user?.username || "",
        name: account.user?.name || "",
        surname: account.user?.surname || "",
        theme: account.theme || null,
      }
    : null;
}

/**
 * Silent desktop authentication. The refresh token is the only persisted
 * secret; access tokens, CSRF tokens and account data stay in this process.
 * The BFF session is created in the instance partition so the tab loads the
 * SPA without going through the Keycloak page.
 */
class DesktopAuth {
  constructor({ deviceClient, tokenStore, openExternal, onChange = () => {}, logger = console }) {
    this.deviceClient = deviceClient;
    this.tokenStore = tokenStore;
    this.openExternal = openExternal;
    this.onChange = onChange;
    this.logger = logger;
    this.contexts = new Map();
  }

  context(instanceUrl) {
    if (!this.contexts.has(instanceUrl)) {
      this.contexts.set(instanceUrl, {
        status: "unknown",
        method: null,
        message: "",
        userCode: null,
        verificationUri: null,
        verificationUriComplete: null,
        expiresIn: null,
        accessToken: null,
        accessExpiresAt: 0,
        csrfToken: "",
        account: null,
        webSession: false,
        flow: null,
        saved: false,
      });
    }
    return this.contexts.get(instanceUrl);
  }

  snapshot(instanceUrl) {
    const context = this.context(instanceUrl);
    const hasStoredToken = Boolean(this.tokenStore.get(instanceUrl));
    const tunnelAccess =
      this.hasAccessToken(instanceUrl) ||
      hasStoredToken ||
      context.webSession;
    return {
      status: context.status,
      method: context.method,
      message: context.message,
      requiresLogin:
        !tunnelAccess && context.status !== "pending" && context.status !== "restoring",
      tunnelAccess,
      userCode: context.userCode,
      verificationUri: context.verificationUri,
      verificationUriComplete: context.verificationUriComplete,
      expiresIn: context.expiresIn,
      saved: hasStoredToken && this.tokenStore.persistenceDisabled !== true,
      theme: context.account?.theme || null,
      user: accountSnapshot(context.account),
    };
  }

  emit(instanceUrl, patch = {}) {
    const context = this.context(instanceUrl);
    Object.assign(context, patch);
    this.onChange(instanceUrl, this.snapshot(instanceUrl));
    return this.snapshot(instanceUrl);
  }

  hasAccessToken(instanceUrl) {
    const context = this.context(instanceUrl);
    return Boolean(
      context.accessToken && context.accessExpiresAt > Date.now() + 30_000,
    );
  }

  setTokens(instanceUrl, tokens, method) {
    const context = this.context(instanceUrl);
    context.accessToken = tokens.accessToken;
    context.accessExpiresAt = tokens.expiresAt;
    if (tokens.refreshToken) {
      context.saved = this.tokenStore.set(instanceUrl, tokens.refreshToken);
    }
    context.method = method;
    return context;
  }

  forgetTokens(instanceUrl) {
    const context = this.context(instanceUrl);
    context.accessToken = null;
    context.accessExpiresAt = 0;
    context.csrfToken = "";
    context.account = null;
  }

  async readAccount(instanceUrl, sessionFetch, { emit = true } = {}) {
    try {
      const account = await fetchAccountSession({ instanceUrl, sessionFetch });
      const context = this.context(instanceUrl);
      if (!account) {
        context.webSession = false;
        return null;
      }
      context.webSession = true;
      context.account = account;
      context.csrfToken = account.csrfToken;
      if (context.status !== "pending") {
        context.status = "ready";
        context.method = context.method || "session";
        context.message = "";
      }
      if (emit) this.emit(instanceUrl);
      return account;
    } catch (error) {
      this.logger.warn("Lettura sessione desktop non riuscita:", friendly(error));
      return null;
    }
  }

  /**
   * The instance tab already holds a BFF cookie when the user signed in from
   * the Web UI: reuse it for tunnel calls instead of asking for a second
   * device-flow login.
   */
  async restoreFromWebSession(instance, sessionFetch) {
    const account = await this.readAccount(instance.url, sessionFetch, {
      emit: false,
    });
    if (!account) return null;
    this.emit(instance.url, {
      status: "ready",
      method: "session",
      message: "",
      saved: false,
    });
    return { mode: "session" };
  }

  /**
   * Startup path: Keycloak refresh, then BFF exchange in the instance
   * partition. Any failure returns a fallback marker; the tab still loads the
   * normal login page.
   */
  async restore(instance, sessionFetch, cookies) {
    const refreshToken = this.tokenStore.get(instance.url);
    if (!refreshToken) {
      const session = await this.restoreFromWebSession(instance, sessionFetch);
      if (session) return session;
      this.emit(instance.url, {
        status: "none",
        method: null,
        message: "Accedi per gestire i tunnel dal desktop.",
        saved: false,
      });
      return { mode: "none" };
    }

    this.emit(instance.url, {
      status: "restoring",
      method: null,
      message: "Ripristino della sessione desktop…",
      saved: true,
    });

    let tokens;
    try {
      tokens = await this.deviceClient.refresh(instance.url, refreshToken);
    } catch (error) {
      if (
        error instanceof DeviceAuthError &&
        ["invalid_grant", "expired_token", "access_denied"].includes(error.code)
      ) {
        this.forgetTokens(instance.url);
        this.tokenStore.delete(instance.url);
        const session = await this.restoreFromWebSession(instance, sessionFetch);
        if (session) return session;
        this.emit(instance.url, {
          status: "none",
          method: null,
          message: "La sessione desktop è scaduta. Accedi di nuovo.",
          saved: false,
        });
        return { mode: "expired" };
      }
      this.emit(instance.url, {
        status: "error",
        method: null,
        message: `${friendly(error)} Accedi dalla pagina di login.`,
        saved: true,
      });
      return { mode: "error", message: friendly(error) };
    }

    this.setTokens(instance.url, tokens, "session");

    try {
      await exchangeDesktopSession({
        instanceUrl: instance.url,
        refreshToken: tokens.refreshToken || refreshToken,
        sessionFetch,
        cookies,
      });
    } catch (error) {
      if (error?.status === 401) {
        this.forgetTokens(instance.url);
        this.tokenStore.delete(instance.url);
        const session = await this.restoreFromWebSession(instance, sessionFetch);
        if (session) return session;
        this.emit(instance.url, {
          status: "none",
          method: null,
          message: friendly(error),
          saved: false,
        });
        return { mode: "revoked", message: friendly(error) };
      }
      this.emit(instance.url, {
        status: "error",
        method: "session",
        message: `${friendly(error)} Accedi dalla pagina di login.`,
        saved: true,
      });
      return { mode: "fallback", message: friendly(error) };
    }

    await this.readAccount(instance.url, sessionFetch);
    this.emit(instance.url, {
      status: "ready",
      method: "session",
      message: "",
      requiresLogin: false,
    });
    return { mode: "silent" };
  }

  /**
   * Periodic renewal for an open instance: refreshes Keycloak and extends the
   * BFF desktop session in the same partition, without changing the visible
   * auth state. A revoked or expired refresh token drops the local session so
   * the next load falls back to the login page.
   */
  async renew(instance, sessionFetch, cookies) {
    const refreshToken = this.tokenStore.get(instance.url);
    if (!refreshToken) return { mode: "none" };

    let tokens;
    try {
      tokens = await this.deviceClient.refresh(instance.url, refreshToken);
    } catch (error) {
      if (
        error instanceof DeviceAuthError &&
        ["invalid_grant", "expired_token", "access_denied"].includes(error.code)
      ) {
        this.forgetTokens(instance.url);
        this.tokenStore.delete(instance.url);
        const session = await this.restoreFromWebSession(instance, sessionFetch);
        if (session) return session;
        this.emit(instance.url, {
          status: "none",
          method: null,
          message: "La sessione desktop è stata revocata. Accedi di nuovo.",
          saved: false,
        });
        return { mode: "revoked" };
      }
      this.logger.warn("Rinnovo sessione desktop non riuscito:", friendly(error));
      return { mode: "error", message: friendly(error) };
    }

    this.setTokens(
      instance.url,
      tokens,
      this.context(instance.url).method || "session",
    );

    try {
      await exchangeDesktopSession({
        instanceUrl: instance.url,
        refreshToken: tokens.refreshToken || refreshToken,
        sessionFetch,
        cookies,
      });
    } catch (error) {
      if (error?.status === 401) {
        this.forgetTokens(instance.url);
        this.tokenStore.delete(instance.url);
        const session = await this.restoreFromWebSession(instance, sessionFetch);
        if (session) return session;
        this.emit(instance.url, {
          status: "none",
          method: null,
          message: friendly(error),
          saved: false,
        });
        return { mode: "revoked", message: friendly(error) };
      }
      this.logger.warn(
        "Scambio sessione desktop non riuscito:",
        friendly(error),
      );
      return { mode: "error", message: friendly(error) };
    }

    await this.readAccount(instance.url, sessionFetch);
    return { mode: "renewed" };
  }

  /**
   * Starts the device flow, opens the verification page in the default
   * browser and polls Keycloak. Authentication happens in that browser; the
   * app only receives tokens.
   */
  async login(instance, sessionFetch, cookies) {
    const context = this.context(instance.url);
    if (context.flow) return this.snapshot(instance.url);

    this.emit(instance.url, {
      status: "pending",
      method: "device",
      message: "Preparazione dell'accesso…",
      userCode: null,
      verificationUri: null,
      verificationUriComplete: null,
      expiresIn: null,
    });

    let authorization;
    try {
      authorization = await this.deviceClient.start(instance.url);
    } catch (error) {
      this.emit(instance.url, {
        status: "error",
        method: "device",
        message: friendly(error),
      });
      return this.snapshot(instance.url);
    }

    const controller = new AbortController();
    context.flow = { controller, authorization };
    this.emit(instance.url, {
      status: "pending",
      method: "device",
      message: `Apri il browser e conferma il codice ${authorization.userCode}.`,
      userCode: authorization.userCode,
      verificationUri: authorization.verificationUri,
      verificationUriComplete: authorization.verificationUriComplete,
      expiresIn: authorization.expiresIn,
    });

    try {
      await this.openExternal(
        authorization.verificationUriComplete || authorization.verificationUri,
      );
    } catch {
      // The user can reopen the URL from the drawer.
    }

    try {
      const tokens = await this.deviceClient.poll(authorization, {
        signal: controller.signal,
      });
      this.setTokens(instance.url, tokens, "device");
      context.flow = null;

      let exchangeMessage = "";
      try {
        await exchangeDesktopSession({
          instanceUrl: instance.url,
          refreshToken: tokens.refreshToken,
          sessionFetch,
          cookies,
        });
      } catch (error) {
        exchangeMessage = friendly(error);
      }

      const account = await this.readAccount(instance.url, sessionFetch, {
        emit: false,
      });
      this.emit(instance.url, {
        status: "ready",
        method: "device",
        message: exchangeMessage
          ? `${exchangeMessage} I tunnel restano disponibili da questa app.`
          : "",
        userCode: null,
        expiresIn: null,
        saved: this.tokenStore.get(instance.url) === tokens.refreshToken,
        account,
      });
      return this.snapshot(instance.url);
    } catch (error) {
      context.flow = null;
      if (error?.name === "AbortError") {
        this.emit(instance.url, {
          status: "none",
          method: null,
          message: "Accesso annullato.",
          userCode: null,
          verificationUri: null,
          verificationUriComplete: null,
        });
      } else {
        this.emit(instance.url, {
          status: "error",
          method: "device",
          message: friendly(error),
          userCode: null,
          verificationUri: null,
          verificationUriComplete: null,
        });
      }
      return this.snapshot(instance.url);
    }
  }

  cancel(instanceUrl) {
    const context = this.context(instanceUrl);
    context.flow?.controller.abort();
    context.flow = null;
  }

  async logout(instanceUrl) {
    this.cancel(instanceUrl);
    this.tokenStore.delete(instanceUrl);
    const context = this.context(instanceUrl);
    const keepWebSession = context.webSession;
    context.accessToken = null;
    context.accessExpiresAt = 0;
    context.csrfToken = "";
    context.account = null;
    this.emit(instanceUrl, {
      status: keepWebSession ? "ready" : "none",
      method: keepWebSession ? "session" : null,
      message: keepWebSession
        ? "Token dimenticato. La sessione web resta attiva."
        : "Sessione tunnel dimenticata.",
      userCode: null,
      verificationUri: null,
      verificationUriComplete: null,
      saved: false,
    });
    return this.snapshot(instanceUrl);
  }

  async ensureAccessToken(instanceUrl, sessionFetch) {
    const context = this.context(instanceUrl);
    if (this.hasAccessToken(instanceUrl)) return context.accessToken;

    const refreshToken = this.tokenStore.get(instanceUrl);
    if (!refreshToken)
      throw new Error("Accedi per usare i tunnel dal desktop.");

    try {
      const tokens = await this.deviceClient.refresh(instanceUrl, refreshToken);
      this.setTokens(instanceUrl, tokens, context.method || "device");
      if (context.status !== "ready") {
        context.status = "ready";
        context.message = "";
        this.emit(instanceUrl);
      }
      return tokens.accessToken;
    } catch (error) {
      if (
        error instanceof DeviceAuthError &&
        ["invalid_grant", "expired_token"].includes(error.code)
      ) {
        this.forgetTokens(instanceUrl);
        this.tokenStore.delete(instanceUrl);
        this.emit(instanceUrl, {
          status: "none",
          method: null,
          message: "La sessione tunnel è scaduta. Accedi di nuovo.",
          saved: false,
        });
      }
      throw new Error(friendly(error));
    }
  }

  async fetchTargets(instanceUrl, sessionFetch) {
    const request = async (token) => {
      const headers = { Accept: "application/json" };
      if (token) headers.Authorization = `Bearer ${token}`;
      const response = await sessionFetch(
        `${instanceUrl.replace(/\/$/, "")}/api/tunnels/targets`,
        {
          headers,
          credentials: "include",
        },
      );
      const body = await response.json().catch(() => ({}));
      return { response, body };
    };

    let result;
    try {
      // First try the BFF session already present in the instance partition.
      result = await request(null);
    } catch {
      throw new Error("Istanza non raggiungibile.");
    }
    if (result.response.ok)
      return Array.isArray(result.body.items) ? result.body.items : [];
    if (![401, 403].includes(result.response.status)) {
      throw new Error(
        result.body?.detail ||
          result.body?.title ||
          `API tunnel: HTTP ${result.response.status}`,
      );
    }

    // No usable BFF session: fall back to the device-flow token.
    let token;
    try {
      token = await this.ensureAccessToken(instanceUrl, sessionFetch);
    } catch {
      throw new Error(
        result.body?.detail ||
          result.body?.title ||
          "Accedi per usare i tunnel dal desktop.",
      );
    }
    try {
      result = await request(token);
    } catch {
      throw new Error("Istanza non raggiungibile.");
    }
    if (result.response.status === 401) {
      const context = this.context(instanceUrl);
      context.accessToken = null;
      token = await this.ensureAccessToken(instanceUrl, sessionFetch);
      result = await request(token);
    }
    if (!result.response.ok) {
      throw new Error(
        result.body?.detail ||
          result.body?.title ||
          `API tunnel: HTTP ${result.response.status}`,
      );
    }
    return Array.isArray(result.body.items) ? result.body.items : [];
  }

  /**
   * Installation-wide connection policy. Returns null when the session has no
   * usable credential; callers fall back to Auto rather than blocking the UI.
   * The relay endpoint still enforces RBAC and the allowlist server-side.
   */
  /**
   * GET with the same credential ladder used by the tunnel API: explicit
   * personal token, partition cookie, device-flow access token. Returns null
   * when every credential is missing or rejected; other failures throw.
   */
  async fetchAuthorizedJson(
    instanceUrl,
    path,
    sessionFetch,
    personalToken = null,
  ) {
    const url = `${instanceUrl.replace(/\/$/, "")}${path}`;
    const request = async (token) => {
      const headers = { Accept: "application/json" };
      if (token) headers.Authorization = `Bearer ${token}`;
      const response = await sessionFetch(url, {
        headers,
        credentials: "include",
      });
      const body = await response.json().catch(() => ({}));
      return { response, body };
    };

    let result;
    if (personalToken) {
      try {
        result = await request(personalToken);
      } catch {
        throw new Error("Istanza non raggiungibile.");
      }
      if (result.response.ok) return result.body;
      if (![401, 403].includes(result.response.status)) {
        throw new Error(
          result.body?.detail ||
            result.body?.title ||
            `API desktop: HTTP ${result.response.status}`,
        );
      }
    }

    // The partition cookie is the normal desktop path.
    try {
      result = await request(null);
    } catch {
      throw new Error("Istanza non raggiungibile.");
    }
    if (result.response.ok) return result.body;
    if (![401, 403].includes(result.response.status)) {
      throw new Error(
        result.body?.detail ||
          result.body?.title ||
          `API desktop: HTTP ${result.response.status}`,
      );
    }

    let token;
    try {
      token = await this.ensureAccessToken(instanceUrl, sessionFetch);
    } catch {
      return null;
    }
    try {
      result = await request(token);
    } catch {
      return null;
    }
    return result.response.ok ? result.body : null;
  }

  async fetchConnectionPolicy(instanceUrl, sessionFetch, personalToken = null) {
    const body = await this.fetchAuthorizedJson(
      instanceUrl,
      "/api/connection-policy",
      sessionFetch,
      personalToken,
    );
    return normalizeConnectionPolicy(body);
  }

  async fetchRemoteTarget(
    instanceUrl,
    serverId,
    sessionFetch,
    personalToken = null,
    scope = null,
  ) {
    const query = scope === "actions" ? "?scope=actions" : "";
    return this.fetchAuthorizedJson(
      instanceUrl,
      `/api/remote/targets/${serverId}${query}`,
      sessionFetch,
      personalToken,
    );
  }

  async fetchCounts(instanceUrl, sessionFetch) {
    const base = instanceUrl.replace(/\/$/, "");
    const read = async (path) => {
      const response = await sessionFetch(base + path, {
        headers: { Accept: "application/json" },
        credentials: "include",
      });
      if (!response.ok) return null;
      return response.json().catch(() => null);
    };
    try {
      const [servers, sessions] = await Promise.all([
        read("/api/servers?pageSize=1"),
        read("/api/tunnels/sessions"),
      ]);
      return {
        serverCount:
          servers && Number.isFinite(servers.total) ? servers.total : null,
        sessionTunnelCount:
          sessions && Array.isArray(sessions.items) ? sessions.items.length : null,
      };
    } catch {
      return { serverCount: null, sessionTunnelCount: null };
    }
  }

  async setTheme(instanceUrl, sessionFetch, theme) {
    const context = this.context(instanceUrl);
    if (!context.csrfToken) return false;
    const saved = await updateAccountTheme({
      instanceUrl,
      sessionFetch,
      csrfToken: context.csrfToken,
      theme,
    });
    if (saved) context.account = { ...(context.account || {}), theme };
    return saved;
  }

  accountTheme(instanceUrl) {
    return this.context(instanceUrl).account?.theme || null;
  }

  csrfToken(instanceUrl) {
    return this.context(instanceUrl).csrfToken;
  }

  dispose() {
    for (const context of this.contexts.values()) {
      context.flow?.controller.abort();
      context.flow = null;
    }
    this.contexts.clear();
  }
}

module.exports = { DesktopAuth };
