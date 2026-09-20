using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Tools
{
    public interface IToolExecutor
    {
        string GetLlmInstructions();
        string GetLlmInstructions(IEnumerable<string> toolNames);
        string GetLlmResponseInstructions(IEnumerable<string> toolNames);
        string GetLlmCatalog();
        bool HasParameters(string toolName);
        IReadOnlyList<string> GetToolNames();

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

        public string Name => "tool_dispatcher";

        public ToolExecutor(
            ILogger<ToolExecutor> logger,
            IEnumerable<ITool> tools)
        {
            _logger = logger;

            _logger.LogInformation("Construct ToolExecutor");

            _tools = tools.ToDictionary(
                x => x.Name,
                StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Construct fininshed ToolExecutor");
        }

        public IReadOnlyList<string> GetToolNames() =>
            _tools.Keys
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

        public bool HasParameters(string toolName) =>
            _tools.TryGetValue(toolName, out var tool) && tool.HasParameters;

        public string GetLlmCatalog() =>
            string.Join(
                '\n',
                _tools.Values
                    .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                    .Select(static tool => $"{{tool:{tool.Name}}} - {tool.Intent}"));

        public string GetLlmInstructions() =>
            string.Join(
                '\n',
                _tools.Values
                    .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                    .Select(static tool => tool.GetLlmSummary())
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));

        public string GetLlmInstructions(IEnumerable<string> toolNames)
        {
            ArgumentNullException.ThrowIfNull(toolNames);

            var requested = new HashSet<string>(
                toolNames.Where(static name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            return string.Join(
                '\n',
                _tools.Values
                    .Where(tool => requested.Contains(tool.Name))
                    .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                    .Select(static tool =>
                        $"{tool.GetLlmInstructions()}\nREQUEST FORMAT\n{tool.GetLlmRequestTemplate()}")
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));
        }

        public string GetLlmResponseInstructions(IEnumerable<string> toolNames)
        {
            ArgumentNullException.ThrowIfNull(toolNames);

            var requested = new HashSet<string>(
                toolNames.Where(static name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            return string.Join(
                '\n',
                _tools.Values
                    .Where(tool => requested.Contains(tool.Name))
                    .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                    .Select(static tool => tool.GetLlmResponseInstructions())
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));
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

            var results = new List<ToolResult>(requests.Count);

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ExecuteAsync(
                    request,
                    cancellationToken);

                results.Add(result);
            }

            return results;
        }
    }
}
