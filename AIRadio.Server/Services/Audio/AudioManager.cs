using System.Text.RegularExpressions;
using AIRadio.Server.Models.Radio;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Tts;

namespace AIRadio.Server.Services.Audio
{
    public interface IAudioManager
    {
        bool IsDucked { get; }
        Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default);
        Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default);
        Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default);
        Task DuckAsync(CancellationToken cancellationToken = default);
        Task UnduckAsync(CancellationToken cancellationToken = default);
        Task StopSpeechAsync(CancellationToken cancellationToken = default);
        Task ClearQueueAsync(CancellationToken cancellationToken = default);
        Task EndUtteranceAsync(bool cancel = false, CancellationToken cancellationToken = default);
        Task WaitForCompletionAsync(CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
    }

    public sealed class AudioManager : IAudioManager, IAsyncDisposable
    {
        private static readonly Regex SoundTagRegex = new(
            @"\{sound:(?<tag>[A-Za-z0-9_.-]+)\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger<AudioManager> _logger;
        private readonly ISoundEffectManager _soundEffectManager;
        private readonly IPipeWireAudioClient _pipeWireAudioClient;
        private readonly IPiperClient _piperClient;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvClient _mpvClient;
        private readonly int _duckVolume;
        private readonly AsyncWorkQueue<AudioRequest> _queue;
        private bool _isDucked;
        private int _normalVolume;
        private bool _disposed;

        public AudioManager(ILogger<AudioManager> logger, ISoundEffectManager soundEffectManager, IPipeWireAudioClient pipeWireAudioClient, IPiperClient piperClient, IMpvManager mpvManager, IMpvClient mpvClient, IConfiguration configuration)
        {
            _logger = logger;
            _soundEffectManager = soundEffectManager;
            _pipeWireAudioClient = pipeWireAudioClient;
            _piperClient = piperClient;
            _mpvManager = mpvManager;
            _mpvClient = mpvClient;
            _duckVolume = configuration.GetValue("Mpv:Transport:DuckVolume", 20);
            _queue = new AsyncWorkQueue<AudioRequest>();
            _queue.Start(ProcessRequestAsync);
        }

        public bool IsDucked => Volatile.Read(ref _isDucked);

        public Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            var position = 0;
            foreach (Match match in SoundTagRegex.Matches(text))
            {
                EnqueueSpeech(text[position..match.Index], cancellationToken);
                EnqueueSound(match.Groups["tag"].Value, cancellationToken);
                position = match.Index + match.Length;
            }

            EnqueueSpeech(text[position..], cancellationToken);


            return Task.CompletedTask;
        }

        public Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sound);
            return EnqueueAsync(new(AudioRequestType.Sound, sound, null), cancellationToken);
        }

        public Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(station);
            return EnqueueAsync(new(AudioRequestType.MpvPlayStation, null, station), cancellationToken);
        }

        public Task DuckAsync(CancellationToken cancellationToken = default) => EnqueueAsync(new(AudioRequestType.MpvDuck, null, null), cancellationToken);
        public Task UnduckAsync(CancellationToken cancellationToken = default) => EnqueueAsync(new(AudioRequestType.MpvUnduck, null, null), cancellationToken);
        public Task StopSpeechAsync(CancellationToken cancellationToken = default) => CancelAsync(cancellationToken);

        public Task ClearQueueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _queue.ClearPending();
            return Task.CompletedTask;
        }

        public async Task EndUtteranceAsync(bool cancel = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.WaitForIdleAsync(cancellationToken);
            await _pipeWireAudioClient.EndUtteranceAsync(cancel, cancellationToken);
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.CancelAsync(CancellationToken.None);
            await _pipeWireAudioClient.EndUtteranceAsync(cancel: true, CancellationToken.None);

            if (IsDucked)
            {
                try { await UnduckMpvAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Unable to restore MPV volume after audio cancellation."); }
            }
            _queue.Resume();
        }

        public async Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await _queue.WaitForIdleAsync(cancellationToken);
                await _pipeWireAudioClient.WaitForPlaybackCompleteAsync(cancellationToken);
                if (_queue.IsIdle && _pipeWireAudioClient.QueuedFrameCount == 0 && _pipeWireAudioClient.OutstandingFrameCount == 0)
                    return;
            }
        }

        private void EnqueueSpeech(string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var sentence in SentenceParser.Split(text))
            {
                _logger.LogInformation("Queue speech: " + sentence);
                cancellationToken.ThrowIfCancellationRequested();
                EnqueueAsync(new(AudioRequestType.Speech, sentence, null), cancellationToken);
            }
        }

        private void EnqueueSound(string tag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueAsync(new(AudioRequestType.Sound, tag, null), cancellationToken);
        }

        private Task EnqueueAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queue.TryEnqueue(request))
                _logger.LogDebug("Audio request rejected because the audio queue is not accepting work.");
            return Task.CompletedTask;
        }

        private async Task ProcessRequestAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            try
            {
                switch (request.Type)
                {
                    case AudioRequestType.Speech:
                        await ProcessSpeechAsync(request.Value!, cancellationToken);
                        break;
                    case AudioRequestType.Sound:
                        await ProcessSoundAsync(request.Value!, cancellationToken);
                        break;
                    case AudioRequestType.MpvDuck:
                    case AudioRequestType.MpvUnduck:
                    case AudioRequestType.MpvPlayStation:
                        await ProcessMpvAsync(request, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported audio request type: {request.Type}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Audio request cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio request failed.");
            }
        }

        private async Task ProcessSpeechAsync(string text, CancellationToken cancellationToken)
        {
            var wavData = await _piperClient.GenerateWavAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (wavData.Length == 0)
            {
                _logger.LogWarning("Piper returned no audio data for speech text.");
                return;
            }

            await DuckMpvAsync(cancellationToken);
            try
            {
                await _pipeWireAudioClient.QueueWavAsync(wavData, cancellationToken);
                await _pipeWireAudioClient.WaitForPlaybackCompleteAsync(cancellationToken);
            }
            finally
            {
                await UnduckMpvAsync(CancellationToken.None);
            }
        }

        private async Task ProcessSoundAsync(string tag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Play sound effect " + tag);
            var sound = _soundEffectManager.GetRandomSound(tag);
            if (sound is null)
            {
                _logger.LogWarning("No sound effect found for tag '{Tag}'.", tag);
                return;
            }
            _logger.LogInformation($"Queue sound {tag} ({sound.FileName}) with {sound.WavData.Length} bytes");
            await _pipeWireAudioClient.QueueWavAsync(sound.WavData, cancellationToken);
        }

        private async Task ProcessMpvAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            switch (request.Type)
            {
                case AudioRequestType.MpvDuck:
                    await DuckMpvAsync(cancellationToken);
                    break;
                case AudioRequestType.MpvUnduck:
                    await UnduckMpvAsync(cancellationToken);
                    break;
                case AudioRequestType.MpvPlayStation:
                    ArgumentNullException.ThrowIfNull(request.Station);
                    await _mpvManager.PlayAsync(request.Station, cancellationToken);
                    break;
            }
        }

        private async Task DuckMpvAsync(CancellationToken cancellationToken)
        {
            if (IsDucked || !_mpvClient.IsPlaying)
                return;

            _normalVolume = await _mpvClient.GetVolumeAsync(cancellationToken);
            await _mpvManager.SetVolumeAsync(_duckVolume, cancellationToken);
            Volatile.Write(ref _isDucked, true);
            _logger.LogDebug("Ducked MPV radio volume from {NormalVolume} to {DuckVolume}.", _normalVolume, _duckVolume);
        }

        private async Task UnduckMpvAsync(CancellationToken cancellationToken)
        {
            if (!IsDucked)
                return;

            await _mpvManager.SetVolumeAsync(_normalVolume, cancellationToken);
            Volatile.Write(ref _isDucked, false);
            _logger.LogDebug("Restored MPV radio volume to {NormalVolume}.", _normalVolume);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await _queue.StopAsync(CancellationToken.None); } catch (Exception ex) { _logger.LogDebug(ex, "Error stopping AudioManager queue."); }
            try { await _pipeWireAudioClient.StopPlaybackAsync(CancellationToken.None); } catch (Exception ex) { _logger.LogDebug(ex, "Error clearing PipeWire playback."); }
            await _queue.DisposeAsync();
        }

        private sealed record AudioRequest(AudioRequestType Type, string? Value, RadioStation? Station);

        private enum AudioRequestType
        {
            Speech,
            Sound,
            MpvDuck,
            MpvUnduck,
            MpvPlayStation
        }
    }
}
