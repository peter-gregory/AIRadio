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

        public OpenMeteoLocationResolver(
            HttpClient httpClient)
        {
            _httpClient = httpClient;
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

            using var response =
                await _httpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

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

            return string.IsNullOrWhiteSpace(result)
                ? location.Raw
                : result;
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
