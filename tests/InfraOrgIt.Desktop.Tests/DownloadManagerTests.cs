using System.Collections.Concurrent;
using System.Net;
using System.Text;
using InfraOrgIt.Desktop.Downloads;

namespace InfraOrgIt.Desktop.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infraorgit-downloads-" + Guid.NewGuid().ToString("N"));

    public DownloadManagerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup for the temporary test folder.
        }
    }

    [Fact]
    public async Task Download_streams_to_a_part_file_then_renames_it()
    {
        var payload = Encoding.UTF8.GetBytes("downloaded bytes");
        var handler = new FakeHandler(_ => Bytes(payload));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var statuses = new ConcurrentQueue<DownloadStatus>();
        var notifications = new ConcurrentQueue<(string Title, string Body)>();
        var destination = Path.Combine(_directory, "report.bin");

        var result = await manager.EnqueueAsync(
            new DownloadRequest(
                "https://example.com/download?ticket=not-a-log-line",
                "report.bin",
                "req-1"),
            (_, _) => Task.FromResult<string?>(destination),
            statuses.Enqueue,
            (title, body) => notifications.Enqueue((title, body)));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(destination, result.FilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            [DownloadStates.Queued, DownloadStates.Started, DownloadStates.Completed],
            statuses.Select(status => status.State));
        Assert.All(statuses, status => Assert.Equal("req-1", status.Id));
        Assert.Contains(
            notifications,
            item => item.Title == "Download completato" &&
                    item.Body == "report.bin");
        Assert.DoesNotContain(
            Directory.GetFiles(_directory),
            path => path.EndsWith(".part", StringComparison.Ordinal));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Slow_download_does_not_block_and_queues_above_the_limit()
    {
        var payload = Encoding.UTF8.GetBytes("slow payload");
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(new GatedStream(payload, gate.Task));
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(
            http, _directory, maxConcurrentDownloads: 1);
        var statuses = new ConcurrentQueue<DownloadStatus>();
        var picks = 0;

        Task<string?> Choose(string _, string __) =>
            Task.FromResult<string?>(Path.Combine(
                _directory, $"file{Interlocked.Increment(ref picks)}.bin"));

        // The background call must return while the response body is stalled.
        var enqueueReturned = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            var running = manager.EnqueueAsync(
                new DownloadRequest("https://example.com/one.bin", "one.bin"),
                Choose, statuses.Enqueue);
            enqueueReturned.TrySetResult(true);
            return await running;
        });

        var winner = await Task.WhenAny(
            enqueueReturned.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Same(enqueueReturned.Task, winner);
            Assert.False(first.IsCompleted);

            var second = manager.EnqueueAsync(
                new DownloadRequest("https://example.com/two.bin", "two.bin"),
                Choose, statuses.Enqueue);

            await Task.Delay(100);

            Assert.Single(handler.Requests);
            Assert.Contains(statuses, status =>
                status.FileName == "two.bin" &&
                status.State == DownloadStates.Queued);
            Assert.DoesNotContain(statuses, status =>
                status.FileName == "two.bin" &&
                status.State == DownloadStates.Started);

            gate.TrySetResult(true);

            var firstResult = await first;
            var secondResult = await second;
            Assert.True(firstResult.Succeeded, firstResult.Message);
            Assert.True(secondResult.Succeeded, secondResult.Message);
            Assert.Equal(2, handler.Requests.Count);
            Assert.True(File.Exists(Path.Combine(_directory, "file1.bin")));
            Assert.True(File.Exists(Path.Combine(_directory, "file2.bin")));
            Assert.DoesNotContain(
                Directory.GetFiles(_directory),
                path => path.EndsWith(".part", StringComparison.Ordinal));
        }
        finally
        {
            gate.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Download_rejects_a_non_http_url_before_the_dialog_or_network()
    {
        var handler = new FakeHandler(_ => Bytes([]));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var statuses = new ConcurrentQueue<DownloadStatus>();
        var pickerCalled = false;

        var result = await manager.EnqueueAsync(
            new DownloadRequest("file:///etc/passwd", "passwd"),
            (_, _) =>
            {
                pickerCalled = true;
                return Task.FromResult<string?>(
                    Path.Combine(_directory, "passwd"));
            },
            statuses.Enqueue);

        Assert.False(result.Succeeded);
        Assert.Contains("http", result.Message);
        Assert.False(pickerCalled);
        Assert.Empty(handler.Requests);
        Assert.Equal(
            DownloadStates.Failed, Assert.Single(statuses).State);
    }

    [Fact]
    public async Task Download_reports_an_http_failure_and_notifies()
    {
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var statuses = new ConcurrentQueue<DownloadStatus>();
        var notifications = new ConcurrentQueue<(string Title, string Body)>();

        var result = await manager.EnqueueAsync(
            new DownloadRequest("https://example.com/file.bin", "file.bin"),
            (_, _) => Task.FromResult<string?>(
                Path.Combine(_directory, "file.bin")),
            statuses.Enqueue,
            (title, body) => notifications.Enqueue((title, body)));

        Assert.False(result.Succeeded);
        Assert.Contains("502", result.Message);
        Assert.Equal(DownloadStates.Failed, statuses.Last().State);
        Assert.Contains(
            notifications,
            item => item.Title == "Download non riuscito" &&
                    item.Body == "file.bin");
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Download_rejects_a_transport_failure_without_leaving_a_partial_file()
    {
        var handler = new FakeHandler(
            _ => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);

        var result = await manager.EnqueueAsync(
            new DownloadRequest("https://example.com/file.bin", "file.bin"),
            (_, _) => Task.FromResult<string?>(
                Path.Combine(_directory, "file.bin")));

        Assert.False(result.Succeeded);
        Assert.Contains("raggiungibile", result.Message);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Cancel_all_stops_a_running_download_and_removes_the_part_file()
    {
        var payload = Encoding.UTF8.GetBytes("stalled");
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(new GatedStream(payload, gate.Task));
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var destination = Path.Combine(_directory, "stalled.bin");

        var download = manager.EnqueueAsync(
            new DownloadRequest("https://example.com/stalled.bin", "stalled.bin"),
            (_, _) => Task.FromResult<string?>(destination));
        await Task.Delay(100);
        manager.CancelAll();

        var result = await download;

        Assert.Equal(DownloadStates.Cancelled, result.State);
        Assert.Empty(Directory.GetFiles(_directory));
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task Blob_over_the_cap_is_rejected_without_decoding_or_saving()
    {
        var handler = new FakeHandler(_ => Bytes([]));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var statuses = new ConcurrentQueue<DownloadStatus>();
        var pickerCalled = false;
        var oversized = Convert.ToBase64String(
            new byte[DownloadManager.MaxBlobBytes + 1]);

        var result = await manager.EnqueueBlobAsync(
            "big.bin",
            oversized,
            (_, _) =>
            {
                pickerCalled = true;
                return Task.FromResult<string?>(
                    Path.Combine(_directory, "big.bin"));
            },
            statuses.Enqueue);

        Assert.False(result.Succeeded);
        Assert.Contains("limite", result.Message);
        Assert.False(pickerCalled);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(DownloadStates.Failed, statuses.Last().State);
        Assert.Contains("limite", statuses.Last().Message);
    }

    [Fact]
    public async Task Blob_is_written_atomically_within_the_cap()
    {
        var handler = new FakeHandler(_ => Bytes([]));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);
        var payload = Encoding.UTF8.GetBytes("small blob");
        var destination = Path.Combine(_directory, "note.bin");
        var notifications = new ConcurrentQueue<(string Title, string Body)>();

        var result = await manager.EnqueueBlobAsync(
            "note.bin",
            Convert.ToBase64String(payload),
            (_, _) => Task.FromResult<string?>(destination),
            notify: (title, body) => notifications.Enqueue((title, body)));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Contains(
            notifications,
            item => item.Title == "Download completato" &&
                    item.Body == "note.bin");
        Assert.DoesNotContain(
            Directory.GetFiles(_directory),
            path => path.EndsWith(".part", StringComparison.Ordinal));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Blob_rejects_invalid_base64()
    {
        var handler = new FakeHandler(_ => Bytes([]));
        using var http = new HttpClient(handler);
        var manager = new DownloadManager(http, _directory);

        var result = await manager.EnqueueBlobAsync(
            "bad.bin",
            "not base64!",
            (_, _) => Task.FromResult<string?>(
                Path.Combine(_directory, "bad.bin")));

        Assert.False(result.Succeeded);
        Assert.Contains("non valido", result.Message);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("https://example.com/file", null, true)]
    [InlineData("http://localhost:5000/file", false, true)]
    [InlineData("http://127.0.0.1/file", false, true)]
    [InlineData("http://[::1]/file", false, true)]
    [InlineData("http://example.com/file", false, false)]
    [InlineData("http://example.com/file", true, true)]
    [InlineData("ftp://example.com/file", false, false)]
    [InlineData("file:///tmp/file", false, false)]
    [InlineData("javascript:alert(1)", false, false)]
    public void Url_policy_accepts_https_and_loopback_http_only(
        string url,
        bool? allowInsecureHttp,
        bool expected)
    {
        Assert.Equal(
            expected,
            DownloadManager.IsAllowedUri(new Uri(url), allowInsecureHttp));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\secret.txt", "secret.txt")]
    [InlineData("report:2024?.zip", "report_2024_.zip")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("   ", "download")]
    [InlineData("", "download")]
    [InlineData("../", "download")]
    [InlineData("....", "download")]
    public void File_names_are_sanitized(string value, string expected)
    {
        Assert.Equal(expected, DownloadManager.SanitizeFileName(value));
    }

    [Fact]
    public void Long_file_names_keep_the_extension_and_are_capped()
    {
        var name = DownloadManager.SanitizeFileName(
            new string('a', 200) + ".txt");

        Assert.Equal(DownloadManager.MaxFileNameLength, name.Length);
        Assert.EndsWith(".txt", name);
    }

    [Fact]
    public void Control_characters_are_replaced()
    {
        Assert.Equal(
            "a_b.txt", DownloadManager.SanitizeFileName("a\u0000b.txt"));
    }

    private static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

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

    private sealed class GatedStream(byte[] data, Task gate) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count))
                .AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(buffer.Length, data.Length - _offset);
            if (count <= 0)
                return 0;

            data.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
