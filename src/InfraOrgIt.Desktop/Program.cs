using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfraOrgIt.Desktop.Auth;
using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Tunnels;
using InfraOrgIt.Desktop.Update;
using Photino.NET;

namespace InfraOrgIt.Desktop;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static PhotinoWindow? _manager;
    private static TunnelManager? _tunnels;
    private static UpdateChecker? _updates;
    private static UpdateDownloader? _downloads;
    private static UpdateInstaller? _installer;
    private static UpdateCheckResult? _latestRelease;
    private static UpdateDownloadResult? _lastDownload;
    private static readonly UpdateInstallGuard _installGuard = new();
    private static int _restoreStarted;

    [STAThread]
    private static void Main()
    {
        var store = new InstanceStore(InstanceStore.DefaultPath());
        var children = new List<PhotinoWindow>();

        _manager = new PhotinoWindow()
            .SetTitle("InfraOrgIT Desktop")
            .SetUseOsDefaultSize(false)
            .SetSize(980, 720)
            .Center()
            .RegisterWebMessageReceivedHandler(
                (sender, message) => HandleMessage(
                    (PhotinoWindow)sender!, message, store, children))
            .RegisterWindowClosingHandler((_, _) =>
            {
                _tunnels?.StopAll();
                foreach (var child in children.ToArray())
                    child.Close();
                return false;
            })
            .Load(Path.Combine(AppContext.BaseDirectory, "ui", "manager.html"));

        _tunnels = new TunnelManager(
            CreateHttpClient(),
            payload => _manager?.Invoke(() => Send(_manager, payload)),
            tokenStore: TokenStoreFactory.Create(message =>
                _manager?.Invoke(() => Send(
                    _manager, new { type = "tunnels.warning", message }))),
            state: new TunnelsStateStore(TunnelsStateStore.DefaultPath()));

        // GitHub checks use their own clients: the dev TLS override applies to
        // instances, never to the release lookup or the download.
        _updates = new UpdateChecker(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        _downloads = new UpdateDownloader(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        _installer = new UpdateInstaller();

#if DEBUG
        _manager.SetDevToolsEnabled(true);
#endif

        _manager.WaitForClose();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (Environment.GetEnvironmentVariable("INFRAORGIT_INSECURE_TLS") == "1")
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static void HandleMessage(
        PhotinoWindow window,
        string message,
        InstanceStore store,
        List<PhotinoWindow> children)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            if (type is "tunnels.list" or "tunnels.start" or "tunnels.login" or
                "tunnels.restore" or "tunnels.logout" or
                "tunnels.retries.cancel")
            {
                var tunnelUrl = root.GetProperty("url").GetString();
                if (!SafeLoad(store).Any(x => x.Url == tunnelUrl))
                {
                    Send(window, new
                    {
                        type = "tunnels.error",
                        message = "Istanza non trovata. Salva di nuovo la lista."
                    });
                    return;
                }

                _tunnels?.Handle(message);
                return;
            }

            if (type is "tunnels.stop" or "tunnels.login.cancel")
            {
                _tunnels?.Handle(message);
                return;
            }

            if (type is "tunnels.forget")
            {
                _tunnels?.Handle(message);
                return;
            }

            switch (type)
            {
                case "list":
                    Send(window, new
                    {
                        type = "instances",
                        instances = SafeLoad(store),
                        version = _updates?.CurrentVersion
                    });
                    StartSavedTunnelRestore(store);
                    break;

                case "auth.open":
                    OpenVerification(window, root);
                    break;

                case "update.check":
                    _ = CheckForUpdatesAsync();
                    break;

                case "update.download":
                    var confirmed =
                        root.TryGetProperty("confirm", out var confirmElement) &&
                        confirmElement.ValueKind == JsonValueKind.True;
                    _ = DownloadUpdateAsync(confirmed);
                    break;

                case "update.install":
                    _ = InstallUpdateAsync();
                    break;

                case "open-external":
                    OpenExternal(window, root);
                    break;

                case "save":
                    var input = root.GetProperty("instances")
                        .Deserialize<List<Instance>>(JsonOptions) ?? [];
                    var saved = store.Save(input);
                    Send(window, new
                    {
                        type = "instances",
                        instances = saved,
                        version = _updates?.CurrentVersion
                    });
                    break;

                case "open":
                    var url = root.GetProperty("url").GetString();
                    var instance = SafeLoad(store)
                        .FirstOrDefault(x => x.Url == url);
                    if (instance is null)
                    {
                        Send(window, new
                        {
                            type = "error",
                            message = "Istanza non trovata. Salva di nuovo la lista."
                        });
                        break;
                    }

                    OpenInstance(window, instance, children);
                    Send(window, new { type = "opened", url = instance.Url });
                    break;

                default:
                    Send(window, new
                    {
                        type = "error",
                        message = "Messaggio non riconosciuto."
                    });
                    break;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
            KeyNotFoundException or InvalidOperationException)
        {
            Send(window, new { type = "error", message = exception.Message });
        }
    }

    private static void OpenVerification(PhotinoWindow window, JsonElement root)
    {
        var value = root.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps) ||
            _tunnels?.IsVerificationUrl(uri.AbsoluteUri) != true)
        {
            Send(window, new
            {
                type = "tunnels.error",
                message = "Pagina di accesso non valida."
            });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            Send(window, new
            {
                type = "tunnels.log",
                message = "Pagina di accesso aperta nel browser."
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            Send(window, new
            {
                type = "tunnels.error",
                message = "Apertura del browser non riuscita."
            });
        }
    }

    /// <summary>
    /// Runs the GitHub lookup off the message loop and reports the result to
    /// the manager page. <see cref="UpdateChecker.CheckAsync"/> returns its
    /// failures, and the extra guard keeps the native loop safe anyway.
    /// </summary>
    private static async Task CheckForUpdatesAsync()
    {
        var checker = _updates;
        if (checker is null)
            return;

        _lastDownload = null;

        UpdateCheckResult result;
        try
        {
            result = await checker.CheckAsync();
        }
        catch (Exception)
        {
            result = new UpdateCheckResult(
                checker.CurrentVersion,
                LatestVersion: null,
                IsNewer: false,
                Failed: true,
                "Controllo aggiornamenti non riuscito.");
        }

        _latestRelease = result is { IsNewer: true, Failed: false }
            ? result
            : null;

        _manager?.Invoke(() => Send(_manager, new
        {
            type = "update.status",
            currentVersion = result.CurrentVersion,
            latestVersion = result.LatestVersion,
            isNewer = result.IsNewer,
            failed = result.Failed,
            message = result.Message,
            releaseUrl = result.ReleaseUrl?.AbsoluteUri,
            releaseName = result.ReleaseName,
            releaseNotes = result.ReleaseNotes,
            downloadAssetName = result.DownloadAsset?.Name,
            signatureAvailable = result.SignatureAvailable
        }));
    }

    /// <summary>
    /// Downloads the archive selected by the last check, verifies its signed
    /// checksum manifest when published and opens the download folder. Nothing
    /// is executed: installation stays manual.
    /// </summary>
    private static async Task DownloadUpdateAsync(bool confirmed)
    {
        var downloader = _downloads;
        var release = _latestRelease;
        _lastDownload = null;
        if (downloader is null || release is null)
        {
            _manager?.Invoke(() => Send(_manager, new
            {
                type = "update.download.status",
                succeeded = false,
                message = "Controlla prima la disponibilità di un aggiornamento.",
                requiresConfirmation = false
            }));
            return;
        }

        UpdateDownloadResult result;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
                _manager?.Invoke(() => Send(_manager, new
                {
                    type = "update.download.progress",
                    received = value.Received,
                    total = value.Total,
                    percent = value.Percent
                })));
            result = await downloader.DownloadAsync(release, confirmed, progress);
        }
        catch (Exception)
        {
            result = new UpdateDownloadResult(
                Succeeded: false, Message: "Download non riuscito.");
        }

        _lastDownload = result.Succeeded ? result : null;

        var folderWarning = result.Succeeded &&
            result.FilePath is not null &&
            !TryOpenFolder(result.FilePath);
        var message = folderWarning
            ? result.Message + " Cartella non aperta automaticamente."
            : result.Message;
        var installMode = result.Succeeded && _installer is not null
            ? _installer.GetMode(result).ToString().ToLowerInvariant()
            : null;

        _manager?.Invoke(() => Send(_manager, new
        {
            type = "update.download.status",
            succeeded = result.Succeeded,
            message,
            path = result.FilePath,
            signatureVerified = result.SignatureVerified,
            requiresConfirmation = result.RequiresConfirmation,
            installMode
        }));
    }

    /// <summary>
    /// Installs the last successful download. The installer re-checks the
    /// signed manifest flag, so an unsigned archive is rejected even if the UI
    /// sends the message. On macOS the app closes after the detached helper
    /// starts; the window closing handler stops the tunnels.
    /// </summary>
    private static async Task InstallUpdateAsync()
    {
        var installer = _installer;
        var download = _lastDownload;
        if (installer is null || download is null)
        {
            _manager?.Invoke(() => Send(_manager, new
            {
                type = "update.install.status",
                status = "rejected",
                message = "Scarica e verifica prima un aggiornamento."
            }));
            return;
        }

        if (!_installGuard.TryEnter())
            return;

        UpdateInstallResult result;
        try
        {
            result = await Task.Run(() => installer.Install(download));
        }
        catch (Exception)
        {
            result = new UpdateInstallResult(
                UpdateInstallStatus.Rejected,
                "Installazione non riuscita.");
        }

        if (!result.Launched)
            _installGuard.Release();

        _manager?.Invoke(() =>
        {
            Send(_manager, new
            {
                type = "update.install.status",
                status = result.Status.ToString().ToLowerInvariant(),
                message = result.Message
            });

            if (result.Launched)
                _manager?.Close();
        });
    }

    private static bool TryOpenFolder(string filePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder))
                return false;

            Process.Start(new ProcessStartInfo(folder)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens an https link in the default browser. A plain http URL is allowed
    /// only when it is the browser URL of an active local HTTP tunnel; the
    /// Keycloak verification page keeps its own allowlist check in
    /// <see cref="OpenVerification"/>.
    /// </summary>
    private static void OpenExternal(PhotinoWindow window, JsonElement root)
    {
        var value = root.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp &&
               _tunnels?.IsTunnelOpenUrl(uri.AbsoluteUri) == true)))
        {
            Send(window, new
            {
                type = "external.error",
                message = "Link non valido: serve un indirizzo https o un tunnel HTTP attivo."
            });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            Send(window, new
            {
                type = "external.error",
                message = "Apertura del browser non riuscita."
            });
        }
    }

    private static void OpenInstance(
        PhotinoWindow parent,
        Instance instance,
        List<PhotinoWindow> children)
    {
        var child = new PhotinoWindow(parent)
            .SetTitle($"InfraOrgIT — {instance.Name}")
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 860)
            .SetUseOsDefaultLocation(false)
            .Center()
            // The documented local-instance escape hatch must also cover the
            // instance webview, not only the tunnel HttpClient.
            .SetIgnoreCertificateErrorsEnabled(IgnoreCertificateErrors())
            .RegisterWindowClosingHandler((sender, _) =>
            {
                children.Remove((PhotinoWindow)sender);
                return false;
            });

        // WKWebView disables the Fullscreen API unless the preference is set.
        if (OperatingSystem.IsMacOS())
            child.SetBrowserControlInitParameters(
                "{\"elementFullscreenEnabled\":true}");

        // WebView2 keeps its data store in %LocalAppData%\Photino by default;
        // give every instance its own directory so same-host sessions do not
        // overwrite each other.
        if (OperatingSystem.IsWindows())
            child.SetTemporaryFilesPath(InstanceDataPath(instance.Url));

        child.Load(instance.Url);

        children.Add(child);
        child.WaitForClose();
    }

    private static bool IgnoreCertificateErrors() =>
        string.Equals(
            Environment.GetEnvironmentVariable("INFRAORGIT_INSECURE_TLS"),
            "1",
            StringComparison.Ordinal);

    private static string InstanceDataPath(string url)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfraOrgIT",
            "webview",
            hash);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<Instance> SafeLoad(InstanceStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Starts the saved tunnels once, after the manager page has loaded and
    /// sent its first <c>list</c>. The restore is best effort and never blocks
    /// the window; failures stay in the tunnel log.
    /// </summary>
    private static void StartSavedTunnelRestore(InstanceStore store)
    {
        var manager = _tunnels;
        if (manager is null || Interlocked.Exchange(ref _restoreStarted, 1) == 1)
            return;

        var instances = SafeLoad(store);
        _ = Task.Run(async () =>
        {
            try
            {
                await manager.RestoreSavedTunnelsAsync(instances);
            }
            catch (Exception)
            {
                // A failed restore must never keep the manager from opening.
            }
        });
    }

    private static void Send(PhotinoWindow? window, object payload)
    {
        if (window is null)
            return;
        window.SendWebMessage(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
