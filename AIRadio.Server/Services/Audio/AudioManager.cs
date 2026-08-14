using AIRadio.Server.Models.Audio;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Tts;

namespace AIRadio.Server.Services.Audio
{
    public interface IAudioManager
    {
        bool IsSpeechPlaying { get; }
        bool IsDucked { get; }

        Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default);
        Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default);
        Task PlayAudioAsync(string path, CancellationToken cancellationToken = default);
        Task DuckAsync(CancellationToken cancellationToken = default);
        Task UnduckAsync(CancellationToken cancellationToken = default);
        Task StopSpeechAsync(CancellationToken cancellationToken = default);
        Task ClearQueueAsync(CancellationToken cancellationToken = default);
        Task WaitForCompletionAsync(CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
    }

    public sealed class AudioManager : IAudioManager, IAsyncDisposable
    {
        private readonly ILogger<AudioManager> _logger;
        private readonly ISoundEffectManager _soundEffectManager;
        private readonly IPipeWireAudioClient _pipeWireAudioClient;
        private readonly IPiperClient _piperClient;
        private readonly AsyncWorkQueue<AudioRequest> _queue;

        private bool _isSpeechPlaying;
        private bool _isDucked;
        private bool _disposed;

        public AudioManager(
            ILogger<AudioManager> logger,
            ISoundEffectManager soundEffectManager,
            IPipeWireAudioClient pipeWireAudioClient,
            IPiperClient piperClient)
        {
            _logger = logger;
            _soundEffectManager = soundEffectManager;
            _pipeWireAudioClient = pipeWireAudioClient;
            _piperClient = piperClient;

            _queue = new AsyncWorkQueue<AudioRequest>();
            _queue.Start(ProcessRequestAsync);
        }

        public bool IsSpeechPlaying => Volatile.Read(ref _isSpeechPlaying);
        public bool IsDucked => Volatile.Read(ref _isDucked);

        public Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            return EnqueueAsync(new(AudioRequestType.Speech, text), cancellationToken);
        }

        public Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sound);
            return EnqueueAsync(new(AudioRequestType.Sound, sound), cancellationToken);
        }

        public Task PlayAudioAsync(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return EnqueueAsync(new(AudioRequestType.AudioFile, path), cancellationToken);
        }

        public Task DuckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDucked)
                return Task.CompletedTask;

            Volatile.Write(ref _isDucked, true);
            _logger.LogDebug("Audio duck requested.");
            return Task.CompletedTask;
        }

        public Task UnduckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDucked)
                return Task.CompletedTask;

            Volatile.Write(ref _isDucked, false);
            _logger.LogDebug("Audio unduck requested.");
            return Task.CompletedTask;
        }

        public Task StopSpeechAsync(CancellationToken cancellationToken = default) =>
            CancelAsync(cancellationToken);

        public void ClearQueue()
        {
            _queue.ClearPending();
        }

        public Task ClearQueueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _queue.ClearPending();
            return Task.CompletedTask;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            // Once cancellation starts, it must finish. The caller token
            // only matters before cancellation is initiated.
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.CancelAsync(CancellationToken.None);

            await _pipeWireAudioClient.StopPlaybackAsync(CancellationToken.None);
            await _pipeWireAudioClient.ClearQueueAsync(CancellationToken.None);

            Volatile.Write(ref _isSpeechPlaying, false);
            _queue.Resume();
        }

        public async Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await _queue.WaitForIdleAsync(cancellationToken);
                await _pipeWireAudioClient.WaitForPlaybackCompleteAsync(cancellationToken);

                if (_queue.IsIdle &&
                    !_pipeWireAudioClient.IsPlaying &&
                    _pipeWireAudioClient.QueuedFrameCount == 0)
                {
                    return;
                }
            }
        }

        private Task EnqueueAsync(
            AudioRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_queue.TryEnqueue(request))
            {
                _logger.LogDebug(
                    "Audio request rejected because the audio queue is not accepting work.");
            }

            return Task.CompletedTask;
        }

        private async Task ProcessRequestAsync(
            AudioRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                if (request.Type == AudioRequestType.Speech)
                    Volatile.Write(ref _isSpeechPlaying, true);

                switch (request.Type)
                {
                    case AudioRequestType.Speech:
                        await ProcessSpeechAsync(request.Value, cancellationToken);
                        break;
                    case AudioRequestType.Sound:
                        await ProcessSoundAsync(request.Value, cancellationToken);
                        break;
                    case AudioRequestType.AudioFile:
                        await ProcessAudioFileAsync(request.Value, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported audio request type: {request.Type}");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Audio request cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio request failed.");
            }
            finally
            {
                if (request.Type == AudioRequestType.Speech)
                    Volatile.Write(ref _isSpeechPlaying, false);
            }
        }

        private async Task ProcessSpeechAsync(
            string text,
            CancellationToken cancellationToken)
        {
            var wavData = await _piperClient.GenerateWavAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (wavData.Length == 0)
            {
                _logger.LogWarning("Piper returned no audio data for speech text.");
                return;
            }

            await _pipeWireAudioClient.QueueWavAsync(wavData, cancellationToken);
        }

        private async Task ProcessSoundAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sound = _soundEffectManager.GetRandomSound(tag);
            if (sound is null)
            {
                _logger.LogWarning("No sound effect found for tag '{Tag}'.", tag);
                return;
            }

            await _pipeWireAudioClient.QueueWavAsync(
                sound.WavData,
                cancellationToken);
        }

        private Task ProcessAudioFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Playing audio file: {Path}", path);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                await _queue.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error stopping AudioManager queue.");
            }

            try
            {
                await _pipeWireAudioClient.StopPlaybackAsync(CancellationToken.None);
                await _pipeWireAudioClient.ClearQueueAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error clearing PipeWire playback.");
            }

            await _queue.DisposeAsync();
        }

        private sealed record AudioRequest(AudioRequestType Type, string Value);

        private enum AudioRequestType
        {
            Speech,
            Sound,
            AudioFile
        }
    }
}
