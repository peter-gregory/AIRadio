using AIRadio.Server.Models.Mpv;
using AIRadio.Server.Models.Radio;

namespace AIRadio.Server.Services.Mpv
{
    public interface IMpvManager
    {
        Task PlayAsync(
            RadioStation station,
            CancellationToken cancellationToken = default);

        Task PlayPlaylistStationAsync(
            int index,
            CancellationToken cancellationToken = default);

        Task PlayNextRadioStationAsync(
            CancellationToken cancellationToken = default);

        Task PlayPreviousRadioStationAsync(
            CancellationToken cancellationToken = default);

        void SetRadioPlaylist(
            IReadOnlyList<RadioStation> stations,
            RadioPlaylistSource source);

        Task StopAsync(
            CancellationToken cancellationToken = default);

        Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default);
    }

    public sealed class MpvManager : IMpvManager
    {
        private readonly ILogger<MpvManager> _logger;
        private readonly IMpvClient _mpv;
        private readonly IMpvState _state;

        public MpvManager(
            ILogger<MpvManager> logger,
            IMpvClient mpv,
            IMpvState state)
        {
            _logger = logger;
            _mpv = mpv;
            _state = state;

            _mpv.PlaybackChanged +=
                OnPlaybackChanged;

            _mpv.MetadataChanged +=
                OnMetadataChanged;

            _mpv.PropertyChanged +=
                OnPropertyChanged;

            _mpv.Error +=
                OnMpvError;
        }

        // ============================================================
        // PLAY
        // ============================================================

        public async Task PlayAsync(
            RadioStation station,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(station);

            if (string.IsNullOrWhiteSpace(
                    station.StreamUrl))
            {
                throw new ArgumentException(
                    "Station does not contain a stream URL.",
                    nameof(station));
            }

            EnsureConnected();

            await _mpv.PlayStreamAsync(
                station.StreamUrl,
                cancellationToken);

            var playlistIndex =
                FindPlaylistIndex(station.Id);

            _state.Update(
                update =>
                {
                    update.RadioStation = station;

                    update.IsConnected =
                        _mpv.IsConnected;

                    update.IsPlaying = true;
                    update.IsPaused = false;
                    update.IsIdle = false;

                    update.CurrentUrl =
                        station.StreamUrl;

                    update.Position =
                        TimeSpan.Zero;

                    update.Duration =
                        TimeSpan.Zero;

                    update.Title = null;
                    update.Artist = null;
                    update.Album = null;

                    if (playlistIndex >= 0)
                    {
                        update.RadioPlaylistIndex =
                            playlistIndex;
                    }
                });

            _logger.LogInformation(
                "Playing radio station {StationName} ({StationId}).",
                station.Name,
                station.Id);
        }

        // ============================================================
        // PLAY PLAYLIST STATION
        // ============================================================

        public Task PlayPlaylistStationAsync(
            int index,
            CancellationToken cancellationToken = default)
        {
            ValidatePlaylistIndex(index);

            var station =
                _state.RadioPlaylist[index];

            return PlayAsync(
                station,
                cancellationToken);
        }

        // ============================================================
        // NEXT
        // ============================================================

        public Task PlayNextRadioStationAsync(
            CancellationToken cancellationToken = default)
        {
            var playlist =
                _state.RadioPlaylist;

            if (playlist.Count == 0)
            {
                throw new InvalidOperationException(
                    "The radio playlist is empty.");
            }

            var nextIndex =
                _state.RadioPlaylistIndex + 1;

            if (nextIndex >= playlist.Count)
            {
                nextIndex = 0;
            }

            return PlayPlaylistStationAsync(
                nextIndex,
                cancellationToken);
        }

        // ============================================================
        // PREVIOUS
        // ============================================================

        public Task PlayPreviousRadioStationAsync(
            CancellationToken cancellationToken = default)
        {
            var playlist =
                _state.RadioPlaylist;

            if (playlist.Count == 0)
            {
                throw new InvalidOperationException(
                    "The radio playlist is empty.");
            }

            var previousIndex =
                _state.RadioPlaylistIndex - 1;

            if (previousIndex < 0)
            {
                previousIndex =
                    playlist.Count - 1;
            }

            return PlayPlaylistStationAsync(
                previousIndex,
                cancellationToken);
        }

        // ============================================================
        // PLAYLIST
        // ============================================================

        public void SetRadioPlaylist(
            IReadOnlyList<RadioStation> stations,
            RadioPlaylistSource source)
        {
            ArgumentNullException.ThrowIfNull(
                stations);

            if (stations.Count == 0)
            {
                throw new ArgumentException(
                    "Radio playlist must contain at least one station.",
                    nameof(stations));
            }

            var playlist =
                stations.ToList();

            _state.Update(
                update =>
                {
                    update.RadioPlaylist =
                        playlist;

                    update.RadioPlaylistSource =
                        source;

                    update.RadioPlaylistIndex =
                        0;
                });

            _logger.LogInformation(
                "Radio playlist set to {Count} stations from {Source}.",
                playlist.Count,
                source);
        }

        // ============================================================
        // STOP
        // ============================================================

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            await _mpv.StopAsync(
                cancellationToken);

            _state.Update(
                update =>
                {
                    update.IsConnected =
                        _mpv.IsConnected;

                    update.IsPlaying = false;
                    update.IsPaused = false;
                    update.IsIdle = true;

                    update.CurrentUrl = null;
                    update.Title = null;
                    update.Artist = null;
                    update.Album = null;

                    update.Position =
                        TimeSpan.Zero;

                    update.Duration =
                        TimeSpan.Zero;
                });

            _logger.LogInformation(
                "Radio playback stopped.");
        }

        // ============================================================
        // VOLUME
        // ============================================================

        public async Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default)
        {
            volume =
                Math.Clamp(
                    volume,
                    0,
                    100);

            EnsureConnected();

            await _mpv.SetVolumeAsync(
                volume,
                cancellationToken);

            _state.Update(
                update =>
                {
                    update.Volume =
                        volume;

                    update.IsMuted =
                        volume == 0;
                });
        }

        // ============================================================
        // VALIDATION
        // ============================================================

        private void EnsureConnected()
        {
            if (!_mpv.IsConnected)
            {
                throw new InvalidOperationException(
                    "MPV is not connected.");
            }
        }

        private void ValidatePlaylistIndex(
            int index)
        {
            var count =
                _state.RadioPlaylist.Count;

            if (count == 0)
            {
                throw new InvalidOperationException(
                    "The radio playlist is empty.");
            }

            if (index < 0 ||
                index >= count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "Radio playlist index is outside the playlist.");
            }
        }

        private int FindPlaylistIndex(
            string stationId)
        {
            var playlist =
                _state.RadioPlaylist;

            for (var i = 0;
                 i < playlist.Count;
                 i++)
            {
                if (string.Equals(
                        playlist[i].Id,
                        stationId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        // ============================================================
        // MPV EVENTS
        // ============================================================

        private void OnPlaybackChanged(
            object? sender,
            EventArgs e)
        {
            _state.Update(
                update =>
                {
                    update.IsConnected =
                        _mpv.IsConnected;

                    update.IsPlaying =
                        _mpv.IsPlaying;

                    update.IsIdle =
                        !_mpv.IsPlaying;
                });
        }

        private void OnMetadataChanged(
            object? sender,
            EventArgs e)
        {
            _logger.LogDebug(
                "MPV metadata changed.");
        }

        private void OnPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            _logger.LogDebug(
                "MPV property changed: {PropertyName}.",
                e.PropertyName);
        }

        private void OnMpvError(
            object? sender,
            MpvErrorEventArgs e)
        {
            _logger.LogWarning(
                "MPV error: {Error}.",
                e);
        }

        // ============================================================
        // DISPOSAL
        // ============================================================

        public async ValueTask DisposeAsync()
        {
            _mpv.PlaybackChanged -=
                OnPlaybackChanged;

            _mpv.MetadataChanged -=
                OnMetadataChanged;

            _mpv.PropertyChanged -=
                OnPropertyChanged;

            _mpv.Error -=
                OnMpvError;

            await _mpv.DisposeAsync();
        }
    }

    public enum MpvManagerCommandType
    {
        PlayStation
    }

    public sealed record MpvManagerCommand(
        MpvManagerCommandType Type,
        RadioStation Station)
    {
        public static MpvManagerCommand PlayStation(
            RadioStation station)
        {
            ArgumentNullException.ThrowIfNull(station);

            return new MpvManagerCommand(
                MpvManagerCommandType.PlayStation,
                station);
        }
    }
}
