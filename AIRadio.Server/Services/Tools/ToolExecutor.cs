using AIRadio.Server.Models.Tools;
using System.Xml.Linq;

namespace AIRadio.Server.Services.Tools
{
    public interface IToolExecutor
    {
        Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ToolResult>> ExecuteAsync(
            IReadOnlyList<ToolRequest> requests,
            CancellationToken cancellationToken = default);
    }

    public sealed class ToolExecutor : IToolExecutor
    {
        private readonly ILogger<ToolExecutor> _logger;

        private readonly Dictionary<string, ITool> _tools;
        public string Name =>
            "tool_dispatcher";

        public ToolExecutor(
            ILogger<ToolExecutor> logger,
            IEnumerable<ITool> tools)
        {
            _logger = logger;

            _tools =
                tools.ToDictionary(
                    x => x.Name,
                    StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ToolResult.Failed(
                    Name,
                    "The tool request did not specify a tool name.");
            }

            if (!_tools.TryGetValue(
                    request.Name,
                    out var tool))
            {
                _logger.LogWarning(
                    "Unknown tool requested: {ToolName}",
                    request.Name);

                return ToolResult.Failed(
                    Name,
                    $"Unknown tool '{request.Name}'.");
            }

            try
            {
                _logger.LogDebug(
                    "Executing tool {ToolName}.",
                    request.Name);

                return await tool.ExecuteAsync(
                    request,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Tool {ToolName} failed.",
                    request.Name);

                return ToolResult.Failed(
                    request.Name,
                    $"Tool '{request.Name}' failed.");
            }
        }

        public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
            IReadOnlyList<ToolRequest> requests,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requests);

            var results =
                new List<ToolResult>(
                    requests.Count);

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result =
                    await ExecuteAsync(
                        request,
                        cancellationToken);

                results.Add(result);
            }

            return results;
        }
    }

}
