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
WEATHER
Get current weather or forecasts.
location: optional city, state, ZIP/postal code, or recognizable place. Omit to use the radio's persistent location.
An explicit location is only for this query; it does not change the persistent location. Use weather for temperature, conditions, rain, snow, humidity, wind, or forecast. Never invent weather.
Examples:
"What's the weather?" -> {tool:weather}
"What's the weather in Miami?" -> {tool:weather,location=Miami}
"What's the weather in Miami, Florida?" -> {tool:weather,location="Miami, Florida"}
"Will it rain in Orlando?" -> {tool:weather,location=Orlando}
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
