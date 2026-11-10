using InfraOrgIt.Desktop.Instances;

namespace InfraOrgIt.Desktop.Tests;

public sealed class InstanceStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infraorgit-desktop-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "instances.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var store = new InstanceStore(FilePath);

        var saved = store.Save([
            new Instance { Name = " Produzione ", Url = "https://infra.example.com/" },
            new Instance { Name = "Test", Url = "http://localhost:3000" }
        ]);

        Assert.Equal("Produzione", saved[0].Name);
        Assert.Equal("https://infra.example.com", saved[0].Url);

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Load_returns_empty_when_file_is_missing()
    {
        var store = new InstanceStore(FilePath);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_skips_invalid_entries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            [
              { "name": "Valida", "url": "https://valid.example.com" },
              { "name": "", "url": "https://empty-name.example.com" },
              { "name": "Ftp", "url": "ftp://invalid.example.com" },
              { "name": "Rotta", "url": "not a url" }
            ]
            """);

        var loaded = new InstanceStore(FilePath).Load();

        var item = Assert.Single(loaded);
        Assert.Equal("Valida", item.Name);
    }

    [Fact]
    public void Save_rejects_non_http_urls()
    {
        var store = new InstanceStore(FilePath);

        var exception = Assert.Throws<ArgumentException>(() =>
            store.Save([new Instance { Name = "Ftp", Url = "ftp://example.com" }]));

        Assert.Contains("http", exception.Message);
    }

    [Fact]
    public void Save_rejects_duplicate_names_and_urls()
    {
        var store = new InstanceStore(FilePath);

        Assert.Throws<ArgumentException>(() => store.Save([
            new Instance { Name = "Stessa", Url = "https://one.example.com" },
            new Instance { Name = "stessa", Url = "https://two.example.com" }
        ]));

        Assert.Throws<ArgumentException>(() => store.Save([
            new Instance { Name = "Uno", Url = "https://same.example.com/" },
            new Instance { Name = "Due", Url = "https://same.example.com" }
        ]));
    }

    [Fact]
    public void Default_path_is_absolute_and_named_instances_json()
    {
        var path = InstanceStore.DefaultPath();

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("instances.json", path);
    }

    [Fact]
    public void TryCreate_trims_the_name_and_normalizes_the_url()
    {
        var ok = Instance.TryCreate(
            "  Prod  ", " HTTPS://Example.com/path/ ", out var instance, out var error);

        Assert.True(ok, error);
        Assert.Equal("Prod", instance.Name);
        Assert.Equal("https://example.com/path", instance.Url);
    }
}
