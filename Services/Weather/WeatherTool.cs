using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.Weather
{
    public sealed class WeatherTool : ITool
    {
        private readonly ILogger<WeatherTool> _logger;
        private readonly IWeatherService _weatherService;
        private readonly ILocationService _locationService;

        public WeatherTool(IWeatherService weatherService, ILocationService locationService, ILogger<WeatherTool> logger)
        {
            _logger = logger;
            _weatherService = weatherService;
            _locationService = locationService;
            _logger.LogInformation("Fininshed construction for WeatherTool");
        }

        public string Name => "weather";

        public string GetLlmInstructions() => """
WEATHER TOOL
Use the weather tool to retrieve current weather and forecast information.

Parameters:
- location: Optional location to query. If omitted, use the radio's persistent current location. This can be a city, state, ZIP/postal code, or another recognizable place name.

Use weather for questions about temperature, conditions, precipitation, rain, snow, humidity, wind, forecasts, and similar weather information.

Important:
- If the user names a location in the weather request, pass that location in the location parameter.
- A location supplied to weather is only for this weather query. It does not change the radio's persistent location.
- If the user asks about weather without naming a place, omit location so the radio's current location is used.
- Do not invent weather information.

Examples:
User: "What's the weather?"
{tool:weather}

User: "What's the weather in Miami?"
{tool:weather,location=Miami}

User: "What's the weather in Miami, Florida?"
{tool:weather,location="Miami, Florida"}

User: "Will it rain in Orlando?"
{tool:weather,location=Orlando}

User: "What's the forecast for Tampa?"
{tool:weather,location=Tampa}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var locationName = request.GetString("location");
            RadioLocation? location = string.IsNullOrWhiteSpace(locationName)
                ? _locationService.GetCurrentLocation()
                : await _locationService.ResolveLocationAsync(locationName.Trim(), cancellationToken);

            if (location is null)
            {
                return ToolResult.Failed(
                    Name,
                    string.IsNullOrWhiteSpace(locationName)
                        ? "The radio's current location is not available."
                        : $"Unable to determine the location '{locationName}'.");
            }

            try
            {
                var weather = await _weatherService.GetWeatherAsync(location, cancellationToken);
                return ToolResult.Successful(Name, "Weather retrieved successfully.", weather);
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
    }
}
