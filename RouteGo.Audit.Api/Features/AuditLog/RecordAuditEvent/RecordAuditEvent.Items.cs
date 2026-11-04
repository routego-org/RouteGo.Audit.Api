namespace RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

// How an audit event becomes the two items written for it. The record is what readers see; the
// marker only says the event's identity has been used, and carries no index keys, so it never
// appears in an entity's history or a day's list.
public sealed partial class RecordAuditEventHandler
{
    private static Dictionary<string, AttributeValue> ToMarkerItem(ReceivedEvent receivedEvent)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = AuditKeys.EventPartitionKey(receivedEvent.EventId) } },
            { "sk", new AttributeValue { S = AuditKeys.EventMarkerSortKey } },
        };
    }

    private static Dictionary<string, AttributeValue> ToRecordItem(ReceivedEvent receivedEvent, AuditEventMessage message)
    {
        string sortKey = AuditKeys.SortKey(receivedEvent.OccurredAt, receivedEvent.EventId);
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = AuditKeys.EntityPartitionKey(message.EntityType!, message.EntityId!) } },
            { "sk", new AttributeValue { S = sortKey } },
            { "gsi1pk", new AttributeValue { S = AuditKeys.BusinessDayKey(receivedEvent.OccurredAt) } },
            { "gsi1sk", new AttributeValue { S = sortKey } },
            { "eventId", new AttributeValue { S = receivedEvent.EventId } },
            { "actorId", new AttributeValue { S = message.ActorId } },
            { "actorRole", new AttributeValue { S = message.ActorRole } },
            { "action", new AttributeValue { S = message.Action } },
            { "entityType", new AttributeValue { S = message.EntityType } },
            { "entityId", new AttributeValue { S = message.EntityId } },
            { "occurredAt", new AttributeValue { S = AuditKeys.FormatTime(receivedEvent.OccurredAt) } },
            { "correlationId", new AttributeValue { S = receivedEvent.CorrelationId } },
        };

        if (message.Details is not null)
        {
            item["details"] = ToAttributeValue(message.Details.Value);
        }

        return item;
    }

    // Details are free-form, so they are stored as the nested map they were sent as: no key,
    // value or number is renamed, retyped or dropped on the way in.
    private static AttributeValue ToAttributeValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return ToMap(value);
            case JsonValueKind.Array:
                return ToList(value);
            case JsonValueKind.String:
                return new AttributeValue { S = value.GetString() };
            case JsonValueKind.Number:
                return new AttributeValue { N = value.GetRawText() };
            case JsonValueKind.True:
                return new AttributeValue { BOOL = true };
            case JsonValueKind.False:
                return new AttributeValue { BOOL = false };
            default:
                return new AttributeValue { NULL = true };
        }
    }

    private static AttributeValue ToMap(JsonElement value)
    {
        var map = new Dictionary<string, AttributeValue>();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            map[property.Name] = ToAttributeValue(property.Value);
        }

        return new AttributeValue { M = map, IsMSet = true };
    }

    private static AttributeValue ToList(JsonElement value)
    {
        var list = new List<AttributeValue>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            list.Add(ToAttributeValue(element));
        }

        return new AttributeValue { L = list, IsLSet = true };
    }
}
