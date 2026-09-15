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
play requires stationId or stationName and must use an exact station from the current playlist.
volume requires an integer 0-100. current/status return playback state; playlist returns available stations. next/previous/stop need no parameters.
If asked which stations are available, use playlist first. Never invent station IDs or names.
Examples:
"Play Jazz FM" -> {tool:radio,action=play,stationName="Jazz FM"}
"Play station 12345" -> {tool:radio,action=play,stationId=12345}
"Play the next station" -> {tool:radio,action=next}
"Set volume to 30" -> {tool:radio,action=volume,volume=30}
"What's playing?" -> {tool:radio,action=current}
"What stations are available?" -> {tool:radio,action=playlist}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                // The response parser also provides command for compatibility
                // with older callers, but action is the model-facing syntax.
                var action = request.GetString("action") ?? request.GetString("command");
                if (string.IsNullOrWhiteSpace(action))
                    return ToolResult.Failed(Name, "Radio tool request did not contain an action.");

                return action.ToLowerInvariant() switch
                {
                    "play" => await PlayAsync(request, cancellationToken),
                    "next" => await NextAsync(cancellationToken),
                    "previous" => await PreviousAsync(cancellationToken),
                    "stop" => await StopAsync(cancellationToken),
                    "volume" => await SetVolumeAsync(request, cancellationToken),
                    "current" or "status" => GetCurrentStation(),
                    "playlist" => GetPlaylist(),
                    _ => ToolResult.Failed(Name, $"Unknown radio action '{action}'.")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Radio tool execution failed.");
                return ToolResult.Failed(Name, ex.Message);
            }
        }

        private async Task<ToolResult> PlayAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var station = GetStationFromRequest(request);
            if (station is null) return ToolResult.Failed(Name, "No radio station was specified.");
            await _mpvManager.PlayAsync(station, cancellationToken);
            return ToolResult.Successful(Name, $"Playing {station.Name}.", new { Action = "play", Station = station });
        }

        private async Task<ToolResult> NextAsync(CancellationToken cancellationToken)
        {
            await _mpvManager.PlayNextRadioStationAsync(cancellationToken);
            return ToolResult.Successful(Name, $"Playing {_mpvState.RadioStation?.Name ?? "next station"}.", CreatePlaybackData());
        }

        private async Task<ToolResult> PreviousAsync(CancellationToken cancellationToken)
        {
            await _mpvManager.PlayPreviousRadioStationAsync(cancellationToken);
            return ToolResult.Successful(Name, $"Playing {_mpvState.RadioStation?.Name ?? "previous station"}.", CreatePlaybackData());
        }

        private async Task<ToolResult> StopAsync(CancellationToken cancellationToken)
        {
            await _mpvManager.StopAsync(cancellationToken);
            return ToolResult.Successful(Name, "Radio playback stopped.", new { Action = "stop" });
        }

        private async Task<ToolResult> SetVolumeAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var volume = GetIntArgument(request, "volume");
            if (volume is null) return ToolResult.Failed(Name, "A volume value from 0 to 100 is required.");
            volume = Math.Clamp(volume.Value, 0, 100);
            await _mpvManager.SetVolumeAsync(volume.Value, cancellationToken);
            return ToolResult.Successful(Name, $"Radio volume set to {volume}.", new { Action = "volume", Volume = volume });
        }

        private ToolResult GetCurrentStation()
        {
            var station = _mpvState.RadioStation;
            if (station is null)
                return ToolResult.Successful(Name, "No radio station is currently playing.", new { IsPlaying = false });

            return ToolResult.Successful(Name, $"Currently playing {station.Name}.", new
            {
                IsPlaying = _mpvState.IsPlaying,
                Station = station,
                Title = _mpvState.Title,
                Artist = _mpvState.Artist,
                Album = _mpvState.Album
            });
        }

        private ToolResult GetPlaylist()
        {
            var playlist = _mpvState.RadioPlaylist;
            return ToolResult.Successful(Name, $"The current radio playlist contains {playlist.Count} station(s).", new
            {
                Count = playlist.Count,
                CurrentIndex = _mpvState.RadioPlaylistIndex,
                Source = _mpvState.RadioPlaylistSource?.ToString(),
                Stations = playlist
            });
        }

        private RadioStation? GetStationFromRequest(ToolRequest request)
        {
            var stationId = request.GetString("stationId");
            if (!string.IsNullOrWhiteSpace(stationId))
                return _mpvState.RadioPlaylist.FirstOrDefault(station => string.Equals(station.Id, stationId, StringComparison.OrdinalIgnoreCase));

            var stationName = request.GetString("stationName");
            if (!string.IsNullOrWhiteSpace(stationName))
                return _mpvState.RadioPlaylist.FirstOrDefault(station => string.Equals(station.Name, stationName, StringComparison.OrdinalIgnoreCase));

            return null;
        }

        private object CreatePlaybackData() => new
        {
            IsPlaying = _mpvState.IsPlaying,
            Station = _mpvState.RadioStation,
            PlaylistIndex = _mpvState.RadioPlaylistIndex,
            PlaylistCount = _mpvState.RadioPlaylist.Count,
            PlaylistSource = _mpvState.RadioPlaylistSource?.ToString(),
            Title = _mpvState.Title,
            Artist = _mpvState.Artist,
            Album = _mpvState.Album
        };

        private static int? GetIntArgument(ToolRequest request, string name)
        {
            var value = request.GetString(name);
            return int.TryParse(value, out var result) ? result : null;
        }
    }
}
