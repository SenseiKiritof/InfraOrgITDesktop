using System.Text.Json;

namespace InfraOrgIt.Desktop.Instances;

/// <summary>
/// JSON file with the configured instances. Saves are atomic and reject
/// duplicate names or URLs; malformed entries are skipped on load so one bad
/// line cannot lock the app out.
/// </summary>
public sealed class InstanceStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InfraOrgIT",
                "instances.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "InfraOrgIT", "instances.json")
            : Path.Combine(home, ".config", "infraorgit", "instances.json");
    }

    public IReadOnlyList<Instance> Load()
    {
        if (!File.Exists(FilePath))
            return [];

        var items = JsonSerializer.Deserialize<List<Instance>>(
            File.ReadAllText(FilePath), JsonOptions) ?? [];
        var result = new List<Instance>();
        foreach (var item in items)
        {
            if (Instance.TryCreate(item.Name, item.Url, out var instance, out _))
                result.Add(instance);
        }

        return result;
    }

    public IReadOnlyList<Instance> Save(IEnumerable<Instance> instances)
    {
        var result = new List<Instance>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in instances)
        {
            if (!Instance.TryCreate(item.Name, item.Url, out var instance, out var error))
                throw new ArgumentException(error);
            if (!names.Add(instance.Name))
                throw new ArgumentException($"Nome duplicato: {instance.Name}.");
            if (!urls.Add(instance.Url))
                throw new ArgumentException($"URL duplicato: {instance.Url}.");
            result.Add(instance);
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
        return result;
    }
}
