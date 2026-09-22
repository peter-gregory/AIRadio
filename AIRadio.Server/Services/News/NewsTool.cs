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
The news data is already written for speech.

- Speak each returned article exactly as supplied.
- Each article consists of its exact headline followed by its exact summary.
- Do not rewrite, summarize, shorten, expand, interpret, or reorder the supplied text.
- Start each article with exactly {sound:news-breaking}.
- Do not speak field labels such as "Headline" or "Summary".
- Do not read URLs, timestamps, IDs, source names, or other metadata aloud.
- Do not add facts or commentary.
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

        var articles = result.Articles
            .Where(article => !string.IsNullOrWhiteSpace(article.Title))
            .ToList();

        if (articles.Count == 0)
            return ToolResult.Failed(Name, "No news headlines are available.");

        var report = string.Join(
            " ",
            articles.Select(article =>
            {
                var headline = article.Title.Trim();
                var summary = article.Summary?.Trim();

                return string.IsNullOrWhiteSpace(summary)
                    ? $"{{sound:news-breaking}} {headline}."
                    : $"{{sound:news-breaking}} {headline}. {summary}";
            }));

        return ToolResult.Successful(
            Name,
            "News headlines retrieved.",
            data: null,
            exactPrompt: report,
            complete: true,
            completionPrompt: "That's all the news for now.");
    }
}