using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
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
