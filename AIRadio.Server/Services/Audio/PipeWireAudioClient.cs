using AIRadio.Server.Models.PipeWire;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AIRadio.Server.Services.Audio;

public interface IPipeWireAudioClient : IAsyncDisposable
{
    bool IsInitialized { get; }
    ulong QueuedFrameCount { get; }
    ulong OutstandingFrameCount { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task QueueWavAsync(
        ReadOnlyMemory<byte> wavData,
        CancellationToken cancellationToken = default);
    Task QueuePcmAsync(
        ReadOnlyMemory<byte> pcmData,
        CancellationToken cancellationToken = default);
    Task QueuePcmBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> pcmSegments,
        CancellationToken cancellationToken = default);
    Task EndUtteranceAsync(
        bool cancel = false,
        CancellationToken cancellationToken = default);
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
    private readonly int _sampleRate;
    private readonly short _channels;
    private readonly short _bitsPerSample;
    private bool _initialized;
    private int _masterVolume = 100;

    public PipeWireAudioClient(
        IPipeWireNativeClient pipeWire,
        IConfiguration configuration,
        ILogger<PipeWireAudioClient> logger)
    {
        _pipeWire = pipeWire;
        _logger = logger;
        _sampleRate = configuration.GetValue("AudioFormat:SampleRate", 48000);
        _channels = configuration.GetValue<short>("AudioFormat:Channels", 1);
        _bitsPerSample = configuration.GetValue<short>("AudioFormat:BitsPerSample", 16);
    }

    public bool IsInitialized => _initialized;

    public ulong QueuedFrameCount => _pipeWire.QueuedFrameCount;

    public ulong OutstandingFrameCount => _pipeWire.OutstandingFrameCount;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _pipeWire.InitializeAsync(
            _sampleRate,
            _channels,
            _bitsPerSample,
            cancellationToken);

        _initialized = true;

        _logger.LogInformation(
            "PipeWire audio initialized: {Rate}Hz {Channels}ch {Bits}bit",
            _sampleRate,
            _channels,
            _bitsPerSample);
    }

    public Task QueueWavAsync(
        ReadOnlyMemory<byte> wavData,
        CancellationToken cancellationToken = default)
    {
        var pcm = WaveParser.Parse(
            wavData,
            _sampleRate,
            _channels,
            _bitsPerSample);

        return QueuePcmAsync(pcm, cancellationToken);
    }

    public Task QueuePcmAsync(
        ReadOnlyMemory<byte> pcmData,
        CancellationToken cancellationToken = default) =>
        _pipeWire.EnqueueAsync(pcmData, cancellationToken);

    public Task QueuePcmBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> pcmSegments,
        CancellationToken cancellationToken = default) =>
        _pipeWire.EnqueueAsync(pcmSegments, cancellationToken);

    public Task EndUtteranceAsync(
        bool cancel = false,
        CancellationToken cancellationToken = default) =>
        _pipeWire.EndUtteranceAsync(cancel, cancellationToken);

    public Task WaitForPlaybackCompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (QueuedFrameCount == 0 && OutstandingFrameCount == 0)
            return Task.CompletedTask;

        return WaitForNativeCompletionAsync(cancellationToken);
    }

    private async Task WaitForNativeCompletionAsync(
        CancellationToken cancellationToken)
    {
        // The native client owns the completion event. If a producer races
        // with this call, the native callback will complete the wait.
        await _pipeWire.WaitForPlaybackCompleteAsync(cancellationToken);
    }

    public Task ClearQueueAsync(
        CancellationToken cancellationToken = default) =>
        _pipeWire.ClearAsync(cancellationToken);

    public Task StopPlaybackAsync(
        CancellationToken cancellationToken = default) =>
        _pipeWire.ClearAsync(cancellationToken);

    public Task SetMasterVolumeAsync(
        int volume,
        CancellationToken cancellationToken = default)
    {
        _masterVolume = Math.Clamp(volume, 0, 100);
        return _pipeWire.SetVolumeAsync(
            _masterVolume,
            cancellationToken);
    }

    public Task<int> GetMasterVolumeAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_masterVolume);

    public ValueTask DisposeAsync() =>
        _pipeWire.DisposeAsync();

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

            var format = ReadFormatChunk(
                span,
                out var dataOffset,
                out var dataLength);

            ValidateFormat(
                format,
                expectedSampleRate,
                expectedChannels,
                expectedBitsPerSample);

            if (dataOffset + dataLength > span.Length)
                throw new InvalidDataException(
                    "WAV data chunk exceeds buffer length.");

            return wavData.Slice(dataOffset, dataLength);
        }

        private static void ValidateRiffHeader(ReadOnlySpan<byte> data)
        {
            if (Encoding.ASCII.GetString(data[..4]) != "RIFF" ||
                Encoding.ASCII.GetString(data.Slice(8, 4)) != "WAVE")
            {
                throw new InvalidDataException("Invalid WAV header.");
            }
        }

        private static WaveFormat ReadFormatChunk(
            ReadOnlySpan<byte> data,
            out int dataOffset,
            out int dataLength)
        {
            var position = 12;
            WaveFormat? format = null;
            dataOffset = 0;
            dataLength = 0;

            while (position + 8 <= data.Length)
            {
                var chunkId =
                    Encoding.ASCII.GetString(data.Slice(position, 4));

                var chunkSize =
                    BinaryPrimitives.ReadInt32LittleEndian(
                        data.Slice(position + 4, 4));

                position += 8;

                if (chunkSize < 0 ||
                    position + chunkSize > data.Length)
                {
                    throw new InvalidDataException(
                        "Invalid WAV chunk size.");
                }

                switch (chunkId)
                {
                    case "fmt ":
                        format = ParseFormatChunk(
                            data.Slice(position, chunkSize));
                        break;

                    case "data":
                        dataOffset = position;
                        dataLength = chunkSize;
                        break;
                }

                position += chunkSize;

                if (format is not null && dataLength > 0)
                    break;
            }

            if (format is null)
                throw new InvalidDataException(
                    "WAV fmt chunk not found.");

            if (dataLength == 0)
                throw new InvalidDataException(
                    "WAV data chunk not found.");

            return format;
        }

        private static WaveFormat ParseFormatChunk(
            ReadOnlySpan<byte> data)
        {
            if (data.Length < 16)
                throw new InvalidDataException(
                    "Invalid fmt chunk.");

            return new WaveFormat
            {
                AudioFormat =
                    BinaryPrimitives.ReadInt16LittleEndian(data[..2]),

                Channels =
                    BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2, 2)),

                SampleRate =
                    BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)),

                BitsPerSample =
                    BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2))
            };
        }

        private static void ValidateFormat(
            WaveFormat format,
            int expectedSampleRate,
            short expectedChannels,
            short expectedBitsPerSample)
        {
            if (format.AudioFormat != PcmFormat)
                throw new NotSupportedException(
                    "Only PCM WAV files are supported.");

            if (format.SampleRate != expectedSampleRate)
                throw new NotSupportedException(
                    $"Sample rate {format.SampleRate}Hz does not match expected {expectedSampleRate}Hz.");

            if (format.Channels != expectedChannels)
                throw new NotSupportedException(
                    $"Channel count {format.Channels} does not match expected {expectedChannels}.");

            if (format.BitsPerSample != expectedBitsPerSample)
                throw new NotSupportedException(
                    $"Bit depth {format.BitsPerSample} does not match expected {expectedBitsPerSample}.");
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
