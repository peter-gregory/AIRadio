using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Weather;
using Newtonsoft.Json;
using System.Globalization;
using System.Text.Json;

namespace AIRadio.Server.Services.Weather
{
    public interface IWeatherService
    {
        Task<WeatherResult> GetWeatherAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default);
    }

    public sealed class WeatherService : IWeatherService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WeatherService> _logger;

        public WeatherService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<WeatherService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<WeatherResult> GetWeatherAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(location);

            var latitude =
                GetRequiredDouble(
                    location,
                    "Latitude");

            var longitude =
                GetRequiredDouble(
                    location,
                    "Longitude");

            var url =
                BuildWeatherUrl(
                    latitude,
                    longitude);

            try
            {
                var response =
                    await _httpClient.GetAsync(
                        url,
                        cancellationToken);

                response.EnsureSuccessStatusCode();

                await using var stream =
                    await response.Content.ReadAsStreamAsync(
                        cancellationToken);

                using var document =
                    await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken);

                return ParseWeather(
                    document.RootElement,
                    location);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to retrieve weather for {Location}.",
                    BuildLocationName(location));

                throw;
            }
        }

        private string BuildWeatherUrl(
            double latitude,
            double longitude)
        {
            var baseUrl =
                _configuration[
                    "Weather:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException(
                    "Weather:BaseUrl is not configured.");
            }

            return
                $"{baseUrl}?latitude={latitude.ToString(CultureInfo.InvariantCulture)}" +
                $"&longitude={longitude.ToString(CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,relative_humidity_2m," +
                "apparent_temperature,precipitation,weather_code," +
                "wind_speed_10m,wind_direction_10m,wind_gusts_10m,is_day" +
                "&daily=weather_code,temperature_2m_max," +
                "temperature_2m_min,precipitation_probability_max," +
                "precipitation_sum,wind_speed_10m_max,sunrise,sunset" +
                "&temperature_unit=fahrenheit" +
                "&wind_speed_unit=mph" +
                "&timezone=auto";
        }

        private static WeatherResult ParseWeather(
            JsonElement root,
            RadioLocation location)
        {
            var current =
                root.GetProperty("current");

            var daily =
                root.GetProperty("daily");

            var weather =
                new WeatherResult
                {
                    Location =
                        BuildLocationName(location),

                    Current =
                        ParseCurrent(current),

                    Forecast =
                        ParseForecast(daily),

                    RetrievedAt =
                        DateTimeOffset.Now
                };

            return weather;
        }

        private static WeatherCurrent ParseCurrent(
            JsonElement current)
        {
            return new WeatherCurrent
            {
                Temperature =
                    GetDouble(
                        current,
                        "temperature_2m"),

                FeelsLike =
                    GetDouble(
                        current,
                        "apparent_temperature"),

                Condition =
                    GetWeatherCondition(
                        GetInt32(
                            current,
                            "weather_code")),

                Humidity =
                    GetInt32(
                        current,
                        "relative_humidity_2m"),

                WindSpeed =
                    GetDouble(
                        current,
                        "wind_speed_10m"),

                WindDirection =
                    GetDouble(
                        current,
                        "wind_direction_10m"),

                WindGusts =
                    GetDouble(
                        current,
                        "wind_gusts_10m"),

                Precipitation =
                    GetDouble(
                        current,
                        "precipitation"),

                IsDay =
                    GetInt32(
                        current,
                        "is_day") == 1
            };
        }

        private static IReadOnlyList<WeatherDay> ParseForecast(
            JsonElement daily)
        {
            var dates =
                daily.GetProperty("time");

            var highs =
                daily.GetProperty("temperature_2m_max");

            var lows =
                daily.GetProperty("temperature_2m_min");

            var codes =
                daily.GetProperty("weather_code");

            var rainChances =
                daily.GetProperty("precipitation_probability_max");

            var precipitation =
                daily.GetProperty("precipitation_sum");

            var winds =
                daily.GetProperty("wind_speed_10m_max");

            var sunrise =
                daily.GetProperty("sunrise");

            var sunset =
                daily.GetProperty("sunset");

            var count =
                dates.GetArrayLength();

            var forecast =
                new List<WeatherDay>(
                    count);

            for (var i = 0; i < count; i++)
            {
                forecast.Add(
                    new WeatherDay
                    {
                        Date =
                            DateOnly.Parse(
                                dates[i].GetString()!),

                        High =
                            GetDouble(
                                highs[i]),

                        Low =
                            GetDouble(
                                lows[i]),

                        Condition =
                            GetWeatherCondition(
                                GetInt32(
                                    codes[i])),

                        RainChance =
                            GetInt32(
                                rainChances[i]),

                        Precipitation =
                            GetDouble(
                                precipitation[i]),

                        WindSpeed =
                            GetDouble(
                                winds[i]),

                        Sunrise =
                            ParseDateTimeOffset(
                                sunrise[i]),

                        Sunset =
                            ParseDateTimeOffset(
                                sunset[i])
                    });
            }

            return forecast;
        }

        private static DateTimeOffset? ParseDateTimeOffset(
            JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text =
                value.GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result)
                    ? result
                    : null;
        }

        private static string BuildLocationName(
            RadioLocation location)
        {
            if (!string.IsNullOrWhiteSpace(location.City) &&
                !string.IsNullOrWhiteSpace(location.State))
            {
                return $"{location.City}, {location.State}";
            }

            if (!string.IsNullOrWhiteSpace(location.City))
            {
                return location.City;
            }

            if (!string.IsNullOrWhiteSpace(location.State))
            {
                return location.State;
            }

            if (!string.IsNullOrWhiteSpace(location.PostalCode))
            {
                return location.PostalCode;
            }

            if (!string.IsNullOrWhiteSpace(location.Country))
            {
                return location.Country;
            }

            return location.Raw;
        }

        private static double GetRequiredDouble(
            RadioLocation location,
            string name)
        {
            /*
             * This assumes latitude/longitude will eventually be added
             * to RadioLocation. If they are supplied by a separate
             * geocoding result, this should instead use that source.
             */
            throw new NotImplementedException();
        }

        private static double GetDouble(
            JsonElement element,
            string property)
        {
            return element.TryGetProperty(
                    property,
                    out var value)
                ? GetDouble(value)
                : 0;
        }

        private static double GetDouble(
            JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;
        }

        private static int GetInt32(
            JsonElement element,
            string property)
        {
            return element.TryGetProperty(
                    property,
                    out var value)
                ? GetInt32(value)
                : 0;
        }

        private static int GetInt32(
            JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;
        }

        private static string GetWeatherCondition(
            int code)
        {
            return code switch
            {
                0 => "Clear",
                1 => "Mostly clear",
                2 => "Partly cloudy",
                3 => "Overcast",

                45 or 48 =>
                    "Foggy",

                51 or 53 or 55 =>
                    "Drizzle",

                56 or 57 =>
                    "Freezing drizzle",

                61 or 63 or 65 =>
                    "Rain",

                66 or 67 =>
                    "Freezing rain",

                71 or 73 or 75 or 77 =>
                    "Snow",

                80 or 81 or 82 =>
                    "Rain showers",

                85 or 86 =>
                    "Snow showers",

                95 =>
                    "Thunderstorms",

                96 or 99 =>
                    "Thunderstorms with hail",

                _ =>
                    "Unknown"
            };
        }
    }

    internal sealed class OpenMeteoResponse
    {
        [JsonProperty("timezone")]
        public string? Timezone { get; set; }

        [JsonProperty("current")]
        public OpenMeteoCurrent? Current { get; set; }

        [JsonProperty("daily")]
        public OpenMeteoDaily? Daily { get; set; }
    }

    internal sealed class OpenMeteoCurrent
    {
        [JsonProperty("time")]
        public string? Time { get; set; }

        [JsonProperty("temperature_2m")]
        public double Temperature { get; set; }

        [JsonProperty("relative_humidity_2m")]
        public int RelativeHumidity { get; set; }

        [JsonProperty("apparent_temperature")]
        public double ApparentTemperature { get; set; }

        [JsonProperty("precipitation")]
        public double Precipitation { get; set; }

        [JsonProperty("weather_code")]
        public int WeatherCode { get; set; }

        [JsonProperty("wind_speed_10m")]
        public double WindSpeed { get; set; }

        [JsonProperty("wind_direction_10m")]
        public double WindDirection { get; set; }

        [JsonProperty("wind_gusts_10m")]
        public double WindGusts { get; set; }

        [JsonProperty("is_day")]
        public int IsDay { get; set; }
    }

    internal sealed class OpenMeteoDaily
    {
        [JsonProperty("time")]
        public List<string> Time { get; set; } = new();

        [JsonProperty("weather_code")]
        public List<int> WeatherCode { get; set; } = new();

        [JsonProperty("temperature_2m_max")]
        public List<double> TemperatureMax { get; set; } = new();

        [JsonProperty("temperature_2m_min")]
        public List<double> TemperatureMin { get; set; } = new();

        [JsonProperty("precipitation_sum")]
        public List<double> PrecipitationSum { get; set; } = new();

        [JsonProperty("precipitation_probability_max")]
        public List<int> PrecipitationProbabilityMax { get; set; } = new();

        [JsonProperty("wind_speed_10m_max")]
        public List<double> WindSpeedMax { get; set; } = new();

        [JsonProperty("sunrise")]
        public List<string> Sunrise { get; set; } = new();

        [JsonProperty("sunset")]
        public List<string> Sunset { get; set; } = new();
    }
}
