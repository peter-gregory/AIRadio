using System.Text;

namespace AIRadio.Server.Services.Audio;

public static class SentenceParser
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr.", "mr.", "mrs.", "ms.", "miss.", "prof.", "sr.", "jr.",
        "st.", "ave.", "rd.", "blvd.", "mt.", "ft.", "no.", "fig.",
        "inc.", "ltd.", "etc.", "e.g.", "i.e.", "vs.", "approx."
    };

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var sentences = new List<string>();
        var start = 0;
        var i = 0;
        var inQuote = false;
        var inParentheses = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is '"' or '\u201C' or '\u201D')
            {
                inQuote = !inQuote;
                i++;
                continue;
            }

            if (c == '(')
            {
                inParentheses++;
                i++;
                continue;
            }

            if (c == ')' && inParentheses > 0)
            {
                inParentheses--;
                i++;
                continue;
            }

            if (c is '!' or '?')
            {
                var boundaryEnd = ConsumePunctuationRun(text, i + 1);
                var end = boundaryEnd;

                if (inQuote && end < text.Length && text[end] is '"' or '\u201D')
                    end++;

                if (ShouldSplit(text, end, inParentheses, allowPunctuationBoundary: true))
                {
                    AddSentence(sentences, text, start, end);
                    start = end;
                    i = end;
                    continue;
                }

                i = boundaryEnd;
                continue;
            }

            if (c == '.' && IsSentencePeriod(text, i))
            {
                var end = ConsumePeriod(text, i);

                if (ShouldSplit(text, end, inParentheses, allowPunctuationBoundary: false))
                {
                    AddSentence(sentences, text, start, end);
                    start = end;
                    i = end;
                    continue;
                }

                i = end;
                continue;
            }

            i++;
        }

        AddSentence(sentences, text, start, text.Length);
        return sentences;
    }

    private static bool IsSentencePeriod(string text, int index)
    {
        if (index > 0 && index + 1 < text.Length &&
            char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]))
            return false;

        var tokenStart = index;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]) &&
               !"()[]{}\"'".Contains(text[tokenStart - 1]))
            tokenStart--;

        var token = text[tokenStart..(index + 1)];

        if (Abbreviations.Contains(token))
            return false;

        if (token.Length == 2 && char.IsLetter(token[0]))
            return false;

        if (index + 1 < text.Length && text[index + 1] == '.')
            return false;

        if (index > 0 && char.IsDigit(text[index - 1]))
            return true;

        return true;
    }

    private static int ConsumePeriod(string text, int index)
    {
        var end = index + 1;
        while (end < text.Length && text[end] == '.')
            end++;
        return end;
    }

    private static int ConsumePunctuationRun(string text, int index)
    {
        var end = index;
        while (end < text.Length && text[end] is '!' or '?')
            end++;
        return end;
    }

    private static bool ShouldSplit(
        string text,
        int end,
        int parenthesesDepth,
        bool allowPunctuationBoundary)
    {
        if (parenthesesDepth > 0)
            return false;

        if (end >= text.Length)
            return true;

        if (text[end] is '"' or '\u201D' or '\u2019' or ')' or ']' or '}')
        {
            end++;
            while (end < text.Length && text[end] is '"' or '\u201D' or '\u2019' or ')' or ']' or '}')
                end++;
        }

        if (end >= text.Length)
            return true;

        return char.IsWhiteSpace(text[end]) &&
               (allowPunctuationBoundary || HasLikelySentenceStart(text, end));
    }

    private static bool HasLikelySentenceStart(string text, int punctuationEnd)
    {
        var index = punctuationEnd;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length)
            return true;

        return char.IsUpper(text[index]) || char.IsDigit(text[index]) || text[index] is '"' or '\u201C';
    }

    private static void AddSentence(List<string> sentences, string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        if (end > start)
            sentences.Add(text[start..end]);
    }
}
