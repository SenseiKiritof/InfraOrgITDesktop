namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Guard rails for tunnel messages sent by an instance child window. A child
/// may only drive the manager for its own instance URL; the manager window
/// keeps the full local bridge and is trusted.
/// </summary>
internal static class InstanceBridgePolicy
{
    private static readonly HashSet<string> TunnelMessages =
        new(StringComparer.Ordinal)
        {
            "tunnels.list",
            "tunnels.restore",
            "tunnels.login",
            "tunnels.login.cancel",
            "tunnels.start",
            "tunnels.stop",
            "tunnels.retries.cancel",
            "tunnels.logout"
        };

    public static bool IsTunnelMessage(string? type) =>
        type is not null && TunnelMessages.Contains(type);

    /// <summary>
    /// Returns the instance URL that owns the message, or null when the message
    /// belongs to another instance. A message without a URL inherits the child
    /// URL; a message URL is accepted only when it is the child URL or its
    /// origin (the SPA sends <c>window.location.origin</c>).
    /// </summary>
    public static string? ResolveUrl(string? messageUrl, string instanceUrl)
    {
        var instance = TunnelStateEntry.NormalizeUrl(instanceUrl);
        if (!Uri.TryCreate(instance, UriKind.Absolute, out var instanceUri))
            return null;

        if (string.IsNullOrWhiteSpace(messageUrl))
            return instance;

        var candidate = TunnelStateEntry.NormalizeUrl(messageUrl);
        if (candidate.Equals(instance, StringComparison.OrdinalIgnoreCase))
            return instance;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) ||
            !candidateUri.Scheme.Equals(
                instanceUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !candidateUri.Host.Equals(
                instanceUri.Host, StringComparison.OrdinalIgnoreCase) ||
            candidateUri.Port != instanceUri.Port ||
            instanceUri.AbsolutePath is not ("" or "/"))
        {
            return null;
        }

        // The SPA origin has no path. An instance mounted under a base path
        // must send the exact instance URL instead of the origin.
        return candidateUri.AbsolutePath is "" or "/" ? instance : null;
    }
}
