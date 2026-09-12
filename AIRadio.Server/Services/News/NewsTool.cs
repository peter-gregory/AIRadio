using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.News
{
    public sealed class NewsTool : ITool
    {
        private readonly INewsService _newsService;
        private readonly ILocationService _locationService;

        public NewsTool(INewsService newsService, ILocationService locationService)
        {
            _newsService = newsService;
            _locationService = locationService;
        }

        public string Name => "news";

        public string GetPromptText() => "news{location?;category?;limit?=5}";

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
