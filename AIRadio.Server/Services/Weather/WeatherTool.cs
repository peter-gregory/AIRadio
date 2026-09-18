using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Weather;

public sealed class WeatherTool : ITool
{
    private readonly IWeatherService _weatherService;
    private readonly ILocationService _locationService;

    public WeatherTool(IWeatherService weatherService, ILocationService locationService)
    {
        _weatherService = weatherService;
        _locationService = locationService;
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

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cityName = request.GetString("City");

        if (string.IsNullOrWhiteSpace(cityName))
        {
            var location = _locationService.GetCurrentLocation();
            if (location is not null)
                cityName = location.City;

            if (string.IsNullOrWhiteSpace(cityName))
            {
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

        var locationForWeather =
            await _locationService.ResolveLocationAsync(cityName.Trim(), cancellationToken);

        if (locationForWeather is null)
            return ToolResult.Failed(
                Name,
                $"Unable to determine the location '{cityName}'.");

        try
        {
            var weather = await _weatherService.GetWeatherAsync(locationForWeather, cancellationToken);
            return ToolResult.Successful(Name, "Current weather retrieved successfully.", weather);
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
