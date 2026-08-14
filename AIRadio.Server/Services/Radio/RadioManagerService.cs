using AIRadio.Server.Models.Radio;
using AIRadio.Server.Services.Mpv;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioManagerService
    {
        Task ProcessSpeechAsync(string text, CancellationToken cancellationToken = default);
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
        private readonly IMpvManager _mpvManager;
        private readonly IMpvState _mpvState;

        public RadioManagerService(
            ILogger<RadioManagerService> logger,
            IIntentService intentService,
            IMpvManager mpvManager,
            IMpvState mpvState)
        {
            _logger = logger;
            _intentService = intentService;
            _mpvManager = mpvManager;
            _mpvState = mpvState;
        }

        public Task ProcessSpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            return _intentService.ProcessAsync(text, cancellationToken);
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
            _mpvManager.PlayAsync(station, cancellationToken);

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
    }
}
