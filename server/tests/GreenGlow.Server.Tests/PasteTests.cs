using System.Text.Json;
using GreenGlow.Engine;
using GreenGlow.Server.Sessions;

namespace GreenGlow.Server.Tests;

/// <summary>Pasting text into a session: typed from the cursor as the user's own keys.</summary>
public sealed class PasteTests
{
    private static readonly Position At = new(6, 53);

    [Fact]
    public void CharactersFollowTheCursorAndEachLineStartsInTheSameColumnBelow()
    {
        var (keys, pastScreen) = Paste.Keys("ab\r\ncd\n", At, 24);
        Assert.False(pastScreen);
        Assert.Equal<Key>(
            [new CharacterKey("a"), new CharacterKey("b"), new CursorKey(7, 53), new CharacterKey("c"), new CharacterKey("d")],
            keys);
    }

    [Fact]
    public void TabsMoveToTheNextFieldAndNothingElseIsAControlKey()
    {
        var (keys, _) = Paste.Keys("a\tb\u0007\u001b", At, 24);
        Assert.Equal<Key>([new CharacterKey("a"), new FunctionKey("Tab"), new CharacterKey("b")], keys);
        Assert.DoesNotContain(keys, k => k is FunctionKey { Name: "Enter" });
    }

    [Fact]
    public void LinesBelowTheScreenAreLeftOut()
    {
        var (keys, pastScreen) = Paste.Keys("1\n2\n3", new Position(23, 1), 24);
        Assert.True(pastScreen);
        Assert.Equal<Key>([new CharacterKey("1"), new CursorKey(24, 1), new CharacterKey("2")], keys);
    }

    private static Task PasteAsync(BrowserClient browser, string text) =>
        browser.SendRawAsync(JsonSerializer.Serialize(new { type = "paste", text }));

    private static async Task<JsonElement> PastedAsync(BrowserClient browser, int since) =>
        await browser.WaitForAsync(m => BrowserClient.TypeOf(m) == "pasted", "the paste to finish", since);

    [Fact]
    public async Task AUserAndPasswordPastedWithATabFillTwoFields()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var scope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.ClickAsync(6, 53);
        var mark = browser.Mark;
        await PasteAsync(browser, "pat\tpw1");
        var pasted = await PastedAsync(browser, mark);
        Assert.Equal((6, 6, JsonValueKind.Null), (pasted.GetProperty("typed").GetInt32(), pasted.GetProperty("of").GetInt32(), pasted.GetProperty("stopped").ValueKind));
        // Nothing was sent: the paste never presses Enter.
        Assert.DoesNotContain(rig.Host.Inputs, i => i.Aid == 0xf1);
        mark = browser.Mark;
        await browser.PressAsync("Enter");
        await browser.WaitForTextAsync("IBM i Main Menu", mark);
        Assert.Equal("PAT", Assert.Single(rig.Host.Inputs, i => i.Aid == 0xf1).FieldAt(6, 53));
    }

    [Fact]
    public async Task APasteStopsAtTheFirstKeyTheScreenRefuses()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var scope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.ClickAsync(1, 1); // the title: protected
        var mark = browser.Mark;
        await PasteAsync(browser, "abc");
        var pasted = await PastedAsync(browser, mark);
        Assert.Equal((0, 3, "refused"), (pasted.GetProperty("typed").GetInt32(), pasted.GetProperty("of").GetInt32(), pasted.GetProperty("stopped").GetString()));
    }

    [Fact]
    public async Task LinesBelowTheScreenAreReported()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var scope = browser;
        await browser.WaitForTextAsync("Sign On");
        await browser.ClickAsync(7, 53);
        var mark = browser.Mark;
        await PasteAsync(browser, string.Join('\n', Enumerable.Range(0, 30).Select(_ => "")) + "\nx");
        var pasted = await PastedAsync(browser, mark);
        Assert.Equal("screen-end", pasted.GetProperty("stopped").GetString());
    }

    [Fact]
    public async Task AnOversizedPasteClosesTheSocket()
    {
        await using var rig = await Rig.StartAsync();
        var (_, browser) = await rig.OpenAndAttachAsync();
        await using var scope = browser;
        await browser.WaitForTextAsync("Sign On");
        await PasteAsync(browser, new string('x', Paste.MaxCharacters + 1));
        var (_, reason) = await browser.WaitClosedAsync();
        Assert.Equal("bad message", reason);
    }
}
