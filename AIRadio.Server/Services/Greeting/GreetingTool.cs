using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Greeting;

public sealed class GreetingTool : ITool
{
    public string Name => "greeting";
    public string Intent => "Speak a simple time-based greeting.";
    public string GetLlmInstructions() => """
GREETING TOOL
Speak a simple time-based greeting.

Parameters:
- None.

The application determines the greeting from the current local time:
- 5:00 AM through 11:59 AM: Good morning
- 12:00 PM through 5:59 PM: Good afternoon
- 6:00 PM through 4:59 AM: Good evening

This tool is deterministic and does not require an LLM response.
""";

    public Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var hour = DateTimeOffset.Now.Hour;
        var greeting = hour switch
        {
            >= 5 and < 12 => "Good morning.",
            >= 12 and < 18 => "Good afternoon.",
            _ => "Good evening."
        };

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                "Greeting generated.",
                exactPrompt: greeting,
                complete: true));
    }
}
