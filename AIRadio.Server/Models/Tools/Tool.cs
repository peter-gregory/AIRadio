namespace AIRadio.Server.Models.Tools
{
    public interface ITool
    {
        string Name { get; }

        string GetLlmInstructions();

        Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default);
    }
}
