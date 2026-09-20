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

- Start with a brief natural introduction.
- Summarize the returned headlines in spoken language.
- Mention the most important headlines; normally cover all returned headlines when they are concise enough.
- Use the article summaries only to add useful context to a headline.
- Do not read URLs, timestamps, IDs, source names, or JSON fields aloud.
- Do not invent facts or add information not present in the result.
- Do not output a tool request, JSON, Markdown, or an internal explanation.
- You may use {sound:news-intro} at the beginning of the briefing.
- Use {sound:news-breaking} only when a returned headline describes genuinely urgent or breaking news.
""";

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _newsService.GetHeadlinesAsync(cancellationToken);
        return ToolResult.Successful(
            Name,
            "News retrieved successfully.",
            result);
    }
}
