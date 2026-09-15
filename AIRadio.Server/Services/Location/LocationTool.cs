namespace AIRadio.Server.Services.Location
{
    using AIRadio.Server.Models.Tools;

    public sealed class LocationTool : ITool
    {
        private readonly ILogger<LocationTool> _logger;
        private readonly ILocationService _locationService;

        public LocationTool(ILocationService locationService, ILogger<LocationTool> logger)
        {
            _logger = logger;
            _locationService = locationService;
            _logger.LogInformation("Finish construction of LocationTool");
        }

        public string Name => "location";

        public string GetLlmInstructions() => """
LOCATION
Read or change the radio's persistent current location.
location: optional city, state, ZIP/postal code, or recognizable place. Omit it to read the current setting; supply it to change the setting. A changed location becomes the default for weather, news, and time. Weather/news locations are query-only and do not change it.
Examples:
"Where am I set to?" -> {tool:location}
"Set my location to Miami" -> {tool:location,location=Miami}
"Change the radio location to Orlando, Florida" -> {tool:location,location="Orlando, Florida"}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var location = request.GetString("location");

            if (string.IsNullOrWhiteSpace(location))
            {
                var current = _locationService.GetCurrentLocation();
                return ToolResult.Successful(Name, "Current location retrieved.", current);
            }

            var updated = await _locationService.UpdateCurrentLocationAsync(location, cancellationToken);
            if (updated is null)
                return ToolResult.Failed(Name, $"Unable to determine location '{location}'.");

            return ToolResult.Successful(Name, "Current location updated.", updated);
        }
    }
}
