// Synthetic host data for tests: placeholder text and names, never a real system's.
// Ported from the test helpers of the retired TypeScript engine.

using System.Collections;
using GreenGlow.Engine;

namespace GreenGlow.Engine.Tests;

internal static class H
{
    public const byte ESC = 0x04;
    public const byte IAC = 0xff, SB = 0xfa, SE = 0xf0, WILL = 0xfb, DO = 0xfd;
    public const byte SEND = 1, VAR = 0, USERVAR = 3;
    public const byte BINARY = 0, TTYPE = 24, EOR_OPT = 25, NEW_ENVIRON = 39;

    /// <summary>
    /// Flattens bytes, ints (taken as bytes) and sequences of them into one array, the way
    /// the TS tests spread arrays into Uint8Array.from.
    /// </summary>
    public static byte[] B(params object[] parts)
    {
        var output = new List<byte>();
        void Add(object part)
        {
            switch (part)
            {
                case byte b:
                    output.Add(b);
                    break;
                case int i:
                    output.Add((byte)i);
                    break;
                case IEnumerable sequence:
                    foreach (var item in sequence) Add(item!);
                    break;
                default:
                    throw new ArgumentException($"cannot flatten {part.GetType()}");
            }
        }
        foreach (var part in parts) Add(part);
        return output.ToArray();
    }

    public static byte[] Ascii(string text) => text.Select(c => (byte)c).ToArray();

    public static byte[] Text(string s) => Ebcdic.Encode(s);

    public static byte[] Sba(int row, int column) => B(0x11, row, column);

    /// <summary>SF with a field format word: attribute, then a 2-byte length.</summary>
    public static byte[] Sf(int ffw, int attribute, int length) =>
        B(0x1d, ffw >> 8, ffw & 0xff, attribute, length >> 8, length & 0xff);

    public static byte[] Wtd(int cc0, int cc1, params object[] orders) => B(ESC, 0x11, cc0, cc1, orders);

    public static readonly byte[] CLEAR = [ESC, 0x40];
    public static readonly byte[] READ_MDT = [ESC, 0x52, 0x00, 0x00];

    /// <summary>A complete host record, framed for Telnet.</summary>
    public static byte[] HostRecord(int opcode, byte[] data, int flags = 0)
    {
        var length = 10 + data.Length;
        return Telnet.EncodeRecord(B(length >> 8, length & 0xff, 0x12, 0xa0, 0, 0, 4, flags, 0, opcode, data));
    }

    /// <summary>The IBM i side of negotiation, as both systems send it.</summary>
    public static readonly byte[] NEGOTIATION = B(
        new[] { IAC, DO, NEW_ENVIRON, IAC, DO, TTYPE },
        new[] { IAC, SB, NEW_ENVIRON, SEND, VAR, USERVAR, IAC, SE },
        new[] { IAC, SB, TTYPE, SEND, IAC, SE },
        new[] { IAC, DO, EOR_OPT, IAC, WILL, EOR_OPT, IAC, DO, BINARY, IAC, WILL, BINARY });

    /// <summary>A synthetic Startup Response Record (placeholder names, not a real system).</summary>
    public static byte[] StartupResponse(string code, string deviceName)
    {
        var record = new byte[0x49];
        B(0x00, 0x49, 0x12, 0xa0).CopyTo(record, 0);
        B(0x90, 0x00, 0x05, 0x60, 0x06, 0x00, 0x20, 0xc0, 0x00, 0x3d, 0x00, 0x00).CopyTo(record, 4);
        Ebcdic.Encode(code).CopyTo(record, 16);
        Ebcdic.Encode("SYSTEM  ").CopyTo(record, 20);
        Ebcdic.Encode(deviceName.PadRight(10, ' ')).CopyTo(record, 28);
        return Telnet.EncodeRecord(record);
    }

    /// <summary>
    /// A sign-on screen with the test system's layout: error row 25, a monocase user field
    /// at row 6 column 53, a nondisplay password field below it, then three hidden bypass
    /// fields, and READ MDT FIELDS.
    /// </summary>
    public static readonly byte[] SIGN_ON = B(
        CLEAR,
        Wtd(
            0x00,
            0x18,
            new[] { 0x01, 0x07, 0x00, 0x00, 0x00, 0x19, 0x00, 0x00, 0x00 },
            Sba(1, 34),
            0x20,
            Text("Sign On"),
            Sba(6, 17),
            Text("User"),
            Sba(6, 52),
            Sf(0x4020, 0x24, 10),
            Sba(7, 17),
            Text("Password"),
            Sba(7, 52),
            Sf(0x4020, 0x27, 10),
            Sba(8, 52),
            Sf(0x6000, 0x27, 10),
            Sba(9, 52),
            Sf(0x6000, 0x27, 10),
            Sba(10, 52),
            Sf(0x6000, 0x27, 10)),
        READ_MDT);

    /// <summary>A connection that has negotiated, started a session and shown the sign-on screen.</summary>
    public static Connection SignOnConnection()
    {
        var connection = new Connection(new ConnectionConfig { TerminalType = "IBM-3477-FC", DeviceName = "GGTEST001" });
        connection.Receive(NEGOTIATION);
        connection.Receive(StartupResponse("I902", "GGTEST001"));
        connection.Receive(HostRecord(0x03, SIGN_ON));
        return connection;
    }

    public static Key Fn(string name) => new FunctionKey(name);

    public static Key Char(string c) => new CharacterKey(c);

    public static Key At(int row, int column) => new CursorKey(row, column);

    /// <summary>One character key per character of the text.</summary>
    public static List<Key> Typed(string text) => text.Select(c => (Key)new CharacterKey(c.ToString())).ToList();

    private static IEnumerable<Key> Keys(object[] items) =>
        items.SelectMany(item => item switch
        {
            Key key => new[] { key },
            IEnumerable<Key> keys => keys,
            _ => throw new ArgumentException($"not a key: {item}"),
        });

    /// <summary>Sends keys to a connection and collects every send and event.</summary>
    public static ReceiveResult PressAll(Connection connection, params object[] items)
    {
        var total = new ReceiveResult();
        foreach (var key in Keys(items))
        {
            var result = connection.Key(key);
            total.Send.AddRange(result.Send);
            total.Events.AddRange(result.Events);
        }
        return total;
    }

    /// <summary>A client send decoded: IAC doubling undone, header parsed.</summary>
    public static HostRecord SentRecord(byte[] bytes)
    {
        var events = new TelnetDecoder().Push(bytes);
        if (events.FirstOrDefault() is not TelnetEvent.Record record) throw new InvalidOperationException("not a 5250 record");
        if (Records.Parse(record.Data) is not ParsedRecord.Data parsed) throw new InvalidOperationException("not a data record");
        return parsed.Record;
    }

    /// <summary>Handles keys on a screen, ignoring the results.</summary>
    public static void Type(Screen screen, params object[] items)
    {
        foreach (var key in Keys(items)) Keyboard.HandleKey(screen, key);
    }

    public static ApplyResult Put(Screen screen, params object[] data) =>
        DataStream.ApplyRecord(screen, new HostRecord(0, Opcode.PutGet, B(data)), true);

    /// <summary>The event types, as the TS tests list them.</summary>
    public static List<string> Types(ReceiveResult result) => result.Events.Select(e => e.Type).ToList();

    public static List<OutboundKind> Kinds(ReceiveResult result) => result.Send.Select(s => s.Kind).ToList();

    /// <summary>The bytes of a framed record without its 10-byte header and IAC EOR.</summary>
    public static byte[] Body(byte[] framed) => framed[10..^2];
}
