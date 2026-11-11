using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TunnelsStateStoreTests : IDisposable
{
    private const string InstanceUrl = "https://infra.example.com";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infraorgit-tunnels-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "tunnels.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Save_then_load_roundtrips_without_the_temporary_file()
    {
        var store = new TunnelsStateStore(FilePath);
        var service = Entry(serviceId: 10, deploymentId: null, localPort: 15_000);
        var deployment = Entry(serviceId: null, deploymentId: 2, localPort: null);

        store.Save([service, deployment]);

        var loaded = new TunnelsStateStore(FilePath).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(service, loaded[0]);
        Assert.Equal(deployment, loaded[1]);
        Assert.Equal("https://infra.example.com|1|service:10", loaded[0].Key);
        Assert.Equal("https://infra.example.com|1|deployment:2", loaded[1].Key);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Load_skips_invalid_and_duplicate_entries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            [
              {
                "instanceUrl": "https://infra.example.com/",
                "serverId": 1,
                "serviceId": 10,
                "serverApplicationId": null,
                "mode": "Local",
                "localPort": 15000
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": 10,
                "serverApplicationId": null,
                "mode": "Local",
                "localPort": 15000
              },
              {
                "instanceUrl": "ftp://infra.example.com",
                "serverId": 1,
                "serviceId": 11,
                "mode": "Local",
                "localPort": null
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": 12,
                "serverApplicationId": 3,
                "mode": "Local",
                "localPort": null
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": null,
                "serverApplicationId": null,
                "mode": "Local",
                "localPort": null
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": 13,
                "serverApplicationId": null,
                "mode": "Sideways",
                "localPort": null
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": 14,
                "serverApplicationId": null,
                "mode": "Local",
                "localPort": 80
              },
              {
                "instanceUrl": "https://infra.example.com",
                "serverId": 1,
                "serviceId": 15,
                "serverApplicationId": null,
                "mode": "Remote",
                "localPort": 15001
              }
            ]
            """);

        var loaded = new TunnelsStateStore(FilePath).Load();

        var item = Assert.Single(loaded);
        Assert.Equal(10, item.ServiceId);
        Assert.Equal("https://infra.example.com", item.InstanceUrl);
    }

    [Fact]
    public void Save_rejects_invalid_entries_and_keeps_the_previous_file()
    {
        var store = new TunnelsStateStore(FilePath);
        store.Save([Entry(serviceId: 10, deploymentId: null, localPort: null)]);

        Assert.Throws<ArgumentException>(() => store.Save([
            Entry(serviceId: 10, deploymentId: null, localPort: null),
            Entry(serviceId: 10, deploymentId: null, localPort: 15_000)
        ]));
        Assert.Throws<ArgumentException>(() => store.Save([
            new TunnelStateEntry(InstanceUrl, 1, null, null, "Local", null)
        ]));

        var loaded = new TunnelsStateStore(FilePath).Load();
        var item = Assert.Single(loaded);
        Assert.Null(item.LocalPort);
    }

    [Fact]
    public void Add_remove_and_clear_update_the_file()
    {
        var store = new TunnelsStateStore(FilePath);
        var service = Entry(serviceId: 10, deploymentId: null, localPort: null);

        Assert.True(store.Add(service));
        Assert.False(store.Add(service));
        Assert.Single(new TunnelsStateStore(FilePath).Load());

        Assert.True(store.Remove(service.Key));
        Assert.False(store.Remove(service.Key));
        Assert.Empty(new TunnelsStateStore(FilePath).Load());

        Assert.True(store.Add(service));
        Assert.True(store.Clear());
        Assert.False(store.Clear());
        Assert.Empty(new TunnelsStateStore(FilePath).Load());
    }

    [Fact]
    public void RemoveWhere_drops_only_the_stale_identities()
    {
        var store = new TunnelsStateStore(FilePath);
        store.Save([
            Entry(serviceId: 10, deploymentId: null, localPort: null),
            Entry(serviceId: 11, deploymentId: null, localPort: null)
        ]);

        var removed = store.RemoveWhere(x => x.ServiceId == 11);

        Assert.Equal(1, removed);
        var item = Assert.Single(new TunnelsStateStore(FilePath).Load());
        Assert.Equal(10, item.ServiceId);
    }

    [Fact]
    public void Load_returns_empty_when_the_file_is_missing()
    {
        Assert.Empty(new TunnelsStateStore(FilePath).Load());
    }

    [Fact]
    public void Memory_store_never_writes_a_file()
    {
        var store = new TunnelsStateStore(null);

        Assert.False(store.IsPersistent);
        Assert.True(store.Add(
            Entry(serviceId: 10, deploymentId: null, localPort: null)));
        Assert.Single(store.Load());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Default_path_is_next_to_instances_json()
    {
        var path = TunnelsStateStore.DefaultPath();

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("tunnels.json", path);
        Assert.Equal(
            Path.GetDirectoryName(InstanceStore.DefaultPath()),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void TryCreate_normalizes_the_url_mode_and_rejects_both_ids()
    {
        var ok = TunnelStateEntry.TryCreate(
            " HTTPS://Example.com/path/ ",
            1,
            null,
            2,
            "local",
            15_000,
            out var entry,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("https://example.com/path", entry!.InstanceUrl);
        Assert.Equal("Local", entry.Mode);

        Assert.False(TunnelStateEntry.TryCreate(
            InstanceUrl, 1, 10, 2, "Local", null, out _, out var both));
        Assert.Contains("non entrambi", both);
    }

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
}
