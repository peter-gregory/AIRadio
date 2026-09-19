using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Radio
{
    public interface IConversationService
    {
        bool IsWaitingForInput { get; }

        Task ProcessAsync(string text, CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ConversationService : IConversationService, IAsyncDisposable
    {
        private readonly ILogger<ConversationService> _logger;
        private readonly IConversationLlamaClient _llama;
        private readonly IAudioManager _audioManager;
        private readonly IReadOnlyDictionary<string, ITool> _tools;
        private readonly AsyncWorkQueue<ConversationRequest> _queue;
        private ToolRequest? _pendingToolRequest;

        public bool IsWaitingForInput => _pendingToolRequest is not null;

        public ConversationService(ILogger<ConversationService> logger, IConversationLlamaClient llama, IAudioManager audioManager, IEnumerable<ITool> tools)
        {
            _logger = logger;
            _llama = llama;
            _audioManager = audioManager;
            ArgumentNullException.ThrowIfNull(tools);
            _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
            _queue = new AsyncWorkQueue<ConversationRequest>();
            _queue.Start(ProcessRequestAsync);
        }

        public Task ProcessAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queue.TryEnqueue(new ConversationRequest(text)))
                _logger.LogDebug("Conversation request rejected because the conversation queue is not accepting work.");
            return Task.CompletedTask;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.CancelAsync(CancellationToken.None);
            await _audioManager.CancelAsync(CancellationToken.None);
            _pendingToolRequest = null;
            _queue.Resume();
        }

        private async Task ProcessRequestAsync(ConversationRequest request, CancellationToken cancellationToken)
        {
            var conversationComplete = false;

            try
            {
                if (_pendingToolRequest is not null)
                {
                    var pendingRequest = ApplyPendingToolInput(request.Text);
                    _pendingToolRequest = null;
                    conversationComplete = await ExecutePendingToolAsync(pendingRequest, cancellationToken);
                }
                else
                {
                    var response = await _llama.StartConversationAsync(request.Text, cancellationToken);
                    conversationComplete = await ProcessLlamaResponseAsync(response, cancellationToken);
                }

                await _audioManager.EndUtteranceAsync(cancel: false, cancellationToken);
                _logger.LogInformation(
                    conversationComplete
                        ? "Llama conversation is complete."
                        : "Llama conversation is waiting for additional tool input.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Conversation processing cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation processing failed.");
                conversationComplete = true;
            }
            finally
            {
                if (conversationComplete)
                {
                    _pendingToolRequest = null;
                    try
                    {
                        await _llama.ResetAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reset the Llama conversation.");
                    }
                }
            }
        }

        private async Task<bool> ExecutePendingToolAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var result = await ExecuteToolAsync(request, cancellationToken);

            return await HandleToolResultAsync(result, cancellationToken);
        }

        private ToolRequest ApplyPendingToolInput(string text)
        {
            if (_pendingToolRequest is null)
                throw new InvalidOperationException("No pending tool request exists.");

            var arguments = (JObject)_pendingToolRequest.Arguments.DeepClone();
            var missing = arguments.Properties()
                .FirstOrDefault(property =>
                    string.Equals(
                        property.Value.Value<string>(),
                        ToolRequest.RequiredValue,
                        StringComparison.Ordinal));

            if (missing is null)
                throw new InvalidOperationException("The pending tool request has no missing parameter.");

            arguments[missing.Name] = text.Trim();

            return new ToolRequest
            {
                Name = _pendingToolRequest.Name,
                Arguments = arguments
            };
        }

        private async Task<bool> ProcessLlamaResponseAsync(LlamaResponse response, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.HasToolRequests)
            {
                if (response.HasSpeech)
                    await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);
                return true;
            }

            return await ExecuteToolsAsync(
                response.ToolRequests,
                response.SpokenText,
                response.HasSpeech,
                cancellationToken);
        }

        private async Task<bool> ExecuteToolsAsync(
            IEnumerable<ToolRequest> toolRequests,
            string? preToolSpeech,
            bool hasPreToolSpeech,
            CancellationToken cancellationToken)
        {
            var requests = toolRequests.ToList();
            _logger.LogInformation("Processing " + requests.Count + " tool requests");
            if (requests.Count == 0) return true;

            var resultTasks = requests.Select(request => ExecuteToolAsync(request, cancellationToken)).ToArray();
            var results = await Task.WhenAll(resultTasks);
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Count == 1)
            {
                var handled = await HandleToolResultAsync(results[0], cancellationToken);
                if (!handled)
                    return false;

                if (results[0].Status == ToolResultStatus.Preamble)
                    return true;
            }

            if (hasPreToolSpeech && !string.IsNullOrWhiteSpace(preToolSpeech))
                await _audioManager.PlaySpeechAsync(preToolSpeech, cancellationToken);

            var response = await _llama.ContinueAsync(results, cancellationToken);
            return await ProcessLlamaResponseAsync(response, cancellationToken);
        }

        private async Task<bool> HandleToolResultAsync(
            ToolResult result,
            CancellationToken cancellationToken)
        {
            switch (result.Status)
            {
                case ToolResultStatus.MissingParameter:
                    if (!string.IsNullOrWhiteSpace(result.ExactPrompt))
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);

                    if (result.PendingRequest is not null)
                    {
                        _pendingToolRequest = result.PendingRequest;
                        return false;
                    }

                    break;

                case ToolResultStatus.Preamble:
                    if (!string.IsNullOrWhiteSpace(result.ExactPrompt))
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);

                    if (result.PendingRequest is not null)
                    {
                        var continuation = await ExecuteToolAsync(result.PendingRequest, cancellationToken);
                        if (continuation.Status == ToolResultStatus.Preamble)
                            throw new InvalidOperationException(
                                $"Tool '{result.ToolName}' returned a repeated preamble without advancing state.");

                        return await HandleToolResultAsync(continuation, cancellationToken);
                    }

                    return true;

                case ToolResultStatus.Result:
                    if (result.ExactPrompt is not null && result.Success)
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);
                    return true;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            return false;
        }

        private async Task<ToolResult> ExecuteToolAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Processing tool " + request.Name);

            if (string.IsNullOrWhiteSpace(request.Name))
                return ToolResult.Failed("tool_dispatcher", "The tool request did not specify a tool name.");

            if (!_tools.TryGetValue(request.Name, out var tool))
                return ToolResult.Failed("tool_dispatcher", $"Tool '{request.Name}' is not available.");

            try
            {
                _logger.LogInformation("Execute tool " + tool.Name);
                var result = await tool.ExecuteAsync(request, cancellationToken);
                return result ?? ToolResult.Failed(request.Name, $"Tool '{request.Name}' returned no result.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool {ToolName} failed.", request.Name);
                return ToolResult.Failed(request.Name, ex.Message);
            }
        }

        public ValueTask DisposeAsync() => _queue.DisposeAsync();

        private sealed record ConversationRequest(string Text);
    }
}