using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Instances;

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
    ITokenStore? tokenStore = null,
    TimeSpan? heartbeatInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? reconnectDelay = null,
    TunnelsStateStore? state = null)
{
    /// <summary>Refresh this long before the access token expires.</summary>
    internal static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>How often an active tunnel refreshes its live session.</summary>
    internal static readonly TimeSpan DefaultHeartbeatInterval =
        TimeSpan.FromSeconds(30);

    /// <summary>Wait between automatic reconnect attempts.</summary>
    internal static readonly TimeSpan DefaultReconnectDelay =
        TimeSpan.FromSeconds(5);

    /// <summary>Maximum automatic reconnect attempts per failure chain.</summary>
    internal const int MaxReconnectAttempts = 3;

    /// <summary>
    /// A tunnel that stayed up this long starts a fresh reconnect budget.
    /// </summary>
    private static readonly TimeSpan ReconnectResetAfter =
        TimeSpan.FromMinutes(1);

    private const int SessionDetailLength = 200;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly Regex BearerPattern = new(
        @"\bbearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretAssignmentPattern = new(
        @"\b(token|password|passphrase|secret)\s*=\s*[^\s&;]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TimeSpan _heartbeatInterval =
        heartbeatInterval ?? DefaultHeartbeatInterval;
    private readonly TimeSpan _reconnectDelay =
        reconnectDelay ?? DefaultReconnectDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? ((time, cancellationToken) => Task.Delay(time, cancellationToken));
    private readonly DeviceAuthClient _auth = auth ?? new DeviceAuthClient(http);
    private readonly ITokenStore _tokens = tokenStore ?? new MemoryTokenStore();
    private readonly TunnelsStateStore _state = state ?? new TunnelsStateStore(null);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _autoStartGate = new(1, 1);
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TunnelProcess> _active =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetryEntry> _retries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refreshTokens =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _verificationUrls =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _loginCancellation;
    private bool _stopping;
    private bool _stateWriteWarned;

    /// <summary>Test seam: the default starts a real ssh process.</summary>
    internal Func<ProcessStartInfo, ISshProcess> SshProcessFactory { get; init; } =
        startInfo => new SystemSshProcess(startInfo);

    /// <summary>
    /// Starts services marked <c>autoStart</c> after every targets load. The
    /// UI toggle clears it for the current app run.
    /// </summary>
    public bool AutoStartEnabled { get; set; } = true;

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
                    CancelRetriesExcept(listUrl);
                    ApplyAutoStart(root);
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
                    var restoreUrl = RequiredString(root, "url");
                    CancelRetriesExcept(restoreUrl);
                    ApplyAutoStart(root);
                    await RestoreSessionAsync(
                        restoreUrl,
                        RequiredString(root, "name"));
                    break;

                case "tunnels.logout":
                    await LogoutAsync(RequiredString(root, "url"));
                    break;

                case "tunnels.login":
                    var loginUrl = RequiredString(root, "url");
                    CancelRetriesExcept(loginUrl);
                    ApplyAutoStart(root);
                    await LoginWithDeviceCodeAsync(
                        loginUrl,
                        RequiredString(root, "name"));
                    break;

                case "tunnels.login.cancel":
                    CancelLogin();
                    break;

                case "tunnels.retries.cancel":
                    CancelRetries(RequiredString(root, "url"));
                    break;

                case "tunnels.start":
                    await StartAsync(
                        RequiredString(root, "url"),
                        root.GetProperty("serverId").GetInt32(),
                        OptionalInt(root, "targetId"),
                        OptionalInt(root, "serviceId"),
                        OptionalInt(root, "localPort"));
                    break;

                case "tunnels.stop":
                    await StopAsync(RequiredString(root, "id"));
                    break;

                case "tunnels.forget":
                    ForgetPersisted();
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
        CancelRetries(url);
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
    /// Restores the tunnels saved in the state file for the configured
    /// instances. Each instance needs a stored refresh token: without a valid
    /// session the saved identities stay in place, one log line is sent and the
    /// app waits for a later sign-in. No browser login is ever started here.
    /// Returns the number of tunnels that were started.
    /// </summary>
    public async Task<int> RestoreSavedTunnelsAsync(
        IReadOnlyList<Instance> instances)
    {
        IReadOnlyList<TunnelStateEntry> saved;
        try
        {
            saved = _state.Load();
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
            return 0;
        }

        if (saved.Count == 0)
        {
            Send(new { type = "tunnels.restored", count = 0 });
            return 0;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            var url = TunnelStateEntry.NormalizeUrl(instance.Url);
            if (url.Length > 0)
                names[url] = instance.Name;
        }

        var welcomed = saved
            .Where(x => names.ContainsKey(x.InstanceUrl))
            .Select(x => x.InstanceUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = 0;
        foreach (var url in welcomed)
            total += await RestoreSavedInstanceAsync(url, names[url]);

        Send(new { type = "tunnels.restored", count = total });
        return total;
    }

    /// <summary>
    /// Drops every saved tunnel identity. Running tunnels keep running; only
    /// the next app start changes.
    /// </summary>
    public void ForgetPersisted()
    {
        try
        {
            _state.Clear();
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
            return;
        }

        SendLog(
            "Tunnel salvati dimenticati: non saranno ripristinati al prossimo avvio.");
    }

    private async Task<int> RestoreSavedInstanceAsync(string url, string name)
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
            SendLog(
                $"Tunnel salvati per \"{name}\": sessione non disponibile, " +
                "ripristino al prossimo accesso.");
            return 0;
        }

        lock (_gate)
        {
            _refreshTokens[url] = stored;
        }

        var refreshed = await RefreshTokensForUrlAsync(
            url, null, CancellationToken.None, notify: false);
        if (refreshed is null)
        {
            SendLog(
                $"Tunnel salvati per \"{name}\": sessione non più valida, " +
                "ripristino al prossimo accesso.");
            return 0;
        }

        var before = ActiveCountFor(url);
        await LoadTargetsAsync(
            url,
            name,
            refreshed.Tokens.AccessToken,
            refreshed.Tokens.ExpiresAt,
            refreshed.Issuer,
            canRefresh: true,
            CancellationToken.None,
            silent: true);
        return Math.Max(0, ActiveCountFor(url) - before);
    }

    private int ActiveCountFor(string url)
    {
        lock (_gate)
        {
            return _active.Values.Count(x =>
                string.Equals(x.Session.Url, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Forgets the instance session: in-memory tokens, targets and the
    /// refresh token in the platform store. Manual tokens keep working.
    /// </summary>
    public async Task LogoutAsync(string url)
    {
        CancelLogin();
        await ClearSessionAsync(url);
        RemovePersistedForUrl(url);
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

    private void SendLog(string message) =>
        Send(new { type = "tunnels.log", message });

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
        CancellationToken cancellationToken,
        bool silent = false)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            ReportTargetsError("Inserisci il token personale.", silent);
            return;
        }

        // A reload replaces the allowlist and may autoStart the service
        // again: drop a pending reconnect first so it cannot start twice.
        CancelRetries(url);

        using var result = await SendTargetsAsync(
            url, token, expiresAt, issuer, canRefresh, cancellationToken, silent);
        if (result is null)
        {
            if (silent)
            {
                SendLog(
                    "Tunnel salvati: sessione non più valida, " +
                    "ripristino al prossimo accesso.");
            }
            return;
        }

        var response = result.Response;
        if (!response.IsSuccessStatusCode)
        {
            ReportTargetsError(
                response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        "Token non valido o scaduto.",
                    HttpStatusCode.Forbidden =>
                        "Serve un token con tunnels:operate e il ruolo operatore sull'ambiente.",
                    _ => $"L'istanza ha risposto {(int)response.StatusCode}."
                },
                silent);
            return;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TunnelTargetsResponse>(JsonOptions, cancellationToken);
        if (payload is null)
        {
            ReportTargetsError("Risposta target non valida.", silent);
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

        ReconcileStoredTargets(url, payload.Items);
        var savedKeys = StoredKeysFor(url);
        await AutoStartAsync(url, payload.Items, cancellationToken);
        await RestoreStoredTunnelsAsync(
            url, payload.Items, savedKeys, cancellationToken);
    }

    /// <summary>
    /// Identities saved before this targets load. AutoStart services started
    /// during the same load are not part of it, so the restore pass cannot
    /// race their first process or a reconnect.
    /// </summary>
    private HashSet<string> StoredKeysFor(string url)
    {
        try
        {
            return _state.Load()
                .Where(x => string.Equals(
                    x.InstanceUrl, url, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
            return [];
        }
    }

    private void ReportTargetsError(string message, bool silent)
    {
        if (silent)
            SendLog("Tunnel salvati: " + message);
        else
            Send(new { type = "tunnels.error", message });
    }

    /// <summary>
    /// Drops saved identities that the instance allowlist no longer returns,
    /// or that belong to a disabled service. The API stays authoritative.
    /// </summary>
    private void ReconcileStoredTargets(
        string url,
        IReadOnlyList<TunnelServer> servers)
    {
        IReadOnlyList<TunnelStateEntry> saved;
        try
        {
            saved = _state.Load();
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
            return;
        }

        var stale = saved
            .Where(x =>
                string.Equals(x.InstanceUrl, url, StringComparison.OrdinalIgnoreCase) &&
                !ExistsInTargets(x, servers))
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (stale.Count == 0)
            return;

        try
        {
            _state.RemoveWhere(x => stale.Contains(x.Key));
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
            return;
        }

        SendLog(
            stale.Count == 1
                ? "Rimosso 1 tunnel salvato non più disponibile."
                : $"Rimossi {stale.Count} tunnel salvati non più disponibili.");
    }

    private static bool ExistsInTargets(
        TunnelStateEntry entry,
        IReadOnlyList<TunnelServer> servers)
    {
        var server = servers.FirstOrDefault(x => x.ServerId == entry.ServerId);
        if (server is null)
            return false;

        if (entry.ServiceId is int serviceId)
        {
            return (server.Services ?? [])
                .Any(x => x.Id == serviceId && x.Enabled);
        }

        return entry.ServerApplicationId is int applicationId &&
            server.Targets.Any(x => x.ServerApplicationId == applicationId);
    }

    /// <summary>
    /// Starts every saved identity that is not active yet. Runs after each
    /// targets load so a later sign-in can retry; an already active tunnel is
    /// never started twice.
    /// </summary>
    private async Task RestoreStoredTunnelsAsync(
        string url,
        IReadOnlyList<TunnelServer> servers,
        IReadOnlySet<string> savedKeys,
        CancellationToken cancellationToken)
    {
        await _restoreGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<TunnelStateEntry> saved;
            try
            {
                saved = _state.Load();
            }
            catch (Exception exception) when (IsStateStoreFailure(exception))
            {
                WarnStateStore();
                return;
            }

            foreach (var entry in saved.Where(x =>
                         savedKeys.Contains(x.Key) &&
                         string.Equals(
                             x.InstanceUrl, url,
                             StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsEntryActive(url, entry) ||
                    IsRetryPending(url, entry) ||
                    !ExistsInTargets(entry, servers))
                {
                    continue;
                }

                try
                {
                    await StartAsync(
                        url,
                        entry.ServerId,
                        entry.ServerApplicationId,
                        entry.ServiceId,
                        entry.LocalPort,
                        automatic: true);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SendLog(
                        "Ripristino del tunnel salvato non riuscito: " +
                        exception.Message);
                }
            }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private bool IsEntryActive(string url, TunnelStateEntry entry)
    {
        lock (_gate)
        {
            return _active.Values.Any(x =>
                string.Equals(
                    x.Session.Url, url, StringComparison.OrdinalIgnoreCase) &&
                (entry.ServiceId is int serviceId
                    ? x.Spec.Service?.Id == serviceId
                    : entry.ServerApplicationId is int applicationId &&
                      x.Spec.Endpoint?.ServerApplicationId == applicationId));
        }
    }

    private bool IsRetryPending(string url, TunnelStateEntry entry)
    {
        if (entry.ServiceId is not int serviceId)
            return false;

        lock (_gate)
        {
            return _retries.ContainsKey(RetryKey(url, serviceId));
        }
    }

    /// <summary>
    /// Starts each enabled autoStart service once per targets load, in API
    /// order. A service already running is skipped and a failure only logs,
    /// so one bad service cannot stop the others.
    /// </summary>
    private async Task AutoStartAsync(
        string url,
        IReadOnlyList<TunnelServer> servers,
        CancellationToken cancellationToken)
    {
        if (!AutoStartEnabled)
            return;

        await _autoStartGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var server in servers)
            {
                foreach (var service in server.Services ?? [])
                {
                    if (!service.Enabled || !service.AutoStart)
                        continue;

                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsServiceActive(url, service.Id))
                        continue;

                    try
                    {
                        await StartAsync(
                            url, server.ServerId, null, service.Id, null,
                            automatic: true);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        SendLog(
                            $"Avvio automatico di \"{service.Name}\" non riuscito: " +
                            exception.Message);
                    }
                }
            }
        }
        finally
        {
            _autoStartGate.Release();
        }
    }

    private bool IsServiceActive(string url, int serviceId)
    {
        lock (_gate)
        {
            return _active.Values.Any(x =>
                string.Equals(x.Session.Url, url, StringComparison.OrdinalIgnoreCase) &&
                x.Spec.Service?.Id == serviceId);
        }
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
        CancellationToken cancellationToken,
        bool silent = false)
    {
        if (canRefresh && IsExpiring(expiresAt))
        {
            var early = await RefreshTokensForUrlAsync(
                url, issuer, cancellationToken, notify: !silent);
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
            url, issuer, cancellationToken, notify: !silent);
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

    public Task StartAsync(
        string url,
        int serverId,
        int targetId,
        int? localPort) =>
        StartAsync(url, serverId, targetId, null, localPort);

    public async Task StartAsync(
        string url,
        int serverId,
        int? targetId,
        int? serviceId,
        int? localPort,
        bool automatic = false)
    {
        if ((targetId is null) == (serviceId is null))
        {
            ReportStartFailure(
                "Indica un deployment o un servizio tunnel, non entrambi.",
                automatic);
            return;
        }

        Session session;
        TunnelServer? server;
        TunnelEndpoint? endpoint = null;
        TunnelService? service = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(url, out session!))
            {
                ReportStartFailure(
                    "Carica prima i target dell'istanza.", automatic);
                return;
            }

            server = session.Targets.SingleOrDefault(x => x.ServerId == serverId);
            if (targetId is int deploymentId)
            {
                endpoint = server?.Targets.SingleOrDefault(
                    x => x.ServerApplicationId == deploymentId);
            }
            else if (serviceId is int customServiceId)
            {
                service = (server?.Services ?? [])
                    .SingleOrDefault(x => x.Id == customServiceId);
            }
        }

        if (server is null ||
            (targetId is not null && endpoint is null) ||
            (serviceId is not null && service is null))
        {
            ReportStartFailure(
                serviceId is null
                    ? "Target non più disponibile; ricarica i target."
                    : "Servizio tunnel non più disponibile; ricarica i target.",
                automatic);
            return;
        }

        if (!TunnelStartSpec.TryCreate(
                server, endpoint, service, localPort, out var spec, out var error))
        {
            ReportStartFailure(
                error ?? "Configurazione del tunnel non valida.", automatic);
            return;
        }

        var activeSpec = spec!;
        if (activeSpec.Service is { } activeService &&
            IsServiceActive(session.Url, activeService.Id))
        {
            SendLog($"Il servizio \"{activeService.Name}\" è già attivo.");
            return;
        }

        if (activeSpec.Service is { } retryingService)
            CancelRetries(session.Url, retryingService.Id);

        var tunnel = await StartTunnelAsync(session, activeSpec, automatic);
        if (tunnel is not null)
            PersistTunnel(session, tunnel.Spec, localPort);
    }

    /// <summary>
    /// Starts one ssh process for an already validated spec and publishes the
    /// start event, the live projection and the active state. Returns the new
    /// process, or null when the spawn failed or the service is already up.
    /// </summary>
    private async Task<TunnelProcess?> StartTunnelAsync(
        Session session,
        TunnelStartSpec spec,
        bool automatic,
        int reconnectAttempt = 0,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        if (spec.Service is { } activeService &&
            IsServiceActive(session.Url, activeService.Id))
        {
            SendLog($"Il servizio \"{activeService.Name}\" è già attivo.");
            return null;
        }

        var id = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        ISshProcess? process = null;
        TunnelProcess tunnel;
        try
        {
            process = SshProcessFactory(
                SshTunnel.BuildStartInfo(spec.Server, spec));
            tunnel = new TunnelProcess(
                id, session, spec, correlationId, process, reconnectAttempt);
            process.ErrorLine += tunnel.AppendError;
            process.Start();
        }
        catch (Exception exception)
        {
            process?.Dispose();
            await SendEventAsync(
                session, spec, "error", correlationId,
                "spawn_failed", ShortErrorDetail(exception.Message));
            ReportStartFailure(
                $"Avvio di ssh non riuscito: {exception.Message}", automatic);
            return null;
        }

        var cancelled = false;
        lock (_gate)
        {
            if (_stopping || cancellationToken.IsCancellationRequested)
                cancelled = true;
            else
                _active[id] = tunnel;
        }

        if (cancelled)
        {
            tunnel.StopRequested = true;
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process already exited before the cancel arrived.
            }
            process.Dispose();
            tunnel.Heartbeat.Dispose();
            return null;
        }

        await SendEventAsync(
            session, spec, "start", correlationId, null, null);
        var projectionActive = await SendSessionAsync(
            tunnel, "active", null, null, CancellationToken.None);
        Send(new { type = "tunnels.state", items = ActiveItems() });
        SendLog(ActiveMessage(spec));

        if (projectionActive)
            tunnel.HeartbeatTask = HeartbeatAsync(tunnel);
        _ = MonitorAsync(tunnel);
        return tunnel;
    }

    private void ReportStartFailure(string message, bool automatic)
    {
        if (automatic)
            SendLog("Avvio automatico non riuscito: " + message);
        else
            Send(new { type = "tunnels.error", message });
    }

    private static string ActiveMessage(TunnelStartSpec spec) =>
        spec.Mode switch
        {
            TunnelMode.Dynamic =>
                $"Proxy SOCKS {spec.Name} su 127.0.0.1:{spec.LocalPort} attivo.",
            TunnelMode.Remote =>
                $"Tunnel {spec.Name} sulla porta remota {spec.RemotePort} attivo.",
            _ => $"Tunnel {spec.Name} su 127.0.0.1:{spec.LocalPort} attivo."
        };

    public Task StopAsync(string id)
    {
        TunnelProcess? tunnel;
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out tunnel))
                return Task.CompletedTask;
            tunnel.StopRequested = true;
        }

        if (tunnel.Spec.Service is { } service)
            CancelRetries(tunnel.Session.Url, service.Id);

        try
        {
            tunnel.Process.Kill();
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
            _stopping = true;
            tunnels = _active.Values.ToArray();
            foreach (var tunnel in tunnels)
                tunnel.StopRequested = true;
        }

        CancelRetries();

        foreach (var tunnel in tunnels)
        {
            // An app exit keeps the saved identity for the next start; only a
            // user stop or a logout forgets it.
            tunnel.PreserveState = true;
            try
            {
                tunnel.Process.Kill();
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

        var stopped = tunnel.StopRequested;
        var error = stopped
            ? null
            : tunnel.ErrorText() is { Length: > 0 } text
                ? text
                : $"ssh è terminato con codice {tunnel.Process.ExitCode}.";
        var detail = error is null ? null : ShortErrorDetail(error);

        // Stop the heartbeat before the final status, so an active projection
        // cannot arrive after stopped/error.
        await tunnel.StopHeartbeatAsync();

        lock (_gate)
        {
            _active.Remove(tunnel.Id);
        }

        var projectionUsable = await SendSessionAsync(
            tunnel, stopped ? "stopped" : "error",
            stopped ? null : "ssh_exit", detail, CancellationToken.None);
        await SendEventAsync(
            tunnel.Session,
            tunnel.Spec,
            stopped ? "stop" : "error",
            tunnel.CorrelationId,
            error is null ? null : "ssh_exit",
            detail);

        Send(new { type = "tunnels.state", items = ActiveItems() });
        var closed = ClosedMessage(tunnel.Spec);
        Send(new
        {
            type = "tunnels.log",
            message = error is null ? closed : $"{closed} Dettaglio: {detail}"
        });

        tunnel.Heartbeat.Dispose();
        tunnel.Process.Dispose();

        if (!stopped)
        {
            var kept = projectionUsable && ScheduleReconnect(tunnel);
            if (!kept)
                RemovePersisted(tunnel.Session.Url, tunnel.Spec);
        }
        else if (!tunnel.PreserveState)
        {
            RemovePersisted(tunnel.Session.Url, tunnel.Spec);
        }
    }

    private static string ClosedMessage(TunnelStartSpec spec) =>
        spec.Mode switch
        {
            TunnelMode.Dynamic => $"Proxy SOCKS {spec.Name} chiuso.",
            TunnelMode.Remote =>
                $"Tunnel {spec.Name} sulla porta remota {spec.RemotePort} chiuso.",
            _ => $"Tunnel {spec.Name} su 127.0.0.1:{spec.LocalPort} chiuso."
        };

    /// <summary>
    /// Cancels pending reconnect delays, for one instance or all of them.
    /// Active tunnels keep running; only the automatic retries stop.
    /// </summary>
    public void CancelRetries(string? url = null)
    {
        List<CancellationTokenSource> cancelled = [];
        lock (_gate)
        {
            foreach (var (key, entry) in _retries.ToArray())
            {
                if (url is null ||
                    string.Equals(entry.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    _retries.Remove(key);
                    cancelled.Add(entry.Cancellation);
                }
            }
        }

        foreach (var cancellation in cancelled)
            cancellation.Cancel();
    }

    private void CancelRetriesExcept(string url)
    {
        List<CancellationTokenSource> cancelled = [];
        lock (_gate)
        {
            foreach (var (key, entry) in _retries.ToArray())
            {
                if (!string.Equals(
                        entry.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    _retries.Remove(key);
                    cancelled.Add(entry.Cancellation);
                }
            }
        }

        foreach (var cancellation in cancelled)
            cancellation.Cancel();
    }

    private void CancelRetries(string url, int serviceId)
    {
        CancellationTokenSource? cancelled = null;
        lock (_gate)
        {
            var key = RetryKey(url, serviceId);
            if (_retries.Remove(key, out var entry))
                cancelled = entry.Cancellation;
        }

        cancelled?.Cancel();
    }

    /// <summary>
    /// Schedules a reconnect only for an autoStart service that ended without
    /// a user stop and still exists in the current allowlist. Deployment
    /// tunnels and manual one-shot services never reconnect. Returns true when
    /// the saved identity must be kept: retrying, or the app is exiting.
    /// </summary>
    private bool ScheduleReconnect(TunnelProcess tunnel)
    {
        if (tunnel.Spec.Service is not { AutoStart: true, Enabled: true } service)
            return false;

        if (!IsRetryEligible(tunnel, out var blocked))
        {
            if (blocked is not null)
                SendLog(blocked);
            return false;
        }

        lock (_gate)
        {
            if (_stopping)
                return true;
        }

        var attempt = DateTimeOffset.UtcNow - tunnel.StartedAt >= ReconnectResetAfter
            ? 1
            : tunnel.ReconnectAttempt + 1;
        if (attempt > MaxReconnectAttempts)
        {
            SendLog(ReconnectGiveUpMessage(tunnel.Spec));
            return false;
        }

        var entry = GetOrCreateRetryEntry(tunnel.Session.Url, service.Id);
        _ = ReconnectAsync(tunnel, attempt, entry);
        return true;
    }

    private async Task ReconnectAsync(
        TunnelProcess failed, int attempt, RetryEntry entry)
    {
        var cancellationToken = entry.Cancellation.Token;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (!IsRetryEligible(failed, out var blocked))
            {
                if (blocked is not null)
                {
                    SendLog(blocked);
                    RemovePersisted(failed.Session.Url, failed.Spec);
                }
                return;
            }

            SendLog(ReconnectScheduledMessage(failed.Spec, attempt));
            try
            {
                await _delay(_reconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested ||
                !IsRetryEligible(failed, out blocked))
            {
                if (blocked is not null)
                {
                    SendLog(blocked);
                    RemovePersisted(failed.Session.Url, failed.Spec);
                }
                return;
            }

            if (!TryReconnectSpec(failed.Spec, out var spec, out var portError))
            {
                SendLog(portError!);
                RemovePersisted(failed.Session.Url, failed.Spec);
                return;
            }

            if (spec.LocalPort != failed.Spec.LocalPort)
            {
                SendLog(
                    $"Servizio \"{spec.Name}\": porta locale " +
                    $"{failed.Spec.LocalPort} occupata, uso {spec.LocalPort}.");
            }

            var tunnel = await StartTunnelAsync(
                failed.Session, spec, automatic: true,
                reconnectAttempt: attempt,
                cancellationToken: cancellationToken);
            if (tunnel is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await StopAsync(tunnel.Id);
                    return;
                }

                SendLog(ReconnectSuccessMessage(spec, attempt));
                return;
            }

            attempt++;
            if (attempt > MaxReconnectAttempts)
            {
                SendLog(ReconnectGiveUpMessage(failed.Spec));
                RemovePersisted(failed.Session.Url, failed.Spec);
                return;
            }
        }
    }

    private bool IsRetryEligible(TunnelProcess tunnel, out string? blocked)
    {
        blocked = null;
        lock (_gate)
        {
            if (tunnel.Session.AuthFailed)
                return false;

            if (!_sessions.TryGetValue(tunnel.Session.Url, out var current) ||
                !ReferenceEquals(current, tunnel.Session))
            {
                return false;
            }

            var service = current.Targets
                .Where(x => x.ServerId == tunnel.Spec.Server.ServerId)
                .SelectMany(x => x.Services ?? [])
                .SingleOrDefault(x => x.Id == tunnel.Spec.Service!.Id);
            if (service is { Enabled: true, AutoStart: true })
                return true;

            blocked =
                $"Servizio \"{tunnel.Spec.Name}\": non è più abilitato " +
                "per l'avvio automatico; riconnessione interrotta.";
            return false;
        }
    }

    /// <summary>
    /// Reuses the same spec except for an auto-allocated local port. A fixed
    /// service port that is now busy stops the retry with a clear message.
    /// </summary>
    private static bool TryReconnectSpec(
        TunnelStartSpec failed,
        out TunnelStartSpec spec,
        out string? error)
    {
        spec = failed;
        error = null;

        if (failed.Mode == TunnelMode.Remote || failed.LocalPort is not int port)
            return true;

        if (PortAllocator.IsFree(port))
            return true;

        if (failed.Service?.LocalPort is not null)
        {
            error =
                $"Servizio \"{failed.Name}\": la porta locale {port} è già " +
                "in uso; riconnessione interrotta.";
            return false;
        }

        spec = failed with { LocalPort = PortAllocator.FindFree() };
        return true;
    }

    private RetryEntry GetOrCreateRetryEntry(string url, int serviceId)
    {
        lock (_gate)
        {
            var key = RetryKey(url, serviceId);
            if (_retries.TryGetValue(key, out var existing) &&
                !existing.Cancellation.IsCancellationRequested)
            {
                return existing;
            }

            var entry = new RetryEntry(
                url, new CancellationTokenSource());
            _retries[key] = entry;
            return entry;
        }
    }

    private string ReconnectScheduledMessage(TunnelStartSpec spec, int attempt) =>
        $"Servizio \"{spec.Name}\" interrotto in modo inatteso. " +
        $"Riconnessione tra {_reconnectDelay.TotalSeconds:0.###} secondi " +
        $"(tentativo {attempt}/{MaxReconnectAttempts}).";

    private static string ReconnectSuccessMessage(
        TunnelStartSpec spec, int attempt) =>
        $"Servizio \"{spec.Name}\" riconnesso " +
        $"(tentativo {attempt}/{MaxReconnectAttempts}).";

    private static string ReconnectGiveUpMessage(TunnelStartSpec spec) =>
        $"Servizio \"{spec.Name}\": riconnessione non riuscita dopo " +
        $"{MaxReconnectAttempts} tentativi. Avvia di nuovo il servizio manualmente.";

    private static string RetryKey(string url, int serviceId) =>
        $"{url}\u001f{serviceId}";

    /// <summary>
    /// Keeps the central live projection fresh while ssh runs. Every failure
    /// is non-fatal; a rejected session stops the loop so it cannot retry the
    /// same expired credentials forever.
    /// </summary>
    private async Task HeartbeatAsync(TunnelProcess tunnel)
    {
        try
        {
            while (true)
            {
                await _delay(_heartbeatInterval, tunnel.Heartbeat.Token);
                if (!await SendSessionAsync(
                        tunnel, "active", null, null, tunnel.Heartbeat.Token))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (tunnel.Heartbeat.IsCancellationRequested)
        {
            // Stop requested or the tunnel exited.
        }
    }

    /// <summary>
    /// Upserts the live projection. A rejection is logged and ignored; it
    /// never stops the tunnel. Returns false when the session cannot be used
    /// again, so the heartbeat loop exits instead of repeating a 401 forever.
    /// </summary>
    private async Task<bool> SendSessionAsync(
        TunnelProcess tunnel,
        string status,
        string? errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendSessionWithRefreshAsync(
                tunnel, status, errorCode, detail, cancellationToken);
            if (response is null)
            {
                MarkAuthFailure(tunnel.Session);
                return false;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthFailure(tunnel.Session);
                SendAuthError(
                    tunnel.Session.Url,
                    "Sessione non più valida. Accedi di nuovo.",
                    requiresLogin: true);
                return false;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                MarkAuthFailure(tunnel.Session);
                SendLog("Permessi tunnel non più validi (403).");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                SendLog(
                    $"Stato tunnel non aggiornato ({(int)response.StatusCode}).");
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            SendLog($"Stato tunnel non aggiornato: {exception.Message}");
            return true;
        }
    }

    private async Task<HttpResponseMessage?> SendSessionWithRefreshAsync(
        TunnelProcess tunnel,
        string status,
        string? errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        var session = tunnel.Session;
        if (!await EnsureSessionTokenAsync(session, cancellationToken))
            return null;

        var token = session.Token;
        var response = await PostSessionAsync(
            tunnel, token, status, errorCode, detail, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            !session.CanRefresh)
        {
            return response;
        }

        response.Dispose();
        if (!await RefreshSessionAsync(session, token, cancellationToken))
            return null;

        return await PostSessionAsync(
            tunnel, session.Token, status, errorCode, detail,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> PostSessionAsync(
        TunnelProcess tunnel,
        string token,
        string status,
        string? errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, tunnel.Session.SessionUrl);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            sessionKey = tunnel.Id,
            status,
            serverId = tunnel.Spec.Server.ServerId,
            serverApplicationId = tunnel.Spec.Endpoint?.ServerApplicationId,
            mode = tunnel.Spec.Mode.ToString(),
            // The API exposes serviceId; the current request DTO binds
            // tunnelServiceId. Send both while the naming settles.
            serviceId = tunnel.Spec.Service?.Id,
            tunnelServiceId = tunnel.Spec.Service?.Id,
            targetHost = tunnel.Spec.TargetHost,
            targetPort = tunnel.Spec.TargetPort,
            localPort = tunnel.Spec.LocalPort,
            remotePort = tunnel.Spec.RemotePort,
            errorCode,
            detail
        });

        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// First non-empty stderr line, redacted and capped: the live projection
    /// and the audit event must not carry the whole ssh output.
    /// </summary>
    private static string ShortErrorDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "ssh exited";

        var line = value
            .Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? value.Trim();
        line = BearerPattern.Replace(line, "Bearer ***");
        line = SecretAssignmentPattern.Replace(line, "$1=***");
        return line.Length <= SessionDetailLength
            ? line
            : line[..SessionDetailLength];
    }

    private async Task SendEventAsync(
        Session session,
        TunnelStartSpec spec,
        string kind,
        string correlationId,
        string? errorCode,
        string? detail)
    {
        try
        {
            using var response = await SendEventWithRefreshAsync(
                session, spec, kind, correlationId, errorCode, detail);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Keep "Accedi di nuovo" as the final message instead of a
                // generic 401 log line that would overwrite it in the panel.
                MarkAuthFailure(session);
                SendAuthError(
                    session.Url,
                    "Sessione non più valida. Accedi di nuovo.",
                    requiresLogin: true);
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                MarkAuthFailure(session);
                Send(new
                {
                    type = "tunnels.log",
                    message = "Evento tunnel non registrato (403)."
                });
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
        TunnelStartSpec spec,
        string kind,
        string correlationId,
        string? errorCode,
        string? detail)
    {
        if (!await EnsureSessionTokenAsync(session, CancellationToken.None))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var token = session.Token;
        var response = await PostEventAsync(
            session, token, spec, kind, correlationId, errorCode, detail);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !session.CanRefresh)
            return response;

        response.Dispose();
        if (!await RefreshSessionAsync(session, token, CancellationToken.None))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        return await PostEventAsync(
            session, session.Token, spec, kind, correlationId,
            errorCode, detail);
    }

    private async Task<HttpResponseMessage> PostEventAsync(
        Session session,
        string token,
        TunnelStartSpec spec,
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
            serverId = spec.Server.ServerId,
            serverApplicationId = spec.Endpoint?.ServerApplicationId,
            mode = spec.Mode.ToString(),
            serviceId = spec.Service?.Id,
            tunnelServiceId = spec.Service?.Id,
            targetHost = spec.TargetHost,
            targetPort = spec.TargetPort,
            localPort = spec.LocalPort,
            remotePort = spec.RemotePort,
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

    private void MarkAuthFailure(Session session)
    {
        lock (_gate)
        {
            session.AuthFailed = true;
        }
    }

    private async Task<RefreshedTokens?> RefreshTokensForUrlAsync(
        string url,
        string? issuer,
        CancellationToken cancellationToken,
        bool notify = true)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshTokensCoreAsync(
                url, issuer, cancellationToken, notify);
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
        string url, string? issuer, CancellationToken cancellationToken,
        bool notify = true)
    {
        string? refreshToken;
        lock (_gate)
        {
            _refreshTokens.TryGetValue(url, out refreshToken);
        }

        if (string.IsNullOrEmpty(refreshToken))
        {
            await ClearSessionAsync(url);
            if (notify)
            {
                SendAuthError(
                    url,
                    "Sessione non disponibile. Accedi di nuovo.",
                    requiresLogin: true);
            }
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
            if (notify)
            {
                SendAuthError(
                    url,
                    "Sessione scaduta. Accedi di nuovo.",
                    requiresLogin: true);
            }
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or
            JsonException)
        {
            if (notify)
                SendAuthError(url, "Rinnovo della sessione non riuscito. Riprova.");
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
        CancelRetries(url);
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
                .OrderBy(x => x.Spec.LocalPort ?? int.MaxValue)
                .ThenBy(x => x.Spec.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => (object)new
                {
                    id = x.Id,
                    instanceName = x.Session.Name,
                    serverName = x.Spec.Server.ServerName,
                    application = x.Spec.Name,
                    serviceId = x.Spec.Service?.Id,
                    mode = x.Spec.Mode.ToString(),
                    kind = x.Spec.Kind.ToString(),
                    targetHost = x.Spec.TargetHost,
                    targetPort = x.Spec.TargetPort,
                    localPort = x.Spec.LocalPort,
                    remotePort = x.Spec.RemotePort,
                    openUrl = x.Spec.OpenUrl
                })
                .ToArray();
        }
    }

    /// <summary>
    /// True only for a browser URL generated by an active local HTTP(S)
    /// tunnel. The manager bridge uses it to keep the generic open-external
    /// path from launching arbitrary http addresses.
    /// </summary>
    public bool IsTunnelOpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
            return false;

        lock (_gate)
        {
            return _active.Values.Any(x =>
                x.Spec.OpenUrl is { } open &&
                Uri.TryCreate(open, UriKind.Absolute, out var expected) &&
                string.Equals(
                    expected.AbsoluteUri, candidate.AbsoluteUri,
                    StringComparison.Ordinal));
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

    private static int? OptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var value)
            ? value
            : null;

    private void ApplyAutoStart(JsonElement root)
    {
        if (root.TryGetProperty("autoStart", out var element) &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            AutoStartEnabled = element.GetBoolean();
        }
    }

    /// <summary>
    /// Saves the identity of a successful start. Nothing secret is written:
    /// only ids the instance must confirm again, the mode and the requested
    /// local port.
    /// </summary>
    private void PersistTunnel(
        Session session, TunnelStartSpec spec, int? requestedLocalPort)
    {
        if (!TunnelStateEntry.TryCreate(
                session.Url,
                spec.Server.ServerId,
                spec.Service?.Id,
                spec.Endpoint?.ServerApplicationId,
                spec.Mode.ToString(),
                requestedLocalPort,
                out var entry,
                out _))
        {
            return;
        }

        try
        {
            _state.Add(entry!);
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
        }
    }

    private void RemovePersisted(string url, TunnelStartSpec spec)
    {
        if (IsSpecActive(url, spec))
            return;

        try
        {
            _state.Remove(TunnelStateEntry.KeyFor(
                url,
                spec.Server.ServerId,
                spec.Service?.Id,
                spec.Endpoint?.ServerApplicationId));
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
        }
    }

    private bool IsSpecActive(string url, TunnelStartSpec spec)
    {
        lock (_gate)
        {
            return _active.Values.Any(x =>
                string.Equals(
                    x.Session.Url, url, StringComparison.OrdinalIgnoreCase) &&
                x.Spec.Server.ServerId == spec.Server.ServerId &&
                (spec.Service is { } service
                    ? x.Spec.Service?.Id == service.Id
                    : spec.Endpoint is { } endpoint &&
                      x.Spec.Endpoint?.ServerApplicationId ==
                      endpoint.ServerApplicationId));
        }
    }

    private void RemovePersistedForUrl(string url)
    {
        var normalized = TunnelStateEntry.NormalizeUrl(url);
        try
        {
            _state.RemoveWhere(x => string.Equals(
                x.InstanceUrl, normalized, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (IsStateStoreFailure(exception))
        {
            WarnStateStore();
        }
    }

    private void WarnStateStore()
    {
        lock (_gate)
        {
            if (_stateWriteWarned)
                return;
            _stateWriteWarned = true;
        }

        SendWarning(
            "Salvataggio dei tunnel non riuscito: il ripristino automatico " +
            "all'avvio non sarà disponibile.");
    }

    private static bool IsStateStoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException;

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
        public bool AuthFailed { get; set; }
        public IReadOnlyList<TunnelServer> Targets { get; } = targets;
        public string EventUrl => Url.TrimEnd('/') + "/api/tunnels/events";
        public string SessionUrl => Url.TrimEnd('/') + "/api/tunnels/sessions";
    }

    private sealed record RefreshedTokens(DeviceTokens Tokens, string Issuer);

    private sealed record RetryEntry(
        string Url,
        CancellationTokenSource Cancellation);

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
        TunnelStartSpec spec,
        string correlationId,
        ISshProcess process,
        int reconnectAttempt)
    {
        private readonly Lock _errorGate = new();
        private readonly StringBuilder _error = new();

        public string Id { get; } = id;
        public Session Session { get; } = session;
        public TunnelStartSpec Spec { get; } = spec;
        public string CorrelationId { get; } = correlationId;
        public ISshProcess Process { get; } = process;
        public int ReconnectAttempt { get; } = reconnectAttempt;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public bool StopRequested { get; set; }
        public bool PreserveState { get; set; }
        public CancellationTokenSource Heartbeat { get; } = new();
        public Task HeartbeatTask { get; set; } = Task.CompletedTask;

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

        public async Task StopHeartbeatAsync()
        {
            if (!Heartbeat.IsCancellationRequested)
                Heartbeat.Cancel();

            try
            {
                await HeartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // The loop already handles cancellation; a late observer is fine.
            }
        }
    }
}
