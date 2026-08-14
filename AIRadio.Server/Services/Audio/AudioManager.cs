using AIRadio.Server.Models.Audio;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Tts;

namespace AIRadio.Server.Services.Audio
{
    /// <summary>
    /// Provides audio orchestration services for synthesized speech,
    /// sound effects, and media control.
    /// </summary>
    public interface IAudioManager
    {
        bool IsSpeechPlaying { get; }

        bool IsDucked { get; }

        Task PlaySpeechAsync(
            string text,
            CancellationToken cancellationToken = default);

        Task PlaySoundAsync(
            string sound,
            CancellationToken cancellationToken = default);

        Task PlayAudioAsync(
            string path,
            CancellationToken cancellationToken = default);

        Task DuckAsync(
            CancellationToken cancellationToken = default);

        Task UnduckAsync(
            CancellationToken cancellationToken = default);

        Task StopSpeechAsync(
            CancellationToken cancellationToken = default);

        Task ClearQueueAsync(
            CancellationToken cancellationToken = default);

        Task WaitForCompletionAsync(
            CancellationToken cancellationToken = default);

        Task CancelAsync(
            CancellationToken cancellationToken = default);
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
            _logger = logger ??
                throw new ArgumentNullException(nameof(logger));

            _soundEffectManager = soundEffectManager ??
                throw new ArgumentNullException(nameof(soundEffectManager));

            _pipeWireAudioClient = pipeWireAudioClient ??
                throw new ArgumentNullException(nameof(pipeWireAudioClient));

            _piperClient = piperClient ??
                throw new ArgumentNullException(nameof(piperClient));

            _queue =
                new AsyncWorkQueue<AudioRequest>();

            _queue.Start(
                ProcessRequestAsync);
        }

        public bool IsSpeechPlaying =>
            Volatile.Read(
                ref _isSpeechPlaying);

        public bool IsDucked =>
            Volatile.Read(
                ref _isDucked);

        public Task PlaySpeechAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                text);

            return EnqueueAsync(
                new AudioRequest(
                    AudioRequestType.Speech,
                    text),
                cancellationToken);
        }

        public Task PlaySoundAsync(
            string sound,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                sound);

            return EnqueueAsync(
                new AudioRequest(
                    AudioRequestType.Sound,
                    sound),
                cancellationToken);
        }

        public Task PlayAudioAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                path);

            return EnqueueAsync(
                new AudioRequest(
                    AudioRequestType.AudioFile,
                    path),
                cancellationToken);
        }

        public Task DuckAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsDucked)
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug(
                "Audio duck requested.");

            Volatile.Write(
                ref _isDucked,
                true);

            // Actual mixer/volume implementation belongs here.

            return Task.CompletedTask;
        }

        public Task UnduckAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDucked)
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug(
                "Audio unduck requested.");

            Volatile.Write(
                ref _isDucked,
                false);

            // Actual mixer/volume implementation belongs here.

            return Task.CompletedTask;
        }

        public Task StopSpeechAsync(
            CancellationToken cancellationToken = default)
        {
            /*
             * StopSpeech is now equivalent to the AudioManager
             * cancellation barrier.
             */
            return CancelAsync(
                cancellationToken);
        }

        public Task ClearQueueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * Clear only pending AudioManager requests.
             *
             * The request currently being processed is left alone.
             */
            _queue.ClearPending();

            return Task.CompletedTask;
        }

        public async Task CancelAsync(
            CancellationToken cancellationToken = default)
        {
            /*
             * AsyncWorkQueue.CancelAsync() establishes the cancellation
             * barrier and does not allow cancellation itself to be
             * interrupted.
             *
             * The caller's token only controls how long this caller waits.
             */
            await _queue.CancelAsync(
                cancellationToken);

            /*
             * At this point:
             *
             * - new AudioManager requests are rejected
             * - pending AudioManager requests are gone
             * - Piper processing has completed
             * - the active AudioManager request has completed
             *
             * Now stop and clear PipeWire.
             *
             * Cancellation cleanup must not itself be cancellable.
             */
            await _pipeWireAudioClient.StopPlaybackAsync(
                CancellationToken.None);

            await _pipeWireAudioClient.ClearQueueAsync(
                CancellationToken.None);

            Volatile.Write(
                ref _isSpeechPlaying,
                false);

            /*
             * The queue remains Blocked until explicitly resumed.
             *
             * This is important because it prevents a new request from
             * entering between PipeWire cleanup and Resume().
             */
            _queue.Resume();

            _logger.LogDebug(
                "Audio cancellation completed.");
        }

        public async Task WaitForCompletionAsync(
            CancellationToken cancellationToken = default)
        {
            /*
             * First wait for AudioManager to finish producing audio.
             */
            await _queue.WaitForIdleAsync(
                cancellationToken);

            /*
             * AudioManager may have finished generating audio while
             * PipeWire is still playing queued PCM/WAV data.
             */
            await _pipeWireAudioClient.WaitForPlaybackCompleteAsync(
                cancellationToken);

            /*
             * There is a small race window: another request can arrive
             * after the first idle check and before PipeWire reports
             * completion.
             *
             * Repeat until both layers are idle.
             */
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _queue.WaitForIdleAsync(
                    cancellationToken);

                await _pipeWireAudioClient.WaitForPlaybackCompleteAsync(
                    cancellationToken);

                if (_queue.IsIdle &&
                    !_pipeWireAudioClient.IsPlaying &&
                    _pipeWireAudioClient.QueuedFrameCount == 0)
                {
                    return;
                }
            }
        }

        private async Task EnqueueAsync(
            AudioRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_queue.TryEnqueue(
                    request))
            {
                /*
                 * The queue is either cancelling, blocked, or stopped.
                 *
                 * This is intentionally not treated as an error. In the
                 * radio system this normally means a barge-in won the race
                 * against a response being queued.
                 */
                _logger.LogDebug(
                    "Audio request rejected because the audio queue is not accepting work.");

                return;
            }

            await Task.CompletedTask;
        }

        private async Task ProcessRequestAsync(
            AudioRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                switch (request.Type)
                {
                    case AudioRequestType.Speech:

                        Volatile.Write(
                            ref _isSpeechPlaying,
                            true);

                        try
                        {
                            await ProcessSpeechAsync(
                                request.Value,
                                cancellationToken);
                        }
                        finally
                        {
                            Volatile.Write(
                                ref _isSpeechPlaying,
                                false);
                        }

                        break;

                    case AudioRequestType.Sound:

                        await ProcessSoundAsync(
                            request.Value,
                            cancellationToken);

                        break;

                    case AudioRequestType.AudioFile:

                        await ProcessAudioFileAsync(
                            request.Value,
                            cancellationToken);

                        break;

                    default:

                        throw new InvalidOperationException(
                            $"Unsupported audio request type: " +
                            $"{request.Type}");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                /*
                 * Cancellation is expected during barge-in.
                 *
                 * Do not propagate it out of the queue worker.
                 */
                _logger.LogDebug(
                    "Audio request cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Audio request failed.");
            }
        }

        private async Task ProcessSpeechAsync(
            string text,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                text);

            /*
             * Piper generation is cancellable.
             */
            var wavData =
                await _piperClient.GenerateWavAsync(
                    text,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (wavData.Length == 0)
            {
                _logger.LogWarning(
                    "Piper returned no audio data for speech text.");

                return;
            }

            /*
             * Queue the complete WAV into PipeWire.
             *
             * PipeWire maintains the playback order independently of
             * AudioManager.
             */
            await _pipeWireAudioClient.QueueWavAsync(
                wavData,
                cancellationToken);
        }

        private async Task ProcessSoundAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                tag);

            cancellationToken.ThrowIfCancellationRequested();

            var sound =
                _soundEffectManager.GetRandomSound(
                    tag);

            if (sound is null)
            {
                _logger.LogWarning(
                    "No sound effect found for tag '{Tag}'.",
                    tag);

                return;
            }

            await _pipeWireAudioClient.QueueWavAsync(
                sound.WavData,
                cancellationToken);
        }

        private async Task ProcessAudioFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(
                "Playing audio file: {Path}",
                path);

            /*
             * Audio-file playback implementation will be connected here.
             */

            await Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                /*
                 * Stop accepting AudioManager work and terminate the
                 * queue worker.
                 */
                await _queue.StopAsync(
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error stopping AudioManager queue.");
            }

            try
            {
                await _pipeWireAudioClient.StopPlaybackAsync(
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error stopping PipeWire playback.");
            }

            try
            {
                await _pipeWireAudioClient.ClearQueueAsync(
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error clearing PipeWire queue.");
            }

            await _queue.DisposeAsync();
        }

        private sealed record AudioRequest(
            AudioRequestType Type,
            string Value);

        private enum AudioRequestType
        {
            Speech,
            Sound,
            AudioFile
        }
    }
}
