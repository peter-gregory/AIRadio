using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Models.Weather;
using AIRadio.Server.Services.Alarms;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Services.News;
using AIRadio.Server.Services.Weather;

namespace AIRadio.Server.Services.Report
{
    public sealed class ReportTool : ITool
    {
        private readonly ILogger<ReportTool> _logger;
        private readonly IAlarmService _alarmService;
        private readonly IWeatherService _weatherService;
        private readonly INewsService _newsService;
        private readonly ILocationService _locationService;

        public ReportTool(IAlarmService alarmManager, IWeatherService weatherService, INewsService newsService, ILocationService locationService, ILogger<ReportTool> logger)
        {
            _alarmService = alarmManager;
            _weatherService = weatherService;
            _newsService = newsService;
            _locationService = locationService;
            _logger = logger;
            _logger.LogInformation("Fininshed construction ReportTool");
        }

        public string Name => "report";

        public string GetLlmInstructions() => """
REPORT TOOL
Use the report tool to gather several kinds of current information into one general report.

Parameters:
- sections: Required. A comma-separated list of one or more section names. Valid section names are events, reminders, weather, and news. Because commas separate tool parameters, the complete list must be enclosed in double quotes.

Behavior:
- events retrieves scheduled events.
- reminders retrieves scheduled reminders. The report uses the same scheduled-event source for events and reminders.
- weather retrieves current weather for the radio's persistent location.
- news retrieves top current news for the radio's persistent location.
- The report always uses the radio's persistent location for weather and news. It does not accept a separate location parameter.
- Use only the sections needed for the user's request.
- Do not invent information that the report does not return.

Examples:
User: "Give me a morning report"
{tool:report,sections="events,weather,news"}

User: "What's happening?"
{tool:report,sections="events,weather,news"}

User: "Give me the weather and news"
{tool:report,sections="weather,news"}

User: "What do I have scheduled and what's the weather?"
{tool:report,sections="events,weather"}

User: "Tell me my reminders"
{tool:report,sections="reminders"}

User: "What's the news?"
{tool:report,sections="news"}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var sections = request.GetString("sections")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [];
            if (sections.Count == 0)
                return ToolResult.Failed(Name, "No report sections were specified.");

            var timestamp = DateTime.Now;
            IReadOnlyList<ScheduledEvent> events = [];
            WeatherResult? weather = null;
            NewsResult? news = null;
            RadioLocation? location = _locationService.GetCurrentLocation();

            var normalizedSections = sections
                .Where(section => !string.IsNullOrWhiteSpace(section))
                .Select(section => section.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            if ((normalizedSections.Contains("weather") || normalizedSections.Contains("news")) && location is null)
                return ToolResult.Failed(Name, "The radio's current location is not available.");

            foreach (var section in normalizedSections)
            {
                switch (section)
                {
                    case "events":
                    case "reminders":
                        events = _alarmService.GetEvents(timestamp);
                        break;
                    case "weather":
                        weather = await _weatherService.GetWeatherAsync(location!, cancellationToken);
                        break;
                    case "news":
                        news = await GetNewsAsync(null, cancellationToken);
                        break;
                    default:
                        return ToolResult.Failed(Name, $"Unknown report section '{section}'.");
                }
            }

            var report = new ReportData
            {
                Timestamp = timestamp,
                Events = events,
                Weather = weather,
                News = news
            };

            return ToolResult.Successful(Name, "Report data retrieved successfully.", report);
        }

        private async Task<NewsResult> GetNewsAsync(string? locationName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(locationName))
            {
                var location = _locationService.GetCurrentLocation()
                    ?? throw new InvalidOperationException("The radio's current location is not available.");
                locationName = BuildLocationName(location);
            }

            return await _newsService.GetHeadlinesAsync(new NewsQuery
            {
                Category = "top",
                Location = locationName,
                Limit = 5,
                MaxAge = TimeSpan.FromHours(24)
            }, cancellationToken);
        }

        private static string BuildLocationName(RadioLocation location)
        {
            if (!string.IsNullOrWhiteSpace(location.City) && !string.IsNullOrWhiteSpace(location.State)) return $"{location.City}, {location.State}";
            if (!string.IsNullOrWhiteSpace(location.City)) return location.City;
            if (!string.IsNullOrWhiteSpace(location.State)) return location.State;
            if (!string.IsNullOrWhiteSpace(location.PostalCode)) return location.PostalCode;
            if (!string.IsNullOrWhiteSpace(location.Country)) return location.Country;
            return location.Raw;
        }
    }

    public sealed class ReportData
    {
        public DateTime Timestamp { get; init; }
        public IReadOnlyList<ScheduledEvent> Events { get; init; } = [];
        public WeatherResult? Weather { get; init; }
        public NewsResult? News { get; init; }
    }
}
