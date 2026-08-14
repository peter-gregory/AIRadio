using AIRadio.Server.Models.Radio;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioSearchClient
    {
        Task<IReadOnlyList<RadioStation>> SearchAsync(
            RadioSearchCriteria criteria,
            CancellationToken cancellationToken = default);
    }

    public sealed class RadioSearchClient : IRadioSearchClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<RadioSearchClient> _logger;

        private readonly string _baseUrl;
        private readonly string _searchPath;
        private readonly string _userAgent;
        private readonly int _defaultLimit;
        private readonly int _maxLimit;

        public RadioSearchClient(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<RadioSearchClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _baseUrl =
                configuration["RadioBrowser:BaseUrl"]
                ?? "https://de1.api.radio-browser.info";

            _searchPath =
                configuration["RadioBrowser:SearchPath"]
                ?? "/json/stations/search";

            _userAgent =
                configuration["RadioBrowser:UserAgent"]
                ?? "RadioAppliance/1.0";

            _defaultLimit =
                GetConfigurationInt(
                    configuration,
                    "RadioBrowser:DefaultLimit",
                    25);

            _maxLimit =
                GetConfigurationInt(
                    configuration,
                    "RadioBrowser:MaxLimit",
                    100);

            _maxLimit =
                Math.Max(1, _maxLimit);

            _defaultLimit =
                Math.Clamp(
                    _defaultLimit,
                    1,
                    _maxLimit);
        }

        public async Task<IReadOnlyList<RadioStation>> SearchAsync(
            RadioSearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(criteria);

            if (!criteria.HasCriteria)
                return [];

            var query =
                BuildQuery(criteria);

            var requestUri =
                $"{_baseUrl.TrimEnd('/')}/" +
                $"{_searchPath.TrimStart('/')}?" +
                query;

            try
            {
                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        requestUri);

                request.Headers.UserAgent.ParseAdd(
                    _userAgent);

                using var response =
                    await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                response.EnsureSuccessStatusCode();

                var results =
                    await response.Content
                        .ReadFromJsonAsync<
                            List<RadioBrowserStation>>(
                                cancellationToken);

                if (results is null ||
                    results.Count == 0)
                {
                    return [];
                }

                return results
                    .Where(IsUsableStation)
                    .Select(MapStation)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Radio Browser search failed: {Uri}",
                    requestUri);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing Radio Browser search results.");

                throw;
            }
        }

        private string BuildQuery(
            RadioSearchCriteria criteria)
        {
            var parameters =
                new List<string>();

            if (!string.IsNullOrWhiteSpace(
                    criteria.StationName))
            {
                parameters.Add(
                    $"name={Uri.EscapeDataString(
                        criteria.StationName)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.Genre))
            {
                parameters.Add(
                    $"tag={Uri.EscapeDataString(
                        criteria.Genre)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.Language))
            {
                parameters.Add(
                    $"language={Uri.EscapeDataString(
                        criteria.Language)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.Country))
            {
                parameters.Add(
                    $"country={Uri.EscapeDataString(
                        criteria.Country)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.State))
            {
                parameters.Add(
                    $"state={Uri.EscapeDataString(
                        criteria.State)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.City))
            {
                parameters.Add(
                    $"city={Uri.EscapeDataString(
                        criteria.City)}");
            }

            if (!string.IsNullOrWhiteSpace(
                    criteria.Tag))
            {
                parameters.Add(
                    $"tag={Uri.EscapeDataString(
                        criteria.Tag)}");
            }

            var limit =
                criteria.Limit > 0
                    ? criteria.Limit
                    : _defaultLimit;

            limit =
                Math.Clamp(
                    limit,
                    1,
                    _maxLimit);

            parameters.Add(
                $"limit={limit}");

            parameters.Add(
                "hidebroken=true");

            parameters.Add(
                "order=clickcount");

            parameters.Add(
                "reverse=true");

            return string.Join(
                "&",
                parameters);
        }

        private static bool IsUsableStation(
            RadioBrowserStation station)
        {
            var streamUrl =
                GetStreamUrl(station);

            return
                !string.IsNullOrWhiteSpace(
                    station.Name) &&
                !string.IsNullOrWhiteSpace(
                    streamUrl);
        }

        private static RadioStation MapStation(
            RadioBrowserStation station)
        {
            var streamUrl =
                GetStreamUrl(station)!;

            return new RadioStation
            {
                Id =
                    station.StationUuid ??
                    streamUrl,

                Name =
                    station.Name?.Trim() ??
                    string.Empty,

                StreamUrl =
                    streamUrl,

                Description =
                    string.IsNullOrWhiteSpace(
                        station.Description)
                        ? null
                        : station.Description.Trim(),

                Homepage =
                    station.Homepage,

                Favicon =
                    station.Favicon,

                Tags =
                    SplitValues(
                        station.Tags),

                Country =
                    station.Country,

                CountryCode =
                    station.CountryCode,

                State =
                    station.State,

                Languages =
                    SplitValues(
                        station.Language),

                Codec =
                    station.Codec,

                Bitrate =
                    station.Bitrate > 0
                        ? station.Bitrate
                        : null,

                Source =
                    "RadioBrowser",

                SourceId =
                    station.StationUuid,

                IsHttps =
                    Uri.TryCreate(
                        streamUrl,
                        UriKind.Absolute,
                        out var uri) &&
                    uri.Scheme.Equals(
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase),

                IsOnline =
                    true,

                Votes =
                    station.Votes,

                Latitude =
                    station.Latitude,

                Longitude =
                    station.Longitude
            };
        }

        private static string? GetStreamUrl(
            RadioBrowserStation station)
        {
            if (!string.IsNullOrWhiteSpace(
                    station.UrlResolved))
            {
                return station.UrlResolved.Trim();
            }

            if (!string.IsNullOrWhiteSpace(
                    station.Url))
            {
                return station.Url.Trim();
            }

            return null;
        }

        private static string[] SplitValues(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            return value
                .Split(
                    [',', ';'],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int GetConfigurationInt(
            IConfiguration configuration,
            string key,
            int defaultValue)
        {
            return int.TryParse(
                    configuration[key],
                    out var value)
                ? value
                : defaultValue;
        }

        private sealed class RadioBrowserStation
        {
            [JsonPropertyName("stationuuid")]
            public string? StationUuid { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("url_resolved")]
            public string? UrlResolved { get; set; }

            [JsonPropertyName("homepage")]
            public string? Homepage { get; set; }

            [JsonPropertyName("favicon")]
            public string? Favicon { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }

            [JsonPropertyName("countrycode")]
            public string? CountryCode { get; set; }

            [JsonPropertyName("state")]
            public string? State { get; set; }

            [JsonPropertyName("language")]
            public string? Language { get; set; }

            [JsonPropertyName("tags")]
            public string? Tags { get; set; }

            [JsonPropertyName("codec")]
            public string? Codec { get; set; }

            [JsonPropertyName("bitrate")]
            public int Bitrate { get; set; }

            [JsonPropertyName("votes")]
            public int Votes { get; set; }

            [JsonPropertyName("geo_lat")]
            public double? Latitude { get; set; }

            [JsonPropertyName("geo_long")]
            public double? Longitude { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }
        }
    }
}
