using GreenGlow.Server.Configuration;
using GreenGlow.Server.Web;

namespace GreenGlow.Server.Tests;

public class TargetParserTests
{
    /// <summary>
    /// The old "name=url,..." cases, now through the JSON seed: each entry becomes a seeded
    /// connection and each "name=label" a label.
    /// </summary>
    [Theory]
    [InlineData("a=b\u0001")]
    [InlineData("a=b\u007f")]
    public void ControlCharactersInLabelsAreRejected(string labels) =>
        Assert.Throws<ConfigException>(() => Parse("a=tcp://h", labels));

    private static IReadOnlyList<Target> Parse(string targets, string labels)
    {
        var named = labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('=', 2)).ToList();
        var entries = targets.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('=', 2))
            .Select(p => new
            {
                name = p[0].Trim(),
                url = p.Length > 1 ? p[1].Trim() : "",
                label = named.LastOrDefault(l => l[0].Trim() == p[0].Trim()) is { Length: > 1 } l ? l[1].Trim() : null,
            })
            .ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(new { connections = entries });
        return ConnectionSeed.Parse("test", json).Select(c => new Target(c.Name, c.Label, c.Secure, c.Host, c.Port)).ToList();
    }

    [Fact]
    public void ParsesTlsAndTcpWithDefaultPorts()
    {
        var t = Parse("prod=tls://ibmi.example.com, test=tcp://test.example.com", "");
        Assert.Equal(2, t.Count);
        Assert.Equal(new Target("prod", "prod", true, "ibmi.example.com", 992), t[0]);
        Assert.Equal(new Target("test", "test", false, "test.example.com", 23), t[1]);
    }

    [Fact]
    public void ExplicitPortsAreKept()
    {
        var t = Parse("a=tcp://h:2323,b=tls://h:9920", "");
        Assert.Equal(2323, t[0].Port);
        Assert.Equal(9920, t[1].Port);
    }

    [Fact]
    public void ExplicitDefaultPortIsStillThatPort()
    {
        var t = Parse("a=tcp://h:23,b=tls://h:992", "");
        Assert.Equal(23, t[0].Port);
        Assert.Equal(992, t[1].Port);
    }

    [Fact]
    public void Ipv6HostInBracketsIsUnwrapped()
    {
        var t = Assert.Single(Parse("v6=tcp://[::1]:2323", ""));
        Assert.Equal("::1", t.Host);
        Assert.Equal(2323, t.Port);
        var d = Assert.Single(Parse("v6=tls://[::1]", ""));
        Assert.Equal(992, d.Port);
    }

    [Fact]
    public void LabelsAreApplied()
    {
        var t = Parse("prod=tls://h,test=tcp://h", "test=Test system, prod=Production");
        Assert.Equal("Production", t[0].Label);
        Assert.Equal("Test system", t[1].Label);
    }

    [Fact]
    public void LabelDefaultsToName() =>
        Assert.Equal("test", Assert.Single(Parse("test=tcp://h", "")).Label);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public void NoConfiguredTargetsIsAnEmptyList(string value) =>
        Assert.Empty(Parse(value, ""));

    [Theory]
    [InlineData("Prod=tcp://h")]
    [InlineData("1a=tcp://h")]
    [InlineData("a_b=tcp://h")]
    [InlineData("=tcp://h")]
    [InlineData("tcp://h")]
    [InlineData("noequals")]
    public void BadNamesAreRejected(string value) =>
        Assert.Throws<ConfigException>(() => Parse(value, ""));

    [Fact]
    public void NameOverThirtyTwoCharactersIsRejected()
    {
        Assert.Single(Parse(new string('a', 32) + "=tcp://h", ""));
        Assert.Throws<ConfigException>(() => Parse(new string('a', 33) + "=tcp://h", ""));
    }

    [Fact]
    public void DuplicateNamesAreRejected() =>
        Assert.Throws<ConfigException>(() => Parse("a=tcp://h,a=tls://h", ""));

    [Theory]
    [InlineData("a=http://h")]
    [InlineData("a=telnet://h")]
    [InlineData("a=ws://h")]
    [InlineData("a=h:23")]
    [InlineData("a=notaurl")]
    public void BadSchemesAreRejected(string value) =>
        Assert.Throws<ConfigException>(() => Parse(value, ""));

    [Theory]
    [InlineData("a=tcp://h/path")]
    [InlineData("a=tcp://h/x/y")]
    [InlineData("a=tcp://h?q=1")]
    [InlineData("a=tcp://user@h")]
    [InlineData("a=tcp://user:pw@h:23")]
    [InlineData("a=tcp://h#frag")]
    public void PathQueryAndUserInfoAreRejected(string value) =>
        Assert.Throws<ConfigException>(() => Parse(value, ""));

    [Fact]
    public void TrailingSlashIsAccepted() =>
        Assert.Equal("h", Assert.Single(Parse("a=tcp://h/", "")).Host);

    [Fact]
    public void LabelLengthLimitIsForty()
    {
        Assert.Equal(40, Parse("a=tcp://h", "a=" + new string('x', 40))[0].Label.Length);
        Assert.Throws<ConfigException>(() => Parse("a=tcp://h", "a=" + new string('x', 41)));
    }

}

public class OriginPolicyTests
{
    private static OriginPolicy Policy(string publicUrl, params string[] extra) =>
        new(new ServerOptions { PublicUrl = publicUrl, AllowedOrigins = extra });

    [Fact]
    public void PublicUrlOriginIsAllowed()
    {
        var p = Policy("https://greenglow.example.com");
        Assert.True(p.IsAllowed("https://greenglow.example.com"));
        Assert.False(p.IsAllowed("https://other.example.com"));
        Assert.False(p.IsAllowed("http://greenglow.example.com"));
    }

    [Fact]
    public void DefaultPublicUrlIsLocalhost() =>
        Assert.True(new OriginPolicy(new ServerOptions()).IsAllowed("http://localhost:5080"));

    [Fact]
    public void ExtraOriginsAreAllowed()
    {
        var p = Policy("https://a.example.com", "http://localhost:5173", " https://b.example.com ");
        Assert.True(p.IsAllowed("http://localhost:5173"));
        Assert.True(p.IsAllowed("https://b.example.com"));
        Assert.True(p.IsAllowed("https://a.example.com"));
        Assert.False(p.IsAllowed("http://localhost:5174"));
    }

    [Fact]
    public void TrailingSlashInConfigurationIsAccepted()
    {
        var p = Policy("https://a.example.com/", "http://localhost:5173/");
        Assert.True(p.IsAllowed("https://a.example.com"));
        Assert.True(p.IsAllowed("http://localhost:5173"));
        Assert.Contains("http://localhost:5173", p.Allowed);
    }

    [Fact]
    public void BlankExtrasAreIgnored() =>
        Assert.Single(Policy("https://a.example.com", "", "  ").Allowed);

    [Theory]
    [InlineData("localhost:5080")]
    [InlineData("ftp://a.example.com")]
    [InlineData("https://a.example.com/app")]
    [InlineData("https://a.example.com/?x=1")]
    [InlineData("https://user@a.example.com")]
    [InlineData("not a url")]
    public void NonOriginValuesAreRejected(string value)
    {
        Assert.Throws<ConfigException>(() => Policy(value));
        Assert.Throws<ConfigException>(() => Policy("https://ok.example.com", value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyOrMissingOriginIsRefused(string? origin) =>
        Assert.False(Policy("https://a.example.com").IsAllowed(origin));
}
