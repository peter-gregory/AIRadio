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

    public string GetLlmResponseInstructions() => """
CONVERSATION RESPONSE
- Respond directly to the user's original request.
- Be human-like, friendly, concise, and natural for speech.
- Do not mention this tool or the tool process.
- Do not invent current or external facts.
""";

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
