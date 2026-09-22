using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Alarms;

public static partial class AlarmActionsParser
{
    public static IReadOnlyList<string> Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var text = Normalize(expression);
        var parts = ActionSeparatorRegex().Split(text);

        return parts
            .Select(Clean)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static string Clean(string value)
    {
        var text = value.Trim(' ', ',', ';', '.');
        text = LeadingSeparatorRegex().Replace(text, string.Empty);
        return text.Trim();
    }

    [GeneratedRegex(@"\s*(?:[,;]|\bthen\b|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ActionSeparatorRegex();

    [GeneratedRegex(@"^(?:(?:and|then)\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSeparatorRegex();
}
