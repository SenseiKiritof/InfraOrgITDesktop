using InfraOrgIt.Desktop.Tunnels;

namespace InfraOrgIt.Desktop.Tests;

public sealed class InstanceBridgePolicyTests
{
    private const string Instance = "https://infra.example.com";

    [Theory]
    [InlineData("tunnels.list")]
    [InlineData("tunnels.restore")]
    [InlineData("tunnels.login")]
    [InlineData("tunnels.login.cancel")]
    [InlineData("tunnels.start")]
    [InlineData("tunnels.stop")]
    [InlineData("tunnels.retries.cancel")]
    [InlineData("tunnels.logout")]
    public void Known_tunnel_messages_are_allowed(string type) =>
        Assert.True(InstanceBridgePolicy.IsTunnelMessage(type));

    [Theory]
    [InlineData(null)]
    [InlineData("tunnels.forget")]
    [InlineData("open")]
    [InlineData("desktop.download")]
    public void Other_messages_are_not_tunnel_messages(string? type) =>
        Assert.False(InstanceBridgePolicy.IsTunnelMessage(type));

    [Fact]
    public void Message_without_url_inherits_the_child_instance()
    {
        Assert.Equal(
            Instance,
            InstanceBridgePolicy.ResolveUrl(null, Instance));
    }

    [Theory]
    [InlineData("https://infra.example.com")]
    [InlineData("https://infra.example.com/")]
    [InlineData("HTTPS://INFRA.EXAMPLE.COM")]
    public void Instance_url_and_origin_match(string messageUrl)
    {
        Assert.Equal(
            Instance,
            InstanceBridgePolicy.ResolveUrl(messageUrl, Instance));
    }

    [Theory]
    [InlineData("https://other.example.com")]
    [InlineData("https://infra.example.com:8443")]
    [InlineData("http://infra.example.com")]
    [InlineData("https://infra.example.com/other")]
    [InlineData("not-a-url")]
    public void Another_instance_is_rejected(string messageUrl)
    {
        Assert.Null(InstanceBridgePolicy.ResolveUrl(messageUrl, Instance));
    }

    [Fact]
    public void Base_path_instance_requires_the_exact_url()
    {
        const string withPath = "https://infra.example.com/app";
        Assert.Null(
            InstanceBridgePolicy.ResolveUrl("https://infra.example.com", withPath));
        Assert.Equal(
            withPath,
            InstanceBridgePolicy.ResolveUrl(withPath, withPath));
    }
}
