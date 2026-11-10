using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelManagerTests
{
    private const string InstanceUrl = "https://infra.example.com";
    private const string Issuer = "https://auth.example.com/realms/infraorgit";
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
        var store = new MemoryTokenStore();
        var manager = new TunnelManager(
            new HttpClient(apiHandler), messages.Enqueue, auth, store);

        await manager.LoginWithDeviceCodeAsync(
            "https://infra.example.com", "Prod");

        Assert.Equal("refresh-token", manager.GetRefreshToken("https://infra.example.com"));
        Assert.Equal("refresh-token",
            await store.LoadAsync("https://infra.example.com"));
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

    [Fact]
    public async Task Restore_refreshes_the_saved_token_then_loads_targets()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.OK,
                        TokenJson("restored-access", "rotated-refresh")),
                "/api/tunnels/targets" => Json(HttpStatusCode.OK, TargetsJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Equal(
            [
                "/.well-known/oauth-protected-resource",
                "/realms/infraorgit/protocol/openid-connect/token",
                "/api/tunnels/targets"
            ],
            handler.Requests.Select(x => x.RequestUri!.AbsolutePath));

        var targets = handler.Requests.Single(
            x => x.RequestUri!.AbsolutePath == "/api/tunnels/targets");
        Assert.Equal("restored-access", targets.Headers.Authorization!.Parameter);
        Assert.Equal("rotated-refresh", await store.LoadAsync(InstanceUrl));

        var payload = Messages(messages);
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "restored");
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.targets");
    }

    [Fact]
    public async Task Restore_without_a_saved_token_sends_none()
    {
        var handler = new FakeHandler(
            _ => throw new InvalidOperationException("no request expected"));
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, new MemoryTokenStore());

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Empty(handler.Requests);
        var payload = Messages(messages);
        var auth = Assert.Single(payload);
        Assert.Equal("tunnels.auth", auth.GetProperty("type").GetString());
        Assert.Equal("none", auth.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejected_refresh_clears_the_saved_token()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.BadRequest,
                        """{"error":"invalid_grant"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Null(await store.LoadAsync(InstanceUrl));
        Assert.Null(manager.GetRefreshToken(InstanceUrl));
        Assert.DoesNotContain(handler.Requests, x =>
            x.RequestUri!.AbsolutePath == "/api/tunnels/targets");

        var payload = Messages(messages);
        var error = payload.Single(x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "error");
        Assert.Contains("Accedi di nuovo", error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("requiresLogin").GetBoolean());
    }

    [Fact]
    public async Task Restore_refreshes_again_when_the_token_is_almost_expired()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var tokenResponses = new Queue<string>(
            [
                TokenJson("first-access", "refresh-1", expiresIn: 30),
                TokenJson("second-access", "refresh-2")
            ]);
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.OK, tokenResponses.Dequeue()),
                "/api/tunnels/targets" => Json(HttpStatusCode.OK, TargetsJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Equal(2, handler.Requests.Count(x =>
            x.RequestUri!.AbsolutePath ==
            "/realms/infraorgit/protocol/openid-connect/token"));
        var targets = handler.Requests.Single(
            x => x.RequestUri!.AbsolutePath == "/api/tunnels/targets");
        Assert.Equal("second-access", targets.Headers.Authorization!.Parameter);
        Assert.Equal("refresh-2", await store.LoadAsync(InstanceUrl));

        var payload = Messages(messages);
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.targets");
    }

    [Fact]
    public async Task Unauthorized_targets_refreshes_once_and_retries()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var tokenResponses = new Queue<string>(
            [
                TokenJson("stale-access", "refresh-1"),
                TokenJson("fresh-access", "refresh-2")
            ]);
        var targetsRequests = 0;
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tunnels/targets")
            {
                targetsRequests++;
                return request.Headers.Authorization!.Parameter == "stale-access"
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json(HttpStatusCode.OK, TargetsJson);
            }

            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.OK, tokenResponses.Dequeue()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Equal(2, targetsRequests);
        var targets = handler.Requests
            .Where(x => x.RequestUri!.AbsolutePath == "/api/tunnels/targets")
            .Select(x => x.Headers.Authorization!.Parameter)
            .ToArray();
        Assert.Equal(2, targets.Length);
        Assert.Equal("stale-access", targets[0]);
        Assert.Equal("fresh-access", targets[1]);
        Assert.Equal("refresh-2", await store.LoadAsync(InstanceUrl));

        var payload = Messages(messages);
        Assert.Contains(payload, x =>
            x.GetProperty("type").GetString() == "tunnels.targets");
    }

    [Fact]
    public async Task Unauthorized_targets_clears_the_token_when_refresh_fails()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var tokenResponses = new Queue<HttpResponseMessage>(
            [
                Json(HttpStatusCode.OK, TokenJson("stale-access", "refresh-1")),
                Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
            ]);
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tunnels/targets")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    tokenResponses.Dequeue(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");

        Assert.Null(await store.LoadAsync(InstanceUrl));
        Assert.Null(manager.GetRefreshToken(InstanceUrl));
        Assert.Single(handler.Requests, x =>
            x.RequestUri!.AbsolutePath == "/api/tunnels/targets");

        var payload = Messages(messages);
        var error = payload.Single(x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "error");
        Assert.Contains("Accedi di nuovo", error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("requiresLogin").GetBoolean());
    }

    [Fact]
    public async Task Logout_clears_the_session_and_the_saved_token()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/.well-known/oauth-protected-resource" => Discovery(),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.OK, TokenJson("access-token", "refresh")),
                "/api/tunnels/targets" => Json(HttpStatusCode.OK, TargetsJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, store);

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");
        await manager.LogoutAsync(InstanceUrl);

        Assert.Null(await store.LoadAsync(InstanceUrl));
        Assert.Null(manager.GetRefreshToken(InstanceUrl));
        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "logout");

        await manager.StartAsync(InstanceUrl, 1, 2, null);
        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.error" &&
            x.GetProperty("message").GetString()!.Contains("Carica prima"));
    }

    [Fact]
    public async Task List_without_a_token_tries_the_saved_session()
    {
        var handler = new FakeHandler(
            _ => throw new InvalidOperationException("no request expected"));
        var messages = new ConcurrentQueue<object>();
        var none = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new TunnelManager(
            new HttpClient(handler),
            payload =>
            {
                messages.Enqueue(payload);
                var element = JsonSerializer.SerializeToElement(
                    payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (element.TryGetProperty("status", out var status) &&
                    status.GetString() == "none")
                {
                    none.TrySetResult();
                }
            },
            new DeviceAuthClient(new HttpClient(handler)),
            new MemoryTokenStore());

        manager.Handle(JsonSerializer.Serialize(new
        {
            type = "tunnels.list",
            url = InstanceUrl,
            name = "Prod"
        }));

        await none.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(handler.Requests);
        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "none");
    }

    private static TunnelManager CreateManager(
        FakeHandler handler,
        ConcurrentQueue<object> messages,
        ITokenStore store) =>
        new(
            new HttpClient(handler),
            messages.Enqueue,
            new DeviceAuthClient(new HttpClient(handler)),
            store);

    private static HttpResponseMessage Discovery() =>
        Json(HttpStatusCode.OK,
            $$"""{"authorization_servers":["{{Issuer}}"]}""");

    private static string TokenJson(
        string accessToken, string refreshToken, int expiresIn = 300) =>
        $$"""
        {
          "access_token": "{{accessToken}}",
          "refresh_token": "{{refreshToken}}",
          "token_type": "Bearer",
          "expires_in": {{expiresIn}}
        }
        """;

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
