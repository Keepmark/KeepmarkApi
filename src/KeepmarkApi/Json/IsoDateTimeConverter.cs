using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeepmarkApi.Json;

/// <summary>Serializes instants exactly like JavaScript toISOString():
/// UTC with always three fractional digits ("...00.000Z"), so whole-second
/// values echoed to a Node server still match its strict string comparison.</summary>
public sealed class IsoDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }
}
