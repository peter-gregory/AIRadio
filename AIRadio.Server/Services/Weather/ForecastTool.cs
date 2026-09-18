using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
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
    public string Name => "forecast";
    public string GetLlmInstructions() => """
FORECAST
Get the weather forecast.
Parameters:
- location: optional city, state, ZIP/postal code, or recognizable place. Omit to use the radio's persistent location.
Use for future weather such as tomorrow, this weekend, or this week.
An explicit location does not change the persistent location.
Speak only the forecast information relevant to the user's requested period.
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var locationName = request.GetString("location");
        RadioLocation? location = string.IsNullOrWhiteSpace(locationName)
            ? _locationService.GetCurrentLocation()
            : await _locationService.ResolveLocationAsync(locationName.Trim(), cancellationToken);
        if (location is null)
            return ToolResult.Failed(Name, string.IsNullOrWhiteSpace(locationName) ? "The radio's current location is not available." : $"Unable to determine the location '{locationName}'.");
        try
        {
            var result = await _weatherService.GetWeatherAsync(location, cancellationToken);
            return ToolResult.Successful(Name, "Weather forecast retrieved successfully.", result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ToolResult.Failed(Name, ex.Message); }
    }
}
