using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;

namespace AIRadio.Server.Services.Radio
{
    public interface IConversationService
    {
        Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default);

        Task CancelAsync(
            CancellationToken cancellationToken = default);
    }

    public sealed class ConversationService :
        IConversationService,
        IAsyncDisposable
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

            _tools = tools.ToDictionary(
                tool => tool.Name,
                StringComparer.OrdinalIgnoreCase);

            _queue =
                new AsyncWorkQueue<ConversationRequest>();

            _queue.Start(
                ProcessRequestAsync);
        }

        public Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                text);

            var request =
                new ConversationRequest(
                    text);

            if (!_queue.TryEnqueue(
                    request))
            {
                _logger.LogDebug(
                    "Conversation request rejected because " +
                    "conversation processing is cancelled.");

                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        public async Task CancelAsync(
            CancellationToken cancellationToken = default)
        {
            await _queue.CancelAsync(
                cancellationToken);
        }

        public void Resume()
        {
            _queue.Resume();
        }

        private async Task ProcessRequestAsync(
            ConversationRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(
                "Processing conversation request.");

            LlamaResponse response;

            if (_llama.IsInitialized)
            {
                response =
                    await _llama.ContinueAsync(
                        request.Text,
                        cancellationToken);
            }
            else
            {
                response =
                    await _llama.StartConversationAsync(
                        request.Text,
                        cancellationToken);
            }

            await ProcessLlamaResponseAsync(
                response,
                cancellationToken);
        }

        private async Task ProcessLlamaResponseAsync(
            LlamaResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * Audio is queued, not played synchronously.
             */
            if (response.HasSoundEvents)
            {
                foreach (var soundEvent in response.SoundEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(
                            soundEvent))
                    {
                        continue;
                    }

                    await _audioManager.PlaySoundAsync(
                        soundEvent,
                        cancellationToken);
                }
            }

            if (response.HasSpeech)
            {
                await _audioManager.PlaySpeechAsync(
                    response.SpokenText,
                    cancellationToken);
            }

            /*
             * Tool execution begins immediately. AudioManager continues
             * processing the previously queued speech independently.
             */
            if (response.HasToolRequests)
            {
                await ExecuteToolsAsync(
                    response.ToolRequests,
                    cancellationToken);
            }
        }

        private async Task ExecuteToolsAsync(
            IEnumerable<ToolRequest> toolRequests,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                toolRequests);

            var results =
                new List<ToolResult>();

            foreach (var request in toolRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(
                        request.Name))
                {
                    _logger.LogWarning(
                        "Llama returned a tool request with no tool name.");

                    continue;
                }

                if (!_tools.TryGetValue(
                        request.Name,
                        out var tool))
                {
                    _logger.LogWarning(
                        "Llama requested unknown tool {ToolName}.",
                        request.Name);

                    results.Add(
                        ToolResult.Failed(
                            request.Name,
                            $"Tool '{request.Name}' is not available."));

                    continue;
                }

                try
                {
                    _logger.LogDebug(
                        "Executing tool {ToolName}.",
                        request.Name);

                    var result =
                        await tool.ExecuteAsync(
                            request,
                            cancellationToken);

                    if (result is null)
                    {
                        _logger.LogWarning(
                            "Tool {ToolName} returned null.",
                            request.Name);

                        results.Add(
                            ToolResult.Failed(
                                request.Name,
                                $"Tool '{request.Name}' returned no result."));

                        continue;
                    }

                    results.Add(result);
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

                    results.Add(
                        ToolResult.Failed(
                            request.Name,
                            ex.Message));
                }
            }

            if (results.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            /*
             * Continue the same conversation. Any resulting speech or
             * sound events are appended to the AudioManager queue.
             */
            var response =
                await _llama.ContinueAsync(
                    results,
                    cancellationToken);

            await ProcessLlamaResponseAsync(
                response,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _queue.DisposeAsync();
        }

        private sealed record ConversationRequest(
            string Text);
    }
}
