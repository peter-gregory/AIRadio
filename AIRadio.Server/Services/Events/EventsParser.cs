using System.Text.RegularExpressions;
using AIRadio.Server.Services.Alarms;

namespace AIRadio.Server.Services.Events;

public sealed record EventsQuery(
    DateTime Date,
    bool IncludeAlarms,
    bool IncludeReminders);

public static class EventsParser
{
    public static EventsQuery? Parse(string text, DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var reference = now ?? DateTime.Now;
        var normalized = Normalize(text);

        var includeAlarms = Regex.IsMatch(
            normalized,
            @"\balarms?\b",
            RegexOptions.IgnoreCase);

        var includeReminders = Regex.IsMatch(
            normalized,
            @"\breminders?\b",
            RegexOptions.IgnoreCase);

        // If neither category was explicitly requested, report both.
        if (!includeAlarms && !includeReminders)
        {
            includeAlarms = true;
            includeReminders = true;
        }

        var dateExpression = ExtractDateExpression(normalized);
        if (dateExpression is null)
        {
            // An events request with no date means today.
            return new EventsQuery(
                reference.Date,
                includeAlarms,
                includeReminders);
        }

        try
        {
            var expression = NormalizeDateExpression(dateExpression);
            var range = RecurrenceTimeRangeParser.Parse(expression, reference);

            if (range.StartDate is null)
                return null;

            return new EventsQuery(
                range.StartDate.Value.ToDateTime(TimeOnly.MinValue),
                includeAlarms,
                includeReminders);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ExtractDateExpression(string text)
    {
        var iso = Regex.Match(
            text,
            @"\b20\d{2}[-/]\d{1,2}[-/]\d{1,2}\b",
            RegexOptions.IgnoreCase);

        if (iso.Success)
            return iso.Value;

        var relative = Regex.Match(
            text,
            @"\bday after tomorrow\b|\btomorrow\b|\btoday\b",
            RegexOptions.IgnoreCase);

        if (relative.Success)
            return relative.Value;

        var weekday = Regex.Match(
            text,
            @"\b(?:(?:this|next)\s+)?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
            RegexOptions.IgnoreCase);

        if (weekday.Success)
            return weekday.Value;

        var monthDay = Regex.Match(
            text,
            @"\b(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2}(?:st|nd|rd|th)?\b",
            RegexOptions.IgnoreCase);

        return monthDay.Success ? monthDay.Value : null;
    }

    private static string NormalizeDateExpression(string expression)
    {
        // RecurrenceTimeRangeParser requires a recurrence qualifier for a
        // bare weekday. Treat "Friday" as the next occurrence of Friday.
        if (Regex.IsMatch(
                expression,
                @"^(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)$",
                RegexOptions.IgnoreCase))
        {
            return $"next {expression}";
        }

        return expression;
    }

    private static string Normalize(string value) =>
        Regex.Replace(
            value.Trim().TrimEnd('.', '?', '!'),
            @"\s+",
            " ");
}
