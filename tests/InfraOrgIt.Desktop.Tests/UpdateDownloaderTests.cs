using System.Net;
using System.Security.Cryptography;
using System.Text;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Tests;

public sealed class UpdateDownloaderTests
{
    private const string AssetName = "InfraOrgIT-Desktop-0.2.0-osx-arm64.zip";
    private const string ArchiveUrl =
        "https://github.com/SenseiKiritof/InfraOrgITDesktop/releases/download/v0.2.0/" +
        AssetName;
    private static readonly byte[] Payload =
        Encoding.UTF8.GetBytes("fake desktop archive");
    private static readonly string Checksum =
        Convert.ToHexString(SHA256.HashData(Payload));

    [Fact]
    public async Task Download_verifies_the_published_sha256_and_saves_the_archive()
    {
        using var directory = new TempDirectory();
        var progress = new RecordingProgress();
        var handler = new FakeHandler(request =>
            IsChecksum(request)
                ? Text($"{Checksum}  {AssetName}")
                : Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(
            Release(Checksum), progress: progress);

        Assert.True(result.Succeeded);
        Assert.True(result.ChecksumVerified);
        Assert.Equal(Path.Combine(directory.Root, AssetName), result.FilePath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith(".sha256", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(Payload.Length, progress.Values[^1].Received);
        Assert.Equal(100, progress.Values[^1].Percent);
    }

    [Fact]
    public async Task Download_rejects_a_checksum_mismatch_and_deletes_the_file()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(request =>
            IsChecksum(request)
                ? Text($"{new string('0', 64)}  {AssetName}")
                : Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(new string('0', 64)));

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-256", result.Message);
        Assert.Contains("non corrispondente", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_requires_confirmation_when_the_checksum_is_missing()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(checksum: null));

        Assert.True(result.RequiresConfirmation);
        Assert.False(result.Succeeded);
        Assert.Contains("non disponibile", result.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_continues_without_checksum_after_confirmation()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(
            Release(checksum: null), allowUnverified: true);

        Assert.True(result.Succeeded);
        Assert.False(result.ChecksumVerified);
        Assert.Contains("senza verifica", result.Message);
        Assert.Single(handler.Requests);
        Assert.True(File.Exists(Path.Combine(directory.Root, AssetName)));
    }

    [Fact]
    public async Task Download_rejects_an_archive_from_an_unexpected_host()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(
            Release(Checksum, archiveUrl: "https://evil.example/archive.zip"));

        Assert.False(result.Succeeded);
        Assert.Contains("non attendibile", result.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_rejects_a_checksum_from_an_unexpected_host()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(_ => Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(
            Release(Checksum, checksumUrl: "https://evil.example/archive.zip.sha256"));

        Assert.False(result.Succeeded);
        Assert.Contains("checksum", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_reports_a_transport_failure_without_throwing()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(
            _ => throw new HttpRequestException("offline"));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(Checksum));

        Assert.False(result.Succeeded);
        Assert.Contains("GitHub", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_reports_a_checksum_download_failure_and_cleans_up()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(request =>
            IsChecksum(request)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(Checksum));

        Assert.False(result.Succeeded);
        Assert.Contains("Checksum", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_reports_an_invalid_checksum_file()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(request =>
            IsChecksum(request)
                ? Text("not-a-checksum")
                : Bytes(Payload));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(Checksum));

        Assert.False(result.Succeeded);
        Assert.Contains("non valido", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task Download_reports_an_archive_http_error()
    {
        using var directory = new TempDirectory();
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var downloader = new UpdateDownloader(new HttpClient(handler), directory.Root);

        var result = await downloader.DownloadAsync(Release(Checksum));

        Assert.False(result.Succeeded);
        Assert.Contains("502", result.Message);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    private static UpdateCheckResult Release(
        string? checksum,
        string? archiveUrl = null,
        string? checksumUrl = null)
    {
        var archive = new UpdateAsset(
            AssetName, new Uri(archiveUrl ?? ArchiveUrl), Payload.Length);
        var checksumAsset = checksum is null
            ? null
            : new UpdateAsset(
                AssetName + ".sha256",
                new Uri(checksumUrl ?? ArchiveUrl + ".sha256"),
                65);

        return new UpdateCheckResult(
            "0.1.0",
            "0.2.0",
            IsNewer: true,
            Failed: false,
            "Nuova versione disponibile.",
            DownloadAsset: archive,
            ChecksumAsset: checksumAsset);
    }

    private static bool IsChecksum(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith(
            ".sha256", StringComparison.Ordinal);

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
