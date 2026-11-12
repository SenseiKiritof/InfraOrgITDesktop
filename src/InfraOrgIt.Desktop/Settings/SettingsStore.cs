using System.Text.Json;
using System.Text.Json.Serialization;
using InfraOrgIt.Desktop.Instances;

namespace InfraOrgIt.Desktop.Settings;

/// <summary>
/// Small JSON preferences file next to <c>instances.json</c>. Saves are
/// atomic and a missing or malformed file falls back to the defaults, so a
/// damaged preference can never keep the app from starting.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public string FilePath { get; } = filePath;

    public static string DefaultPath()
    {
        var directory = Path.GetDirectoryName(InstanceStore.DefaultPath());
        return Path.Combine(directory!, "settings.json");
    }

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new DesktopSettings();

            var settings = JsonSerializer.Deserialize<DesktopSettings>(
                File.ReadAllText(FilePath), JsonOptions);
            return settings is not null && Enum.IsDefined(settings.DownloadMode)
                ? settings
                : new DesktopSettings();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or
            UnauthorizedAccessException)
        {
            return new DesktopSettings();
        }
    }

    public DesktopSettings Save(DesktopSettings settings)
    {
        if (!Enum.IsDefined(settings.DownloadMode))
            throw new ArgumentException("Modalità di download non valida.");

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(
            temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
        return settings;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
