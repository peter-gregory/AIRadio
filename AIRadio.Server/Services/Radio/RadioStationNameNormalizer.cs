using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

internal static partial class RadioStationNameNormalizer
{
    private const int MaxWords = 8;

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var canonicalName = name.Trim();

        // Radio Browser station names frequently contain a human-facing
        // station name followed by alternate names, marketing text, or a
        // comma-separated genre list. Keep the first meaningful segment.
        canonicalName = PipeDelimiterRegex().Split(canonicalName, 2)[0];
        canonicalName = LeadingDelimiterRegex().Replace(canonicalName, string.Empty).Trim();

        // A spaced hyphen is a legitimate part of many station names
        // (for example "Radio Caprice - Acoustic Guitar"), so only treat it
        // as a delimiter when what follows looks like a long descriptor list.
        var hyphenMatch = GenreSuffixRegex().Match(canonicalName);
        if (hyphenMatch.Success)
            canonicalName = canonicalName[..hyphenMatch.Index].Trim();

        // Strong delimiters used by Radio Browser for appended metadata.
        var strongDelimiterMatch = StrongDelimiterRegex().Match(canonicalName);
        if (strongDelimiterMatch.Success)
            canonicalName = canonicalName[..strongDelimiterMatch.Index].Trim();

        canonicalName = ControlCharacterRegex().Replace(canonicalName, " ");
        canonicalName = WhitespaceRegex().Replace(canonicalName, " ").Trim();

        if (canonicalName.Length == 0)
            return string.Empty;

        var words = canonicalName.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > MaxWords)
            canonicalName = string.Join(' ', words.Take(MaxWords));

        // Keep short all-uppercase call signs speakable by Piper.
        if (CallSignRegex().IsMatch(canonicalName))
            return string.Join(' ', canonicalName.ToCharArray());

        return canonicalName;
    }

    [GeneratedRegex(@"\|+")]
    private static partial Regex PipeDelimiterRegex();

    [GeneratedRegex(@"^(?:[-|]+\s*)+")]
    private static partial Regex LeadingDelimiterRegex();

    // Only strip a spaced-hyphen suffix when it contains a comma-separated
    // descriptor/genre list. This preserves normal names such as
    // "Spoon Radio - Acoustic Rock".
    [GeneratedRegex(@"\s+-\s+(?=[^\r\n]*,)", RegexOptions.CultureInvariant)]
    private static partial Regex GenreSuffixRegex();

    [GeneratedRegex(@"\s+(?:---+|--+)\s+")]
    private static partial Regex StrongDelimiterRegex();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]+")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[A-Z]{3,5}$")]
    private static partial Regex CallSignRegex();
}
