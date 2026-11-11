using System.Buffers;
using System.Security.Cryptography;

namespace InfraOrgIt.Desktop.Update;

/// <summary>
/// Downloads the release archive selected by <see cref="UpdateChecker"/> and
/// verifies the SHA-256 file published next to it. Files are only saved, never
/// executed: the manager opens the containing folder and the user installs the
/// update manually. Errors come back as a result.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string? downloadDirectory = null)
{
    /// <summary>
    /// Only the initial URL is checked; GitHub redirects the download to its
    /// object storage, which <see cref="HttpClient"/> follows internally.
    /// </summary>
    internal const string AllowedDownloadHost = "github.com";

    private const int BufferSize = 81920;

    /// <summary>
    /// Downloads go to <c>~/Downloads/InfraOrgIT Desktop</c> so the user can
    /// find them; <see cref="Path.GetTempPath"/> is the fallback when the home
    /// folder is unknown.
    /// </summary>
    public string DownloadDirectory { get; } =
        ResolveDirectory(downloadDirectory);

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateCheckResult release,
        bool allowUnverified = false,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.DownloadAsset is not { } asset)
        {
            return Failure(
                "Nessun pacchetto di aggiornamento per questo sistema.");
        }

        if (!IsAllowedUri(asset.Url))
            return Failure("URL del pacchetto non attendibile.");

        if (release.ChecksumAsset is { } checksum && !IsAllowedUri(checksum.Url))
            return Failure("URL del checksum non attendibile.");

        if (release.ChecksumAsset is null && !allowUnverified)
        {
            return new UpdateDownloadResult(
                Succeeded: false,
                Message:
                    "Checksum SHA-256 non disponibile per questo pacchetto. " +
                    "Puoi scaricarlo comunque, ma l'integrità non sarà verificata.",
                RequiresConfirmation: true);
        }

        var fileName = SafeFileName(asset.Name);
        if (fileName is null)
            return Failure("Nome del pacchetto non valido.");

        string? partial = null;
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            var destination = Path.Combine(DownloadDirectory, fileName);
            partial = destination + ".part";

            var error = await DownloadToFileAsync(
                asset.Url, partial, asset.Size, progress, cancellationToken);
            if (error is not null)
                return Failure(error);

            var verified = false;
            if (release.ChecksumAsset is { } checksumAsset)
            {
                var expected = await FetchChecksumAsync(
                    checksumAsset.Url, cancellationToken);
                if (expected is null)
                {
                    return Failure(
                        "Checksum SHA-256 non scaricato o non valido: " +
                        "download annullato.");
                }

                var actual = await ComputeSha256Async(partial, cancellationToken);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        "Checksum SHA-256 non corrispondente: file eliminato.");
                }

                verified = true;
            }

            File.Move(partial, destination, overwrite: true);
            partial = null;
            var length = new FileInfo(destination).Length;
            progress?.Report(new UpdateDownloadProgress(length, length));

            return new UpdateDownloadResult(
                Succeeded: true,
                Message: verified
                    ? "Download completato e verificato (SHA-256)."
                    : "Download completato senza verifica: " +
                      "checksum SHA-256 non disponibile.",
                FilePath: destination,
                ChecksumVerified: verified);
        }
        catch (OperationCanceledException)
        {
            return Failure("Download annullato o scaduto.");
        }
        catch (HttpRequestException)
        {
            return Failure(
                "Download non riuscito: impossibile contattare GitHub.");
        }
        catch (IOException)
        {
            return Failure(
                "Salvataggio non riuscito nella cartella dei download.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("Cartella dei download non accessibile.");
        }
        catch (ArgumentException)
        {
            return Failure("Percorso del download non valido.");
        }
        finally
        {
            if (partial is not null)
                TryDelete(partial);
        }
    }

    internal static string ResolveDirectory(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return Path.GetFullPath(value);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "InfraOrgIT Desktop", "downloads")
            : Path.Combine(home, "Downloads", "InfraOrgIT Desktop");
    }

    internal static bool IsAllowedUri(Uri? uri) =>
        uri is not null &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, AllowedDownloadHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the first token of a <c>sha256sum</c> file:
    /// <c>&lt;64 hex chars&gt;  &lt;file name&gt;</c>.
    /// </summary>
    internal static string? ParseChecksum(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var token = text.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return token is { Length: 64 } && token.All(char.IsAsciiHexDigit)
            ? token
            : null;
    }

    private async Task<string?> DownloadToFileAsync(
        Uri uri,
        string path,
        long expectedSize,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return $"Download non riuscito: GitHub ha risposto " +
                   $"{(int)response.StatusCode}.";
        }

        var contentLength = response.Content.Headers.ContentLength;
        long? total = contentLength is > 0
            ? contentLength
            : expectedSize > 0
                ? expectedSize
                : null;

        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var target = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long received = 0;
            var lastPercent = -2;
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                    break;

                await target.WriteAsync(
                    buffer.AsMemory(0, read), cancellationToken);
                received += read;

                var percent = total is > 0
                    ? (int)Math.Min(100, received * 100 / total.Value)
                    : -1;
                if (percent == lastPercent)
                    continue;

                lastPercent = percent;
                progress?.Report(new UpdateDownloadProgress(received, total));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return null;
    }

    private async Task<string?> FetchChecksumAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseChecksum(text);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string? SafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return value;
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
            // Best effort: the .part file stays out of the final path anyway.
        }
    }

    private static UpdateDownloadResult Failure(string message) =>
        new(Succeeded: false, Message: message);
}

public sealed record UpdateDownloadResult(
    bool Succeeded,
    string Message,
    string? FilePath = null,
    bool ChecksumVerified = false,
    bool RequiresConfirmation = false);

public sealed record UpdateDownloadProgress(long Received, long? Total)
{
    /// <summary>Null when the server did not send a content length.</summary>
    public int? Percent => Total is > 0
        ? (int)Math.Min(100, Received * 100 / Total.Value)
        : null;
}
