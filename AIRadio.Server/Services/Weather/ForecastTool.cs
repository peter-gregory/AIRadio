using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Models.Weather;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.Weather;

public sealed class ForecastTool : ITool
{
    private readonly IWeatherService _weatherService;
    private readonly ILocationService _locationService;

    public ForecastTool(IWeatherService weatherService, ILocationService locationService)
    {
        _weatherService = weatherService;
        _locationService = locationService;
    }

    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:forecast,location=<optional>}";
    public string Name => "forecast";
    public string Intent => "Get the weather forecast.";

    public string GetLlmInstructions() => """
FORECAST
Get the weather forecast.
Parameters:
- location: optional city, state, ZIP/postal code, or recognizable place. Omit to use the radio's persistent location.
Use for future weather such as tomorrow, this weekend, or this week.
An explicit location does not change the persistent location.
Speak only the forecast information relevant to the user's requested period.
""";

    public string GetLlmResponseInstructions() => """
FORECAST RESPONSE
- Use only the forecast information in the tool result.
- Clearly identify the day when reporting a forecast.
- High and low temperatures are Fahrenheit values. Say "degrees", never "°F" or "Fahrenheit".
- Round temperatures to the nearest whole degree for speech.
- Report rain probability as a percentage only when it is present.
- Report wind speed in miles per hour.
- Do not present forecast values as current conditions.
- Keep the response concise and natural for speech.
""";

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var locationName = request.GetString("location");
        RadioLocation? location = string.IsNullOrWhiteSpace(locationName)
            ? _locationService.GetCurrentLocation()
            : await _locationService.ResolveLocationAsync(locationName.Trim(), cancellationToken);

        if (location is null)
            return ToolResult.Failed(
                Name,
                string.IsNullOrWhiteSpace(locationName)
                    ? "The radio's current location is not available."
                    : $"Unable to determine the location '{locationName}'.");

        try
        {
            var result = await _weatherService.GetWeatherAsync(location, cancellationToken);
            return ToolResult.Successful(
                Name,
                "Weather forecast retrieved successfully.",
                new WeatherForecastReport(result));
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
    private sealed class WeatherForecastReport
    {
        public string Location { get; }
        public IReadOnlyList<WeatherDay> Forecast { get; }

        public WeatherForecastReport(WeatherResult result)
        {
            Location = result.Location;
            Forecast = result.Forecast;
        }
    }
}
