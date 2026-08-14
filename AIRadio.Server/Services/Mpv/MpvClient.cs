using AIRadio.Server.Models.Mpv;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;


namespace AIRadio.Server.Services.Mpv
{
    public interface IMpvClient : IAsyncDisposable
    {
        bool IsConnected { get; }

        bool IsPlaying { get; }

        Task ConnectAsync(
            CancellationToken cancellationToken = default);

        Task DisconnectAsync(
            CancellationToken cancellationToken = default);

        Task PlayStreamAsync(
            string url,
            CancellationToken cancellationToken = default);

        Task StopAsync(
            CancellationToken cancellationToken = default);

        Task PauseAsync(
            CancellationToken cancellationToken = default);

        Task ResumeAsync(
            CancellationToken cancellationToken = default);

        Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default);

        Task<int> GetVolumeAsync(
            CancellationToken cancellationToken = default);

        event EventHandler? PlaybackChanged;

        event EventHandler? MetadataChanged;

        event PropertyChangedEventHandler? PropertyChanged;

        event EventHandler<MpvErrorEventArgs>? Error;
    }

    public sealed class MpvClient :
        IMpvClient
    {
        private readonly IMpvTransport _transport;
        private readonly ILogger<MpvClient> _logger;

        private bool _isPlaying;
        private bool _disposed;

        private int _volume;

        public MpvClient(
            IMpvTransport transport,
            ILogger<MpvClient> logger)
        {
            _transport = transport;
            _logger = logger;

            _transport.MessageReceived +=
                OnMessageReceived;

            _transport.Error +=
                OnTransportError;
        }

        public bool IsConnected =>
            _transport.IsConnected;

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (_isPlaying == value)
                    return;

                _isPlaying = value;

                OnPropertyChanged();

                PlaybackChanged?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        }

        public event EventHandler? PlaybackChanged;

        public event EventHandler? MetadataChanged;

        public event PropertyChangedEventHandler?
            PropertyChanged;

        public event EventHandler<MpvErrorEventArgs>?
            Error;

        public async Task ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _transport.ConnectAsync(
                cancellationToken);
        }

        public async Task DisconnectAsync(
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return;

            await _transport.DisconnectAsync(
                cancellationToken);

            IsPlaying = false;
        }

        public async Task PlayStreamAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ArgumentException.ThrowIfNullOrWhiteSpace(
                url);

            var command =
                MpvCommand.Command(
                    "loadfile",
                    url,
                    "replace");

            await _transport.SendCommandAsync(
                command,
                cancellationToken);

            IsPlaying = true;
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _transport.SendCommandAsync(
                MpvCommand.Command(
                    "stop"),
                cancellationToken);

            IsPlaying = false;
        }

        public async Task PauseAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _transport.SendCommandAsync(
                MpvCommand.SetProperty(
                    "pause",
                    true),
                cancellationToken);

            IsPlaying = false;
        }

        public async Task ResumeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _transport.SendCommandAsync(
                MpvCommand.SetProperty(
                    "pause",
                    false),
                cancellationToken);

            IsPlaying = true;
        }

        public async Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            volume =
                Math.Clamp(
                    volume,
                    0,
                    100);

            await _transport.SendCommandAsync(
                MpvCommand.SetProperty(
                    "volume",
                    volume),
                cancellationToken);

            VolumeChanged(
                volume);
        }

        public async Task<int> GetVolumeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var result =
                await _transport.SendCommandAsync(
                    MpvCommand.GetProperty(
                        "volume"),
                    cancellationToken);

            if (result is null)
                return _volume;

            if (result.Value.ValueKind ==
                JsonValueKind.Number &&
                result.Value.TryGetInt32(
                    out var volume))
            {
                VolumeChanged(volume);

                return volume;
            }

            if (result.Value.ValueKind ==
                JsonValueKind.Number &&
                result.Value.TryGetDouble(
                    out var doubleVolume))
            {
                volume =
                    (int)Math.Round(
                        doubleVolume);

                VolumeChanged(volume);

                return volume;
            }

            return _volume;
        }

        private void OnMessageReceived(
            object? sender,
            MpvMessageEventArgs e)
        {
            try
            {
                ProcessMessage(
                    e.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing MPV message.");

                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        ex.Message));
            }
        }

        private void ProcessMessage(
            JsonElement message)
        {
            if (!message.TryGetProperty(
                    "event",
                    out var eventElement))
            {
                return;
            }

            var eventName =
                eventElement.GetString();

            switch (eventName)
            {
                case "start-file":
                    IsPlaying = true;
                    break;

                case "end-file":
                    IsPlaying = false;

                    PlaybackChanged?.Invoke(
                        this,
                        EventArgs.Empty);

                    break;

                case "file-loaded":
                case "metadata-update":
                    MetadataChanged?.Invoke(
                        this,
                        EventArgs.Empty);

                    break;

                case "pause":
                    IsPlaying = false;
                    break;

                case "unpause":
                    IsPlaying = true;
                    break;
            }
        }

        private void OnTransportError(
            object? sender,
            MpvErrorEventArgs e)
        {
            Error?.Invoke(
                this,
                e);
        }

        private void VolumeChanged(
            int volume)
        {
            volume =
                Math.Clamp(
                    volume,
                    0,
                    100);

            if (_volume == volume)
                return;

            _volume = volume;

            OnPropertyChanged(
                nameof(Volume));
        }

        private int Volume =>
            _volume;

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(
                    propertyName));
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            _transport.MessageReceived -=
                OnMessageReceived;

            _transport.Error -=
                OnTransportError;

            await _transport.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }
}
