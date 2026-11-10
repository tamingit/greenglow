using System.Net;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Tests;

public class ClientAddressTests
{
    [Theory]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::7", "2001:db8::7")]
    public void AnIpv4ClientIsWrittenAsIpv4(string address, string expected) =>
        Assert.Equal(expected, ClientAddress.Format(IPAddress.Parse(address)));

    [Fact]
    public void NoAddressIsNone() => Assert.Null(ClientAddress.Format(null));
}
