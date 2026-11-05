using VRCLearn.App.Mcp;
using Xunit;

namespace VRCLearn.Mcp.Tests;

public sealed class McpAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", 39421, null, true)]
    [InlineData("localhost", 39421, "", true)]
    [InlineData("127.0.0.1", 39422, null, false)]
    [InlineData("127.0.0.1", null, null, false)]
    [InlineData("127.0.0.1.attacker.test", 39421, null, false)]
    [InlineData("localhost.attacker.test", 39421, null, false)]
    [InlineData("192.168.1.2", 39421, null, false)]
    [InlineData("0.0.0.0", 39421, null, false)]
    [InlineData("::1", 39421, null, false)]
    [InlineData("127.0.0.1", 39421, "https://attacker.test", false)]
    [InlineData("localhost", 39421, "null", false)]
    [InlineData("localhost", 39421, " ", false)]
    public void RequiresTheConfiguredLoopbackEndpointWithoutBrowserOrigin(string host, int? port, string? origin, bool expected)
    {
        Assert.Equal(expected, McpAccess.Allows("Bearer test-token", "test-token", host, port, 39421, origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-token")]
    [InlineData("Basic test-token")]
    [InlineData("bearer test-token")]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer test-token ")]
    [InlineData(" Bearer test-token")]
    [InlineData("Bearer test-token\r\n")]
    public void RejectsMissingOrIncorrectAuthorization(string? authorization)
    {
        Assert.False(McpAccess.Allows(authorization, "test-token", "localhost", 39421, 39421, null));
    }

    [Fact]
    public void RejectsAnEmptyConfiguredToken()
    {
        Assert.False(McpAccess.Allows("Bearer ", "", "localhost", 39421, 39421, null));
    }

    [Fact]
    public void ComparesTheCompleteUtf8Token()
    {
        Assert.True(McpAccess.Allows("Bearer 中文-token", "中文-token", "127.0.0.1", 39421, 39421, null));
        Assert.False(McpAccess.Allows("Bearer 中文-toke", "中文-token", "127.0.0.1", 39421, 39421, null));
        Assert.False(McpAccess.Allows("Bearer 中文-token-extra", "中文-token", "127.0.0.1", 39421, 39421, null));
    }
}
