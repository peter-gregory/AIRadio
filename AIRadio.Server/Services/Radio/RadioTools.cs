using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Mpv;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Radio;

public abstract class RadioToolBase : ITool
{
    protected readonly IMpvManager Mpv;
    protected readonly IMpvState State;
    protected RadioToolBase(IMpvManager mpv, IMpvState state) { Mpv = mpv; State = state; }
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

public sealed class RadioPlayTool : RadioToolBase
{
    public RadioPlayTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
    public override string Name => "radioPlay";
    public override string Intent => "Play a known radio station by its exact station name or ID. A genre, style, mood, language, country, or other station characteristic is a radioSearch request.";
    public override bool HasParameters => true;
    public override string GetLlmRequestTemplate() => "{tool:radioPlay,stationId=<optional>,stationName=<optional>}";
    public override string GetLlmInstructions() => """
RADIO PLAY

Play a station from the current radio playlist.

Parameters:
- stationId: Optional station ID. Use the exact ID from the current playlist.
- stationName: Optional station name. Use the exact station name from the current playlist.
- Provide stationId or stationName when the user supplies a station identifier or station name.
- At least one of stationId or stationName is required; the execution state engine will ask for one if neither is supplied.
- When both are supplied, stationId takes precedence.

Important:
- A station name or ID must identify an actual station explicitly supplied by the user or returned by a previous search/playlist result.
- Do not turn a genre, style, mood, language, country, artist, or other descriptive term into stationName. For example, "light jazz" is a search criterion, not a station name.
- The play action can only play a station that is already in the current playlist.
- Do not invent station IDs or station names.
- Use the exact station ID or station name returned by a previous radio playlist/search result.
- If the requested station is not in the current playlist, do not substitute another station.
- If the user asks for a station that is not currently available, report that it was not found.

Examples:
User: "Play Jazz FM"
{tool:radioPlay,stationName="Jazz FM"}

User: "Play station 12345"
{tool:radioPlay,stationId=12345}
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var stationId = request.GetString("stationId");
        var stationName = request.GetString("stationName");
        if (request.State == ToolRequestState.Initial)
        {
            return ToolResult.Preamble(
                Name,
                "Finding that station for you now {sound:radio-tuning}",
                request.WithState(ToolRequestState.PreambleComplete));
        }

        if (string.IsNullOrWhiteSpace(stationId) && string.IsNullOrWhiteSpace(stationName))
        {
            var pending = new ToolRequest
            {
                Name = Name,
                Arguments = new JObject
                {
                    ["stationName"] = ToolRequest.RequiredValue
                }
            };

            return ToolResult.MissingParameter(
                Name,
                "Which radio station would you like me to play?",
                pending);
        }

        var station = FindStation(request);
        if (station is null)
        {
            return ToolResult.Failed(
                Name,
                "The requested station was not found in the current playlist.",
                "{sound:radio-static} I'm sorry, I can't find that station.",
                complete: true);
        }
        await Mpv.PlayAsync(station, cancellationToken);
        return ToolResult.Successful(
            Name,
            $"Playing {station.Name}.",
            new { Station = station },
            $"Now playing {station.Name}.",
            true);
    }
}

public sealed class RadioStopTool : RadioToolBase
{
    public RadioStopTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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
        return ToolResult.Successful(Name, "Radio playback stopped.", new { Action = "stop" });
    }
}

public sealed class RadioNextTool : RadioToolBase
{
    public RadioNextTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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
        Validate(request, cancellationToken); await Mpv.PlayNextRadioStationAsync(cancellationToken);
        return ToolResult.Successful(Name, $"Playing {State.RadioStation?.Name ?? "next station"}.", new { Station = State.RadioStation });
    }
}

public sealed class RadioPreviousTool : RadioToolBase
{
    public RadioPreviousTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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
        Validate(request, cancellationToken); await Mpv.PlayPreviousRadioStationAsync(cancellationToken);
        return ToolResult.Successful(Name, $"Playing {State.RadioStation?.Name ?? "previous station"}.", new { Station = State.RadioStation });
    }
}

public sealed class RadioVolumeTool : RadioToolBase
{
    public RadioVolumeTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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
        return ToolResult.Successful(Name, $"Radio volume set to {value}.", new { Volume = value });
    }
}

public sealed class RadioCurrentTool : RadioToolBase
{
    public RadioCurrentTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
    public override string Name => "radioCurrent";
    public override string Intent => "Report the currently playing station and metadata.";
    public override string GetLlmInstructions() => """
RADIO CURRENT

Get the currently playing radio station and its available playback metadata.

Parameters:
- None.

Use this tool for questions such as what is playing, which station is playing, or requests for the current song/artist information.

Examples:
User: "What's playing?"
{tool:radioCurrent}

User: "What station is this?"
{tool:radioCurrent}

User: "Who is playing?"
{tool:radioCurrent}
""";
    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var station = State.RadioStation;
        return Task.FromResult(station is null
            ? ToolResult.Successful(Name, "No radio station is currently playing.", new { IsPlaying = false })
            : ToolResult.Successful(Name, $"Currently playing {station.Name}.", new { IsPlaying = State.IsPlaying, Station = station, Title = State.Title, Artist = State.Artist, Album = State.Album }));
    }
}

public sealed class RadioStatusTool : RadioToolBase
{
    public RadioStatusTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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
    public RadioPlaylistTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
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

    public RadioSearchTool(IRadioSearchClient search, IMpvManager mpv)
    {
        _search = search;
        _mpv = mpv;
    }
    public string Name => "radioSearch";
    public string Intent => "Find a radio station matching a genre, style, artist, topic, language, country, or station name, then play the selected station.";
    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:radioSearch,query=!required!}";
    public string GetLlmInstructions() => """
RADIO SEARCH

Search for radio stations.

Parameters:
- query: Required search text such as a station name, genre, artist, or topic.

Important:
- Use the user's search terms as the query; do not invent or embellish a station name.
- Requests such as "play some light jazz", "find a rock station", or "something classical" are radioSearch requests.
- The search request is a criterion, not a station name.
- Search results are authoritative for stations returned by the search service.
- A search result does not automatically mean the station is in the current playback playlist.
- If the user wants to play a search result, use the exact station ID or station name returned by the search before calling radioPlay.
- Never invent search results, station IDs, station names, or stream URLs.

Examples:
User: "Find Jazz FM"
{tool:radioSearch,query="Jazz FM"}

User: "Find some jazz stations"
{tool:radioSearch,query="jazz"}

User: "Search for stations that play Taylor Swift"
{tool:radioSearch,query="Taylor Swift"}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.State == ToolRequestState.Initial)
        {
            return ToolResult.Preamble(
                Name,
                "Finding that station for you now {sound:radio-tuning}",
                request.WithState(ToolRequestState.PreambleComplete));
        }

        var query = request.GetString("query");
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
        var results = await _search.SearchAsync(
            new RadioSearchCriteria { Query = query.Trim() },
            cancellationToken);

        if (results.Count == 0)
        {
            return ToolResult.Failed(
                Name,
                "No matching radio stations were found.",
                "{sound:radio-static} I'm sorry, I can't find that station.");
        }

        var station = results[0];
        _mpv.SetRadioPlaylist(results, RadioPlaylistSource.Search);
        await _mpv.PlayAsync(station, cancellationToken);

        return ToolResult.Successful(
            Name,
            $"Playing {station.Name}.",
            new { Station = station, Results = results },
            $"Now playing {station.Name}.");
    }
}
