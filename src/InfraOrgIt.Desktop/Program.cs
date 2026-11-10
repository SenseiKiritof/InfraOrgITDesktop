using System.Diagnostics;
using System.Text.Json;
using InfraOrgIt.Desktop.Instances;
using InfraOrgIt.Desktop.Tunnels;
using Photino.NET;

namespace InfraOrgIt.Desktop;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static PhotinoWindow? _manager;
    private static TunnelManager? _tunnels;

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
            payload => _manager?.Invoke(() => Send(_manager, payload)));

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

            if (type is "tunnels.list" or "tunnels.start" or "tunnels.login")
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

            switch (type)
            {
                case "list":
                    Send(window, new { type = "instances", instances = SafeLoad(store) });
                    break;

                case "auth.open":
                    OpenVerification(window, root);
                    break;

                case "save":
                    var input = root.GetProperty("instances")
                        .Deserialize<List<Instance>>(JsonOptions) ?? [];
                    var saved = store.Save(input);
                    Send(window, new { type = "instances", instances = saved });
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
            .RegisterWindowClosingHandler((sender, _) =>
            {
                children.Remove((PhotinoWindow)sender);
                return false;
            })
            .Load(instance.Url);

        children.Add(child);
        child.WaitForClose();
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

    private static void Send(PhotinoWindow? window, object payload)
    {
        if (window is null)
            return;
        window.SendWebMessage(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
