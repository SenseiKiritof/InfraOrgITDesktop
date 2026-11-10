using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// OAuth 2.0 Device Authorization Grant (RFC 8628) with PKCE for the desktop
/// app. The authorization server is discovered from the instance; tokens are
/// returned to the caller and never written to disk or logs.
/// </summary>
public sealed class DeviceAuthClient
{
    public const string ClientId = "infraorgit-mcp-desktop";
    public const string Scope =
        "openid profile email mcp:read tunnels:operate";

    private const string DiscoveryPath = "/.well-known/oauth-protected-resource";
    private const string DevicePath = "/protocol/openid-connect/auth/device";
    private const string TokenPath = "/protocol/openid-connect/token";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DeviceAuthClient(
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? ((time, cancellationToken) =>
            Task.Delay(time, cancellationToken));
    }

    /// <summary>
    /// Reads <c>authorization_servers[0]</c> from the protected-resource
    /// metadata. The local realm path is used only when the document has no
    /// entry.
    /// </summary>
    public async Task<string> DiscoverIssuerAsync(
        string instanceUrl,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUrl(instanceUrl, out var baseUrl))
            throw new DeviceAuthException("L'URL dell'istanza non è valido.");

        using var response = await _http.GetAsync(
            baseUrl + DiscoveryPath, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeviceAuthException(
                $"La scoperta dell'autenticazione ha risposto {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    "authorization_servers", out var servers) &&
                servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.ValueKind == JsonValueKind.String &&
                        server.GetString() is { Length: > 0 } value)
                    {
                        return NormalizeIssuer(value);
                    }
                }
            }
        }
        catch (JsonException)
        {
            throw new DeviceAuthException("Risposta di scoperta non valida.");
        }

        return baseUrl + "/realms/infraorgit";
    }

    /// <summary>
    /// Requests a device code and user code for the instance. The returned
    /// record keeps the PKCE verifier needed by <see cref="PollAsync"/>.
    /// </summary>
    public async Task<DeviceAuthorization> StartAsync(
        string instanceUrl,
        CancellationToken cancellationToken)
    {
        var issuer = await DiscoverIssuerAsync(instanceUrl, cancellationToken);
        var codeVerifier = CreateCodeVerifier();
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["code_challenge"] = CreateCodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256"
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, issuer + DevicePath)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryRead<DeviceAuthorizationResponse>(body)?.Error;
            throw new DeviceAuthException(
                DescribeDeviceError((int)response.StatusCode, error));
        }

        var payload = TryRead<DeviceAuthorizationResponse>(body) ??
            throw new DeviceAuthException("Risposta di avvio accesso non valida.");
        if (string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            throw new DeviceAuthException("Risposta di avvio accesso incompleta.");
        }

        var interval = Math.Max(1, payload.Interval);
        var expiresIn = payload.ExpiresIn > 0 ? payload.ExpiresIn : 600;
        return new DeviceAuthorization(
            issuer,
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUri,
            payload.VerificationUriComplete,
            interval,
            expiresIn,
            codeVerifier)
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        };
    }

    /// <summary>
    /// Polls the token endpoint until the user confirms, the code expires or
    /// the caller cancels. A <c>slow_down</c> response adds five seconds to
    /// the interval, as RFC 8628 requires.
    /// </summary>
    public async Task<DeviceTokens> PollAsync(
        DeviceAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(authorization.IntervalSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= authorization.ExpiresAt)
            {
                throw new DeviceAuthException(
                    "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.");
            }

            await _delay(interval, cancellationToken);

            var payload = await RequestTokenAsync(
                authorization.Issuer,
                new Dictionary<string, string>
                {
                    ["grant_type"] =
                        "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = authorization.DeviceCode,
                    ["client_id"] = ClientId,
                    ["code_verifier"] = authorization.CodeVerifier
                },
                cancellationToken);

            if (payload.AccessToken is { Length: > 0 } accessToken)
                return ToTokens(accessToken, payload, null);

            switch (payload.Error)
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "expired_token":
                    throw new DeviceAuthException(
                        "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.");

                case "access_denied":
                    throw new DeviceAuthException(
                        "Accesso negato. Riprova e conferma nel browser.");

                default:
                    throw new DeviceAuthException(
                        DescribeTokenError(payload.Error));
            }
        }
    }

    /// <summary>
    /// Exchanges a refresh token for new tokens. The refresh token stays in
    /// the caller's memory only.
    /// </summary>
    public async Task<DeviceTokens> RefreshAsync(
        string issuer,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new DeviceAuthException(
                "Sessione non valida. Accedi di nuovo con Keycloak.");
        }

        var payload = await RequestTokenAsync(
            NormalizeIssuer(issuer),
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken
            },
            cancellationToken);

        if (payload.AccessToken is not { Length: > 0 } accessToken)
            throw new DeviceAuthException(DescribeTokenError(payload.Error));

        return ToTokens(accessToken, payload, refreshToken);
    }

    private async Task<TokenResponse> RequestTokenAsync(
        string issuer,
        Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, issuer + TokenPath)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = TryRead<TokenResponse>(body);

        if (payload is null)
        {
            throw new DeviceAuthException(
                $"Il server di autenticazione ha risposto {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode &&
            string.IsNullOrEmpty(payload.Error) &&
            string.IsNullOrEmpty(payload.AccessToken))
        {
            throw new DeviceAuthException(
                DescribeHttpStatus((int)response.StatusCode));
        }

        return payload;
    }

    private static DeviceTokens ToTokens(
        string accessToken,
        TokenResponse payload,
        string? fallbackRefreshToken)
    {
        var expiresIn = payload.ExpiresIn > 0 ? payload.ExpiresIn : 60;
        var refreshToken = payload.RefreshToken is { Length: > 0 } value
            ? value
            : fallbackRefreshToken;
        return new DeviceTokens(accessToken, refreshToken, expiresIn)
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        };
    }

    private static string CreateCodeVerifier() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url.EncodeToString(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string NormalizeIssuer(string issuer)
    {
        if (!TryNormalizeUrl(issuer, out var normalized))
            throw new DeviceAuthException(
                "L'authorization server scoperto non è valido.");
        return normalized;
    }

    private static bool TryNormalizeUrl(string url, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    private static T? TryRead<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string DescribeDeviceError(int status, string? error) =>
        error switch
        {
            "invalid_client" =>
                "Il client desktop non è configurato su Keycloak.",
            "unauthorized_client" =>
                "Keycloak non consente l'accesso da questo client.",
            "invalid_scope" =>
                "Keycloak non autorizza gli scope richiesti dal desktop.",
            _ => DescribeHttpStatus(status)
        };

    private static string DescribeTokenError(string? error) =>
        error switch
        {
            "expired_token" =>
                "Il codice di accesso è scaduto. Avvia di nuovo l'accesso.",
            "access_denied" =>
                "Accesso negato. Riprova e conferma nel browser.",
            "invalid_grant" =>
                "La richiesta di accesso non è più valida. Avvia di nuovo l'accesso.",
            "invalid_client" =>
                "Il client desktop non è configurato su Keycloak.",
            _ => "Accesso non riuscito. Riprova."
        };

    private static string DescribeHttpStatus(int status) => status switch
    {
        429 => "Troppe richieste al server di autenticazione. Attendi e riprova.",
        _ => $"Il server di autenticazione ha risposto {status}."
    };

    private sealed record DeviceAuthorizationResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("verification_uri_complete")]
        string? VerificationUriComplete,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);
}

/// <summary>
/// Device authorization request returned to the caller. The JSON DTOs are
/// camelCase; this record is never serialized to the UI as a whole.
/// </summary>
public sealed record DeviceAuthorization(
    string Issuer,
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int IntervalSeconds,
    int ExpiresInSeconds,
    string CodeVerifier)
{
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Tokens from the device or refresh grant. They live in memory only.
/// </summary>
public sealed record DeviceTokens(
    string AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds)
{
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class DeviceAuthException(string message) : Exception(message);
