using System.Globalization;

namespace RouteGo.Audit.Api.Domain;

/// <summary>The rules for asking the audit log a question: how a day is written and how pages are cut.</summary>
public static class AuditSearchRules
{
    /// <summary>The page returned when the caller names none.</summary>
    public const int DefaultPage = 1;

    /// <summary>How many records a page holds when the caller names no size.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The most records one page may hold.</summary>
    public const int MaxPageSize = 200;

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>True when the text is a calendar day written yyyy-MM-dd.</summary>
    public static bool IsBusinessDay(string? text)
    {
        DateOnly? day = ParseBusinessDay(text);
        return day != null;
    }

    /// <summary>The day the text names, or null when it is not a calendar day written yyyy-MM-dd.</summary>
    public static DateOnly? ParseBusinessDay(string? text)
    {
        bool isDay = DateOnly.TryParseExact(text, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day);
        if (!isDay)
        {
            return null;
        }

        return day;
    }

    /// <summary>True when the page number is one the log can serve; pages count from 1.</summary>
    public static bool IsValidPage(int page)
    {
        return page >= DefaultPage;
    }

    /// <summary>True when the page size is from 1 to the most one page may hold.</summary>
    public static bool IsValidPageSize(int pageSize)
    {
        return pageSize >= 1 && pageSize <= MaxPageSize;
    }
}
