namespace AIRadio.Server.Services.Location
{
    using AIRadio.Server.Models.Tools;
    using Newtonsoft.Json;
    using System.Text.RegularExpressions;

    public sealed class LocationTool : ITool
    {
        private readonly ILocationService _locationService;

        public LocationTool(
            ILocationService locationService)
        {
            _locationService = locationService;
        }

        public string Name =>
            "location";

        public async Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var location =
                request.GetString("location");

            if (string.IsNullOrWhiteSpace(location))
            {
                var current =
                    _locationService.GetCurrentLocation();

                return ToolResult.Successful(
                    Name,
                    "Current location retrieved.",
                    current);
            }

            var updated =
                await _locationService.UpdateCurrentLocationAsync(
                    location,
                    cancellationToken);

            if (updated is null)
            {
                return ToolResult.Failed(
                    Name,
                    $"Unable to determine location '{location}'.");
            }

            return ToolResult.Successful(
                Name,
                "Current location updated.",
                updated);
        }
    }
}
