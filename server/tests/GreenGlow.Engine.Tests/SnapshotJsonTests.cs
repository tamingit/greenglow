// Not a port of a TS test: the browser renders the snapshot the server serializes, so its
// JSON names must be the TS ScreenSnapshot's camelCase names, and enums the TS strings.

using System.Text.Json;
using GreenGlow.Engine;
using static GreenGlow.Engine.Tests.H;

namespace GreenGlow.Engine.Tests;

public class SnapshotJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "JSON: a snapshot serializes with the TS ScreenSnapshot names and strings")]
    public void SnapshotSerializesWithTsNames()
    {
        var connection = SignOnConnection();
        PressAll(connection, At(1, 1), Typed("x"));
        var json = JsonDocument.Parse(JsonSerializer.Serialize(connection.Screen, Web)).RootElement;
        Assert.Equal(
            [
                "rows", "columns", "buffer", "attributes", "fields", "cursor", "keyboard", "keyboardLocked", "error",
                "insertMode", "fieldExitRequired", "typeAhead", "messageWaiting", "errorRow", "messageLine", "pendingRead",
            ],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("error", json.GetProperty("keyboard").GetString());
        Assert.Equal("operator", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal(1, json.GetProperty("cursor").GetProperty("row").GetInt32());
        var field = json.GetProperty("fields")[0];
        Assert.Equal(
            ["row", "column", "length", "ffw", "fcws", "attribute", "format"],
            field.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            [
                "bypass", "dupEnable", "modified", "shift", "autoEnter", "fieldExitRequired", "monocase", "mandatoryEnter",
                "adjust",
            ],
            field.GetProperty("format").EnumerateObject().Select(p => p.Name));
        Assert.Equal("alpha-shift", field.GetProperty("format").GetProperty("shift").GetString());
    }

    [Fact(DisplayName = "JSON: events carry their TS type and field names")]
    public void EventsCarryTheirTsNames()
    {
        ConnectionEvent record = new ConnectionEvent.Record(new HostRecord(0, 3, [1]));
        var json = JsonDocument.Parse(JsonSerializer.Serialize(record, record.GetType(), Web)).RootElement;
        Assert.Equal("record", json.GetProperty("type").GetString());
        Assert.Equal(3, json.GetProperty("record").GetProperty("opcode").GetInt32());
        var outbound = JsonSerializer.Serialize(new Outbound(OutboundKind.HostRead, []), Web);
        Assert.Contains("\"kind\":\"host-read\"", outbound);
    }
}
