using System.Diagnostics;
using System.IO.Compression;
using InfraOrgIt.Desktop.Update;
using Xunit.Abstractions;

namespace InfraOrgIt.Desktop.Tests;

/// <summary>
/// End-to-end check of the generated Windows PowerShell helper against a real
/// self-contained publish: the real exe runs from the old folder and the
/// helper, produced by <see cref="UpdateInstaller"/>, replaces it after the
/// PID exits. `scripts/self-install-windows.ps1` publishes the two versions
/// and exports the environment variables; without them the tests report
/// Skipped, so the regular build-test matrix is unchanged.
/// </summary>
public sealed class WindowsSelfInstallTests
{
    private const string MarkerFileName = "self-install-marker.txt";

    private const string OldPublishVariable = "INFRAORGIT_SELF_INSTALL_OLD_PUBLISH";
    private const string ArchiveVariable = "INFRAORGIT_SELF_INSTALL_ARCHIVE";
    private const string OldMarkerVariable = "INFRAORGIT_SELF_INSTALL_OLD_MARKER";
    private const string NewMarkerVariable = "INFRAORGIT_SELF_INSTALL_NEW_MARKER";
    private const string EvidenceVariable = "INFRAORGIT_SELF_INSTALL_EVIDENCE";

    private readonly ITestOutputHelper _output;

    public WindowsSelfInstallTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Windows_helper_replaces_a_real_publish_after_the_pid_exits()
    {
        var scenario = RequireScenario();
        using var temp = new TempDirectory();
        var install = temp.CopyFrom(scenario.OldPublish, "install");
        Log($"Old install: {install} (marker {ReadMarker(install)})");

        using var app = StartApp(install);
        try
        {
            await WaitForWindowAsync(app);
            Log($"Started the published app: pid {app.Id}, window {app.MainWindowHandle}");

            var helper = CaptureHelper(
                scenario.Archive, install, app.Id, temp.Root);
            var recorder = temp.CreateLaunchRecorder();
            // Only the launch line changes: the GUI must not be started twice.
            await File.WriteAllTextAsync(
                helper.ScriptPath,
                UpdateInstaller.CreateWindowsInstallScript(recorder.LaunchCommand));
            Assert.Contains(
                recorder.LaunchCommand,
                await File.ReadAllTextAsync(helper.ScriptPath),
                StringComparison.Ordinal);

            using var process = StartHelper(helper.Arguments);
            try
            {
                await WaitForLogAsync(
                    helper.LogPath, "update helper started", TimeSpan.FromSeconds(20));

                Assert.False(
                    process.HasExited,
                    "The helper must wait for the app PID before replacing the folder.");
                Assert.Equal(scenario.OldMarker, ReadMarker(install));
                Assert.DoesNotContain(
                    "App exited",
                    await File.ReadAllTextAsync(helper.LogPath),
                    StringComparison.Ordinal);
                Log(
                    "Helper started and is still waiting: the app PID is alive " +
                    "and the old marker is unchanged.");

                await StopAppAsync(app);
                var exit = await WaitForExitAsync(process, TimeSpan.FromSeconds(90));

                Assert.Equal(0, exit);
                Assert.Equal(scenario.NewMarker, ReadMarker(install));
                Assert.True(
                    File.Exists(recorder.CalledPath),
                    "The helper did not run the launch recorder.");
                Assert.Contains(
                    install,
                    await File.ReadAllTextAsync(recorder.CalledPath),
                    StringComparison.Ordinal);
                Assert.Empty(OldBackups(temp.Root));
                var log = await File.ReadAllTextAsync(helper.LogPath);
                Assert.Contains("App exited", log, StringComparison.Ordinal);
                Log(
                    $"Helper exit {exit}; new marker {ReadMarker(install)}; " +
                    $"launch recorder saw {install}.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            await StopAppAsync(app);
        }
    }

    [SkippableFact]
    public async Task Windows_helper_restores_the_old_folder_when_the_new_exe_fails()
    {
        var scenario = RequireScenario();
        using var temp = new TempDirectory();
        var install = temp.CopyFrom(scenario.OldPublish, "install-fail");
        var corrupt = temp.CreateCorruptArchive(scenario.Archive);
        var oldExecutable = new FileInfo(
            Path.Combine(install, UpdateInstaller.WindowsExecutableName));
        var oldLength = oldExecutable.Length;
        Log($"Old install: {install} (marker {ReadMarker(install)})");

        using var app = StartApp(install);
        try
        {
            await WaitForWindowAsync(app);

            // No seam here: this is the helper exactly as the installer wrote
            // it. The payload exe is not a valid program, so the launch step
            // fails after the old folder has been moved aside.
            var helper = CaptureHelper(corrupt, install, app.Id, temp.Root);
            using var process = StartHelper(helper.Arguments);
            try
            {
                await WaitForLogAsync(
                    helper.LogPath, "update helper started", TimeSpan.FromSeconds(20));
                await StopAppAsync(app);

                var exit = await WaitForExitAsync(process, TimeSpan.FromSeconds(90));

                Assert.NotEqual(0, exit);
                Assert.Equal(scenario.OldMarker, ReadMarker(install));
                Assert.Equal(
                    oldLength,
                    new FileInfo(Path.Combine(
                        install, UpdateInstaller.WindowsExecutableName)).Length);
                Assert.Empty(OldBackups(temp.Root));
                Assert.Contains(
                    "restoring the previous installation",
                    await File.ReadAllTextAsync(helper.LogPath),
                    StringComparison.Ordinal);
                Log(
                    $"Helper exit {exit}; old marker and executable restored " +
                    $"({ReadMarker(install)}, {oldLength} bytes).");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            await StopAppAsync(app);
        }
    }

    private static Scenario RequireScenario()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The Windows helper only runs on Windows.");
        foreach (var variable in new[]
                 {
                     OldPublishVariable, ArchiveVariable,
                     OldMarkerVariable, NewMarkerVariable,
                 })
        {
            Skip.If(
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)),
                $"Set {variable} (run scripts/self-install-windows.ps1).");
        }

        return new Scenario(
            Environment.GetEnvironmentVariable(OldPublishVariable)!,
            Environment.GetEnvironmentVariable(ArchiveVariable)!,
            Environment.GetEnvironmentVariable(OldMarkerVariable)!,
            Environment.GetEnvironmentVariable(NewMarkerVariable)!);
    }

    /// <summary>
    /// Runs the real installer so the generated script, its paths and its
    /// argument list come from production code; the runner replaces only the
    /// detached start with a recorder.
    /// </summary>
    private static HelperCall CaptureHelper(
        string archive, string install, int processId, string tempRoot)
    {
        var environment = new FakeEnvironment
        {
            Platform = UpdateInstallPlatform.Windows,
            BaseDirectory = install,
            ProcessPath = Path.Combine(
                install, UpdateInstaller.WindowsExecutableName),
            ProcessId = processId,
            TempDirectory = tempRoot,
        };
        var process = new CapturingProcess();
        var installer = new UpdateInstaller(environment, process);
        var download = new UpdateDownloadResult(
            Succeeded: true,
            Message: "Download completato.",
            FilePath: archive,
            ChecksumVerified: true);

        Assert.Equal(UpdateInstallMode.Restart, installer.GetMode(download));
        var result = installer.Install(download);
        Assert.Equal(UpdateInstallStatus.Launched, result.Status);

        var call = Assert.Single(process.Detached);
        Assert.Equal("powershell.exe", call.FileName);
        var arguments = call.Arguments.ToArray();
        Assert.Contains("-NoProfile", arguments);
        Assert.Equal(
            "Bypass",
            ArgumentAfter(arguments, "-ExecutionPolicy"));
        return new HelperCall(
            ArgumentAfter(arguments, "-File"),
            ArgumentAfter(arguments, "-LogPath"),
            arguments);
    }

    private static string ArgumentAfter(string[] arguments, string flag)
    {
        var index = Array.IndexOf(arguments, flag);
        Assert.True(
            index >= 0 && index + 1 < arguments.Length,
            $"{flag} is missing from the helper call.");
        return arguments[index + 1];
    }

    private static Process StartApp(string directory)
    {
        var startInfo = new ProcessStartInfo(
            Path.Combine(directory, UpdateInstaller.WindowsExecutableName))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The published app did not start.");
    }

    private static async Task WaitForWindowAsync(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The published app exited before opening a window " +
                    $"(exit code {process.ExitCode}).");
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            "The published app did not open a manager window within 20 seconds.");
    }

    private static async Task WaitForLogAsync(
        string path, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) &&
                    (await File.ReadAllTextAsync(path)).Contains(
                        marker, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
                // The helper may still be writing the line; retry.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"The helper log did not contain '{marker}' within " +
            $"{timeout.TotalSeconds:0} seconds.");
    }

    private static async Task StopAppAsync(Process process)
    {
        if (process.HasExited)
            return;

        process.CloseMainWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static Process StartHelper(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update helper did not start.");
    }

    private static async Task<int> WaitForExitAsync(
        Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"The update helper did not finish in {timeout.TotalSeconds:0} seconds.");
        }

        return process.ExitCode;
    }

    private static string ReadMarker(string directory) =>
        File.ReadAllText(Path.Combine(directory, MarkerFileName)).Trim();

    private static string[] OldBackups(string root) =>
        Directory.GetDirectories(root, "*.old.*");

    private void Log(string message)
    {
        _output.WriteLine(message);
        var evidence = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(evidence))
            return;

        try
        {
            File.AppendAllText(evidence, message + Environment.NewLine);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Evidence is diagnostic only.
        }
    }

    private sealed record Scenario(
        string OldPublish, string Archive, string OldMarker, string NewMarker);

    private sealed record HelperCall(
        string ScriptPath, string LogPath, IReadOnlyList<string> Arguments);

    private sealed record ProcessCall(
        string FileName, IReadOnlyList<string> Arguments);

    private sealed class CapturingProcess : IUpdateInstallProcess
    {
        public List<ProcessCall> Detached { get; } = [];

        public UpdateProcessResult Run(
            string fileName, IReadOnlyList<string> arguments) =>
            new(0, "");

        public void StartDetached(
            string fileName, IReadOnlyList<string> arguments)
        {
            Detached.Add(new ProcessCall(fileName, arguments.ToArray()));
        }
    }

    private sealed class FakeEnvironment : IUpdateInstallEnvironment
    {
        public required UpdateInstallPlatform Platform { get; init; }

        public required string BaseDirectory { get; init; }

        public int ProcessId { get; init; }

        public required string TempDirectory { get; init; }

        public string? ProcessPath { get; init; }

        public bool OpenFolder(string path) => true;
    }

    private sealed class LaunchRecorder
    {
        public LaunchRecorder(string scriptPath)
        {
            ScriptPath = scriptPath;
            CalledPath = Path.Combine(
                Path.GetDirectoryName(scriptPath)!, "launch-recorder.called");
            LaunchCommand = "& '" + scriptPath + "' \"$newExe\"";
        }

        public string ScriptPath { get; }

        public string CalledPath { get; }

        public string LaunchCommand { get; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "infraorgit-self-install-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CopyFrom(string source, string name)
        {
            var destination = Path.Combine(Root, name);
            CopyDirectory(source, destination);
            return destination;
        }

        public LaunchRecorder CreateLaunchRecorder()
        {
            var script = Path.Combine(Root, "launch-recorder.cmd");
            File.WriteAllText(
                script,
                "@echo off\r\n" +
                "> \"%~dp0launch-recorder.called\" echo %~1\r\n");
            return new LaunchRecorder(script);
        }

        /// <summary>
        /// Rebuilds the release archive with the new executable replaced by a
        /// text file, keeping the exact top-level layout.
        /// </summary>
        public string CreateCorruptArchive(string archive)
        {
            var extract = Path.Combine(Root, "corrupt-source");
            ZipFile.ExtractToDirectory(archive, extract);
            var payload = Assert.Single(Directory.GetDirectories(extract));
            File.WriteAllText(
                Path.Combine(payload, UpdateInstaller.WindowsExecutableName),
                "not a Windows executable");

            var corrupt = Path.Combine(Root, "corrupt-update.zip");
            // Zip the payload folder itself: includeBaseDirectory adds its
            // name as the single top-level entry also used by the release zip.
            ZipFile.CreateFromDirectory(
                payload, corrupt, CompressionLevel.Fastest,
                includeBaseDirectory: true);
            return corrupt;
        }

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

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(
                         source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(
                    Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.GetFiles(
                         source, "*", SearchOption.AllDirectories))
            {
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetRelativePath(source, file)),
                    overwrite: true);
            }
        }
    }
}
