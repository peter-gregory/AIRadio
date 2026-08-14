using AIRadio.Server.Services.AI;

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

    public sealed class IntentService :
    IIntentService,
    IAsyncDisposable
    {
        private readonly ILogger<IntentService> _logger;
        private readonly ILlamaIntentClient _intentClient;
        private readonly IConversationService _conversationService;

        private readonly AsyncWorkQueue<string> _queue;

        private bool _disposed;

        public IntentService(
            ILogger<IntentService> logger,
            ILlamaIntentClient intentClient,
            IConversationService conversationService)
        {
            _logger = logger ??
                throw new ArgumentNullException(nameof(logger));

            _intentClient = intentClient ??
                throw new ArgumentNullException(nameof(intentClient));

            _conversationService = conversationService ??
                throw new ArgumentNullException(nameof(conversationService));

            _queue =
                new AsyncWorkQueue<string>();

            _queue.Start(
                ProcessIntentAsync);
        }

        public Task ProcessAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                text);

            cancellationToken.ThrowIfCancellationRequested();

            /*
             * Intent processing is deliberately queued.
             *
             * The caller does not wait for intent detection to complete.
             * This allows STT/event processing to continue independently.
             */
            if (!_queue.TryEnqueue(
                    text))
            {
                /*
                 * This is expected when a barge-in cancellation is
                 * already in progress.
                 */
                _logger.LogDebug(
                    "Intent request rejected because the intent queue " +
                    "is not accepting work.");

                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        public async Task CancelAsync(
            CancellationToken cancellationToken = default)
        {
            /*
             * AsyncWorkQueue establishes the cancellation barrier:
             *
             * 1. Reject new intent requests.
             * 2. Clear pending requests.
             * 3. Cancel the active intent operation.
             * 4. Wait for it to finish.
             * 5. Leave the queue blocked.
             *
             * The caller's cancellation token only controls how long this
             * caller waits. It cannot interrupt the underlying cancellation.
             */
            await _queue.CancelAsync(
                cancellationToken);

            _logger.LogDebug(
                "Intent service cancellation completed.");
        }

        private async Task ProcessIntentAsync(
            string text,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug(
                    "Processing intent text: {Text}",
                    text);

                var result =
                    await _intentClient.CheckBargeInAsync(
                        text,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (result.IsBargeIn)
                {
                    await HandleBargeInAsync(
                        result,
                        cancellationToken);

                    return;
                }

                /*
                 * Normal speech continues to the conversation service.
                 *
                 * ConversationService owns the conversation/model queue,
                 * so IntentService does not wait for the model response.
                 */
                await _conversationService.ProcessAsync(
                    text,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                /*
                 * Cancellation is expected during barge-in.
                 *
                 * Do not propagate it out of the worker.
                 */
                _logger.LogDebug(
                    "Intent processing cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Intent processing failed for text: {Text}",
                    text);
            }
        }

        private async Task HandleBargeInAsync(
            BargeInResult result,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Barge-in detected. " +
                "Confidence={Confidence}, " +
                "Command={Command}, " +
                "Reason={Reason}.",
                result.Confidence,
                result.Command,
                result.Reason);

            /*
             * The IntentService is responsible for recognizing the
             * barge-in. The higher-level conversation/radio service owns
             * the actual response to that command.
             */
            await _conversationService.HandleBargeInAsync(
                result,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await _queue.StopAsync(
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error stopping IntentService queue.");
            }

            await _queue.DisposeAsync();
        }
    }
}
