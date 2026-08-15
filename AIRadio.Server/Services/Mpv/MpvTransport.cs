using AIRadio.Server.Models.Mpv;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AIRadio.Server.Services.Mpv
{
    public interface IMpvTransport : IAsyncDisposable
    {
        bool IsConnected { get; }

        event EventHandler<MpvMessageEventArgs>? MessageReceived;
        event EventHandler<MpvErrorEventArgs>? Error;

        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
        Task<JsonElement?> SendCommandAsync(MpvCommand command, CancellationToken cancellationToken = default);
    }

    public sealed class MpvTransport : IMpvTransport
    {
        private readonly ILogger<MpvTransport> _logger;
        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
        private readonly JsonSerializerOptions _serializerOptions;
        private readonly TimeSpan _commandTimeout;

        private Socket? _socket;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _receiveCancellation;
        private Task? _receiveTask;
        private long _requestId;
        private bool _disposed;

        public MpvTransport(IConfiguration configuration, ILogger<MpvTransport> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            var timeoutMilliseconds = configuration.GetValue(
                "Mpv:Transport:SendTimeoutMilliseconds",
                5000);

            _commandTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

            _logger.LogInformation(
                "MPV transport initialized. CommandTimeout={CommandTimeout}.",
                _commandTimeout);
        }

        public bool IsConnected =>
            _socket?.Connected == true;

        public event EventHandler<MpvMessageEventArgs>? MessageReceived;
        public event EventHandler<MpvErrorEventArgs>? Error;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _connectionLock.WaitAsync(cancellationToken);

            try
            {
                if (IsConnected)
                {
                    return;
                }

                var socketPath = _configuration.GetValue<string>(
                    "Mpv:Transport:SocketPath");

                if (string.IsNullOrWhiteSpace(socketPath))
                {
                    throw new InvalidOperationException(
                        "Mpv:Transport:SocketPath configuration value is missing.");
                }

                _logger.LogInformation(
                    "Connecting to MPV IPC socket {SocketPath}.",
                    socketPath);

                _socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);

                await _socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(socketPath),
                    cancellationToken);

                var networkStream = new NetworkStream(
                    _socket,
                    ownsSocket: false);

                _reader = new StreamReader(networkStream, Encoding.UTF8);
                _writer = new StreamWriter(networkStream, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                _receiveCancellation = new CancellationTokenSource();
                _receiveTask = Task.Run(
                    () => ReceiveLoopAsync(_receiveCancellation.Token),
                    CancellationToken.None);

                _logger.LogInformation("Connected to MPV IPC socket.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to connect to MPV IPC socket.");
                await CleanupConnectionAsync();

                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        "Failed to connect to MPV.",
                        ex));

                throw;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);

            try
            {
                if (!IsConnected)
                {
                    return;
                }

                await CleanupConnectionAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task<JsonElement?> SendCommandAsync(
            MpvCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "MPV transport is not connected.");
            }

            var requestId = Interlocked.Increment(ref _requestId);
            command.RequestId = requestId;

            var completionSource = new TaskCompletionSource<JsonElement?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pendingRequests.TryAdd(requestId, completionSource))
            {
                throw new InvalidOperationException(
                    "Unable to register MPV request.");
            }

            try
            {
                var json = JsonSerializer.Serialize(command, _serializerOptions);

                await _sendLock.WaitAsync(cancellationToken);

                try
                {
                    if (_writer is null)
                    {
                        throw new InvalidOperationException(
                            "MPV writer is not initialized.");
                    }

                    await _writer.WriteLineAsync(json);
                    await _writer.FlushAsync(cancellationToken);
                }
                finally
                {
                    _sendLock.Release();
                }

                using var timeoutCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                timeoutCancellation.CancelAfter(_commandTimeout);

                using var registration = timeoutCancellation.Token.Register(
                    () => completionSource.TrySetCanceled(timeoutCancellation.Token));

                return await completionSource.Task;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "MPV command {RequestId} cancelled or timed out.",
                    requestId);
                throw;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_reader is null)
                {
                    throw new InvalidOperationException(
                        "MPV reader is not initialized.");
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken);

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (root.TryGetProperty("request_id", out var requestIdElement))
                    {
                        if (!requestIdElement.TryGetInt64(out var requestId))
                        {
                            _logger.LogWarning(
                                "Received MPV response with invalid request_id.");
                            continue;
                        }

                        if (_pendingRequests.TryRemove(requestId, out var pendingRequest))
                        {
                            if (root.TryGetProperty("error", out var errorElement) &&
                                !string.Equals(
                                    errorElement.GetString(),
                                    "success",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                pendingRequest.TrySetException(
                                    new InvalidOperationException(
                                        $"MPV command failed: {errorElement.GetString()}"));
                            }
                            else
                            {
                                pendingRequest.TrySetResult(root.Clone());
                            }
                        }

                        continue;
                    }

                    try
                    {
                        MessageReceived?.Invoke(
                            this,
                            new MpvMessageEventArgs(root.Clone()));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing MPV message event.");
                        Error?.Invoke(
                            this,
                            new MpvErrorEventArgs(
                                "Error processing MPV event.",
                                ex,
                                nameof(ReceiveLoopAsync)));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in MPV receive loop.");
                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        "MPV receive loop failed.",
                        ex,
                        nameof(ReceiveLoopAsync)));
            }
            finally
            {
                _logger.LogDebug("MPV receive loop stopped.");
            }
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
                await CleanupConnectionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing MPV transport.");
            }

            _sendLock.Dispose();
            _connectionLock.Dispose();

            GC.SuppressFinalize(this);
        }

        private async Task CleanupConnectionAsync()
        {
            if (_receiveCancellation is not null)
            {
                try
                {
                    await _receiveCancellation.CancelAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error cancelling MPV receive loop.");
                }
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("MPV receive loop did not exit before timeout.");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Exception while waiting for MPV receive loop shutdown.");
                }
            }

            foreach (var request in _pendingRequests)
            {
                request.Value.TrySetCanceled();
            }

            _pendingRequests.Clear();

            _writer?.Dispose();
            _writer = null;

            _reader?.Dispose();
            _reader = null;

            if (_socket is not null)
            {
                try
                {
                    if (_socket.Connected)
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }

            _receiveCancellation?.Dispose();
            _receiveCancellation = null;
            _receiveTask = null;
        }
    }
}
