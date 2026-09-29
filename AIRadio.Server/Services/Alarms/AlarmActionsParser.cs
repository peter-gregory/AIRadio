using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Alarms;

public static partial class AlarmActionsParser
{
    public static IReadOnlyList<string> Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var text = Normalize(expression);
        var parts = ActionSeparatorRegex()
            .Split(text)
            .Select(Clean)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (parts.Count <= 1)
            return parts;

        var inheritedVerb = GetLeadingVerb(parts[0]);

        if (string.IsNullOrWhiteSpace(inheritedVerb))
            return parts;

        for (var i = 1; i < parts.Count; i++)
        {
            if (!HasLeadingVerb(parts[i]))
                parts[i] = $"{inheritedVerb} {parts[i]}";
        }

        return parts;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static string Clean(string value)
    {
        var text = value.Trim(' ', ',', ';', '.');
        text = LeadingSeparatorRegex().Replace(text, string.Empty);
        return text.Trim();
    }

    private static string? GetLeadingVerb(string value)
    {
        var match = LeadingVerbRegex().Match(value);
        return match.Success ? match.Groups["verb"].Value : null;
    }

    private static bool HasLeadingVerb(string value) =>
        LeadingVerbRegex().IsMatch(value);

    [GeneratedRegex(@"^(?<verb>[a-z]+(?:\s+[a-z]+){0,2})(?=\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVerbRegex();

    [GeneratedRegex(@"\s*(?:[,;]|\bthen\b|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ActionSeparatorRegex();

    [GeneratedRegex(@"^(?:(?:and|then)\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSeparatorRegex();
}
