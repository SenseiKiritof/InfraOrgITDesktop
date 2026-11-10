using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelManagerTests
{
    private const string TargetsJson = """
        {
          "items": [
            {
              "serverId": 1,
              "serverName": "srv",
              "environmentLabel": "Provider / Env",
              "sshHost": "10.0.0.1",
              "sshPort": 22,
              "username": "deploy",
              "jump": null,
              "targets": [
                {
                  "serverApplicationId": 2,
                  "applicationId": 3,
                  "application": "app",
                  "version": "1.0.0",
                  "targetHost": "127.0.0.1",
                  "targetPort": 8080,
                  "suggestedLocalPort": 8080
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Load_targets_stores_the_allowlist_and_notifies_the_ui()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, TargetsJson));
        var messages = new ConcurrentQueue<object>();
        var manager = new TunnelManager(new HttpClient(handler), messages.Enqueue);

        await manager.LoadTargetsAsync("https://infra.example.com", "Prod", "itk_test");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://infra.example.com/api/tunnels/targets", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("itk_test", request.Headers.Authorization.Parameter);

        var payload = Messages(messages);
        var targets = payload.Single(x => x.GetProperty("type").GetString() == "tunnels.targets");
        var server = targets.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("srv", server.GetProperty("serverName").GetString());
        Assert.Equal(8080,
            server.GetProperty("targets").EnumerateArray().Single()
                .GetProperty("targetPort").GetInt32());
    }

    [Fact]
    public async Task Load_targets_reports_the_scope_error()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var messages = new ConcurrentQueue<object>();
        var manager = new TunnelManager(new HttpClient(handler), messages.Enqueue);

        await manager.LoadTargetsAsync("https://infra.example.com", "Prod", "itk_test");

        var payload = Messages(messages);
        var error = payload.Single(x => x.GetProperty("type").GetString() == "tunnels.error");
        Assert.Contains("tunnels:operate", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Start_without_loaded_targets_reports_an_error()
    {
        var messages = new ConcurrentQueue<object>();
        var manager = new TunnelManager(
            new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"))),
            messages.Enqueue);

        await manager.StartAsync("https://infra.example.com", 1, 2, null);

        var payload = Messages(messages);
        var error = payload.Single(x => x.GetProperty("type").GetString() == "tunnels.error");
        Assert.Contains("Carica prima", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Device_login_loads_targets_and_keeps_the_refresh_token_in_memory()
    {
        var authHandler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" => Json(
                    HttpStatusCode.OK,
                    """{"authorization_servers":["https://auth.example.com/realms/infraorgit"]}"""),
                "/realms/infraorgit/protocol/openid-connect/auth/device" => Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "device_code": "device-code",
                      "user_code": "ABCD-EFGH",
                      "verification_uri": "https://auth.example.com/device",
                      "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
                      "expires_in": 600,
                      "interval": 5
                    }
                    """),
                "/realms/infraorgit/protocol/openid-connect/token" => Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "access_token": "access-token",
                      "refresh_token": "refresh-token",
                      "token_type": "Bearer",
                      "expires_in": 300
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var auth = new DeviceAuthClient(
            new HttpClient(authHandler), (_, _) => Task.CompletedTask);
        var apiHandler = new FakeHandler(_ => Json(HttpStatusCode.OK, TargetsJson));
        var messages = new ConcurrentQueue<object>();
        var manager = new TunnelManager(
            new HttpClient(apiHandler), messages.Enqueue, auth);

        await manager.LoginWithDeviceCodeAsync(
            "https://infra.example.com", "Prod");

        Assert.Equal("refresh-token", manager.GetRefreshToken("https://infra.example.com"));
        var request = Assert.Single(apiHandler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("access-token", request.Headers.Authorization.Parameter);

        var payload = Messages(messages);
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "pending" &&
            x.GetProperty("userCode").GetString() == "ABCD-EFGH");
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "success");
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.targets");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static List<JsonElement> Messages(ConcurrentQueue<object> queue) =>
        queue.Select(x => JsonSerializer.SerializeToElement(
            x, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToList();

    private sealed class FakeHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
