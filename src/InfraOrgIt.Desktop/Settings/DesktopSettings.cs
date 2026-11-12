namespace InfraOrgIt.Desktop.Settings;

/// <summary>How the instance SPA should hand downloads to the desktop app.</summary>
public enum DownloadMode
{
    Auto,
    Bridge,
    Native
}

/// <summary>
/// Local app preferences. Only non-secret, user-facing settings belong here;
/// tokens stay in memory or in the OS secret store.
/// </summary>
public sealed record DesktopSettings
{
    public DownloadMode DownloadMode { get; init; } = DownloadMode.Auto;
}

/// <summary>Stable wire and JSON values for <see cref="DownloadMode"/>.</summary>
public static class DownloadModeNames
{
    public const string Auto = "auto";
    public const string Bridge = "bridge";
    public const string Native = "native";

    public static bool TryParse(string? value, out DownloadMode mode)
    {
        switch (value)
        {
            case Auto:
                mode = DownloadMode.Auto;
                return true;
            case Bridge:
                mode = DownloadMode.Bridge;
                return true;
            case Native:
                mode = DownloadMode.Native;
                return true;
            default:
                mode = DownloadMode.Auto;
                return false;
        }
    }

    public static string ToWireValue(DownloadMode mode) => mode switch
    {
        DownloadMode.Auto => Auto,
        DownloadMode.Bridge => Bridge,
        DownloadMode.Native => Native,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
