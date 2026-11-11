using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelPersistenceTests : IDisposable
{
    private const string InstanceUrl = "https://infra.example.com";
    private const string Issuer = "https://auth.example.com/realms/infraorgit";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infraorgit-persist-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "tunnels.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Restore_starts_saved_existing_tunnels_only_once()
    {
        var state = new TunnelsStateStore(FilePath);
        var servicePort = PortAllocator.FindFree();
        var deploymentPort = FindOtherFree(servicePort);
        Assert.True(state.Add(
            Entry(serviceId: 10, deploymentId: null, localPort: servicePort)));
        Assert.True(state.Add(
            Entry(serviceId: null, deploymentId: 2, localPort: deploymentPort)));
        Assert.True(state.Add(
            Entry(serviceId: 99, deploymentId: null, localPort: null)));

        var tokens = new MemoryTokenStore();
        await tokens.SaveAsync(InstanceUrl, "stored-refresh");
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, state, tokens, _ => NewProcess(processes));

        var restored = await manager.RestoreSavedTunnelsAsync([Instance()]);

        Assert.Equal(2, restored);
        Assert.Equal(2, processes.Count);
        var saved = state.Load();
        Assert.Equal(2, saved.Count);
        Assert.DoesNotContain(saved, x => x.ServiceId == 99);
        var restoredMessage = Messages(messages).Single(x =>
            x.GetProperty("type").GetString() == "tunnels.restored");
        Assert.Equal(2, restoredMessage.GetProperty("count").GetInt32());

        var again = await manager.RestoreSavedTunnelsAsync([Instance()]);

        Assert.Equal(0, again);
        Assert.Equal(2, processes.Count);
        manager.StopAll();
    }

    [Fact]
    public async Task Missing_token_skips_without_deleting_the_state()
    {
        var state = new TunnelsStateStore(FilePath);
        Assert.True(state.Add(
            Entry(serviceId: 10, deploymentId: null, localPort: null)));
        var handler = new RecordingHandler(
            _ => throw new InvalidOperationException("no request expected"));
        var messages = new ConcurrentQueue<object>();
        var processes = new ConcurrentQueue<FakeSshProcess>();
        var manager = CreateManager(
            handler, messages, state, new MemoryTokenStore(),
            _ => NewProcess(processes));

        var restored = await manager.RestoreSavedTunnelsAsync([Instance()]);

        Assert.Equal(0, restored);
        Assert.Empty(handler.Calls);
        Assert.Empty(processes);
        Assert.Single(state.Load());
        var log = Messages(messages).Single(x =>
            x.GetProperty("type").GetString() == "tunnels.log");
        Assert.Contains("prossimo accesso", log.GetProperty("message").GetString());
        Assert.DoesNotContain(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.auth");
    }

    [Fact]
    public async Task Explicit_stop_removes_the_saved_identity()
    {
        var state = new TunnelsStateStore(FilePath);
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, state, processFactory: _ => process);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);
        var entry = Assert.Single(state.Load());
        Assert.Equal(10, entry.ServiceId);

        var id = LastTunnelId(messages);
        await manager.StopAsync(id);

        await WaitForAsync(() => state.Load().Count == 0);
        manager.StopAll();
    }

    [Fact]
    public async Task Logout_removes_the_saved_identities_of_the_instance()
    {
        var state = new TunnelsStateStore(FilePath);
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, state, processFactory: _ => process);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);
        Assert.Single(state.Load());

        await manager.LogoutAsync(InstanceUrl);

        Assert.Empty(state.Load());
        manager.StopAll();
    }

    [Fact]
    public async Task Unexpected_exit_without_reconnect_removes_the_identity()
    {
        var state = new TunnelsStateStore(FilePath);
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, state, processFactory: _ => process);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);
        Assert.Single(state.Load());

        process.Exit(255);

        await WaitForAsync(() => state.Load().Count == 0);
        manager.StopAll();
    }

    [Fact]
    public async Task App_exit_keeps_the_saved_identity()
    {
        var state = new TunnelsStateStore(FilePath);
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var process = new FakeSshProcess();
        var manager = CreateManager(handler, messages, state, processFactory: _ => process);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");
        await manager.StartAsync(InstanceUrl, 1, null, 10, null);

        manager.StopAll();
        await Task.Delay(50);

        Assert.Single(state.Load());
    }

    [Fact]
    public async Task Deleted_or_disabled_services_are_pruned_on_targets_load()
    {
        var state = new TunnelsStateStore(FilePath);
        Assert.True(state.Add(
            Entry(serviceId: 99, deploymentId: null, localPort: null)));
        Assert.True(state.Add(
            Entry(serviceId: 11, deploymentId: null, localPort: null)));
        var handler = new RecordingHandler(
            request => Respond(request, TargetsJson()));
        var messages = new ConcurrentQueue<object>();
        var manager = CreateManager(handler, messages, state);

        await manager.LoadTargetsAsync(InstanceUrl, "Prod", "token");

        Assert.Empty(state.Load());
        Assert.Contains(Messages(messages), x =>
            x.GetProperty("type").GetString() == "tunnels.log" &&
            x.GetProperty("message").GetString()!.Contains(
                "Rimossi 2 tunnel salvati"));
    }

    private TunnelManager CreateManager(
        RecordingHandler handler,
        ConcurrentQueue<object> messages,
        TunnelsStateStore state,
        ITokenStore? tokens = null,
        Func<ProcessStartInfo, FakeSshProcess>? processFactory = null) =>
        new(
            new HttpClient(handler),
            messages.Enqueue,
            tokenStore: tokens ?? new MemoryTokenStore(),
            state: state)
        {
            SshProcessFactory = processFactory ?? (_ => new FakeSshProcess())
        };

    private static Instance Instance() => new()
    {
        Name = "Prod",
        Url = InstanceUrl
    };

    private static TunnelStateEntry Entry(
        int? serviceId, int? deploymentId, int? localPort)
    {
        Assert.True(TunnelStateEntry.TryCreate(
            InstanceUrl,
            1,
            serviceId,
            deploymentId,
            "Local",
            localPort,
            out var entry,
            out var error), error);
        return entry!;
    }

    private static int FindOtherFree(int first)
    {
        for (var index = 0; index < 20; index++)
        {
            var candidate = PortAllocator.FindFree();
            if (candidate != first)
                return candidate;
        }

        throw new InvalidOperationException("Nessuna porta libera distinta.");
    }

    private static FakeSshProcess NewProcess(
        ConcurrentQueue<FakeSshProcess> processes)
    {
        var process = new FakeSshProcess();
        processes.Enqueue(process);
        return process;
    }

    private static string LastTunnelId(ConcurrentQueue<object> messages) =>
        Messages(messages)
            .Last(x => x.GetProperty("type").GetString() == "tunnels.state")
            .GetProperty("items")[0]
            .GetProperty("id")
            .GetString()!;

    private static HttpResponseMessage Respond(
        HttpRequestMessage request, string targetsJson)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/.well-known/oauth-protected-resource" => Json(
                HttpStatusCode.OK,
                $$"""{"authorization_servers":["{{Issuer}}"]}"""),
            "/realms/infraorgit/protocol/openid-connect/token" => Json(
                HttpStatusCode.OK,
                """
                {
                  "access_token": "access-token",
                  "refresh_token": "rotated-refresh",
                  "token_type": "Bearer",
                  "expires_in": 300
                }
                """),
            "/api/tunnels/targets" => Json(HttpStatusCode.OK, targetsJson),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        };
    }

    private static string TargetsJson() => """
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
                  "suggestedLocalPort": null
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
                  "localPort": null,
                  "remotePort": null,
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": false,
                  "enabled": true
                },
                {
                  "id": 11,
                  "serverId": 1,
                  "name": "Old",
                  "mode": "Local",
                  "targetHost": "127.0.0.1",
                  "targetPort": 8081,
                  "localPort": null,
                  "remotePort": null,
                  "kind": "Tcp",
                  "openPath": null,
                  "autoStart": false,
                  "enabled": false
                }
              ]
            }
          ]
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

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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
            lock (_gate)
                _calls.Add(new Call(request.RequestUri!, body));
            return respond(request);
        }

        public sealed record Call(Uri Url, string Body);
    }

    private sealed class FakeSshProcess : ISshProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string>? ErrorLine;

        public bool Started { get; private set; }
        public int ExitCode { get; private set; }

        public void Start() => Started = true;

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
