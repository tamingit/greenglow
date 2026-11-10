using System.Text.Json;
using GreenGlow.Engine;
using GreenGlow.Server.Sessions;

namespace GreenGlow.Server.Tests;

/// <summary>Builds synthetic 24x80 screens with placeholder text.</summary>
internal sealed class ScreenBuilder
{
    public const int Columns = 80;
    private readonly byte[] _buffer = new byte[24 * Columns];
    private readonly List<SnapshotField> _fields = [];
    public KeyboardState Keyboard { get; set; } = KeyboardState.Unlocked;

    /// <summary>Text at a 1-based position.</summary>
    public ScreenBuilder Text(int row, int column, string text)
    {
        Ebcdic.Encode(text).CopyTo(_buffer, (row - 1) * Columns + column - 1);
        return this;
    }

    /// <summary>An attribute byte at a 1-based position; the text after it has this attribute.</summary>
    public ScreenBuilder Attribute(int row, int column, byte attribute)
    {
        _buffer[(row - 1) * Columns + column - 1] = attribute;
        return this;
    }

    /// <summary>Bright text, with its attribute byte just before it and a normal attribute after it.</summary>
    public ScreenBuilder Bright(int row, int column, string text) =>
        Attribute(row, column - 1, 0x22).Text(row, column, text).Attribute(row, column + text.Length, 0x20);

    /// <summary>An input field whose attribute byte is just before it, with its typed contents.</summary>
    public ScreenBuilder Field(int row, int column, int length, byte attribute, string contents = "", bool bypass = false)
    {
        var ffw = bypass ? 0x6000 : 0x4000;
        Attribute(row, column - 1, attribute);
        Text(row, column, contents);
        _fields.Add(new SnapshotField(row, column, length, ffw, [], attribute, FieldFormat.Decode(ffw)));
        return this;
    }

    public ScreenSnapshot Build() => new(
        24, Columns, _buffer, Display.AttributesInEffect(_buffer), _fields, new Position(1, 1), Keyboard,
        Keyboard != KeyboardState.Unlocked, null, false, false, 0, false, 24, null, null);
}

public sealed class ScreenFactsTests
{
    [Fact]
    public void ABrightRow1TitleWinsOverOtherText()
    {
        var screen = new ScreenBuilder()
            .Text(1, 2, "PGM100")
            .Bright(1, 30, "Placeholder Entry")
            .Text(1, 62, "Sat Oct 6 2026")
            .Build();
        Assert.Equal("Placeholder Entry", ScreenFacts.Title(screen));
    }

    [Fact]
    public void TheLongestBrightSegmentIsTheTitle()
    {
        var screen = new ScreenBuilder().Bright(1, 5, "Hi").Bright(1, 30, "Placeholder Master Maintenance").Build();
        Assert.Equal("Placeholder Master Maintenance", ScreenFacts.Title(screen));
    }

    [Fact]
    public void ACentredTitleIsUsedWhenNothingIsBright()
    {
        var screen = new ScreenBuilder()
            .Text(1, 2, "PGM200")
            .Text(1, 33, "Placeholder Inquiry")
            .Text(1, 70, "10/06/26")
            .Build();
        Assert.Equal("Placeholder Inquiry", ScreenFacts.Title(screen));
    }

    [Fact]
    public void AProgramIdAtTheStartOfRow1IsSkipped()
    {
        var screen = new ScreenBuilder().Text(1, 2, "PGMLIST").Text(1, 12, "Work with Placeholders").Build();
        Assert.Equal("Work with Placeholders", ScreenFacts.Title(screen));
    }

    [Fact]
    public void ASlashedProgramIdIsSkippedToo()
    {
        var screen = new ScreenBuilder().Text(1, 2, "LIB/PGM1").Bright(1, 30, "Print Queue").Build();
        Assert.Equal("Print Queue", ScreenFacts.Title(screen));
    }

    [Fact]
    public void ALoneProgramIdLookingWordIsStillTheTitleWhenNothingElseIsOnRow1()
    {
        // No other text on the row: an all-capitals word is a title, not an ID.
        var screen = new ScreenBuilder().Text(1, 2, "SIGNON").Build();
        Assert.Equal("SIGNON", ScreenFacts.Title(screen));
    }

    [Fact]
    public void WithoutAClearTitleTheFirstRowWithTextIsUsed()
    {
        var screen = new ScreenBuilder().Text(3, 10, "Press   Enter   to continue").Build();
        Assert.Equal("Press Enter to continue", ScreenFacts.Title(screen));
    }

    [Fact]
    public void AnAmbiguousRow1FallsBackToTheFirstRowWithText()
    {
        var screen = new ScreenBuilder().Text(1, 2, "Left words here").Text(1, 50, "Right words here").Build();
        Assert.Equal("Left words here Right words here", ScreenFacts.Title(screen));
    }

    [Fact]
    public void ABlankScreenHasNoTitle() => Assert.Equal("", ScreenFacts.Title(new ScreenBuilder().Build()));

    [Fact]
    public void NondisplayTextIsNeverATitle()
    {
        var screen = new ScreenBuilder().Attribute(1, 29, 0x27).Text(1, 30, "Hidden Secret Words").Build();
        Assert.DoesNotContain("Hidden", ScreenFacts.Title(screen));
    }

    [Fact]
    public void LayoutIsTheSizeAndTheFieldPositionsNeverTheirContents()
    {
        var screen = new ScreenBuilder().Field(6, 53, 10, 0x24, "typed").Field(7, 53, 10, 0x27, "secret").Build();
        Assert.Equal("24x80 6:53:10 7:53:10", ScreenFacts.Layout(screen));
    }
}

public sealed class SignOnWatchTests
{
    private static ScreenBuilder SignOn(string user = "", string password = "", KeyboardState keyboard = KeyboardState.Unlocked) =>
        new ScreenBuilder { Keyboard = keyboard }
            .Bright(1, 34, "Sign On")
            .Field(6, 53, 10, 0x24, user)
            .Field(7, 53, 10, 0x27, password);

    private static ScreenSnapshot Menu(KeyboardState keyboard = KeyboardState.Unlocked) =>
        new ScreenBuilder { Keyboard = keyboard }.Bright(1, 33, "Main Menu").Field(20, 7, 60, 0x24).Build();

    [Fact]
    public void NothingIsKnownBeforeAScreenWithInput()
    {
        var watch = new SignOnWatch();
        watch.See(new ScreenBuilder().Text(1, 1, "Hello").Build());
        Assert.False(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void ALockedScreenIsNotTheSignOnScreen()
    {
        var watch = new SignOnWatch();
        watch.See(SignOn(keyboard: KeyboardState.Locked).Build());
        watch.See(Menu());
        // The menu was the first screen to take input, so it is the layout to compare against.
        Assert.False(watch.SignedOn);
    }

    [Fact]
    public void TheUserIsCapturedOnlyAfterLeavingTheSignOnLayout()
    {
        var watch = new SignOnWatch();
        watch.See(SignOn().Build());
        Assert.False(watch.SignedOn);
        watch.BeforeAid(SignOn("pat", "SECRET").Build());
        Assert.Null(watch.IbmUser);
        Assert.False(watch.SignedOn);

        watch.See(SignOn("pat", "SECRET").Build());
        Assert.Null(watch.IbmUser);

        watch.See(Menu());
        Assert.True(watch.SignedOn);
        Assert.Equal("PAT", watch.IbmUser);
    }

    [Fact]
    public void ALockedScreenAfterEnterDoesNotCountAsSignedOnOrLoseTheUser()
    {
        var watch = new SignOnWatch();
        watch.See(SignOn().Build());
        watch.BeforeAid(SignOn("pat", "x").Build());
        watch.See(Menu());
        watch.See(SignOn(keyboard: KeyboardState.Locked).Build());
        Assert.Equal("PAT", watch.IbmUser);
    }

    [Fact]
    public void ReturningToSignOnClearsTheUser()
    {
        var watch = new SignOnWatch();
        watch.See(SignOn().Build());
        watch.BeforeAid(SignOn("pat", "x").Build());
        watch.See(Menu());
        Assert.Equal("PAT", watch.IbmUser);

        watch.See(SignOn().Build());
        Assert.False(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void AFailedSignOnThatShowsTheSameScreenIsNotSignedOn()
    {
        var watch = new SignOnWatch();
        watch.See(SignOn().Build());
        watch.BeforeAid(SignOn("pat", "wrong").Build());
        watch.See(SignOn("pat", "").Build());
        Assert.False(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void ANondisplayFieldIsNeverRead()
    {
        var watch = new SignOnWatch();
        // A layout whose only input field is nondisplay: there is no user to read.
        var passwordOnly = () => new ScreenBuilder().Field(7, 53, 10, 0x27, "SECRET");
        watch.See(passwordOnly().Build());
        watch.BeforeAid(passwordOnly().Build());
        watch.See(Menu());
        Assert.True(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void ThePasswordIsNotTheUserEvenWhenItComesFirst()
    {
        var watch = new SignOnWatch();
        var screen = (string password, string user) => new ScreenBuilder()
            .Field(5, 53, 10, 0x27, password).Field(6, 53, 10, 0x24, user);
        watch.See(screen("", "").Build());
        watch.BeforeAid(screen("SECRET", "pat").Build());
        watch.See(Menu());
        Assert.Equal("PAT", watch.IbmUser);
    }

    [Fact]
    public void ABypassFieldIsSkipped()
    {
        var watch = new SignOnWatch();
        var screen = (string user) => new ScreenBuilder()
            .Field(4, 53, 10, 0x24, "SYSTEM01", bypass: true).Field(6, 53, 10, 0x24, user);
        watch.See(screen("").Build());
        watch.BeforeAid(screen("pat").Build());
        watch.See(Menu());
        Assert.Equal("PAT", watch.IbmUser);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyUserIsNotKept(string typed)
    {
        var watch = new SignOnWatch();
        watch.See(SignOn().Build());
        watch.BeforeAid(SignOn(typed, "x").Build());
        watch.See(Menu());
        Assert.True(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void AnOverlongUserIsNotKept()
    {
        var watch = new SignOnWatch();
        var wide = (string user) => new ScreenBuilder().Field(6, 53, 20, 0x24, user);
        watch.See(wide("").Build());
        watch.BeforeAid(wide("TOOLONGUSER1").Build());
        watch.See(Menu());
        Assert.True(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }

    [Fact]
    public void AnAidOnAnotherScreenOrBeforeTheSignOnScreenIsIgnored()
    {
        var watch = new SignOnWatch();
        watch.BeforeAid(SignOn("pat", "x").Build());
        watch.See(SignOn().Build());
        var menu = new ScreenBuilder().Field(20, 7, 60, 0x24, "wrkactjob").Build();
        watch.BeforeAid(menu);
        watch.See(Menu());
        Assert.True(watch.SignedOn);
        Assert.Null(watch.IbmUser);
    }
}

public sealed class OutboxTests
{
    private static async Task<List<string>> DrainAsync(Outbox outbox, int max = 10_000)
    {
        var all = new List<string>();
        while (all.Count < max && await outbox.NextAsync(T.Token()) is { } message) all.Add(message);
        return all;
    }

    [Fact]
    public async Task MessagesComeOutInOrder()
    {
        var outbox = new Outbox();
        outbox.Post("a");
        outbox.Post("b");
        outbox.Close("end");
        Assert.Equal(["a", "b", "end"], await DrainAsync(outbox));
    }

    [Fact]
    public async Task ANewerScreenReplacesAnUnsentOneInPlaceAndKeepsTheOrderOfOthers()
    {
        var outbox = new Outbox();
        outbox.Post("a");
        outbox.PostScreen("s1");
        outbox.Post("b");
        outbox.PostScreen("s2");
        outbox.PostScreen("s3");
        outbox.Post("c");
        outbox.Close("end");
        Assert.Equal(["a", "s3", "b", "c", "end"], await DrainAsync(outbox));
    }

    [Fact]
    public async Task AScreenAfterOneWasSentQueuesAfresh()
    {
        var outbox = new Outbox();
        outbox.PostScreen("s1");
        outbox.Post("a");
        Assert.Equal("s1", await outbox.NextAsync(T.Token()));
        outbox.PostScreen("s2");
        outbox.PostScreen("s3");
        outbox.Close("end");
        Assert.Equal(["a", "s3", "end"], await DrainAsync(outbox));
    }

    [Fact]
    public async Task ManyScreensNeverOverflow()
    {
        var outbox = new Outbox();
        for (var i = 0; i < Outbox.MaxPending * 4; i++) outbox.PostScreen($"s{i}");
        Assert.False(outbox.Overflowed);
        outbox.Close("end");
        Assert.Equal([$"s{Outbox.MaxPending * 4 - 1}", "end"], await DrainAsync(outbox));
    }

    [Fact]
    public async Task AFullQueueOfOtherMessagesIsAllowedButOnePastItOverflows()
    {
        var outbox = new Outbox();
        for (var i = 0; i < Outbox.MaxPending; i++) outbox.Post($"m{i}");
        Assert.False(outbox.Overflowed);
        outbox.Post("one too many");
        Assert.True(outbox.Overflowed);
        Assert.Null(await outbox.NextAsync(T.Token()));
        outbox.Post("later");
        Assert.Null(await outbox.NextAsync(T.Token()));
    }

    [Fact]
    public async Task OverflowDropsTheBacklogEvenWhenAScreenIsQueued()
    {
        var outbox = new Outbox();
        outbox.PostScreen("s");
        for (var i = 0; i < Outbox.MaxPending; i++) outbox.Post($"m{i}");
        Assert.True(outbox.Overflowed);
        Assert.Empty(await DrainAsync(outbox));
    }

    [Fact]
    public async Task CloseIsLastAndNothingFollowsIt()
    {
        var outbox = new Outbox();
        outbox.Post("a");
        outbox.Close("bye");
        outbox.Post("late");
        outbox.PostScreen("late screen");
        outbox.Close("again");
        Assert.Equal(["a", "bye"], await DrainAsync(outbox));
        Assert.Null(await outbox.NextAsync(T.Token()));
        Assert.False(outbox.Overflowed);
    }

    [Fact]
    public async Task NextWaitsForAMessageAndHonoursCancellation()
    {
        var outbox = new Outbox();
        var waiting = outbox.NextAsync(T.Token());
        Assert.False(waiting.IsCompleted);
        outbox.Post("hello");
        Assert.Equal("hello", await waiting);

        using var cancel = new CancellationTokenSource();
        var cancelled = outbox.NextAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }
}

public sealed class ParseKeyTests
{
    private static Key? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SessionEndpoints.ParseKey(doc.RootElement);
    }

    [Fact]
    public void ACharacterIsOneCharacter()
    {
        Assert.Equal(new CharacterKey("a"), Parse("""{"type":"character","char":"a"}"""));
        Assert.Equal(new CharacterKey("é"), Parse("""{"type":"character","char":"é"}"""));
        Assert.Equal(new CharacterKey(" "), Parse("""{"type":"character","char":" "}"""));
        // One code point made of two UTF-16 units.
        Assert.Equal(new CharacterKey("\U0001F600"), Parse("""{"type":"character","char":"😀"}"""));
    }

    [Theory]
    [InlineData("""{"type":"character"}""")]
    [InlineData("""{"type":"character","char":""}""")]
    [InlineData("""{"type":"character","char":"ab"}""")]
    [InlineData("""{"type":"character","char":"\u0000"}""")]
    [InlineData("""{"type":"character","char":"\u0007"}""")]
    [InlineData("""{"type":"character","char":"\t"}""")]
    [InlineData("""{"type":"character","char":"\r"}""")]
    [InlineData("""{"type":"character","char":7}""")]
    [InlineData("""{"type":"character","char":null}""")]
    public void ABadCharacterIsRefused(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData("Enter")]
    [InlineData("F3")]
    [InlineData("F24")]
    [InlineData("RollUp")]
    [InlineData("Tab")]
    [InlineData("Reset")]
    public void AKnownFunctionKeyIsAccepted(string name) =>
        Assert.Equal(new FunctionKey(name), Parse($$"""{"type":"function","name":"{{name}}"}"""));

    [Theory]
    [InlineData("""{"type":"function","name":"NotAKey"}""")]
    [InlineData("""{"type":"function","name":"enter"}""")]
    [InlineData("""{"type":"function","name":"F25"}""")]
    [InlineData("""{"type":"function","name":""}""")]
    [InlineData("""{"type":"function","name":3}""")]
    [InlineData("""{"type":"function"}""")]
    public void AnUnknownFunctionKeyIsRefused(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 80)]
    [InlineData(27, 132)]
    [InlineData(28, 132)]
    public void ACursorInsideTheLargestScreenIsAccepted(int row, int column) =>
        Assert.Equal(new CursorKey(row, column), Parse($$"""{"type":"cursor","row":{{row}},"column":{{column}}}"""));

    [Theory]
    [InlineData("""{"type":"cursor","row":0,"column":1}""")]
    [InlineData("""{"type":"cursor","row":1,"column":0}""")]
    [InlineData("""{"type":"cursor","row":29,"column":1}""")]
    [InlineData("""{"type":"cursor","row":1,"column":133}""")]
    [InlineData("""{"type":"cursor","row":-1,"column":1}""")]
    [InlineData("""{"type":"cursor","row":1.5,"column":1}""")]
    [InlineData("""{"type":"cursor","row":"1","column":"1"}""")]
    [InlineData("""{"type":"cursor","row":99999999999,"column":1}""")]
    [InlineData("""{"type":"cursor","row":1}""")]
    [InlineData("""{"type":"cursor"}""")]
    public void ACursorOutsideTheScreenIsRefused(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"type":"other"}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""[]""")]
    [InlineData("\"Enter\"")]
    [InlineData("""null""")]
    public void NotAKeyAtAllIsRefused(string json) => Assert.Null(Parse(json));
}

public sealed class HostErrorTests
{
    [Fact]
    public void ErrorCodesNameTheCauseWithoutDetail()
    {
        Assert.Equal("ConnectionRefused", HostSession.ErrorCode(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        Assert.Equal("certificate rejected", HostSession.ErrorCode(new System.Security.Authentication.AuthenticationException("The remote certificate is invalid")));
        Assert.Equal("ConnectionReset", HostSession.ErrorCode(new IOException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset))));
        Assert.Equal("connect timeout", HostSession.ErrorCode(new TimeoutException()));
        Assert.Equal("io", HostSession.ErrorCode(new IOException("x")));
    }
}
