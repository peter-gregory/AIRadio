using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Tools;

public sealed class ConversationTool : ITool
{
    public string Name => "conversation";
    public string Intent => "Respond to ordinary conversation without using an application tool.";

    public string GetLlmInstructions() => """
CONVERSATION
Answer requests that do not require current, external, stored, or radio-state data.
Parameters: none.
Example: "Tell me a joke." -> {tool:conversation}
""";

    public string GetLlmResponseInstructions()
    {
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12
            ? "Good morning."
            : hour < 18
                ? "Good afternoon."
                : "Good evening.";

        return $"""
CONVERSATION RESPONSE
- Respond directly and naturally to the user's request.
- Be friendly and concise for speech.
- Do not mention this tool or the tool process.
- Do not invent current or external facts.
- If the user asks you to speak a greeting, speak exactly "{greeting}".
- Do not choose a different greeting.
- When the request is simply to speak a greeting, output only the greeting.
""";
    }

    public Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                "Conversation response requested."));
    }
}