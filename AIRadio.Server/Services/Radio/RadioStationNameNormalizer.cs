using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

internal static partial class RadioStationNameNormalizer
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Radio Browser commonly appends alternate names and stream
        // metadata after a pipe. The first segment is the station's
        // human-facing name.
        var canonicalName = name.Split('|', 2)[0].Trim();

        if (canonicalName.Length == 0)
            return string.Empty;

        canonicalName = ControlCharacterRegex().Replace(canonicalName, " ");
        canonicalName = WhitespaceRegex().Replace(canonicalName, " ").Trim();

        // Keep short all-uppercase call signs speakable by Piper.
        if (CallSignRegex().IsMatch(canonicalName))
            return string.Join(' ', canonicalName.ToCharArray());

        return canonicalName;
    }

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]+")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[A-Z]{3,5}$")]
    private static partial Regex CallSignRegex();
}
