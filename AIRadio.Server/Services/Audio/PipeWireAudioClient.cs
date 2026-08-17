using AIRadio.Server.Models.PipeWire;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;

namespace AIRadio.Server.Services.Audio
{
    public interface IPipeWireAudioClient : IAsyncDisposable
    {
        bool IsInitialized { get; }
        int QueuedFrameCount { get; }
        long OutstandingFrameCount { get; }
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task QueueWavAsync(ReadOnlyMemory<byte> wavData, CancellationToken cancellationToken = default);
        Task QueuePcmAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default);
        Task WaitForPlaybackCompleteAsync(CancellationToken cancellationToken = default);
        Task ClearQueueAsync(CancellationToken cancellationToken = default);
        Task StopPlaybackAsync(CancellationToken cancellationToken = default);
        Task SetMasterVolumeAsync(int volume, CancellationToken cancellationToken = default);
        Task<int> GetMasterVolumeAsync(CancellationToken cancellationToken = default);
    }

    public sealed class PipeWireAudioClient : IPipeWireAudioClient
    {
        private readonly ILogger<PipeWireAudioClient> _logger;
        private readonly IPipeWireNativeClient _pipeWire;
        private readonly Channel<AudioFrame> _audioQueue;
        private readonly int _sampleRate;
        private readonly short _channels;
        private readonly short _bitsPerSample;
        private readonly int _frameDurationMs;
        private readonly int _queueCapacity;
        private readonly object _flushLock = new();
        private bool _initialized;
        private bool _flushing;
        private int _queuedFrames;
        private long _outstandingFrames;
        private int _masterVolume = 100;
        private TaskCompletionSource<object?>? _flushedCompletion;
        private TaskCompletionSource<object?>? _playbackCompletion;

        public PipeWireAudioClient(IPipeWireNativeClient pipeWire, IConfiguration configuration, ILogger<PipeWireAudioClient> logger)
        {
            _pipeWire = pipeWire;
            _logger = logger;
            _sampleRate = configuration.GetValue("AudioFormat:SampleRate", 22050);
            _channels = configuration.GetValue<short>("AudioFormat:Channels", 1);
            _bitsPerSample = configuration.GetValue<short>("AudioFormat:BitsPerSample", 16);
            _frameDurationMs = configuration.GetValue("AudioFormat:FrameMilliseconds", 20);
            _queueCapacity = Math.Max(1, configuration.GetValue("AudioFormat:QueueFrames", 32));

            _audioQueue = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(_queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public bool IsInitialized => _initialized;
        public int QueuedFrameCount => Math.Max(0, Volatile.Read(ref _queuedFrames));
        public long OutstandingFrameCount => Math.Max(0, Volatile.Read(ref _outstandingFrames));

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized) return;

            await _pipeWire.InitializeAsync(_sampleRate, _channels, _bitsPerSample, cancellationToken);
            _pipeWire.BufferRequested += OnPipeWireBufferRequested;
            _pipeWire.BufferCompleted += OnPipeWireBufferCompleted;
            _pipeWire.Drained += OnPipeWireDrained;
            _initialized = true;

            _logger.LogInformation(
                "PipeWire audio initialized: {Rate}Hz {Channels}ch {Bits}bit frame {Frame}ms queue {QueueFrames} frames ({QueueMs}ms)",
                _sampleRate, _channels, _bitsPerSample, _frameDurationMs, _queueCapacity, _queueCapacity * _frameDurationMs);
        }

        public Task QueueWavAsync(ReadOnlyMemory<byte> wavData, CancellationToken cancellationToken = default) =>
            QueuePcmAsync(WaveParser.Parse(wavData, _sampleRate, _channels, _bitsPerSample), cancellationToken);

        public async Task QueuePcmAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default)
        {
            var frameSize = CalculateFrameSize();

            for (var offset = 0; offset < pcmData.Length; offset += frameSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_flushLock)
                {
                    if (_flushing)
                        throw new OperationCanceledException("PipeWire playback is being flushed.", cancellationToken);
                }

                var length = Math.Min(frameSize, pcmData.Length - offset);
                Interlocked.Increment(ref _queuedFrames);
                try
                {
                    await _audioQueue.Writer.WriteAsync(
                        new AudioFrame(pcmData.Slice(offset, length)),
                        cancellationToken);
                }
                catch
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    SignalFlushedIfEmpty();
                    throw;
                }
            }
        }

        public async Task WaitForPlaybackCompleteAsync(CancellationToken cancellationToken = default)
        {
            Task completion;

            lock (_flushLock)
            {
                if (IsEmptyUnsafe())
                    return;

                _playbackCompletion ??= CreateCompletionSource();
                completion = _playbackCompletion.Task;
            }

            await completion.WaitAsync(cancellationToken);
        }

        public async Task ClearQueueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task completion;
            lock (_flushLock)
            {
                _flushing = true;
                _flushedCompletion ??= CreateCompletionSource();
                completion = _flushedCompletion.Task;
            }

            SignalFlushedIfEmpty();
            await _pipeWire.FlushAsync(drain: false, cancellationToken);

            // The PipeWire process callback is the sole reader. It discards
            // queued WAV frames while _flushing is set. The completion is
            // signaled only after both application and native buffers are empty.
            await completion.WaitAsync(cancellationToken);

            lock (_flushLock)
            {
                _flushing = false;
            }
        }

        public async Task StopPlaybackAsync(CancellationToken cancellationToken = default) =>
            await ClearQueueAsync(cancellationToken);

        private void OnPipeWireBufferRequested(object? sender, PipeWireBufferEventArgs e)
        {
            try
            {
                while (_audioQueue.Reader.TryRead(out var frame))
                {
                    Interlocked.Decrement(ref _queuedFrames);

                    if (Volatile.Read(ref _flushing))
                    {
                        SignalFlushedIfEmpty();
                        continue;
                    }

                    e.CopyFrom(frame.Data);
                    return;
                }

                e.FillSilence();
                SignalFlushedIfEmpty();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to provide PipeWire audio buffer.");
                e.FillSilence();
                SignalFlushedIfEmpty();
            }
        }

        private void OnPipeWireBufferCompleted(object? sender, PipeWireBufferCompletedEventArgs e)
        {
            if (e.Frames > 0)
                Interlocked.Add(ref _outstandingFrames, -e.Frames);

            SignalFlushedIfEmpty();
        }

        private void OnPipeWireDrained(object? sender, EventArgs e) =>
            SignalFlushedIfEmpty();

        private void SignalFlushedIfEmpty()
        {
            TaskCompletionSource<object?>? flushCompletion = null;
            TaskCompletionSource<object?>? playbackCompletion = null;

            lock (_flushLock)
            {
                if (!IsEmptyUnsafe())
                    return;

                if (_flushing)
                {
                    flushCompletion = _flushedCompletion;
                    _flushedCompletion = null;
                }
                else
                {
                    playbackCompletion = _playbackCompletion;
                    _playbackCompletion = null;
                }
            }

            flushCompletion?.TrySetResult(null);
            playbackCompletion?.TrySetResult(null);
        }

        private bool IsEmptyUnsafe() =>
            Volatile.Read(ref _queuedFrames) == 0 &&
            Volatile.Read(ref _outstandingFrames) == 0;

        private static TaskCompletionSource<object?> CreateCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SetMasterVolumeAsync(int volume, CancellationToken cancellationToken = default)
        {
            _masterVolume = Math.Clamp(volume, 0, 100);
            return _pipeWire.SetVolumeAsync(_masterVolume, cancellationToken);
        }

        public Task<int> GetMasterVolumeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_masterVolume);

        private int CalculateFrameSize() =>
            _sampleRate * _channels * (_bitsPerSample / 8) * _frameDurationMs / 1000;

        private int BytesPerFrame() => _channels * (_bitsPerSample / 8);

        public async ValueTask DisposeAsync()
        {
            _pipeWire.BufferRequested -= OnPipeWireBufferRequested;
            _pipeWire.BufferCompleted -= OnPipeWireBufferCompleted;
            _pipeWire.Drained -= OnPipeWireDrained;

            lock (_flushLock)
            {
                _flushing = false;
                var flushCompletion = _flushedCompletion;
                var playbackCompletion = _playbackCompletion;
                _flushedCompletion = null;
                _playbackCompletion = null;
                flushCompletion?.TrySetCanceled();
                playbackCompletion?.TrySetCanceled();
            }

            await _pipeWire.DisposeAsync();
            _logger.LogInformation("PipeWire client disposed.");
        }
    }

    internal sealed class AudioFrame
    {
        public AudioFrame(ReadOnlyMemory<byte> data) => Data = data;
        public ReadOnlyMemory<byte> Data { get; }
    }

    internal static class WaveParser
    {
        private const short PcmFormat = 1;

        public static ReadOnlyMemory<byte> Parse(
            ReadOnlyMemory<byte> wavData,
            int expectedSampleRate,
            short expectedChannels,
            short expectedBitsPerSample)
        {
            if (wavData.Length < 44)
                throw new InvalidDataException("WAV data is too small.");

            var span = wavData.Span;
            ValidateRiffHeader(span);
            var format = ReadFormatChunk(span, out var dataOffset, out var dataLength);
            ValidateFormat(format, expectedSampleRate, expectedChannels, expectedBitsPerSample);

            if (dataOffset + dataLength > span.Length)
                throw new InvalidDataException("WAV data chunk exceeds buffer length.");

            return wavData.Slice(dataOffset, dataLength);
        }

        private static void ValidateRiffHeader(ReadOnlySpan<byte> data)
        {
            if (Encoding.ASCII.GetString(data[..4]) != "RIFF" ||
                Encoding.ASCII.GetString(data.Slice(8, 4)) != "WAVE")
                throw new InvalidDataException("Invalid WAV header.");
        }

        private static WaveFormat ReadFormatChunk(ReadOnlySpan<byte> data, out int dataOffset, out int dataLength)
        {
            var position = 12;
            WaveFormat? format = null;
            dataOffset = 0;
            dataLength = 0;

            while (position + 8 <= data.Length)
            {
                var chunkId = Encoding.ASCII.GetString(data.Slice(position, 4));
                var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position + 4, 4));
                position += 8;

                if (chunkSize < 0 || position + chunkSize > data.Length)
                    throw new InvalidDataException("Invalid WAV chunk size.");

                switch (chunkId)
                {
                    case "fmt ": format = ParseFormatChunk(data.Slice(position, chunkSize)); break;
                    case "data": dataOffset = position; dataLength = chunkSize; break;
                }

                position += chunkSize;
                if (format is not null && dataLength > 0)
                    break;
            }

            if (format is null) throw new InvalidDataException("WAV fmt chunk not found.");
            if (dataLength == 0) throw new InvalidDataException("WAV data chunk not found.");
            return format;
        }

        private static WaveFormat ParseFormatChunk(ReadOnlySpan<byte> data)
        {
            if (data.Length < 16) throw new InvalidDataException("Invalid fmt chunk.");
            return new WaveFormat
            {
                AudioFormat = BinaryPrimitives.ReadInt16LittleEndian(data[..2]),
                Channels = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2, 2)),
                SampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)),
                BitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2))
            };
        }

        private static void ValidateFormat(WaveFormat format, int expectedSampleRate, short expectedChannels, short expectedBitsPerSample)
        {
            if (format.AudioFormat != PcmFormat) throw new NotSupportedException("Only PCM WAV files are supported.");
            if (format.SampleRate != expectedSampleRate) throw new NotSupportedException($"Sample rate {format.SampleRate}Hz does not match expected {expectedSampleRate}Hz.");
            if (format.Channels != expectedChannels) throw new NotSupportedException($"Channel count {format.Channels} does not match expected {expectedChannels}.");
            if (format.BitsPerSample != expectedBitsPerSample) throw new NotSupportedException($"Bit depth {format.BitsPerSample} does not match expected {expectedBitsPerSample}.");
        }

        private sealed class WaveFormat
        {
            public short AudioFormat { get; init; }
            public short Channels { get; init; }
            public int SampleRate { get; init; }
            public short BitsPerSample { get; init; }
        }
    }
}
