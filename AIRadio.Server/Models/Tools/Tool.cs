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

        /// <summary>
        /// True when the original user utterance should be parsed for tool
        /// arguments before the tool is executed.
        /// </summary>
        bool HasParameters => false;

        /// <summary>
        /// Model-facing request syntax used during the argument parsing round.
        /// </summary>
        string GetLlmRequestTemplate() => $"{{tool:{Name}}}";

        string GetLlmInstructions();

        /// <summary>
        /// Summary used by the tool execution round to teach the model how to use this tool.
        /// </summary>
        string GetLlmSummary() => GetLlmInstructions();

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