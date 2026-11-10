using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace InfraOrgIt.Desktop.Update;

/// <summary>
/// Reads the latest stable GitHub release and compares it with the running
/// version. It never downloads or installs anything: the manager page only
/// offers the release page. Failures come back as a result, so the UI thread
/// never sees an unhandled exception.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string? currentVersion = null)
{
    internal const string RepositoryOwner = "SenseiKiritof";
    internal const string RepositoryName = "InfraOrgITDesktop";
    internal const string ReleasesApiUrl =
        "https://api.github.com/repos/" + RepositoryOwner + "/" +
        RepositoryName + "/releases/latest";
    internal const string ReleasesPageUrl =
        "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases";
    internal const string UserAgent = "InfraOrgIT-Desktop";

    /// <summary>Release notes are shown in a small card, so they are capped.</summary>
    internal const int NotesLimit = 600;

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
            return new UpdateCheckResult(
                CurrentVersion,
                latestVersion,
                IsNewer: true,
                Failed: false,
                Message: $"Nuova versione disponibile: {latestVersion}.",
                ReleaseUrl: ReleaseUri(OptionalString(root, "html_url")),
                ReleaseName: OptionalString(root, "name"),
                ReleaseNotes: CapNotes(OptionalString(root, "body")));
        }

        return new UpdateCheckResult(
            CurrentVersion,
            latestVersion,
            IsNewer: false,
            Failed: false,
            Message: $"Hai già l'ultima versione ({CurrentVersion}).");
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
    string? ReleaseNotes = null)
{
    internal static UpdateCheckResult Failure(string current, string message) =>
        new(current, null, IsNewer: false, Failed: true, message);

    internal static UpdateCheckResult Unavailable(string current, string message) =>
        new(current, null, IsNewer: false, Failed: false, message);
}
