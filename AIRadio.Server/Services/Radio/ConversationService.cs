using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;

namespace AIRadio.Server.Services.Radio
{
    public interface IConversationService
    {
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
        private bool _awaitingToolInput;

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
            _awaitingToolInput = false;
            _queue.Resume();
        }

        private async Task ProcessRequestAsync(ConversationRequest request, CancellationToken cancellationToken)
        {
            var conversationComplete = false;

            try
            {
                var response = _awaitingToolInput
                    ? await _llama.ContinueToolAsync(request.Text, cancellationToken)
                    : await _llama.StartConversationAsync(request.Text, cancellationToken);

                conversationComplete = await ProcessLlamaResponseAsync(response, cancellationToken);

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
                    _awaitingToolInput = false;
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

        private async Task<bool> ProcessLlamaResponseAsync(LlamaResponse response, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.HasToolRequests)
            {
                _awaitingToolInput = false;
                if (response.HasSpeech)
                    await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);
                return true;
            }

            // A tool request containing !required! is an interactive request,
            // not an executable request. Speak the question and keep the
            // conversation alive for the next utterance.
            if (response.ToolRequests.Any(request => request.HasMissingRequiredArguments))
            {
                _awaitingToolInput = true;
                _logger.LogInformation(
                    "Tool input required for {Tools}: {Arguments}",
                    string.Join(", ", response.ToolRequests.Select(request => request.Name)),
                    string.Join(", ", response.ToolRequests.SelectMany(request => request.MissingRequiredArguments)));

                if (response.HasSpeech)
                    await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);

                return false;
            }

            _awaitingToolInput = false;
            if (response.HasSpeech)
                await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);

            return await ExecuteToolsAsync(response.ToolRequests, cancellationToken);
        }

        private async Task<bool> ExecuteToolsAsync(IEnumerable<ToolRequest> toolRequests, CancellationToken cancellationToken)
        {
            var requests = toolRequests.ToList();
            if (requests.Count == 0) return true;

            var resultTasks = requests.Select(request => ExecuteToolAsync(request, cancellationToken)).ToArray();
            var results = await Task.WhenAll(resultTasks);
            cancellationToken.ThrowIfCancellationRequested();

            // Some tools can provide deterministic speech directly. When a single
            // tool supplies an exact prompt, skip the expensive second LLM round.
            if (results.Count == 1 &&
                results[0].Success &&
                !string.IsNullOrWhiteSpace(results[0].ExactPrompt))
            {
                await _audioManager.PlaySpeechAsync(results[0].ExactPrompt, cancellationToken);
                return true;
            }

            var response = await _llama.ContinueAsync(results, cancellationToken);
            return await ProcessLlamaResponseAsync(response, cancellationToken);
        }

        private async Task<ToolResult> ExecuteToolAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(request.Name))
                return ToolResult.Failed("tool_dispatcher", "The tool request did not specify a tool name.");

            if (!_tools.TryGetValue(request.Name, out var tool))
                return ToolResult.Failed("tool_dispatcher", $"Tool '{request.Name}' is not available.");

            try
            {
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
