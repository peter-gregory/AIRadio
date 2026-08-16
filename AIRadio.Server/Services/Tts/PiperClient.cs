using System.Diagnostics;
using System.Text;

namespace AIRadio.Server.Services.Tts;

public interface IPiperClient
{
    Task<byte[]> GenerateWavAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public sealed class PiperClient : IPiperClient
{
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

        text = text.Trim();
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
}
