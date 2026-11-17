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
    public async Task FilesUploadIntoNewFoldersReadBackAndAreCleanedUp()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var client = await ConnectAsync(cancel.Token);
        if (client is null) return;

        // Everything happens in a folder of its own under /tmp, and is deleted after.
        var root = "/tmp/greenglow-test-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var file = root + "/sub/deeper/data.bin";
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(150_000);
        try
        {
            await client.EnsureFolderAsync(root + "/sub/deeper", cancel.Token);
            await client.EnsureFolderAsync(root + "/sub", cancel.Token);
            Assert.Equal(data.Length, await client.UploadAsync(file, new MemoryStream(data), replace: false, 1208, cancel.Token));

            using (var back = new MemoryStream())
            {
                await client.DownloadAsync(file, back, cancel.Token);
                Assert.Equal(data, back.ToArray());
            }
            var listed = Assert.Single(await client.ListAsync(root + "/sub/deeper", 0, cancel.Token));
            Assert.Equal(("data.bin", false, (long)data.Length), (listed.Name, listed.Folder, listed.Size));

            var there = await Assert.ThrowsAsync<FileServerException>(() =>
                client.UploadAsync(file, new MemoryStream(data[..10]), replace: false, 1208, cancel.Token));
            Assert.Equal(FileServerClient.AlreadyThere, there.Code);
            Assert.Equal(10, await client.UploadAsync(file, new MemoryStream(data[..10]), replace: true, 1208, cancel.Token));
            var replaced = await client.OpenForReadAsync(file, cancel.Token);
            await client.CloseAsync(replaced.Handle, cancel.Token);
            Assert.Equal(10, replaced.Size);

            var noFolder = await Assert.ThrowsAsync<FileServerException>(() =>
                client.UploadAsync(root + "/missing/x.bin", new MemoryStream(data[..10]), replace: false, 1208, cancel.Token));
            Assert.Equal(FileServerClient.NoSuchFolder, noFolder.Code);
            var notEmpty = await Assert.ThrowsAsync<FileServerException>(() => client.DeleteFolderAsync(root + "/sub", cancel.Token));
            Console.WriteLine($"uploaded {data.Length} bytes; deleting a folder that isn't empty: {notEmpty.Code}");
        }
        finally
        {
            foreach (var path in new[] { file })
                try { await client.DeleteFileAsync(path, cancel.Token); } catch (FileServerException) { }
            foreach (var path in new[] { root + "/sub/deeper", root + "/sub", root })
                try { await client.DeleteFolderAsync(path, cancel.Token); } catch (FileServerException) { }
        }
        var gone = await Assert.ThrowsAsync<FileServerException>(() => client.ListAsync(root, 0, cancel.Token));
        Assert.Equal(FileServerClient.NoSuchFolder, gone.Code);
    }

    [Fact]
    public async Task FilesAndFoldersRenameAndATreeDeletes()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var client = await ConnectAsync(cancel.Token);
        if (client is null) return;

        var root = "/tmp/greenglow-test-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        try
        {
            await client.EnsureFolderAsync(root + "/a/b", cancel.Token);
            await client.UploadAsync(root + "/a/one.txt", new MemoryStream("placeholder"u8.ToArray()), false, 1208, cancel.Token);
            await client.UploadAsync(root + "/a/b/two.txt", new MemoryStream("placeholder"u8.ToArray()), false, 1208, cancel.Token);
            await client.UploadAsync(root + "/a/three.txt", new MemoryStream("x"u8.ToArray()), false, 1208, cancel.Token);

            await client.RenameAsync(root + "/a/one.txt", root + "/a/uno.txt", cancel.Token);
            await client.RenameAsync(root + "/a/b", root + "/a/bee", cancel.Token);
            var taken = await Assert.ThrowsAsync<FileServerException>(() =>
                client.RenameAsync(root + "/a/three.txt", root + "/a/uno.txt", cancel.Token));
            Console.WriteLine($"renaming onto a name in use: {taken.Code}");
            Assert.Equal(FileServerClient.AlreadyThere, taken.Code);
            var names = (await client.ListAsync(root + "/a", 0, cancel.Token)).Select(e => e.Name).Order().ToList();
            Assert.Equal(["bee", "three.txt", "uno.txt"], names);

            // An empty folder lists as empty, not as missing.
            await client.CreateFolderAsync(root + "/a/empty", cancel.Token);
            Assert.Empty(await client.ListAsync(root + "/a/empty", 0, cancel.Token));

            var made = await Assert.ThrowsAsync<FileServerException>(() => client.CreateFolderAsync(root + "/a", cancel.Token));
            Assert.Equal(FileServerClient.AlreadyThere, made.Code);
            Assert.Equal(7, await client.DeleteTreeAsync(root, cancel.Token));
        }
        finally
        {
            try { await client.DeleteTreeAsync(root, cancel.Token); } catch (FileServerException) { }
        }
        Assert.Equal(FileServerClient.NoSuchFolder,
            (await Assert.ThrowsAsync<FileServerException>(() => client.ListAsync(root, 0, cancel.Token))).Code);
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
