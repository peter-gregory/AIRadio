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
        private readonly IAlarmManagerService _alarmManager;
        private readonly IWeatherService _weatherService;
        private readonly INewsService _newsService;
        private readonly ILocationService _locationService;

        public ReportTool(IAlarmManagerService alarmManager, IWeatherService weatherService, INewsService newsService, ILocationService locationService)
        {
            _alarmManager = alarmManager;
            _weatherService = weatherService;
            _newsService = newsService;
            _locationService = locationService;
        }

        public string Name => "report";

        public string GetPromptText() => "report{sections=events|reminders|weather|news}";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var sections = request.GetArgument<List<string>>("sections") ?? [];
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
                        events = _alarmManager.GetEvents(timestamp);
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
