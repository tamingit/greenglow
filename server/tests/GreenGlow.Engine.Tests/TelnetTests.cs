using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

public class TelnetDecoderTests
{
    private const byte EOR = Telnet.Eor;

    [Fact(DisplayName = "TelnetDecoder: decodes negotiation commands")]
    public void DecodesNegotiationCommands()
    {
        var events = new TelnetDecoder().Push(B(IAC, DO, TelnetOption.NewEnviron, IAC, DO, TelnetOption.TerminalType));
        Assert.Equal(
            [new TelnetEvent.Negotiate(NegotiationCommand.Do, 39), new TelnetEvent.Negotiate(NegotiationCommand.Do, 24)],
            events);
    }

    [Fact(DisplayName = "TelnetDecoder: ends a record at IAC EOR and undoubles IAC IAC")]
    public void EndsARecordAtIacEor()
    {
        var events = new TelnetDecoder().Push(B(0x01, IAC, IAC, 0x02, IAC, EOR));
        Assert.Equal([new TelnetEvent.Record(B(0x01, 0xff, 0x02))], events);
    }

    [Fact(DisplayName = "TelnetDecoder: emits an empty record for a bare IAC EOR")]
    public void EmitsAnEmptyRecord()
    {
        Assert.Equal([new TelnetEvent.Record([])], new TelnetDecoder().Push(B(IAC, EOR)));
    }

    [Fact(DisplayName = "TelnetDecoder: decodes a subnegotiation, undoubling IAC inside it")]
    public void DecodesASubnegotiation()
    {
        var events = new TelnetDecoder().Push(B(IAC, SB, TelnetOption.TerminalType, 0x01, IAC, IAC, IAC, SE));
        Assert.Equal([new TelnetEvent.Subnegotiation(24, B(0x01, 0xff))], events);
    }

    [Fact(DisplayName = "TelnetDecoder: reports other two-byte commands")]
    public void ReportsOtherCommands()
    {
        Assert.Equal([new TelnetEvent.Control(0xf1)], new TelnetDecoder().Push(B(IAC, 0xf1)));
    }

    [Fact(DisplayName = "TelnetDecoder: ends a subnegotiation that is interrupted by another command")]
    public void EndsAnInterruptedSubnegotiation()
    {
        var events = new TelnetDecoder().Push(B(IAC, SB, 24, 0x01, IAC, DO, 0));
        Assert.Equal(
            [new TelnetEvent.Subnegotiation(24, B(0x01)), new TelnetEvent.Negotiate(NegotiationCommand.Do, 0)],
            events);
    }

    [Fact(DisplayName = "TelnetDecoder: gives the same events however the stream is split")]
    public void GivesTheSameEventsHoweverSplit()
    {
        var stream = B(
            new[] { IAC, DO, 39, IAC, SB, 24, 0x01, IAC, IAC, IAC, SE },
            new[] { 0x04, 0x00, IAC, IAC, 0xf3, IAC, EOR },
            new[] { IAC, DO, 25, 0x05, IAC, EOR });
        var whole = new TelnetDecoder().Push(stream);
        Assert.Equal(5, whole.Count);
        for (var a = 0; a <= stream.Length; a++)
        {
            for (var b = a; b <= stream.Length; b++)
            {
                var decoder = new TelnetDecoder();
                var events = new List<TelnetEvent>();
                events.AddRange(decoder.Push(stream.AsSpan(0, a)));
                events.AddRange(decoder.Push(stream.AsSpan(a, b - a)));
                events.AddRange(decoder.Push(stream.AsSpan(b)));
                Assert.Equal(whole, events);
            }
        }
    }
}

public class TelnetEncoderTests
{
    [Fact(DisplayName = "encoders: encodeRecord doubles 0xFF and appends IAC EOR")]
    public void EncodeRecordDoubles()
    {
        Assert.Equal(B(0x01, IAC, IAC, 0x02, IAC, Telnet.Eor), Telnet.EncodeRecord(B(0x01, 0xff, 0x02)));
    }

    [Fact(DisplayName = "encoders: encodeRecord round-trips every byte value through the decoder")]
    public void EncodeRecordRoundTrips()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal([new TelnetEvent.Record(all)], new TelnetDecoder().Push(Telnet.EncodeRecord(all)));
    }

    [Fact(DisplayName = "encoders: encodeNegotiation")]
    public void EncodeNegotiation()
    {
        Assert.Equal(B(IAC, 0xfb, 25), Telnet.EncodeNegotiation(NegotiationCommand.Will, TelnetOption.EndOfRecord));
    }

    [Fact(DisplayName = "encoders: encodeSubnegotiation frames TERMINAL-TYPE IS")]
    public void EncodeSubnegotiation()
    {
        var isReply = B(0x00, Ascii("IBM-3179-2"));
        Assert.Equal(B(IAC, SB, 24, isReply, IAC, SE), Telnet.EncodeSubnegotiation(TelnetOption.TerminalType, isReply));
    }

    [Fact(DisplayName = "encoders: optionName")]
    public void OptionName()
    {
        Assert.Equal("NEW-ENVIRON", Telnet.OptionName(39));
        Assert.Equal("option 99", Telnet.OptionName(99));
    }
}
