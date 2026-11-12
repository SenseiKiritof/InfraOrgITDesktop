using System.Buffers;
using System.Text;
using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Downloads;

/// <summary>
/// Queues downloads requested by an instance webview. Files are streamed on a
/// background task to a temporary <c>.part</c> file and only then renamed, so
/// the instance page never blocks the native message loop. HTTP(S) is the only
/// accepted scheme, no downloaded file is ever executed, and status messages
/// carry file names but never the source URL (signed links may contain
/// tickets).
/// </summary>
public sealed class DownloadManager : IDisposable
{
    /// <summary>
    /// True only when the macOS app loaded the patched Photino.Native with
    /// WKDownload support. It stays false on Windows/Linux and on macOS when
    /// the optional native library is absent.
    /// </summary>
    public static bool NativeDownloadAvailable { get; private set; }

    internal static void SetNativeDownloadAvailable(bool available) =>
        NativeDownloadAvailable = available;

    /// <summary>Small blobs are handed over as base64; larger files stream.</summary>
    public const int MaxBlobBytes = 8 * 1024 * 1024;

    public const int DefaultMaxConcurrentDownloads = 2;

    internal const int MaxFileNameLength = 120;

    private const int BufferSize = 81920;

    private static readonly char[] InvalidFileNameChars = Path
        .GetInvalidFileNameChars()
        .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
        .Distinct()
        .ToArray();

    private static readonly HashSet<string> ReservedNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public DownloadManager(
        HttpClient http,
        string? downloadDirectory = null,
        int maxConcurrentDownloads = DefaultMaxConcurrentDownloads)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (maxConcurrentDownloads < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentDownloads));
        }

        _http = http;
        DownloadDirectory = UpdateDownloader.ResolveDirectory(downloadDirectory);
        _slots = new SemaphoreSlim(
            maxConcurrentDownloads, maxConcurrentDownloads);
    }

    public string DownloadDirectory { get; }

    /// <summary>Cancels queued and running downloads when the app exits.</summary>
    public void CancelAll()
    {
        if (!_disposed)
            _shutdown.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Starts a queued download and returns its result. The caller never waits
    /// on this from the message loop; the task streams in the background and
    /// reports through <paramref name="status"/>.
    /// </summary>
    public async Task<DownloadResult> EnqueueAsync(
        DownloadRequest request,
        Func<string, string, Task<string?>> choosePath,
        Action<DownloadStatus>? status = null,
        Action<string, string>? notify = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(choosePath);

        var fileName = SanitizeFileName(
            request.FileName ?? SuggestFileName(request.Url));
        if (!TryGetAllowedUri(request.Url, out var uri, out var error))
            return ReportFailure(fileName, error, request.Id, status);

        Report(status, new DownloadStatus(
            DownloadStates.Queued, fileName, Message: "Download in coda.",
            Id: request.Id));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token, cancellationToken);
        var token = linked.Token;
        var acquired = false;
        string? partial = null;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            Directory.CreateDirectory(DownloadDirectory);
            var destination = await choosePath(
                "Salva file", Path.Combine(DownloadDirectory, fileName))
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(destination))
            {
                return Report(
                    DownloadStates.Cancelled,
                    "Download annullato.",
                    fileName,
                    path: null,
                    request.Id,
                    status);
            }

            destination = Path.GetFullPath(destination);
            var actualName = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(actualName))
                return ReportFailure(
                    fileName, "Percorso del download non valido.",
                    request.Id, status);

            Report(status, new DownloadStatus(
                DownloadStates.Started, actualName,
                Message: "Download avviato.", Id: request.Id));

            partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";

            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);
            using var response = await _http.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            // A redirect may leave https; apply the same scheme policy to the
            // final URL instead of trusting the first hop only.
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && !IsAllowedUri(finalUri))
            {
                return ReportFailure(
                    actualName,
                    "Download non riuscito: indirizzo finale non consentito.",
                    request.Id,
                    status,
                    notify);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ReportFailure(
                    actualName,
                    $"Download non riuscito: il server ha risposto " +
                    $"{(int)response.StatusCode}.",
                    request.Id,
                    status,
                    notify);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(
                token).ConfigureAwait(false))
            await using (var target = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(
                            buffer.AsMemory(0, BufferSize), token)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;

                        await target.WriteAsync(
                            buffer.AsMemory(0, read), token)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await target.FlushAsync(token).ConfigureAwait(false);
            }

            File.Move(partial, destination, overwrite: true);
            partial = null;

            var completed = Report(
                DownloadStates.Completed,
                "Download completato.",
                actualName,
                destination,
                request.Id,
                status);
            TryNotify(notify, "Download completato", actualName);
            return completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Report(
                DownloadStates.Cancelled,
                "Download annullato.",
                fileName,
                path: null,
                request.Id,
                status);
        }
        catch (OperationCanceledException)
        {
            return ReportFailure(
                fileName, "Download non riuscito: tempo scaduto.",
                request.Id, status, notify);
        }
        catch (HttpRequestException)
        {
            return ReportFailure(
                fileName, "Download non riuscito: server non raggiungibile.",
                request.Id, status, notify);
        }
        catch (IOException)
        {
            return ReportFailure(
                fileName, "Salvataggio non riuscito nella cartella scelta.",
                request.Id, status, notify);
        }
        catch (UnauthorizedAccessException)
        {
            return ReportFailure(
                fileName, "Cartella dei download non accessibile.",
                request.Id, status, notify);
        }
        catch (ArgumentException)
        {
            return ReportFailure(
                fileName, "Percorso del download non valido.",
                request.Id, status);
        }
        catch (ApplicationException)
        {
            return ReportFailure(
                fileName, "Finestra non disponibile per il salvataggio.",
                request.Id, status);
        }
        finally
        {
            if (partial is not null)
                TryDelete(partial);
            if (acquired)
                _slots.Release();
        }
    }

    /// <summary>
    /// Saves an in-memory base64 payload (clipboard/export style downloads).
    /// The decoded size is capped before decoding and again afterwards; the
    /// write goes through the same temporary-then-rename path.
    /// </summary>
    public async Task<DownloadResult> EnqueueBlobAsync(
        string? fileName,
        string? base64,
        Func<string, string, Task<string?>> choosePath,
        Action<DownloadStatus>? status = null,
        string? id = null,
        Action<string, string>? notify = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choosePath);

        var safeName = SanitizeFileName(fileName);
        if (!IsBlobWithinCap(base64, out var capError))
            return ReportFailure(safeName, capError, id, status, notify);

        Report(status, new DownloadStatus(
            DownloadStates.Queued, safeName, Message: "Download in coda.",
            Id: id));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token, cancellationToken);
        var token = linked.Token;
        var acquired = false;
        string? partial = null;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            Directory.CreateDirectory(DownloadDirectory);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64!);
            }
            catch (FormatException)
            {
                return ReportFailure(
                    safeName, "Contenuto del file non valido.", id, status,
                    notify);
            }

            if (bytes.LongLength > MaxBlobBytes)
            {
                return ReportFailure(
                    safeName,
                    $"Il file supera il limite di {MaxBlobBytes / (1024 * 1024)} MB.",
                    id, status, notify);
            }

            var destination = await choosePath(
                "Salva file", Path.Combine(DownloadDirectory, safeName))
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(destination))
            {
                return Report(
                    DownloadStates.Cancelled,
                    "Download annullato.",
                    safeName,
                    path: null,
                    id,
                    status);
            }

            destination = Path.GetFullPath(destination);
            var actualName = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(actualName))
                return ReportFailure(
                    safeName, "Percorso del download non valido.", id, status,
                    notify);

            Report(status, new DownloadStatus(
                DownloadStates.Started, actualName,
                Message: "Download avviato.", Id: id));

            partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            await File.WriteAllBytesAsync(partial, bytes, token)
                .ConfigureAwait(false);
            File.Move(partial, destination, overwrite: true);
            partial = null;

            var completed = Report(
                DownloadStates.Completed,
                "Download completato.",
                actualName,
                destination,
                id,
                status);
            TryNotify(notify, "Download completato", actualName);
            return completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Report(
                DownloadStates.Cancelled,
                "Download annullato.",
                safeName,
                path: null,
                id,
                status);
        }
        catch (IOException)
        {
            return ReportFailure(
                safeName, "Salvataggio non riuscito nella cartella scelta.",
                id, status, notify);
        }
        catch (UnauthorizedAccessException)
        {
            return ReportFailure(
                safeName, "Cartella dei download non accessibile.",
                id, status, notify);
        }
        catch (ArgumentException)
        {
            return ReportFailure(
                safeName, "Percorso del download non valido.", id, status,
                notify);
        }
        catch (ApplicationException)
        {
            return ReportFailure(
                safeName, "Finestra non disponibile per il salvataggio.",
                id, status, notify);
        }
        finally
        {
            if (partial is not null)
                TryDelete(partial);
            if (acquired)
                _slots.Release();
        }
    }

    internal static bool TryGetAllowedUri(
        string? value,
        out Uri uri,
        out string error,
        bool? allowInsecureHttp = null)
    {
        uri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp &&
             parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Il download richiede un indirizzo http o https.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp &&
            !parsed.IsLoopback &&
            !(allowInsecureHttp ?? AllowInsecureHttpFromEnvironment()))
        {
            error = "Il download richiede https; http è consentito solo per " +
                    "localhost o con l'override di sviluppo.";
            return false;
        }

        uri = parsed;
        error = "";
        return true;
    }

    internal static bool IsAllowedUri(Uri? uri, bool? allowInsecureHttp = null) =>
        uri is not null &&
        TryGetAllowedUri(uri.AbsoluteUri, out _, out _, allowInsecureHttp);

    internal static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "download";

        var normalized = value.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(
                char.IsControl(character) ||
                Array.IndexOf(InvalidFileNameChars, character) >= 0
                    ? '_'
                    : character);
        }

        name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name is "" or "." or "..")
            return "download";

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem))
            name = "_" + name;

        if (name.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length is 0 or > 20)
                extension = "";
            name = name[..(MaxFileNameLength - extension.Length)] + extension;
        }

        name = name.TrimEnd('.', ' ');
        return name.Length == 0 ? "download" : name;
    }

    internal static string SuggestFileName(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            return "download";

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return SanitizeFileName(path[(path.LastIndexOf('/') + 1)..]);
    }

    internal static bool IsBlobWithinCap(string? base64, out string error)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            error = "Contenuto del file mancante.";
            return false;
        }

        var maxEncoded = ((MaxBlobBytes + 2L) / 3) * 4;
        if (base64.Length > maxEncoded)
        {
            error = $"Il file supera il limite di " +
                    $"{MaxBlobBytes / (1024 * 1024)} MB.";
            return false;
        }

        error = "";
        return true;
    }

    private DownloadResult ReportFailure(
        string fileName,
        string message,
        string? id,
        Action<DownloadStatus>? status,
        Action<string, string>? notify = null)
    {
        var result = Report(
            DownloadStates.Failed, message, fileName, path: null, id, status);
        TryNotify(notify, "Download non riuscito", fileName);
        return result;
    }

    private static DownloadResult Report(
        Action<DownloadStatus>? status,
        DownloadStatus value)
    {
        try
        {
            status?.Invoke(value);
        }
        catch (Exception)
        {
            // A closed/blocked instance window must not lose the download.
        }

        return new DownloadResult(
            value.State == DownloadStates.Completed,
            value.Message ?? "",
            value.Path,
            value.State);
    }

    private static DownloadResult Report(
        string state,
        string message,
        string fileName,
        string? path,
        string? id,
        Action<DownloadStatus>? status)
    {
        var value = new DownloadStatus(state, fileName, path, message, id);
        return Report(status, value);
    }

    private static void TryNotify(
        Action<string, string>? notify,
        string title,
        string message)
    {
        try
        {
            notify?.Invoke(title, message);
        }
        catch (Exception)
        {
            // Notifications are optional and platform dependent.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the .part file never becomes the final file.
        }
    }

    private static bool AllowInsecureHttpFromEnvironment() =>
        Environment.GetEnvironmentVariable(Instance.AllowInsecureHttpVariable) == "1";
}

public sealed record DownloadRequest(
    string Url,
    string? FileName = null,
    string? Id = null);

public static class DownloadStates
{
    public const string Queued = "queued";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record DownloadStatus(
    string State,
    string FileName,
    string? Path = null,
    string? Message = null,
    string? Id = null);

public sealed record DownloadResult(
    bool Succeeded,
    string Message,
    string? FilePath = null,
    string State = DownloadStates.Failed);
