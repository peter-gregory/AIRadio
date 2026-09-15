using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Mpv;

namespace AIRadio.Server.Services.Radio
{
    public sealed class RadioTool : ITool
    {
        private readonly ILogger<RadioTool> _logger;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvState _mpvState;

        public RadioTool(ILogger<RadioTool> logger, IMpvManager mpvManager, IMpvState mpvState)
        {
            _logger = logger;
            _mpvManager = mpvManager;
            _mpvState = mpvState;
        }

        public string Name => "radio";

        public string GetLlmInstructions() => """
RADIO
Control playback or read radio state.
Actions: play, next, previous, stop, volume, current, status, playlist.
play: stationId or stationName required; use an exact station from the current playlist.
volume: integer 0-100.
current/status: current station and playback metadata. playlist: available stations. next/previous/stop: no parameters.
If asked which stations are available, use playlist first. Never invent station IDs/names.
Examples:
{tool:radio,action=play,stationName="Jazz FM"}
{tool:radio,action=play,stationId=12345}
{tool:radio,action=next}
{tool:radio,action=volume,volume=30}
{tool:radio,action=current}
{tool:radio,action=playlist}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                var action = request.GetString("action") ?? request.GetString("command");
                if (string.IsNullOrWhiteSpace(action)) return ToolResult.Failed(Name, "Radio tool request did not contain an action.");
                return action.ToLowerInvariant() switch
                {
                    "play" => await PlayAsync(request, cancellationToken), "next" => await NextAsync(cancellationToken),
                    "previous" => await PreviousAsync(cancellationToken), "stop" => await StopAsync(cancellationToken),
                    "volume" => await SetVolumeAsync(request, cancellationToken), "current" or "status" => GetCurrentStation(),
                    "playlist" => GetPlaylist(), _ => ToolResult.Failed(Name, $"Unknown radio action '{action}'.")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Radio tool execution failed."); return ToolResult.Failed(Name, ex.Message); }
        }
        private async Task<ToolResult> PlayAsync(ToolRequest request, CancellationToken ct) { var station = GetStationFromRequest(request); if (station is null) return ToolResult.Failed(Name, "No radio station was specified."); await _mpvManager.PlayAsync(station, ct); return ToolResult.Successful(Name, $"Playing {station.Name}.", new { Action = "play", Station = station }); }
        private async Task<ToolResult> NextAsync(CancellationToken ct) { await _mpvManager.PlayNextRadioStationAsync(ct); return ToolResult.Successful(Name, $"Playing {_mpvState.RadioStation?.Name ?? "next station"}.", CreatePlaybackData()); }
        private async Task<ToolResult> PreviousAsync(CancellationToken ct) { await _mpvManager.PlayPreviousRadioStationAsync(ct); return ToolResult.Successful(Name, $"Playing {_mpvState.RadioStation?.Name ?? "previous station"}.", CreatePlaybackData()); }
        private async Task<ToolResult> StopAsync(CancellationToken ct) { await _mpvManager.StopAsync(ct); return ToolResult.Successful(Name, "Radio playback stopped.", new { Action = "stop" }); }
        private async Task<ToolResult> SetVolumeAsync(ToolRequest request, CancellationToken ct) { var volume = GetIntArgument(request, "volume"); if (volume is null) return ToolResult.Failed(Name, "A volume value from 0 to 100 is required."); volume = Math.Clamp(volume.Value, 0, 100); await _mpvManager.SetVolumeAsync(volume.Value, ct); return ToolResult.Successful(Name, $"Radio volume set to {volume}.", new { Action = "volume", Volume = volume }); }
        private ToolResult GetCurrentStation() { var station = _mpvState.RadioStation; if (station is null) return ToolResult.Successful(Name, "No radio station is currently playing.", new { IsPlaying = false }); return ToolResult.Successful(Name, $"Currently playing {station.Name}.", new { IsPlaying = _mpvState.IsPlaying, Station = station, Title = _mpvState.Title, Artist = _mpvState.Artist, Album = _mpvState.Album }); }
        private ToolResult GetPlaylist() { var playlist = _mpvState.RadioPlaylist; return ToolResult.Successful(Name, $"The current radio playlist contains {playlist.Count} station(s).", new { Count = playlist.Count, CurrentIndex = _mpvState.RadioPlaylistIndex, Source = _mpvState.RadioPlaylistSource?.ToString(), Stations = playlist }); }
        private RadioStation? GetStationFromRequest(ToolRequest request) { var stationId = request.GetString("stationId"); if (!string.IsNullOrWhiteSpace(stationId)) return _mpvState.RadioPlaylist.FirstOrDefault(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase)); var stationName = request.GetString("stationName"); if (!string.IsNullOrWhiteSpace(stationName)) return _mpvState.RadioPlaylist.FirstOrDefault(s => string.Equals(s.Name, stationName, StringComparison.OrdinalIgnoreCase)); return null; }
        private object CreatePlaybackData() => new { IsPlaying = _mpvState.IsPlaying, Station = _mpvState.RadioStation, PlaylistIndex = _mpvState.RadioPlaylistIndex, PlaylistCount = _mpvState.RadioPlaylist.Count, PlaylistSource = _mpvState.RadioPlaylistSource?.ToString(), Title = _mpvState.Title, Artist = _mpvState.Artist, Album = _mpvState.Album };
        private static int? GetIntArgument(ToolRequest request, string name) { var value = request.GetString(name); return int.TryParse(value, out var result) ? result : null; }
    }
}
