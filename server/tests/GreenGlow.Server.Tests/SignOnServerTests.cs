using GreenGlow.FakeHost;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

/// <summary>The IBM i sign-on server (checking and changing a password) and the port mapper, against the fake IBM i.</summary>
public sealed class SignOnServerTests
{
    private static readonly HostConnector Connector = new(null);

    private static Target Endpoint(int port) => new("test", "Test", false, "127.0.0.1", port);

    private static FakeSignOn Fake(int level, string user = FakeAccounts.DefaultUser, string password = FakeAccounts.DefaultPassword)
    {
        var accounts = new FakeAccounts { PasswordLevel = level };
        accounts.Set(user, password);
        return new FakeSignOn(accounts);
    }

    [Theory]
    [InlineData(0, "PRTUSER", "secret12")]
    [InlineData(1, "PRTUSER123", "1secret90")]
    [InlineData(2, "PRTUSER", "Secret12")]
    [InlineData(3, "PRTUSER123", "A much longer pass phrase, #2")]
    [InlineData(4, "PRTUSER", "Secret12")]
    [InlineData(4, "PRTUSER123", "abc")]
    public async Task ARightPasswordIsVerifiedAtEveryPasswordLevel(int level, string user, string password)
    {
        await using var fake = Fake(level, user, password);
        var info = await SignOnServer.VerifyAsync(Connector, Endpoint(fake.Port), user.ToLowerInvariant(), password, T.Token());
        Assert.Equal(user, info.User);
        Assert.Equal(level, info.PasswordLevel);
        Assert.Equal(new DateTime(2027, 2, 14), info.PasswordExpires);
        Assert.Equal(new DateTime(2026, 11, 13, 17, 5, 0), info.LastSignOn);
        Assert.Equal(7, info.ExpiryWarningDays);
        Assert.Equal(1, fake.Verified);
    }

    [Fact]
    public void ThePasswordLevel4SubstituteIsPbkdf2ThenSha512()
    {
        // Worked by hand from the published steps, not with either implementation's code.
        byte[] clientSeed = [1, 2, 3, 4, 5, 6, 7, 8], serverSeed = [8, 7, 6, 5, 4, 3, 2, 1];
        var id = System.Text.Encoding.BigEndianUnicode.GetBytes("PRTUSER   ");
        var salt = System.Security.Cryptography.SHA256.HashData([.. id, .. System.Text.Encoding.BigEndianUnicode.GetBytes("et12")]);
        var token = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("Secret12"u8, salt, 10022,
            System.Security.Cryptography.HashAlgorithmName.SHA512, 64);
        var expected = System.Security.Cryptography.SHA512.HashData([.. token, .. serverSeed, .. clientSeed, .. id, 0, 0, 0, 0, 0, 0, 0, 1]);

        var (scheme, substitute) = PasswordSubstitute.For(4, "prtuser", "Secret12", clientSeed, serverSeed);
        Assert.Equal(PasswordSubstitute.Sha512Scheme, scheme);
        Assert.Equal(64, substitute.Length);
        Assert.Equal(expected, substitute);
    }

    [Fact]
    public void APasswordLevelNobodyKnowsIsUnsupported()
    {
        var e = Assert.Throws<SignOnException>(() => PasswordSubstitute.For(5, "PRTUSER", "Secret12", new byte[8], new byte[8]));
        Assert.Equal(SignOnFailure.Unsupported, e.Failure);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedOnce()
    {
        await using var fake = Fake(2);
        var e = await Assert.ThrowsAsync<SignOnException>(() =>
            SignOnServer.VerifyAsync(Connector, Endpoint(fake.Port), "PRTUSER", "not-the-password", T.Token()));
        Assert.Equal(SignOnFailure.WrongPassword, e.Failure);
        Assert.DoesNotContain("not-the-password", e.Message);
        Assert.Equal(1, fake.Refusals);
    }

    [Fact]
    public async Task UnknownDisabledAndExpiredAreToldApart()
    {
        await using var fake = Fake(2);
        fake.Accounts.Set("LOCKED", "Secret12");
        fake.Accounts.Disable("LOCKED");
        fake.Accounts.Set("OLDPWD", "Secret12");
        fake.Accounts.Expire("OLDPWD");
        async Task<SignOnFailure> Try(string user) => (await Assert.ThrowsAsync<SignOnException>(() =>
            SignOnServer.VerifyAsync(Connector, Endpoint(fake.Port), user, "Secret12", T.Token()))).Failure;
        Assert.Equal(SignOnFailure.UnknownUser, await Try("NOBODY"));
        Assert.Equal(SignOnFailure.ProfileDisabled, await Try("LOCKED"));
        Assert.Equal(SignOnFailure.PasswordExpired, await Try("OLDPWD"));
    }

    [Fact]
    public async Task AUserOrPasswordThatCannotBeSentNeverReachesTheIbmI()
    {
        await using var fake = Fake(2);
        var e = await Assert.ThrowsAsync<SignOnException>(() =>
            SignOnServer.VerifyAsync(Connector, Endpoint(fake.Port), "not a user", "x", T.Token()));
        Assert.Equal(SignOnFailure.InvalidUser, e.Failure);
        e = await Assert.ThrowsAsync<SignOnException>(() => SignOnServer.VerifyAsync(Connector, Endpoint(fake.Port), "PRTUSER", " ", T.Token()));
        Assert.Equal(SignOnFailure.InvalidPassword, e.Failure);
        Assert.Equal(0, fake.Refusals + fake.Verified);
    }

    [Theory]
    [InlineData(0, "PRTUSER", "secret12", "newpass1")]
    [InlineData(0, "PRTUSER123", "1secret90", "2another9")]
    [InlineData(1, "PRTUSER", "short1", "longer1234")]
    [InlineData(1, "PRTUSER", "longer1234", "short1")]
    [InlineData(2, "PRTUSER", "Secret12", "NewSecret34")]
    [InlineData(3, "PRTUSER123", "Secret12", "A new pass phrase of more than twenty characters")]
    [InlineData(3, "PRTUSER", "An old pass phrase of more than twenty characters", "Short#1")]
    [InlineData(4, "PRTUSER", "Secret12", "NewSecret34")]
    [InlineData(4, "PRTUSER123", "Secret12", "A new pass phrase long enough to need more than one block of 64")]
    [InlineData(4, "PRTUSER", "An old pass phrase long enough to need more than one block of 64", "Short#1")]
    public async Task APasswordIsChangedAtEveryPasswordLevel(int level, string user, string oldPassword, string newPassword)
    {
        await using var fake = Fake(level, user, oldPassword);
        var endpoint = Endpoint(fake.Port);
        await SignOnServer.ChangePasswordAsync(Connector, endpoint, user, oldPassword, newPassword, T.Token());
        Assert.Equal(1, fake.Changes);
        Assert.Equal(newPassword, fake.Accounts.PasswordOf(user), ignoreCase: level < 2);

        await SignOnServer.VerifyAsync(Connector, endpoint, user, newPassword, T.Token());
        var e = await Assert.ThrowsAsync<SignOnException>(() => SignOnServer.VerifyAsync(Connector, endpoint, user, oldPassword, T.Token()));
        Assert.Equal(SignOnFailure.WrongPassword, e.Failure);
    }

    [Fact]
    public async Task AnExpiredPasswordCanBeChangedAndThenWorks()
    {
        await using var fake = Fake(3);
        fake.Accounts.Expire("PRTUSER");
        var endpoint = Endpoint(fake.Port);
        await SignOnServer.ChangePasswordAsync(Connector, endpoint, "PRTUSER", "Secret12", "Renewed#34", T.Token());
        var info = await SignOnServer.VerifyAsync(Connector, endpoint, "PRTUSER", "Renewed#34", T.Token());
        Assert.Equal("PRTUSER", info.User);
    }

    [Theory]
    [InlineData("Secret12", "not-the-password", "NewSecret34", SignOnFailure.WrongPassword)]
    [InlineData("Secret12", "Secret12", "Secret12", SignOnFailure.NewPasswordRefused)]
    [InlineData("Secret12", "Secret12", "tiny", SignOnFailure.NewPasswordRefused)]
    [InlineData("Secret12", "Secret12", "*NONE", SignOnFailure.NewPasswordRefused)]
    public async Task ARefusedChangeLeavesThePasswordAsItWas(string actual, string typedOld, string typedNew, SignOnFailure failure)
    {
        await using var fake = Fake(2, "PRTUSER", actual);
        var e = await Assert.ThrowsAsync<SignOnException>(() =>
            SignOnServer.ChangePasswordAsync(Connector, Endpoint(fake.Port), "PRTUSER", typedOld, typedNew, T.Token()));
        Assert.Equal(failure, e.Failure);
        Assert.DoesNotContain(typedNew, e.Message);
        Assert.Equal(actual, fake.Accounts.PasswordOf("PRTUSER"));
        Assert.Equal(0, fake.Changes);
    }

    [Fact]
    public async Task ASignOnServerThatIsNotThereIsUnreachable()
    {
        int port;
        await using (var fake = Fake(2)) port = fake.Port;
        var e = await Assert.ThrowsAsync<SignOnException>(() =>
            SignOnServer.VerifyAsync(Connector, Endpoint(port), "PRTUSER", "Secret12", T.Token()));
        Assert.Equal(SignOnFailure.Unreachable, e.Failure);
    }

    [Fact]
    public async Task ThePortMapperSaysWhereAServiceIs()
    {
        await using var mapper = new FakePortMapper().Map("as-signon", 18476).Map("as-signon-s", 19476);
        Assert.Equal(18476, await PortMapper.LookupAsync("127.0.0.1", mapper.Port, "as-signon", T.Token()));
        Assert.Equal(19476, await PortMapper.LookupAsync("127.0.0.1", mapper.Port, "as-signon-s", T.Token()));
        var e = await Assert.ThrowsAsync<SignOnException>(() => PortMapper.LookupAsync("127.0.0.1", mapper.Port, "as-netprt", T.Token()));
        Assert.Equal(SignOnFailure.Unreachable, e.Failure);
        Assert.Contains("as-netprt", e.Message);
    }

    [Fact]
    public async Task AConnectionThatUsesThePortMapperFindsItsHostServersThere()
    {
        await using var fake = Fake(2);
        await using var mapper = new FakePortMapper().Map("as-signon", fake.Port);
        var endpoints = new HostEndpoints(new HostServerOptions { MapperPort = mapper.Port }, new PrinterOptions(), TimeProvider.System);
        var telnet = new Target("test", "Test", false, "127.0.0.1", 23, UsePortMapper: true);

        var endpoint = await endpoints.ForAsync(telnet, HostService.SignOn, T.Token());
        Assert.Equal(fake.Port, endpoint.Port);
        await SignOnServer.VerifyAsync(Connector, endpoint, "PRTUSER", "Secret12", T.Token());

        // Remembered: the second lookup doesn't ask again, until it is forgotten.
        await endpoints.ForAsync(telnet, HostService.SignOn, T.Token());
        Assert.Equal(1, mapper.Lookups);
        endpoints.Forget(telnet, HostService.SignOn);
        await endpoints.ForAsync(telnet, HostService.SignOn, T.Token());
        Assert.Equal(2, mapper.Lookups);

        // A service the port mapper doesn't list is unreachable, not guessed at.
        var e = await Assert.ThrowsAsync<SignOnException>(() => endpoints.ForAsync(telnet, HostService.NetworkPrint, T.Token()));
        Assert.Equal(SignOnFailure.Unreachable, e.Failure);
    }

    [Fact]
    public async Task WithoutThePortMapperTheDefaultPortsAreUsed()
    {
        var endpoints = new HostEndpoints(new HostServerOptions(), new PrinterOptions(), TimeProvider.System);
        var secure = new Target("prod", "Production", true, "ibmi.example", 992);
        Assert.Equal(9476, (await endpoints.ForAsync(secure, HostService.SignOn, T.Token())).Port);
        Assert.Equal(8476, (await endpoints.ForAsync(secure with { Secure = false, Port = 23 }, HostService.SignOn, T.Token())).Port);
        Assert.Equal(9474, (await endpoints.ForAsync(secure, HostService.NetworkPrint, T.Token())).Port);
    }
}
