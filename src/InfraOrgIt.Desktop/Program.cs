using System.Text.Json;
using InfraOrgIt.Desktop.Instances;
using Photino.NET;

namespace InfraOrgIt.Desktop;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [STAThread]
    private static void Main()
    {
        var store = new InstanceStore(InstanceStore.DefaultPath());
        var children = new List<PhotinoWindow>();

        var manager = new PhotinoWindow()
            .SetTitle("InfraOrgIT Desktop")
            .SetUseOsDefaultSize(false)
            .SetSize(980, 720)
            .Center()
            .RegisterWebMessageReceivedHandler(
                (sender, message) => HandleMessage(
                    (PhotinoWindow)sender!, message, store, children))
            .RegisterWindowClosingHandler((_, _) =>
            {
                foreach (var child in children.ToArray())
                    child.Close();
                return false;
            })
            .Load(Path.Combine(AppContext.BaseDirectory, "ui", "manager.html"));

#if DEBUG
        manager.SetDevToolsEnabled(true);
#endif

        manager.WaitForClose();
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

            switch (type)
            {
                case "list":
                    Send(window, new { type = "instances", instances = SafeLoad(store) });
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

    private static void Send(PhotinoWindow window, object payload) =>
        window.SendWebMessage(JsonSerializer.Serialize(payload, JsonOptions));
}
