using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio
{
    public sealed class SpeechIntentRule
    {
        public string Name { get; init; } = string.Empty;
        public string Intent { get; init; } = string.Empty;
        public int Priority { get; init; }
        public string Expression { get; init; } = string.Empty;
    }

    public sealed record SpeechIntentMatch(
        string Intent,
        string RuleName,
        string Expression,
        string Text);

    public interface IRegexIntentParser
    {
        SpeechIntentMatch? Match(string text);
    }

    public sealed class RegexIntentParser : IRegexIntentParser
    {
        private readonly ILogger<RegexIntentParser> _logger;
        private readonly IReadOnlyList<CompiledRule> _rules;

        public RegexIntentParser(
            IConfiguration configuration,
            ILogger<RegexIntentParser> logger)
        {
            _logger = logger;

            var configuredRules = configuration
                .GetSection("SpeechIntents")
                .GetChildren()
                .Select(section => new SpeechIntentRule
                {
                    Name = section["Name"] ?? string.Empty,
                    Intent = section["Intent"] ?? string.Empty,
                    Priority = int.TryParse(section["Priority"], out var priority)
                        ? priority
                        : 0,
                    Expression = section["Expression"] ?? string.Empty
                })
                .ToList();

            if (configuredRules.Count == 0)
            {
                throw new InvalidOperationException(
                    "No speech intent rules are configured in SpeechIntents.");
            }

            _rules = configuredRules
                .OrderByDescending(rule => rule.Priority)
                .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CompileRule)
                .ToList();

            _logger.LogInformation(
                "Loaded {Count} regex speech intent rules.",
                _rules.Count);
        }

        public SpeechIntentMatch? Match(string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            foreach (var rule in _rules)
            {
                if (!rule.Regex.IsMatch(text))
                    continue;

                _logger.LogDebug(
                    "Speech intent matched rule {RuleName}: Intent={Intent}, Text={Text}.",
                    rule.Name,
                    rule.Intent,
                    text);

                return new SpeechIntentMatch(
                    rule.Intent,
                    rule.Name,
                    rule.Expression,
                    text);
            }

            return null;
        }

        private static CompiledRule CompileRule(SpeechIntentRule rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                throw new InvalidOperationException(
                    "Every speech intent rule must have a Name.");
            }

            if (string.IsNullOrWhiteSpace(rule.Intent))
            {
                throw new InvalidOperationException(
                    $"Speech intent rule '{rule.Name}' has no Intent.");
            }

            if (string.IsNullOrWhiteSpace(rule.Expression))
            {
                throw new InvalidOperationException(
                    $"Speech intent rule '{rule.Name}' has no Expression.");
            }

            try
            {
                var regex = new Regex(
                    rule.Expression,
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant |
                    RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(250));

                return new CompiledRule(
                    rule.Name,
                    rule.Intent,
                    rule.Priority,
                    rule.Expression,
                    regex);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid regex in speech intent rule '{rule.Name}': {rule.Expression}",
                    ex);
            }
        }

        private sealed record CompiledRule(
            string Name,
            string Intent,
            int Priority,
            string Expression,
            Regex Regex);
    }
}
