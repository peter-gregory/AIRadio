using System.Globalization;
using System.Text.RegularExpressions;
using AIRadio.Server.Models.Alarms;

namespace AIRadio.Server.Services.Alarms;

public static partial class RecurrenceTimeRangeParser
{
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames
        .Where(x => !string.IsNullOrEmpty(x)).ToArray();

    public static RecurrenceTimeRange Parse(string expression, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("A date and time expression is required.", nameof(expression));

        var reference = now ?? DateTime.Now;
        var text = Normalize(expression);

        var timer = ParseTimer(text, reference);
        if (timer.HasValue)
        {
            var dueAt = timer.Value;
            return new()
            {
                Type = SchedulePatternType.Once,
                StartDate = DateOnly.FromDateTime(dueAt),
                EndDate = DateOnly.FromDateTime(dueAt),
                DueAt = dueAt,
                TimeOfDay = dueAt.TimeOfDay
            };
        }

        var time = ParseTime(text);
        var ordinal = ParseOrdinal(text);

        var dateRange = ParseDateRange(text, reference);
        if (dateRange.HasValue)
            return new()
            {
                Type = SchedulePatternType.Once,
                StartDate = dateRange.Value.Start,
                EndDate = dateRange.Value.End,
                TimeOfDay = time
            };
        var weekday = ParseWeekday(text);
        var month = ParseMonth(text);
        var day = ParseNumericDate(text, reference, month);

        if (IsDaily(text))
            return new() { Type = SchedulePatternType.Daily, TimeOfDay = time };

        if (IsWeekdays(text))
            return new() {
                Type = SchedulePatternType.Weekly,
                DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                TimeOfDay = time
            };

        if (IsWeekends(text))
            return new() {
                Type = SchedulePatternType.Weekly,
                DaysOfWeek = [DayOfWeek.Saturday, DayOfWeek.Sunday],
                TimeOfDay = time
            };

        if (ordinal is not null && weekday is not null &&
            Regex.IsMatch(text, @"\b(every|each)\s+(month|year)\b|\bof\s+every\s+month\b", RegexOptions.IgnoreCase))
        {
            return new()
            {
                Type = SchedulePatternType.Monthly,
                WeekOfMonth = ordinal,
                WeekdayOfMonth = weekday,
                TimeOfDay = time
            };
        }

        var weekdays = ParseWeekdays(text);
        if (weekdays.Length > 0 && IsRecurring(text))
            return new()
            {
                Type = SchedulePatternType.Weekly,
                DaysOfWeek = weekdays,
                TimeOfDay = time
            };

        if (month.HasValue && day.HasValue && IsYearly(text))
            return new()
            {
                Type = SchedulePatternType.Yearly,
                Month = month,
                DayOfMonth = day,
                WeekOfMonth = ordinal,
                WeekdayOfMonth = ordinal is not null ? weekday : null,
                TimeOfDay = time
            };

        if (month.HasValue && day.HasValue && IsMonthlyOrdinal(text))
            return new()
            {
                Type = SchedulePatternType.Monthly,
                Month = month,
                DayOfMonth = day,
                WeekOfMonth = ordinal,
                WeekdayOfMonth = weekday,
                TimeOfDay = time
            };

        if (IsMonthlyDay(text))
            return new()
            {
                Type = SchedulePatternType.Monthly,
                DayOfMonth = day ?? throw new ArgumentException("A day of the month is required."),
                TimeOfDay = time
            };

        var onceDate = ParseRelativeDate(text, reference) ?? ParseExplicitDate(text, reference);
        if (onceDate.HasValue)
            return new()
            {
                Type = SchedulePatternType.Once,
                StartDate = onceDate,
                EndDate = onceDate,
                TimeOfDay = time
            };

        if (time.HasValue && !IsRecurring(text))
            return new()
            {
                Type = SchedulePatternType.Once,
                StartDate = DateOnly.FromDateTime(reference),
                EndDate = DateOnly.FromDateTime(reference),
                TimeOfDay = time
            };

        throw new ArgumentException($"Could not understand date/time expression '{expression}'.");
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static bool IsDaily(string text) =>
        Regex.IsMatch(text, @"\b(every|each)\s+day\b|\bdaily\b", RegexOptions.IgnoreCase);

    private static bool IsWeekdays(string text) =>
        Regex.IsMatch(text, @"\b(weekdays|every\s+weekday)\b", RegexOptions.IgnoreCase);

    private static bool IsWeekends(string text) =>
        Regex.IsMatch(text, @"\b(weekends|every\s+weekend)\b", RegexOptions.IgnoreCase);

    private static bool IsRecurring(string text) =>
        Regex.IsMatch(text, @"\b(every|each|weekly|weekdays|weekends)\b", RegexOptions.IgnoreCase);

    private static bool IsYearly(string text) =>
        Regex.IsMatch(text, @"\b(every|each)\s+(year|yearly)|\bannually\b", RegexOptions.IgnoreCase);

    private static bool IsMonthlyOrdinal(string text) =>
        Regex.IsMatch(text, @"\b(first|second|third|fourth|fifth|last)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(text, @"\b(month|monthly)\b", RegexOptions.IgnoreCase);

    private static bool IsMonthlyDay(string text) =>
        Regex.IsMatch(text, @"\b(every|each)\s+month\b|\bmonthly\b", RegexOptions.IgnoreCase);

    private static DateTime? ParseTimer(string text, DateTime reference)
    {
        var match = TimerRegex().Match(text);
        if (!match.Success)
            return null;

        var amount = match.Groups["amount"].Success
            ? double.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture)
            : 1;

        var unit = match.Groups["unit"].Value;
        var delay = unit switch
        {
            "second" or "seconds" or "sec" or "secs" => TimeSpan.FromSeconds(amount),
            "minute" or "minutes" or "min" or "mins" => TimeSpan.FromMinutes(amount),
            "hour" or "hours" or "hr" or "hrs" => TimeSpan.FromHours(amount),
            "day" or "days" => TimeSpan.FromDays(amount),
            _ => throw new ArgumentException("Unsupported timer duration.")
        };

        if (delay < TimeSpan.FromMinutes(1))
            throw new ArgumentException("I can only set alarms for one minute or longer.");

        // Alarms are minute-based. Always round up so "in 5 minutes and 20 seconds"
        // fires at the beginning of the next minute rather than losing time.
        var dueAt = reference.Add(delay);
        return new DateTime(
            dueAt.Year, dueAt.Month, dueAt.Day,
            dueAt.Hour, dueAt.Minute, 0, dueAt.Kind).AddMinutes(
                dueAt.Second == 0 && dueAt.Millisecond == 0 ? 0 : 1);
    }

    [GeneratedRegex(@"\bin\s+(?:(?<amount>\d+(?:\.\d+)?)\s+)?(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?)\b")]
    private static partial Regex TimerRegex();

    private static TimeSpan? ParseTime(string text)
    {
        var match = TimeRegex().Match(text);
        if (!match.Success)
            return null;

        var hourText = match.Groups["hour"].Success ? match.Groups["hour"].Value : match.Groups["hour2"].Value;
        var hour = int.Parse(hourText, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
            : 0;
        var meridiem = match.Groups["ampm"].Value.Replace(".", string.Empty, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(meridiem))
        {
            if (hour is < 1 or > 12 || minute > 59)
                throw new ArgumentException("Invalid time in date/time expression.");
            if (meridiem == "pm" && hour < 12) hour += 12;
            if (meridiem == "am" && hour == 12) hour = 0;
        }
        else if (hour > 23 || minute > 59)
            throw new ArgumentException("Invalid time in date/time expression.");

        return new TimeSpan(hour, minute, 0);
    }

    private static int? ParseOrdinal(string text) =>
        Regex.Match(text, @"\b(?<ordinal>first|second|third|fourth|fifth|last)\b") is { Success: true } m
            ? m.Groups["ordinal"].Value switch
            {
                "first" => 1, "second" => 2, "third" => 3,
                "fourth" => 4, "fifth" => 5, "last" => -1, _ => null
            }
            : null;

    private static DayOfWeek? ParseWeekday(string text)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
            if (Regex.IsMatch(text, $@"\b{day}\b", RegexOptions.IgnoreCase))
                return day;
        return null;
    }

    private static DayOfWeek[] ParseWeekdays(string text)
    {
        var result = Enum.GetValues<DayOfWeek>()
            .Where(day => Regex.IsMatch(text, $@"\b{day}\b", RegexOptions.IgnoreCase))
            .ToArray();
        return result;
    }

    private static int? ParseMonth(string text)
    {
        for (var i = 0; i < MonthNames.Length; i++)
            if (Regex.IsMatch(text, $@"\b{MonthNames[i].ToLowerInvariant()}\b", RegexOptions.IgnoreCase))
                return i + 1;
        return null;
    }

    private static int? ParseNumericDate(string text, DateTime reference, int? month)
    {
        var match = Regex.Match(text, @"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\b");
        if (!match.Success) return null;
        var value = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        return value is >= 1 and <= 31 ? value : null;
    }

    private static DateOnly? ParseRelativeDate(string text, DateTime reference)
    {
        if (Regex.IsMatch(text, @"\btoday\b")) return DateOnly.FromDateTime(reference);
        if (Regex.IsMatch(text, @"\btomorrow\b")) return DateOnly.FromDateTime(reference.AddDays(1));
        if (Regex.IsMatch(text, @"\bday after tomorrow\b")) return DateOnly.FromDateTime(reference.AddDays(2));

        var weekday = ParseWeekday(text);
        if (weekday is null || !Regex.IsMatch(text, @"\b(next|this)\b")) return null;

        var current = (int)reference.DayOfWeek;
        var target = (int)weekday.Value;
        var delta = (target - current + 7) % 7;
        if (Regex.IsMatch(text, @"\bnext\b") || delta == 0) delta = delta == 0 ? 7 : delta;
        return DateOnly.FromDateTime(reference.AddDays(delta));
    }

    private static DateOnly? ParseExplicitDate(string text, DateTime reference)
    {
        var iso = Regex.Match(text, @"\b(?<y>20\d{2})[-/](?<m>\d{1,2})[-/](?<d>\d{1,2})\b");
        if (iso.Success &&
            DateTime.TryParse($"{iso.Groups["y"].Value}-{iso.Groups["m"].Value}-{iso.Groups["d"].Value}", out var value))
            return DateOnly.FromDateTime(value);

        var month = ParseMonth(text);
        var day = ParseNumericDate(text, reference, month);
        if (!month.HasValue || !day.HasValue) return null;

        var year = reference.Year;
        var candidate = new DateOnly(year, month.Value, day.Value);
        if (candidate < DateOnly.FromDateTime(reference.Date))
            candidate = candidate.AddYears(1);
        return candidate;
    }

    [GeneratedRegex(@"(?:\bat\s+)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>a\.?m\.?|p\.?m\.?)\b|\bat\s+(?<hour2>\d{1,2})\b")]
    private static partial Regex TimeRegex();

    private static (DateOnly Start, DateOnly End)? ParseDateRange(string text, DateTime reference)
    {
        var match = DateRangeRegex().Match(text);
        if (!match.Success)
            return null;

        var start = ParseMonthDay(match.Groups["sm"].Value, match.Groups["sd"].Value, reference);
        var end = ParseMonthDay(match.Groups["em"].Value, match.Groups["ed"].Value, reference, start.Year);
        if (end < start)
            end = end.AddYears(1);

        return (start, end);
    }

    private static DateOnly ParseMonthDay(string monthText, string dayText, DateTime reference, int? preferredYear = null)
    {
        var month = Array.FindIndex(MonthNames, x => x.Equals(monthText, StringComparison.OrdinalIgnoreCase)) + 1;
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        if (month < 1 || day < 1 || day > DateTime.DaysInMonth(preferredYear ?? reference.Year, month))
            throw new ArgumentException("Invalid date in date/time expression.");

        var candidate = new DateOnly(preferredYear ?? reference.Year, month, day);
        if (!preferredYear.HasValue && candidate < DateOnly.FromDateTime(reference.Date))
            candidate = candidate.AddYears(1);
        return candidate;
    }

    [GeneratedRegex(@"\b(?<sm>[a-z]+)\s+(?<sd>\d{1,2})(?:st|nd|rd|th)?\s+(?:through|to|-)\s*(?<em>[a-z]+)\s+(?<ed>\d{1,2})(?:st|nd|rd|th)?\b")]
    private static partial Regex DateRangeRegex();
}
