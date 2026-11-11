using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Builds the system ssh command and allocates loopback ports. Arguments go
/// through ArgumentList, so usernames and hosts are never interpreted by a
/// shell. Host-key verification and the user's SSH agent stay in charge.
/// </summary>
public static class SshTunnel
{
    public static IReadOnlyList<string> BuildArguments(
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort) =>
        BuildArguments(
            server,
            TunnelStartSpec.CreateDeployment(server, endpoint, localPort));

    internal static IReadOnlyList<string> BuildArguments(
        TunnelServer server,
        TunnelStartSpec spec)
    {
        var arguments = new List<string>
        {
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-p", server.SshPort.ToString()
        };

        var jumps = JumpChain(server);
        if (jumps.Count > 0)
        {
            arguments.Add("-J");
            arguments.Add(string.Join(",", jumps.Select(FormatJump)));
        }

        switch (spec.Mode)
        {
            case TunnelMode.Dynamic:
                arguments.Add("-D");
                arguments.Add($"127.0.0.1:{spec.LocalPort}");
                break;

            case TunnelMode.Remote:
                arguments.Add("-R");
                arguments.Add(
                    $"127.0.0.1:{spec.RemotePort}:" +
                    $"{FormatHost(spec.TargetHost!)}:{spec.TargetPort}");
                break;

            default:
                arguments.Add("-L");
                arguments.Add(
                    $"127.0.0.1:{spec.LocalPort}:" +
                    $"{FormatHost(spec.TargetHost!)}:{spec.TargetPort}");
                break;
        }

        arguments.Add($"{server.Username}@{FormatHost(server.SshHost)}");
        return arguments;
    }

    public static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

    public static ProcessStartInfo BuildStartInfo(
        TunnelServer server,
        TunnelEndpoint endpoint,
        int localPort) =>
        BuildStartInfo(
            server,
            TunnelStartSpec.CreateDeployment(server, endpoint, localPort));

    internal static ProcessStartInfo BuildStartInfo(
        TunnelServer server,
        TunnelStartSpec spec)
    {
        var executable = Environment.GetEnvironmentVariable("INFRAORGIT_SSH");
        var startInfo = new ProcessStartInfo(
            string.IsNullOrWhiteSpace(executable) ? "ssh" : executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(server, spec))
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static IReadOnlyList<TunnelJump> JumpChain(TunnelServer server)
    {
        if (server.Jumps is { Count: > 0 } jumps)
            return jumps.Take(3).ToArray();
        return server.Jump is null ? [] : [server.Jump];
    }

    private static string FormatJump(TunnelJump jump) =>
        $"{jump.Username}@{FormatHost(jump.Host)}:{jump.Port}";
}

public static class PortAllocator
{
    public static int FindFree()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static bool IsFree(int port)
    {
        if (port is < 1024 or > 65_535)
            return false;

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
