using System.Globalization;

namespace RouteGo.Audit.Api.Domain;

/// <summary>
/// How an audit record is addressed in the audit log: the partition one entity's history lives in,
/// the order inside it, the business-day index, and the marker that makes an event's identity
/// unique. Writing and reading must agree on every one of these, so they are spelled once, here.
/// </summary>
public static class AuditKeys
{
    /// <summary>The name of the index that lists records by business day.</summary>
    public const string DayIndexName = "gsi1";

    /// <summary>The sort key of an event marker; a marker has no other sort part.</summary>
    public const string EventMarkerSortKey = "EVENT";

    private const string EntityPrefix = "ENTITY#";
    private const string EventPrefix = "EVENT#";
    private const string DayPrefix = "DAY#";
    private const string Separator = "#";
    private const string DayFormat = "yyyy-MM-dd";
    private const string TimeFormat = "O";

    // A business day is an India Standard Time day. India keeps no daylight saving, so one fixed
    // offset is the whole rule.
    private static readonly TimeSpan _indiaOffset = new TimeSpan(5, 30, 0);

    /// <summary>The partition holding the history of one entity, looked up by exactly that spelling.</summary>
    public static string EntityPartitionKey(string entityType, string entityId)
    {
        return EntityPrefix + entityType + Separator + entityId;
    }

    /// <summary>The partition of the marker that says an event has been recorded.</summary>
    public static string EventPartitionKey(string eventId)
    {
        return EventPrefix + eventId;
    }

    /// <summary>
    /// The sort key of a record: the time the action happened, then the event's identity. Every
    /// time is written the same fixed width in UTC, so sorting the text sorts by time, and records
    /// with the same time keep one stable order.
    /// </summary>
    public static string SortKey(DateTimeOffset occurredAt, string eventId)
    {
        string time = FormatTime(occurredAt);
        return time + Separator + eventId;
    }

    /// <summary>The fixed-width UTC text of a time, as stored and as read back.</summary>
    public static string FormatTime(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The business-day index key of the day an action happened on, judged in India Standard Time.</summary>
    public static string BusinessDayKey(DateTimeOffset occurredAt)
    {
        DateTimeOffset india = occurredAt.ToOffset(_indiaOffset);
        string day = india.ToString(DayFormat, CultureInfo.InvariantCulture);
        return DayPrefix + day;
    }

    /// <summary>The business-day index key of a day an admin asks for.</summary>
    public static string BusinessDayKey(DateOnly day)
    {
        string text = day.ToString(DayFormat, CultureInfo.InvariantCulture);
        return DayPrefix + text;
    }
}
