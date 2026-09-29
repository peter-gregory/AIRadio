using AIRadio.Server.Models.Radio;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Mpv;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioManagerService
    {
        Task ProcessSpeechAsync(string text, CancellationToken cancellationToken = default);
        Task<Guid> ProcessAlarmAsync(string text, CancellationToken cancellationToken = default);
        Task RenderAlarmAsync(string content, CancellationToken cancellationToken = default);
        Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default);
        Task PlayPlaylistStationAsync(int index, CancellationToken cancellationToken = default);
        Task PlayNextStationAsync(CancellationToken cancellationToken = default);
        Task PlayPreviousStationAsync(CancellationToken cancellationToken = default);
        Task StopRadioAsync(CancellationToken cancellationToken = default);
        void SetRadioPlaylist(IReadOnlyList<RadioStation> stations, RadioPlaylistSource source);
        IMpvState GetRadioState();
    }

    public sealed class RadioManagerService : IRadioManagerService
    {
        private readonly ILogger<RadioManagerService> _logger;
        private readonly IIntentService _intentService;
        private readonly IAudioManager _audioManager;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvState _mpvState;
        private readonly IConversationService _conversationService;

        public RadioManagerService(
            ILogger<RadioManagerService> logger,
            IIntentService intentService,
            IAudioManager audioManager,
            IMpvManager mpvManager,
            IMpvState mpvState,
            IConversationService conversationService)
        {
            _logger = logger;
            _intentService = intentService;
            _audioManager = audioManager;
            _mpvManager = mpvManager;
            _mpvState = mpvState;
            _conversationService = conversationService;
            _conversationService.StateChanged += OnConversationStateChanged;
            _audioManager.PlaybackCompleted += OnAudioPlaybackCompleted;
            _logger.LogInformation("Finished starting RadioManagerService");
        }

        public async Task ProcessSpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            _logger.LogInformation("Processing received voice prompt: " + text);
            await _audioManager.DuckAsync(cancellationToken);
            await _intentService.ProcessAsync(text, cancellationToken);
        }

        public Task<Guid> ProcessAlarmAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            _logger.LogInformation("Processing alarm command directly: " + text);
            return _intentService.ProcessAlarmAsync(text, cancellationToken);
        }

        public async Task RenderAlarmAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(content);

            _logger.LogInformation("Rendering alarm: {Content}", content);

            await _intentService.CancelAsync(cancellationToken);
            await _intentService.ProcessAsync(content, cancellationToken);
        }

        public Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default) =>
            _audioManager.PlayStationAsync(station, cancellationToken);

        public Task PlayPlaylistStationAsync(int index, CancellationToken cancellationToken = default) =>
            _mpvManager.PlayPlaylistStationAsync(index, cancellationToken);

        public Task PlayNextStationAsync(CancellationToken cancellationToken = default) =>
            _mpvManager.PlayNextRadioStationAsync(cancellationToken);

        public Task PlayPreviousStationAsync(CancellationToken cancellationToken = default) =>
            _mpvManager.PlayPreviousRadioStationAsync(cancellationToken);

        public Task StopRadioAsync(CancellationToken cancellationToken = default) =>
            _mpvManager.StopAsync(cancellationToken);

        public void SetRadioPlaylist(IReadOnlyList<RadioStation> stations, RadioPlaylistSource source) =>
            _mpvManager.SetRadioPlaylist(stations, source);

        public IMpvState GetRadioState() => _mpvState;

        private void OnConversationStateChanged(
            object? sender,
            ConversationStateChangedEventArgs e)
        {
            if (e.Current != ConversationState.Idle)
                return;

            _logger.LogInformation(
                "Conversation {ConversationId} reached Idle; checking whether speech playback is complete.",
                e.ConversationId);

            // Do not wait for speech here. If PipeWire is still playing,
            // the PlaybackCompleted event will perform the second Idle check.
            if (!_audioManager.IsPlaybackComplete)
            {
                _logger.LogInformation(
                    "Conversation {ConversationId} is idle, but speech playback is still active; waiting for PlaybackCompleted.",
                    e.ConversationId);
                return;
            }

            _logger.LogInformation(
                "Conversation {ConversationId} is idle and speech playback is already complete; restoring radio volume.",
                e.ConversationId);

            _ = RestoreRadioVolumeAsync(e.ConversationId);
        }

        private async Task RestoreRadioVolumeAsync(Guid conversationId)
        {
            try
            {
                if (_conversationService.State != ConversationState.Idle ||
                    !_audioManager.IsPlaybackComplete ||
                    !_audioManager.IsDucked)
                    return;

                _logger.LogInformation(
                    "Conversation {ConversationId} is idle and speech playback is complete; restoring radio volume.",
                    conversationId);

                await _audioManager.UnduckAsync(CancellationToken.None);

                _logger.LogInformation(
                    "Radio volume restored after conversation {ConversationId}.",
                    conversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to restore radio volume after conversation {ConversationId}.",
                    conversationId);
            }
        }

        private void OnAudioPlaybackCompleted(object? sender, EventArgs e)
        {
            var state = _conversationService.State;

            _logger.LogInformation(
                "C# PlaybackCompleted callback received by RadioManagerService; conversationState={ConversationState}.",
                state);

            if (state != ConversationState.Idle)
            {
                _logger.LogInformation(
                    "PlaybackCompleted received while conversation is not idle; radio resume deferred until conversation reaches Idle.");
                return;
            }

            _logger.LogInformation(
                "PlaybackCompleted received while conversation is idle; restoring radio volume now.");

            _ = RestoreRadioVolumeAsync(Guid.Empty);
        }
    }
}
