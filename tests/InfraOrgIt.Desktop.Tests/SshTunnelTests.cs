using System.Net;
using System.Net.Sockets;
using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class SshTunnelTests
{
    private static TunnelServer Server(
        string sshHost = "10.0.0.1",
        TunnelJump? jump = null) =>
        new(1, "srv", "Provider / Env", sshHost, 22, "deploy", jump,
            [Endpoint()]);

    private static TunnelEndpoint Endpoint(
        string host = "127.0.0.1", int port = 8080) =>
        new(2, 3, "app", "1.0.0", host, port, port);

    [Fact]
    public void Builds_a_local_forward_without_a_jump_host()
    {
        var arguments = SshTunnel.BuildArguments(Server(), Endpoint(), 18080);

        Assert.Equal([
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-p", "22",
            "-L", "127.0.0.1:18080:127.0.0.1:8080",
            "deploy@10.0.0.1"
        ], arguments);
    }

    [Fact]
    public void Builds_a_jump_host_and_brackets_ipv6_addresses()
    {
        var server = Server(
            "2001:db8::10",
            new TunnelJump("2001:db8::9", 2222, "jump-user"));
        var endpoint = Endpoint("2001:db8::20", 8443);

        var arguments = SshTunnel.BuildArguments(server, endpoint, 18443);

        Assert.Equal([
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-p", "22",
            "-J", "jump-user@[2001:db8::9]:2222",
            "-L", "127.0.0.1:18443:[2001:db8::20]:8443",
            "deploy@[2001:db8::10]"
        ], arguments);
    }

    [Fact]
    public void Builds_a_dynamic_socks_proxy_without_a_target()
    {
        var port = PortAllocator.FindFree();
        var spec = ServiceSpec(Server(), "Dynamic", localPort: port);

        var arguments = SshTunnel.BuildArguments(Server(), spec);

        Assert.Equal([
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-p", "22",
            "-D", $"127.0.0.1:{port}",
            "deploy@10.0.0.1"
        ], arguments);
    }

    [Fact]
    public void Builds_a_remote_forward_on_the_remote_port()
    {
        var server = Server();
        var spec = ServiceSpec(
            server, "Remote", targetHost: "2001:db8::30", targetPort: 443,
            localPort: null, remotePort: 9443);

        var arguments = SshTunnel.BuildArguments(server, spec);

        Assert.Equal([
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-p", "22",
            "-R", "127.0.0.1:9443:[2001:db8::30]:443",
            "deploy@10.0.0.1"
        ], arguments);
    }

    [Fact]
    public void Builds_a_multi_hop_jump_chain_nearest_first()
    {
        var server = Server() with
        {
            Jump = new TunnelJump("2001:db8::1", 2222, "nearest"),
            Jumps =
            [
                new TunnelJump("2001:db8::1", 2222, "nearest"),
                new TunnelJump("2001:db8::2", 2200, "far")
            ]
        };

        var arguments = SshTunnel.BuildArguments(server, Endpoint(), 18080);

        Assert.Contains("-J", arguments);
        Assert.Contains(
            "nearest@[2001:db8::1]:2222,far@[2001:db8::2]:2200",
            arguments);
    }

    [Fact]
    public void Finds_a_free_loopback_port()
    {
        var port = PortAllocator.FindFree();

        Assert.InRange(port, 1024, 65_535);
        Assert.True(PortAllocator.IsFree(port));
    }

    [Fact]
    public void Detects_an_occupied_port_and_rejects_privileged_ones()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.False(PortAllocator.IsFree(port));
        }
        finally
        {
            listener.Stop();
        }

        Assert.False(PortAllocator.IsFree(80));
    }

    private static TunnelStartSpec ServiceSpec(
        TunnelServer server,
        string mode,
        string kind = "Tcp",
        string targetHost = "127.0.0.1",
        int? targetPort = 8080,
        int? localPort = 18080,
        int? remotePort = null)
    {
        var service = new TunnelService(
            10, server.ServerId, "svc", mode, targetHost,
            targetPort, localPort, remotePort, kind, null, false, true);
        Assert.True(
            TunnelStartSpec.TryCreate(
                server, null, service, null, out var spec, out var error),
            error);
        return spec!;
    }
}
