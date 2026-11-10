using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using InfraOrgIt.Desktop.Auth;

namespace InfraOrgIt.Desktop.Tests;

public sealed class DeviceAuthClientTests
{
    private const string InstanceUrl = "https://infra.example.com";
    private const string Issuer = "https://auth.example.com/realms/infraorgit";

    private const string DeviceJson = """
        {
          "device_code": "device-code",
          "user_code": "ABCD-EFGH",
          "verification_uri": "https://auth.example.com/device",
          "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
          "expires_in": 600,
          "interval": 5
        }
        """;

    private const string TokenJson = """
        {
          "access_token": "access-token",
          "refresh_token": "refresh-token",
          "token_type": "Bearer",
          "expires_in": 300
        }
        """;

    [Fact]
    public async Task Start_uses_discovery_and_sends_pkce_with_scopes()
    {
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" =>
                    Json(HttpStatusCode.OK,
                        $$"""{"authorization_servers":["{{Issuer}}"]}"""),
                "/realms/infraorgit/protocol/openid-connect/auth/device" =>
                    Json(HttpStatusCode.OK, DeviceJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new DeviceAuthClient(new HttpClient(handler));

        var authorization = await client.StartAsync(
            InstanceUrl, CancellationToken.None);

        Assert.Equal(Issuer, authorization.Issuer);
        Assert.Equal("device-code", authorization.DeviceCode);
        Assert.Equal("ABCD-EFGH", authorization.UserCode);
        Assert.Equal("https://auth.example.com/device", authorization.VerificationUri);
        Assert.Equal("https://auth.example.com/device?user_code=ABCD-EFGH",
            authorization.VerificationUriComplete);
        Assert.Equal(5, authorization.IntervalSeconds);
        Assert.Equal(600, authorization.ExpiresInSeconds);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            InstanceUrl + "/.well-known/oauth-protected-resource",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(
            Issuer + "/protocol/openid-connect/auth/device",
            handler.Requests[1].RequestUri!.ToString());

        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal("infraorgit-mcp-desktop", form["client_id"]);
        Assert.Equal(DeviceAuthClient.Scope, form["scope"]);
        Assert.Equal(
            "openid profile email offline_access mcp:read tunnels:operate",
            form["scope"]);
        Assert.Equal("S256", form["code_challenge_method"]);
        var expectedChallenge = Base64Url.EncodeToString(
            SHA256.HashData(Encoding.ASCII.GetBytes(authorization.CodeVerifier)));
        Assert.Equal(expectedChallenge, form["code_challenge"]);
    }

    [Fact]
    public async Task Start_falls_back_to_the_instance_realm_when_discovery_has_no_entry()
    {
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" =>
                    Json(HttpStatusCode.OK, """{"authorization_servers":[]}"""),
                "/realms/infraorgit/protocol/openid-connect/auth/device" =>
                    Json(HttpStatusCode.OK, DeviceJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new DeviceAuthClient(new HttpClient(handler));

        var authorization = await client.StartAsync(
            InstanceUrl, CancellationToken.None);

        Assert.Equal(InstanceUrl + "/realms/infraorgit", authorization.Issuer);
        Assert.Equal(
            InstanceUrl + "/realms/infraorgit/protocol/openid-connect/auth/device",
            handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Poll_waits_for_pending_and_returns_the_tokens()
    {
        var responses = new Queue<(HttpStatusCode Status, string Body)>();
        responses.Enqueue((HttpStatusCode.BadRequest,
            """{"error":"authorization_pending"}"""));
        responses.Enqueue((HttpStatusCode.OK, TokenJson));
        var handler = new FakeHandler(_ =>
        {
            var next = responses.Dequeue();
            return Json(next.Status, next.Body);
        });
        var delays = new List<TimeSpan>();
        var client = new DeviceAuthClient(
            new HttpClient(handler),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var tokens = await client.PollAsync(
            Authorization(), CancellationToken.None);

        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        Assert.Equal(300, tokens.ExpiresInSeconds);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);

        var form = ParseForm(handler.Bodies[0]);
        Assert.Equal(
            "urn:ietf:params:oauth:grant-type:device_code",
            form["grant_type"]);
        Assert.Equal("device-code", form["device_code"]);
        Assert.Equal("infraorgit-mcp-desktop", form["client_id"]);
        Assert.Equal("test-verifier", form["code_verifier"]);
    }

    [Fact]
    public async Task Poll_slow_down_increases_the_delay()
    {
        var responses = new Queue<(HttpStatusCode Status, string Body)>();
        responses.Enqueue((HttpStatusCode.BadRequest,
            """{"error":"slow_down"}"""));
        responses.Enqueue((HttpStatusCode.BadRequest,
            """{"error":"slow_down"}"""));
        responses.Enqueue((HttpStatusCode.OK, TokenJson));
        var handler = new FakeHandler(_ =>
        {
            var next = responses.Dequeue();
            return Json(next.Status, next.Body);
        });
        var delays = new List<TimeSpan>();
        var client = new DeviceAuthClient(
            new HttpClient(handler),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var tokens = await client.PollAsync(
            Authorization(), CancellationToken.None);

        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal(3, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(15), delays[2]);
    }

    [Theory]
    [InlineData("expired_token", "scaduto")]
    [InlineData("access_denied", "negato")]
    public async Task Poll_surfaces_expired_and_denied_errors(
        string error,
        string expected)
    {
        var handler = new FakeHandler(_ => Json(
            HttpStatusCode.BadRequest, $$"""{"error":"{{error}}"}"""));
        var client = new DeviceAuthClient(
            new HttpClient(handler), (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<DeviceAuthException>(
            () => client.PollAsync(Authorization(), CancellationToken.None));

        Assert.Contains(expected, exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Poll_surfaces_http_errors()
    {
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new DeviceAuthClient(
            new HttpClient(handler), (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<DeviceAuthException>(
            () => client.PollAsync(Authorization(), CancellationToken.None));

        Assert.Contains("503", exception.Message);
    }

    [Fact]
    public async Task Refresh_sends_the_refresh_grant_and_returns_tokens()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, TokenJson));
        var client = new DeviceAuthClient(new HttpClient(handler));

        var tokens = await client.RefreshAsync(
            Issuer, "refresh-token", CancellationToken.None);

        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        var form = ParseForm(Assert.Single(handler.Bodies));
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("infraorgit-mcp-desktop", form["client_id"]);
        Assert.Equal("refresh-token", form["refresh_token"]);
    }

    private static DeviceAuthorization Authorization() => new(
        Issuer,
        "device-code",
        "ABCD-EFGH",
        Issuer + "/device",
        Issuer + "/device?user_code=ABCD-EFGH",
        5,
        600,
        "test-verifier")
    {
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string> ParseForm(string body)
    {
        var values = HttpUtility.ParseQueryString(body);
        return values.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => values[key] ?? "");
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
