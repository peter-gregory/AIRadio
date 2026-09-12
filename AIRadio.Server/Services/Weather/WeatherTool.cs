using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.Weather
{
    public sealed class WeatherTool : ITool
    {
        private readonly IWeatherService _weatherService;
        private readonly ILocationService _locationService;

        public WeatherTool(IWeatherService weatherService, ILocationService locationService)
        {
            _weatherService = weatherService;
            _locationService = locationService;
        }

        public string Name => "weather";

        public string GetPromptText() => "weather{location?}";

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
