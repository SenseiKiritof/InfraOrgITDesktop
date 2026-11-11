namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Response of GET /api/tunnels/targets. The API is the allowlist; the app
/// never invents a host or port. Services and jumps are optional so an older
/// instance that only returns deployments keeps working.
/// </summary>
public sealed record TunnelTargetsResponse(IReadOnlyList<TunnelServer> Items);

public sealed record TunnelServer(
    int ServerId,
    string ServerName,
    string EnvironmentLabel,
    string SshHost,
    int SshPort,
    string Username,
    TunnelJump? Jump,
    IReadOnlyList<TunnelEndpoint> Targets,
    IReadOnlyList<TunnelJump>? Jumps = null,
    IReadOnlyList<TunnelService>? Services = null);

public sealed record TunnelJump(string Host, int Port, string Username);

public sealed record TunnelEndpoint(
    int ServerApplicationId,
    int ApplicationId,
    string Application,
    string Version,
    string TargetHost,
    int TargetPort,
    int? SuggestedLocalPort);

public sealed record TunnelService(
    int Id,
    int ServerId,
    string Name,
    string Mode,
    string TargetHost,
    int? TargetPort,
    int? LocalPort,
    int? RemotePort,
    string Kind,
    string? OpenPath,
    bool AutoStart,
    bool Enabled);
