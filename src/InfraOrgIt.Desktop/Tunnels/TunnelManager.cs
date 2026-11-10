using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;

namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Owns the tunnel sessions of the desktop app. Access tokens and targets stay
/// in memory; only the refresh token is handed to <see cref="ITokenStore"/>,
/// keyed by instance URL. The allowlist comes from the instance API, and each
/// ssh process is tracked until it exits. UI messages are delivered through
/// <c>notify</c>.
/// </summary>
public sealed class TunnelManager(
    HttpClient http,
    Action<object> notify,
    DeviceAuthClient? auth = null,
    ITokenStore? tokenStore = null)
{
    /// <summary>Refresh this long before the access token expires.</summary>
    internal static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly DeviceAuthClient _auth = auth ?? new DeviceAuthClient(http);
    private readonly ITokenStore _tokens = tokenStore ?? new MemoryTokenStore();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TunnelProcess> _active =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refreshTokens =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _verificationUrls =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _loginCancellation;

    public void Handle(string message) => _ = HandleAsync(message);

    private async Task HandleAsync(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "tunnels.list":
                    var listUrl = RequiredString(root, "url");
                    var listName = RequiredString(root, "name");
                    if (OptionalString(root, "token") is { Length: > 0 } token)
                    {
                        await LoadTargetsAsync(listUrl, listName, token);
                    }
                    else
                    {
                        await RestoreSessionAsync(listUrl, listName);
                    }
                    break;

                case "tunnels.restore":
                    await RestoreSessionAsync(
                        RequiredString(root, "url"),
                        RequiredString(root, "name"));
                    break;

                case "tunnels.logout":
                    await LogoutAsync(RequiredString(root, "url"));
                    break;

                case "tunnels.login":
                    await LoginWithDeviceCodeAsync(
                        RequiredString(root, "url"),
                        RequiredString(root, "name"));
                    break;

                case "tunnels.login.cancel":
                    CancelLogin();
                    break;

                case "tunnels.start":
                    await StartAsync(
                        RequiredString(root, "url"),
                        root.GetProperty("serverId").GetInt32(),
                        root.GetProperty("targetId").GetInt32(),
                        root.TryGetProperty("localPort", out var portElement) &&
                        portElement.ValueKind is not JsonValueKind.Null
                            ? portElement.GetInt32()
                            : null);
                    break;

                case "tunnels.stop":
                    await StopAsync(RequiredString(root, "id"));
                    break;

                default:
                    Send(new { type = "tunnels.error", message = "Messaggio tunnel non riconosciuto." });
                    break;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or
            HttpRequestException or TaskCanceledException or
            TokenStoreException)
        {
            Send(new { type = "tunnels.error", message = exception.Message });
        }
    }

    /// <summary>
    /// Starts the Keycloak device flow for the instance, reports the user code
    /// to the UI and, once the token arrives, stores the refresh token and
    /// loads the tunnel allowlist. The previous poll is cancelled when a new
    /// login starts.
    /// </summary>
    public async Task LoginWithDeviceCodeAsync(string url, string name)
    {
        CancelLogin();
        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _loginCancellation = cancellation;
            _verificationUrls.Clear();
        }

        try
        {
            var authorization = await _auth.StartAsync(url, cancellation.Token);
            StoreVerificationUrls(authorization);

            Send(new
            {
                type = "tunnels.auth",
                status = "pending",
                url,
                userCode = authorization.UserCode,
                verificationUri = authorization.VerificationUri,
                verificationUriComplete = authorization.VerificationUriComplete,
                expiresIn = authorization.ExpiresInSeconds
            });

            var tokens = await _auth.PollAsync(authorization, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var canRefresh = false;
            var saved = false;
            if (tokens.RefreshToken is { Length: > 0 } refreshToken)
            {
                canRefresh = true;
                lock (_gate)
                {
                    _refreshTokens[url] = refreshToken;
                }
                saved = await SaveRefreshTokenAsync(url, refreshToken);
            }

            lock (_gate)
            {
                _verificationUrls.Clear();
            }

            Send(new
            {
                type = "tunnels.auth",
                status = "success",
                url,
                saved,
                message = saved
                    ? "Accesso riuscito. Sessione salvata."
                    : "Accesso riuscito. La sessione resta valida solo per questa esecuzione."
            });

            await LoadTargetsAsync(
                url,
                name,
                tokens.AccessToken,
                tokens.ExpiresAt,
                authorization.Issuer,
                canRefresh,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The panel closed or another instance opened: nothing to show.
        }
        catch (DeviceAuthException exception)
        {
            SendAuthError(url, exception.Message);
        }
        catch (TaskCanceledException)
        {
            SendAuthError(url, "Il server di autenticazione non ha risposto in tempo.");
        }
        catch (HttpRequestException exception)
        {
            SendAuthError(url, "Accesso non riuscito: " + exception.Message);
        }
        catch (JsonException)
        {
            SendAuthError(url, "Risposta dell'istanza non valida.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_loginCancellation, cancellation))
                {
                    _loginCancellation = null;
                    _verificationUrls.Clear();
                }
            }
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Tries the refresh token saved for the instance, without user
    /// interaction. Called when the tunnel panel opens and on a
    /// <c>tunnels.list</c> without a manual token.
    /// </summary>
    public async Task RestoreSessionAsync(string url, string name)
    {
        RefreshedTokens? refreshed;
        await _refreshGate.WaitAsync();
        try
        {
            string? stored;
            try
            {
                stored = await _tokens.LoadAsync(url);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stored = null;
            }

            if (string.IsNullOrEmpty(stored))
            {
                Send(new { type = "tunnels.auth", status = "none", url });
                return;
            }

            lock (_gate)
            {
                _refreshTokens[url] = stored;
            }

            Send(new { type = "tunnels.auth", status = "restoring", url });

            refreshed = await RefreshTokensCoreAsync(
                url, null, CancellationToken.None);
        }
        finally
        {
            _refreshGate.Release();
        }

        if (refreshed is null)
            return;

        Send(new
        {
            type = "tunnels.auth",
            status = "restored",
            url,
            saved = _tokens.IsPersistent,
            message = "Sessione ripristinata."
        });

        await LoadTargetsAsync(
            url,
            name,
            refreshed.Tokens.AccessToken,
            refreshed.Tokens.ExpiresAt,
            refreshed.Issuer,
            canRefresh: true,
            CancellationToken.None);
    }

    /// <summary>
    /// Forgets the instance session: in-memory tokens, targets and the
    /// refresh token in the platform store. Manual tokens keep working.
    /// </summary>
    public async Task LogoutAsync(string url)
    {
        CancelLogin();
        await ClearSessionAsync(url);
        Send(new
        {
            type = "tunnels.auth",
            status = "logout",
            url,
            message = "Sessione eliminata."
        });
    }

    /// <summary>
    /// Cancels a running device poll. Called when the tunnel panel closes,
    /// another instance opens or the app exits.
    /// </summary>
    public void CancelLogin()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _loginCancellation;
            _loginCancellation = null;
            _verificationUrls.Clear();
        }
        cancellation?.Cancel();
    }

    /// <summary>
    /// Only the verification page returned by the current device flow may be
    /// opened in the system browser.
    /// </summary>
    public bool IsVerificationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        lock (_gate)
        {
            return _verificationUrls.Contains(uri.AbsoluteUri);
        }
    }

    /// <summary>
    /// Refresh token kept in memory for the current run. The same value is
    /// written to the platform store; it is never logged.
    /// </summary>
    public string? GetRefreshToken(string url)
    {
        lock (_gate)
        {
            return _refreshTokens.GetValueOrDefault(url);
        }
    }

    private void StoreVerificationUrls(DeviceAuthorization authorization)
    {
        lock (_gate)
        {
            _verificationUrls.Clear();
            AddVerificationUrl(authorization.VerificationUri);
            if (authorization.VerificationUriComplete is { Length: > 0 } complete)
                AddVerificationUrl(complete);
        }
    }

    private void AddVerificationUrl(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp ||
             uri.Scheme == Uri.UriSchemeHttps))
        {
            _verificationUrls.Add(uri.AbsoluteUri);
        }
    }

    private void SendAuthError(
        string url, string message, bool requiresLogin = false) =>
        Send(new
        {
            type = "tunnels.auth",
            status = "error",
            url,
            message,
            requiresLogin
        });

    private void SendWarning(string message) =>
        Send(new { type = "tunnels.warning", message });

    /// <summary>Loads targets for a manual token, without refresh.</summary>
    public Task LoadTargetsAsync(string url, string name, string token) =>
        LoadTargetsAsync(
            url, name, token, null, null, canRefresh: false,
            CancellationToken.None);

    private async Task LoadTargetsAsync(
        string url,
        string name,
        string token,
        DateTimeOffset? expiresAt,
        string? issuer,
        bool canRefresh,
        CancellationToken cancellationToken)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            Send(new { type = "tunnels.error", message = "Inserisci il token personale." });
            return;
        }

        using var result = await SendTargetsAsync(
            url, token, expiresAt, issuer, canRefresh, cancellationToken);
        if (result is null)
            return;

        var response = result.Response;
        if (!response.IsSuccessStatusCode)
        {
            Send(new
            {
                type = "tunnels.error",
                message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        "Token non valido o scaduto.",
                    HttpStatusCode.Forbidden =>
                        "Serve un token con tunnels:operate e il ruolo operatore sull'ambiente.",
                    _ => $"L'istanza ha risposto {(int)response.StatusCode}."
                }
            });
            return;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TunnelTargetsResponse>(JsonOptions, cancellationToken);
        if (payload is null)
        {
            Send(new { type = "tunnels.error", message = "Risposta target non valida." });
            return;
        }

        lock (_gate)
        {
            _sessions[url] = new Session(
                url,
                name,
                result.Token,
                result.ExpiresAt,
                result.Issuer,
                canRefresh,
                payload.Items);
        }

        Send(new
        {
            type = "tunnels.targets",
            url,
            items = payload.Items
        });
    }

    /// <summary>
    /// Loads the allowlist with the session token. Refreshes before the call
    /// when the access token expires within <see cref="RefreshWindow"/>, and
    /// once more after a 401. A failed refresh clears the stored session.
    /// </summary>
    private async Task<AuthorizedResponse?> SendTargetsAsync(
        string url,
        string token,
        DateTimeOffset? expiresAt,
        string? issuer,
        bool canRefresh,
        CancellationToken cancellationToken)
    {
        if (canRefresh && IsExpiring(expiresAt))
        {
            var early = await RefreshTokensForUrlAsync(
                url, issuer, cancellationToken);
            if (early is null)
                return null;

            token = early.Tokens.AccessToken;
            expiresAt = early.Tokens.ExpiresAt;
            issuer = early.Issuer;
        }

        var response = await GetTargetsAsync(url, token, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !canRefresh)
            return new AuthorizedResponse(response, token, expiresAt, issuer);

        response.Dispose();

        var refreshed = await RefreshTokensForUrlAsync(
            url, issuer, cancellationToken);
        if (refreshed is null)
            return null;

        token = refreshed.Tokens.AccessToken;
        expiresAt = refreshed.Tokens.ExpiresAt;
        issuer = refreshed.Issuer;
        response = await GetTargetsAsync(url, token, cancellationToken);
        return new AuthorizedResponse(response, token, expiresAt, issuer);
    }

    private async Task<HttpResponseMessage> GetTargetsAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, url.TrimEnd('/') + "/api/tunnels/targets");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task StartAsync(
        string url,
        int serverId,
        int targetId,
        int? localPort)
    {
        Session session;
        TunnelServer server;
        TunnelEndpoint endpoint;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(url, out session!))
            {
                Send(new
                {
                    type = "tunnels.error",
                    message = "Carica prima i target dell'istanza."
                });
                return;
            }

            server = session.Targets
                .SingleOrDefault(x => x.ServerId == serverId)!;
            endpoint = server?.Targets
                .SingleOrDefault(x => x.ServerApplicationId == targetId)!;
            if (server is null || endpoint is null)
            {
                Send(new
                {
                    type = "tunnels.error",
                    message = "Target non più disponibile; ricarica i target."
                });
                return;
            }
        }

        var port = localPort ?? endpoint.SuggestedLocalPort ?? PortAllocator.FindFree();
        if (port is < 1024 or > 65_535)
        {
            Send(new
            {
                type = "tunnels.error",
                message = "La porta locale deve essere tra 1024 e 65535."
            });
            return;
        }

        if (!PortAllocator.IsFree(port))
        {
            Send(new
            {
                type = "tunnels.error",
                message = $"La porta locale {port} è già in uso."
            });
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        Process process;
        try
        {
            process = new Process
            {
                StartInfo = SshTunnel.BuildStartInfo(server, endpoint, port)
            };
            process.Start();
        }
        catch (Exception exception)
        {
            await SendEventAsync(
                session, server, endpoint, port, "error", correlationId,
                "spawn_failed", exception.Message);
            Send(new
            {
                type = "tunnels.error",
                message = $"Avvio di ssh non riuscito: {exception.Message}"
            });
            return;
        }

        var tunnel = new TunnelProcess(
            id, session, server, endpoint, port, correlationId, process);
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                tunnel.AppendError(args.Data);
        };
        process.BeginErrorReadLine();

        lock (_gate)
        {
            _active[id] = tunnel;
        }

        await SendEventAsync(
            session, server, endpoint, port, "start", correlationId,
            null, null);
        Send(new { type = "tunnels.state", items = ActiveItems() });
        Send(new
        {
            type = "tunnels.log",
            message = $"Tunnel {endpoint.Application} su 127.0.0.1:{port} attivo."
        });

        _ = MonitorAsync(tunnel);
    }

    public Task StopAsync(string id)
    {
        TunnelProcess? tunnel;
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out tunnel))
                return Task.CompletedTask;
            tunnel.StopRequested = true;
        }

        try
        {
            tunnel.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited; the monitor sends the final event.
        }

        return Task.CompletedTask;
    }

    public void StopAll()
    {
        CancelLogin();
        TunnelProcess[] tunnels;
        lock (_gate)
        {
            tunnels = _active.Values.ToArray();
            foreach (var tunnel in tunnels)
                tunnel.StopRequested = true;
        }

        foreach (var tunnel in tunnels)
        {
            try
            {
                tunnel.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }

    private async Task MonitorAsync(TunnelProcess tunnel)
    {
        try
        {
            await tunnel.Process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Process already gone.
        }

        var error = tunnel.StopRequested
            ? null
            : tunnel.ErrorText() is { Length: > 0 } text
                ? text
                : $"ssh è terminato con codice {tunnel.Process.ExitCode}.";

        lock (_gate)
        {
            _active.Remove(tunnel.Id);
        }

        await SendEventAsync(
            tunnel.Session,
            tunnel.Server, tunnel.Endpoint, tunnel.LocalPort,
            tunnel.StopRequested ? "stop" : "error",
            tunnel.CorrelationId,
            error is null ? null : "ssh_exit",
            error);

        Send(new { type = "tunnels.state", items = ActiveItems() });
        Send(new
        {
            type = "tunnels.log",
            message = error is null
                ? $"Tunnel su 127.0.0.1:{tunnel.LocalPort} chiuso."
                : $"Tunnel su 127.0.0.1:{tunnel.LocalPort} terminato: {error}"
        });

        tunnel.Process.Dispose();
    }

    private async Task SendEventAsync(
        Session session,
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort,
        string kind,
        string correlationId,
        string? errorCode,
        string? detail)
    {
        try
        {
            using var response = await SendEventWithRefreshAsync(
                session, server, endpoint, localPort, kind, correlationId,
                errorCode, detail);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Keep "Accedi di nuovo" as the final message instead of a
                // generic 401 log line that would overwrite it in the panel.
                SendAuthError(
                    session.Url,
                    "Sessione non più valida. Accedi di nuovo.",
                    requiresLogin: true);
            }
            else if (!response.IsSuccessStatusCode)
            {
                Send(new
                {
                    type = "tunnels.log",
                    message = $"Evento tunnel non registrato ({(int)response.StatusCode})."
                });
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            Send(new
            {
                type = "tunnels.log",
                message = $"Evento tunnel non registrato: {exception.Message}"
            });
        }
    }

    private async Task<HttpResponseMessage> SendEventWithRefreshAsync(
        Session session,
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort,
        string kind,
        string correlationId,
        string? errorCode,
        string? detail)
    {
        if (!await EnsureSessionTokenAsync(session, CancellationToken.None))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var token = session.Token;
        var response = await PostEventAsync(
            session, token, server, endpoint, localPort, kind,
            correlationId, errorCode, detail);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !session.CanRefresh)
            return response;

        response.Dispose();
        if (!await RefreshSessionAsync(session, token, CancellationToken.None))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        return await PostEventAsync(
            session, session.Token, server, endpoint, localPort, kind,
            correlationId, errorCode, detail);
    }

    private async Task<HttpResponseMessage> PostEventAsync(
        Session session,
        string token,
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort,
        string kind,
        string correlationId,
        string? errorCode,
        string? detail)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, session.EventUrl);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            kind,
            serverId = server.ServerId,
            serverApplicationId = endpoint.ServerApplicationId,
            targetHost = endpoint.TargetHost,
            targetPort = endpoint.TargetPort,
            localPort,
            errorCode,
            detail,
            correlationId
        });

        return await http.SendAsync(request);
    }

    /// <summary>
    /// Refreshes the session token when it is about to expire, so a tunnel
    /// event does not fail with a stale token.
    /// </summary>
    private async Task<bool> EnsureSessionTokenAsync(
        Session session, CancellationToken cancellationToken)
    {
        if (!session.CanRefresh || !IsExpiring(session.ExpiresAt))
            return true;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsExpiring(session.ExpiresAt))
                return true;

            var refreshed = await RefreshTokensCoreAsync(
                session.Url, session.Issuer, cancellationToken);
            if (refreshed is null)
                return false;

            UpdateSession(session, refreshed);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Refreshes once after a 401. When another request already refreshed the
    /// token, it only retries with the newer value.
    /// </summary>
    private async Task<bool> RefreshSessionAsync(
        Session session, string staleToken, CancellationToken cancellationToken)
    {
        if (!session.CanRefresh)
            return false;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!string.Equals(session.Token, staleToken, StringComparison.Ordinal))
                    return true;
            }

            var refreshed = await RefreshTokensCoreAsync(
                session.Url, session.Issuer, cancellationToken);
            if (refreshed is null)
                return false;

            UpdateSession(session, refreshed);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void UpdateSession(Session session, RefreshedTokens refreshed)
    {
        lock (_gate)
        {
            session.Token = refreshed.Tokens.AccessToken;
            session.ExpiresAt = refreshed.Tokens.ExpiresAt;
            session.Issuer = refreshed.Issuer;
        }
    }

    private async Task<RefreshedTokens?> RefreshTokensForUrlAsync(
        string url, string? issuer, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshTokensCoreAsync(url, issuer, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Exchanges the in-memory refresh token for new tokens and persists a
    /// rotated refresh token. Auth failures clear the stored session; a
    /// transient network failure keeps it so a retry can work.
    /// </summary>
    private async Task<RefreshedTokens?> RefreshTokensCoreAsync(
        string url, string? issuer, CancellationToken cancellationToken)
    {
        string? refreshToken;
        lock (_gate)
        {
            _refreshTokens.TryGetValue(url, out refreshToken);
        }

        if (string.IsNullOrEmpty(refreshToken))
        {
            await ClearSessionAsync(url);
            SendAuthError(
                url,
                "Sessione non disponibile. Accedi di nuovo.",
                requiresLogin: true);
            return null;
        }

        try
        {
            issuer ??= await _auth.DiscoverIssuerAsync(url, cancellationToken);
            var tokens = await _auth.RefreshAsync(
                issuer, refreshToken, cancellationToken);
            var next = tokens.RefreshToken is { Length: > 0 } value
                ? value
                : refreshToken;

            lock (_gate)
            {
                _refreshTokens[url] = next;
            }
            await SaveRefreshTokenAsync(url, next);
            return new RefreshedTokens(tokens, issuer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeviceAuthException)
        {
            await ClearSessionAsync(url);
            SendAuthError(
                url,
                "Sessione scaduta. Accedi di nuovo.",
                requiresLogin: true);
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or
            JsonException)
        {
            SendAuthError(
                url,
                "Rinnovo della sessione non riuscito. Riprova.");
            return null;
        }
    }

    private async Task<bool> SaveRefreshTokenAsync(
        string url, string refreshToken)
    {
        try
        {
            await _tokens.SaveAsync(url, refreshToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SendWarning(
                "Salvataggio della sessione non riuscito. " +
                "La sessione resta valida solo per questa esecuzione.");
            return false;
        }

        return _tokens.IsPersistent;
    }

    private async Task ClearSessionAsync(string url)
    {
        lock (_gate)
        {
            _sessions.Remove(url);
            _refreshTokens.Remove(url);
        }

        try
        {
            await _tokens.DeleteAsync(url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SendWarning("Rimozione della sessione salvata non riuscita.");
        }
    }

    private static bool IsExpiring(DateTimeOffset? expiresAt) =>
        expiresAt is { } value &&
        value - DateTimeOffset.UtcNow <= RefreshWindow;

    private object[] ActiveItems()
    {
        lock (_gate)
        {
            return _active.Values
                .OrderBy(x => x.LocalPort)
                .Select(x => (object)new
                {
                    id = x.Id,
                    instanceName = x.Session.Name,
                    serverName = x.Server.ServerName,
                    application = x.Endpoint.Application,
                    localPort = x.LocalPort,
                    targetPort = x.Endpoint.TargetPort
                })
                .ToArray();
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Campo {name} mancante.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private void Send(object payload)
    {
        try
        {
            notify(payload);
        }
        catch (Exception)
        {
            // The window may be closing; nothing left to notify.
        }
    }

    private sealed class Session(
        string url,
        string name,
        string token,
        DateTimeOffset? expiresAt,
        string? issuer,
        bool canRefresh,
        IReadOnlyList<TunnelServer> targets)
    {
        public string Url { get; } = url;
        public string Name { get; } = name;
        public string Token { get; set; } = token;
        public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;
        public string? Issuer { get; set; } = issuer;
        public bool CanRefresh { get; } = canRefresh;
        public IReadOnlyList<TunnelServer> Targets { get; } = targets;
        public string EventUrl => Url.TrimEnd('/') + "/api/tunnels/events";
    }

    private sealed record RefreshedTokens(DeviceTokens Tokens, string Issuer);

    private sealed record AuthorizedResponse(
        HttpResponseMessage Response,
        string Token,
        DateTimeOffset? ExpiresAt,
        string? Issuer) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }

    private sealed class TunnelProcess(
        string id,
        Session session,
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort,
        string correlationId,
        Process process)
    {
        private readonly Lock _errorGate = new();
        private readonly StringBuilder _error = new();

        public string Id { get; } = id;
        public Session Session { get; } = session;
        public TunnelServer Server { get; } = server;
        public TunnelEndpoint Endpoint { get; } = endpoint;
        public int LocalPort { get; } = localPort;
        public string CorrelationId { get; } = correlationId;
        public Process Process { get; } = process;
        public bool StopRequested { get; set; }

        public void AppendError(string line)
        {
            lock (_errorGate)
            {
                if (_error.Length < 2000)
                    _error.AppendLine(line);
            }
        }

        public string ErrorText()
        {
            lock (_errorGate)
            {
                return _error.ToString().Trim();
            }
        }
    }
}
