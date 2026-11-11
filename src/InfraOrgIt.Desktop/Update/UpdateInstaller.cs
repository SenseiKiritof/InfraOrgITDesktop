using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InfraOrgIt.Desktop.Update;

/// <summary>How the manager should label the install action after a download.</summary>
public enum UpdateInstallMode
{
    /// <summary>The download did not pass the SHA-256 check, so it is not installable.</summary>
    None,

    /// <summary>A helper replaces the app and restarts it.</summary>
    Restart,

    /// <summary>The platform needs a manual install; the button opens the folder.</summary>
    Open,
}

public enum UpdateInstallStatus
{
    /// <summary>The detached helper was started; the app must exit now.</summary>
    Launched,

    /// <summary>The folder was opened so the user can install manually.</summary>
    Manual,

    /// <summary>The download or archive is not eligible; nothing was launched.</summary>
    Rejected,
}

public sealed record UpdateInstallResult(UpdateInstallStatus Status, string Message)
{
    public bool Launched => Status == UpdateInstallStatus.Launched;
}

internal enum UpdateInstallPlatform
{
    MacOS,
    Windows,
    Linux,
    Other,
}

/// <summary>
/// Side effects of an install: current process, app location and the folder
/// opener. Tests replace this to exercise the decisions without touching the
/// real app.
/// </summary>
internal interface IUpdateInstallEnvironment
{
    UpdateInstallPlatform Platform { get; }

    string BaseDirectory { get; }

    int ProcessId { get; }

    string TempDirectory { get; }

    string? ProcessPath { get; }

    bool OpenFolder(string path);
}

/// <summary>
/// Runs tools and starts the detached helper. Arguments are always passed as a
/// list, never as a shell string.
/// </summary>
internal interface IUpdateInstallProcess
{
    UpdateProcessResult Run(string fileName, IReadOnlyList<string> arguments);

    void StartDetached(string fileName, IReadOnlyList<string> arguments);
}

internal sealed record UpdateProcessResult(int ExitCode, string Output);

/// <summary>
/// Installs a downloaded and SHA-256 verified archive. macOS replaces the
/// current <c>.app</c> from a detached helper, Linux does the same for a
/// published app directory and Windows replaces a published app folder from a
/// detached PowerShell helper; every other case falls back to opening the
/// download folder. A missing or failed checksum can never reach a helper.
/// </summary>
public sealed class UpdateInstaller
{
    internal const string MacBundleIdentifier = "it.infraorgit.desktop";
    internal const string MacBundleMarker = "/Contents/MacOS";
    internal const string WindowsExecutableName = "InfraOrgIt.Desktop.exe";

    private const string OtherManualMessage =
        "Installazione automatica non disponibile su questo sistema: " +
        "installa manualmente l'aggiornamento.";

    private readonly IUpdateInstallEnvironment _environment;
    private readonly IUpdateInstallProcess _process;

    public UpdateInstaller()
        : this(new SystemUpdateInstallEnvironment(), new SystemUpdateInstallProcess())
    {
    }

    internal UpdateInstaller(
        IUpdateInstallEnvironment environment,
        IUpdateInstallProcess process)
    {
        _environment = environment;
        _process = process;
    }

    /// <summary>
    /// Decides the button label without unpacking the archive. The full
    /// validation still runs on <see cref="Install"/>; an invalid archive falls
    /// back to the manual folder.
    /// </summary>
    public UpdateInstallMode GetMode(UpdateDownloadResult download)
    {
        if (FindRejection(download) is not null)
            return UpdateInstallMode.None;

        var archive = download.FilePath!;
        return _environment.Platform switch
        {
            UpdateInstallPlatform.MacOS when IsAutomaticMacTarget(archive) =>
                UpdateInstallMode.Restart,
            UpdateInstallPlatform.Linux when IsAutomaticLinuxTarget(archive) =>
                UpdateInstallMode.Restart,
            UpdateInstallPlatform.Windows when IsAutomaticWindowsTarget(archive) =>
                UpdateInstallMode.Restart,
            _ => UpdateInstallMode.Open,
        };
    }

    /// <summary>
    /// Installs only a successful download whose SHA-256 was verified. Every
    /// platform checks this first, so no helper can start unverified.
    /// </summary>
    public UpdateInstallResult Install(UpdateDownloadResult download)
    {
        var rejection = FindRejection(download);
        if (rejection is not null)
            return new UpdateInstallResult(UpdateInstallStatus.Rejected, rejection);

        var archive = download.FilePath!;
        try
        {
            return _environment.Platform switch
            {
                UpdateInstallPlatform.MacOS => InstallMac(archive),
                UpdateInstallPlatform.Linux => InstallLinux(archive),
                UpdateInstallPlatform.Windows => InstallWindows(archive),
                _ => OpenManual(archive, OtherManualMessage),
            };
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or Win32Exception or
            NotSupportedException)
        {
            return OpenManual(
                archive,
                "Preparazione dell'installazione non riuscita: installa manualmente.");
        }
    }

    /// <summary>Null when the download is installable; otherwise the reason.</summary>
    internal static string? FindRejection(UpdateDownloadResult download)
    {
        if (!download.Succeeded)
            return "Download non riuscito: installazione non disponibile.";

        if (!download.ChecksumVerified)
        {
            return "Installazione bloccata: il pacchetto non ha superato " +
                   "la verifica SHA-256.";
        }

        if (string.IsNullOrWhiteSpace(download.FilePath))
        {
            return "Percorso del pacchetto non disponibile: " +
                   "scarica di nuovo l'aggiornamento.";
        }

        if (!File.Exists(download.FilePath))
        {
            return "Pacchetto di aggiornamento non trovato: " +
                   "scarica di nuovo l'aggiornamento.";
        }

        return null;
    }

    /// <summary>
    /// Returns the bundle that contains the running executable, or null when
    /// <paramref name="baseDirectory"/> is not <c>...app/Contents/MacOS</c>
    /// (for example a <c>dotnet run</c> build).
    /// </summary>
    internal static string? FindCurrentBundle(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        var path = baseDirectory
            .Replace('\\', '/')
            .TrimEnd('/');
        if (!path.EndsWith(MacBundleMarker, StringComparison.OrdinalIgnoreCase))
            return null;

        var bundle = path[..^MacBundleMarker.Length];
        return bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
               bundle.Length > ".app".Length
            ? bundle.Replace('/', Path.DirectorySeparatorChar)
            : null;
    }

    /// <summary>Exactly one <c>*.app</c> is expected in the unpacked archive.</summary>
    internal static string? FindSingleApplication(string root)
    {
        var applications = Directory
            .EnumerateDirectories(root, "*.app", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return applications.Length == 1 ? applications[0] : null;
    }

    /// <summary>
    /// Validates the bundle identity from <c>Contents/Info.plist</c> and the
    /// executable directory, without launching anything from the archive.
    /// </summary>
    internal static bool HasExpectedBundleIdentifier(string application)
    {
        var plist = Path.Combine(application, "Contents", "Info.plist");
        var identifier = ReadBundleIdentifier(plist);
        return string.Equals(identifier, MacBundleIdentifier, StringComparison.Ordinal) &&
               Directory.Exists(Path.Combine(application, "Contents", "MacOS"));
    }

    internal static string? ReadBundleIdentifier(string plistPath)
    {
        try
        {
            var document = XDocument.Load(plistPath);
            var key = document
                .Descendants("key")
                .FirstOrDefault(element => element.Value == "CFBundleIdentifier");
            return key?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// A published Linux folder has the apphost executable and no managed
    /// <c>.dll</c> next to it; a <c>dotnet run</c> or framework build is refused.
    /// </summary>
    internal static bool IsInstalledLayout(string appDirectory, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var executable = Path.GetFileName(processPath);
        if (string.IsNullOrEmpty(executable) ||
            executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(appDirectory, executable)))
        {
            return false;
        }

        var assembly = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable[..^4] + ".dll"
            : executable + ".dll";
        return !File.Exists(Path.Combine(appDirectory, assembly));
    }

    internal static bool IsTarGz(string path) =>
        path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(
                directory, ".infraorgit-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe))
            {
                // The probe is only here to prove the directory accepts a file.
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static string AppDirectory(string baseDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));

    private bool IsAutomaticMacTarget(string archive) =>
        archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
        FindCurrentBundle(_environment.BaseDirectory) is { } bundle &&
        Directory.Exists(bundle) &&
        CanReplaceMacBundle(bundle);

    private static bool CanReplaceMacBundle(string bundle) =>
        Path.GetDirectoryName(bundle) is { } parent &&
        IsDirectoryWritable(parent);

    private bool IsAutomaticLinuxTarget(string archive)
    {
        if (!IsTarGz(archive))
            return false;

        var target = AppDirectory(_environment.BaseDirectory);
        if (!IsInstalledLayout(target, _environment.ProcessPath))
            return false;

        var parent = Path.GetDirectoryName(target);
        return parent is not null &&
               IsDirectoryWritable(target) &&
               IsDirectoryWritable(parent);
    }

    private bool IsAutomaticWindowsTarget(string archive)
    {
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        var target = AppDirectory(_environment.BaseDirectory);
        return IsWindowsInstalledLayout(target, _environment.ProcessPath) &&
               CanReplaceWindowsDirectory(target);
    }

    private static bool CanReplaceWindowsDirectory(string target)
    {
        var parent = Path.GetDirectoryName(target);
        return parent is not null &&
               !string.Equals(target, Path.GetPathRoot(target), StringComparison.Ordinal) &&
               IsDirectoryWritable(target) &&
               IsDirectoryWritable(parent);
    }

    /// <summary>
    /// A published Windows folder runs <c>InfraOrgIt.Desktop.exe</c> from the
    /// app directory and has no managed <c>.dll</c> next to it (single-file
    /// publish); <c>dotnet run</c> is refused.
    /// </summary>
    internal static bool IsWindowsInstalledLayout(
        string appDirectory, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var executable = Path.GetFileName(processPath);
        if (!string.Equals(
                executable, WindowsExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    Path.GetDirectoryName(Path.GetFullPath(processPath))!)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(Path.Combine(appDirectory, WindowsExecutableName)) &&
               IsInstalledLayout(appDirectory, processPath);
    }

    private UpdateInstallResult InstallMac(string archive)
    {
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return OpenManual(
                archive,
                "Il pacchetto non è un archivio zip macOS valido: " +
                "installa manualmente.");
        }

        var bundle = FindCurrentBundle(_environment.BaseDirectory);
        if (bundle is null || !Directory.Exists(bundle))
        {
            return OpenManual(
                archive,
                "L'app non è in esecuzione da un bundle .app " +
                "(per esempio con dotnet run): installazione automatica non " +
                "disponibile.");
        }

        if (!CanReplaceMacBundle(bundle))
        {
            return OpenManual(
                archive,
                "La cartella dell'app non è scrivibile: " +
                "installa manualmente con i permessi necessari.");
        }

        var work = CreateWorkDirectory();
        var keep = false;
        try
        {
            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);

            var result = _process.Run(
                "/usr/bin/ditto", ["-x", "-k", archive, extract]);
            if (result.ExitCode != 0)
            {
                return OpenManual(
                    archive,
                    "Estrazione del pacchetto non riuscita: installa manualmente.");
            }

            var application = FindSingleApplication(extract);
            if (application is null)
            {
                return OpenManual(
                    archive,
                    "Il pacchetto non contiene una sola applicazione .app: " +
                    "installa manualmente.");
            }

            if (!HasExpectedBundleIdentifier(application))
            {
                return OpenManual(
                    archive,
                    "L'app nel pacchetto non è InfraOrgIT Desktop: " +
                    "installazione rifiutata.");
            }

            var script = Path.Combine(work, "install.sh");
            File.WriteAllText(script, MacInstallScript, new UTF8Encoding(false));
            _process.StartDetached(
                "/bin/sh",
                [
                    script,
                    _environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    application,
                    bundle,
                    work,
                ]);
            keep = true;

            return new UpdateInstallResult(
                UpdateInstallStatus.Launched,
                "Installazione avviata: l'app si chiuderà e verrà riavviata.");
        }
        finally
        {
            if (!keep)
                TryDeleteDirectory(work);
        }
    }

    private UpdateInstallResult InstallLinux(string archive)
    {
        if (!IsTarGz(archive))
        {
            return OpenManual(
                archive,
                "Il pacchetto non è un archivio tar.gz Linux valido: " +
                "installa manualmente.");
        }

        var target = AppDirectory(_environment.BaseDirectory);
        var executable = string.IsNullOrWhiteSpace(_environment.ProcessPath)
            ? null
            : Path.GetFileName(_environment.ProcessPath);
        if (executable is null ||
            !IsInstalledLayout(target, _environment.ProcessPath))
        {
            return OpenManual(
                archive,
                "L'app non è in esecuzione da un'installazione pubblicata: " +
                "installazione automatica non disponibile.");
        }

        var parent = Path.GetDirectoryName(target);
        if (parent is null ||
            string.Equals(target, Path.GetPathRoot(target), StringComparison.Ordinal) ||
            !IsDirectoryWritable(target) ||
            !IsDirectoryWritable(parent))
        {
            return OpenManual(
                archive,
                "La cartella dell'app non è scrivibile: " +
                "installa manualmente con i permessi necessari.");
        }

        var work = CreateWorkDirectory();
        var keep = false;
        try
        {
            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            using (var file = File.OpenRead(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, extract, overwriteFiles: true);
            }

            var payload = FindLinuxPayload(extract, executable);
            if (payload is null)
            {
                return OpenManual(
                    archive,
                    "Il pacchetto non contiene l'applicazione attesa: " +
                    "installa manualmente.");
            }

            var script = Path.Combine(work, "install.sh");
            File.WriteAllText(script, LinuxInstallScript, new UTF8Encoding(false));
            _process.StartDetached(
                "/bin/sh",
                [
                    script,
                    _environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    payload,
                    target,
                    work,
                    executable,
                ]);
            keep = true;

            return new UpdateInstallResult(
                UpdateInstallStatus.Launched,
                "Installazione avviata: l'app si chiuderà e verrà riavviata.");
        }
        finally
        {
            if (!keep)
                TryDeleteDirectory(work);
        }
    }

    internal static string? FindLinuxPayload(string extractDirectory, string executable)
    {
        var candidates = Directory
            .EnumerateDirectories(extractDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, executable)))
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];

        return File.Exists(Path.Combine(extractDirectory, executable))
            ? extractDirectory
            : null;
    }

    private UpdateInstallResult InstallWindows(string archive)
    {
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return OpenManual(
                archive,
                "Il pacchetto non è un archivio zip Windows valido: " +
                "installa manualmente.");
        }

        var target = AppDirectory(_environment.BaseDirectory);
        if (!IsWindowsInstalledLayout(target, _environment.ProcessPath))
        {
            return OpenManual(
                archive,
                "L'app non è in esecuzione da un'installazione pubblicata " +
                "(per esempio con dotnet run): installazione automatica non " +
                "disponibile.");
        }

        if (!CanReplaceWindowsDirectory(target))
        {
            return OpenManual(
                archive,
                "La cartella dell'app non è scrivibile: " +
                "installa manualmente con i permessi necessari.");
        }

        var payload = FindWindowsPayloadFolder(archive);
        if (payload is null)
        {
            return OpenManual(
                archive,
                "Il pacchetto non contiene una sola cartella con " +
                $"{WindowsExecutableName}: installa manualmente.");
        }

        var work = CreateWorkDirectory();
        var keep = false;
        try
        {
            var script = Path.Combine(work, "install.ps1");
            var log = Path.Combine(
                _environment.TempDirectory, Path.GetFileName(work) + ".log");
            File.WriteAllText(
                script, WindowsInstallScript, new UTF8Encoding(false));
            _process.StartDetached(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", script,
                    "-AppProcessId",
                    _environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    "-ArchivePath", archive,
                    "-TargetDirectory", target,
                    "-WorkDirectory", work,
                    "-LogPath", log,
                    "-PayloadFolder", payload,
                    "-ExecutableName", WindowsExecutableName,
                ]);
            keep = true;

            return new UpdateInstallResult(
                UpdateInstallStatus.Launched,
                "Installazione avviata: l'app si chiuderà e verrà riavviata.");
        }
        finally
        {
            if (!keep)
                TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Reads the zip directory only: exactly one top-level folder, the expected
    /// executable directly inside it and no rooted or <c>..</c> entry. Returns
    /// the folder name, or null when the archive is not installable.
    /// </summary>
    internal static string? FindWindowsPayloadFolder(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        string? root = null;
        var hasExecutable = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0)
                continue;

            if (name.StartsWith('/') || name.Contains(':'))
                return null;

            var trimmed = name.TrimEnd('/');
            var segments = trimmed.Split('/');
            if (segments.Length == 0 ||
                segments.Any(segment => segment.Length == 0 || segment == ".."))
            {
                return null;
            }

            if (root is null)
                root = segments[0];
            else if (!string.Equals(root, segments[0], StringComparison.Ordinal))
                return null;

            if (segments.Length == 2 &&
                !name.EndsWith('/') &&
                string.Equals(
                    segments[1], WindowsExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                hasExecutable = true;
            }
        }

        return root is not null && hasExecutable ? root : null;
    }

    private UpdateInstallResult OpenManual(string archive, string reason)
    {
        var folder = Path.GetDirectoryName(archive);
        var opened = !string.IsNullOrEmpty(folder) && _environment.OpenFolder(folder);
        var message = opened
            ? reason + " Ho aperto la cartella dei download: installa " +
              "manualmente l'aggiornamento."
            : reason + " Cartella non aperta automaticamente: apri il download " +
              "e installa manualmente l'aggiornamento.";
        return new UpdateInstallResult(UpdateInstallStatus.Manual, message);
    }

    private string CreateWorkDirectory()
    {
        var path = Path.Combine(
            _environment.TempDirectory,
            "infraorgit-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stale temp folder is harmless.
        }
    }

    /// <summary>
    /// Waits for the current PID, moves the old bundle aside, copies the new
    /// one with ditto, removes quarantine, relaunches and cleans up. Arguments
    /// arrive through the argument list, so paths are never interpolated into
    /// the script.
    /// </summary>
    internal const string MacInstallScript = """
#!/bin/sh
# Generated by InfraOrgIT Desktop.
# Arguments: PID NEW_APP TARGET_APP WORK_DIR
set -u
pid="$1"
new_app="$2"
target_app="$3"
work_dir="$4"
log="$work_dir/install.log"

exec >>"$log" 2>&1
echo "InfraOrgIT Desktop update helper started."

i=0
while kill -0 "$pid" 2>/dev/null; do
  if [ "$i" -ge 120 ]; then
    echo "Timeout while waiting for the app to exit."
    exit 1
  fi
  i=$((i + 1))
  /bin/sleep 1
done

old_app="${target_app}.old.$$"
if ! /bin/mv "$target_app" "$old_app"; then
  echo "Cannot move the current bundle aside."
  exit 1
fi

if /usr/bin/ditto "$new_app" "$target_app"; then
  /usr/bin/xattr -dr com.apple.quarantine "$target_app" >/dev/null 2>&1 || true
  /usr/bin/open "$target_app" || true
  /bin/rm -rf "$old_app"
  /bin/rm -rf "$work_dir" & exit 0
fi

echo "ditto failed; restoring the previous bundle."
/bin/rm -rf "$target_app"
/bin/mv "$old_app" "$target_app"
exit 1
""";

    /// <summary>
    /// Test seam for the macOS helper: the launch command is the only side
    /// effect that cannot run against a disposable bundle, so an integration
    /// test replaces it with a recorder. Everything else stays as generated.
    /// </summary>
    internal static string CreateMacInstallScript(string launchCommand)
    {
        const string marker = "/usr/bin/open \"$target_app\"";
        if (!MacInstallScript.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The macOS helper template no longer contains the launch line.");
        }

        return MacInstallScript.Replace(
            marker,
            launchCommand + " \"$target_app\"",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits for the current PID, moves the app directory aside, copies the
    /// unpacked payload into place, restores on failure and execs the new
    /// binary. Arguments arrive through the argument list.
    /// </summary>
    internal const string LinuxInstallScript = """
#!/bin/sh
# Generated by InfraOrgIT Desktop.
# Arguments: PID NEW_DIR TARGET_DIR WORK_DIR EXECUTABLE
set -u
pid="$1"
new_dir="$2"
target_dir="$3"
work_dir="$4"
executable="$5"
log="$work_dir/install.log"

exec >>"$log" 2>&1
echo "InfraOrgIT Desktop update helper started."
cd / || exit 1

i=0
while kill -0 "$pid" 2>/dev/null; do
  if [ "$i" -ge 120 ]; then
    echo "Timeout while waiting for the app to exit."
    exit 1
  fi
  i=$((i + 1))
  /bin/sleep 1
done

old_dir="${target_dir}.old.$$"
if ! /bin/mv "$target_dir" "$old_dir"; then
  echo "Cannot move the current app directory aside."
  exit 1
fi

if /bin/mkdir "$target_dir" && /bin/cp -a "$new_dir/." "$target_dir/"; then
  /bin/chmod +x "$target_dir/$executable"
  /bin/rm -rf "$old_dir"
  /bin/rm -rf "$work_dir" & exec "$target_dir/$executable"
fi

echo "Copy failed; restoring the previous app directory."
/bin/rm -rf "$target_dir"
/bin/mv "$old_dir" "$target_dir"
exit 1
""";

    /// <summary>
    /// Waits for the current PID, expands the zip into a fresh temp folder,
    /// validates the single top-level payload folder, moves the app folder
    /// aside, copies the new payload into place, restores on failure, starts
    /// the new exe and cleans up. Every path arrives as an argument, so no
    /// user-controlled string is interpolated into the script body.
    /// </summary>
    internal const string WindowsInstallScript = """
# Generated by InfraOrgIT Desktop.
# Arguments: -AppProcessId -ArchivePath -TargetDirectory -WorkDirectory
#            -LogPath -PayloadFolder -ExecutableName
param(
  [Parameter(Mandatory = $true)][int]$AppProcessId,
  [Parameter(Mandatory = $true)][string]$ArchivePath,
  [Parameter(Mandatory = $true)][string]$TargetDirectory,
  [Parameter(Mandatory = $true)][string]$WorkDirectory,
  [Parameter(Mandatory = $true)][string]$LogPath,
  [Parameter(Mandatory = $true)][string]$PayloadFolder,
  [Parameter(Mandatory = $true)][string]$ExecutableName
)

$ErrorActionPreference = 'Stop'

# The helper inherits the app folder as its current directory when the app was
# started from there; leave it before that folder is moved aside, and keep the
# temp work folder removable later.
Set-Location -LiteralPath ([System.IO.Path]::GetTempPath())

function Write-Log {
  param([string]$Message)
  try {
    $line = "[{0}] {1}" -f (Get-Date).ToString("o"), $Message
    Add-Content -LiteralPath "$LogPath" -Value $line
  } catch {
    # Logging must never stop the replacement.
  }
}

Write-Log "InfraOrgIT Desktop update helper started."

try {
  $deadline = (Get-Date).AddSeconds(120)
  while (Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue) {
    if ((Get-Date) -ge $deadline) {
      throw "Timeout while waiting for the app to exit."
    }
    Start-Sleep -Milliseconds 500
  }
  Write-Log "App exited; expanding the package."

  $extract = Join-Path "$WorkDirectory" "extract"
  if (Test-Path -LiteralPath "$extract") {
    Remove-Item -LiteralPath "$extract" -Recurse -Force
  }
  New-Item -ItemType Directory -Path "$extract" -Force | Out-Null
  Expand-Archive -LiteralPath "$ArchivePath" -DestinationPath "$extract" -Force

  $folders = @(Get-ChildItem -LiteralPath "$extract" -Directory)
  if ($folders.Count -ne 1 -or $folders[0].Name -ne $PayloadFolder) {
    throw "Unexpected package layout."
  }
  $newDir = $folders[0].FullName
  if (-not (Test-Path -LiteralPath (Join-Path "$newDir" "$ExecutableName") -PathType Leaf)) {
    throw "The package does not contain the expected executable."
  }

  $oldDir = "$TargetDirectory.old.$AppProcessId"
  if (Test-Path -LiteralPath "$oldDir") {
    Remove-Item -LiteralPath "$oldDir" -Recurse -Force
  }
  Move-Item -LiteralPath "$TargetDirectory" -Destination "$oldDir"
  Write-Log "Previous installation moved aside."

  try {
    New-Item -ItemType Directory -Path "$TargetDirectory" -Force | Out-Null
    Get-ChildItem -LiteralPath "$newDir" -Force |
      Copy-Item -Destination "$TargetDirectory" -Recurse -Force
    $newExe = Join-Path "$TargetDirectory" "$ExecutableName"
    if (-not (Test-Path -LiteralPath "$newExe" -PathType Leaf)) {
      throw "Copy failed."
    }
    Write-Log "New payload copied."
    Start-Process -FilePath "$newExe" -WorkingDirectory "$TargetDirectory"
    Write-Log "New version started."
  } catch {
    Write-Log ("Replacement failed; restoring the previous installation: " + $_.Exception.Message)
    if (Test-Path -LiteralPath "$TargetDirectory") {
      Remove-Item -LiteralPath "$TargetDirectory" -Recurse -Force -ErrorAction SilentlyContinue
    }
    Move-Item -LiteralPath "$oldDir" -Destination "$TargetDirectory"
    throw
  }

  Remove-Item -LiteralPath "$oldDir" -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath "$WorkDirectory" -Recurse -Force -ErrorAction SilentlyContinue
} catch {
  Write-Log ("Update failed: " + $_.Exception.Message)
  exit 1
}

exit 0
""";

    /// <summary>
    /// Test seam for the Windows helper: the launch step is the only side
    /// effect that opens a second GUI instance, so the self-install test
    /// replaces it with a recorder. Everything else stays as generated.
    /// </summary>
    internal static string CreateWindowsInstallScript(string launchCommand)
    {
        const string marker =
            "Start-Process -FilePath \"$newExe\" -WorkingDirectory \"$TargetDirectory\"";
        if (!WindowsInstallScript.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Windows helper template no longer contains the launch line.");
        }

        return WindowsInstallScript.Replace(
            marker, launchCommand, StringComparison.Ordinal);
    }
}

/// <summary>
/// One install at a time: the manager page can send <c>update.install</c>
/// twice, and the second message must not start another helper.
/// </summary>
internal sealed class UpdateInstallGuard
{
    private int _inProgress;

    public bool TryEnter() => Interlocked.Exchange(ref _inProgress, 1) == 0;

    public void Release() => Interlocked.Exchange(ref _inProgress, 0);
}

internal sealed class SystemUpdateInstallEnvironment : IUpdateInstallEnvironment
{
    public UpdateInstallPlatform Platform =>
        OperatingSystem.IsMacOS()
            ? UpdateInstallPlatform.MacOS
            : OperatingSystem.IsWindows()
                ? UpdateInstallPlatform.Windows
                : OperatingSystem.IsLinux()
                    ? UpdateInstallPlatform.Linux
                    : UpdateInstallPlatform.Other;

    public string BaseDirectory => AppContext.BaseDirectory;

    public int ProcessId => Environment.ProcessId;

    public string TempDirectory => Path.GetTempPath();

    public string? ProcessPath => Environment.ProcessPath;

    public bool OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
            ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class SystemUpdateInstallProcess : IUpdateInstallProcess
{
    public UpdateProcessResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = StartInfo(fileName, arguments);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return new UpdateProcessResult(-1, "Processo non avviato.");

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        return new UpdateProcessResult(
            process.ExitCode,
            output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    public void StartDetached(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = StartInfo(fileName, arguments),
        };
        if (!process.Start())
            throw new InvalidOperationException("Helper non avviato.");
    }

    private static ProcessStartInfo StartInfo(
        string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
