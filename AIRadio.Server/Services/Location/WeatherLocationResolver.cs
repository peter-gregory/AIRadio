using AIRadio.Server.Models.Location;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.Location
{
    public interface IWeatherLocationResolver
    {
        Task<WeatherCoordinates?> ResolveAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default);
    }

    public sealed class OpenMeteoLocationResolver
        : IWeatherLocationResolver
    {
        private readonly HttpClient _httpClient;
        private ILogger<OpenMeteoLocationResolver> _logger;

        public OpenMeteoLocationResolver(
            HttpClient httpClient, ILogger<OpenMeteoLocationResolver> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<WeatherCoordinates?> ResolveAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default)
        {
            var query =
                !string.IsNullOrWhiteSpace(location.PostalCode)
                    ? location.PostalCode
                    : BuildQuery(location);

            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var url =
                "https://geocoding-api.open-meteo.com/v1/search" +
                $"?name={Uri.EscapeDataString(query)}" +
                "&count=1" +
                "&language=en" +
                "&format=json";

            _logger.LogInformation($"Get location lat/long using {url}");

            try
            {
                using var response =
                    await _httpClient.GetAsync(
                        url,
                        cancellationToken);

                _logger.LogInformation($"Response: {response}");

                response.EnsureSuccessStatusCode();

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                _logger.LogInformation($"Received location reply {json}");

                var result =
                    JsonConvert.DeserializeObject<OpenMeteoGeocodingResponse>(
                        json);

                var item =
                    result?.Results?.FirstOrDefault();

                if (item is null)
                {
                    return null;
                }

                return new WeatherCoordinates
                {
                    Latitude = item.Latitude,
                    Longitude = item.Longitude,
                    TimeZone = item.TimeZone
                };
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Weather location lookup failed for '{Query}'.",
                    query);

                return null;
            }


        }

        private static string? BuildQuery(
            RadioLocation location)
        {
            var parts =
                new[]
                {
                location.City,
                location.State,
                location.Country
                }
                .Where(
                    value =>
                        !string.IsNullOrWhiteSpace(value));

            var result =
                string.Join(
                    ", ",
                    parts);

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }

            return NormalizeRawLocation(location.Raw);
        }

        private static string? NormalizeRawLocation(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            var value = raw.Trim();

            // Preserve locations that already use comma-separated formatting.
            if (value.Contains(','))
            {
                return value;
            }

            // Open-Meteo handles "City, State" more reliably than a raw
            // value such as "Palm City Florida".
            var states = new[]
            {
                ("Alabama", "AL"), ("Alaska", "AK"), ("Arizona", "AZ"),
                ("Arkansas", "AR"), ("California", "CA"), ("Colorado", "CO"),
                ("Connecticut", "CT"), ("Delaware", "DE"), ("Florida", "FL"),
                ("Georgia", "GA"), ("Hawaii", "HI"), ("Idaho", "ID"),
                ("Illinois", "IL"), ("Indiana", "IN"), ("Iowa", "IA"),
                ("Kansas", "KS"), ("Kentucky", "KY"), ("Louisiana", "LA"),
                ("Maine", "ME"), ("Maryland", "MD"), ("Massachusetts", "MA"),
                ("Michigan", "MI"), ("Minnesota", "MN"), ("Mississippi", "MS"),
                ("Missouri", "MO"), ("Montana", "MT"), ("Nebraska", "NE"),
                ("Nevada", "NV"), ("New Hampshire", "NH"), ("New Jersey", "NJ"),
                ("New Mexico", "NM"), ("New York", "NY"), ("North Carolina", "NC"),
                ("North Dakota", "ND"), ("Ohio", "OH"), ("Oklahoma", "OK"),
                ("Oregon", "OR"), ("Pennsylvania", "PA"), ("Rhode Island", "RI"),
                ("South Carolina", "SC"), ("South Dakota", "SD"), ("Tennessee", "TN"),
                ("Texas", "TX"), ("Utah", "UT"), ("Vermont", "VT"),
                ("Virginia", "VA"), ("Washington", "WA"), ("West Virginia", "WV"),
                ("Wisconsin", "WI"), ("Wyoming", "WY"), ("District of Columbia", "DC")
            };

            foreach (var (stateName, abbreviation) in states)
            {
                if (value.EndsWith(stateName, StringComparison.OrdinalIgnoreCase))
                {
                    var city = value[..^stateName.Length].TrimEnd();

                    if (city.Length > 0)
                    {
                        return $"{city}, {value[^stateName.Length..]}";
                    }
                }

                if (value.EndsWith(abbreviation, StringComparison.OrdinalIgnoreCase))
                {
                    var city = value[..^abbreviation.Length].TrimEnd();

                    if (city.Length > 0 &&
                        char.IsWhiteSpace(value[value.Length - abbreviation.Length - 1]))
                    {
                        return $"{city}, {value[^abbreviation.Length..]}";
                    }
                }
            }

            return value;
        }
    }

    internal sealed class OpenMeteoGeocodingResponse
    {
        [JsonProperty("results")]
        public List<OpenMeteoGeocodingResult>? Results { get; set; }
    }

    internal sealed class OpenMeteoGeocodingResult
    {
        [JsonProperty("latitude")]
        public double Latitude { get; set; }

        [JsonProperty("longitude")]
        public double Longitude { get; set; }

        [JsonProperty("timezone")]
        public string? TimeZone { get; set; }
    }

    public sealed class WeatherCoordinates
    {
        public double Latitude { get; init; }

        public double Longitude { get; init; }

        public string? TimeZone { get; init; }
    }
}
