using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Models.Weather;
using AIRadio.Server.Services.Location;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Weather;

public sealed class WeatherTool : ITool
{
    private readonly IWeatherService _weatherService;
    private readonly ILocationService _locationService;
    private readonly ILogger<WeatherTool> _logger;

    public WeatherTool(IWeatherService weatherService, ILocationService locationService, ILogger<WeatherTool> logger)
    {
        _weatherService = weatherService;
        _locationService = locationService;
        _logger = logger;
    }

    public string Name => "weather-current";

    public string GetLlmInstructions() => """
WEATHER-CURRENT
Get current weather conditions.
Parameters:
- City: required city and state, ZIP/postal code, or recognizable place. The application may supply a persistent location when available.
Use for what the weather is like now. Future weather belongs to forecast.
Examples:
"What's the weather?" -> {tool:weather-current}
"What's the weather in Miami?" -> {tool:weather-current,City="Miami, Florida"}
"Give me a weather report" -> {tool:weather-current}
""";

    public string GetLlmResponseInstructions() => """
WEATHER RESPONSE
- Report only the information present in Current.
- Temperature and FeelsLike are Fahrenheit values. Say "degrees", never "°F" or "Fahrenheit".
- Round temperatures to the nearest whole degree for speech.
- Say wind speed in miles per hour.
- Say WindDirection as a compass direction, not as a degree value.
- Report humidity as a percent.
- Mention precipitation only when it is greater than zero.
- Use the Condition as provided; do not infer a different condition.
- Keep the response concise and natural for speech.
""";

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Start execute tool weather-current");
        ArgumentNullException.ThrowIfNull(request);

        var cityName = request.GetString("City");

        if (string.IsNullOrWhiteSpace(cityName))
        {
            _logger.LogInformation("City name is not defined - use current locations to get it");
            var location = _locationService.GetCurrentLocation();
            if (location is not null)
                cityName = location.City;

            if (string.IsNullOrWhiteSpace(cityName))
            {
                _logger.LogInformation("Current location does not have city name");
                var pendingRequest = new ToolRequest
                {
                    Name = Name,
                    Arguments = new JObject
                    {
                        ["City"] = ToolRequest.RequiredValue
                    }
                };

                return ToolResult.Failed(
                    Name,
                    "A city and state are required to get the current weather.",
                    exactPrompt: "I need to know where we are. What is the name of the city and state where we are currently located?",
                    pendingRequest: pendingRequest);
            }
        }

        _logger.LogInformation("Resolve location for city name " + cityName);

        var locationForWeather =
            await _locationService.ResolveLocationAsync(cityName.Trim(), cancellationToken);

        if (locationForWeather is null)
        {
            _logger.LogInformation("Failed to resolve location for city name " + cityName);
            return ToolResult.Failed(
                    Name,
                    $"Unable to determine the location '{cityName}'.");
        }

        try
        {
            _logger.LogInformation("Get weather from weather client");
            var weather = await _weatherService.GetWeatherAsync(locationForWeather, cancellationToken);

            if (weather.Current is null)
                return ToolResult.Failed(Name, "Current weather data was not returned.");

            return ToolResult.Successful(
                Name,
                "Current weather retrieved successfully.",
                new WeatherCurrentReport(
                    weather.Location,
                    weather.Current));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failed(Name, ex.Message);
        }
    }

    private sealed class WeatherCurrentReport
    {
        public string Location { get; }
        public double Temperature { get; }
        public double FeelsLike { get; }
        public string Condition { get; }
        public int Humidity { get; }
        public double WindSpeed { get; }
        public string WindDirection { get; }
        public double WindGusts { get; }
        public double Precipitation { get; }

        public WeatherCurrentReport(string location, WeatherCurrent current)
        {
            Location = location;
            Temperature = current.Temperature;
            FeelsLike = current.FeelsLike;
            Condition = current.Condition;
            Humidity = current.Humidity;
            WindSpeed = current.WindSpeed;
            WindDirection = ToCompassDirection(current.WindDirection);
            WindGusts = current.WindGusts;
            Precipitation = current.Precipitation;
        }

        private static string ToCompassDirection(double degrees)
        {
            var normalized = ((degrees % 360) + 360) % 360;
            var index = (int)Math.Round(normalized / 22.5, MidpointRounding.AwayFromZero) % 16;
            return new[]
            {
                "north", "north-northeast", "northeast", "east-northeast",
                "east", "east-southeast", "southeast", "south-southeast",
                "south", "south-southwest", "southwest", "west-southwest",
                "west", "west-northwest", "northwest", "north-northwest"
            }[index];
        }
    }
}
