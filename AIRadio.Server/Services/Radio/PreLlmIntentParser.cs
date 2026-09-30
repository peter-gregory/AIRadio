using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

/// <summary>
/// Handles simple, unambiguous spoken commands before the LLM intent classifier.
/// This parser is deliberately conservative: commands that require interpretation
/// or contain additional criteria fall through to the normal LLM pipeline.
/// </summary>
public interface IPreLlmIntentParser
{
    ToolRequest? TryParse(string text);
}

public sealed partial class PreLlmIntentParser : IPreLlmIntentParser
{
    public ToolRequest? TryParse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var normalized = NormalizeText(text);

        if (TryParseVolume(normalized, out var volume))
            return Request("radioVolume", new JObject { ["volume"] = volume });

        if (IsGreetingCommand(normalized))
            return Request("greeting");

        if (IsNewsCommand(normalized))
            return Request("news");

        if (IsWeatherCommand(normalized))
            return Request("weather-current");

        if (IsEventsCommand(normalized))
            return Request("events");

        if (IsDateTimeCommand(normalized))
            return Request("datetime");

        if (IsDateCommand(normalized))
            return Request("date");

        if (IsTimeCommand(normalized))
            return Request("time");

        if (IsRadioStopCommand(normalized))
            return Request("radioStop");

        if (IsRadioNextCommand(normalized))
            return Request("radioNext");

        if (IsRadioPreviousCommand(normalized))
            return Request("radioPrevious");

        if (IsSavedRadioPlaybackCommand(normalized))
            return Request("radioPlay");

        return null;
    }

    private static bool TryParseVolume(string text, out int volume)
    {
        volume = 0;

        var match = VolumeRegex().Match(text);
        if (!match.Success)
            return false;

        var valueText = SpokenNumberNormalizer.Normalize(match.Groups["value"].Value)
            .Trim()
            .TrimEnd('%')
            .Trim();

        if (!int.TryParse(valueText, out volume))
            return false;

        return volume is >= 0 and <= 100;
    }

    private static bool IsGreetingCommand(string text) =>
        text is
            "greeting" or
            "a greeting" or
            "play a greeting" or
            "speak a greeting" or
            "say a greeting" or
            "give me a greeting" or
            "give me the greeting";

    private static bool IsNewsCommand(string text) =>
        text is
            "news" or
            "the news" or
            "play news" or
            "play the news" or
            "get news" or
            "get the news" or
            "give me the news" or
            "what's the news" or
            "what is the news" or
            "news report" or
            "give me a news report";

    private static bool IsWeatherCommand(string text) =>
        text is
            "weather" or
            "the weather" or
            "play weather" or
            "play the weather" or
            "get weather" or
            "get the weather" or
            "check the weather" or
            "check weather" or
            "what's the weather" or
            "what is the weather" or
            "what's the current weather" or
            "what is the current weather" or
            "weather report" or
            "give me a weather report";

    private static bool IsEventsCommand(string text) =>
        text is
            "events" or
            "my events" or
            "play events" or
            "play the events" or
            "get events" or
            "get my events" or
            "get the events" or
            "what are the events" or
            "what are my events" or
            "what events do i have" or
            "tell me my events" or
            "events report";

    private static bool IsDateTimeCommand(string text) =>
        text is
            "date and time" or
            "the date and time" or
            "get the date and time" or
            "what is the date and time" or
            "what's the date and time" or
            "what is today's date and time" or
            "what's today's date and time";

    private static bool IsDateCommand(string text) =>
        text is
            "date" or
            "the date" or
            "get the date" or
            "what is the date" or
            "what's the date" or
            "what day is it" or
            "what is today's date" or
            "what's today's date";

    private static bool IsTimeCommand(string text) =>
        text is
            "time" or
            "the time" or
            "get the time" or
            "what time is it" or
            "what's the time" or
            "what is the time" or
            "current time";

    private static bool IsRadioStopCommand(string text) =>
        text is
            "stop the radio" or
            "stop radio" or
            "turn off the radio" or
            "turn off radio" or
            "quit the radio" or
            "quit radio";

    private static bool IsRadioNextCommand(string text) =>
        text is
            "next" or
            "next station" or
            "radio next" or
            "play the next station" or
            "go to the next station" or
            "skip to the next station";

    private static bool IsRadioPreviousCommand(string text) =>
        text is
            "previous" or
            "previous station" or
            "radio previous" or
            "play the previous station" or
            "go to the previous station" or
            "skip to the previous station";

    private static bool IsSavedRadioPlaybackCommand(string text) =>
        text is
            "play the radio" or
            "play radio" or
            "play some music" or
            "play my favorites" or
            "play my saved stations" or
            "play my saved radio stations";

    private static string NormalizeText(string text) =>
        Regex.Replace(
            text.Trim().TrimEnd('.', '!', '?'),
            @"\s+",
            " ")
            .ToLowerInvariant();

    private static ToolRequest Request(string name, JObject? arguments = null) =>
        new()
        {
            Name = name,
            Arguments = arguments ?? new JObject(),
            State = ToolRequestState.Initial
        };

    [GeneratedRegex(@"^(?:set\s+(?:the\s+)?(?:radio\s+)?volume|(?:radio\s+)?volume|turn\s+(?:the\s+)?(?:radio\s+)?volume)\s*(?:to|at)?\s*(?<value>.+?)(?:\s+percent)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeRegex();
}
