using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelReconnectTests
{
    private const string InstanceUrl = "https://infra.example.com";

    [Fact]
    public async Task Unexpected_exit_retries_three_times_then_gives_up()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var starts = 0;
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess
                {
                    ExitOnStart = Interlocked.Increment(ref starts) <= 4 ? 255 : null
                };
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(1));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");

        await WaitForAsync(() => ProcessesWith(processes) == 4);
        await WaitForAsync(() => Messages(messages).Any(x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("dopo 3 tentativi")));

        var retries = Messages(messages).Count(x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("interrotto in modo inatteso"));
        Assert.Equal(3, retries);

        await Task.Delay(40);
        Assert.Equal(4, ProcessesWith(processes));
        manager.StopAll();
    }

    [Fact]
    public async Task User_stop_prevents_reconnect()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(handler, messages, _ =>
        {
            var process = new FakeSshProcess();
            processes.Enqueue(process);
            return process;
        });

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        var id = Assert.Single(StateItems(messages)).GetProperty("id").GetString()!;

        await manager.StopAsync(id);
        await WaitForAsync(() => SessionBodies(handler).Any(x =>
            x.GetProperty("status").GetString() == "stopped"));
        await Task.Delay(40);

        Assert.Single(processes);
        Assert.DoesNotContain(Messages(messages), x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("interrotto in modo inatteso"));
        manager.StopAll();
    }

    [Fact]
    public async Task Fixed_busy_port_stops_retrying_with_a_clear_error()
    {
        var port = PortAllocator.FindFree();
        var handler = new RecordingHandler(
            TargetsJson(port, autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess();
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(30));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        var process = Assert.Single(processes);

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        process.Exit(255);

        await WaitForAsync(() => Messages(messages).Any(x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("è già in uso; riconnessione interrotta")));
        Assert.Single(processes);
        await Task.Delay(60);
        Assert.Single(processes);
        manager.StopAll();
    }

    [Fact]
    public async Task Auto_allocated_port_is_replaced_when_it_is_no_longer_free()
    {
        var handler = new RecordingHandler(
            TargetsJson(localPort: null, autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var startInfos = new ConcurrentQueue<ProcessStartInfo>();
        var manager = CreateManager(
            handler, messages, startInfo =>
            {
                startInfos.Enqueue(startInfo);
                var process = new FakeSshProcess();
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(30));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        var first = Assert.Single(processes);
        var oldPort = Assert.Single(StateItems(messages))
            .GetProperty("localPort").GetInt32();

        using var listener = new TcpListener(IPAddress.Loopback, oldPort);
        listener.Start();
        first.Exit(255);

        await WaitForAsync(() => ProcessesWith(processes) == 2);
        await WaitForAsync(() =>
        {
            var items = StateItems(messages);
            return items.Count == 1 &&
                items[0].GetProperty("localPort").GetInt32() != oldPort;
        });
        var newPort = Assert.Single(StateItems(messages))
            .GetProperty("localPort").GetInt32();
        var arguments = startInfos.ToArray()[1].ArgumentList;
        Assert.Contains(arguments, x => x == $"127.0.0.1:{newPort}:127.0.0.1:8080");

        manager.StopAll();
    }

    [Fact]
    public async Task Successful_retry_uses_a_new_id_session_and_correlation()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var starts = 0;
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess
                {
                    ExitOnStart = Interlocked.Increment(ref starts) == 1
                        ? 255
                        : null
                };
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(10));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await WaitForAsync(() => ProcessesWith(processes) == 2);
        await WaitForAsync(() => SessionBodies(handler).Count(x =>
            x.GetProperty("status").GetString() == "active") == 2);

        var states = Messages(messages)
            .Where(x => x.GetProperty("type").GetString() == "tunnels.state")
            .Select(x => x.GetProperty("items").EnumerateArray().ToArray())
            .Where(x => x.Length == 1)
            .Select(x => x[0].GetProperty("id").GetString())
            .ToArray();
        var ids = states.Select(x => x!).Distinct().ToArray();
        Assert.Equal(2, ids.Length);

        var sessions = SessionBodies(handler);
        Assert.Equal(
            [ids[0], ids[0], ids[1]],
            sessions
                .Select(x => x.GetProperty("sessionKey").GetString()!)
                .ToArray());
        Assert.Equal(
            ["active", "error", "active"],
            sessions.Select(x => x.GetProperty("status").GetString()!).ToArray());

        var correlations = EventBodies(handler)
            .Where(x => x.GetProperty("kind").GetString() == "start")
            .Select(x => x.GetProperty("correlationId").GetString()!)
            .ToArray();
        Assert.Equal(2, correlations.Length);
        Assert.Equal(2, correlations.Distinct().Count());
        Assert.Contains(Messages(messages), x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("riconnesso"));

        await Task.Delay(40);
        Assert.Equal(2, ProcessesWith(processes));
        manager.StopAll();
        await WaitForAsync(() => SessionBodies(handler).Any(x =>
            x.GetProperty("status").GetString() == "stopped"));
    }

    [Fact]
    public async Task Manual_service_without_auto_start_does_not_reconnect()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: false));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess();
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(10));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        Assert.Empty(processes);
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);
        var process = Assert.Single(processes);

        process.Exit(255);
        await WaitForAsync(() => SessionBodies(handler).Any(x =>
            x.GetProperty("status").GetString() == "error"));
        await Task.Delay(60);

        Assert.Single(processes);
        Assert.DoesNotContain(Messages(messages), x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("interrotto in modo inatteso"));
        manager.StopAll();
    }

    [Fact]
    public async Task Forbidden_session_projection_disables_reconnect()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: true),
            sessionStatus: HttpStatusCode.Forbidden);
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess();
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(10));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        var process = Assert.Single(processes);
        await WaitForAsync(() => Messages(messages).Any(x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("Permessi tunnel non più validi")));

        process.Exit(255);
        await Task.Delay(80);

        Assert.Single(processes);
        Assert.DoesNotContain(Messages(messages), x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("interrotto in modo inatteso"));
        manager.StopAll();
    }

    [Fact]
    public async Task Panel_close_cancels_a_pending_retry()
    {
        var handler = new RecordingHandler(
            TargetsJson(PortAllocator.FindFree(), autoStart: true));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var starts = 0;
        var manager = CreateManager(
            handler, messages, _ =>
            {
                var process = new FakeSshProcess
                {
                    ExitOnStart = Interlocked.Increment(ref starts) == 1
                        ? 255
                        : null
                };
                processes.Enqueue(process);
                return process;
            },
            reconnectDelay: TimeSpan.FromMilliseconds(100));

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await WaitForAsync(() => Messages(messages).Any(x =>
            IsLog(x) && x.GetProperty("message").GetString()!
                .Contains("interrotto in modo inatteso")));

        manager.Handle(JsonSerializer.Serialize(new
        {
            type = "tunnels.retries.cancel",
            url = InstanceUrl
        }));

        await Task.Delay(180);
        Assert.Single(processes);
        manager.StopAll();
    }

    private static int ProcessesWith(ConcurrentQueue<FakeSshProcess> processes) =>
        processes.Count(x => x.Started);

    private static TunnelManager CreateManager(
        RecordingHandler handler,
        ConcurrentQueue<object> messages,
        Func<ProcessStartInfo, FakeSshProcess> processFactory,
        TimeSpan? reconnectDelay = null) =>
        new(
            new HttpClient(handler),
            messages.Enqueue,
            reconnectDelay: reconnectDelay)
        {
            SshProcessFactory = startInfo => processFactory(startInfo)
        };

    private static string TargetsJson(
        int? localPort,
        bool autoStart) =>
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
              "targets": [],
              "services": [
                {
                  "id": 10,
                  "serverId": 1,
                  "name": "Web",
                  "mode": "Local",
                  "targetHost": "127.0.0.1",
                  "targetPort": 8080,
                  "localPort": {{localPort?.ToString() ?? "null"}},
                  "remotePort": null,
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": {{(autoStart ? "true" : "false")}},
                  "enabled": true
                }
              ]
            }
          ]
        }
        """;

    private static bool IsLog(JsonElement message) =>
        message.GetProperty("type").GetString() == "tunnels.log";

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

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent);

    private sealed class RecordingHandler(
        string targetsJson,
        HttpStatusCode? sessionStatus = null)
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

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tunnels/targets")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        targetsJson, Encoding.UTF8, "application/json")
                };
            }

            return path == "/api/tunnels/sessions" && sessionStatus is { } status
                ? new HttpResponseMessage(status)
                : NoContent();
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

        public int? ExitOnStart { get; init; }
        public bool Started { get; private set; }
        public int ExitCode { get; private set; }

        public void Start()
        {
            Started = true;
            if (ExitOnStart is int code)
                Exit(code);
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
