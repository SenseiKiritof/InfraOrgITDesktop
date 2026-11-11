using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public void Install_rejects_an_unverified_download_without_running_anything()
    {
        using var directory = new TempDirectory();
        var archive = directory.WriteFile("update.zip", [1, 2, 3]);
        var process = new FakeProcess();
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(directory.Root, "bin", "Debug", "net10.0"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: false);

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Rejected, result.Status);
        Assert.Contains("SHA-256", result.Message);
        Assert.Equal(UpdateInstallMode.None, installer.GetMode(download));
        Assert.Empty(process.Runs);
        Assert.Empty(process.Detached);
        Assert.Empty(environment.OpenedFolders);
    }

    [Fact]
    public void Install_rejects_a_failed_download()
    {
        using var directory = new TempDirectory();
        var archive = directory.WriteFile("update.zip", [1, 2, 3]);
        var process = new FakeProcess();
        var installer = new UpdateInstaller(
            new FakeEnvironment { Platform = UpdateInstallPlatform.MacOS },
            process);

        var result = installer.Install(new UpdateDownloadResult(
            Succeeded: false,
            Message: "Download non riuscito.",
            FilePath: archive,
            ChecksumVerified: true));

        Assert.Equal(UpdateInstallStatus.Rejected, result.Status);
        Assert.Empty(process.Runs);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Install_rejects_a_missing_archive()
    {
        using var directory = new TempDirectory();
        var process = new FakeProcess();
        var installer = new UpdateInstaller(
            new FakeEnvironment { Platform = UpdateInstallPlatform.MacOS },
            process);

        var result = installer.Install(Download(
            Path.Combine(directory.Root, "missing.zip"), checksumVerified: true));

        Assert.Equal(UpdateInstallStatus.Rejected, result.Status);
        Assert.Contains("non trovato", result.Message);
        Assert.Empty(process.Runs);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Mac_install_uses_ditto_and_starts_a_detached_helper()
    {
        using var directory = new TempDirectory();
        var bundle = directory.CreateMacBundle("InfraOrgIT Desktop.app");
        var archive = Path.Combine(directory.Root, "update.zip");
        CreateMacZip(archive, "InfraOrgIT Desktop.app", UpdateInstaller.MacBundleIdentifier);
        var process = new FakeProcess
        {
            RunHandler = Unzip,
        };
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(bundle, "Contents", "MacOS")
                + Path.DirectorySeparatorChar,
            TempDirectory = Path.Combine(directory.Root, "tmp"),
            ProcessId = 4242,
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: true);

        Assert.Equal(UpdateInstallMode.Restart, installer.GetMode(download));

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Launched, result.Status);
        Assert.True(result.Launched);
        var run = Assert.Single(process.Runs);
        Assert.Equal("/usr/bin/ditto", run.FileName);
        Assert.Equal(["-x", "-k", archive], run.Arguments.Take(3));

        var helper = Assert.Single(process.Detached);
        Assert.Equal("/bin/sh", helper.FileName);
        Assert.Equal(5, helper.Arguments.Count);
        var script = helper.Arguments[0];
        Assert.Equal("4242", helper.Arguments[1]);
        Assert.EndsWith(".app", helper.Arguments[2]);
        Assert.Equal(
            Path.GetFileName(bundle),
            Path.GetFileName(helper.Arguments[2]));
        Assert.Equal(bundle, helper.Arguments[3]);
        Assert.Equal(Path.GetDirectoryName(script), helper.Arguments[4]);

        var content = File.ReadAllText(script);
        Assert.Contains("/usr/bin/ditto \"$new_app\" \"$target_app\"", content);
        Assert.Contains("/usr/bin/xattr -dr com.apple.quarantine", content);
        Assert.Contains("/usr/bin/open \"$target_app\"", content);
        Assert.Contains("/bin/mv \"$target_app\" \"$old_app\"", content);
        Assert.DoesNotContain(directory.Root, content);
        Assert.DoesNotContain(archive, content);
        Assert.Contains("kill -0 \"$pid\"", content);
    }

    [Fact]
    public void Mac_install_refuses_dotnet_run_and_opens_the_download_folder()
    {
        using var directory = new TempDirectory();
        var archive = Path.Combine(directory.Root, "update.zip");
        CreateMacZip(archive, "InfraOrgIT Desktop.app", UpdateInstaller.MacBundleIdentifier);
        var process = new FakeProcess();
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(directory.Root, "bin", "Debug", "net10.0"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: true);

        Assert.Equal(UpdateInstallMode.Open, installer.GetMode(download));

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Contains("bundle .app", result.Message);
        Assert.Equal(directory.Root, Assert.Single(environment.OpenedFolders));
        Assert.Empty(process.Runs);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Mac_install_rejects_an_archive_with_the_wrong_bundle_identifier()
    {
        using var directory = new TempDirectory();
        var bundle = directory.CreateMacBundle("InfraOrgIT Desktop.app");
        var archive = Path.Combine(directory.Root, "update.zip");
        CreateMacZip(archive, "InfraOrgIT Desktop.app", "com.example.evil");
        var process = new FakeProcess { RunHandler = Unzip };
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(bundle, "Contents", "MacOS"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);

        var result = installer.Install(Download(archive, checksumVerified: true));

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Contains("non è InfraOrgIT Desktop", result.Message);
        Assert.Equal(directory.Root, Assert.Single(environment.OpenedFolders));
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Mac_install_rejects_an_archive_without_an_app()
    {
        using var directory = new TempDirectory();
        var bundle = directory.CreateMacBundle("InfraOrgIT Desktop.app");
        var archive = Path.Combine(directory.Root, "update.zip");
        var staging = Path.Combine(directory.Root, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "readme.txt"), "not an app");
        ZipFile.CreateFromDirectory(
            staging, archive, CompressionLevel.Fastest, includeBaseDirectory: false);
        var process = new FakeProcess { RunHandler = Unzip };
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(bundle, "Contents", "MacOS"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);

        var result = installer.Install(Download(archive, checksumVerified: true));

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Contains("una sola applicazione .app", result.Message);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Mac_install_rejects_an_archive_with_more_than_one_app()
    {
        using var directory = new TempDirectory();
        var bundle = directory.CreateMacBundle("InfraOrgIT Desktop.app");
        var archive = Path.Combine(directory.Root, "update.zip");
        var staging = Path.Combine(directory.Root, "staging");
        CreateFakeMacApp(
            Path.Combine(staging, "InfraOrgIT Desktop.app"),
            UpdateInstaller.MacBundleIdentifier);
        CreateFakeMacApp(
            Path.Combine(staging, "Other.app"),
            UpdateInstaller.MacBundleIdentifier);
        ZipFile.CreateFromDirectory(
            staging, archive, CompressionLevel.Fastest, includeBaseDirectory: false);
        var process = new FakeProcess { RunHandler = Unzip };
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.MacOS,
            BaseDirectory = Path.Combine(bundle, "Contents", "MacOS"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);

        var result = installer.Install(Download(archive, checksumVerified: true));

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Windows_install_opens_the_folder_and_never_runs_a_helper()
    {
        using var directory = new TempDirectory();
        var archive = directory.WriteFile("update.zip", [1, 2, 3]);
        var process = new FakeProcess();
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.Windows,
            BaseDirectory = Path.Combine(directory.Root, "app"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: true);

        Assert.Equal(UpdateInstallMode.Open, installer.GetMode(download));

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Contains("Windows", result.Message);
        Assert.Equal(directory.Root, Assert.Single(environment.OpenedFolders));
        Assert.Empty(process.Runs);
        Assert.Empty(process.Detached);
    }

    [Fact]
    public void Linux_install_starts_a_helper_for_a_published_layout()
    {
        using var directory = new TempDirectory();
        var app = Path.Combine(directory.Root, "app");
        Directory.CreateDirectory(app);
        var executable = Path.Combine(app, "InfraOrgIt.Desktop");
        File.WriteAllText(executable, "old binary");
        var archive = Path.Combine(directory.Root, "update.tar.gz");
        CreateLinuxArchive(archive, "InfraOrgIT-Desktop-0.2.0-linux-x64");
        var process = new FakeProcess();
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.Linux,
            BaseDirectory = app + Path.DirectorySeparatorChar,
            ProcessPath = executable,
            TempDirectory = Path.Combine(directory.Root, "tmp"),
            ProcessId = 777,
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: true);

        Assert.Equal(UpdateInstallMode.Restart, installer.GetMode(download));

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Launched, result.Status);
        var helper = Assert.Single(process.Detached);
        Assert.Equal("/bin/sh", helper.FileName);
        Assert.Equal(6, helper.Arguments.Count);
        Assert.Equal("777", helper.Arguments[1]);
        Assert.EndsWith(
            "InfraOrgIT-Desktop-0.2.0-linux-x64", helper.Arguments[2]);
        Assert.Equal(app, helper.Arguments[3]);
        Assert.Equal("InfraOrgIt.Desktop", helper.Arguments[5]);

        var content = File.ReadAllText(helper.Arguments[0]);
        Assert.Contains("/bin/cp -a \"$new_dir/.\" \"$target_dir/\"", content);
        Assert.Contains("exec \"$target_dir/$executable\"", content);
        Assert.DoesNotContain(directory.Root, content);
    }

    [Fact]
    public void Linux_install_refuses_a_dotnet_run_layout()
    {
        using var directory = new TempDirectory();
        var app = Path.Combine(directory.Root, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "InfraOrgIt.Desktop.dll"), "managed");
        var archive = Path.Combine(directory.Root, "update.tar.gz");
        CreateLinuxArchive(archive, "InfraOrgIT-Desktop-0.2.0-linux-x64");
        var process = new FakeProcess();
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.Linux,
            BaseDirectory = app,
            ProcessPath = Path.Combine(app, "InfraOrgIt.Desktop"),
            TempDirectory = Path.Combine(directory.Root, "tmp"),
        };
        var installer = new UpdateInstaller(environment, process);
        var download = Download(archive, checksumVerified: true);

        Assert.Equal(UpdateInstallMode.Open, installer.GetMode(download));

        var result = installer.Install(download);

        Assert.Equal(UpdateInstallStatus.Manual, result.Status);
        Assert.Contains("installazione pubblicata", result.Message);
        Assert.Empty(process.Detached);
    }

    [Theory]
    [InlineData("/Applications/InfraOrgIT Desktop.app/Contents/MacOS/", true)]
    [InlineData("/Applications/InfraOrgIT Desktop.app/Contents/MacOS", true)]
    [InlineData("/Users/me/project/bin/Debug/net10.0/", false)]
    [InlineData("/Applications/InfraOrgIT Desktop.app/Contents/Resources/", false)]
    public void FindCurrentBundle_requires_the_Contents_MacOS_marker(
        string baseDirectory, bool expected)
    {
        var bundle = UpdateInstaller.FindCurrentBundle(baseDirectory);

        Assert.Equal(expected, bundle is not null);
        if (expected)
        {
            Assert.NotNull(bundle);
            Assert.EndsWith(
                "InfraOrgIT Desktop.app", bundle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadBundleIdentifier_reads_the_plist_value()
    {
        using var directory = new TempDirectory();
        var plist = directory.WriteFile("Info.plist", InfoPlist("it.example.test"));

        Assert.Equal(
            "it.example.test",
            UpdateInstaller.ReadBundleIdentifier(plist));
    }

    private static UpdateDownloadResult Download(string path, bool checksumVerified) =>
        new(
            Succeeded: true,
            Message: "Download completato.",
            FilePath: path,
            ChecksumVerified: checksumVerified);

    private static UpdateProcessResult Unzip(IReadOnlyList<string> arguments)
    {
        ZipFile.ExtractToDirectory(arguments[2], arguments[3]);
        return new UpdateProcessResult(0, "");
    }

    private static void CreateMacZip(
        string zipPath, string appName, string identifier)
    {
        var staging = Path.Combine(
            Path.GetDirectoryName(zipPath)!, "staging-" + Guid.NewGuid().ToString("N"));
        CreateFakeMacApp(Path.Combine(staging, appName), identifier);
        ZipFile.CreateFromDirectory(
            staging, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(staging, recursive: true);
    }

    private static void CreateFakeMacApp(string appPath, string identifier)
    {
        Directory.CreateDirectory(Path.Combine(appPath, "Contents", "MacOS"));
        File.WriteAllText(
            Path.Combine(appPath, "Contents", "Info.plist"),
            InfoPlist(identifier));
        File.WriteAllText(
            Path.Combine(appPath, "Contents", "MacOS", "InfraOrgIt.Desktop"),
            "binary");
    }

    private static void CreateLinuxArchive(string archive, string rootName)
    {
        var staging = Path.Combine(
            Path.GetDirectoryName(archive)!, "staging-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, rootName);
        Directory.CreateDirectory(Path.Combine(payload, "ui"));
        File.WriteAllText(
            Path.Combine(payload, "InfraOrgIt.Desktop"), "new binary");
        File.WriteAllText(
            Path.Combine(payload, "ui", "manager.html"), "<html></html>");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(
                staging, gzip, includeBaseDirectory: false);
        }

        Directory.Delete(staging, recursive: true);
    }

    private static string InfoPlist(string identifier) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleIdentifier</key>
          <string>{identifier}</string>
        </dict>
        </plist>
        """;

    private sealed record ProcessCall(
        string FileName, IReadOnlyList<string> Arguments);

    private sealed class FakeProcess : IUpdateInstallProcess
    {
        public List<ProcessCall> Runs { get; } = [];

        public List<ProcessCall> Detached { get; } = [];

        public Func<IReadOnlyList<string>, UpdateProcessResult>? RunHandler { get; init; }

        public UpdateProcessResult Run(
            string fileName, IReadOnlyList<string> arguments)
        {
            Runs.Add(new ProcessCall(fileName, arguments.ToArray()));
            return RunHandler?.Invoke(arguments) ?? new UpdateProcessResult(0, "");
        }

        public void StartDetached(
            string fileName, IReadOnlyList<string> arguments)
        {
            Detached.Add(new ProcessCall(fileName, arguments.ToArray()));
        }
    }

    private sealed class FakeEnvironment : IUpdateInstallEnvironment
    {
        public required UpdateInstallPlatform Platform { get; init; }

        public string BaseDirectory { get; init; } = "";

        public int ProcessId { get; init; } = 4242;

        public string TempDirectory { get; init; } = Path.GetTempPath();

        public string? ProcessPath { get; init; }

        public List<string> OpenedFolders { get; } = [];

        public bool OpenFolder(string path)
        {
            OpenedFolders.Add(path);
            return true;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "infraorgit-install-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateMacBundle(string name)
        {
            var bundle = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
            File.WriteAllText(
                Path.Combine(bundle, "Contents", "Info.plist"),
                UpdateInstallerTests.InfoPlist(UpdateInstaller.MacBundleIdentifier));
            return bundle;
        }

        public string WriteFile(string name, byte[] content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string WriteFile(string name, string content) =>
            WriteFile(name, Encoding.UTF8.GetBytes(content));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Best effort cleanup for the temporary test folder.
            }
        }
    }
}
