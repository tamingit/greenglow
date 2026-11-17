using GreenGlow.Server.Configuration;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Tests;

/// <summary>
/// The IBM i file server (as-file) on a real test system. Runs only when E2E_IBMI_TARGET,
/// E2E_IBMI_USER and E2E_IBMI_PASSWORD are set (the same variables as the browser live tests);
/// otherwise each test does nothing. The user name and password are used for the sign-on and
/// are never printed or written anywhere. Only system files are read.
/// </summary>
public sealed class FileServerLiveTests
{
    /// <summary>A PASE program every IBM i with PASE has: an XCOFF executable.</summary>
    private const string SystemFile = "/QOpenSys/usr/bin/ls";

    private static async Task<FileServerClient?> ConnectAsync(CancellationToken cancel)
    {
        var target = Environment.GetEnvironmentVariable("E2E_IBMI_TARGET");
        var user = Environment.GetEnvironmentVariable("E2E_IBMI_USER");
        var password = Environment.GetEnvironmentVariable("E2E_IBMI_PASSWORD");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("file server live test skipped: E2E_IBMI_* not set");
            return null;
        }
        var endpoints = new HostEndpoints(new HostServerOptions(), new PrinterOptions(), TimeProvider.System);
        return await FileServerClient.ConnectAsync(HostConnector.Create(""), endpoints,
            TargetParser.ParseUrl("live", "live", target), user, password, cancel);
    }

    [Fact]
    public async Task ASystemFileDownloadsWhole()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cancel.Token);
        if (client is null) return;
        Console.WriteLine($"datastream level {client.DatastreamLevel}, CCSID {client.Ccsid}");

        var opened = await client.OpenForReadAsync(SystemFile, cancel.Token);
        await client.CloseAsync(opened.Handle, cancel.Token);
        Assert.True(opened.Size > 0);

        using var copy = new MemoryStream();
        var copied = await client.DownloadAsync(SystemFile, copy, cancel.Token);
        Console.WriteLine($"downloaded {copied} bytes of {opened.Size}");
        Assert.Equal(opened.Size, copied);
        Assert.Equal(copied, copy.Length);
        // XCOFF64's magic number.
        Assert.Equal([0x01, 0xF7], copy.ToArray()[..2]);
    }

    [Fact]
    public async Task FoldersListWithTheirFoldersAndFiles()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cancel.Token);
        if (client is null) return;

        var usr = await client.ListAsync("/QOpenSys/usr", 0, cancel.Token);
        Assert.Contains(usr, e => e.Name == "bin" && e.Folder);
        Assert.DoesNotContain(usr, e => e.Name is "." or "..");

        var bin = await client.ListAsync("/QOpenSys/usr/bin/", 0, cancel.Token);
        var ls = Assert.Single(bin, e => e.Name == "ls");
        Assert.False(ls.Folder);
        Assert.True(ls.Modified.Year > 1990);
        Console.WriteLine($"/QOpenSys/usr has {usr.Count} entries, /QOpenSys/usr/bin {bin.Count}");

        var ten = await client.ListAsync("/QOpenSys/usr/bin", 10, cancel.Token);
        Assert.Equal(10, ten.Count);

        var missing = await Assert.ThrowsAsync<FileServerException>(() => client.ListAsync("/greenglow-no-such-folder", 0, cancel.Token));
        Assert.Equal(3, missing.Code);

        // The connection is still in step after the chains.
        var opened = await client.OpenForReadAsync(SystemFile, cancel.Token);
        await client.CloseAsync(opened.Handle, cancel.Token);
    }

    [Fact]
    public async Task AMissingFileOrFolderIsRefusedWithItsCode()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cancel.Token);
        if (client is null) return;

        var noFolder = await Assert.ThrowsAsync<FileServerException>(() =>
            client.OpenForReadAsync("/greenglow-no-such-folder/nothing.txt", cancel.Token));
        var noFile = await Assert.ThrowsAsync<FileServerException>(() =>
            client.OpenForReadAsync("/QOpenSys/usr/bin/greenglow-no-such-file", cancel.Token));
        var folder = await Assert.ThrowsAsync<FileServerException>(() =>
            client.OpenForReadAsync("/QOpenSys/usr/bin", cancel.Token));
        Console.WriteLine($"no folder {noFolder.Code}, no file {noFile.Code}, a folder {folder.Code}");
        Assert.Equal(3, noFolder.Code);
        Assert.Equal(2, noFile.Code);
        Assert.Equal(4, folder.Code);

        // The connection still works after refusals.
        var opened = await client.OpenForReadAsync(SystemFile, cancel.Token);
        await client.CloseAsync(opened.Handle, cancel.Token);
    }
}
