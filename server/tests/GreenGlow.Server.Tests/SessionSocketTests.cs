using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using GreenGlow.Server.Data;
using GreenGlow.Server.Sessions;

namespace GreenGlow.Server.Tests;

public sealed class SessionSocketTests
{
    // At most 10 characters: the fake host's fields are 10 wide and typing past the end moves on to the next field.
    private const string Password = "Zxq7731!pw";

    // ---- who may attach ----

    [Fact]
    public async Task AnonymousIs401()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectAsync(opened.Id, upn: null));
        Assert.Contains("401", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://evil.example.com")]
    [InlineData("http://localhost:5080/")]
    [InlineData("https://localhost:5080")]
    public async Task MissingOrDisallowedOriginIs403(string? origin)
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectAsync(opened.Id, origin));
        Assert.Contains("403", error.Message);
        // Nothing attached, so the session is still there for the right origin.
        Assert.Single((await rig.ListAsync()).Sessions);
    }

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://localhost:5173")]
    public async Task ConfiguredOriginsAreAccepted(string origin)
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        await using var browser = new BrowserClient(await rig.Factory.ConnectAsync(opened.Id, origin));
        await browser.WaitForTypeAsync("hello");
        Assert.Equal(WebSocketState.Open, browser.Socket.State);
    }

    [Fact]
    public async Task APlainRequestIsBadRequest()
    {
        await using var rig = await Rig.StartAsync();
        var opened = await rig.OpenOkAsync();
        var client = await rig.Factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/ws/session/{opened.Id}", T.Token())).StatusCode);
    }

    [Fact]
    public async Task AnUnknownSessionIs404()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Factory.SignInAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectAsync(Guid.NewGuid()));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task TheOldPipePathIsGone()
    {
        await using var rig = await Rig.StartAsync();
        var client = await rig.Factory.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ws/test", T.Token())).StatusCode);
    }

    // ---- the flow ----

    [Fact]
    public async Task AttachingGivesHelloControlAndTheSignOnScreen()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;

        var hello = browser.Messages[0];
        Assert.Equal("hello", BrowserClient.TypeOf(hello));
        Assert.Equal("owner", hello.GetProperty("role").GetString());
        Assert.Equal(opened.Id, hello.GetProperty("sessionId").GetGuid());
        Assert.Equal("test", hello.GetProperty("target").GetString());
        Assert.False(hello.TryGetProperty("host", out _));

        var control = await browser.WaitForTypeAsync("control");
        Assert.True(control.GetProperty("you").GetBoolean());
        Assert.Equal(1, control.GetProperty("frontends").GetInt32());

        var screen = await browser.WaitForTextAsync("Sign On");
        Assert.Equal(2, screen.InputFields);
        Assert.Contains("User", screen.Text);
        Assert.Equal(1, rig.Host.Open);
        var session = Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Equal(1, session.GetProperty("frontends").GetInt32());
    }

    [Fact]
    public async Task RawHostRecordsNeverReachTheBrowser()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        var types = browser.Messages.Where(m => BrowserClient.TypeOf(m) == "event")
            .Select(m => m.GetProperty("event").GetProperty("type").GetString()).ToList();
        Assert.DoesNotContain("record", types);
    }

    [Fact]
    public async Task SigningOnShowsTheMenuAndTheSessionListFollowsTheScreens()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        Assert.Equal("Sign On", (await rig.ListAsync()).Sessions.Single().GetProperty("title").GetString());
        Assert.False((await rig.ListAsync()).Sessions.Single().GetProperty("signedOn").GetBoolean());

        var mark = browser.Mark;
        await browser.SignOnAsync("pat", Password);
        var menu = await browser.WaitForTextAsync("IBM i Main Menu", mark);
        Assert.Equal(0, menu.InputFields == 1 ? 0 : 1);

        var session = Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Equal(opened.Id, session.GetProperty("id").GetGuid());
        Assert.Equal("IBM i Main Menu", session.GetProperty("title").GetString());
        Assert.Equal("PAT", session.GetProperty("ibmUser").GetString());
        // Each screen message carries the same title, for the page's tab.
        var message = await browser.WaitForAsync(m => BrowserClient.TypeOf(m) == "screen"
            && m.GetProperty("title").GetString() == "IBM i Main Menu", "a screen titled IBM i Main Menu", mark);
        Assert.Equal(JsonValueKind.Object, message.GetProperty("snapshot").ValueKind);
        // And whether it is past sign-on, which the server knows from the session's first screen.
        Assert.True(message.GetProperty("signedOn").GetBoolean());
        Assert.True(session.GetProperty("signedOn").GetBoolean());

        // What the host got: the user, and the password field (masked by the fake host).
        var input = Assert.Single(rig.Host.Inputs, i => i.Aid == 0xf1);
        Assert.Equal("PAT", input.FieldAt(6, 53));
        Assert.Equal(new string('*', Password.Length), input.FieldAt(7, 53));

        mark = browser.Mark;
        await browser.PressAsync("F3");
        await browser.WaitForTextAsync("Sign On", mark);
        var signedOff = await browser.WaitForAsync(m => BrowserClient.TypeOf(m) == "screen"
            && m.GetProperty("title").GetString() == "Sign On", "the sign-on screen again", mark);
        Assert.False(signedOff.GetProperty("signedOn").GetBoolean());
        session = Assert.Single((await rig.ListAsync()).Sessions);
        Assert.False(session.GetProperty("signedOn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("ibmUser").ValueKind);
        Assert.Equal("Sign On", session.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ARejectedSignOnIsNotSignedOn()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        var mark = browser.Mark;
        await browser.PressAsync("Enter");
        await browser.WaitForTextAsync("User required", mark);
        var session = Assert.Single((await rig.ListAsync()).Sessions);
        Assert.False(session.GetProperty("signedOn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("ibmUser").ValueKind);
    }

    [Fact]
    public async Task TypedPasswordsNeverReachABrowser()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.ClickAsync(7, 53);
        await browser.TypeAsync("Qz9k");
        // One placeholder per typed character, so the page can still show bullets.
        var typed = await browser.WaitForScreenAsync(v => v.Text.Split('\n')[6].Contains("****"), "four placeholders");
        Assert.DoesNotContain("Qz9k", typed.Text);
        foreach (var message in browser.Messages.Where(m => BrowserClient.TypeOf(m) == "screen"))
            Assert.DoesNotContain("Qz9k", new ScreenView(message.GetProperty("snapshot")).Text);
    }

    [Fact]
    public async Task ThePasswordIsNeverLoggedOrStored()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        var mark = browser.Mark;
        await browser.SignOnAsync("pat", Password);
        await browser.WaitForTextAsync("IBM i Main Menu", mark);

        var (raw, _) = await rig.ListAsync();
        Assert.DoesNotContain(Password, raw, StringComparison.OrdinalIgnoreCase);
        await browser.CloseAsync();
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync()).All(r => r.EndedAt is not null), "the log rows to close");

        Assert.NotEmpty(rig.Factory.Logs.Entries);
        Assert.All(rig.Factory.Logs.Entries, e =>
        {
            Assert.DoesNotContain(Password, e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Password, e.State, StringComparison.OrdinalIgnoreCase);
        });
        var strings = rig.AllDatabaseStrings();
        Assert.NotEmpty(strings);
        Assert.DoesNotContain(strings, s => s.Contains(Password, StringComparison.OrdinalIgnoreCase));
        // The typed IBM i user is not in the database either: only the log's metadata is.
        Assert.DoesNotContain(strings, s => s.Contains("PAT", StringComparison.Ordinal) && s.Length <= 10);
    }

    [Fact]
    public async Task TheLogFollowsTheSessionAndEachBrowser()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync()).Count == 2, "both log rows");
        var rows = await rig.LogRowsAsync();
        var session = Assert.Single(rows, r => r.Kind == LogKind.Session);
        var frontend = Assert.Single(rows, r => r.Kind == LogKind.Frontend);
        Assert.Equal(opened.Id, frontend.SessionId);
        Assert.Equal("owner", frontend.Role);
        Assert.Equal(RelayFactory.DefaultUpn, frontend.Upn);
        Assert.Equal(opened.Device, frontend.DeviceName);
        Assert.Null(session.EndedAt);
        Assert.Null(frontend.EndedAt);
    }

    // ---- two browsers ----

    [Fact]
    public async Task OnlyTheControllersKeysApplyAndTakeControlMovesTheKeyboard()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await first.WaitForTextAsync("Sign On");
        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await second.WaitForTextAsync("Sign On");

        // Both see who holds the keyboard.
        var firstControl = await first.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("frontends").GetInt32() == 2, "control for 2");
        Assert.True(firstControl.GetProperty("you").GetBoolean());
        var secondControl = await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control", "control");
        Assert.False(secondControl.GetProperty("you").GetBoolean());
        Assert.Equal(2, secondControl.GetProperty("frontends").GetInt32());

        // The second browser may not type.
        var mark2 = second.Mark;
        await second.PressAsync("Enter");
        var refused = await second.WaitForTypeAsync("refused", mark2);
        Assert.Equal("not-controller", refused.GetProperty("reason").GetString());
        Assert.Empty(rig.Host.Inputs);

        // It takes the keyboard; the first learns it lost it.
        var mark1 = first.Mark;
        mark2 = second.Mark;
        await second.TakeControlAsync();
        Assert.True((await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "control gained", mark2)).GetProperty("you").GetBoolean());
        Assert.False((await first.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && !m.GetProperty("you").GetBoolean(), "control lost", mark1)).GetProperty("you").GetBoolean());

        // Now the first is refused and the second's keys reach the host; both see the new screen.
        mark1 = first.Mark;
        await first.PressAsync("Enter");
        await first.WaitForTypeAsync("refused", mark1);
        Assert.Empty(rig.Host.Inputs);
        mark1 = first.Mark;
        mark2 = second.Mark;
        await second.SignOnAsync("pat", Password);
        await first.WaitForTextAsync("IBM i Main Menu", mark1);
        await second.WaitForTextAsync("IBM i Main Menu", mark2);
        Assert.Single(rig.Host.Inputs);
    }

    [Fact]
    public async Task WhenTheControllerLeavesTheOtherOwnerGetsTheKeyboardAndTheSessionGoesOn()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await first.WaitForTextAsync("Sign On");
        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && !m.GetProperty("you").GetBoolean(), "control for 2nd");

        var mark = second.Mark;
        await first.CloseAsync();
        var control = await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("you").GetBoolean(), "control gained", mark);
        Assert.Equal(1, control.GetProperty("frontends").GetInt32());
        Assert.Single((await rig.ListAsync()).Sessions);
        Assert.Equal(1, rig.Host.Open);
        Assert.Equal(WebSocketState.Open, second.Socket.State);
    }

    // ---- bad input ----

    public static TheoryData<string, string> BadMessages => new()
    {
        { "this is not json", "bad message" },
        { "[]", "bad message" },
        { "42", "bad message" },
        { """{"key":{"type":"character","char":"a"}}""", "bad message" },
        { """{"type":"unknown"}""", "bad message" },
        { """{"type":5}""", "bad message" },
        { """{"type":"key","key":{"type":7}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":null,"column":null}}""", "bad message" },
        { """{"type":"key"}""", "bad message" },
        { """{"type":"key","key":"Enter"}""", "bad message" },
        { """{"type":"key","key":{"type":"function","name":"NotAKey"}}""", "bad message" },
        { """{"type":"key","key":{"type":"function"}}""", "bad message" },
        { """{"type":"key","key":{"type":"character","char":"ab"}}""", "bad message" },
        { """{"type":"key","key":{"type":"character","char":""}}""", "bad message" },
        { """{"type":"key","key":{"type":"character","char":"\u0007"}}""", "bad message" },
        { """{"type":"key","key":{"type":"character","char":"\n"}}""", "bad message" },
        { """{"type":"key","key":{"type":"character","char":5}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":0,"column":1}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":29,"column":1}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":1,"column":133}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":1,"column":0}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":"1","column":1}}""", "bad message" },
        { """{"type":"key","key":{"type":"cursor","row":1.5,"column":1}}""", "bad message" },
        { """{"type":"key","key":{"type":"mystery"}}""", "bad message" },
    };

    [Theory]
    [MemberData(nameof(BadMessages))]
    public async Task ABadMessageClosesTheSocketAndEndsTheSession(string message, string reason)
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.SendRawAsync(message);
        var (status, closeReason) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal(reason, closeReason);
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
        Assert.Empty(rig.Host.Inputs);
    }

    [Fact]
    public async Task ABinaryFrameClosesTheSocketAndEndsTheSession()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.SendRawAsync("""{"type":"take-control"}""", WebSocketMessageType.Binary);
        var (_, reason) = await browser.WaitClosedAsync();
        Assert.Equal("binary frames are not accepted", reason);
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
    }

    [Fact]
    public async Task AnOversizedMessageClosesTheSocket()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.SendRawAsync(new string(' ', SessionEndpoints.MaxClientMessage) + """{"type":"take-control"}""");
        var (_, reason) = await browser.WaitClosedAsync();
        Assert.Equal("message too big", reason);
        await rig.WaitNoSessionsAsync();
    }

    [Fact]
    public async Task ABadMessageFromOneBrowserLeavesTheOtherAttached()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, first) = await rig.OpenAndAttachAsync();
        await using var firstScope = first;
        await using var second = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
        await second.WaitForTypeAsync("hello");
        await second.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("frontends").GetInt32() == 2, "both attached");
        await second.SendRawAsync("garbage");
        await second.WaitClosedAsync();
        var control = await first.WaitForAsync(m => BrowserClient.TypeOf(m) == "control" && m.GetProperty("frontends").GetInt32() == 1, "one left", 1);
        Assert.True(control.GetProperty("you").GetBoolean());
        Assert.Single((await rig.ListAsync()).Sessions);
    }

    // ---- endings ----

    [Fact]
    public async Task TheBrowserClosingEndsTheSessionAndTheLogRows()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        Assert.Equal(1, rig.Host.Open);

        await browser.CloseAsync();
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync()).Count(r => r.EndedAt is not null) == 2, "both log rows to close");
        var rows = await rig.LogRowsAsync();
        var session = Assert.Single(rows, r => r.Kind == LogKind.Session);
        var frontend = Assert.Single(rows, r => r.Kind == LogKind.Frontend);
        Assert.Equal(opened.Id, session.SessionId);
        Assert.Equal("no browser attached", session.EndReason);
        Assert.Equal("browser closed", frontend.EndReason);
        Assert.True(session.EndedAt >= session.StartedAt);
        Assert.True(frontend.EndedAt >= frontend.StartedAt);
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync(opened.Id));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Factory.ConnectAsync(opened.Id));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task TheHostClosingTellsTheBrowserAndClosesNormally()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        await rig.Host.CloseAllAsync();

        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        var closed = BrowserClient.TypeOf(browser.Messages[^1]);
        Assert.Equal("closed", closed);
        Assert.Equal("host closed", browser.Messages[^1].GetProperty("reason").GetString());
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync()).All(r => r.EndedAt is not null), "the log rows to close");
        Assert.Equal("host closed", (await rig.LogRowsAsync(LogKind.Session)).Single().EndReason);
    }

    [Fact]
    public async Task SigningOffWithOption90ClosesTheSession()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        await browser.WaitForTextAsync("Sign On");
        var mark = browser.Mark;
        await browser.SignOnAsync("pat", Password);
        await browser.WaitForTextAsync("IBM i Main Menu", mark);
        await browser.ClickAsync(20, 7);
        await browser.TypeAsync("90");
        await browser.PressAsync("Enter");

        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal("closed", BrowserClient.TypeOf(browser.Messages[^1]));
        Assert.Equal("host closed", browser.Messages[^1].GetProperty("reason").GetString());
        await rig.WaitNoSessionsAsync();
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
    }

    [Fact]
    public async Task DeletingAttachedSessionTellsTheBrowser()
    {
        await using var rig = await Rig.StartAsync();
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync(opened.Id));
        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal("closed", BrowserClient.TypeOf(browser.Messages[^1]));
        Assert.Equal("ended by the user", browser.Messages[^1].GetProperty("reason").GetString());
        await Wait.UntilAsync(() => rig.Host.Open == 0, "the host connection to drop");
    }

    [Fact]
    public async Task AnUnusedPortEndsTheSessionWithAHostError()
    {
        await using var rig = await Rig.StartAsync(targets: _ => $"test=tcp://127.0.0.1:{T.FreePort()}");
        await ExpectHostErrorAsync(rig, "host error ConnectionRefused");
    }

    [Fact]
    public async Task AHostThatResetsTheConnectionEndsTheSessionWithAHostError()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new Proxy(1, gate: gate.Task, reset: true);
        await using var rig = await Rig.StartAsync(targets: _ => $"test=tcp://127.0.0.1:{proxy.Port}");
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        gate.SetResult();
        var closed = await browser.WaitForTypeAsync("closed");
        Assert.StartsWith("host error", closed.GetProperty("reason").GetString());
        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        await rig.WaitNoSessionsAsync();
    }

    /// <summary>
    /// The connection fails within milliseconds, so the browser may attach too late to hear
    /// of it. Either way the log has the reason, and a browser that did get in is told.
    /// </summary>
    internal static async Task ExpectHostErrorAsync(Rig rig, string reason, string target = "test")
    {
        var opened = await rig.OpenOkAsync(target);
        try
        {
            await using var browser = await BrowserClient.AttachAsync(rig.Factory, opened.Id);
            try
            {
                var closed = await browser.WaitForTypeAsync("closed");
                Assert.Equal(reason, closed.GetProperty("reason").GetString());
            }
            catch (TimeoutException) when (browser.Ended)
            {
                // Attached after the session ended: the socket was closed without a message.
            }
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            // Too late to attach at all (404).
        }
        await Wait.UntilAsync(async () => (await rig.LogRowsAsync(LogKind.Session)).Single().EndedAt is not null, "the session row to close");
        Assert.Equal(reason, (await rig.LogRowsAsync(LogKind.Session)).Single().EndReason);
        await rig.WaitNoSessionsAsync();
    }

    // ---- device in use ----

    [Fact]
    public async Task ADeviceInUseIsReportedToTheBrowserAndTheSessionCloses()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await GreenGlow.FakeHost.FakeIbmI.StartAsync();
        await using var _host = host;
        await using var proxy = new Proxy(host.Port, gate: gate.Task);
        using var factory = RelayFactory.For($"test=tcp://127.0.0.1:{proxy.Port}");
        var rig = Rig.Wrap(host, factory);

        await using var holder = await HostHolder.ConnectAsync(host, "GG1001");
        var (opened, browser) = await rig.OpenAndAttachAsync();
        await using var browserScope = browser;
        Assert.Equal("GG1001", opened.Device);
        gate.SetResult();

        var inUse = await browser.WaitForAsync(m => BrowserClient.TypeOf(m) == "event"
            && m.GetProperty("event").GetProperty("type").GetString() == "device-in-use", "a device-in-use event");
        Assert.Equal("GG1001", inUse.GetProperty("event").GetProperty("deviceName").GetString());
        await browser.WaitForTypeAsync("closed");
        var (status, _) = await browser.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        await rig.WaitNoSessionsAsync();
        Assert.Equal(1, host.Open);
    }
}
