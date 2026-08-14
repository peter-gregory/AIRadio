using AIRadio.Server.Models.Radio;
using System.Collections.ObjectModel;

namespace AIRadio.Server.Services.Mpv
{
    public interface IMpvState
    {
        bool IsConnected { get; }

        bool IsIdle { get; }

        bool IsPlaying { get; }

        bool IsPaused { get; }

        bool IsMuted { get; }

        int Volume { get; }

        RadioStation? RadioStation { get; }

        IReadOnlyList<RadioStation> RadioPlaylist { get; }

        int RadioPlaylistCount { get; }

        int RadioPlaylistIndex { get; }

        RadioPlaylistSource? RadioPlaylistSource { get; }

        string? CurrentUrl { get; }

        string? Title { get; }

        string? Artist { get; }

        string? Album { get; }

        TimeSpan Position { get; }

        TimeSpan Duration { get; }

        DateTimeOffset LastUpdated { get; }

        event EventHandler<MpvStateChangedEventArgs>? StateChanged;

        void Update(
            Action<MpvStateUpdate> configure);
    }

    public sealed class MpvState : IMpvState
    {
        private readonly object _stateLock = new();

        private bool _isConnected;
        private bool _isIdle = true;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isMuted;

        private int _volume;

        private RadioStation? _radioStation;

        /*
         * The radio playlist always exists.
         *
         * It represents the stations currently available for
         * selection. It is not a traditional saved media playlist.
         */
        private IReadOnlyList<RadioStation> _radioPlaylist =
            Array.Empty<RadioStation>();

        private int _radioPlaylistIndex;

        private RadioPlaylistSource? _radioPlaylistSource;

        private string? _currentUrl;
        private string? _title;
        private string? _artist;
        private string? _album;

        private TimeSpan _position;
        private TimeSpan _duration;

        private DateTimeOffset _lastUpdated =
            DateTimeOffset.UtcNow;

        public MpvState()
        {
        }

        // ============================================================
        // CONNECTION / PLAYBACK STATE
        // ============================================================

        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                {
                    return _isConnected;
                }
            }
        }

        public bool IsIdle
        {
            get
            {
                lock (_stateLock)
                {
                    return _isIdle;
                }
            }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_stateLock)
                {
                    return _isPlaying;
                }
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (_stateLock)
                {
                    return _isPaused;
                }
            }
        }

        public bool IsMuted
        {
            get
            {
                lock (_stateLock)
                {
                    return _isMuted;
                }
            }
        }

        public int Volume
        {
            get
            {
                lock (_stateLock)
                {
                    return _volume;
                }
            }
        }

        // ============================================================
        // RADIO STATE
        // ============================================================

        public RadioStation? RadioStation
        {
            get
            {
                lock (_stateLock)
                {
                    return _radioStation;
                }
            }
        }

        public IReadOnlyList<RadioStation> RadioPlaylist
        {
            get
            {
                lock (_stateLock)
                {
                    return _radioPlaylist;
                }
            }
        }

        public int RadioPlaylistCount
        {
            get
            {
                lock (_stateLock)
                {
                    return _radioPlaylist.Count;
                }
            }
        }

        public int RadioPlaylistIndex
        {
            get
            {
                lock (_stateLock)
                {
                    return _radioPlaylistIndex;
                }
            }
        }

        public RadioPlaylistSource? RadioPlaylistSource
        {
            get
            {
                lock (_stateLock)
                {
                    return _radioPlaylistSource;
                }
            }
        }

        // ============================================================
        // METADATA
        // ============================================================

        public string? CurrentUrl
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentUrl;
                }
            }
        }

        public string? Title
        {
            get
            {
                lock (_stateLock)
                {
                    return _title;
                }
            }
        }

        public string? Artist
        {
            get
            {
                lock (_stateLock)
                {
                    return _artist;
                }
            }
        }

        public string? Album
        {
            get
            {
                lock (_stateLock)
                {
                    return _album;
                }
            }
        }

        public TimeSpan Position
        {
            get
            {
                lock (_stateLock)
                {
                    return _position;
                }
            }
        }

        public TimeSpan Duration
        {
            get
            {
                lock (_stateLock)
                {
                    return _duration;
                }
            }
        }

        public DateTimeOffset LastUpdated
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastUpdated;
                }
            }
        }

        // ============================================================
        // EVENTS
        // ============================================================

        public event EventHandler<MpvStateChangedEventArgs>?
            StateChanged;

        // ============================================================
        // UPDATE
        // ============================================================

        public void Update(
            Action<MpvStateUpdate> configure)
        {
            ArgumentNullException.ThrowIfNull(
                configure);

            MpvStateChangedEventArgs eventArgs;

            lock (_stateLock)
            {
                var update =
                    new MpvStateUpdate();

                configure(update);

                ApplyUpdate(update);

                _lastUpdated =
                    DateTimeOffset.UtcNow;

                eventArgs =
                    new MpvStateChangedEventArgs(
                        this);
            }

            /*
             * Never invoke application event handlers while holding
             * the state lock.
             */
            StateChanged?.Invoke(
                this,
                eventArgs);
        }

        // ============================================================
        // APPLY UPDATE
        // ============================================================

        private void ApplyUpdate(
            MpvStateUpdate update)
        {
            if (update.IsConnected.HasValue)
            {
                _isConnected =
                    update.IsConnected.Value;
            }

            if (update.IsIdle.HasValue)
            {
                _isIdle =
                    update.IsIdle.Value;
            }

            if (update.IsPlaying.HasValue)
            {
                _isPlaying =
                    update.IsPlaying.Value;
            }

            if (update.IsPaused.HasValue)
            {
                _isPaused =
                    update.IsPaused.Value;
            }

            if (update.IsMuted.HasValue)
            {
                _isMuted =
                    update.IsMuted.Value;
            }

            if (update.Volume.HasValue)
            {
                _volume =
                    Math.Clamp(
                        update.Volume.Value,
                        0,
                        100);
            }

            if (update.RadioStation is not null)
            {
                _radioStation =
                    update.RadioStation;
            }

            if (update.RadioPlaylist is not null)
            {
                SetRadioPlaylist(
                    update.RadioPlaylist);
            }

            if (update.RadioPlaylistIndex.HasValue)
            {
                SetRadioPlaylistIndex(
                    update.RadioPlaylistIndex.Value);
            }

            if (update.RadioPlaylistSource.HasValue)
            {
                _radioPlaylistSource =
                    update.RadioPlaylistSource.Value;
            }

            if (update.CurrentUrl is not null)
            {
                _currentUrl =
                    update.CurrentUrl;
            }

            if (update.Title is not null)
            {
                _title =
                    update.Title;
            }

            if (update.Artist is not null)
            {
                _artist =
                    update.Artist;
            }

            if (update.Album is not null)
            {
                _album =
                    update.Album;
            }

            if (update.Position.HasValue)
            {
                _position =
                    update.Position.Value;
            }

            if (update.Duration.HasValue)
            {
                _duration =
                    update.Duration.Value;
            }
        }

        // ============================================================
        // PLAYLIST
        // ============================================================

        private void SetRadioPlaylist(
            IReadOnlyList<RadioStation> playlist)
        {
            ArgumentNullException.ThrowIfNull(
                playlist);

            if (playlist.Count == 0)
            {
                throw new ArgumentException(
                    "RadioPlaylist must contain at least one station.",
                    nameof(playlist));
            }

            /*
             * Make a defensive copy so callers cannot modify the
             * state underneath us after Update() returns.
             */
            _radioPlaylist =
                new ReadOnlyCollection<RadioStation>(
                    playlist.ToList());

            /*
             * Keep the current index valid when a new station list
             * replaces the existing list.
             */
            if (_radioPlaylistIndex >=
                _radioPlaylist.Count)
            {
                _radioPlaylistIndex =
                    _radioPlaylist.Count - 1;
            }

            if (_radioPlaylistIndex < 0)
            {
                _radioPlaylistIndex = 0;
            }
        }

        private void SetRadioPlaylistIndex(
            int index)
        {
            if (_radioPlaylist.Count == 0)
            {
                throw new InvalidOperationException(
                    "RadioPlaylist must contain at least one station.");
            }

            if (index < 0 ||
                index >= _radioPlaylist.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "Radio playlist index is outside the playlist.");
            }

            _radioPlaylistIndex =
                index;
        }

        // ============================================================
        // SNAPSHOT
        // ============================================================

        public MpvStateSnapshot CreateSnapshot()
        {
            lock (_stateLock)
            {
                return new MpvStateSnapshot
                {
                    IsConnected =
                        _isConnected,

                    IsIdle =
                        _isIdle,

                    IsPlaying =
                        _isPlaying,

                    IsPaused =
                        _isPaused,

                    IsMuted =
                        _isMuted,

                    Volume =
                        _volume,

                    RadioStation =
                        _radioStation,

                    RadioPlaylist =
                        _radioPlaylist.ToList(),

                    RadioPlaylistIndex =
                        _radioPlaylistIndex,

                    RadioPlaylistSource =
                        _radioPlaylistSource,

                    CurrentUrl =
                        _currentUrl,

                    Title =
                        _title,

                    Artist =
                        _artist,

                    Album =
                        _album,

                    Position =
                        _position,

                    Duration =
                        _duration,

                    LastUpdated =
                        _lastUpdated
                };
            }
        }
    }

    public sealed class MpvStateUpdate
    {
        public bool? IsConnected { get; set; }

        public bool? IsIdle { get; set; }

        public bool? IsPlaying { get; set; }

        public bool? IsPaused { get; set; }

        public bool? IsMuted { get; set; }

        public int? Volume { get; set; }

        public RadioStation? RadioStation { get; set; }

        public IReadOnlyList<RadioStation>? RadioPlaylist { get; set; }

        public int? RadioPlaylistIndex { get; set; }

        public RadioPlaylistSource? RadioPlaylistSource { get; set; }

        public string? CurrentUrl { get; set; }

        public string? Title { get; set; }

        public string? Artist { get; set; }

        public string? Album { get; set; }

        public TimeSpan? Position { get; set; }

        public TimeSpan? Duration { get; set; }
    }

    public sealed class MpvStateSnapshot
    {
        public bool IsConnected { get; init; }

        public bool IsIdle { get; init; }

        public bool IsPlaying { get; init; }

        public bool IsPaused { get; init; }

        public bool IsMuted { get; init; }

        public int Volume { get; init; }

        public RadioStation? RadioStation { get; init; }

        public IReadOnlyList<RadioStation> RadioPlaylist { get; init; } =
            [];

        public int RadioPlaylistCount =>
            RadioPlaylist.Count;

        public int RadioPlaylistIndex { get; init; }

        public RadioPlaylistSource? RadioPlaylistSource { get; init; }

        public string? CurrentUrl { get; init; }

        public string? Title { get; init; }

        public string? Artist { get; init; }

        public string? Album { get; init; }

        public TimeSpan Position { get; init; }

        public TimeSpan Duration { get; init; }

        public DateTimeOffset LastUpdated { get; init; }
    }

    public enum RadioPlaylistSource
    {
        Search,
        Favorites,
        SavedStations,
        Recent,
        Recommendation
    }

    public sealed class MpvStateChangedEventArgs : EventArgs
    {
        public MpvStateChangedEventArgs(
            IMpvState state)
        {
            State = state;
        }

        public IMpvState State { get; }
    }

}
