using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Tts;

public interface IPiperClient
{
    Task<byte[]> GenerateWavAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public sealed class PiperClient : IPiperClient
{
    private static readonly Regex SentenceSeparator = new(
        @"(?<=[.!?])\s+",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly PiperAudioCache _cache;
    private readonly ILogger<PiperClient> _logger;
    private readonly string _endpoint;
    private readonly string _voice;

    public PiperClient(
        IConfiguration configuration,
        HttpClient httpClient,
        PiperAudioCache cache,
        ILogger<PiperClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;

        _endpoint = configuration["Piper:Endpoint"]
            ?? throw new InvalidOperationException("Piper endpoint is not configured.");

        _voice = configuration["Piper:Voice"]
            ?? throw new InvalidOperationException("Piper voice is not configured.");
    }

    public async Task<byte[]> GenerateWavAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var sentences = SplitSentences(text);
        var audio = new List<byte[]>(sentences.Length);

        foreach (var sentence in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            audio.Add(await GenerateSentenceAsync(sentence, cancellationToken));
        }

        return audio.Count == 1
            ? audio[0]
            : CombineWavFiles(audio);
    }

    private async Task<byte[]> GenerateSentenceAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{_voice}:{text}";
        var cacheId = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(cacheKey)));

        var cached = await _cache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            _logger.LogDebug(
                "Piper cache hit. CacheId={CacheId}, Bytes={Bytes}",
                cacheId,
                cached.Length);

            return cached;
        }

        try
        {
            var start = Stopwatch.GetTimestamp();
            var wav = await GenerateFromPiperAsync(text, cancellationToken);

            _logger.LogInformation(
                "Piper synthesis completed. CacheId={CacheId}, Bytes={Bytes}, DurationMs={DurationMs}",
                cacheId,
                wav.Length,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            await _cache.SetAsync(cacheKey, wav, cancellationToken);
            return wav;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Piper synthesis cancelled. CacheId={CacheId}",
                cacheId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Piper synthesis failed. CacheId={CacheId}",
                cacheId);
            throw;
        }
    }

    private async Task<byte[]> GenerateFromPiperAsync(
        string text,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _endpoint)
        {
            Content = JsonContent.Create(new
            {
                text,
                voice = _voice
            })
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(
            cancellationToken);
    }

    private static string[] SplitSentences(string text) =>
        SentenceSeparator
            .Split(text.Trim())
            .Where(static sentence => !string.IsNullOrWhiteSpace(sentence))
            .Select(static sentence => sentence.Trim())
            .ToArray();

    private static byte[] CombineWavFiles(IReadOnlyList<byte[]> wavFiles)
    {
        if (wavFiles.Count == 0)
            throw new ArgumentException("At least one WAV file is required.", nameof(wavFiles));

        var first = ParseWav(wavFiles[0]);
        var dataLength = first.Data.Length;

        for (var i = 1; i < wavFiles.Count; i++)
        {
            var current = ParseWav(wavFiles[i]);

            if (!first.Format.SequenceEqual(current.Format))
            {
                throw new InvalidOperationException(
                    "Cannot combine Piper WAV files with different formats.");
            }

            dataLength += current.Data.Length;
        }

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(first.Format.Length);
        writer.Write(first.Format);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        writer.Write(first.Data);
        for (var i = 1; i < wavFiles.Count; i++)
            writer.Write(ParseWav(wavFiles[i]).Data);

        writer.Flush();
        return stream.ToArray();
    }

    private static (byte[] Format, byte[] Data) ParseWav(byte[] wav)
    {
        if (wav.Length < 12 ||
            !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Piper returned invalid WAV data.");
        }

        byte[]? format = null;
        byte[]? data = null;
        var offset = 12;

        while (offset + 8 <= wav.Length)
        {
            var chunkId = wav.AsSpan(offset, 4);
            var chunkLength = BitConverter.ToInt32(wav, offset + 4);
            offset += 8;

            if (chunkLength < 0 || offset + chunkLength > wav.Length)
                throw new InvalidDataException("Piper returned malformed WAV data.");

            if (chunkId.SequenceEqual("fmt "u8))
                format = wav.AsSpan(offset, chunkLength).ToArray();
            else if (chunkId.SequenceEqual("data"u8))
                data = wav.AsSpan(offset, chunkLength).ToArray();

            offset += chunkLength + (chunkLength & 1);
        }

        if (format is null || data is null)
            throw new InvalidDataException("Piper returned a WAV without format or audio data.");

        return (format, data);
    }
}
