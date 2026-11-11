using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelServiceTests
{
    private const string InstanceUrl = "https://infra.example.com";

    [Fact]
    public async Task Start_by_service_id_uses_the_local_mode_and_reports_ports()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(
            _ => Json(TargetsJson(port, socksPort)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);

        var start = Assert.Single(starts);
        Assert.Equal(
            [
                "-N",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "BatchMode=yes",
                "-p", "22",
                "-L", $"127.0.0.1:{port}:127.0.0.1:8080",
                "deploy@10.0.0.1"
            ],
            start.ArgumentList);

        var body = Assert.Single(SessionBodies(handler));
        Assert.Equal("Local", body.GetProperty("mode").GetString());
        Assert.Equal(10, body.GetProperty("serviceId").GetInt32());
        Assert.Equal(10, body.GetProperty("tunnelServiceId").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            body.GetProperty("serverApplicationId").ValueKind);
        Assert.Equal("127.0.0.1",
            body.GetProperty("targetHost").GetString());
        Assert.Equal(8080, body.GetProperty("targetPort").GetInt32());
        Assert.Equal(port, body.GetProperty("localPort").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            body.GetProperty("remotePort").ValueKind);

        var audit = EventBodies(handler).Single(x =>
            x.GetProperty("kind").GetString() == "start");
        Assert.Equal("Local", audit.GetProperty("mode").GetString());
        Assert.Equal(10, audit.GetProperty("serviceId").GetInt32());
        Assert.Equal(10, audit.GetProperty("tunnelServiceId").GetInt32());
        Assert.Equal(port, audit.GetProperty("localPort").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            audit.GetProperty("remotePort").ValueKind);

        var openUrl = $"http://127.0.0.1:{port}/login";
        var item = Assert.Single(StateItems(messages));
        Assert.Equal(openUrl, item.GetProperty("openUrl").GetString());
        Assert.True(manager.IsTunnelOpenUrl(openUrl));

        manager.StopAll();
        await WaitForAsync(() => !manager.IsTunnelOpenUrl(openUrl));
    }

    [Fact]
    public async Task Start_by_service_id_uses_the_dynamic_mode_without_a_target()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(
            _ => Json(TargetsJson(port, socksPort)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 11, null);

        var start = Assert.Single(starts);
        Assert.Equal(
            [
                "-N",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "BatchMode=yes",
                "-p", "22",
                "-D", $"127.0.0.1:{socksPort}",
                "deploy@10.0.0.1"
            ],
            start.ArgumentList);

        var body = Assert.Single(SessionBodies(handler));
        Assert.Equal("Dynamic", body.GetProperty("mode").GetString());
        Assert.Equal(11, body.GetProperty("tunnelServiceId").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("targetPort").ValueKind);
        Assert.Equal(socksPort, body.GetProperty("localPort").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("remotePort").ValueKind);

        var item = Assert.Single(StateItems(messages));
        Assert.Equal("Dynamic", item.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("openUrl").ValueKind);
    }

    [Fact]
    public async Task Start_by_service_id_uses_the_remote_mode_without_a_local_port()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(
            _ => Json(TargetsJson(port, socksPort)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 12, null);

        var start = Assert.Single(starts);
        Assert.Equal(
            [
                "-N",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "BatchMode=yes",
                "-p", "22",
                "-R", "127.0.0.1:15432:10.0.0.9:5432",
                "deploy@10.0.0.1"
            ],
            start.ArgumentList);

        var body = Assert.Single(SessionBodies(handler));
        Assert.Equal("Remote", body.GetProperty("mode").GetString());
        Assert.Equal(12, body.GetProperty("tunnelServiceId").GetInt32());
        Assert.Equal("10.0.0.9", body.GetProperty("targetHost").GetString());
        Assert.Equal(5432, body.GetProperty("targetPort").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("localPort").ValueKind);
        Assert.Equal(15432, body.GetProperty("remotePort").GetInt32());
    }

    [Fact]
    public async Task Invalid_remote_service_is_rejected_before_ssh_starts()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(
            _ => Json(TargetsJson(port, socksPort, remotePort: null)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 12, null);

        Assert.Empty(starts);
        var error = Messages(messages).Last(x =>
            x.GetProperty("type").GetString() == "tunnels.error");
        Assert.Contains("porta remota non valida",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Auto_start_starts_only_marked_services_once_per_load()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(_ => Json(TargetsJson(
            port, socksPort, localAutoStart: true, socksAutoStart: true)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        Assert.Equal(2, starts.Count);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        Assert.Equal(2, starts.Count);

        var activeServices = SessionBodies(handler)
            .Where(x => x.GetProperty("status").GetString() == "active")
            .Select(x => x.GetProperty("tunnelServiceId").GetInt32())
            .Order()
            .ToArray();
        Assert.Equal([10, 11], activeServices);
        Assert.Equal(2, StateItems(messages).Count);
    }

    [Fact]
    public async Task Auto_start_can_be_suppressed_for_the_session()
    {
        var port = PortAllocator.FindFree();
        var socksPort = PortAllocator.FindFree();
        var handler = new RecordingHandler(_ => Json(TargetsJson(
            port, socksPort, localAutoStart: true, socksAutoStart: true)));
        var messages = new ConcurrentQueue<object>();
        var starts = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(handler, messages, starts);
        manager.AutoStartEnabled = false;

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");

        Assert.Empty(starts);
    }

    [Fact]
    public void Browser_url_exists_only_for_local_http_services()
    {
        var port = PortAllocator.FindFree();

        var http = Spec("Local", "Http", "admin", port);
        Assert.Equal($"http://127.0.0.1:{port}/admin", http.OpenUrl);

        var https = Spec("Local", "Https", "/login", port);
        Assert.Equal($"https://127.0.0.1:{port}/login", https.OpenUrl);

        Assert.Null(Spec("Local", "Tcp", null, port).OpenUrl);
        Assert.Null(Spec("Dynamic", "Tcp", null, port).OpenUrl);
        Assert.Null(Spec("Remote", "Tcp", null, null, 15432).OpenUrl);
    }

    private static TunnelStartSpec Spec(
        string mode,
        string kind,
        string? openPath,
        int? localPort,
        int? remotePort = null)
    {
        var server = new TunnelServer(
            1, "srv", "Provider / Env", "10.0.0.1", 22, "deploy",
            null, [], [], []);
        var service = new TunnelService(
            10, 1, "svc", mode, "10.0.0.9", 5432,
            localPort, remotePort, kind, openPath, false, true);
        Assert.True(
            TunnelStartSpec.TryCreate(
                server, null, service, null, out var spec, out var error),
            error);
        return spec!;
    }

    private static string TargetsJson(
        int localPort,
        int socksPort,
        bool localAutoStart = false,
        bool socksAutoStart = false,
        int? remotePort = 15432) =>
        $$"""
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
              "jumps": [],
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
              ],
              "services": [
                {
                  "id": 10,
                  "serverId": 1,
                  "name": "Web",
                  "mode": "Local",
                  "targetHost": "127.0.0.1",
                  "targetPort": 8080,
                  "localPort": {{localPort}},
                  "remotePort": null,
                  "kind": "Http",
                  "openPath": "/login",
                  "autoStart": {{(localAutoStart ? "true" : "false")}},
                  "enabled": true
                },
                {
                  "id": 11,
                  "serverId": 1,
                  "name": "Socks",
                  "mode": "Dynamic",
                  "targetHost": "127.0.0.1",
                  "targetPort": null,
                  "localPort": {{socksPort}},
                  "remotePort": null,
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": {{(socksAutoStart ? "true" : "false")}},
                  "enabled": true
                },
                {
                  "id": 12,
                  "serverId": 1,
                  "name": "Database",
                  "mode": "Remote",
                  "targetHost": "10.0.0.9",
                  "targetPort": 5432,
                  "localPort": null,
                  "remotePort": {{(remotePort?.ToString() ?? "null")}},
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": false,
                  "enabled": true
                },
                {
                  "id": 13,
                  "serverId": 1,
                  "name": "Console",
                  "mode": "Local",
                  "targetHost": "127.0.0.1",
                  "targetPort": 9090,
                  "localPort": null,
                  "remotePort": null,
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": false,
                  "enabled": true
                }
              ]
            }
          ]
        }
        """;

    private static TunnelManager CreateManager(
        RecordingHandler handler,
        ConcurrentQueue<object> messages,
        ConcurrentQueue<ProcessStartInfo> starts) =>
        new(new HttpClient(handler), messages.Enqueue)
        {
            SshProcessFactory = startInfo =>
            {
                starts.Enqueue(startInfo);
                return new FakeSshProcess();
            }
        };

    private static IReadOnlyList<JsonElement> StateItems(
        ConcurrentQueue<object> messages) =>
        Messages(messages)
            .Last(x => x.GetProperty("type").GetString() == "tunnels.state")
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

    private static List<JsonElement> SessionBodies(RecordingHandler handler) =>
        handler.Calls
            .Where(x => x.Url.AbsolutePath == "/api/tunnels/sessions")
            .Select(Parse)
            .ToList();

    private static List<JsonElement> EventBodies(RecordingHandler handler) =>
        handler.Calls
            .Where(x => x.Url.AbsolutePath == "/api/tunnels/events")
            .Select(Parse)
            .ToList();

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

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
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
            return respond(request);
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

        public int ExitCode { get; private set; }

        public void Start()
        {
        }

        public void Kill() => Exit(0);

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
