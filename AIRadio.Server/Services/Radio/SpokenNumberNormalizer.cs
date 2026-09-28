using System.Globalization;
using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

public static partial class SpokenNumberNormalizer
{
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
    };

    private static readonly HashSet<string> ScaleWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "hundred", "thousand", "million", "billion"
        };

    public static string Normalize(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return text;

        var output = new List<string>();
        var index = 0;

        while (index < tokens.Count)
        {
            if (!IsNumberStart(tokens[index]))
            {
                output.Add(tokens[index]);
                index++;
                continue;
            }

            var end = FindNumberEnd(tokens, index);
            if (end == index)
            {
                output.Add(tokens[index]);
                index++;
                continue;
            }

            var phrase = tokens.GetRange(index, end - index);
            if (!TryParsePhrase(phrase, out var value))
            {
                output.AddRange(phrase);
                index = end;
                continue;
            }

            output.Add(value);
            index = end;
        }

        return string.Join(" ", output);
    }

    private static List<string> Tokenize(string text) =>
        text.Replace('-', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    private static bool IsNumberStart(string token) =>
        NumberWords.ContainsKey(token) ||
        ScaleWords.Contains(token) ||
        token.Equals("point", StringComparison.OrdinalIgnoreCase) ||
        DigitsRegex().IsMatch(token);

    private static int FindNumberEnd(IReadOnlyList<string> tokens, int start)
    {
        var index = start;
        var sawPoint = false;
        var sawNumber = false;

        while (index < tokens.Count)
        {
            var token = tokens[index];

            if (NumberWords.ContainsKey(token) || ScaleWords.Contains(token) || DigitsRegex().IsMatch(token))
            {
                if (sawPoint)
                {
                    // Decimal digits are individual digits after "point".
                    // Stop if the next token is a multi-digit literal so it can
                    // be preserved as the decimal value supplied by the user.
                    if (DigitsRegex().Match(token).Length > 1)
                    {
                        index++;
                        break;
                    }
                }

                sawNumber = true;
                index++;
                continue;
            }

            if (token.Equals("point", StringComparison.OrdinalIgnoreCase) && sawNumber && !sawPoint)
            {
                sawPoint = true;
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static bool TryParsePhrase(IReadOnlyList<string> tokens, out string value)
    {
        value = string.Empty;

        var pointIndex = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("point", StringComparison.OrdinalIgnoreCase))
            {
                pointIndex = i;
                break;
            }
        }

        var integerTokens = pointIndex >= 0
            ? tokens.Take(pointIndex).ToList()
            : tokens.ToList();

        if (!TryParseInteger(integerTokens, out var integerValue))
            return false;

        if (pointIndex < 0)
        {
            value = integerValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var decimalTokens = tokens.Skip(pointIndex + 1).ToList();
        if (decimalTokens.Count == 0)
            return false;

        var decimalDigits = new List<char>();

        foreach (var token in decimalTokens)
        {
            if (DigitsRegex().IsMatch(token))
            {
                if (token.Length == 1)
                {
                    decimalDigits.Add(token[0]);
                    continue;
                }

                // A spoken phrase such as "point 1" is expected to use
                // individual digits. Keep a literal multi-digit value intact.
                decimalDigits.AddRange(token);
                continue;
            }

            if (NumberWords.TryGetValue(token, out var digit) && digit <= 9)
            {
                decimalDigits.Add((char)('0' + digit));
                continue;
            }

            return false;
        }

        value = $"{integerValue.ToString(CultureInfo.InvariantCulture)}.{new string(decimalDigits.ToArray())}";
        return true;
    }

    private static bool TryParseInteger(IReadOnlyList<string> tokens, out long value)
    {
        value = 0;
        long current = 0;
        var sawNumber = false;

        foreach (var token in tokens)
        {
            if (NumberWords.TryGetValue(token, out var number))
            {
                current += number;
                sawNumber = true;
                continue;
            }

            switch (token.ToLowerInvariant())
            {
                case "hundred":
                    current = Math.Max(current, 1) * 100;
                    sawNumber = true;
                    break;

                case "thousand":
                    value += Math.Max(current, 1) * 1_000;
                    current = 0;
                    sawNumber = true;
                    break;

                case "million":
                    value += Math.Max(current, 1) * 1_000_000;
                    current = 0;
                    sawNumber = true;
                    break;

                case "billion":
                    value += Math.Max(current, 1) * 1_000_000_000;
                    current = 0;
                    sawNumber = true;
                    break;

                default:
                    return false;
            }
        }

        if (!sawNumber)
            return false;

        value += current;
        return true;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRegex();
}
