namespace AIRadio.Server.Services.Radio
{
    public sealed class AsyncWorkQueue<T> : IAsyncDisposable
    {
        private readonly object _sync = new();

        private readonly Queue<T> _queue = new();

        private readonly SemaphoreSlim _signal =
            new(0);

        private readonly CancellationTokenSource _shutdown =
            new();

        private CancellationTokenSource? _currentItemCts;

        private TaskCompletionSource? _currentItemCompletion;

        private TaskCompletionSource? _cancellationCompletion;

        private Task? _workerTask;

        private QueueState _state =
            QueueState.Accepting;

        private bool _disposed;

        public QueueState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        public bool IsAccepting =>
            State == QueueState.Accepting;

        public bool IsCancelling =>
            State == QueueState.Cancelling;

        public bool IsBlocked =>
            State == QueueState.Blocked;

        public bool IsStopped =>
            State == QueueState.Stopped;

        /// <summary>
        /// Gets whether the queue contains no pending or active work.
        ///
        /// This only describes work managed by this queue. It does not
        /// indicate whether downstream processing, such as audio playback,
        /// has completed.
        /// </summary>
        public bool IsIdle
        {
            get
            {
                lock (_sync)
                {
                    return _queue.Count == 0 &&
                           _currentItemCompletion is null;
                }
            }
        }

        /// <summary>
        /// Starts the queue worker.
        /// </summary>
        public void Start(
            Func<T, CancellationToken, Task> processor)
        {
            ArgumentNullException.ThrowIfNull(
                processor);

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                if (_workerTask is not null)
                {
                    throw new InvalidOperationException(
                        "The queue has already been started.");
                }

                if (_state == QueueState.Stopped)
                {
                    throw new InvalidOperationException(
                        "The queue has been stopped.");
                }

                _workerTask =
                    ProcessAsync(processor);
            }
        }

        /// <summary>
        /// Attempts to add an item to the queue.
        ///
        /// Returns false when the queue is cancelling, blocked, stopped,
        /// or otherwise not accepting new work.
        /// </summary>
        public bool TryEnqueue(
            T item)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                if (_state != QueueState.Accepting)
                {
                    return false;
                }

                _queue.Enqueue(
                    item);

                /*
                 * The signal represents the possibility of work rather
                 * than an exact count that must match _queue.Count.
                 *
                 * ClearPending() may therefore leave stale signals. The
                 * worker handles that by checking the queue after waking.
                 */
                _signal.Release();

                return true;
            }
        }

        /// <summary>
        /// Removes all pending work from the queue.
        ///
        /// The currently executing item, if any, is not affected.
        /// </summary>
        public void ClearPending()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                _queue.Clear();
            }
        }

        /// <summary>
        /// Waits until the queue has no pending or active work.
        ///
        /// This does not wait for downstream processing performed by the
        /// processor after the queue item has been submitted.
        /// </summary>
        public async Task WaitForIdleAsync(
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task? currentItemCompletion;

                lock (_sync)
                {
                    if (_queue.Count == 0 &&
                        _currentItemCompletion is null)
                    {
                        return;
                    }

                    currentItemCompletion =
                        _currentItemCompletion?.Task;
                }

                /*
                 * If an item is currently being processed, wait directly
                 * for that item rather than polling.
                 */
                if (currentItemCompletion is not null)
                {
                    await currentItemCompletion.WaitAsync(
                        cancellationToken);

                    continue;
                }

                /*
                 * There is pending work, but the worker has not yet
                 * established the current item. Give the worker an
                 * opportunity to acquire it.
                 */
                await Task.Delay(
                    10,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Cancels all pending and active work.
        ///
        /// Once cancellation begins, it cannot be interrupted.
        /// The caller's cancellation token only controls how long that
        /// caller waits for cancellation to complete.
        ///
        /// The queue remains blocked when cancellation completes and
        /// must be explicitly resumed with Resume().
        /// </summary>
        public Task CancelAsync(
            CancellationToken cancellationToken = default)
        {
            Task cancellationTask;

            CancellationTokenSource? currentItemCts;

            Task? currentItemCompletion;

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                if (_state == QueueState.Stopped)
                {
                    return Task.CompletedTask;
                }

                if (_state == QueueState.Blocked)
                {
                    return Task.CompletedTask;
                }

                /*
                 * Another caller is already cancelling the queue.
                 *
                 * All callers wait for the same cancellation operation.
                 */
                if (_state == QueueState.Cancelling)
                {
                    cancellationTask =
                        _cancellationCompletion!.Task;

                    return WaitForCancellationAsync(
                        cancellationTask,
                        cancellationToken);
                }

                /*
                 * Establish the cancellation barrier first.
                 *
                 * From this point forward TryEnqueue() rejects all
                 * new work.
                 */
                _state =
                    QueueState.Cancelling;

                _cancellationCompletion =
                    new TaskCompletionSource(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);

                cancellationTask =
                    _cancellationCompletion.Task;

                /*
                 * Remove work that has not started.
                 */
                _queue.Clear();

                /*
                 * Capture the active state atomically.
                 *
                 * These references are cleared together by the worker when
                 * the current item completes.
                 */
                currentItemCts =
                    _currentItemCts;

                currentItemCompletion =
                    _currentItemCompletion?.Task;
            }

            /*
             * There is no active item.
             */
            if (currentItemCts is null)
            {
                CompleteCancellation();
            }
            else
            {
                /*
                 * Cancellation is performed asynchronously so CancelAsync()
                 * can return a task representing the cancellation operation.
                 *
                 * The cancellation operation itself cannot be interrupted.
                 */
                _ = CompleteCancellationAsync(
                    currentItemCts,
                    currentItemCompletion);
            }

            return WaitForCancellationAsync(
                cancellationTask,
                cancellationToken);
        }

        private async Task CompleteCancellationAsync(
            CancellationTokenSource currentItemCts,
            Task? currentItemCompletion)
        {
            try
            {
                /*
                 * Cancel the active operation.
                 *
                 * This is deliberately outside _sync.
                 */
                await currentItemCts.CancelAsync();

                /*
                 * Wait until the processor has completely exited.
                 *
                 * The completion task was captured atomically when
                 * cancellation began.
                 */
                if (currentItemCompletion is not null)
                {
                    await currentItemCompletion;
                }

                CompleteCancellation();
            }
            catch (Exception ex)
            {
                CompleteCancellation(
                    ex);
            }
        }

        private void CompleteCancellation(
            Exception? exception = null)
        {
            lock (_sync)
            {
                if (_state == QueueState.Cancelling)
                {
                    _state =
                        QueueState.Blocked;
                }

                if (_cancellationCompletion is null)
                {
                    return;
                }

                if (exception is null)
                {
                    _cancellationCompletion.TrySetResult();
                }
                else
                {
                    _cancellationCompletion.TrySetException(
                        exception);
                }
            }
        }

        private static async Task WaitForCancellationAsync(
            Task cancellationTask,
            CancellationToken cancellationToken)
        {
            /*
             * The cancellation operation itself cannot be interrupted.
             *
             * The caller can stop waiting, but the queue continues
             * cancelling in the background.
             */
            await cancellationTask.WaitAsync(
                cancellationToken);
        }

        /// <summary>
        /// Reopens the queue after cancellation has completed.
        /// </summary>
        public void Resume()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);

                if (_state == QueueState.Accepting)
                {
                    return;
                }

                if (_state != QueueState.Blocked)
                {
                    throw new InvalidOperationException(
                        "The queue must be blocked before it can be resumed.");
                }

                _state =
                    QueueState.Accepting;

                _cancellationCompletion =
                    null;
            }
        }

        /// <summary>
        /// Permanently stops the queue worker.
        ///
        /// Unlike CancelAsync(), StopAsync() permanently transitions the
        /// queue to the Stopped state.
        /// </summary>
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            Task? workerTask;

            CancellationTokenSource? currentItemCts;

            lock (_sync)
            {
                if (_disposed ||
                    _state == QueueState.Stopped)
                {
                    return;
                }

                /*
                 * Prevent new work immediately.
                 */
                _state =
                    QueueState.Cancelling;

                _queue.Clear();

                currentItemCts =
                    _currentItemCts;

                workerTask =
                    _workerTask;

                _shutdown.Cancel();
            }

            /*
             * Do not invoke cancellation while holding _sync.
             */
            if (currentItemCts is not null)
            {
                await currentItemCts.CancelAsync();
            }

            if (workerTask is not null)
            {
                await workerTask.WaitAsync(
                    cancellationToken);
            }

            lock (_sync)
            {
                if (!_disposed)
                {
                    _state =
                        QueueState.Stopped;
                }
            }
        }

        private async Task ProcessAsync(
            Func<T, CancellationToken, Task> processor)
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    await _signal.WaitAsync(
                        _shutdown.Token);

                    T item;

                    CancellationTokenSource itemCts;

                    TaskCompletionSource itemCompletion;

                    lock (_sync)
                    {
                        /*
                         * The worker remains alive while the queue is
                         * cancelling or blocked.
                         *
                         * It simply does not dequeue new work.
                         */
                        if (_state != QueueState.Accepting ||
                            _queue.Count == 0)
                        {
                            continue;
                        }

                        item =
                            _queue.Dequeue();

                        itemCts =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                _shutdown.Token);

                        itemCompletion =
                            new TaskCompletionSource(
                                TaskCreationOptions
                                    .RunContinuationsAsynchronously);

                        _currentItemCts =
                            itemCts;

                        _currentItemCompletion =
                            itemCompletion;
                    }

                    try
                    {
                        await processor(
                            item,
                            itemCts.Token);
                    }
                    catch (OperationCanceledException)
                        when (itemCts.IsCancellationRequested)
                    {
                        /*
                         * Expected cancellation.
                         *
                         * Do not allow cancellation of one item to
                         * terminate the worker.
                         */
                    }
                    catch (Exception)
                    {
                        /*
                         * An individual item must not terminate the
                         * queue worker.
                         *
                         * The owning service is responsible for logging
                         * processor-specific errors.
                         */
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(
                                    _currentItemCts,
                                    itemCts))
                            {
                                _currentItemCts =
                                    null;
                            }

                            if (ReferenceEquals(
                                    _currentItemCompletion,
                                    itemCompletion))
                            {
                                _currentItemCompletion =
                                    null;
                            }
                        }

                        itemCompletion.TrySetResult();

                        itemCts.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
                when (_shutdown.IsCancellationRequested)
            {
                /*
                 * Normal worker shutdown.
                 */
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? workerTask;

            CancellationTokenSource? currentItemCts;

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                _state =
                    QueueState.Stopped;

                _queue.Clear();

                currentItemCts =
                    _currentItemCts;

                workerTask =
                    _workerTask;

                _shutdown.Cancel();
            }

            /*
             * Stop the active processor.
             */
            if (currentItemCts is not null)
            {
                await currentItemCts.CancelAsync();
            }

            /*
             * Wait for the worker to exit.
             */
            if (workerTask is not null)
            {
                try
                {
                    await workerTask;
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
            }

            _shutdown.Dispose();
            _signal.Dispose();
        }

        public enum QueueState
        {
            Accepting,
            Cancelling,
            Blocked,
            Stopped
        }
    }
}
