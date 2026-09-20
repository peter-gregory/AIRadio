using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;

namespace AIRadio.Server.Services.News;

public sealed class NewsTool : ITool
{
    private readonly INewsService _newsService;
    private readonly ILocationService _locationService;

    public NewsTool(INewsService newsService, ILocationService locationService)
    {
        _newsService = newsService;
        _locationService = locationService;
    }

    public bool HasParameters => true;
    public string Name => "news";
    public string Intent => "Get current news headlines.";

    public string GetLlmInstructions() => """
NEWS
Get current news headlines.
Parameters:
- location: optional location for local news. Omit when local context is not requested.
- category: optional news category or topic.
- limit: optional maximum number of headlines; defaults to 5.
Use for current headlines, breaking news, local, national, world, or topic news.
"News report" means this tool; report is not a separate tool.
Examples:
"Give me a news report" -> {tool:news}
"What's in the news?" -> {tool:news}
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

    private static string GetLocationName(RadioLocation location) =>
        !string.IsNullOrWhiteSpace(location.City) && !string.IsNullOrWhiteSpace(location.State)
            ? $"{location.City}, {location.State}"
            : !string.IsNullOrWhiteSpace(location.City) ? location.City : location.Raw;
}
