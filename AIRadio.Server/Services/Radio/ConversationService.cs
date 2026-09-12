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

        public ConversationService(
            ILogger<ConversationService> logger,
            IConversationLlamaClient llama,
            IAudioManager audioManager,
            IEnumerable<ITool> tools)
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
            _queue.Resume();
        }

        private async Task ProcessRequestAsync(
            ConversationRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = _llama.IsInitialized
                    ? await _llama.ContinueAsync(request.Text, cancellationToken)
                    : await _llama.StartConversationAsync(request.Text, cancellationToken);

                await ProcessLlamaResponseAsync(response, cancellationToken);

                /*
                 * ProcessLlamaResponseAsync may enqueue multiple sentences and
                 * may recursively process tool-result responses. Only after
                 * the complete logical response has been produced do we close
                 * the native audio utterance.
                 */
                await _audioManager.EndUtteranceAsync(
                    cancel: false,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Conversation processing cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation processing failed.");
            }
        }

        private async Task ProcessLlamaResponseAsync(
            LlamaResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var soundEvent in response.SoundEvents)
            {
                if (!string.IsNullOrWhiteSpace(soundEvent))
                    await _audioManager.PlaySoundAsync(soundEvent, cancellationToken);
            }

            if (response.HasSpeech)
                await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);

            if (response.HasToolRequests)
                await ExecuteToolsAsync(response.ToolRequests, cancellationToken);
        }

        private async Task ExecuteToolsAsync(
            IEnumerable<ToolRequest> toolRequests,
            CancellationToken cancellationToken)
        {
            var results = new List<ToolResult>();

            foreach (var request in toolRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    _logger.LogWarning("Llama returned a tool request with no tool name.");
                    continue;
                }

                if (!_tools.TryGetValue(request.Name, out var tool))
                {
                    results.Add(ToolResult.Failed(
                        request.Name,
                        $"Tool '{request.Name}' is not available."));
                    continue;
                }

                try
                {
                    var result = await tool.ExecuteAsync(request, cancellationToken);
                    results.Add(result ?? ToolResult.Failed(
                        request.Name,
                        $"Tool '{request.Name}' returned no result."));
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tool {ToolName} failed.", request.Name);
                    results.Add(ToolResult.Failed(request.Name, ex.Message));
                }
            }

            if (results.Count == 0)
                return;

            var response = await _llama.ContinueAsync(results, cancellationToken);
            await ProcessLlamaResponseAsync(response, cancellationToken);
        }

        public ValueTask DisposeAsync() => _queue.DisposeAsync();

        private sealed record ConversationRequest(string Text);
    }
}
