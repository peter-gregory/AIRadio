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
        "smooth jazz", "classic rock", "soft rock", "hard rock", "easy listening",
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
        {
            Add(parameters, "tag", tags[0]);
        }
        else if (tags.Count > 1)
        {
            Add(parameters, "tagList", string.Join(",", tags));
        }
        else if (request.Length > 0)
        {
            var genre = FindGenre(request);

            Add(
                parameters,
                "tag",
                genre ?? request);
        }

        return string.Join("&", parameters);
    }

    /// <summary>
    /// Builds a broader station-name search for a descriptive query.
    /// This is used only after a tag/tagList search returns no stations.
    /// Location and language constraints are preserved when present.
    /// </summary>
    public static string ParseFallback(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var request = RemovePrefix(Clean(text));

        if (request.Length == 0)
            return string.Empty;

        var parameters = new List<string>();

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

        if (request.Length > 0)
            Add(parameters, "name", request);

        return string.Join("&", parameters);
    }

    private static string? FindGenre(string request)
    {
        var normalizedRequest = Normalize(request);

        if (normalizedRequest.Length == 0)
            return null;

        var bestGenre = default(string);
        var bestScore = 0.0;

        foreach (var genre in Tags)
        {
            var score = GenreSimilarity(normalizedRequest, Normalize(genre));

            if (score <= bestScore)
                continue;

            bestScore = score;
            bestGenre = genre;
        }

        return bestScore >= 0.55 ? bestGenre : null;
    }

    private static double GenreSimilarity(string request, string genre)
    {
        if (request.Equals(genre, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var requestWords = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var genreWords = genre.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var wordScores =
            requestWords
                .Select(requestWord =>
                    genreWords.Max(genreWord =>
                        Similarity(requestWord, genreWord)))
                .ToArray();

        var averageWordScore =
            wordScores.Length == 0
                ? 0.0
                : wordScores.Average();

        var matchedWords =
            wordScores.Count(score => score >= 0.72);

        var tokenCoverage =
            genreWords.Length == 0
                ? 0.0
                : (double)matchedWords / genreWords.Length;

        var wholePhraseScore =
            Similarity(request, genre);

        return Math.Max(
            wholePhraseScore,
            (averageWordScore * 0.65) + (tokenCoverage * 0.35));
    }

    private static double Similarity(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var longest = Math.Max(left.Length, right.Length);

        if (longest == 0)
            return 1.0;

        return 1.0 - ((double)LevenshteinDistance(left, right) / longest);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost =
                    char.ToLowerInvariant(left[i - 1]) ==
                    char.ToLowerInvariant(right[j - 1])
                        ? 0
                        : 1;

                current[j] = Math.Min(
                    Math.Min(
                        current[j - 1] + 1,
                        previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            previous = current;
        }

        return previous[right.Length];
    }

    private static string Normalize(string value)
    {
        value = value.ToLowerInvariant();
        value = Regex.Replace(value, @"[^a-z0-9]+", " ");
        value = Regex.Replace(value, @"\s+", " ");
        return value.Trim();
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
