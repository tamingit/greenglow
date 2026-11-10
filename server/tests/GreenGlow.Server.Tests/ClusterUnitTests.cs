using System.Text;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace GreenGlow.Server.Tests;

public sealed class HlcTests
{
    /// <summary>A clock that can be set back, as a node's clock can be corrected.</summary>
    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void VersionsOrderAsStringsAndNeverRepeatOrGoBack()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var hlc = new Hlc(2, clock);
        var a = hlc.Next();
        var b = hlc.Next();
        clock.Now -= TimeSpan.FromMilliseconds(50);
        var c = hlc.Next();
        Assert.True(Hlc.Compare(a, b) < 0);
        Assert.True(Hlc.Compare(b, c) < 0);
        Assert.Equal(Hlc.Length, a.Length);
        Assert.True(Hlc.Compare(Hlc.Zero, a) < 0);
        Assert.EndsWith("02", a);
    }

    [Fact]
    public void AVersionSeenFromAnotherNodeMovesTheClockPastIt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ahead = new Hlc(1, new FakeTimeProvider(clock.GetUtcNow().AddMinutes(5))).Next();
        var hlc = new Hlc(2, clock);
        hlc.Witness(ahead);
        Assert.True(Hlc.Compare(hlc.Next(), ahead) > 0);
        hlc.Witness("not a version");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000000000000000zz")]
    [InlineData("00000000000000000")]
    [InlineData("0000000000000000000")]
    public void OnlyWellFormedVersionsAreValid(string version) => Assert.False(Hlc.IsValid(version));
}

public sealed class SealTests
{
    [Fact]
    public void ASealOpensOnlyWithTheSameSecretAndContextAndUntouched()
    {
        var seal = new Seal("one secret that is long enough for the test");
        var plain = Encoding.UTF8.GetBytes("{\"x\":1}");
        var sealedBytes = seal.Close(plain, "context");
        Assert.Equal(plain, seal.Open(sealedBytes, "context"));
        Assert.Null(seal.Open(sealedBytes, "another context"));
        Assert.Null(new Seal("another secret that is long enough for it").Open(sealedBytes, "context"));
        sealedBytes[^1] ^= 1;
        Assert.Null(seal.Open(sealedBytes, "context"));
        Assert.Null(seal.Open(new byte[5], "context"));
        // Never the same twice: a fresh nonce each time.
        Assert.NotEqual(Seal.NonceOf(seal.Close(plain, "context")), Seal.NonceOf(seal.Close(plain, "context")));
    }
}

public sealed class ClusterOptionsTests
{
    private const string Secret = "a secret that is at least thirty-two characters";

    [Fact]
    public void ASingleServerNeedsNothing()
    {
        var options = new ClusterOptions().Validate();
        Assert.False(options.Clustered);
        Assert.Equal(1, options.NodeId);
    }

    [Fact]
    public void PeersAreReadByNodeNumber()
    {
        var options = new ClusterOptions { NodeId = 1, Secret = Secret, Peers = new() { ["2"] = "http://two:8081" } }.Validate();
        Assert.Equal(new Uri("http://two:8081"), options.PeerUrls[2]);
    }

    [Theory]
    [InlineData(0, "2", "http://two:8081", Secret)]
    [InlineData(10, "2", "http://two:8081", Secret)]
    [InlineData(1, "1", "http://two:8081", Secret)]
    [InlineData(1, "x", "http://two:8081", Secret)]
    [InlineData(1, "2", "ftp://two", Secret)]
    [InlineData(1, "2", "http://two:8081/?q=1", Secret)]
    [InlineData(1, "2", "http://two:8081", "short")]
    [InlineData(1, "2", "http://two:8081", null)]
    public void BadSettingsStopTheStart(int node, string peer, string url, string? secret) =>
        Assert.Throws<ConfigException>(() =>
            new ClusterOptions { NodeId = node, Secret = secret, Peers = new() { [peer] = url } }.Validate());

    [Fact]
    public void ThePeerAndAgentPortsMustDiffer() =>
        Assert.Throws<ConfigException>(() => new ClusterOptions { Port = 8081, AgentPort = 8081 }.Validate());
}
