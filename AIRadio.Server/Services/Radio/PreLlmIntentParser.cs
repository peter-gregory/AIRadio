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
            return Request("weatherCurrent");

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

        if (IsRadioCurrentCommand(normalized))
            return Request("radioCurrent");

        if (IsRadioSongCommand(normalized))
            return Request("radioSong");

        if (IsRadioSaveCommand(normalized))
            return Request("radioSave");

        if (IsRadioFavoriteCommand(normalized))
            return Request("radioFavorite");

        if (IsRadioUnfavoriteCommand(normalized))
            return Request("radioUnfavorite");

        if (IsRadioRemoveCommand(normalized))
            return Request("radioRemove");

        if (IsRadioStatusCommand(normalized))
            return Request("radioStatus");

        if (IsRadioPlaylistCommand(normalized))
            return Request("radioPlaylist");

        if (IsLocationGetCommand(normalized))
            return Request("locationGet");

        if (IsSavedRadioPlaybackCommand(normalized))
            return Request("radioPlay");

        var radioRequest = TryParseRadioCommand(text);
        if (radioRequest is not null)
            return radioRequest;

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

    private static bool IsRadioCurrentCommand(string text) =>
        text is
            "what station is this" or
            "what radio station is this" or
            "what station am i listening to" or
            "what radio station am i listening to" or
            "which station is this" or
            "which radio station is this" or
            "what's the current station" or
            "what is the current station";

    private static bool IsRadioSongCommand(string text) =>
        text is
            "what song is playing" or
            "what song is playing right now" or
            "what's playing right now" or
            "what is playing right now" or
            "what track is playing" or
            "who is the current song by";

    private static bool IsRadioSaveCommand(string text) =>
        text is
            "save this station" or
            "save the station" or
            "remember this station" or
            "remember the station" or
            "keep this station";

    private static bool IsRadioFavoriteCommand(string text) =>
        text is
            "favorite this station" or
            "favorite the station" or
            "make this a favorite" or
            "make the station a favorite" or
            "add this station to favorites" or
            "add the station to my favorites";

    private static bool IsRadioUnfavoriteCommand(string text) =>
        text is
            "unfavorite this station" or
            "unfavorite the station" or
            "i don't like this station anymore" or
            "i do not like this station anymore" or
            "remove this one from my favorites" or
            "remove this station from my favorites" or
            "remove the station from my favorites";

    private static bool IsRadioRemoveCommand(string text) =>
        text is
            "remove" or
            "remove station" or
            "remove this station" or
            "remove this one" or
            "delete station" or
            "delete this station" or
            "delete this one" or
            "get rid of this station" or
            "get rid of this one" or
            "forget this station" or
            "forget this one" or
            "unsave this station" or
            "remove this station from my saved stations";

    private static bool IsRadioStatusCommand(string text) =>
        text is
            "radio status" or
            "what's the radio status" or
            "what is the radio status" or
            "is the radio playing" or
            "is the radio stopped" or
            "is the radio paused" or
            "is the radio muted" or
            "what is the radio volume" or
            "what's the radio volume";

    private static bool IsRadioPlaylistCommand(string text) =>
        text is
            "radio playlist" or
            "what stations are available" or
            "what radio stations are available" or
            "show me the radio stations" or
            "show me the stations" or
            "which stations can i play" or
            "what stations can i play" or
            "what stations are in the playlist";

    private static bool IsLocationGetCommand(string text) =>
        text is
            "where am i" or
            "what is my location" or
            "what's my location" or
            "where is the radio located" or
            "what is the radio location" or
            "what's the radio location";

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
            "play saved stations" or
            "play the saved stations" or
            "play saved radio stations" or
            "play my saved stations" or
            "play my saved radio stations" or
            "play my playlist" or
            "play my stations";

    /// <summary>
    /// Deterministically separates station-name requests from radio searches.
    /// Explicit search verbs always produce radioSearch. A play request is a
    /// station name when it has station-name structure (FM/AM/radio/network,
    /// a numeric group, or an uppercase call sign). Otherwise known
    /// descriptive music terms produce radioSearch.
    /// </summary>
    private static ToolRequest? TryParseRadioCommand(string text)
    {
        var command = CleanCommand(text);

        var searchMatch = SearchRadioRegex().Match(command);
        if (searchMatch.Success)
        {
            var query = CleanArgument(searchMatch.Groups["query"].Value);
            return string.IsNullOrWhiteSpace(query)
                ? null
                : Request("radioSearch", new JObject { ["query"] = query });
        }

        var playMatch = PlayRadioRegex().Match(command);
        if (!playMatch.Success)
            return null;

        var value = CleanArgument(playMatch.Groups["query"].Value);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (LooksLikeStationName(value) || !ContainsRadioSearchTerm(value))
            return Request("radioPlay", new JObject { ["stationName"] = value });

        return Request("radioSearch", new JObject { ["query"] = value });
    }

    private static bool LooksLikeStationName(string value)
    {
        if (Regex.IsMatch(value, @"(?:fm|am|radio|network)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])d+(?:.d+)?(?![A-Za-z0-9])"))
            return true;

        return Regex.IsMatch(value, @"(?<![A-Za-z])(?:[A-Z]{3,5})(?![A-Za-z])");
    }

    private static bool ContainsRadioSearchTerm(string value)
    {
        foreach (var term in RadioSearchTerms)
        {
            if (Regex.IsMatch(
                    value,
                    $@"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static string CleanCommand(string text) =>
        Regex.Replace(
            text.Trim().TrimEnd('.', '!', '?'),
            @"s+",
            " ")
            .Trim();

    private static string CleanArgument(string text) =>
        Regex.Replace(
            text.Trim().TrimEnd('.', '!', '?'),
            @"s+",
            " ")
            .Trim(' ', ',', ';', ':', '-');

    private static readonly string[] RadioSearchTerms =
    [
        "unplugged", "smooth", "classic", "soft", "hard", "light",
        "easy listening", "ambient", "chill", "chillout", "lounge",
        "acoustic", "rock", "alternative", "indie", "jazz", "blues",
        "country", "folk", "pop", "rap", "hip hop", "hip-hop", "r&b",
        "soul", "funk", "disco", "dance", "electronic", "edm", "house",
        "techno", "trance", "metal", "punk", "reggae", "ska", "gospel",
        "christian", "latin", "salsa", "reggaeton", "oldies", "hits",
        "music", "news", "sports", "talk", "comedy", "podcast",
        "spanish", "english", "french", "german", "italian", "portuguese",
        "dutch", "russian", "ukrainian", "polish", "turkish", "arabic",
        "hebrew", "greek", "hindi", "tamil", "telugu", "bengali",
        "punjabi", "japanese", "korean", "chinese"
    ];

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

    [GeneratedRegex(@"^(?:find|search(?: for)?|look for|look up|give me|show me)\s+(?:a\s+)?(?:radio\s+)?(?:station\s+)?(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchRadioRegex();

    [GeneratedRegex(@"^(?:play|listen to|tune to|turn on)\s+(?:the\s+)?(?:radio\s+)?(?:station\s+)?(?:some\s+)?(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayRadioRegex();

    [GeneratedRegex(@"^(?:set\s+(?:the\s+)?(?:radio\s+)?volume|(?:radio\s+)?volume|turn\s+(?:the\s+)?(?:radio\s+)?volume)\s*(?:to|at)?\s*(?<value>.+?)(?:\s+percent)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeRegex();
}
