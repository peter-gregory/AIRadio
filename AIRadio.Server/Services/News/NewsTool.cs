using AIRadio.Server.Models.LLama;
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
- Speak each returned news item as a headline followed by its supporting summary.
- The summary is important context; do not omit it when supplied.
- Preserve the meaning of each headline and summary, but rewrite slightly when needed for natural speech.
- Start every news item with {sound:news-breaking}.
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

        var commands = result.Articles
            .Where(article => !string.IsNullOrWhiteSpace(article.Title))
            .Select(article => new LlmCommand(
                """
                Convert the supplied news headline and summary into one natural spoken news item.

                The supplied headline and summary are authoritative.
                Speak the complete headline first, then briefly summarize the supplied
                summary so the listener understands the story. The summary is supporting
                context for the headline; do not merely repeat the headline.

                Rules:
                - Start with exactly one {sound:news-breaking} tag.
                - {sound:news-breaking} is the only sound tag permitted.
                - Speak the complete headline.
                - If a summary is supplied, follow it with one or two concise sentences
                  explaining the important information from that summary.
                - Preserve the facts and meaning of the supplied headline and summary.
                - You may make minor wording changes needed for natural speech.
                - Do not invent facts, interpretations, causes, or details.
                - Do not mention the source.
                - Do not say "Title", "Summary", or "Description".
                - Never output a sound tag without spoken news text.
                - If the summary is empty, speak the headline only.
                - Output only the sound tag and spoken news item.
                """,
                $"Headline: {article.Title}\nSummary: {article.Summary ?? string.Empty}",
                64))
            .ToList();

        if (commands.Count == 0)
            return ToolResult.Failed(Name, "No news headlines are available.");

        commands.Add(new LlmCommand(
            """
            Say the supplied closing sentence naturally for a radio news briefing.
            - Speak only the supplied sentence.
            - Do not add or change any words.
            - Do not output a sound tag.
            """,
            "And that's all the news for now.",
            16));

        return ToolResult.SuccessfulWithLlmCommands(
            Name,
            commands,
            "News headlines retrieved.");
    }
}
