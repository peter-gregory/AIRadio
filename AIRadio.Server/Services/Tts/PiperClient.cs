using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;

namespace AIRadio.Server.Services.Tts
{
    public interface IPiperClient
    {
        Task<byte[]> GenerateWavAsync(
            string text,
            CancellationToken cancellationToken = default);
    }

    public sealed class PiperClient : IPiperClient
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<PiperClient> _logger;
        private readonly string _endpoint;
        private readonly string _voice;
        private readonly MemoryCacheEntryOptions _cacheOptions;

        public PiperClient(
            IConfiguration configuration,
            HttpClient httpClient,
            IMemoryCache cache,
            ILogger<PiperClient> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _logger = logger;

            _endpoint = configuration["Piper:Endpoint"]
                ?? throw new InvalidOperationException("Piper endpoint is not configured.");

            _voice = configuration["Piper:Voice"]
                ?? throw new InvalidOperationException("Piper voice is not configured.");

            var expirationMinutes = int.TryParse(
                configuration["Piper:CacheExpirationMinutes"],
                out var expiration)
                    ? expiration
                    : 60;

            _cacheOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(expirationMinutes))
                .SetSize(1);
        }

        public async Task<byte[]> GenerateWavAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            var cacheKey = $"{_voice}:{text}";
            var cacheId = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(cacheKey)));

            if (_cache.TryGetValue(cacheKey, out byte[]? cached) &&
                cached is not null)
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

                var wav = await GenerateFromPiperAsync(
                    text,
                    cancellationToken);

                _logger.LogInformation(
                    "Piper synthesis completed. CacheId={CacheId}, Bytes={Bytes}, DurationMs={DurationMs}",
                    cacheId,
                    wav.Length,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);

                if (wav.Length > 5 * 1024 * 1024)
                {
                    _logger.LogWarning(
                        "Large Piper WAV response. CacheId={CacheId}, Bytes={Bytes}",
                        cacheId,
                        wav.Length);
                }

                _cache.Set(cacheKey, wav, _cacheOptions);
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
}
