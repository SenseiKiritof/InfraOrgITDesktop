using System.Net;
using System.Net.Sockets;
using System.Text;
using Photino.NET;

// Disposable Photino.NET 4.0.16 harness for the patched Photino.Native.dylib.
//
// Serves a normal page, then:
//   1. clicks <a download href="/inline.txt"> (navigation-action download path)
//   2. navigates to /attachment with Content-Disposition: attachment
//
// With PHOTINO_DOWNLOAD_DIR set, the native patch saves without a panel, so the
// run can be asserted unattended. Without the patched dylib both files stay
// missing and the harness exits 2.

var configured = Environment.GetEnvironmentVariable("PHOTINO_DOWNLOAD_DIR");
var downloadDir = string.IsNullOrWhiteSpace(configured)
    ? Path.Combine(Path.GetTempPath(), "photino-download-test-" + Guid.NewGuid().ToString("N"))
    : configured;
Directory.CreateDirectory(downloadDir);

var inlinePath = Path.Combine(downloadDir, "inline.txt");
var attachmentPath = Path.Combine(downloadDir, "report.txt");

var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();

var baseUrl = $"http://127.0.0.1:{port}";
var listener = new HttpListener();
listener.Prefixes.Add(baseUrl + "/");
listener.Start();

var serverLog = new List<string>();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (HttpListenerException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        var path = context.Request.Url?.AbsolutePath ?? "/";
        lock (serverLog)
            serverLog.Add(path);

        byte[] body;
        switch (path)
        {
            case "/inline.txt":
                context.Response.ContentType = "text/plain";
                body = Encoding.UTF8.GetBytes("inline anchor download\n");
                break;
            case "/attachment":
                context.Response.ContentType = "text/plain";
                context.Response.AddHeader(
                    "Content-Disposition",
                    "attachment; filename=\"report.txt\"");
                body = Encoding.UTF8.GetBytes("attachment body\n");
                break;
            case "/":
                context.Response.ContentType = "text/html";
                body = Encoding.UTF8.GetBytes("""
                    <!doctype html>
                    <html>
                      <head><meta charset="utf-8"><title>download harness</title></head>
                      <body>
                        <h1>normal page loaded</h1>
                        <a id="inline" href="/inline.txt" download>download inline</a>
                        <script>
                          window.addEventListener('load', function () {
                            setTimeout(function () {
                              document.getElementById('inline').click();
                            }, 700);
                            setTimeout(function () {
                              location.assign('/attachment');
                            }, 1900);
                          });
                        </script>
                      </body>
                    </html>
                    """);
                break;
            default:
                context.Response.StatusCode = 404;
                body = Encoding.UTF8.GetBytes("not found");
                break;
        }

        try
        {
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // The webview can abort a request when a download is cancelled.
        }
        catch (ObjectDisposedException)
        {
        }
    }
});

var window = new PhotinoWindow()
    .SetTitle("photino download harness")
    .SetSize(700, 500)
    .Load(baseUrl + "/");

var timeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("HARNESS_TIMEOUT_SECONDS"),
    out var parsed) && parsed > 0
    ? parsed
    : 20;
var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

_ = Task.Run(() =>
{
    while (DateTime.UtcNow < deadline)
    {
        if (ContentsEqual(inlinePath, "inline anchor download\n")
            && ContentsEqual(attachmentPath, "attachment body\n"))
        {
            Console.WriteLine("RESULT: PASS (inline anchor and attachment saved)");
            window.Invoke(() => Environment.Exit(0));
            return;
        }

        Thread.Sleep(100);
    }

    Console.WriteLine("RESULT: FAIL (files not saved before timeout)");
    lock (serverLog)
        Console.WriteLine("server requests: " + string.Join(", ", serverLog));
    Console.WriteLine("download dir: " + downloadDir);
    window.Invoke(() => Environment.Exit(2));
});

window.WaitForClose();
Console.WriteLine("RESULT: FAIL (window closed before downloads completed)");
Environment.Exit(2);

static bool ContentsEqual(string path, string expected)
{
    try
    {
        return File.Exists(path) && File.ReadAllText(path) == expected;
    }
    catch (IOException)
    {
        return false;
    }
}
