using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InfraOrgIt.Desktop.Update;

/// <summary>
/// Reads the latest stable GitHub release, compares it with the running
/// version and selects the archive for the current platform. Download and
/// checksum verification live in <see cref="UpdateDownloader"/>. Failures come
/// back as a result, so the UI thread never sees an unhandled exception.
/// </summary>
public sealed class UpdateChecker(
    HttpClient http,
    string? currentVersion = null,
    string? runtimeIdentifier = null)
{
    internal const string RepositoryOwner = "SenseiKiritof";
    internal const string RepositoryName = "InfraOrgITDesktop";
    internal const string ReleasesApiUrl =
        "https://api.github.com/repos/" + RepositoryOwner + "/" +
        RepositoryName + "/releases/latest";
    internal const string ReleasesPageUrl =
        "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases";
    internal const string UserAgent = "InfraOrgIT-Desktop";
    internal const string AssetPrefix = "InfraOrgIT-Desktop-";

    /// <summary>Release notes are shown in a small card, so they are capped.</summary>
    internal const int NotesLimit = 600;

    private readonly string _runtimeIdentifier =
        string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? RuntimeInformation.RuntimeIdentifier
            : runtimeIdentifier!;

    /// <summary>Version read from the assembly informational version.</summary>
    public string CurrentVersion { get; } =
        string.IsNullOrWhiteSpace(currentVersion)
            ? DetectCurrentVersion()
            : currentVersion!;

    public async Task<UpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.SendAsync(
                request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Unavailable(
                    CurrentVersion, "Nessuna release pubblicata.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.Forbidden &&
                IsRateLimited(response))
            {
                return UpdateCheckResult.Failure(
                    CurrentVersion,
                    "Limite di richieste GitHub raggiunto. Riprova più tardi.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failure(
                    CurrentVersion,
                    $"GitHub ha risposto {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(
                cancellationToken);
            return Parse(body);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failure(
                CurrentVersion, "GitHub non ha risposto in tempo.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failure(
                CurrentVersion,
                "Controllo non riuscito: impossibile contattare GitHub.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failure(
                CurrentVersion, "Risposta di GitHub non valida.");
        }
    }

    private UpdateCheckResult Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagElement))
        {
            return UpdateCheckResult.Failure(
                CurrentVersion, "Risposta di GitHub non valida.");
        }

        var tag = tagElement.GetString();
        if (!TryParseVersion(tag, out var latest, out var isPrerelease))
        {
            return UpdateCheckResult.Failure(
                CurrentVersion, "La release non ha una versione valida.");
        }

        var flagged = IsFlag(root, "prerelease") || IsFlag(root, "draft");
        if (flagged || isPrerelease)
        {
            return UpdateCheckResult.Unavailable(
                CurrentVersion,
                "L'ultima release è una prerelease: nessun aggiornamento stabile.");
        }

        var latestVersion = $"{latest.Major}.{latest.Minor}.{latest.Build}";
        if (IsNewerVersion(CurrentVersion, latestVersion))
        {
            var assets = ParseAssets(root);
            var download = PickDownloadAsset(assets, _runtimeIdentifier);
            var checksum = download is null
                ? null
                : assets.FirstOrDefault(asset => string.Equals(
                    asset.Name,
                    download.Name + ".sha256",
                    StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckResult(
                CurrentVersion,
                latestVersion,
                IsNewer: true,
                Failed: false,
                Message: $"Nuova versione disponibile: {latestVersion}.",
                ReleaseUrl: ReleaseUri(OptionalString(root, "html_url")),
                ReleaseName: OptionalString(root, "name"),
                ReleaseNotes: CapNotes(OptionalString(root, "body")),
                DownloadAsset: download,
                ChecksumAsset: checksum);
        }

        return new UpdateCheckResult(
            CurrentVersion,
            latestVersion,
            IsNewer: false,
            Failed: false,
            Message: $"Hai già l'ultima versione ({CurrentVersion}).");
    }

    /// <summary>
    /// Picks the release archive for a runtime identifier. The exact RID wins;
    /// otherwise the same OS with any architecture is accepted, which is the
    /// documented fallback when a runner does not publish the user's RID:
    /// macOS and Windows ship a zip, Linux a tar.gz.
    /// </summary>
    internal static UpdateAsset? PickDownloadAsset(
        IReadOnlyList<UpdateAsset> assets,
        string? runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
            return null;

        var rid = runtimeIdentifier.Trim();
        var exact = assets.FirstOrDefault(asset => IsArchiveForRid(asset.Name, rid));
        if (exact is not null)
            return exact;

        var os = OperatingSystemFromRid(rid);
        if (os is null)
            return null;

        var extension = os == "linux" ? ".tar.gz" : ".zip";
        return assets.FirstOrDefault(asset =>
            asset.Name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains($"-{os}-", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArchiveForRid(string name, string rid) =>
        name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
        (name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith($"-{rid}.tar.gz", StringComparison.OrdinalIgnoreCase));

    private static string? OperatingSystemFromRid(string rid)
    {
        var os = rid.Split('-')[0].ToLowerInvariant();
        return os is "osx" or "win" or "linux" ? os : null;
    }

    private static List<UpdateAsset> ParseAssets(JsonElement root)
    {
        var assets = new List<UpdateAsset>();
        if (!root.TryGetProperty("assets", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = OptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(
                    OptionalString(item, "browser_download_url"),
                    UriKind.Absolute,
                    out var url) ||
                url.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var size =
                item.TryGetProperty("size", out var sizeElement) &&
                sizeElement.ValueKind == JsonValueKind.Number &&
                sizeElement.TryGetInt64(out var value)
                    ? value
                    : 0;
            assets.Add(new UpdateAsset(name, url, size));
        }

        return assets;
    }

    /// <summary>
    /// Compares semver strings. A leading <c>v</c> and build metadata are
    /// accepted; a prerelease tag is never considered an update.
    /// </summary>
    internal static bool IsNewerVersion(string current, string latest)
    {
        if (!TryParseVersion(latest, out var latestVersion, out var isPrerelease) ||
            isPrerelease)
        {
            return false;
        }

        if (!TryParseVersion(current, out var currentVersion, out _))
            return true;

        return latestVersion > currentVersion;
    }

    internal static bool TryParseVersion(
        string? value, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0);
        isPrerelease = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length > 1 && text[0] is 'v' or 'V')
            text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            isPrerelease = true;
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out var patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }

    /// <summary>
    /// The assembly informational version, without build metadata: the SDK may
    /// append <c>+&lt;commit&gt;</c> to <c>Version</c>.
    /// </summary>
    internal static string DetectCurrentVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        var value =
            assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "0.0.0";

        var plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus] : value;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        values.FirstOrDefault() == "0";

    private static bool IsFlag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.True;

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static Uri ReleaseUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : new Uri(ReleasesPageUrl);

    private static string? CapNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        var text = notes.Trim();
        return text.Length <= NotesLimit
            ? text
            : text[..NotesLimit].TrimEnd() + "…";
    }
}

/// <summary>
/// Outcome of a check. <see cref="Failed"/> separates a real error from a
/// healthy "no release yet" or "already up to date" answer.
/// </summary>
public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool IsNewer,
    bool Failed,
    string Message,
    Uri? ReleaseUrl = null,
    string? ReleaseName = null,
    string? ReleaseNotes = null,
    UpdateAsset? DownloadAsset = null,
    UpdateAsset? ChecksumAsset = null)
{
    /// <summary>True when the release publishes the archive's SHA-256 file.</summary>
    public bool ChecksumAvailable => ChecksumAsset is not null;

    internal static UpdateCheckResult Failure(string current, string message) =>
        new(current, null, IsNewer: false, Failed: true, message);

    internal static UpdateCheckResult Unavailable(string current, string message) =>
        new(current, null, IsNewer: false, Failed: false, message);
}

/// <summary>A GitHub release asset, as listed by the release API.</summary>
public sealed record UpdateAsset(string Name, Uri Url, long Size);
