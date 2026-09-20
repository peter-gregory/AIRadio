using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.News;

public sealed class NewsTool : ITool
{
    private readonly INewsService _newsService;

    public NewsTool(INewsService newsService)
    {
        _newsService = newsService;
    }

    public bool HasParameters => false;
    public string GetLlmRequestTemplate() => "{tool:news}";
    public string Name => "news";
    public string Intent => "Get the current top national news headlines.";

    public string GetLlmInstructions() => """
NEWS
Get the current top national news headlines.

This is a simple broadcast-style news briefing for the radio.
It always returns the current top national headlines.
It does not accept a location, category, topic, or headline-count parameter.

Examples:
"Give me the news" -> {tool:news}
"What's in the news?" -> {tool:news}
"Give me a news report" -> {tool:news}
""";

    public string GetLlmResponseInstructions() => """
NEWS RESPONSE
Present the returned headlines as a short, conversational radio news briefing.

- The preamble has already been spoken before this response. Do not repeat or add an introduction.
- Speak only the returned news headlines.
- Preserve the meaning of each headline, but rewrite slightly when needed for natural speech.
- Start every headline with {sound:news-breaking}.
- Do not combine multiple headlines into one sentence.
- Do not read URLs, timestamps, IDs, source names, or JSON fields aloud.
- Do not invent facts or add information not present in the result.
- Do not output a tool request, JSON, Markdown, or an internal explanation.
""";

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.State == ToolRequestState.Initial)
        {
            return ToolResult.Preamble(
                Name,
                "Here are the latest news headlines {sound:news-intro}",
                request.WithState(ToolRequestState.PreambleComplete));
        }

        var result = await _newsService.GetHeadlinesAsync(cancellationToken);
        return ToolResult.Successful(
            Name,
            "News retrieved successfully.",
            result);
    }
}
