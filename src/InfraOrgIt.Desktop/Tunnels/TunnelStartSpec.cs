namespace InfraOrgIt.Desktop.Tunnels;

internal enum TunnelMode
{
    Local,
    Dynamic,
    Remote
}

internal enum TunnelKind
{
    Tcp,
    Http,
    Https
}

/// <summary>
/// One ssh session: either a deployment forward or a custom tunnel service.
/// Only the mode-specific fields are set, so the live projections can send
/// null for the values the API does not accept in that mode.
/// </summary>
internal sealed record TunnelStartSpec(
    TunnelServer Server,
    TunnelEndpoint? Endpoint,
    TunnelService? Service,
    TunnelMode Mode,
    TunnelKind Kind,
    string? TargetHost,
    int? TargetPort,
    int? LocalPort,
    int? RemotePort)
{
    public string Name => Service?.Name ?? Endpoint?.Application ?? "tunnel";

    /// <summary>
    /// Only a local HTTP(S) service has a browser URL, and only while its
    /// tunnel is active. The manager keeps this value out of the UI until the
    /// process is running.
    /// </summary>
    public string? OpenUrl
    {
        get
        {
            if (Mode != TunnelMode.Local ||
                Kind is not (TunnelKind.Http or TunnelKind.Https) ||
                LocalPort is not int port)
            {
                return null;
            }

            var path = Service?.OpenPath?.Trim() ?? "";
            if (path.Length > 0 && !path.StartsWith('/'))
                path = "/" + path;
            var scheme = Kind == TunnelKind.Https ? "https" : "http";
            return $"{scheme}://127.0.0.1:{port}{path}";
        }
    }

    public static TunnelStartSpec CreateDeployment(
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort) =>
        new(
            server,
            endpoint,
            null,
            TunnelMode.Local,
            TunnelKind.Tcp,
            endpoint.TargetHost,
            endpoint.TargetPort,
            localPort,
            null);

    /// <summary>
    /// Validates a deployment or service and resolves its ports. A local or
    /// dynamic forward takes the requested port, then the service port, then
    /// a free port; a remote forward never allocates on this machine.
    /// </summary>
    public static bool TryCreate(
        TunnelServer server,
        TunnelEndpoint? endpoint,
        TunnelService? service,
        int? requestedLocalPort,
        out TunnelStartSpec? spec,
        out string? error)
    {
        spec = null;
        error = null;

        if (service is null)
        {
            if (endpoint is null)
            {
                error = "Target non più disponibile; ricarica i target.";
                return false;
            }

            if (!TryLocalPort(
                    requestedLocalPort, endpoint.SuggestedLocalPort,
                    null, out var deploymentPort, out error))
            {
                return false;
            }

            spec = CreateDeployment(server, endpoint, deploymentPort);
            return true;
        }

        if (!service.Enabled)
        {
            error = $"Servizio \"{service.Name}\": disabilitato.";
            return false;
        }

        if (!TryParseMode(service.Mode, out var mode))
        {
            error = $"Servizio \"{service.Name}\": modalità \"{service.Mode}\" non valida.";
            return false;
        }

        if (!TryParseKind(service.Kind, out var kind))
        {
            error = $"Servizio \"{service.Name}\": tipo \"{service.Kind}\" non valido.";
            return false;
        }

        if (mode != TunnelMode.Local && kind != TunnelKind.Tcp)
        {
            error =
                $"Servizio \"{service.Name}\": il tipo Http o Https è ammesso solo in modalità Locale.";
            return false;
        }

        switch (mode)
        {
            case TunnelMode.Dynamic:
                if (!TryLocalPort(
                        requestedLocalPort, service.LocalPort,
                        service.Name, out var dynamicPort, out error))
                {
                    return false;
                }

                spec = new TunnelStartSpec(
                    server, null, service, mode, kind,
                    null, null, dynamicPort, null);
                return true;

            case TunnelMode.Remote:
                if (requestedLocalPort is not null)
                {
                    error =
                        $"Servizio \"{service.Name}\": la modalità Remota non usa una porta locale.";
                    return false;
                }

                if (service.LocalPort is not null)
                {
                    error =
                        $"Servizio \"{service.Name}\": la modalità Remota non ammette una porta locale configurata.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(service.TargetHost))
                {
                    error = $"Servizio \"{service.Name}\": host target mancante.";
                    return false;
                }

                if (service.TargetPort is not (>= 1 and <= 65_535))
                {
                    error = $"Servizio \"{service.Name}\": porta target non valida.";
                    return false;
                }

                if (service.RemotePort is not (>= 1 and <= 65_535))
                {
                    error = $"Servizio \"{service.Name}\": porta remota non valida.";
                    return false;
                }

                spec = new TunnelStartSpec(
                    server, null, service, mode, kind,
                    service.TargetHost, service.TargetPort,
                    null, service.RemotePort);
                return true;

            default:
                if (string.IsNullOrWhiteSpace(service.TargetHost))
                {
                    error = $"Servizio \"{service.Name}\": host target mancante.";
                    return false;
                }

                if (service.TargetPort is not (>= 1 and <= 65_535))
                {
                    error = $"Servizio \"{service.Name}\": porta target non valida.";
                    return false;
                }

                if (!TryLocalPort(
                        requestedLocalPort, service.LocalPort,
                        service.Name, out var localPort, out error))
                {
                    return false;
                }

                spec = new TunnelStartSpec(
                    server, null, service, mode, kind,
                    service.TargetHost, service.TargetPort,
                    localPort, null);
                return true;
        }
    }

    private static bool TryLocalPort(
        int? requested,
        int? configured,
        string? serviceName,
        out int port,
        out string? error)
    {
        error = null;
        if ((requested ?? configured) is int candidate)
        {
            if (candidate is < 1024 or > 65_535)
            {
                error = serviceName is null
                    ? "La porta locale deve essere tra 1024 e 65535."
                    : $"Servizio \"{serviceName}\": la porta locale deve essere tra 1024 e 65535.";
                port = 0;
                return false;
            }

            if (!PortAllocator.IsFree(candidate))
            {
                error = serviceName is null
                    ? $"La porta locale {candidate} è già in uso."
                    : $"Servizio \"{serviceName}\": la porta locale {candidate} è già in uso.";
                port = 0;
                return false;
            }

            port = candidate;
            return true;
        }

        port = PortAllocator.FindFree();
        return true;
    }

    private static bool TryParseMode(string? value, out TunnelMode mode) =>
        Enum.TryParse(value, ignoreCase: true, out mode) &&
        Enum.IsDefined(mode);

    private static bool TryParseKind(string? value, out TunnelKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind) &&
        Enum.IsDefined(kind);
}
