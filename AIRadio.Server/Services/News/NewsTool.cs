using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.News
{
    public sealed class NewsTool : ITool
    {
        private ILogger<NewsTool> _logger;
        private readonly INewsService _newsService;
        private readonly ILocationService _locationService;

        public NewsTool(INewsService newsService, ILocationService locationService, ILogger<NewsTool> logger)
        {
            _logger = logger;
            _newsService = newsService;
            _locationService = locationService;
            _logger.LogInformation("Finished constructing NewsTool");
        }

        public string Name => "news";

        public string GetLlmInstructions() => """
NEWS TOOL
Purpose: Retrieve current news headlines.
Parameters:
- location (optional): The location for local news. If omitted, use the radio's persistent location when local context is implied.
- category (optional): The requested news category or topic.
- limit (optional): Maximum number of headlines; defaults to 5.
Use news for current headlines, breaking news, local/national/world news, topic news, and similar requests.
An explicit location is only a query location; do not change the radio's persistent location because of it.
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var locationName = request.GetString("location");
            RadioLocation? location = string.IsNullOrWhiteSpace(locationName)
                ? _locationService.GetCurrentLocation()
                : await _locationService.ResolveLocationAsync(locationName.Trim(), cancellationToken);

            if (location is null)
                return ToolResult.Failed(Name, "Unable to determine the requested location.");

            var query = new NewsQuery
            {
                Location = GetLocationName(location),
                Category = request.GetString("category"),
                Limit = request.GetInt32("limit") ?? 5
            };

            var articles = await _newsService.GetHeadlinesAsync(query, cancellationToken);
            return ToolResult.Successful(Name, "News retrieved successfully.", articles);
        }

        private static string GetLocationName(RadioLocation location)
        {
            if (!string.IsNullOrWhiteSpace(location.City) && !string.IsNullOrWhiteSpace(location.State))
                return $"{location.City}, {location.State}";
            if (!string.IsNullOrWhiteSpace(location.City))
                return location.City;
            return location.Raw;
        }
    }
}
