namespace AIRadio.Server.Models.Tools
{
    public interface ITool
    {
        string Name { get; }

        string GetLlmInstructions();

        /// <summary>
        /// Instructions for turning a successful tool result into natural spoken text.
        /// These instructions are only supplied during the response round.
        /// </summary>
        string GetLlmResponseInstructions() => string.Empty;

        // Compact description used by the first conversation-routing round.
        // The default extracts the first descriptive line from the detailed
        // instructions so individual tools do not need a second prompt API.
        string GetLlmSummary()
        {
            var lines = GetLlmInstructions()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
                return Name;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line.EndsWith(':'))
                    continue;

                if (line.StartsWith("Purpose:", StringComparison.OrdinalIgnoreCase))
                    return line[8..].Trim();

                if (!line.Equals("Examples:", StringComparison.OrdinalIgnoreCase))
                    return line;
            }

            return Name;
        }

        Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default);
    }
}
