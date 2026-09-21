using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.Audio;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Radio;

public abstract class RadioToolBase : ITool
{
    protected readonly IMpvManager Mpv;
    protected readonly IMpvState State;
    protected readonly IAudioManager Audio;
    protected RadioToolBase(IMpvManager mpv, IMpvState state, IAudioManager audio) { Mpv = mpv; State = state; Audio = audio; }
    public abstract string Name { get; }
    public abstract string Intent { get; }
    public virtual bool HasParameters => false;
    public virtual string GetLlmRequestTemplate() => $"{{tool:{Name}}}";
    public abstract string GetLlmInstructions();
    public abstract Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
    protected static void Validate(ToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
    }
    protected RadioStation? FindStation(ToolRequest request)
    {
        var id = request.GetString("stationId");
        if (!string.IsNullOrWhiteSpace(id))
            return State.RadioPlaylist.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        var name = request.GetString("stationName");
        if (!string.IsNullOrWhiteSpace(name))
            return State.RadioPlaylist.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

}

internal static class RadioSpeechFormatter
{
    public static string FormatStationNameForSpeech(string name)
    {
        if (Regex.IsMatch(name.Trim(), @"^[A-Z]{3,5}$"))
            return string.Join(' ', name.Trim().ToCharArray());

        return name;
    }
}

public sealed class RadioPlayTool : RadioToolBase
{
    private readonly IRadioSearchClient _search;
    private readonly ILogger<RadioPlayTool> _logger;

    public RadioPlayTool(IMpvManager mpv, IMpvState state, IAudioManager audio, IRadioSearchClient search, ILogger<RadioPlayTool> logger) : base(mpv, state, audio)
    {
        _search = search;
        _logger = logger;
    }
    public override string Name => "radioPlay";
    public override string Intent => "Play a known radio station by its exact station name or ID. A genre, style, mood, language, country, or other station characteristic is a radioSearch request.";
    public override bool HasParameters => true;
    public override string GetLlmRequestTemplate() => "{tool:radioPlay,stationId=<optional>,stationName=<optional>}";
    public override string GetLlmInstructions() => """
RADIO PLAY

Play a station, or start playback from the saved station list when no station is specified.

Parameters:
- stationId: Optional station ID. Use the exact ID from the current playlist. Station IDs are identifiers such as numeric IDs; do not put a station call sign, brand name, or descriptive name in stationId.
- stationName: Optional station name or call sign supplied by the user. Use stationName for names such as "Jazz FM", "Lightning 100", or "WRLT".
- Provide stationId or stationName when the user supplies a station identifier or station name.
- stationId and stationName are both optional. If neither is supplied, play the saved station list starting with its first station.
- When both are supplied, stationId takes precedence.

Important:
- A station name or ID must identify an actual station explicitly supplied by the user or returned by a previous search/playlist result.
- Do not turn a genre, style, mood, language, country, artist, or other descriptive term into stationName. For example, "light jazz" is a search criterion, not a station name.
- First check the current radio playlist for the requested station.
- If a stationName is not already in the playlist, use the station-name search service to resolve it.
- Do not invent station IDs or station names.
- Do not turn a genre, style, mood, language, country, artist, or other descriptive term into stationName.
- If the user says "play the radio", "play some music", or "play my favorites" without naming a station, load the saved station list and start with its first station.
- The saved list is ordered with the most recently favorited station first, followed by other saved stations.
- If the saved station list is empty, report that there are no saved stations.

Examples:
User: "Play Jazz FM"
{tool:radioPlay,stationName="Jazz FM"}

User: "Play station 12345"
{tool:radioPlay,stationId=12345}

User: "Play WRLT"
{tool:radioPlay,stationName="WRLT"}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var stationId = request.GetString("stationId");
        var stationName = request.GetString("stationName");
        if (request.State == ToolRequestState.Initial)
        {
            // Station tuning owns the complete audio transition. Prepare the
            // MPV state before the preamble is queued, so the old station stops
            // immediately and remains muted while the new station and response
            // speech are played. EndUtteranceAsync restores the prior volume.
            await Audio.PrepareStationChangeAsync(cancellationToken);
            await Audio.WaitForCompletionAsync(cancellationToken);
            var stationDescription = !string.IsNullOrWhiteSpace(stationName)
                ? stationName
                : $"station {stationId}";

            await Audio.QueueSpeechAsync(
                $"Looking for station {stationDescription} now {{sound:radio-tuning}}",
                cancellationToken);

            return ToolResult.Preamble(
                Name,
                string.Empty,
                request.WithState(ToolRequestState.PreambleComplete));
        }

        if (string.IsNullOrWhiteSpace(stationId) && string.IsNullOrWhiteSpace(stationName))
        {
            var savedStations = _stationStore.GetSavedStations();

            if (savedStations.Count == 0)
            {
                const string speech = "I don't have any saved radio stations yet.";
                return ToolResult.Successful(Name, speech, new { SavedStations = 0 }, speech, true);
            }

            _logger.LogInformation("Starting saved radio playlist with {Count} station(s).", savedStations.Count);
            Mpv.SetRadioPlaylist(savedStations, RadioPlaylistSource.Search);
            var savedStation = savedStations[0];

            try
            {
                await Audio.PlayStationAsync(savedStation, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ToolResult.Failed(
                    Name,
                    $"Unable to tune {savedStation.Name}: {ex.Message}",
                    "{sound:radio-static} I'm sorry, I wasn't able to tune that station.",
                    complete: true);
            }

            var savedSpokenName = RadioSpeechFormatter.FormatStationNameForSpeech(savedStation.Name);
            var savedSpeech = $"Now playing {savedSpokenName}.";

            return ToolResult.Successful(
                Name,
                $"Playing {savedStation.Name}.",
                new { Station = savedStation, SavedStationCount = savedStations.Count },
                savedSpeech,
                true);
        }

        var station = FindStation(request);
        if (station is null && !string.IsNullOrWhiteSpace(stationName))
        {
            var searchQuery = stationName.Trim();
            _logger.LogInformation(
                "Radio play name lookup: sending query '{Query}' to search service.",
                searchQuery);

            var results = await _search.SearchAsync(
                new RadioSearchCriteria { StationName = searchQuery },
                cancellationToken);

            _logger.LogInformation(
                "Radio play name lookup: received {ResultCount} result(s) for query '{Query}'.",
                results.Count,
                searchQuery);

            if (results.Count > 0)
            {
                _logger.LogInformation(
                    "Radio play name lookup results: {Results}",
                    JsonSerializer.Serialize(results));
            }

            station = results.FirstOrDefault();
        }

        if (station is null)
        {
            return ToolResult.Failed(
                Name,
                "The requested station was not found.",
                "{sound:radio-static} I'm sorry, I can't find that station.",
                complete: true);
        }
        try
        {
            await Audio.PlayStationAsync(station, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failed(
                Name,
                $"Unable to tune {station.Name}: {ex.Message}",
                "{sound:radio-static} I'm sorry, I wasn't able to tune that station.",
                complete: true);
        }

        return ToolResult.Successful(
            Name,
            $"Playing {station.Name}.",
            new { Station = station },
            $"Now playing {RadioSpeechFormatter.FormatStationNameForSpeech(station.Name)}.",
            true);
    }
}

public sealed class RadioStopTool : RadioToolBase
{
    public RadioStopTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioStop";
    public override string Intent => "Stop radio playback.";
    public override string GetLlmInstructions() => """
RADIO STOP

Stop the current radio playback.

Parameters:
- None.

Use this tool when the user explicitly asks to stop, turn off, or quit radio playback.

Examples:
User: "Stop the radio"
{tool:radioStop}

User: "Turn off the radio"
{tool:radioStop}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken); await Mpv.StopAsync(cancellationToken);
        return ToolResult.Successful(
            Name,
            "Radio playback stopped.",
            new { Action = "stop" },
            "The radio is stopped.",
            true);
    }
}

public sealed class RadioNextTool : RadioToolBase
{
    public RadioNextTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioNext";
    public override string Intent => "Play the next radio station.";
    public override string GetLlmInstructions() => """
RADIO NEXT

Play the next station in the current radio playlist.

Parameters:
- None.

Use this tool when the user asks to play the next station, move to the next station, or skip to the next radio station.

Examples:
User: "Play the next station"
{tool:radioNext}

User: "Go to the next station"
{tool:radioNext}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken); var station = State.RadioPlaylist[(State.RadioPlaylistIndex + 1) % State.RadioPlaylist.Count];
        await Audio.PlayStationAsync(station, cancellationToken);
        return ToolResult.Successful(
            Name,
            $"Playing {State.RadioStation?.Name ?? "next station"}.",
            new { Station = State.RadioStation },
            $"Now playing {State.RadioStation?.Name ?? "the next station"}.",
            true);
    }
}

public sealed class RadioPreviousTool : RadioToolBase
{
    public RadioPreviousTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioPrevious";
    public override string Intent => "Play the previous radio station.";
    public override string GetLlmInstructions() => """
RADIO PREVIOUS

Play the previous station in the current radio playlist.

Parameters:
- None.

Use this tool when the user asks to go back, return to, or play the previous radio station.

Examples:
User: "Go back to the previous station"
{tool:radioPrevious}

User: "Play the previous station"
{tool:radioPrevious}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken); var station = State.RadioPlaylist[(State.RadioPlaylistIndex - 1 + State.RadioPlaylist.Count) % State.RadioPlaylist.Count];
        await Audio.PlayStationAsync(station, cancellationToken);
        return ToolResult.Successful(
            Name,
            $"Playing {State.RadioStation?.Name ?? "previous station"}.",
            new { Station = State.RadioStation },
            $"Now playing {State.RadioStation?.Name ?? "the previous station"}.",
            true);
    }
}

public sealed class RadioVolumeTool : RadioToolBase
{
    public RadioVolumeTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioVolume";
    public override string Intent => "Set the radio volume.";
    public override string GetLlmRequestTemplate() => "{tool:radioVolume,volume=!required!}";
    public override bool HasParameters => true;
    public override string GetLlmInstructions() => """
RADIO VOLUME

Set the radio playback volume.

Parameters:
- volume: Required integer from 0 through 100.

Important:
- A numeric volume value is required.
- Values below 0 or above 100 are clamped by the application.
- Interpret natural phrases such as "turn it down to 10", "set the volume to 30", or "make it louder" as a volume request.
- If the user requests a relative change such as "a little louder" without a numeric target, do not invent a value; ask for a specific volume.

Examples:
User: "Set the volume to 30"
{tool:radioVolume,volume=30}

User: "Turn it down to 10"
{tool:radioVolume,volume=10}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var value = request.GetInt32("volume");
        if (!value.HasValue)
        {
            var pending = new ToolRequest
            {
                Name = Name,
                Arguments = new Newtonsoft.Json.Linq.JObject
                {
                    ["volume"] = ToolRequest.RequiredValue
                }
            };

            return ToolResult.MissingParameter(
                Name,
                "What volume should I set the radio to, from 0 to 100?",
                pending);
        }
        value = Math.Clamp(value.Value, 0, 100);
        await Mpv.SetVolumeAsync(value.Value, cancellationToken);
        return ToolResult.Successful(
            Name,
            $"Radio volume set to {value}.",
            new { Volume = value },
            $"Radio volume set to {value}.",
            true);
    }
}

public sealed class RadioSongTool : RadioToolBase
{
    public RadioSongTool(IMpvManager mpv, IMpvState state, IAudioManager audio)
        : base(mpv, state, audio) { }

    public override string Name => "radio-song";
    public override string Intent => "Get the song currently playing on the radio.";
    public override string GetLlmInstructions() => """
RADIO SONG

Get the song currently playing on the radio.

Parameters:
- None.

Use this tool when the user asks what song is playing, what track is playing, or who the current song is by.

Examples:
User: "What song is playing?"
{tool:radio-song}

User: "What's playing right now?"
{tool:radio-song}
""";

    public override Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        if (!State.IsPlaying)
        {
            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    "No radio station is currently playing.",
                    complete: true,
                    exactPrompt: "The radio isn't playing right now."));
        }

        var title = State.Title?.Trim();
        var artist = State.Artist?.Trim();

        var speech =
            !string.IsNullOrWhiteSpace(title) &&
            !string.IsNullOrWhiteSpace(artist)
                ? $"You're listening to {title} by {artist}."
                : !string.IsNullOrWhiteSpace(title)
                    ? $"The current song is {title}."
                    : !string.IsNullOrWhiteSpace(artist)
                        ? $"The current artist is {artist}."
                        : "I don't have the current song information.";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                speech,
                new { Title = title, Artist = artist },
                speech,
                true));
    }
}

public sealed class RadioCurrentTool : RadioToolBase
{
    public RadioCurrentTool(IMpvManager mpv, IMpvState state, IAudioManager audio)
        : base(mpv, state, audio) { }

    public override string Name => "radio-current";
    public override string Intent => "Get the name of the currently playing radio station.";
    public override string GetLlmInstructions() => """
RADIO CURRENT

Get the name of the currently playing radio station.

Parameters:
- None.

Use this tool only for questions about which radio station is currently playing.

Examples:
User: "What station is this?"
{tool:radio-current}

User: "What radio station am I listening to?"
{tool:radio-current}
""";

    public override Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        var station = State.RadioStation;
        if (station is null || !State.IsPlaying)
        {
            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    "No radio station is currently playing.",
                    new { IsPlaying = false },
                    "The radio isn't playing right now.",
                    true));
        }

        var spokenName =
            RadioSpeechFormatter.FormatStationNameForSpeech(station.Name);

        var speech = $"You're listening to {spokenName}.";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                speech,
                new { Station = station },
                speech,
                true));
    }
}

public sealed class RadioSaveTool : RadioToolBase
{
    private readonly IRadioStationStore _stationStore;

    public RadioSaveTool(
        IMpvManager mpv,
        IMpvState state,
        IAudioManager audio,
        IRadioStationStore stationStore)
        : base(mpv, state, audio)
    {
        _stationStore = stationStore;
    }

    public override string Name => "radio-save";
    public override string Intent => "Save the currently playing radio station.";
    public override string GetLlmInstructions() => """
RADIO SAVE

Save the currently playing radio station.

Parameters:
- None.

Use this tool when the user asks to save, remember, or keep the current station.

Examples:
User: "Save this station"
{tool:radio-save}

User: "Remember this station"
{tool:radio-save}
""";

    public override Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        var station = State.RadioStation;
        if (station is null)
        {
            const string speech = "There isn't a radio station playing right now.";
            return Task.FromResult(
                ToolResult.Successful(Name, speech, new { Saved = false }, speech, true));
        }

        var alreadySaved = _stationStore.IsSaved(station.Id);
        if (!alreadySaved)
            _stationStore.Save(station);

        var spokenName =
            RadioSpeechFormatter.FormatStationNameForSpeech(station.Name);

        var speech = alreadySaved
            ? $"{spokenName} is already saved."
            : $"I've saved {spokenName}.";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                speech,
                new { Station = station, Saved = true, AlreadySaved = alreadySaved },
                speech,
                true));
    }
}

public sealed class RadioForgetTool : RadioToolBase
{
    private readonly IRadioStationStore _stationStore;

    public RadioForgetTool(
        IMpvManager mpv,
        IMpvState state,
        IAudioManager audio,
        IRadioStationStore stationStore)
        : base(mpv, state, audio)
    {
        _stationStore = stationStore;
    }

    public override string Name => "radio-forget";
    public override string Intent => "Remove the currently playing radio station from the saved station list.";
    public override string GetLlmInstructions() => """
RADIO FORGET

Remove the currently playing radio station from the saved station list.

Parameters:
- None.

Use this tool when the user asks to forget, remove, or unsave the current station.

Examples:
User: "Forget this station"
{tool:radio-forget}

User: "Remove this station from my saved stations"
{tool:radio-forget}
""";

    public override Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        var station = State.RadioStation;
        if (station is null)
        {
            const string speech = "There isn't a radio station playing right now.";
            return Task.FromResult(
                ToolResult.Successful(Name, speech, new { Removed = false }, speech, true));
        }

        var removed = _stationStore.Forget(station.Id);
        var spokenName =
            RadioSpeechFormatter.FormatStationNameForSpeech(station.Name);

        var speech = removed
            ? $"I've removed {spokenName} from your saved stations."
            : $"{spokenName} isn't in your saved stations.";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                speech,
                new { Station = station, Removed = removed },
                speech,
                true));
    }
}

public sealed class RadioFavoriteTool : RadioToolBase
{
    private readonly IRadioStationStore _stationStore;

    public RadioFavoriteTool(
        IMpvManager mpv,
        IMpvState state,
        IAudioManager audio,
        IRadioStationStore stationStore)
        : base(mpv, state, audio)
    {
        _stationStore = stationStore;
    }

    public override string Name => "radio-favorite";
    public override string Intent => "Mark the currently playing radio station as a favorite station.";
    public override string GetLlmInstructions() => """
RADIO FAVORITE

Mark the currently playing radio station as a favorite station.

Parameters:
- None.

Use this tool when the user asks to favorite, mark as a favorite, or add the current station to favorites.

Examples:
User: "Favorite this station"
{tool:radio-favorite}

User: "Make this a favorite"
{tool:radio-favorite}
""";

    public override Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        var station = State.RadioStation;
        if (station is null)
        {
            const string speech = "There isn't a radio station playing right now.";
            return Task.FromResult(
                ToolResult.Successful(Name, speech, new { Favorite = false }, speech, true));
        }

        var alreadyFavorite = _stationStore.IsFavorite(station.Id);
        _stationStore.SetFavorite(station);

        var spokenName =
            RadioSpeechFormatter.FormatStationNameForSpeech(station.Name);

        var speech = alreadyFavorite
            ? $"{spokenName} is already a favorite."
            : $"I've marked {spokenName} as a favorite.";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                speech,
                new { Station = station, Favorite = true, AlreadyFavorite = alreadyFavorite },
                speech,
                true));
    }
}

public sealed class RadioStatusTool : RadioToolBase
{
    public RadioStatusTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioStatus";
    public override string Intent => "Report the current radio playback state.";
    public override string GetLlmInstructions() => """
RADIO STATUS

Get the current radio playback state.

Parameters:
- None.

The result can include whether playback is active, paused, idle, or muted, plus the current volume.

Use this tool for questions about whether the radio is playing, paused, stopped, muted, or what the current radio volume is.

Examples:
User: "What's the radio status?"
{tool:radioStatus}

User: "Is the radio playing?"
{tool:radioStatus}

User: "Is it muted?"
{tool:radioStatus}
""";
    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        return Task.FromResult(ToolResult.Successful(Name, "Radio status retrieved.", new { IsPlaying = State.IsPlaying, IsPaused = State.IsPaused, IsIdle = State.IsIdle, IsMuted = State.IsMuted, Volume = State.Volume }));
    }
}

public sealed class RadioPlaylistTool : RadioToolBase
{
    public RadioPlaylistTool(IMpvManager mpv, IMpvState state, IAudioManager audio) : base(mpv, state, audio) { }
    public override string Name => "radioPlaylist";
    public override string Intent => "List stations in the current radio playlist.";
    public override string GetLlmInstructions() => """
RADIO PLAYLIST

Get the stations currently available in the radio playlist.

Parameters:
- None.

Important:
- Use this tool when the user asks which stations are available, what stations are in the playlist, or requests a list of playable stations.
- The returned station IDs and names are authoritative for subsequent radioPlay requests.
- Do not invent stations that are not present in the returned playlist.

Examples:
User: "What stations are available?"
{tool:radioPlaylist}

User: "Show me the radio stations"
{tool:radioPlaylist}

User: "Which stations can I play?"
{tool:radioPlaylist}
""";
    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var playlist = State.RadioPlaylist;
        return Task.FromResult(ToolResult.Successful(Name, $"The current radio playlist contains {playlist.Count} station(s).", new { Count = playlist.Count, CurrentIndex = State.RadioPlaylistIndex, Stations = playlist }));
    }
}

public sealed class RadioSearchTool : ITool
{
    private readonly IRadioSearchClient _search;
    private readonly IMpvManager _mpv;
    private readonly IAudioManager _audio;
    private ILogger<RadioSearchTool> _logger;

    public RadioSearchTool(IRadioSearchClient search, IMpvManager mpv, IAudioManager audio, ILogger<RadioSearchTool> logger)
    {
        _search = search;
        _mpv = mpv;
        _audio = audio;
        _logger = logger;
    }
    public string Name => "radioSearch";
    public string Intent => "Find and play a radio station by station name, genre or style, artist, topic or format, language, or location.";
    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:radioSearch,query=!required!}";
    public string GetLlmInstructions() => """
RADIO SEARCH

Search for radio stations.

Parameters:
- query: Required search text describing the station the user wants.

Expected search intents:
- Station name: "Find Jazz FM"
- Genre or style: "Play some light jazz"
- Artist: "Find a station that plays Taylor Swift"
- Topic or format: "Find sports talk radio"
- Language: "Find a Spanish station"
- Location: "Find a Nashville station"

Important:
- The query is one natural-language search criterion. Do not split it into fields or invent a category.
- Preserve the user's meaningful search terms. Do not add words, station names, artists, locations, or other criteria the user did not provide.
- Use radioSearch when the user wants a station matching one or more of the expected search intents above.
- Do not use radioSearch for "next", "previous", "stop", volume, current station, or playlist requests; those have dedicated radio tools.
- If the user names a known station and simply wants to play it, use radioPlay instead.
- A descriptive request such as "light jazz" is a search criterion, not a station name.
- Search results are authoritative for stations returned by the search service.
- A search result does not automatically mean the station is in the current playback playlist.
- Never invent search results, station IDs, station names, or stream URLs.

Examples:
User: "Find Jazz FM"
{tool:radioSearch,query="Jazz FM"}

User: "Play some light jazz"
{tool:radioSearch,query="light jazz"}

User: "Find a station that plays Taylor Swift"
{tool:radioSearch,query="Taylor Swift"}

User: "Find Spanish news radio"
{tool:radioSearch,query="Spanish news"}

User: "Find a Nashville country station"
{tool:radioSearch,query="Nashville country"}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = "";
        if (request.State == ToolRequestState.Initial)
        {
            await _audio.PrepareStationChangeAsync(cancellationToken);
            await _audio.WaitForCompletionAsync(cancellationToken);
            query = request.GetString("query");
            var searchDescription = string.IsNullOrWhiteSpace(query)
                ? "radio stations"
                : $"radio stations with {query.Trim()}";

            await _audio.QueueSpeechAsync(
                $"Looking for {searchDescription} now {{sound:radio-tuning}}",
                cancellationToken);

            return ToolResult.Preamble(
                Name,
                string.Empty,
                request.WithState(ToolRequestState.PreambleComplete));
        }

        query = request.GetString("query");
        if (string.IsNullOrWhiteSpace(query))
        {
            var pending = new ToolRequest
            {
                Name = Name,
                Arguments = new Newtonsoft.Json.Linq.JObject
                {
                    ["query"] = ToolRequest.RequiredValue
                }
            };

            return ToolResult.MissingParameter(
                Name,
                "What radio station, genre, artist, or topic should I search for?",
                pending);
        }
        var searchQuery = query.Trim();
        _logger.LogInformation(
            $"Radio search by name/criteria: sending query '{searchQuery}' to search service.");

        var results = await _search.SearchAsync(
            new RadioSearchCriteria { Query = searchQuery },
            cancellationToken);

        _logger.LogInformation(
            "Radio search by name/criteria: received {ResultCount} result(s) for query '{Query}'.",
            results.Count,
            searchQuery);

        if (results.Count > 0)
        {
            _logger.LogInformation(
                "Radio search by name/criteria results: {Results}",
                JsonSerializer.Serialize(results));
        }

        if (results.Count == 0)
        {
            return ToolResult.Failed(
                Name,
                "No matching radio stations were found.",
                "{sound:radio-static} I'm sorry, I can't find that station.",
                complete: true);
        }

        var station = results[0];
        _mpv.SetRadioPlaylist(results, RadioPlaylistSource.Search);

        try
        {
            await _audio.PlayStationAsync(station, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(
                ex,
                "Timed out waiting for MPV to accept station {StationName}.",
                station.Name);

            return ToolResult.Failed(
                Name,
                $"Unable to tune {station.Name}: MPV did not respond within the configured timeout.",
                "{sound:radio-static} I'm sorry, I wasn't able to tune that station.",
                complete: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to tune station {StationName}.",
                station.Name);

            return ToolResult.Failed(
                Name,
                $"Unable to tune {station.Name}: {ex.Message}",
                "{sound:radio-static} I'm sorry, I wasn't able to tune that station.",
                complete: true);
        }

        var spokenStation = RadioSpeechFormatter.FormatStationNameForSpeech(station.Name);
        return ToolResult.Successful(
            Name,
            $"Playing {station.Name}.",
            new { Station = station, Results = results },
            $"Now playing {spokenStation}.",
            true);
    }
}