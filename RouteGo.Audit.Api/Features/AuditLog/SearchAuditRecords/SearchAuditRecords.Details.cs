using System.Text;

namespace RouteGo.Audit.Api.Features.AuditLog.SearchAuditRecords;

// Details were stored as the nested map the publisher sent. Reading them back rebuilds exactly
// that JSON — strings, numbers, true/false, null, lists and objects — so a record shows what was
// written, however old it is.
public sealed partial class SearchAuditRecordsHandler
{
    private static Dictionary<string, JsonElement>? ToDetails(Dictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue("details", out AttributeValue? stored) || !stored.IsMSet || stored.M.Count == 0)
        {
            return null;
        }

        var details = new Dictionary<string, JsonElement>();
        foreach (KeyValuePair<string, AttributeValue> entry in stored.M)
        {
            details[entry.Key] = ToJson(entry.Value);
        }

        return details;
    }

    private static JsonElement ToJson(AttributeValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, AttributeValue value)
    {
        if (value.IsMSet)
        {
            WriteMap(writer, value.M);
            return;
        }

        if (value.IsLSet)
        {
            WriteList(writer, value.L);
            return;
        }

        WriteScalar(writer, value);
    }

    private static void WriteMap(Utf8JsonWriter writer, Dictionary<string, AttributeValue> map)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, AttributeValue> entry in map)
        {
            writer.WritePropertyName(entry.Key);
            Write(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter writer, List<AttributeValue> list)
    {
        writer.WriteStartArray();
        foreach (AttributeValue element in list)
        {
            Write(writer, element);
        }

        writer.WriteEndArray();
    }

    private static void WriteScalar(Utf8JsonWriter writer, AttributeValue value)
    {
        if (value.S != null)
        {
            writer.WriteStringValue(value.S);
            return;
        }

        if (value.N != null)
        {
            writer.WriteRawValue(value.N);
            return;
        }

        if (value.BOOL != null)
        {
            writer.WriteBooleanValue(value.BOOL.Value);
            return;
        }

        writer.WriteNullValue();
    }
}
