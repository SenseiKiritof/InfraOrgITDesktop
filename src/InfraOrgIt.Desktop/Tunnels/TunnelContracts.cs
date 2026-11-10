namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Response of GET /api/tunnels/targets. The API is the allowlist; the app
/// never invents a host or port.
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
    IReadOnlyList<TunnelEndpoint> Targets);

public sealed record TunnelJump(string Host, int Port, string Username);

public sealed record TunnelEndpoint(
    int ServerApplicationId,
    int ApplicationId,
    string Application,
    string Version,
    string TargetHost,
    int TargetPort,
    int? SuggestedLocalPort);
