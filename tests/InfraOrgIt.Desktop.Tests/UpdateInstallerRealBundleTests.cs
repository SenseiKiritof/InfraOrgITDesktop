using System.Diagnostics;
using System.Xml.Linq;
using InfraOrgIt.Desktop.Update;
using Xunit.Abstractions;

namespace InfraOrgIt.Desktop.Tests;

/// <summary>
/// Runs the generated macOS update helper against two real bundles produced by
/// <c>scripts/publish.sh</c>. The environment variables are provided by
/// <c>scripts/test-real-self-install.sh</c>; without them both tests skip, so
/// the normal CI matrix never builds or launches the app.
/// </summary>
public sealed class UpdateInstallerRealBundleTests
{
    private const string ExecutableName = "InfraOrgIt.Desktop";
    private const string OldMarker = "old-published-bundle";
    private const string NewMarker = "new-published-bundle";

    private readonly ITestOutputHelper _output;

    public UpdateInstallerRealBundleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Published_mac_bundle_is_replaced_after_the_helper_waits()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "The macOS helper only runs on macOS.");
        var paths = GetPaths();
        RequireMacTools();

        using var directory = new TempDirectory();
        var newApp = ExtractPublishedUpdate(paths.NewArchive, directory.Root);
        WriteMarker(paths.OldApp, OldMarker);
        WriteMarker(newApp, NewMarker);

        var oldVersion = ReadBundleVersion(paths.OldApp);
        var newVersion = ReadBundleVersion(newApp);
        Assert.Equal(paths.OldVersion, oldVersion);
        Assert.Equal(paths.NewVersion, newVersion);
        Assert.NotEqual(oldVersion, newVersion);
        Assert.Equal(
            Path.GetFullPath(paths.OldApp),
            Path.GetFullPath(
                UpdateInstaller.FindCurrentBundle(
                    Path.Combine(paths.OldApp, "Contents", "MacOS")
                    + Path.DirectorySeparatorChar)!
            ));
        Assert.True(UpdateInstaller.HasExpectedBundleIdentifier(newApp));

        var work = CreateWorkDirectory(directory.Root);
        var recorder = CreateLaunchRecorder(directory.Root);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(
            script,
            UpdateInstaller.CreateMacInstallScript(ShellQuote(recorder)));
        var home = Path.Combine(directory.Root, "home");
        Directory.CreateDirectory(home);

        var (app, kind) = await StartAppProcessAsync(paths.OldApp, home);
        _output.WriteLine(
            $"Reporter PID {app.Id} is kept alive by {kind}; " +
            $"bundle {oldVersion} -> {newVersion}.");
        try
        {
            using var helper = StartHelper(
                script, app.Id, newApp, paths.OldApp, work);

            // The helper must not replace the bundle while the app is alive.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.False(
                helper.HasExited,
                "The helper exited while the app PID was still alive.");
            Assert.False(
                File.Exists(recorder + ".called"),
                "The helper relaunched the app before it exited.");
            Assert.Equal(oldVersion, ReadBundleVersion(paths.OldApp));
            Assert.Equal(
                OldMarker,
                await File.ReadAllTextAsync(
                    Path.Combine(
                        paths.OldApp,
                        "Contents",
                        "real-self-install-marker.txt")));

            app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync();

            var exit = await WaitForHelperExitAsync(helper);
            Assert.Equal(0, exit);
        }
        finally
        {
            if (!app.HasExited)
                app.Kill(entireProcessTree: true);
            app.Dispose();
        }

        Assert.Equal(newVersion, ReadBundleVersion(paths.OldApp));
        Assert.Equal(
            NewMarker,
            await File.ReadAllTextAsync(
                Path.Combine(paths.OldApp, "Contents", "real-self-install-marker.txt")));
        Assert.Equal(
            Path.GetFullPath(paths.OldApp),
            Path.GetFullPath(
                (await File.ReadAllTextAsync(recorder + ".called")).Trim()));
        Assert.Empty(OldBackups(paths.OldApp));

        // The helper removes its work directory in a background shell before
        // exiting, so allow a few seconds instead of racing the cleanup.
        await WaitUntilAsync(
            () => !Directory.Exists(work),
            TimeSpan.FromSeconds(10),
            "The helper did not remove its work directory.");
        _output.WriteLine(
            $"Replaced bundle {oldVersion} with {newVersion}, relaunch recorded, " +
            "old backup and work directory removed.");
    }

    [SkippableFact]
    public async Task Published_mac_bundle_is_restored_when_the_copy_fails()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "The macOS helper only runs on macOS.");
        var paths = GetPaths();
        RequireMacTools();

        using var directory = new TempDirectory();
        WriteMarker(paths.RestoreApp, OldMarker);
        var oldVersion = ReadBundleVersion(paths.RestoreApp);
        Assert.Equal(paths.OldVersion, oldVersion);

        var work = CreateWorkDirectory(directory.Root);
        var recorder = CreateLaunchRecorder(directory.Root);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(
            script,
            UpdateInstaller.CreateMacInstallScript(ShellQuote(recorder)));
        var pid = await StartAndReapDummyProcessAsync();
        var missingApp = Path.Combine(directory.Root, "missing-update.app");

        using var helper = StartHelper(
            script, pid, missingApp, paths.RestoreApp, work);
        var exit = await WaitForHelperExitAsync(helper);

        Assert.NotEqual(0, exit);
        Assert.Equal(oldVersion, ReadBundleVersion(paths.RestoreApp));
        Assert.Equal(
            OldMarker,
            await File.ReadAllTextAsync(
                Path.Combine(
                    paths.RestoreApp, "Contents", "real-self-install-marker.txt")));
        Assert.False(
            File.Exists(recorder + ".called"),
            "A failed helper must not relaunch the app.");
        Assert.Empty(OldBackups(paths.RestoreApp));
        Assert.Contains(
            "restoring the previous bundle",
            await File.ReadAllTextAsync(Path.Combine(work, "install.log")));
        _output.WriteLine(
            $"Failed copy restored bundle {oldVersion}; the launch recorder " +
            "was not called.");
    }

    private static RealBundlePaths GetPaths()
    {
        var oldApp = Environment.GetEnvironmentVariable("INFRAORGIT_REAL_OLD_APP");
        var restoreApp = Environment.GetEnvironmentVariable(
            "INFRAORGIT_REAL_RESTORE_APP");
        var newArchive = Environment.GetEnvironmentVariable(
            "INFRAORGIT_REAL_NEW_ARCHIVE");
        var oldVersion = Environment.GetEnvironmentVariable(
            "INFRAORGIT_REAL_OLD_VERSION");
        var newVersion = Environment.GetEnvironmentVariable(
            "INFRAORGIT_REAL_NEW_VERSION");
        Skip.If(
            string.IsNullOrWhiteSpace(oldApp) ||
            string.IsNullOrWhiteSpace(restoreApp) ||
            string.IsNullOrWhiteSpace(newArchive) ||
            string.IsNullOrWhiteSpace(oldVersion) ||
            string.IsNullOrWhiteSpace(newVersion),
            "Set the INFRAORGIT_REAL_* paths or run scripts/test-real-self-install.sh.");
        Skip.IfNot(
            Directory.Exists(oldApp) &&
            Directory.Exists(restoreApp) &&
            File.Exists(newArchive),
            "The published bundles from scripts/test-real-self-install.sh are missing.");

        return new RealBundlePaths(
            oldApp!, restoreApp!, newArchive!, oldVersion!, newVersion!);
    }

    private static void RequireMacTools()
    {
        foreach (var tool in new[]
                 {
                     "/bin/sh", "/bin/mv", "/bin/rm", "/bin/sleep",
                     "/usr/bin/ditto", "/usr/bin/xattr",
                 })
        {
            Skip.IfNot(File.Exists(tool), $"{tool} is not available.");
        }
    }

    private static string ExtractPublishedUpdate(string archive, string root)
    {
        var extract = Path.Combine(root, "extract");
        Directory.CreateDirectory(extract);
        var startInfo = new ProcessStartInfo("/usr/bin/ditto")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-x", "-k", archive, extract })
            startInfo.ArgumentList.Add(argument);

        using (var process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("ditto did not start."))
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        var app = UpdateInstaller.FindSingleApplication(extract);
        if (app is null)
        {
            throw new InvalidOperationException(
                "The published archive does not contain a single .app bundle.");
        }

        Assert.True(
            UpdateInstaller.HasExpectedBundleIdentifier(app),
            "The published archive does not contain the InfraOrgIT Desktop bundle.");
        return app;
    }

    private static string ReadBundleVersion(string app)
    {
        var plist = XDocument.Load(Path.Combine(app, "Contents", "Info.plist"));
        var key = plist
            .Descendants("key")
            .FirstOrDefault(element => element.Value == "CFBundleShortVersionString");
        var version = key?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
        Assert.False(
            string.IsNullOrWhiteSpace(version),
            "The published bundle has no CFBundleShortVersionString.");
        return version!;
    }

    private static void WriteMarker(string app, string marker)
    {
        File.WriteAllText(
            Path.Combine(app, "Contents", "real-self-install-marker.txt"), marker);
    }

    private static string CreateLaunchRecorder(string root)
    {
        var path = Path.Combine(root, "launch-recorder.sh");
        File.WriteAllText(
            path,
            "#!/bin/sh\nprintf '%s\\n' \"$1\" > \"${0}.called\"\n");
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The recorder needs a Unix shell.");

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return path;
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string CreateWorkDirectory(string root)
    {
        var work = Path.Combine(
            root, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static async Task<(Process Process, string Kind)> StartAppProcessAsync(
        string bundle, string home)
    {
        var executable = Path.Combine(bundle, "Contents", "MacOS", ExecutableName);
        Assert.True(File.Exists(executable));
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.Environment["HOME"] = home;
        var app = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The app did not start.");

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (!app.HasExited)
            return (app, "the real published app executable");

        var exitCode = app.ExitCode;
        app.Dispose();
        var standInInfo = new ProcessStartInfo("/bin/sleep")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        standInInfo.ArgumentList.Add("10");
        var standIn = Process.Start(standInInfo)
            ?? throw new InvalidOperationException("The stand-in process did not start.");
        return (
            standIn,
            $"a stand-in process (the real app exited with {exitCode} " +
            "without a GUI session)");
    }

    private static Process StartHelper(
        string script, int pid, string newApp, string targetApp, string work)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     script, pid.ToString(), newApp, targetApp, work,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The helper did not start.");
    }

    private static async Task<int> WaitForHelperExitAsync(Process helper)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await helper.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            helper.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The installer helper did not finish in 30 seconds.");
        }

        return helper.ExitCode;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);
            await Task.Delay(100);
        }
    }

    private static async Task<int> StartAndReapDummyProcessAsync()
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dummy process did not start.");
        await process.WaitForExitAsync();
        return process.Id;
    }

    private static string[] OldBackups(string app) =>
        Directory.GetDirectories(Path.GetDirectoryName(app)!, "*.old.*");

    private sealed record RealBundlePaths(
        string OldApp,
        string RestoreApp,
        string NewArchive,
        string OldVersion,
        string NewVersion);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "infraorgit-real-bundle-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

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
