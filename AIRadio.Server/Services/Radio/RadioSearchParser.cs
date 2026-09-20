using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

public static class RadioSearchParser
{
    private static readonly string[] Languages =
    [
        "english", "spanish", "french", "german", "italian", "portuguese",
        "dutch", "russian", "ukrainian", "polish", "turkish", "arabic",
        "hebrew", "greek", "hindi", "tamil", "telugu", "bengali",
        "punjabi", "japanese", "korean", "chinese"
    ];

    private static readonly string[] Tags =
    [
        "classic rock", "soft rock", "hard rock", "easy listening",
        "hip hop", "hip-hop", "r&b", "top 40",
        "pop", "rock", "jazz", "blues", "classical", "country", "folk",
        "rap", "soul", "funk", "disco", "dance", "electronic", "edm",
        "house", "techno", "trance", "metal", "punk", "indie",
        "alternative", "reggae", "ska", "gospel", "christian", "latin",
        "salsa", "reggaeton", "oldies", "80s", "90s", "2000s", "70s",
        "60s", "news", "sports", "talk", "ambient", "chillout", "lounge",
        "hits", "music", "comedy", "podcast"
    ];

    public static string Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var request = RemovePrefix(Clean(text));

        if (request.Length == 0)
            return string.Empty;

        var parameters = new List<string>();

        if (Regex.IsMatch(
                request,
                @"\b(?:fm|am|radio|network)\b",
                RegexOptions.IgnoreCase))
        {
            Add(parameters, "name", request);
            return string.Join("&", parameters);
        }

        var location = Regex.Match(
            request,
            @"\b(?:in|from|near|around)\s+(.+)$",
            RegexOptions.IgnoreCase);

        if (location.Success)
        {
            var value = Clean(location.Groups[1].Value);

            if (!value.Equals("news", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("the news", StringComparison.OrdinalIgnoreCase))
            {
                Add(parameters, "city", value);
                request = Clean(request[..location.Index]);
            }
        }

        foreach (var language in Languages)
        {
            var match = Regex.Match(
                request,
                $@"\b{Regex.Escape(language)}\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            Add(parameters, "language", language);
            request = Clean(request.Remove(match.Index, match.Length));
            break;
        }

        var tags = new List<string>();

        foreach (var tag in Tags)
        {
            var pattern = $@"(?<![\w-]){Regex.Escape(tag)}(?![\w-])";

            if (!Regex.IsMatch(request, pattern, RegexOptions.IgnoreCase))
                continue;

            tags.Add(tag);
            request = Regex.Replace(request, pattern, " ", RegexOptions.IgnoreCase);
        }

        request = Clean(request);

        if (tags.Count == 1)
            Add(parameters, "tag", tags[0]);
        else if (tags.Count > 1)
            Add(parameters, "tagList", string.Join(",", tags));
        else if (request.Length > 0)
            Add(parameters, "tag", request);

        return string.Join("&", parameters);
    }

    private static string RemovePrefix(string value)
    {
        string[] prefixes =
        [
            "please play ", "please find ", "please search for ",
            "play ", "find ", "search for ", "search ",
            "look for ", "give me ", "show me "
        ];

        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Clean(value[prefix.Length..]);
        }

        return value;
    }

    private static string Clean(string value)
    {
        value = value.Trim().TrimEnd('.', '?', '!');
        value = Regex.Replace(value, @"\s+", " ");
        return value.Trim(' ', ',', ';', ':', '-');
    }

    private static void Add(
        ICollection<string> parameters,
        string name,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
    }
}
