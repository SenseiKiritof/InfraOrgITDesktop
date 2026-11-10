using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;

namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Owns the tunnel sessions of the desktop app. Tokens and targets stay in
/// memory; the allowlist comes from the instance API, and each ssh process is
/// tracked until it exits. UI messages are delivered through <c>notify</c>.
/// </summary>
public sealed class TunnelManager(
    HttpClient http,
    Action<object> notify,
    DeviceAuthClient? auth = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly DeviceAuthClient _auth = auth ?? new DeviceAuthClient(http);
    private readonly Lock _gate = new();
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
                    await LoadTargetsAsync(
                        RequiredString(root, "url"),
                        RequiredString(root, "name"),
                        RequiredString(root, "token"));
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
            HttpRequestException or TaskCanceledException)
        {
            Send(new { type = "tunnels.error", message = exception.Message });
        }
    }

    /// <summary>
    /// Starts the Keycloak device flow for the instance, reports the user code
    /// to the UI and, once the token arrives, loads the tunnel allowlist. The
    /// previous poll is cancelled when a new login starts.
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

            lock (_gate)
            {
                if (tokens.RefreshToken is { Length: > 0 } refreshToken)
                    _refreshTokens[url] = refreshToken;
                _verificationUrls.Clear();
            }

            Send(new
            {
                type = "tunnels.auth",
                status = "success",
                url,
                message = "Accesso riuscito. Caricamento target…"
            });

            await LoadTargetsAsync(url, name, tokens.AccessToken);
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
    /// Refresh token kept in memory for the next iteration that wires
    /// automatic refresh. It is never persisted.
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

    private void SendAuthError(string url, string message) =>
        Send(new
        {
            type = "tunnels.auth",
            status = "error",
            url,
            message
        });

    public async Task LoadTargetsAsync(string url, string name, string token)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            Send(new { type = "tunnels.error", message = "Inserisci il token personale." });
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, url.TrimEnd('/') + "/api/tunnels/targets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Send(new
            {
                type = "tunnels.error",
                message = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized =>
                        "Token non valido o scaduto.",
                    System.Net.HttpStatusCode.Forbidden =>
                        "Serve un token con tunnels:operate e il ruolo operatore sull'ambiente.",
                    _ => $"L'istanza ha risposto {(int)response.StatusCode}."
                }
            });
            return;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TunnelTargetsResponse>(JsonOptions);
        if (payload is null)
        {
            Send(new { type = "tunnels.error", message = "Risposta target non valida." });
            return;
        }

        lock (_gate)
        {
            _sessions[url] = new Session(url, name, token, payload.Items);
        }

        Send(new
        {
            type = "tunnels.targets",
            url,
            items = payload.Items
        });
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
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            session.EventUrl);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", session.Token);
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

        try
        {
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
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

    private sealed record Session(
        string Url,
        string Name,
        string Token,
        IReadOnlyList<TunnelServer> Targets)
    {
        public string EventUrl => Url.TrimEnd('/') + "/api/tunnels/events";
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
