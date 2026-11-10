using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelSessionTests
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
    public async Task Start_posts_the_active_session_projection()
    {
        var handler = DefaultHandler();
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, process);
        var port = PortAllocator.FindFree();

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, 2, port);

        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal("/api/tunnels/sessions", call.Url.AbsolutePath);
        Assert.Equal("Bearer", call.Token!.Scheme);
        Assert.Equal("token", call.Token.Parameter);

        var body = Parse(call);
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal(TunnelId(messages), body.GetProperty("sessionKey").GetString());
        Assert.Equal(1, body.GetProperty("serverId").GetInt32());
        Assert.Equal(2, body.GetProperty("serverApplicationId").GetInt32());
        Assert.Equal("127.0.0.1", body.GetProperty("targetHost").GetString());
        Assert.Equal(8080, body.GetProperty("targetPort").GetInt32());
        Assert.Equal(port, body.GetProperty("localPort").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errorCode").ValueKind);
        Assert.True(process.Started);
        Assert.False(process.Killed);

        manager.StopAll();
        await WaitForAsync(() => StatusCalls(handler, "stopped") == 1);
    }

    [Fact]
    public async Task Heartbeat_repeats_active_on_the_injected_delay()
    {
        var handler = DefaultHandler();
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var intervals = new ConcurrentQueue<TimeSpan>();
        var ticks = 0;
        var manager = CreateManager(
            handler,
            messages,
            process,
            delay: (interval, cancellationToken) =>
            {
                intervals.Enqueue(interval);
                return Interlocked.Increment(ref ticks) <= 2
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var port = PortAllocator.FindFree();

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, 2, port);

        await WaitForAsync(() => StatusCalls(handler, "active") >= 3);

        Assert.True(intervals.Count >= 2);
        Assert.All(intervals, interval =>
            Assert.Equal(TimeSpan.FromSeconds(30), interval));

        manager.StopAll();
        await WaitForAsync(() => StatusCalls(handler, "stopped") == 1);
        Assert.Equal(
            "stopped",
            SessionBodies(handler)[^1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stop_posts_stopped_once_even_when_called_twice()
    {
        var handler = DefaultHandler();
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, process);
        var port = PortAllocator.FindFree();

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, 2, port);
        var id = TunnelId(messages);

        await manager.StopAsync(id);
        await manager.StopAsync(id);

        await WaitForAsync(() => StatusCalls(handler, "stopped") == 1);
        await WaitForAsync(() => HasEvent(handler, "stop"));
        await Task.Delay(100);

        Assert.Equal(1, StatusCalls(handler, "stopped"));
        Assert.Equal(0, StatusCalls(handler, "error"));
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Unexpected_exit_posts_error_with_a_short_redacted_detail()
    {
        var handler = DefaultHandler();
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, process);
        var port = PortAllocator.FindFree();

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, 2, port);

        process.Emit(
            "token=s3cr3t " + new string('x', 300));
        process.Emit("second line");
        process.Exit(255);

        await WaitForAsync(() => StatusCalls(handler, "error") == 1);
        await WaitForAsync(() => HasEvent(handler, "error"));
        var body = SessionBodies(handler)
            .Single(x => x.GetProperty("status").GetString() == "error");
        Assert.Equal("ssh_exit", body.GetProperty("errorCode").GetString());
        var detail = body.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("s3cr3t", detail);
        Assert.Contains("***", detail);
        Assert.True(detail.Length <= 200);
        Assert.DoesNotContain("second line", detail);

        var audit = handler.Calls.Single(x =>
            x.Url.AbsolutePath == "/api/tunnels/events" &&
            Parse(x).GetProperty("kind").GetString() == "error");
        var auditDetail = Parse(audit).GetProperty("detail").GetString()!;
        Assert.DoesNotContain("s3cr3t", auditDetail);
        Assert.True(auditDetail.Length <= 200);
        Assert.Equal(0, StatusCalls(handler, "stopped"));
    }

    [Fact]
    public async Task Unauthorized_session_refreshes_once_and_retries()
    {
        var store = new MemoryTokenStore();
        await store.SaveAsync(InstanceUrl, "stored-refresh");
        var tokenResponses = new Queue<string>(
            [
                TokenJson("stale-access", "refresh-1"),
                TokenJson("fresh-access", "refresh-2")
            ]);
        var sessionTokens = new ConcurrentQueue<string>();
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tunnels/sessions")
            {
                var token = request.Headers.Authorization?.Parameter ?? "";
                sessionTokens.Enqueue(token);
                return token == "stale-access"
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : NoContent();
            }

            return path switch
            {
                "/.well-known/oauth-protected-resource" => Json(
                    HttpStatusCode.OK,
                    $$"""{"authorization_servers":["{{Issuer}}"]}"""),
                "/realms/infraorgit/protocol/openid-connect/token" =>
                    Json(HttpStatusCode.OK, tokenResponses.Dequeue()),
                "/api/tunnels/targets" => Json(HttpStatusCode.OK, TargetsJson),
                "/api/tunnels/events" => NoContent(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = new TunnelManager(
            new HttpClient(handler),
            messages.Enqueue,
            new DeviceAuthClient(new HttpClient(handler)),
            store)
        {
            SshProcessFactory = _ => process
        };
        var port = PortAllocator.FindFree();

        await manager.RestoreSessionAsync(InstanceUrl, "Prod");
        await manager.StartAsync(InstanceUrl, 1, 2, port);

        Assert.Equal(
            ["stale-access", "fresh-access"],
            sessionTokens.ToArray());
        Assert.Equal("refresh-2", await store.LoadAsync(InstanceUrl));
        Assert.DoesNotContain(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.auth" &&
            x.GetProperty("status").GetString() == "error");

        manager.StopAll();
        await WaitForAsync(() => StatusCalls(handler, "stopped") == 1);
    }

    [Fact]
    public async Task Failed_session_call_warns_and_keeps_the_tunnel()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tunnels/sessions")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return path == "/api/tunnels/targets"
                ? Json(HttpStatusCode.OK, TargetsJson)
                : NoContent();
        });
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, process);
        var port = PortAllocator.FindFree();

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, 2, port);

        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.log" &&
            x.GetProperty("message").GetString()!.Contains(
                "Stato tunnel non aggiornato (500)"));
        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.state" &&
            x.GetProperty("items").GetArrayLength() == 1);
        Assert.False(process.Killed);
        Assert.Single(SessionCalls(handler));

        manager.StopAll();
        await WaitForAsync(() => StatusCalls(handler, "stopped") == 1);
    }

    private static RecordingHandler DefaultHandler() =>
        new((request, _) =>
            request.RequestUri!.AbsolutePath == "/api/tunnels/targets"
                ? Json(HttpStatusCode.OK, TargetsJson)
                : NoContent());

    private static TunnelManager CreateManager(
        RecordingHandler handler,
        ConcurrentQueue<object> messages,
        FakeSshProcess process,
        TimeSpan? heartbeatInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            new HttpClient(handler),
            messages.Enqueue,
            heartbeatInterval: heartbeatInterval,
            delay: delay)
        {
            SshProcessFactory = _ => process
        };

    private static string TunnelId(ConcurrentQueue<object> messages) =>
        Messages(messages)
            .Last(x => x.GetProperty("type").GetString() == "tunnels.state")
            .GetProperty("items")[0]
            .GetProperty("id")
            .GetString()!;

    private static IReadOnlyList<RecordingHandler.Call> SessionCalls(
        RecordingHandler handler) =>
        handler.Calls
            .Where(x => x.Url.AbsolutePath == "/api/tunnels/sessions")
            .ToArray();

    private static int StatusCalls(RecordingHandler handler, string status) =>
        SessionBodies(handler).Count(x =>
            x.GetProperty("status").GetString() == status);

    private static bool HasEvent(RecordingHandler handler, string kind) =>
        handler.Calls.Any(x =>
            x.Url.AbsolutePath == "/api/tunnels/events" &&
            Parse(x).GetProperty("kind").GetString() == kind);

    private static List<JsonElement> SessionBodies(RecordingHandler handler) =>
        SessionCalls(handler).Select(Parse).ToList();

    private static JsonElement Parse(RecordingHandler.Call call)
    {
        using var document = JsonDocument.Parse(call.Body);
        return document.RootElement.Clone();
    }

    private static List<JsonElement> Messages(ConcurrentQueue<object> queue) =>
        queue.Select(x => JsonSerializer.SerializeToElement(
            x, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condizione non raggiunta in tempo.");
            await Task.Delay(10);
        }
    }

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

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<Call> _calls = [];

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_gate)
                    return _calls.ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var call = new Call(
                request.RequestUri!,
                request.Headers.Authorization,
                body);
            lock (_gate)
                _calls.Add(call);
            return respond(request, body);
        }

        public sealed record Call(
            Uri Url,
            System.Net.Http.Headers.AuthenticationHeaderValue? Token,
            string Body);
    }

    private sealed class FakeSshProcess : ISshProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string>? ErrorLine;

        public bool Started { get; private set; }
        public bool Killed { get; private set; }
        public int ExitCode { get; private set; }

        public void Start() => Started = true;

        public void Kill()
        {
            Killed = true;
            Exit(0);
        }

        public Task WaitForExitAsync() => _exit.Task;

        public void Emit(string line) => ErrorLine?.Invoke(line);

        public void Exit(int code)
        {
            if (_exit.TrySetResult(code))
                ExitCode = code;
        }

        public void Dispose()
        {
        }
    }
}
