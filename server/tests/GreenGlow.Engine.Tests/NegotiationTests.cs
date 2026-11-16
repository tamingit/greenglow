using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

public class TelnetNegotiatorTests
{
    // NEW-ENVIRON and TERMINAL-TYPE codes used in the expected bytes.
    private const byte IS = 0, VALUE = 1;
    private const byte TM = 6, SGA = 3;
    private const byte DONT = Telnet.Dont, WONT = Telnet.Wont;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Hex(string ascii) => Hex(Ascii(ascii));

    /// <summary>Feeds host bytes through the decoder and negotiator; returns what the client sends.</summary>
    private sealed class Exchange(NegotiationConfig? config = null)
    {
        private readonly TelnetDecoder _decoder = new();

        public TelnetNegotiator Negotiator { get; } =
            new(config ?? new NegotiationConfig("IBM-3477-FC", "GGTEST001"));

        public (byte[] Sent, List<NegotiationEvent> Events) Host(params object[] bytes)
        {
            var sent = new List<byte>();
            var events = new List<NegotiationEvent>();
            foreach (var telnetEvent in _decoder.Push(B(bytes)))
            {
                var result = Negotiator.Handle(telnetEvent);
                foreach (var chunk in result.Send) sent.AddRange(chunk);
                events.AddRange(result.Events);
            }
            return (sent.ToArray(), events);
        }
    }

    private static byte[] Variable(string name, string value) => B(USERVAR, Ascii(name), VALUE, Ascii(value));

    [Fact(DisplayName = "TelnetNegotiator: follows the IBM i opening sequence into 5250 mode")]
    public void FollowsTheOpeningSequence()
    {
        var session = new Exchange();

        Assert.Equal(
            B(IAC, WILL, NEW_ENVIRON, IAC, WILL, TTYPE),
            session.Host(IAC, DO, NEW_ENVIRON, IAC, DO, TTYPE).Sent);

        // The IBM i asks for every variable and offers a binary seed for auto sign-on.
        var seed = B(0x00, 0x01, 0x02, 0x03, 0x10, 0x20, 0x30, 0x40);
        var environment = session.Host(
            B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("IBMRSEED"), seed),
            B(VAR, USERVAR, IAC, SE));
        Assert.Equal(
            B(
                B(IAC, SB, NEW_ENVIRON, IS),
                Variable("DEVNAME", "GGTEST001"),
                Variable("KBDTYPE", "USB"),
                Variable("CODEPAGE", "37"),
                Variable("CHARSET", "697"),
                Variable("IBMSENDCONFREC", "YES"),
                B(IAC, SE)),
            environment.Sent);

        Assert.Equal(B(IAC, SB, TTYPE, IS, Ascii("IBM-3477-FC"), IAC, SE), session.Host(IAC, SB, TTYPE, SEND, IAC, SE).Sent);

        var binary = session.Host(
            B(IAC, DO, EOR_OPT, IAC, WILL, EOR_OPT),
            B(IAC, DO, BINARY, IAC, WILL, BINARY));
        Assert.Equal(
            B(IAC, WILL, EOR_OPT, IAC, DO, EOR_OPT, IAC, WILL, BINARY, IAC, DO, BINARY),
            binary.Sent);
        Assert.Equal([new NegotiationEvent.Ready()], binary.Events);
        Assert.True(session.Negotiator.IsReady);
    }

    [Fact(DisplayName = "TelnetNegotiator: does not answer a request for an option that is already on or already off")]
    public void DoesNotAnswerRepeatedRequests()
    {
        var session = new Exchange();
        session.Host(IAC, DO, BINARY);
        Assert.Empty(session.Host(IAC, DO, BINARY).Sent);
        Assert.Empty(session.Host(IAC, WONT, BINARY).Sent);
        Assert.Empty(session.Host(IAC, DONT, SGA).Sent);
        Assert.Equal(B(IAC, WONT, BINARY), session.Host(IAC, DONT, BINARY).Sent);
    }

    [Fact(DisplayName = "TelnetNegotiator: refuses options it does not support, every time they are asked")]
    public void RefusesUnsupportedOptions()
    {
        var session = new Exchange();
        Assert.Equal(B(IAC, WONT, SGA, IAC, WONT, SGA), session.Host(IAC, DO, SGA, IAC, DO, SGA).Sent);
        Assert.Equal(B(IAC, DONT, TTYPE), session.Host(IAC, WILL, TTYPE).Sent);
    }

    [Fact(DisplayName = "TelnetNegotiator: answers every timing mark")]
    public void AnswersEveryTimingMark()
    {
        var session = new Exchange();
        Assert.Equal(B(IAC, WILL, TM), session.Host(IAC, DO, TM).Sent);
        Assert.Equal(B(IAC, WILL, TM), session.Host(IAC, DO, TM).Sent);
    }

    [Fact(DisplayName = "TelnetNegotiator: is not ready until the terminal type has been sent")]
    public void IsNotReadyUntilTerminalTypeSent()
    {
        var session = new Exchange();
        var early = session.Host(IAC, DO, EOR_OPT, IAC, WILL, EOR_OPT, IAC, DO, BINARY, IAC, WILL, BINARY);
        Assert.Empty(early.Events);
        Assert.Equal([new NegotiationEvent.Ready()], session.Host(IAC, SB, TTYPE, SEND, IAC, SE).Events);
    }

    [Fact(DisplayName = "TelnetNegotiator: repeats the device name when the IBM i reports it in use")]
    public void RepeatsTheDeviceName()
    {
        var session = new Exchange();
        session.Host(IAC, SB, NEW_ENVIRON, SEND, VAR, USERVAR, IAC, SE);
        var collision = session.Host(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("DEVNAME"), IAC, SE);
        Assert.Equal([new NegotiationEvent.DeviceCollision("GGTEST001")], collision.Events);
        Assert.Equal(B(IAC, SB, NEW_ENVIRON, IS, Variable("DEVNAME", "GGTEST001"), IAC, SE), collision.Sent);
    }

    [Fact(DisplayName = "TelnetNegotiator: treats a DEVNAME-only request before its first reply as the first request")]
    public void TreatsAnEarlyDevnameRequestAsFirst()
    {
        var session = new Exchange();
        var first = session.Host(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("DEVNAME"), IAC, SE);
        Assert.Empty(first.Events);
        Assert.Contains(Hex("IBMSENDCONFREC"), Hex(first.Sent));
    }

    [Fact(DisplayName = "TelnetNegotiator: sends no DEVNAME when none is configured")]
    public void SendsNoDevnameWhenNoneConfigured()
    {
        var session = new Exchange(new NegotiationConfig("IBM-3477-FC"));
        var (sent, _) = session.Host(IAC, SB, NEW_ENVIRON, SEND, VAR, USERVAR, IAC, SE);
        Assert.DoesNotContain(Hex("DEVNAME"), Hex(sent));
    }

    [Fact(DisplayName = "TelnetNegotiator: rejects an invalid device name")]
    public void RejectsAnInvalidDeviceName()
    {
        var error = Assert.Throws<ArgumentException>(() => new TelnetNegotiator(new NegotiationConfig("IBM-3477-FC", "ghtest")));
        Assert.Contains("Not a valid device name", error.Message);
    }

    [Fact(DisplayName = "encodeEnvironment: escapes bytes that look like NEW-ENVIRON codes")]
    public void EncodeEnvironmentEscapes()
    {
        Assert.Equal(
            B(IAC, SB, NEW_ENVIRON, IS, USERVAR, 0x41, 2, 0x01, VALUE, 0x42, 2, 0x03, IAC, SE),
            TelnetNegotiator.EncodeEnvironment([("A\u0001", "B\u0003")]));
    }

    [Theory(DisplayName = "isValidDeviceName: accepts")]
    [InlineData("GGK7Q2MX1")]
    [InlineData("QPADEV0001")]
    [InlineData("$DEV")]
    [InlineData("A")]
    [InlineData("AB_C#@")]
    public void AcceptsValidDeviceNames(string name)
    {
        Assert.True(TelnetNegotiator.IsValidDeviceName(name));
    }

    [Theory(DisplayName = "isValidDeviceName: rejects")]
    [InlineData("")]
    [InlineData("ghk7")]
    [InlineData("1ABC")]
    [InlineData("_ABC")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("AB-C")]
    [InlineData("AB C")]
    public void RejectsInvalidDeviceNames(string name)
    {
        Assert.False(TelnetNegotiator.IsValidDeviceName(name));
    }

    // ---- Automatic sign-on (RFC 4777 section 5) ----

    private const byte ENV_ESC = 2;

    [Fact(DisplayName = "TelnetNegotiator: signs on with a substitute made from the IBM i's seed")]
    public void SignsOnWithASubstitute()
    {
        byte[]? given = null;
        // Both the client seed and the substitute hold bytes that look like NEW-ENVIRON codes.
        var clientSeed = B(0x11, 0x00, 0x33, 0x44, 0x55, 0x66, 0x77, 0x03);
        var substitute = B(0xa1, 0x01, 0xa3, 0xa4, 0x02, 0xa6, 0xa7, 0xff);
        var session = new Exchange(new NegotiationConfig("IBM-3477-FC", "GGTEST001", new AutoSignOn("SOMEUSER", seed =>
        {
            given = seed;
            return (clientSeed, substitute);
        })));
        session.Host(IAC, DO, NEW_ENVIRON);

        // The seed's 0x01 arrives after an ESC, as RFC 1572 has it.
        var (sent, _) = session.Host(
            B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("IBMRSEED"), 0x9a, ENV_ESC, 0x01, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1),
            B(VAR, USERVAR, IAC, SE));

        Assert.Equal(B(0x9a, 0x01, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1), given);
        Assert.True(session.Negotiator.SignOnSent);
        Assert.Equal(
            B(
                B(IAC, SB, NEW_ENVIRON, IS),
                B(VAR, Ascii("USER"), VALUE, Ascii("SOMEUSER")),
                B(USERVAR, Ascii("IBMRSEED"), VALUE, 0x11, ENV_ESC, 0x00, 0x33, 0x44, 0x55, 0x66, 0x77, ENV_ESC, 0x03),
                // 0xff is doubled for Telnet as well.
                B(USERVAR, Ascii("IBMSUBSPW"), VALUE, 0xa1, ENV_ESC, 0x01, 0xa3, 0xa4, ENV_ESC, 0x02, 0xa6, 0xa7, 0xff, 0xff),
                Variable("DEVNAME", "GGTEST001"),
                Variable("KBDTYPE", "USB"),
                Variable("CODEPAGE", "37"),
                Variable("CHARSET", "697"),
                Variable("IBMSENDCONFREC", "YES"),
                B(IAC, SE)),
            sent);

        // Asked again, the IBM i gets the variables but never a second substitute.
        given = null;
        var (again, _) = session.Host(
            B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("IBMRSEED"), 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1),
            B(VAR, USERVAR, IAC, SE));
        Assert.Null(given);
        Assert.DoesNotContain("IBMSUBSPW", System.Text.Encoding.Latin1.GetString(again));
    }

    [Theory(DisplayName = "TelnetNegotiator: without a usable seed the sign-on screen is used")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WithoutASeedNothingIsSent(bool zeroSeed, bool declined)
    {
        var asked = 0;
        var session = new Exchange(new NegotiationConfig("IBM-3477-FC", "GGTEST001", new AutoSignOn("SOMEUSER", _ =>
        {
            asked++;
            return declined ? null : (new byte[8], new byte[20]);
        })));
        session.Host(IAC, DO, NEW_ENVIRON);
        var (sent, _) = declined
            ? session.Host(B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("IBMRSEED"), 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1), B(VAR, USERVAR, IAC, SE))
            : zeroSeed
                ? session.Host(B(IAC, SB, NEW_ENVIRON, SEND, USERVAR, Ascii("IBMRSEED"), ENV_ESC, 0, ENV_ESC, 0, ENV_ESC, 0, ENV_ESC, 0),
                    B(ENV_ESC, 0, ENV_ESC, 0, ENV_ESC, 0, ENV_ESC, 0, VAR, USERVAR, IAC, SE))
                : session.Host(IAC, SB, NEW_ENVIRON, SEND, VAR, USERVAR, IAC, SE);

        Assert.False(session.Negotiator.SignOnSent);
        Assert.Equal(declined ? 1 : 0, asked);
        var text = System.Text.Encoding.Latin1.GetString(sent);
        Assert.DoesNotContain("USER\u0001SOMEUSER", text);
        Assert.DoesNotContain("IBMSUBSPW", text);
        Assert.Contains("DEVNAME", text);
    }

    [Fact(DisplayName = "Connection: a recorded session never signs on automatically")]
    public void ARecordingRefusesAutomaticSignOn()
    {
        Assert.Throws<ArgumentException>(() => new Connection(new ConnectionConfig
        {
            TerminalType = "IBM-3477-FC",
            SignOn = new AutoSignOn("SOMEUSER", _ => null),
            Recording = new RecordingOptions(() => 0, Blank.Nondisplay),
        }));
    }
}
