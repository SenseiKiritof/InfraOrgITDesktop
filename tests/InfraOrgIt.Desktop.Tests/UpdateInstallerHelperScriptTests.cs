using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Tests;

/// <summary>
/// Runs the generated macOS and Linux update helpers against disposable app
/// layouts. The helper is the risky part of self-replace, so these tests use
/// the real shell script, a real exited PID and real file operations inside a
/// temporary directory. The macOS launch step is replaced by a recorder; the
/// Linux helper execs the disposable fake binary. Nothing outside the test
/// directory is touched.
/// </summary>
public sealed class UpdateInstallerHelperScriptTests
{
    private const string BundleName = "InfraOrgIT Desktop.app";
    private const string LinuxExecutable = "InfraOrgIt.Desktop";

    [SkippableFact]
    public async Task Mac_helper_replaces_the_bundle_and_records_the_launch()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "The macOS helper only runs on macOS.");
        RequireMacTools();

        using var directory = new TempDirectory();
        var target = CreateMacBundle(directory.Root, BundleName, "old-marker");
        var archive = Path.Combine(directory.Root, "update.zip");
        CreateMacArchive(archive, BundleName, "new-marker");

        var extract = Path.Combine(directory.Root, "extract");
        ZipFile.ExtractToDirectory(archive, extract);
        var newApp = Path.Combine(extract, BundleName);
        var work = CreateWorkDirectory(directory.Root);
        var launch = CreateLaunchRecorder(directory.Root);
        var script = Path.Combine(work, "install.sh");
        var scriptText = UpdateInstaller.CreateMacInstallScript(launch);
        await File.WriteAllTextAsync(script, scriptText);
        var pid = await StartAndReapDummyProcessAsync();

        var exit = await RunHelperAsync(
            script, pid.ToString(), newApp, target, work);

        Assert.Equal(0, exit);
        Assert.Equal(
            "new-marker",
            await File.ReadAllTextAsync(
                Path.Combine(target, "Contents", "marker.txt")));
        Assert.True(
            File.Exists(launch + ".called"),
            "The generated helper did not call the launch command.");
        Assert.Equal(
            target,
            (await File.ReadAllTextAsync(launch + ".called")).Trim());
        Assert.Empty(OldBackups(directory.Root));
        Assert.StartsWith(directory.Root, target, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/Applications", scriptText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Mac_helper_restores_the_bundle_when_the_copy_fails()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "The macOS helper only runs on macOS.");
        RequireMacTools();

        using var directory = new TempDirectory();
        var target = CreateMacBundle(directory.Root, BundleName, "old-marker");
        var work = CreateWorkDirectory(directory.Root);
        var launch = CreateLaunchRecorder(directory.Root);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(
            script, UpdateInstaller.CreateMacInstallScript(launch));
        var pid = await StartAndReapDummyProcessAsync();
        var missingApp = Path.Combine(directory.Root, "missing.app");

        var exit = await RunHelperAsync(
            script, pid.ToString(), missingApp, target, work);

        Assert.NotEqual(0, exit);
        Assert.Equal(
            "old-marker",
            await File.ReadAllTextAsync(
                Path.Combine(target, "Contents", "marker.txt")));
        Assert.False(
            File.Exists(launch + ".called"),
            "A failed helper must not launch the app.");
        Assert.Empty(OldBackups(directory.Root));
        Assert.Contains(
            "restoring the previous bundle",
            await File.ReadAllTextAsync(Path.Combine(work, "install.log")));
    }

    [SkippableFact]
    public async Task Linux_helper_replaces_the_app_and_runs_the_new_binary()
    {
        Skip.IfNot(
            OperatingSystem.IsLinux(),
            "The Linux helper only runs on Linux.");
        RequireLinuxTools();

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Root, "app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "old-marker.txt"), "old-marker");
        await File.WriteAllTextAsync(
            Path.Combine(target, LinuxExecutable), "#!/bin/sh\nexit 0\n");

        var archive = Path.Combine(directory.Root, "update.tar.gz");
        CreateLinuxArchive(archive, directory.Root);
        var newDirectory = ExtractLinuxArchive(archive, directory.Root);

        var work = CreateWorkDirectory(directory.Root);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(script, UpdateInstaller.LinuxInstallScript);
        var pid = await StartAndReapDummyProcessAsync();

        var exit = await RunHelperAsync(
            script, pid.ToString(), newDirectory, target, work,
            LinuxExecutable);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(target, "old-marker.txt")));
        Assert.Equal(
            "new-marker",
            await File.ReadAllTextAsync(Path.Combine(target, "marker.txt")));
        Assert.Equal(
            "new",
            (await File.ReadAllTextAsync(Path.Combine(target, "ran.txt"))).Trim());
        Assert.Empty(OldBackups(directory.Root));
        Assert.StartsWith(directory.Root, target, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Linux_helper_restores_the_app_when_the_copy_fails()
    {
        Skip.IfNot(
            OperatingSystem.IsLinux(),
            "The Linux helper only runs on Linux.");
        RequireLinuxTools();

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Root, "app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "old-marker.txt"), "old-marker");
        await File.WriteAllTextAsync(
            Path.Combine(target, LinuxExecutable), "#!/bin/sh\nexit 0\n");

        var work = CreateWorkDirectory(directory.Root);
        var script = Path.Combine(work, "install.sh");
        await File.WriteAllTextAsync(script, UpdateInstaller.LinuxInstallScript);
        var pid = await StartAndReapDummyProcessAsync();
        var missingDirectory = Path.Combine(directory.Root, "missing-payload");

        var exit = await RunHelperAsync(
            script, pid.ToString(), missingDirectory, target, work,
            LinuxExecutable);

        Assert.NotEqual(0, exit);
        Assert.Equal(
            "old-marker",
            await File.ReadAllTextAsync(
                Path.Combine(target, "old-marker.txt")));
        Assert.False(File.Exists(Path.Combine(target, "marker.txt")));
        Assert.Empty(OldBackups(directory.Root));
        Assert.Contains(
            "restoring the previous app directory",
            await File.ReadAllTextAsync(Path.Combine(work, "install.log")));
    }

    private static void RequireMacTools()
    {
        foreach (var tool in new[]
                 {
                     "/bin/sh", "/bin/mv", "/bin/rm", "/bin/sleep",
                     "/usr/bin/ditto",
                 })
        {
            Skip.IfNot(File.Exists(tool), $"{tool} is not available.");
        }
    }

    private static void RequireLinuxTools()
    {
        foreach (var tool in new[]
                 {
                     "/bin/sh", "/bin/mkdir", "/bin/cp", "/bin/chmod",
                     "/bin/mv", "/bin/rm", "/bin/sleep",
                 })
        {
            Skip.IfNot(File.Exists(tool), $"{tool} is not available.");
        }
    }

    private static string CreateMacBundle(
        string root, string name, string marker)
    {
        var bundle = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "marker.txt"), marker);
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "MacOS", "InfraOrgIt.Desktop"),
            "#!/bin/sh\nexit 0\n");
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Resources", "old.txt"),
            "old-resource");
        return bundle;
    }

    private static void CreateMacArchive(
        string path, string bundleName, string marker)
    {
        var staging = Path.Combine(
            Path.GetDirectoryName(path)!,
            "staging-" + Guid.NewGuid().ToString("N"));
        CreateMacBundle(staging, bundleName, marker);
        ZipFile.CreateFromDirectory(
            staging, path, CompressionLevel.Fastest,
            includeBaseDirectory: false);
        Directory.Delete(staging, recursive: true);
    }

    private static void CreateLinuxArchive(string path, string root)
    {
        var staging = Path.Combine(
            root, "staging-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "InfraOrgIT-Desktop-test-linux-x64");
        Directory.CreateDirectory(Path.Combine(payload, "ui"));
        File.WriteAllText(
            Path.Combine(payload, "marker.txt"), "new-marker");
        File.WriteAllText(
            Path.Combine(payload, "ui", "manager.html"), "<html></html>");
        File.WriteAllText(
            Path.Combine(payload, LinuxExecutable),
            "#!/bin/sh\nprintf 'new\\n' > \"$(dirname \"$0\")/ran.txt\"\n");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(
                staging, gzip, includeBaseDirectory: false);
        }

        Directory.Delete(staging, recursive: true);
    }

    private static string ExtractLinuxArchive(string path, string root)
    {
        var extract = Path.Combine(
            root, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);
        using (var file = File.OpenRead(path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzip, extract, overwriteFiles: true);
        }

        return Path.Combine(extract, "InfraOrgIT-Desktop-test-linux-x64");
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

    private static string CreateWorkDirectory(string root)
    {
        var work = Path.Combine(
            root, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
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

    private static async Task<int> RunHelperAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The helper did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The installer helper did not finish in 30 seconds.");
        }

        return process.ExitCode;
    }

    private static string[] OldBackups(string root) =>
        Directory.GetDirectories(root, "*.old.*");

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "infraorgit-helper-tests",
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
