namespace AIRadio.Server.Models.Tools
{
    public interface ITool
    {
        string Name { get; }

        /// <summary>
        /// Short intent-training text used by the first-round tool catalog.
        /// Keep this focused on recognizing when this tool should be selected.
        /// </summary>
        string Intent { get; }

        string GetLlmInstructions();

        /// <summary>
        /// Instructions for turning a successful tool result into natural spoken text.
        /// These instructions are only supplied during the response round.
        /// </summary>
        string GetLlmResponseInstructions() => string.Empty;

        Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default);
    }
}