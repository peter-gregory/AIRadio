namespace AIRadio.Server.Services.Radio
{
    public interface IIntentService
    {
        Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default);

        Task CancelAsync(
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

            _queue = new AsyncWorkQueue<string>();
            _queue.Start(ProcessIntentAsync);
        }

        public Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_queue.TryEnqueue(text))
            {
                _logger.LogDebug(
                    "Intent request rejected because the intent queue is not accepting work.");
            }

            return Task.CompletedTask;
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

                        return;
                    }
                }

                await _conversationService.ProcessAsync(
                    text,
                    cancellationToken);
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
