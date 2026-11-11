using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace InfraOrgIt.Desktop.Update;

/// <summary>
/// Downloads the release archive selected by <see cref="UpdateChecker"/> and
/// verifies it against the signed checksum manifest. The result carries the
/// saved path and <see cref="UpdateDownloadResult.SignatureVerified"/>; only
/// that flag makes the file eligible for <see cref="UpdateInstaller"/>. The
/// download itself never executes the archive. Errors come back as a result.
/// </summary>
/// <remarks>
/// A SHA-256 file published next to an unsigned release is not trusted: the
/// hash is read only from a manifest whose RSA/SHA-256 signature matches the
/// public key embedded in the app. A download may still be offered without a
/// valid signature, but installation is then manual only.
/// </remarks>
public sealed class UpdateDownloader(
    HttpClient http,
    string? downloadDirectory = null,
    string? publicKeyPem = null)
{
    /// <summary>
    /// Only the initial URL is checked; GitHub redirects the download to its
    /// object storage, which <see cref="HttpClient"/> follows internally.
    /// </summary>
    internal const string AllowedDownloadHost = "github.com";

    internal const string SignatureBlockedMessage =
        "Installazione bloccata: firma aggiornamento non valida/mancante.";

    private const int BufferSize = 81920;

    private readonly string? _publicKeyPem =
        publicKeyPem ?? UpdateSignature.ProductionPublicKeyPem;

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

        var manifest = release.ManifestAsset;
        var signatureAsset = release.SignatureAsset;
        if (manifest is not null && !IsAllowedUri(manifest.Url))
            return Failure("URL del manifest non attendibile.");

        if (signatureAsset is not null && !IsAllowedUri(signatureAsset.Url))
            return Failure("URL della firma non attendibile.");

        var signed = manifest is not null && signatureAsset is not null;
        if (!signed && !allowUnverified)
        {
            return new UpdateDownloadResult(
                Succeeded: false,
                Message:
                    SignatureBlockedMessage +
                    " Il pacchetto non è firmato: puoi scaricarlo comunque, " +
                    "ma l'installazione automatica resterà bloccata.",
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
            var warning = (string?)null;
            if (signed)
            {
                var manifestBytes = await TryFetchAsync(
                    manifest!.Url, cancellationToken);
                var signatureBytes = await TryFetchAsync(
                    signatureAsset!.Url, cancellationToken);
                if (manifestBytes is null || signatureBytes is null ||
                    !UpdateSignature.Verify(
                        manifestBytes, signatureBytes, _publicKeyPem))
                {
                    warning = SignatureBlockedMessage +
                        " Il manifest o la firma non sono validi: " +
                        "il file resta disponibile per un'installazione manuale.";
                }
                else
                {
                    var expected = UpdateManifest
                        .Parse(Encoding.UTF8.GetString(manifestBytes))
                        ?.FindHash(asset.Name);
                    if (expected is null)
                    {
                        warning = SignatureBlockedMessage +
                            " Il pacchetto non è elencato nel manifest firmato: " +
                            "il file resta disponibile per un'installazione " +
                            "manuale.";
                    }
                    else
                    {
                        var actual = await ComputeSha256Async(
                            partial, cancellationToken);
                        if (!string.Equals(
                                actual,
                                expected,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return Failure(
                                "Checksum SHA-256 non corrispondente al " +
                                "manifest firmato: file eliminato.");
                        }

                        verified = true;
                    }
                }
            }

            File.Move(partial, destination, overwrite: true);
            partial = null;
            var length = new FileInfo(destination).Length;
            progress?.Report(new UpdateDownloadProgress(length, length));

            return new UpdateDownloadResult(
                Succeeded: true,
                Message: verified
                    ? "Download completato e verificato (firma del manifest " +
                      "e SHA-256)."
                    : warning ??
                      SignatureBlockedMessage +
                      " Il file resta disponibile per un'installazione manuale.",
                FilePath: destination,
                SignatureVerified: verified);
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

    /// <summary>
    /// Best effort fetch for the manifest and its signature: a missing file
    /// turns into the unsigned path (download kept, install blocked) instead of
    /// discarding an archive the user already downloaded.
    /// </summary>
    private async Task<byte[]?> TryFetchAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
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

/// <summary>
/// Result of a download. <see cref="SignatureVerified"/> is true only after
/// the manifest signature matched the embedded public key and the archive hash
/// matched the signed manifest; <see cref="FilePath"/> is the final archive
/// path, never the <c>.part</c> file. Both are required by
/// <see cref="UpdateInstaller"/>.
/// </summary>
public sealed record UpdateDownloadResult(
    bool Succeeded,
    string Message,
    string? FilePath = null,
    bool SignatureVerified = false,
    bool RequiresConfirmation = false);

public sealed record UpdateDownloadProgress(long Received, long? Total)
{
    /// <summary>Null when the server did not send a content length.</summary>
    public int? Percent => Total is > 0
        ? (int)Math.Min(100, Received * 100 / Total.Value)
        : null;
}
