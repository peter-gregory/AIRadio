using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Mpv;

namespace AIRadio.Server.Services.Radio;

public abstract class RadioToolBase : ITool
{
    protected readonly IMpvManager Mpv;
    protected readonly IMpvState State;
    protected RadioToolBase(IMpvManager mpv, IMpvState state) { Mpv = mpv; State = state; }
    public abstract string Name { get; }
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
    public override string GetLlmInstructions() => """
RADIO PLAY
Play a station from the current playlist.
Parameters:
- stationName: required exact station name from the current playlist.
- stationId: required exact station ID when supplied instead of stationName.
Use the station value from a previous radio search/playlist result. Never invent it.
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var station = FindStation(request);
        if (station is null) return ToolResult.Failed(Name, "The requested station was not found in the current playlist.");
        await Mpv.PlayAsync(station, cancellationToken);
        return ToolResult.Successful(Name, $"Playing {station.Name}.", new { Station = station });
    }
}

public sealed class RadioStopTool : RadioToolBase
{
    public RadioStopTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
    public override string Name => "radioStop";
    public override string GetLlmInstructions() => "RADIO STOP\nStop radio playback. Parameters: none.";
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
    public override string GetLlmInstructions() => "RADIO NEXT\nPlay the next station in the current playlist. Parameters: none.";
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
    public override string GetLlmInstructions() => "RADIO PREVIOUS\nPlay the previous station in the current playlist. Parameters: none.";
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
    public override string GetLlmInstructions() => """
RADIO VOLUME
Set radio volume.
Parameters:
- volume: required integer from 0 to 100.
""";
    public override async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var value = request.GetInt32("volume");
        if (!value.HasValue) return ToolResult.Failed(Name, "A volume from 0 to 100 is required.");
        value = Math.Clamp(value.Value, 0, 100);
        await Mpv.SetVolumeAsync(value.Value, cancellationToken);
        return ToolResult.Successful(Name, $"Radio volume set to {value}.", new { Volume = value });
    }
}

public sealed class RadioCurrentTool : RadioToolBase
{
    public RadioCurrentTool(IMpvManager mpv, IMpvState state) : base(mpv, state) { }
    public override string Name => "radioCurrent";
    public override string GetLlmInstructions() => "RADIO CURRENT\nReport the currently playing station and metadata. Parameters: none.";
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
    public override string GetLlmInstructions() => "RADIO STATUS\nReport the current radio playback state. Parameters: none.";
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
    public override string GetLlmInstructions() => "RADIO PLAYLIST\nList stations in the current radio playlist. Parameters: none.";
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
    public RadioSearchTool(IRadioSearchClient search) => _search = search;
    public string Name => "radioSearch";
    public string GetLlmInstructions() => """
RADIO SEARCH
Search for radio stations.
Parameters:
- query: required search text such as station name, genre, artist, or topic.
Never invent search results.
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.GetString("query");
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Failed(Name, "A search query is required.");
        var results = await _search.SearchAsync(new RadioSearchCriteria { Query = query.Trim() }, cancellationToken);
        return ToolResult.Successful(Name, $"Found {results.Count} radio station(s).", results);
    }
}
