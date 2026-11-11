namespace InfraOrgIt.Desktop.Instances;

/// <summary>
/// One InfraOrgIT installation the desktop app can open. A URL is always
/// absolute http/https; credentials stay in the instance, the app stores none.
/// Plain http is accepted only for loopback hosts unless the development
/// override <c>INFRAORGIT_ALLOW_INSECURE_HTTP=1</c> is set.
/// </summary>
public sealed record Instance
{
    /// <summary>Development-only escape hatch for a remote plain-http instance.</summary>
    public const string AllowInsecureHttpVariable = "INFRAORGIT_ALLOW_INSECURE_HTTP";

    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    public static bool TryCreate(
        string? name,
        string? url,
        out Instance instance,
        out string error,
        bool? allowInsecureHttp = null)
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

        if (uri.Scheme == Uri.UriSchemeHttp &&
            !uri.IsLoopback &&
            !(allowInsecureHttp ?? AllowInsecureHttpFromEnvironment()))
        {
            error = "L'URL deve usare https; http è consentito solo per localhost, 127.0.0.1 o [::1].";
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

    private static bool AllowInsecureHttpFromEnvironment() =>
        Environment.GetEnvironmentVariable(AllowInsecureHttpVariable) == "1";
}
