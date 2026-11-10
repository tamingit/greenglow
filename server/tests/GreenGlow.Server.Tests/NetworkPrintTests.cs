using GreenGlow.FakeHost;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Printing;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

/// <summary>Listing and reading the user's spooled files over the Network Print server (the fake's).</summary>
public sealed class NetworkPrintTests
{
    private static Task<NetworkPrintClient> ConnectAsync(FakeNetworkPrint fake) =>
        NetworkPrintClient.ConnectAsync(new HostConnector(null), new Target("test", "Test", false, "127.0.0.1", fake.Port),
            FakeNetworkPrint.DefaultUser, FakeNetworkPrint.DefaultPassword, T.Token());

    [Fact]
    public async Task ListsTheUsersSpooledFilesWithTheirAttributes()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        await using var client = await ConnectAsync(fake);
        Assert.Equal(37, client.ServerCcsid);
        Assert.Equal("PRTUSER", client.User);

        var files = await client.ListAsync(T.Token());
        Assert.Equal(["QSYSPRT", "QPRTAFP", "QPDFOUT"], files.Select(f => f.Name));
        var report = files[0];
        Assert.Equal(1, report.Number);
        Assert.Equal(("QPADEV0001", "PRTUSER", "123456", "FAKEIBMI"), (report.JobName, report.JobUser, report.JobNumber, report.JobSystem));
        Assert.Equal(new DateTime(2026, 10, 12, 9, 30, 0), report.Created);
        Assert.Equal(("*READY", 2, "PLACEHOLDR"), (report.Status, report.Pages, report.UserData));
        Assert.Equal(("*SCS", "QUSRSYS/QPRINT", "*STD"), (report.DeviceType, report.OutputQueue, report.FormType));
        Assert.Equal(["*SCS", "*AFPDS", "*USERASCII"], files.Select(f => f.DeviceType));
    }

    [Fact]
    public async Task NoSpooledFilesIsAnEmptyList()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.Files.Clear();
        await using var client = await ConnectAsync(fake);
        Assert.Empty(await client.ListAsync(T.Token()));
    }

    [Fact]
    public async Task ReadsAWholeSpooledFileInChunksAndClosesIt()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.ReadChunk = 1000;
        await using var client = await ConnectAsync(fake);
        var files = await client.ListAsync(T.Token());

        var data = await client.ReadAllAsync(files[0], T.Token());
        Assert.Equal(FakeSpooledFile.WideReport(), data);
        Assert.Equal(1, fake.Closes);

        var pdf = SpooledPdf.From(files[0].DeviceType, data);
        Assert.NotNull(pdf);
        var document = Printing.Scs.Render(data);
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(132, document.Columns);
    }

    [Fact]
    public async Task APdfSpooledFilePassesThroughAndAfpCannotBeOpened()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        await using var client = await ConnectAsync(fake);
        var files = await client.ListAsync(T.Token());

        var pdf = await client.ReadAllAsync(files[2], T.Token());
        Assert.Same(pdf, SpooledPdf.From(files[2].DeviceType, pdf));
        Assert.Null(SpooledPdf.From(files[1].DeviceType, await client.ReadAllAsync(files[1], T.Token())));
    }

    [Fact]
    public async Task ACpfMessageIsShownAsTheIbmISaidIt()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        fake.Files[0] = fake.Files[0] with { Unauthorized = true };
        await using var client = await ConnectAsync(fake);
        var files = await client.ListAsync(T.Token());
        var e = await Assert.ThrowsAsync<PrintServerException>(() => client.ReadAllAsync(files[0], T.Token()));
        Assert.StartsWith("CPF3303: ", e.Message);
        Assert.False(e.Lost);
    }

    [Fact]
    public async Task ASpooledFileDeletedSinceTheListIsSaidSo()
    {
        await using var fake = await FakeNetworkPrint.StartAsync();
        await using var client = await ConnectAsync(fake);
        var files = await client.ListAsync(T.Token());
        fake.Files.RemoveAt(0);
        var e = await Assert.ThrowsAsync<PrintServerException>(() => client.ReadAllAsync(files[0], T.Token()));
        Assert.Contains("no longer exists", e.Message);

        // The connection is still good.
        Assert.Equal(2, (await client.ListAsync(T.Token())).Count);
    }

    [Fact]
    public async Task ALostConnectionIsSaidSo()
    {
        var fake = await FakeNetworkPrint.StartAsync();
        await using var client = await ConnectAsync(fake);
        await fake.DisposeAsync();
        var e = await Assert.ThrowsAsync<PrintServerException>(() => client.ListAsync(T.Token()));
        Assert.True(e.Lost);
    }

    [Fact]
    public void AttributesRoundTripAndEntriesOutOfRangeAreLeftOut()
    {
        var data = NpAttributes.Encode([(NpAttribute.JobUser, "*CURRENT"), (NpAttribute.NumberOfBytes, 65536)]);
        var decoded = NpAttributes.Decode(data);
        Assert.Equal("*CURRENT", decoded[NpAttribute.JobUser]);
        Assert.Equal(65536, decoded[NpAttribute.NumberOfBytes]);

        // The second entry's value offset points past the end.
        data[4 + 12 + 8 + 3] = 0xFF;
        decoded = NpAttributes.Decode(data);
        Assert.Single(decoded);
        Assert.Empty(NpAttributes.Decode([0, 1]));
    }

    [Theory]
    [InlineData("1261012", "093000", "2026-10-12T09:30:00")]
    [InlineData("0991231", "235959", "1999-12-31T23:59:59")]
    [InlineData("1261012", "", "2026-10-12T00:00:00")]
    [InlineData("261012", "093000", null)]
    [InlineData("1261399", "093000", null)]
    public void CreationDatesAreCyymmdd(string date, string time, string? expected)
    {
        var created = NetworkPrintClient.Created(date, time);
        Assert.Equal(expected, created?.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }
}
