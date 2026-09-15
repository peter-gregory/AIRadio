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
Purpose: Read or update the radio's persistent current location.
Parameters:
- location: The location to set. Omit it to read the current location.
Use this tool when the user explicitly gives a new location for the radio or when the current persistent location is needed.
A location supplied only to weather or news is a query location and must not update the persistent location.
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
