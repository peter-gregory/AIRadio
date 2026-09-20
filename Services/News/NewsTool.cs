using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Models.LLama;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.News
{
    public sealed class NewsTool : ITool
    {
        private readonly ILogger<NewsTool> _logger;
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
Use the news tool to retrieve current news headlines.

Parameters:
- location: Optional location for the news query. If omitted, use the radio's persistent current location. Use this for local or location-specific news.
- category: Optional news category or topic, such as local, national, world, sports, business, technology, or another requested topic.
- limit: Optional integer specifying the maximum number of headlines. Defaults to 5.

Use news for current headlines, breaking news, local news, national news, world news, topic news, and similar requests.

Important:
- If the user names a location, pass it as location.
- If the user asks for local news without naming a place, omit location so the radio's current location is used.
- An explicit location is only a query location. It does not change the radio's persistent location.
- If the user asks for a particular number of stories, pass that number as limit.
- Do not invent headlines or news facts.

Examples:
User: "What's the news?"
{tool:news}

User: "What's the local news?"
{tool:news}

User: "What's the news in Miami?"
{tool:news,location=Miami}

User: "Give me the latest technology news"
{tool:news,category=technology}

User: "Give me the top 3 sports stories"
{tool:news,category=sports,limit=3}

User: "What's the news in Orlando about business?"
{tool:news,location=Orlando,category=business}
""";

        public string GetLlmResponseInstructions() => """
NEWS RESPONSE
- The preamble has already been spoken before this response. Do not repeat or add an introduction.
- Speak only the news headlines. Do not summarize the article descriptions.
- Preserve the meaning of each headline, but rewrite it slightly when needed for natural speech.
- Start every headline with {sound:news-breaking}.
- Use one short sound effect between each story by placing {sound:news-breaking} immediately before every headline.
- Do not combine multiple headlines into one sentence.
- Do not read report labels such as REPORT, Articles, Source, Title, or Summary.
- Do not mention the source, URLs, timestamps, IDs, or internal fields.
- Do not invent or add facts.
- Normally speak all returned headlines unless the user requested a smaller number.
- Output only the spoken headlines and the required sound-effect tags.
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.State == ToolRequestState.Initial)
            {
                _logger.LogInformation("News request received; returning preamble before fetching headlines.");

                return ToolResult.Preamble(
                    Name,
                    "Here are the latest news headlines. {sound:news-intro}",
                    request.WithState(ToolRequestState.PreambleComplete));
            }

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

            var commands = JToken.FromObject(articles)
                .OfType<JObject>()
                .Select(article => article["Title"]?.Value<string>()?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => new LlmCommand(
                    """
                    Convert the supplied news headline into one natural spoken headline.

                    - Speak only the headline.
                    - Preserve the important facts.
                    - Do not mention the source.
                    - Do not summarize or add information.
                    - Do not say "Title" or "Summary".
                    - Start with {sound:news-breaking}.
                    - Output only the speech and sound tag.
                    """,
                    title!,
                    32))
                .ToList();

            if (commands.Count == 0)
                return ToolResult.Failed(Name, "No news headlines are available.");

            return ToolResult.SuccessfulWithLlmCommands(
                Name,
                commands,
                "News headlines retrieved.");
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
