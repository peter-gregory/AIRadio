using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AIRadio.Server.Services.Tts;

/// <summary>
/// Converts PCM WAV audio to AIRadio's canonical PCM WAV format.
/// Uses a windowed-sinc low-pass filter so downsampling from Piper's
/// 22.05 kHz output does not introduce audible aliasing.
/// </summary>
internal static class PcmWavResampler
{
    private const short PcmFormat = 1;
    private const int FilterTaps = 32;

    public static byte[] Resample(
        ReadOnlySpan<byte> sourceWav,
        int targetSampleRate,
        short targetChannels,
        short targetBitsPerSample)
    {
        var source = Parse(sourceWav);

        if (source.AudioFormat != PcmFormat ||
            source.Channels != 1 ||
            source.BitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"Piper WAV must be PCM 16-bit mono; received format={source.AudioFormat}, channels={source.Channels}, bits={source.BitsPerSample}.");
        }

        if (targetChannels != 1 || targetBitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"AIRadio TTS output must be 16-bit mono; configured {targetChannels} channels, {targetBitsPerSample} bits.");
        }

        if (targetSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));

        if (source.SampleRate == targetSampleRate)
            return BuildWav(
                source.Pcm,
                targetSampleRate,
                targetChannels,
                targetBitsPerSample);

        var sourceSamples = MemoryMarshal.Cast<byte, short>(source.Pcm).ToArray();
        var outputLength = (int)Math.Ceiling(
            sourceSamples.Length * (double)targetSampleRate / source.SampleRate);

        var outputSamples = new short[outputLength];
        var ratio = (double)targetSampleRate / source.SampleRate;
        var halfTaps = FilterTaps / 2;
        var cutoff = Math.Min(1.0, ratio) * 0.5;

        for (var outputIndex = 0; outputIndex < outputSamples.Length; outputIndex++)
        {
            var center = outputIndex / ratio;
            var centerIndex = (int)Math.Floor(center);
            var first = Math.Max(0, centerIndex - halfTaps + 1);
            var last = Math.Min(
                sourceSamples.Length - 1,
                centerIndex + halfTaps);

            double sum = 0;
            double weightSum = 0;

            for (var inputIndex = first; inputIndex <= last; inputIndex++)
            {
                var distance = inputIndex - center;
                var x = ratio * distance;

                var sinc = Math.Abs(x) < 1e-12
                    ? 1.0
                    : Math.Sin(Math.PI * x) / (Math.PI * x);

                var windowPosition = distance / halfTaps;
                var window = Math.Abs(windowPosition) >= 1.0
                    ? 0.0
                    : 0.5 * (1.0 + Math.Cos(Math.PI * windowPosition));

                var weight = ratio * sinc * window;
                sum += sourceSamples[inputIndex] * weight;
                weightSum += weight;
            }

            var sample = weightSum == 0 ? 0 : sum / weightSum;
            outputSamples[outputIndex] = (short)Math.Clamp(
                Math.Round(sample),
                short.MinValue,
                short.MaxValue);
        }

        return BuildWav(
            MemoryMarshal.AsBytes(outputSamples.AsSpan()),
            targetSampleRate,
            targetChannels,
            targetBitsPerSample);
    }

    private static WaveData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 ||
            Encoding.ASCII.GetString(data[..4]) != "RIFF" ||
            Encoding.ASCII.GetString(data.Slice(8, 4)) != "WAVE")
        {
            throw new InvalidDataException("Invalid Piper WAV data.");
        }

        var position = 12;
        short format = 0;
        short channels = 0;
        int sampleRate = 0;
        short bits = 0;
        byte[]? pcm = null;

        while (position + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data.Slice(position, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(
                data.Slice(position + 4, 4));

            position += 8;

            if (size < 0 || position + size > data.Length)
                throw new InvalidDataException("Invalid WAV chunk size.");

            if (id == "fmt " && size >= 16)
            {
                var chunk = data.Slice(position, size);
                format = BinaryPrimitives.ReadInt16LittleEndian(chunk[..2]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(14, 2));
            }
            else if (id == "data")
            {
                pcm = data.Slice(position, size).ToArray();
            }

            position += size;

            if (format != 0 && pcm is not null)
                break;
        }

        if (format == 0 ||
            sampleRate <= 0 ||
            channels <= 0 ||
            bits <= 0 ||
            pcm is null ||
            pcm.Length == 0)
        {
            throw new InvalidDataException("Incomplete WAV data.");
        }

        return new WaveData(format, channels, sampleRate, bits, pcm);
    }

    private static byte[] BuildWav(
        ReadOnlySpan<byte> pcm,
        int sampleRate,
        short channels,
        short bitsPerSample)
    {
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var result = new byte[44 + pcm.Length];
        var span = result.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(4, 4),
            36 + pcm.Length);

        Encoding.ASCII.GetBytes("WAVE").CopyTo(span.Slice(8, 4));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), PcmFormat);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(span.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), pcm.Length);
        pcm.CopyTo(span.Slice(44));

        return result;
    }

    private readonly record struct WaveData(
        short AudioFormat,
        short Channels,
        int SampleRate,
        short BitsPerSample,
        byte[] Pcm);
}
