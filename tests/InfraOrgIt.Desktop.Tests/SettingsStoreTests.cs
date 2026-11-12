using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Settings;

namespace InfraOrgIt.Desktop.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infraorgit-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Default_path_sits_next_to_instances_json()
    {
        var path = SettingsStore.DefaultPath();

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("settings.json", path);
        Assert.Equal(
            Path.GetDirectoryName(InstanceStore.DefaultPath()),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void Load_defaults_to_auto_when_the_file_is_missing()
    {
        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(DownloadMode.Auto, settings.DownloadMode);
    }

    [Theory]
    [InlineData(DownloadMode.Auto)]
    [InlineData(DownloadMode.Bridge)]
    [InlineData(DownloadMode.Native)]
    public void Save_then_load_roundtrips(DownloadMode mode)
    {
        var store = new SettingsStore(FilePath);

        var saved = store.Save(new DesktopSettings { DownloadMode = mode });
        var loaded = store.Load();

        Assert.Equal(mode, saved.DownloadMode);
        Assert.Equal(mode, loaded.DownloadMode);
        Assert.Contains(
            $"\"downloadMode\": \"{DownloadModeNames.ToWireValue(mode)}\"",
            File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "downloadMode": "unsupported" }""")]
    [InlineData("""{ "downloadMode": 7 }""")]
    public void Load_falls_back_to_defaults_on_malformed_content(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(DownloadMode.Auto, settings.DownloadMode);
    }

    [Fact]
    public void Load_falls_back_when_the_path_is_not_a_file()
    {
        Directory.CreateDirectory(FilePath);

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(DownloadMode.Auto, settings.DownloadMode);
    }

    [Fact]
    public void Save_rejects_an_undefined_mode()
    {
        var store = new SettingsStore(FilePath);

        Assert.Throws<ArgumentException>(() =>
            store.Save(new DesktopSettings
            {
                DownloadMode = (DownloadMode)99
            }));
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temporary_file()
    {
        var store = new SettingsStore(FilePath);

        store.Save(new DesktopSettings { DownloadMode = DownloadMode.Bridge });

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(
            DownloadMode.Bridge, new SettingsStore(FilePath).Load().DownloadMode);
    }
}
