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
LOCATION TOOL
Use the location tool to read or change the radio's persistent current location.

Parameters:
- location: Optional location to set. If omitted, read the current persistent location. When supplied, it may be a city, state, ZIP/postal code, or another recognizable place name.

Behavior:
- Omit location when the user asks where the radio is currently set.
- Supply location when the user explicitly wants to change the radio's location.
- Setting the location updates the persistent location used by weather, news, and time when those tools do not receive their own location.
- A location supplied directly to weather or news is only a query location and does not change this persistent location.

Examples:
User: "Where am I set to?"
{tool:location}

User: "What's my current location?"
{tool:location}

User: "Set my location to Miami"
{tool:location,location=Miami}

User: "Change the radio location to Orlando, Florida"
{tool:location,location="Orlando, Florida"}

User: "Use New York as my location"
{tool:location,location="New York"}
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
