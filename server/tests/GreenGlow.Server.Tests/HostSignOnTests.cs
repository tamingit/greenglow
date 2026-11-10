using System.Net;
using System.Net.Sockets;
using GreenGlow.FakeHost;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Printing;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

/// <summary>Signing on to an IBM i host server (the Network Print server) with a password substitute.</summary>
public sealed class HostSignOnTests
{
    private static readonly HostConnector Connector = new(null);

    private static Target Endpoint(int port) => new("test", "Test", false, "127.0.0.1", port);

    private static Task<HostServerClient> SignOnAsync(FakeNetworkPrint fake, string user, string password) =>
        HostServerClient.SignOnAsync(Connector, Endpoint(fake.Port), HostService.NetworkPrint, user, password, T.Token());

    [Fact]
    public void DesIsStandardDes()
    {
        // The worked example from FIPS 46 tutorials: key 133457799BBCDFF1, plain 0123456789ABCDEF.
        var cipher = PasswordSubstitute.Encipher(Convert.FromHexString("133457799BBCDFF1"), Convert.FromHexString("0123456789ABCDEF"));
        Assert.Equal("85E813540F0AB405", Convert.ToHexString(cipher));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SignsOnAtEveryPasswordLevel(int level)
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.PasswordLevel = level;
        await using var client = await SignOnAsync(fake, "prtuser", level < 2 ? "secret12" : FakeNetworkPrint.DefaultPassword);
        Assert.Equal("PRTUSER", client.User);
        Assert.Equal(level, client.PasswordLevel);
        Assert.Equal(1, fake.SignOns);
    }

    [Theory]
    [InlineData(0, "PRTUSER123", "1secret90")]
    [InlineData(1, "PRTUSERAB", "secret")]
    [InlineData(3, "PRTUSER123", "Mixed Case Pass#2")]
    public async Task LongUsersAndPasswordsTakeTheLongerPaths(int level, string user, string password)
    {
        // A 9- or 10-character user is folded and a password over 8 (with the Q in front of a
        // leading digit) takes two halves, for DES.
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.PasswordLevel = level;
        fake.User = user;
        fake.Password = password;
        await using var client = await SignOnAsync(fake, user, password);
        Assert.Equal(1, fake.SignOns);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedOnceAndNeverRetried()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnAsync(fake, "PRTUSER", "not-the-password"));
        Assert.Equal(SignOnFailure.WrongPassword, e.Failure);
        Assert.Equal(0x0003000B, e.Code);
        Assert.Contains("counts toward the profile being disabled", e.Message);
        Assert.DoesNotContain("not-the-password", e.Message);
        Assert.Equal(1, fake.Refusals);
        Assert.Equal(0, fake.SignOns);
    }

    [Fact]
    public async Task AnUnknownUserIsSaidSo()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnAsync(fake, "SOMEONE", FakeNetworkPrint.DefaultPassword));
        Assert.Equal(SignOnFailure.UnknownUser, e.Failure);
    }

    [Theory]
    [InlineData(0x0003000D, SignOnFailure.PasswordExpired, "expired")]
    [InlineData(0x00020002, SignOnFailure.ProfileDisabled, "disabled")]
    [InlineData(0x0003000C, SignOnFailure.WrongPasswordDisabled, "now disabled")]
    [InlineData(0x00030010, SignOnFailure.NoPassword, "no password")]
    [InlineData(0x00040000, SignOnFailure.Refused, "00040000")]
    public async Task RefusalsSayWhatTheIbmISaid(int code, SignOnFailure failure, string words)
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.Refuse = code;
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnAsync(fake, "PRTUSER", FakeNetworkPrint.DefaultPassword));
        Assert.Equal(failure, e.Failure);
        Assert.Contains(words, e.Message);
    }

    [Theory]
    [InlineData("bad name", "x", SignOnFailure.InvalidUser)]
    [InlineData("TOOLONGUSER1", "x", SignOnFailure.InvalidUser)]
    [InlineData("PRTUSER", "", SignOnFailure.InvalidPassword)]
    [InlineData("PRTUSER", "*SAME", SignOnFailure.InvalidPassword)]
    public async Task WhatCantBeSentIsNeverAnAttempt(string user, string password, SignOnFailure failure)
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnAsync(fake, user, password));
        Assert.Equal(failure, e.Failure);
        Assert.Equal(0, fake.Refusals);
    }

    [Fact]
    public async Task ADesPasswordOverTenCharactersIsNeverSent()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.PasswordLevel = 0;
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnAsync(fake, "PRTUSER", "elevenchars"));
        Assert.Equal(SignOnFailure.InvalidPassword, e.Failure);
        Assert.Equal(0, fake.Refusals);
    }

    [Fact]
    public async Task NoServerIsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var e = await Assert.ThrowsAsync<SignOnException>(() =>
            HostServerClient.SignOnAsync(Connector, Endpoint(port), HostService.NetworkPrint, "PRTUSER", "x", T.Token()));
        Assert.Equal(SignOnFailure.Unreachable, e.Failure);
        Assert.Contains("as-netprt", e.Message);
    }

    [Fact]
    public void TheEndpointIsTheConnectionsHostOnTheDefaultPorts()
    {
        var secure = new Target("prod", "Production", true, "ibmi.example", 992);
        var plain = new Target("test", "Test", false, "test.example", 23);
        Assert.Equal(("ibmi.example", 9474, true), Shape(HostService.NetworkPrint.EndpointFor(secure)));
        Assert.Equal(("test.example", 8474, false), Shape(HostService.NetworkPrint.EndpointFor(plain)));
        return;

        static (string, int, bool) Shape(Target t) => (t.Host, t.Port, t.Secure);
    }
}
