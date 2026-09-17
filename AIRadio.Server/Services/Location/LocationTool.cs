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

Parameters:
- location: Optional city, state, ZIP/postal code, or recognizable place.
- Omit location to read the current setting.
- Supply location only when the user explicitly wants to change the setting.

Rules:
- Use this tool for location requests and location configuration only.
- Do not call this tool just because another tool is available.
- The time tool does not require this tool.
- A changed location remains the default for weather and news.
- Weather and news may use their own query location without changing this setting.

Examples:
"Where am I set to?" -> {tool:location}
"Set my location to Miami" -> {tool:location,location=Miami}
"Change the radio location to Orlando, Florida" -> {tool:location,location="Orlando, Florida"}
"What time is it?" -> do not call location; use {tool:time}
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
