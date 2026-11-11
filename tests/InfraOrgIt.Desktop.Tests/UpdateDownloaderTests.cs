using System.Net;
using System.Security.Cryptography;
using System.Text;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Tests;

public sealed class UpdateDownloaderTests
{
    private const string AssetName = "InfraOrgIT-Desktop-0.2.0-osx-arm64.zip";
    private const string ManifestName = "InfraOrgIT-Desktop-0.2.0-sha256.txt";
    private const string ArchiveUrl =
        "https://github.com/SenseiKiritof/InfraOrgITDesktop/releases/download/v0.2.0/" +
        AssetName;
    private const string ManifestUrl =
        "https://github.com/SenseiKiritof/InfraOrgITDesktop/releases/download/v0.2.0/" +
        ManifestName;
    private static readonly byte[] Payload =
        Encoding.UTF8.GetBytes("fake desktop archive");
    private static readonly string Checksum =
        Convert.ToHexString(SHA256.HashData(Payload));
    private static readonly string ManifestText = $"{Checksum}  {AssetName}\n";

    /// <summary>
    /// Ephemeral test key: the production private key never exists in the
    /// repository or in CI, and these tests sign their own manifests.
    /// </summary>
    private static readonly RSA TestKey = RSA.Create(3072);
    private static readonly string TestPublicKey =
        TestKey.ExportSubjectPublicKeyInfoPem();

    [Fact]
    public async Task Download_accepts_a_signed_manifest_with_a_matching_hash()
    {
        using var directory = new TempDirectory();
        var progress = new RecordingProgress();
        var signature = Sign(ManifestText);
        var handler = Handler(signature);
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(signature: signature), progress: progress);

        Assert.True(result.Succeeded);
        Assert.True(result.SignatureVerified);
        Assert.Equal(Path.Combine(directory.Root, AssetName), result.FilePath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith(ManifestName, handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.EndsWith(".sig", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(Payload.Length, progress.Values[^1].Received);
        Assert.Equal(100, progress.Values[^1].Percent);
    }

    [Fact]
    public async Task Download_blocks_installation_when_the_signature_is_invalid()
    {
        using var directory = new TempDirectory();
        var signature = Sign(ManifestText);
        signature[^1] ^= 0xFF;
        var handler = Handler(signature);
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(Release(signature: signature));

        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.True(File.Exists(Path.Combine(directory.Root, AssetName)));
    }

    [Fact]
    public async Task Download_blocks_installation_when_the_signature_uses_another_key()
    {
        using var directory = new TempDirectory();
        var signature = Sign(ManifestText);
        var handler = Handler(signature);
        var downloader = new UpdateDownloader(
            new HttpClient(handler),
            directory.Root,
            UpdateSignature.ProductionPublicKeyPem);

        var result = await downloader.DownloadAsync(Release(signature: signature));

        Assert.NotNull(UpdateSignature.ProductionPublicKeyPem);
        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
    }

    [Fact]
    public async Task Download_requires_confirmation_when_the_signature_is_missing()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(includeSignature: false));

        Assert.True(result.RequiresConfirmation);
        Assert.False(result.Succeeded);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_keeps_an_unsigned_archive_but_blocks_installation()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(includeSignature: false), allowUnverified: true);

        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.Single(handler.Requests);
        Assert.True(File.Exists(Path.Combine(directory.Root, AssetName)));
    }

    [Fact]
    public async Task Download_does_not_verify_without_a_signed_manifest()
    {
        using var directory = new TempDirectory();
        var handler = Handler(Sign(ManifestText));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(includeManifest: false), allowUnverified: true);

        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Download_blocks_installation_when_the_manifest_does_not_list_the_archive()
    {
        using var directory = new TempDirectory();
        var manifest = $"{Checksum}  InfraOrgIT-Desktop-0.2.0-win-x64.zip\n";
        var signature = Sign(manifest);
        var handler = Handler(signature, manifest);
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(manifest: manifest, signature: signature));

        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.Contains("non è elencato", result.Message);
        Assert.True(File.Exists(Path.Combine(directory.Root, AssetName)));
    }

    [Fact]
    public async Task Download_rejects_a_checksum_mismatch_and_deletes_the_file()
    {
        using var directory = new TempDirectory();
        var manifest = $"{new string('0', 64)}  {AssetName}\n";
        var signature = Sign(manifest);
        var handler = Handler(signature, manifest);
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(manifest: manifest, signature: signature));

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-256", result.Message);
        Assert.Contains("non corrispondente", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_blocks_installation_when_the_manifest_cannot_be_fetched()
    {
        using var directory = new TempDirectory();
        var signature = Sign(ManifestText);
        var handler = new FakeHandler(request =>
            IsManifest(request)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : IsSignature(request)
                    ? Bytes(signature)
                    : Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(Release(signature: signature));

        Assert.True(result.Succeeded);
        Assert.False(result.SignatureVerified);
        Assert.Contains("firma aggiornamento non valida/mancante", result.Message);
        Assert.True(File.Exists(Path.Combine(directory.Root, AssetName)));
    }

    [Fact]
    public async Task Download_rejects_an_archive_from_an_unexpected_host()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(
                signature: Sign(ManifestText),
                archiveUrl: "https://evil.example/archive.zip"));

        Assert.False(result.Succeeded);
        Assert.Contains("non attendibile", result.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_rejects_a_manifest_from_an_unexpected_host()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(
                signature: Sign(ManifestText),
                manifestUrl: "https://evil.example/manifest.txt"));

        Assert.False(result.Succeeded);
        Assert.Contains("manifest", result.Message);
        Assert.Contains("non attendibile", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_rejects_a_signature_from_an_unexpected_host()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(
                signature: Sign(ManifestText),
                signatureUrl: "https://evil.example/manifest.txt.sig"));

        Assert.False(result.Succeeded);
        Assert.Contains("firma", result.Message);
        Assert.Contains("non attendibile", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_reports_a_transport_failure_without_throwing()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(
            _ => throw new HttpRequestException("offline"));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(signature: Sign(ManifestText)));

        Assert.False(result.Succeeded);
        Assert.Contains("GitHub", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_reports_an_archive_http_error()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var downloader = Downloader(directory, handler);

        var result = await downloader.DownloadAsync(
            Release(signature: Sign(ManifestText)));

        Assert.False(result.Succeeded);
        Assert.Contains("502", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void Production_public_key_is_embedded_and_parses()
    {
        var pem = UpdateSignature.ProductionPublicKeyPem;

        Assert.False(string.IsNullOrWhiteSpace(pem));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem!);
        Assert.Equal(3072, rsa.KeySize);
    }

    private static UpdateDownloader Downloader(
        TempDirectory directory, HttpMessageHandler handler) =>
        new(new HttpClient(handler), directory.Root, TestPublicKey);

    private static FakeHandler Handler(byte[] signature, string? manifest = null) =>
        new(request =>
            IsSignature(request)
                ? Bytes(signature)
                : IsManifest(request)
                    ? Text(manifest ?? ManifestText)
                    : Bytes(Payload));

    private static UpdateCheckResult Release(
        string? manifest = null,
        byte[]? signature = null,
        string? archiveUrl = null,
        string? manifestUrl = null,
        string? signatureUrl = null,
        bool includeManifest = true,
        bool includeSignature = true)
    {
        manifest ??= ManifestText;
        var archive = new UpdateAsset(
            AssetName, new Uri(archiveUrl ?? ArchiveUrl), Payload.Length);
        var manifestAsset = includeManifest && manifest is not null
            ? new UpdateAsset(
                ManifestName, new Uri(manifestUrl ?? ManifestUrl), 100)
            : null;
        var signatureAsset = manifestAsset is not null && includeSignature
            ? new UpdateAsset(
                ManifestName + ".sig",
                new Uri(signatureUrl ?? ManifestUrl + ".sig"),
                256)
            : null;

        return new UpdateCheckResult(
            "0.1.0",
            "0.2.0",
            IsNewer: true,
            Failed: false,
            "Nuova versione disponibile.",
            DownloadAsset: archive,
            ManifestAsset: manifestAsset,
            SignatureAsset: signatureAsset);
    }

    private static byte[] Sign(string manifest) =>
        TestKey.SignData(
            Encoding.UTF8.GetBytes(manifest),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

    private static bool IsManifest(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith(
            "-sha256.txt", StringComparison.Ordinal);

    private static bool IsSignature(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith(
            ".sig", StringComparison.Ordinal);

    private static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class FakeHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class RecordingProgress : IProgress<UpdateDownloadProgress>
    {
        public List<UpdateDownloadProgress> Values { get; } = [];

        public void Report(UpdateDownloadProgress value) => Values.Add(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "infraorgit-update-tests",
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
