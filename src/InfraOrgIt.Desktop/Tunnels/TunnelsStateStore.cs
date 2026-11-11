using System.Text.Json;
using InfraOrgIt.Desktop.Instances;

namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Identity of a tunnel that should be restored after an app restart. It
/// carries no token, host, username or port target: only ids that the instance
/// API must confirm again in its allowlist, plus the requested local port.
/// </summary>
public sealed record TunnelStateEntry(
    string InstanceUrl,
    int ServerId,
    int? ServiceId,
    int? ServerApplicationId,
    string Mode,
    int? LocalPort)
{
    /// <summary>Stable identity of the tunnel, also persisted for readability.</summary>
    public string Key => KeyFor(
        InstanceUrl, ServerId, ServiceId, ServerApplicationId);

    public static string KeyFor(
        string instanceUrl,
        int serverId,
        int? serviceId,
        int? serverApplicationId) =>
        $"{NormalizeUrl(instanceUrl)}|{serverId}|" +
        (serviceId is int service
            ? $"service:{service}"
            : $"deployment:{serverApplicationId}");

    public static bool TryCreate(
        string instanceUrl,
        int serverId,
        int? serviceId,
        int? serverApplicationId,
        string? mode,
        int? localPort,
        out TunnelStateEntry? entry,
        out string? error) =>
        TryValidate(
            new TunnelStateEntry(
                instanceUrl, serverId, serviceId, serverApplicationId,
                mode ?? "", localPort),
            out entry,
            out error);

    internal static bool TryValidate(
        TunnelStateEntry candidate,
        out TunnelStateEntry? entry,
        out string? error)
    {
        entry = null;
        error = null;

        var url = NormalizeUrl(candidate.InstanceUrl);
        if (url.Length == 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL istanza non valido.";
            return false;
        }

        if (candidate.ServerId <= 0)
        {
            error = "Server non valido.";
            return false;
        }

        var hasService = candidate.ServiceId is not null;
        var hasDeployment = candidate.ServerApplicationId is not null;
        if (hasService == hasDeployment)
        {
            error = "Serve un servizio o un deployment, non entrambi.";
            return false;
        }

        if (candidate.ServiceId is <= 0 || candidate.ServerApplicationId is <= 0)
        {
            error = "Identificativo del target non valido.";
            return false;
        }

        if (!Enum.TryParse<TunnelMode>(
                candidate.Mode?.Trim(), ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode))
        {
            error = "Modalità del tunnel non valida.";
            return false;
        }

        if (candidate.LocalPort is int port && port is < 1024 or > 65_535)
        {
            error = "La porta locale deve essere tra 1024 e 65535.";
            return false;
        }

        if (mode == TunnelMode.Remote && candidate.LocalPort is not null)
        {
            error = "La modalità Remota non usa una porta locale.";
            return false;
        }

        entry = new TunnelStateEntry(
            url,
            candidate.ServerId,
            candidate.ServiceId,
            candidate.ServerApplicationId,
            mode.ToString(),
            candidate.LocalPort);
        return true;
    }

    internal static string NormalizeUrl(string? instanceUrl)
    {
        var value = instanceUrl?.Trim() ?? "";
        if (value.Length == 0)
            return "";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.ToString().TrimEnd('/')
            : value.TrimEnd('/');
    }
}

/// <summary>
/// JSON file with the tunnels to restore on the next app start, next to
/// <c>instances.json</c>. Saves are atomic, malformed entries are skipped on
/// load and only validated identities are written. No token or secret is ever
/// part of an entry. A <c>null</c> path keeps the state in memory, which the
/// tests use and the app does not.
/// </summary>
public sealed class TunnelsStateStore(string? filePath)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private List<TunnelStateEntry>? _entries;

    public string? FilePath { get; } =
        string.IsNullOrWhiteSpace(filePath) ? null : filePath;

    public bool IsPersistent => FilePath is not null;

    public static string DefaultPath()
    {
        var directory = Path.GetDirectoryName(InstanceStore.DefaultPath());
        return Path.Combine(directory ?? ".", "tunnels.json");
    }

    public IReadOnlyList<TunnelStateEntry> Load()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return [.. _entries!];
        }
    }

    public void Save(IEnumerable<TunnelStateEntry> entries)
    {
        var result = new List<TunnelStateEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in entries)
        {
            if (!TunnelStateEntry.TryValidate(candidate, out var entry, out var error))
                throw new ArgumentException(error);
            if (!keys.Add(entry!.Key))
                throw new ArgumentException($"Tunnel duplicato: {entry.Key}.");
            result.Add(entry);
        }

        lock (_gate)
        {
            _entries = result;
            PersistLocked();
        }
    }

    /// <summary>Adds or refreshes one identity. Returns false when invalid.</summary>
    public bool Add(TunnelStateEntry candidate)
    {
        if (!TunnelStateEntry.TryValidate(candidate, out var entry, out _))
            return false;

        lock (_gate)
        {
            EnsureLoadedLocked();
            var index = _entries!.FindIndex(x => x.Key == entry!.Key);
            if (index >= 0)
            {
                if (_entries[index] == entry)
                    return false;
                _entries[index] = entry!;
            }
            else
            {
                _entries.Add(entry!);
            }

            PersistLocked();
            return true;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var removed = _entries!.RemoveAll(x => x.Key == key) > 0;
            if (removed)
                PersistLocked();
            return removed;
        }
    }

    public int RemoveWhere(Func<TunnelStateEntry, bool> predicate)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var removed = _entries!.RemoveAll(x => predicate(x));
            if (removed > 0)
                PersistLocked();
            return removed;
        }
    }

    /// <summary>Forgets every saved tunnel; active tunnels keep running.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_entries!.Count == 0)
                return false;
            _entries.Clear();
            PersistLocked();
            return true;
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_entries is not null)
            return;

        var loaded = new List<TunnelStateEntry>();
        if (IsPersistent && File.Exists(FilePath))
        {
            var text = File.ReadAllText(FilePath!);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        TunnelStateEntry? raw;
                        try
                        {
                            raw = element.Deserialize<TunnelStateEntry>(JsonOptions);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (raw is null ||
                            !TunnelStateEntry.TryValidate(raw, out var entry, out _) ||
                            !keys.Add(entry!.Key))
                        {
                            continue;
                        }

                        loaded.Add(entry);
                    }
                }
            }
            catch (JsonException)
            {
                loaded.Clear();
            }
        }

        _entries = loaded;
    }

    private void PersistLocked()
    {
        if (!IsPersistent)
            return;

        var path = FilePath!;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
