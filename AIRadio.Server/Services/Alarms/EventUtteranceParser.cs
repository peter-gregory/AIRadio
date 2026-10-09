using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Alarms;

public sealed record EventUtterance(
    string? WhenExpression,
    string? Content);

public static class EventUtteranceParser
{
    private static readonly string MonthPattern =
        @"(?:january|february|march|april|may|june|july|august|september|october|november|december)";

    private static readonly string WeekdayPattern =
        @"(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)";

    public static EventUtterance Parse(string text, DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var input = NormalizeSpokenDateOrdinals(Normalize(text));

        if (string.IsNullOrWhiteSpace(input))
            return new(null, null);

        var date = FindDateExpression(input, now);
        if (date is null)
            return new(null, CleanContent(input));

        var content = ExtractContent(input, date.Value);
        return new(date.Value.Expression, content);
    }

    public static string? TryExtractWhen(string text, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var input = NormalizeSpokenDateOrdinals(Normalize(text));
        var date = FindDateExpression(input, now);

        if (date is not null)
            return date.Value.Expression;

        try
        {
            RecurrenceTimeRangeParser.Parse(input, now);
            return input;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (string Expression, int Start, int Length)? FindDateExpression(
        string text,
        DateTime? now)
    {
        var patterns = new[]
        {
            $@"\b(?:the\s+)?(?:first|second|third|fourth|fifth|last)\s+{WeekdayPattern}\s+of\s+(?:every|each)\s+month\b",
            $@"\b(?:every|each)\s+{WeekdayPattern}(?:\s+(?:and\s+)?{WeekdayPattern})+(?:\s+and\s+{WeekdayPattern})?\b",
            $@"\b(?:every|each)\s+(?:weekday|weekend|day|daily|weekly)\b",
            $@"\b{MonthPattern}\s+\d{{1,2}}(?:st|nd|rd|th)?\s+(?:through|to|-)\s*{MonthPattern}\s+\d{{1,2}}(?:st|nd|rd|th)?\b",
            $@"\b(?:every|each)\s+{MonthPattern}\s+\d{{1,2}}(?:st|nd|rd|th)?(?:\s+\d{{4}})?\b",
            $@"\b{MonthPattern}\s+\d{{1,2}}(?:st|nd|rd|th)?(?:\s+\d{{4}})?\b",
            @"\b20\d{2}[-/]\d{1,2}[-/]\d{1,2}\b",
            @"\bday after tomorrow\b",
            @"\btomorrow\b",
            @"\btoday\b",
            $@"\b(?:(?:this|next)\s+)?{WeekdayPattern}\b"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var expression = TrimDateExpression(match.Value);

                try
                {
                    RecurrenceTimeRangeParser.Parse(expression, now);
                    var length = match.Length;
                    var remainder = text[(match.Index + length)..];
                    var time = Regex.Match(
                        remainder,
                        @"^\s+at\s+\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?)\b",
                        RegexOptions.IgnoreCase);

                    if (time.Success)
                    {
                        length += time.Length;
                        expression = TrimDateExpression(text.Substring(match.Index, length));
                    }

                    return (expression, match.Index, length);
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static string ExtractContent(
        string input,
        (string Expression, int Start, int Length) date)
    {
        var before = input[..date.Start].Trim();
        var after = input[(date.Start + date.Length)..].Trim();

        var afterContent = CleanContent(after);
        if (!string.IsNullOrWhiteSpace(afterContent))
            return afterContent;

        return CleanContent(before);
    }

    private static string CleanContent(string text)
    {
        var value = text.Trim().Trim(',', '.', ':', ';', '-', ' ');

        value = Regex.Replace(
            value,
            @"^(?:and\s+)?(?:tell\s+me|say\s+to\s+me)\s+",
            string.Empty,
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"^(?:and\s+)?say\s+",
            string.Empty,
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"^and\s+",
            string.Empty,
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"^(?:and\s+)?to\s+",
            string.Empty,
            RegexOptions.IgnoreCase);

        return value.Trim();
    }

    private static string NormalizeSpokenDateOrdinals(string text)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
            ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8,
            ["ninth"] = 9, ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12,
            ["thirteenth"] = 13, ["fourteenth"] = 14, ["fifteenth"] = 15,
            ["sixteenth"] = 16, ["seventeenth"] = 17, ["eighteenth"] = 18,
            ["nineteenth"] = 19, ["twentieth"] = 20, ["twenty-first"] = 21,
            ["twenty-second"] = 22, ["twenty-third"] = 23, ["twenty-fourth"] = 24,
            ["twenty-fifth"] = 25, ["twenty-sixth"] = 26, ["twenty-seventh"] = 27,
            ["twenty-eighth"] = 28, ["twenty-ninth"] = 29, ["thirtieth"] = 30,
            ["thirty-first"] = 31
        };

        var ordinalPattern = string.Join("|", ordinals.Keys
            .OrderByDescending(value => value.Length)
            .Select(Regex.Escape));

        return Regex.Replace(
            text,
            $@"\b(?<month>{MonthPattern})\s+(?<day>{ordinalPattern})\b",
            match => $"{match.Groups["month"].Value} {ordinals[match.Groups["day"].Value]}",
            RegexOptions.IgnoreCase);
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static string TrimDateExpression(string expression)
    {
        var value = expression.Trim().Trim(',', '.', ':', ';', ' ');
        return Regex.Replace(value, @"\s+", " ");
    }
}
