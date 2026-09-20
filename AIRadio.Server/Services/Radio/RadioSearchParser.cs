using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

public static partial class RadioSearchParser
{
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "english", "spanish", "french", "german", "italian", "portuguese",
        "dutch", "russian", "ukrainian", "polish", "turkish", "arabic",
        "hebrew", "greek", "hindi", "tamil", "telugu", "bengali",
        "punjabi", "japanese", "korean", "chinese"
    };

    private static readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        "pop", "rock", "jazz", "blues", "classical", "country", "folk",
        "hip hop", "hip-hop", "rap", "r&b", "soul", "funk", "disco",
        "dance", "electronic", "edm", "house", "techno", "trance",
        "metal", "punk", "indie", "alternative", "reggae", "ska",
        "gospel", "christian", "latin", "salsa", "reggaeton", "oldies",
        "80s", "90s", "2000s", "70s", "60s", "news", "sports", "talk",
        "ambient", "chillout", "lounge", "easy listening",
        "classic rock", "soft rock", "hard rock", "top 40", "hits",
        "music", "comedy", "podcast"
    };

    private static readonly HashSet<string> SearchFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "smooth", "mellow", "relaxing", "relaxed", "upbeat",
        "something", "something like", "some", "best", "good", "popular"
    };

    private static readonly string[] CommandPrefixes =
    [
        "please play ",
        "please find ",
        "please search for ",
        "play ",
        "find ",
        "search for ",
        "search ",
        "look for ",
        "give me ",
        "show me "
    ];

    public static string Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var request = Normalize(text);

        if (request.Length == 0)
            return string.Empty;

        var parameters = new List<string>();
        var tags = new List<string>();

        // An explicit "plays X" phrase is an artist/topic criterion.
        // Keep the complete criterion together rather than treating X as a
        // station name.
        var playsMatch = PlaysRegex().Match(request);
        if (playsMatch.Success)
        {
            var value = RemoveFillers(playsMatch.Groups["value"].Value);
            var locationMatch = LocationRegex().Match(value);

            if (locationMatch.Success)
            {
                var location = CleanValue(locationMatch.Groups["location"].Value);
                if (location.Length > 0)
                {
                    Add(parameters, "city", location);
                    value = CleanValue(
                        value.Remove(locationMatch.Index, locationMatch.Length));
                }
            }

            value = RemoveFillers(value);
            if (value.Length > 0)
                Add(parameters, "tag", value);

            return string.Join("&", parameters);
        }

        var locationMatch = LocationRegex().Match(request);
        if (locationMatch.Success)
        {
            var location = CleanValue(locationMatch.Groups["location"].Value);
            if (location.Length > 0)
            {
                Add(parameters, "city", location);
                request = CleanValue(
                    request.Remove(locationMatch.Index, locationMatch.Length));
            }
        }

        foreach (var language in Languages.OrderByDescending(x => x.Length))
        {
            var match = Regex.Match(
                request,
                $@"\b{Regex.Escape(language)}\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            Add(parameters, "language", language);
            request = CleanValue(request.Remove(match.Index, match.Length));
            break;
        }

        var matchedTags = Tags
            .OrderByDescending(x => x.Length)
            .Where(tag => Regex.IsMatch(
                request,
                $@"(?<![\w-]){Regex.Escape(tag)}(?![\w-])",
                RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tag in matchedTags)
        {
            tags.Add(tag);
            request = Regex.Replace(
                request,
                $@"(?<![\w-]){Regex.Escape(tag)}(?![\w-])",
                " ",
                RegexOptions.IgnoreCase);
        }

        request = RemoveFillers(request);

        if (request.Length > 0 &&
            !parameters.Any(x => x.StartsWith("city=", StringComparison.OrdinalIgnoreCase)) &&
            tags.Count > 0 &&
            LooksLikeLocation(request))
        {
            Add(parameters, "city", request);
            request = string.Empty;
        }

        if (request.Length > 0 && parameters.Count > 0)
        {
            tags.Add(request);
            request = string.Empty;
        }

        if (tags.Count > 0)
        {
            tags = tags
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tags.Count == 1)
                Add(parameters, "tag", tags[0]);
            else
                Add(parameters, "tagList", string.Join(",", tags));
        }

        if (parameters.Count == 0)
        {
            if (LooksLikeStationName(request))
                Add(parameters, "name", request);
            else
                Add(parameters, "tag", request);
        }

        return string.Join("&", parameters);
    }

    private static string Normalize(string text)
    {
        var value = text.Trim().TrimEnd('.', '?', '!');

        foreach (var prefix in CommandPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..].Trim();
                break;
            }
        }

        value = Regex.Replace(
            value,
            @"^(?:a|an|the|some)\s+(?:radio\s+)?(?:station\s+)?",
            string.Empty,
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"\b(?:radio\s+)?station\s+(?:that\s+)?(?:plays|playing)\s+",
            "plays ",
            RegexOptions.IgnoreCase);

        value = Regex.Replace(
            value,
            @"\b(?:radio|station)\b$",
            string.Empty,
            RegexOptions.IgnoreCase);

        return CleanValue(value);
    }

    private static string RemoveFillers(string value)
    {
        value = CleanValue(value);

        foreach (var filler in SearchFillers.OrderByDescending(x => x.Length))
        {
            value = Regex.Replace(
                value,
                $@"(?<![\w-]){Regex.Escape(filler)}(?![\w-])",
                " ",
                RegexOptions.IgnoreCase);
        }

        return CleanValue(value);
    }

    private static string CleanValue(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        value = value.Trim(',', ';', '-', ':');
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static bool LooksLikeStationName(string value)
    {
        return Regex.IsMatch(
            value,
            @"(?:\bfm\b|\bam\b|\bradio\b|\bnetwork\b|\bbroadcast\b)",
            RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeLocation(string value)
    {
        // Proper names are a useful fallback for requests such as
        // "Nashville country". We deliberately keep this conservative.
        return value.Any(char.IsUpper);
    }

    private static void Add(
        ICollection<string> parameters,
        string name,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        parameters.Add(
            $"{name}={Uri.EscapeDataString(value.Trim())}");
    }

    [GeneratedRegex(@"\b(?:plays|playing)\s+(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaysRegex();

    [GeneratedRegex(@"\b(?:in|from|near|around)\s+(?<location>[^,]+?)(?=\s+(?:for|with|playing|plays|that|on)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LocationRegex();
}
