namespace AIRadio.Server.Services.Radio
{
    public interface IIntentService
    {
        Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default);

        Task CancelAsync(
            CancellationToken cancellationToken = default);

        Task<Guid> ProcessAlarmAsync(
            string text,
            CancellationToken cancellationToken = default);
    }

    public sealed class IntentService : IIntentService, IAsyncDisposable
    {
        private readonly ILogger<IntentService> _logger;
        private readonly IRegexIntentParser _intentParser;
        private readonly IConversationService _conversationService;
        private readonly AsyncWorkQueue<string> _queue;

        public IntentService(
            ILogger<IntentService> logger,
            IRegexIntentParser intentParser,
            IConversationService conversationService)
        {
            _logger = logger;
            _intentParser = intentParser;
            _conversationService = conversationService;

            _logger.LogInformation("Starting IntentService");

            _queue = new AsyncWorkQueue<string>();
            _queue.Start(ProcessIntentAsync);

            _logger.LogInformation("Finished IntentService");
        }

        public Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Processing text prompt: " + text);

            if (!_queue.TryEnqueue(text))
            {
                _logger.LogDebug(
                    "Intent request rejected because the intent queue is not accepting work.");
            }

            return Task.CompletedTask;
        }

        public Task<Guid> ProcessAlarmAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Starting alarm command directly: " + text);
            return _conversationService.StartAlarmAsync(text, cancellationToken);
        }

        public async Task CancelAsync(
            CancellationToken cancellationToken = default)
        {
            await _queue.CancelAsync(cancellationToken);

            // Intent processing continues after a barge-in. The queue itself
            // must therefore be reopened after its cancellation completes.
            _queue.Resume();
        }

        private async Task ProcessIntentAsync(
            string text,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Processing intent for " + text);

                // A pending conversation takes precedence over the normal
                // intent parser. The user's response is an answer to the
                // conversation's outstanding question, not a new intent.
                if (_conversationService.IsWaitingForInput)
                {
                    _logger.LogInformation(
                        "Conversation is waiting for input; forwarding text directly to ConversationService: {Text}",
                        text);

                    await _conversationService.ProcessAsync(
                        text,
                        cancellationToken);

                    return;
                }

                var match = _intentParser.Match(text);

                if (match is not null)
                {
                    if (string.Equals(
                            match.Intent,
                            "BargeIn",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Barge-in detected by regex rule {RuleName}: {Text}.",
                            match.RuleName,
                            text);

                        await _conversationService.CancelAsync(
                            cancellationToken);

                        await _conversationService.ProcessAsync(
                            text,
                            cancellationToken);

                        return;
                    }

                    if (string.Equals(
                            match.Intent,
                            "WakeUp",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Wake-up detected by regex rule {RuleName}: {Text}.",
                            match.RuleName,
                            text);

                        // Give immediate audible feedback when a new conversation
                        // starts, before the intent LLM has a chance to respond.
                        if (_conversationService.State == ConversationState.Idle)
                        {
                            await _conversationService.PlayWakeAcknowledgementAsync(
                                cancellationToken);
                        }

                        await _conversationService.ProcessAsync(
                            match.Text,
                            cancellationToken);
                    }
                    else
                    {
                        _logger.LogInformation("Intent is unknown for text " + text);
                    }
                }
                else
                {
                    _logger.LogInformation("No match for intent " + text);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Intent processing cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Intent processing failed for text: {Text}",
                    text);
            }
        }

        public ValueTask DisposeAsync()
        {
            return _queue.DisposeAsync();
        }
    }
}
