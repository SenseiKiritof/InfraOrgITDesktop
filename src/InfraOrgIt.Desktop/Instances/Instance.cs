namespace InfraOrgIt.Desktop.Instances;

/// <summary>
/// One InfraOrgIT installation the desktop app can open. A URL is always
/// absolute http/https; credentials stay in the instance, the app stores none.
/// </summary>
public sealed record Instance
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    public static bool TryCreate(
        string? name,
        string? url,
        out Instance instance,
        out string error)
    {
        instance = new Instance();
        var cleanName = name?.Trim() ?? "";
        if (cleanName.Length is 0 or > 80)
        {
            error = "Il nome deve avere tra 1 e 80 caratteri.";
            return false;
        }

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "L'URL deve essere http o https.";
            return false;
        }

        instance = new Instance
        {
            Name = cleanName,
            Url = uri.ToString().TrimEnd('/')
        };
        error = "";
        return true;
    }
}
