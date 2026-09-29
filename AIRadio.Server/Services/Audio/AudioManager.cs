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
        bool HasPendingPlayback { get; }
        event EventHandler? PlaybackCompleted;
        Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default);
        Task QueueSpeechAsync(string text, CancellationToken cancellationToken = default);
        Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default);
        Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default);
        Task PrepareStationChangeAsync(CancellationToken cancellationToken = default);
        Task DuckAsync(CancellationToken cancellationToken = default);
        Task UnduckAsync(CancellationToken cancellationToken = default);
        Task StopSpeechAsync(CancellationToken cancellationToken = default);
        Task ClearQueueAsync(CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
    }

    public sealed class AudioManager : IAudioManager, IAsyncDisposable
    {
        private static readonly Regex AudioTagRegex = new(
            @"{sound-loop:(?<loop>[A-Za-z0-9_.-]+)}|{sound:(?<sound>[A-Za-z0-9_.-]+)}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger<AudioManager> _logger;
        private readonly ISoundEffectManager _soundEffectManager;
        private readonly IPipeWireAudioClient _pipeWireAudioClient;
        private readonly IPiperClient _piperClient;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvClient _mpvClient;
        private readonly int _duckVolume;
        private readonly AsyncWorkQueue<AudioRequest> _queue;
        private readonly object _soundLoopSync = new();
        private CancellationTokenSource? _soundLoopCts;
        private Task? _soundLoopTask;
        private int _pendingSpeechRequests;
        private bool _soundLoopActive;
        private bool _soundLoopPlaybackActive;
        private bool _isDucked;
        private bool _hasPendingPlayback;
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
            _pipeWireAudioClient.PlaybackCompleted += OnPlaybackCompleted;
        }

        public bool IsDucked => Volatile.Read(ref _isDucked);

        // Conversation completion must wait for both queued audio requests and
        // audio already submitted to PipeWire. _hasPendingPlayback remains set
        // until PipeWire reports that the current playback run has completed.
        public bool HasPendingPlayback =>
            !_queue.IsIdle ||
            Volatile.Read(ref _hasPendingPlayback);

        public event EventHandler? PlaybackCompleted;

        public async Task PlaySpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            var position = 0;
            foreach (Match match in AudioTagRegex.Matches(text))
            {
                await EnqueueSpeechAsync(text[position..match.Index], cancellationToken);
                if (match.Groups["loop"].Success)
                    StartSoundLoop(match.Groups["loop"].Value);
                else
                    EnqueueSound(match.Groups["sound"].Value, cancellationToken);
                position = match.Index + match.Length;
            }

            await EnqueueSpeechAsync(text[position..], cancellationToken);
        }

        public async Task QueueSpeechAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            var position = 0;
            foreach (Match match in AudioTagRegex.Matches(text))
            {
                await EnqueueSpeechAsync(text[position..match.Index], cancellationToken, waitForPlayback: false);
                if (match.Groups["loop"].Success)
                    StartSoundLoop(match.Groups["loop"].Value);
                else
                    EnqueueSound(match.Groups["sound"].Value, cancellationToken);
                position = match.Index + match.Length;
            }

            await EnqueueSpeechAsync(text[position..], cancellationToken, waitForPlayback: false);
        }

        public Task PlaySoundAsync(string sound, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sound);
            return EnqueueAsync(new(AudioRequestType.Sound, sound, null), cancellationToken);
        }

        public Task PlaySoundLoopAsync(string sound, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sound);
            cancellationToken.ThrowIfCancellationRequested();
            StartSoundLoop(sound);
            return Task.CompletedTask;
        }

        public Task PlayStationAsync(RadioStation station, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(station);
            return EnqueueAsync(new(AudioRequestType.MpvPlayStation, null, station), cancellationToken);
        }

        public Task PrepareStationChangeAsync(CancellationToken cancellationToken = default) => EnqueueAsync(new(AudioRequestType.MpvPrepareStationChange, null, null), cancellationToken);
        public Task DuckAsync(CancellationToken cancellationToken = default) => EnqueueAsync(new(AudioRequestType.MpvDuck, null, null), cancellationToken);
        public Task UnduckAsync(CancellationToken cancellationToken = default) => EnqueueAsync(new(AudioRequestType.MpvUnduck, null, null), cancellationToken);
        public Task StopSpeechAsync(CancellationToken cancellationToken = default) => CancelAsync(cancellationToken);

        public Task ClearQueueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _queue.ClearPending();
            return Task.CompletedTask;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopSoundLoopAsync(CancellationToken.None);
            await _queue.CancelAsync(CancellationToken.None);
            await _pipeWireAudioClient.EndUtteranceAsync(cancel: true, CancellationToken.None);
            Volatile.Write(ref _hasPendingPlayback, false);

            if (IsDucked)
            {
                try { await UnduckMpvAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Unable to restore MPV volume after audio cancellation."); }
            }
            _queue.Resume();
        }

        private async Task EnqueueSpeechAsync(string text, CancellationToken cancellationToken, bool waitForPlayback = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            await StopSoundLoopAsync(cancellationToken);

            foreach (var sentence in SentenceParser.Split(text))
            {
                Interlocked.Increment(ref _pendingSpeechRequests);
                Volatile.Write(ref _hasPendingPlayback, true);
                _logger.LogInformation("Queue speech: " + sentence);
                cancellationToken.ThrowIfCancellationRequested();
                EnqueueAsync(new(AudioRequestType.Speech, sentence, null, waitForPlayback), cancellationToken);
            }
        }

        private void EnqueueSound(string tag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _hasPendingPlayback, true);
            EnqueueAsync(new(AudioRequestType.Sound, tag, null), cancellationToken);
        }

        private Task EnqueueAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queue.TryEnqueue(request))
            {
                if (request.Type == AudioRequestType.Speech)
                    Interlocked.Decrement(ref _pendingSpeechRequests);
                _logger.LogDebug("Audio request rejected because the audio queue is not accepting work.");
                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        private async Task ProcessRequestAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            if (request.Type == AudioRequestType.Speech)
                Interlocked.Decrement(ref _pendingSpeechRequests);

            try
            {
                switch (request.Type)
                {
                    case AudioRequestType.Speech:
                        await ProcessSpeechAsync(request, cancellationToken);
                        break;
                    case AudioRequestType.Sound:
                        await ProcessSoundAsync(request.Value!, cancellationToken);
                        break;
                    case AudioRequestType.MpvPrepareStationChange:
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

        private async Task ProcessSpeechAsync(AudioRequest request, CancellationToken cancellationToken)
        {
            var wavData = await _piperClient.GenerateWavAsync(request.Value!, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (wavData.Length == 0)
            {
                _logger.LogWarning("Piper returned no audio data for speech text.");
                return;
            }

            await DuckMpvAsync(cancellationToken);

            await _pipeWireAudioClient.QueueWavAsync(wavData, cancellationToken);
            // QueueWavAsync only queues the samples. Playback completion is
            // reported by the PipeWire completion callback.
        }

        private void StartSoundLoop(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);

            lock (_soundLoopSync)
            {
                _soundLoopCts?.Cancel();
                _soundLoopCts = new CancellationTokenSource();
                _soundLoopActive = true;
                _soundLoopTask = RunSoundLoopAsync(tag, _soundLoopCts);
            }

            _logger.LogInformation("Started sound loop {Tag}.", tag);
        }

        private async Task StopSoundLoopAsync(CancellationToken cancellationToken)
        {
            Task? loopTask = null;
            var cancelPlayback = false;

            lock (_soundLoopSync)
            {
                if (_soundLoopActive)
                    _soundLoopActive = false;

                cancelPlayback = _soundLoopPlaybackActive;
                loopTask = _soundLoopTask;
                _soundLoopCts?.Cancel();
            }

            if (cancelPlayback)
            {
                _logger.LogDebug("Cancelling active sound loop playback before queuing new audio.");
                await _pipeWireAudioClient.EndUtteranceAsync(
                    cancel: true,
                    cancellationToken);
            }

            if (loopTask is not null)
            {
                try
                {
                    await loopTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        private bool IsSoundLoopActive() =>
            Volatile.Read(ref _soundLoopActive);

        private async Task RunSoundLoopAsync(string tag, CancellationTokenSource loopCts)
        {
            var cancellationToken = loopCts.Token;
            var sound = _soundEffectManager.GetRandomLoopSound(tag);

            if (sound is null)
            {
                _logger.LogWarning("No sound loop found for tag '{Tag}'.", tag);
                return;
            }

            _logger.LogInformation(
                "Selected sound loop {Tag} ({FileName}) with {Bytes} bytes.",
                tag, sound.FileName, sound.WavData.Length);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _pendingSpeechRequests) > 0)
                        break;

                    if (!_queue.IsIdle)
                    {
                        if (Volatile.Read(ref _hasPendingPlayback))
                            await WaitForPlaybackCompletionAsync(cancellationToken);
                        else
                            await Task.Delay(50, cancellationToken);

                        continue;
                    }

                    if (Volatile.Read(ref _hasPendingPlayback))
                    {
                        await WaitForPlaybackCompletionAsync(cancellationToken);
                        continue;
                    }

                    if (!IsSoundLoopActive())
                        break;

                    Volatile.Write(ref _hasPendingPlayback, true);
                    lock (_soundLoopSync)
                        _soundLoopPlaybackActive = true;

                    _logger.LogInformation(
                        "Queue sound loop {Tag} ({FileName}) with {Bytes} bytes.",
                        tag, sound.FileName, sound.WavData.Length);

                    var completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler handler = (_, _) => completion.TrySetResult();

                    _pipeWireAudioClient.PlaybackCompleted += handler;
                    try
                    {
                        await _pipeWireAudioClient.QueueWavAsync(
                            sound.WavData,
                            cancellationToken);

                        await completion.Task.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        _pipeWireAudioClient.PlaybackCompleted -= handler;
                        lock (_soundLoopSync)
                            _soundLoopPlaybackActive = false;
                    }

                    if (!IsSoundLoopActive() || Volatile.Read(ref _pendingSpeechRequests) > 0)
                        break;

                    // Reuse the same WAV. The loop is a repeat of the selected
                    // sound, not a new random selection on each iteration.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Sound loop {Tag} cancelled.", tag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sound loop {Tag} failed.", tag);
            }
            finally
            {
                lock (_soundLoopSync)
                {
                    if (ReferenceEquals(_soundLoopCts, loopCts))
                    {
                        _soundLoopActive = false;
                        _soundLoopPlaybackActive = false;
                        _soundLoopTask = null;
                        _soundLoopCts = null;
                    }
                }

                loopCts.Dispose();
            }
        }

        private async Task WaitForPlaybackCompletionAsync(CancellationToken cancellationToken)
        {
            if (!Volatile.Read(ref _hasPendingPlayback))
                return;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (_, _) => completion.TrySetResult();

            _pipeWireAudioClient.PlaybackCompleted += handler;
            try
            {
                // Re-check after subscribing so a completion that raced with
                // the initial state check cannot leave the waiter blocked.
                if (Volatile.Read(ref _hasPendingPlayback))
                    await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                _pipeWireAudioClient.PlaybackCompleted -= handler;
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
                case AudioRequestType.MpvPrepareStationChange:
                    await PrepareStationChangeInternalAsync(cancellationToken);
                    break;
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

        private async Task PrepareStationChangeInternalAsync(CancellationToken cancellationToken)
        {
            if (_mpvClient.IsPlaying)
                await _mpvManager.StopAsync(cancellationToken);

            // A station change only mutes MPV while the new stream is selected.
            // Conversation duck/resume state owns the eventual volume restore.
            await _mpvManager.SetTemporaryVolumeAsync(0, cancellationToken);
            Volatile.Write(ref _isDucked, true);
            _logger.LogDebug("Temporarily muted MPV radio volume for station change; persistent radio volume was unchanged.");
        }

        private async Task DuckMpvAsync(CancellationToken cancellationToken)
        {
            if (IsDucked)
                return;

            var persistentVolume = _mpvManager.PersistentVolume;
            if (persistentVolume <= _duckVolume)
            {
                _logger.LogDebug(
                    "Skipping MPV duck because persistent radio volume {PersistentVolume} is already at or below duck volume {DuckVolume}.",
                    persistentVolume,
                    _duckVolume);
                return;
            }

            await _mpvManager.SetTemporaryVolumeAsync(_duckVolume, cancellationToken);
            Volatile.Write(ref _isDucked, true);
            _logger.LogDebug(
                "Ducked MPV radio volume to {DuckVolume}; persistent radio volume was unchanged.",
                _duckVolume);
        }

        private async Task UnduckMpvAsync(CancellationToken cancellationToken)
        {
            if (!IsDucked)
                return;

            await _mpvManager.RestoreVolumeAsync(cancellationToken);
            Volatile.Write(ref _isDucked, false);
            _logger.LogDebug("Restored MPV radio volume to the persistent radio volume.");
        }

        private void OnPlaybackCompleted(object? sender, EventArgs e)
        {
            Volatile.Write(ref _hasPendingPlayback, false);
            PlaybackCompleted?.Invoke(this, e);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            await StopSoundLoopAsync(CancellationToken.None);
            _pipeWireAudioClient.PlaybackCompleted -= OnPlaybackCompleted;
            _disposed = true;
            try { await _queue.StopAsync(CancellationToken.None); } catch (Exception ex) { _logger.LogDebug(ex, "Error stopping AudioManager queue."); }
            try { await _pipeWireAudioClient.StopPlaybackAsync(CancellationToken.None); } catch (Exception ex) { _logger.LogDebug(ex, "Error clearing PipeWire playback."); }
            await _queue.DisposeAsync();
        }

        private sealed record AudioRequest(AudioRequestType Type, string? Value, RadioStation? Station, bool WaitForPlayback = true);

        private enum AudioRequestType
        {
            Speech,
            Sound,
            MpvPrepareStationChange,
            MpvDuck,
            MpvUnduck,
            MpvPlayStation
        }
    }
}
