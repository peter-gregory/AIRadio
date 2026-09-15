using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.Time
{
    public sealed class TimeTool : ITool
    {
        private readonly ILogger<TimeTool> _logger;
        private readonly ILocationService _locationService;

        public TimeTool(ILocationService locationService, ILogger<TimeTool> logger)
        {
            _locationService = locationService;
            _logger = logger;
            _logger.LogInformation("Finished construction TimeTool");
        }

        public string Name => "time";

        public string GetLlmInstructions() => """
TIME TOOL
Use the time tool when the user needs the accurate current local date or time.

Parameters:
- None. The time tool does not accept parameters.

Behavior:
- The tool uses the radio's persistent current location to provide local time context.
- Always use this tool for the current time or current date instead of guessing.
- If the radio's current location is unavailable, explain that naturally to the user rather than inventing a time.

Examples:
User: "What time is it?"
{tool:time}

User: "What's the date today?"
{tool:time}

User: "What time is it right now?"
{tool:time}

User: "What day is it?"
{tool:time}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var location = _locationService.GetCurrentLocation();

            if (location is null)
                return ToolResult.Failed(Name, "The radio's current location is not available.");

            var now = DateTimeOffset.Now;
            var result = new TimeResult
            {
                Timestamp = now,
                Location = BuildLocationName(location),
                Time = now.ToString("h:mm tt"),
                Date = now.ToString("dddd, MMMM d, yyyy")
            };

            return ToolResult.Successful(Name, "Current local time retrieved.", result);
        }

        private static string BuildLocationName(RadioLocation location)
        {
            if (!string.IsNullOrWhiteSpace(location.City) && !string.IsNullOrWhiteSpace(location.State))
                return $"{location.City}, {location.State}";
            if (!string.IsNullOrWhiteSpace(location.City)) return location.City;
            if (!string.IsNullOrWhiteSpace(location.State)) return location.State;
            if (!string.IsNullOrWhiteSpace(location.PostalCode)) return location.PostalCode;
            if (!string.IsNullOrWhiteSpace(location.Country)) return location.Country;
            return location.Raw;
        }
    }

    public sealed class TimeResult
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Location { get; init; } = string.Empty;
        public string Time { get; init; } = string.Empty;
        public string Date { get; init; } = string.Empty;
    }
}
