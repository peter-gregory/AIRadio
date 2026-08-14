using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Mpv;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Radio
{
    public sealed class RadioTool : ITool
    {
        private readonly ILogger<RadioTool> _logger;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvState _mpvState;

        public RadioTool(
            ILogger<RadioTool> logger,
            IMpvManager mpvManager,
            IMpvState mpvState)
        {
            _logger = logger;
            _mpvManager = mpvManager;
            _mpvState = mpvState;
        }

        public string Name =>
            "radio";

        public async Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                var command =
                    GetStringArgument(
                        request,
                        "command");

                if (string.IsNullOrWhiteSpace(command))
                {
                    return ToolResult.Failed(
                        Name,
                        "Radio tool request did not contain a command.");
                }

                switch (command.ToLowerInvariant())
                {
                    case "play":
                        return await PlayAsync(
                            request,
                            cancellationToken);

                    case "next":
                        return await NextAsync(
                            cancellationToken);

                    case "previous":
                        return await PreviousAsync(
                            cancellationToken);

                    case "stop":
                        return await StopAsync(
                            cancellationToken);

                    case "volume":
                        return await SetVolumeAsync(
                            request,
                            cancellationToken);

                    case "current":
                    case "status":
                        return GetCurrentStation();

                    case "playlist":
                        return GetPlaylist();

                    default:
                        return ToolResult.Failed(
                            Name,
                            $"Unknown radio command '{command}'.");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Radio tool execution failed.");

                return ToolResult.Failed(
                    Name,
                    ex.Message);
            }
        }

        // ============================================================
        // PLAY
        // ============================================================

        private async Task<ToolResult> PlayAsync(
            ToolRequest request,
            CancellationToken cancellationToken)
        {
            var station =
                GetStationFromRequest(
                    request);

            if (station is null)
            {
                return ToolResult.Failed(
                    Name,
                    "No radio station was specified.");
            }

            await _mpvManager.PlayAsync(
                station,
                cancellationToken);

            return ToolResult.Successful(
                Name,
                $"Playing {station.Name}.",
                new
                {
                    Action = "play",
                    Station = station
                });
        }

        // ============================================================
        // NEXT
        // ============================================================

        private async Task<ToolResult> NextAsync(
            CancellationToken cancellationToken)
        {
            await _mpvManager.PlayNextRadioStationAsync(
                cancellationToken);

            return ToolResult.Successful(
                Name,
                $"Playing {_mpvState.RadioStation?.Name ?? "next station"}.",
                CreatePlaybackData());
        }

        // ============================================================
        // PREVIOUS
        // ============================================================

        private async Task<ToolResult> PreviousAsync(
            CancellationToken cancellationToken)
        {
            await _mpvManager.PlayPreviousRadioStationAsync(
                cancellationToken);

            return ToolResult.Successful(
                Name,
                $"Playing {_mpvState.RadioStation?.Name ?? "previous station"}.",
                CreatePlaybackData());
        }

        // ============================================================
        // STOP
        // ============================================================

        private async Task<ToolResult> StopAsync(
            CancellationToken cancellationToken)
        {
            await _mpvManager.StopAsync(
                cancellationToken);

            return ToolResult.Successful(
                Name,
                "Radio playback stopped.",
                new
                {
                    Action = "stop"
                });
        }

        // ============================================================
        // VOLUME
        // ============================================================

        private async Task<ToolResult> SetVolumeAsync(
            ToolRequest request,
            CancellationToken cancellationToken)
        {
            var volume =
                GetIntArgument(
                    request,
                    "volume");

            if (volume is null)
            {
                return ToolResult.Failed(
                    Name,
                    "A volume value from 0 to 100 is required.");
            }

            volume =
                Math.Clamp(
                    volume.Value,
                    0,
                    100);

            await _mpvManager.SetVolumeAsync(
                volume.Value,
                cancellationToken);

            return ToolResult.Successful(
                Name,
                $"Radio volume set to {volume}.",
                new
                {
                    Action = "volume",
                    Volume = volume
                });
        }

        // ============================================================
        // CURRENT STATION
        // ============================================================

        private ToolResult GetCurrentStation()
        {
            var station =
                _mpvState.RadioStation;

            if (station is null)
            {
                return ToolResult.Successful(
                    Name,
                    "No radio station is currently playing.",
                    new
                    {
                        IsPlaying = false
                    });
            }

            return ToolResult.Successful(
                Name,
                $"Currently playing {station.Name}.",
                new
                {
                    IsPlaying =
                        _mpvState.IsPlaying,

                    Station =
                        station,

                    Title =
                        _mpvState.Title,

                    Artist =
                        _mpvState.Artist,

                    Album =
                        _mpvState.Album
                });
        }

        // ============================================================
        // PLAYLIST
        // ============================================================

        private ToolResult GetPlaylist()
        {
            var playlist =
                _mpvState.RadioPlaylist;

            return ToolResult.Successful(
                Name,
                $"The current radio playlist contains {playlist.Count} station(s).",
                new
                {
                    Count =
                        playlist.Count,

                    CurrentIndex =
                        _mpvState.RadioPlaylistIndex,

                    Source =
                        _mpvState.RadioPlaylistSource?.ToString(),

                    Stations =
                        playlist
                });
        }

        // ============================================================
        // STATION
        // ============================================================

        private RadioStation? GetStationFromRequest(
            ToolRequest request)
        {
            var stationId =
                GetStringArgument(
                    request,
                    "stationId");

            if (!string.IsNullOrWhiteSpace(stationId))
            {
                return _mpvState.RadioPlaylist
                    .FirstOrDefault(
                        station =>
                            string.Equals(
                                station.Id,
                                stationId,
                                StringComparison.OrdinalIgnoreCase));
            }

            var stationName =
                GetStringArgument(
                    request,
                    "stationName");

            if (!string.IsNullOrWhiteSpace(stationName))
            {
                return _mpvState.RadioPlaylist
                    .FirstOrDefault(
                        station =>
                            string.Equals(
                                station.Name,
                                stationName,
                                StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        // ============================================================
        // PLAYBACK DATA
        // ============================================================

        private object CreatePlaybackData()
        {
            return new
            {
                IsPlaying =
                    _mpvState.IsPlaying,

                Station =
                    _mpvState.RadioStation,

                PlaylistIndex =
                    _mpvState.RadioPlaylistIndex,

                PlaylistCount =
                    _mpvState.RadioPlaylist.Count,

                PlaylistSource =
                    _mpvState.RadioPlaylistSource?.ToString(),

                Title =
                    _mpvState.Title,

                Artist =
                    _mpvState.Artist,

                Album =
                    _mpvState.Album
            };
        }

        // ============================================================
        // ARGUMENT HELPERS
        // ============================================================

        private static string? GetStringArgument(
            ToolRequest request,
            string name)
        {
            if (request.Arguments is null)
            {
                return null;
            }

            if (!request.Arguments.TryGetValue(
                    name,
                    out var value))
            {
                return null;
            }

            return value?.ToString();
        }

        private static int? GetIntArgument(
            ToolRequest request,
            string name)
        {
            var value =
                GetStringArgument(
                    request,
                    name);

            if (int.TryParse(
                    value,
                    out var result))
            {
                return result;
            }

            return null;
        }
    }
}
