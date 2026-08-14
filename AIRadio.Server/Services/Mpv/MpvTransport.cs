using AIRadio.Server.Models.Mpv;
using System.Collections.Concurrent;
using System.IO.Pipes;
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

        Task ConnectAsync(
            CancellationToken cancellationToken = default);

        Task DisconnectAsync(
            CancellationToken cancellationToken = default);

        Task<JsonElement?> SendCommandAsync(
            MpvCommand command,
            CancellationToken cancellationToken = default);
    }

    public sealed class MpvTransport : IMpvTransport
    {
        private readonly ILogger<MpvTransport> _logger;

        private readonly IConfiguration _configuration;

        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        private Socket? _socket;

        private StreamReader? _reader;

        private StreamWriter? _writer;

        private CancellationTokenSource? _receiveCancellation;

        private Task? _receiveTask;

        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private readonly ConcurrentDictionary<long,
            TaskCompletionSource<JsonElement?>>
            _pendingRequests = new();

        private readonly JsonSerializerOptions _serializerOptions;

        private NamedPipeClientStream? _pipe;

        private readonly TimeSpan _commandTimeout;

        private long _requestId;
        private bool _disposed;

        public MpvTransport(
            IConfiguration configuration,
            ILogger<MpvTransport> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            _logger.LogInformation(
                "MPV transport initialized.");
        }

        public bool IsConnected =>
            _pipe?.IsConnected == true;

        public event EventHandler<MpvMessageEventArgs>? MessageReceived;

        public event EventHandler<MpvErrorEventArgs>? Error;

        public async Task ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvTransport));
            
            await _connectionLock.WaitAsync(cancellationToken);

            try
            {
                if (IsConnected)
                {
                    _logger.LogDebug(
                        "MPV transport is already connected.");

                    return;
                }


                var socketPath =
                    _configuration.GetValue<string>(
                        "Mpv:IpcSocket");


                if (string.IsNullOrWhiteSpace(socketPath))
                {
                    _logger.LogError(
                        "MPV IPC socket path is not configured.");

                    throw new InvalidOperationException(
                        "Mpv:IpcSocket configuration value is missing.");
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


                var networkStream =
                    new NetworkStream(
                        _socket,
                        ownsSocket: false);


                _reader =
                    new StreamReader(
                        networkStream,
                        Encoding.UTF8);


                _writer =
                    new StreamWriter(
                        networkStream,
                        Encoding.UTF8)
                    {
                        AutoFlush = true
                    };


                _receiveCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);


                _receiveTask =
                    Task.Run(
                        () => ReceiveLoopAsync(
                            _receiveCancellation.Token),
                        CancellationToken.None);


                _logger.LogInformation(
                    "Connected to MPV IPC socket.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "MPV connection attempt was cancelled.");

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to connect to MPV IPC socket.");

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

        public async Task DisconnectAsync(
            CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);

            try
            {
                if (!IsConnected)
                {
                    _logger.LogDebug(
                        "MPV transport already disconnected.");

                    return;
                }

                _logger.LogInformation(
                    "Disconnecting from MPV.");

                await CleanupConnectionAsync();

                _logger.LogInformation(
                    "MPV disconnected.");
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

            if (!IsConnected)
            {
                _logger.LogWarning(
                    "Cannot send MPV command. Transport is disconnected.");

                throw new InvalidOperationException(
                    "MPV transport is not connected.");
            }


            var requestId =
                Interlocked.Increment(ref _requestId);


            command.RequestId = requestId;


            var completionSource =
                new TaskCompletionSource<JsonElement?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);


            if (!_pendingRequests.TryAdd(
                    requestId,
                    completionSource))
            {
                _logger.LogError(
                    "Unable to register MPV request {RequestId}.",
                    requestId);

                throw new InvalidOperationException(
                    "Unable to register MPV request.");
            }


            try
            {
                var json =
                    JsonSerializer.Serialize(
                        command,
                        _serializerOptions);


                _logger.LogTrace(
                    "Sending MPV command {RequestId}: {Command}",
                    requestId,
                    json);


                await _sendLock.WaitAsync(
                    cancellationToken);


                try
                {
                    if (_writer == null)
                    {
                        throw new InvalidOperationException(
                            "MPV writer is not initialized.");
                    }


                    await _writer.WriteLineAsync(json);

                    await _writer.FlushAsync(
                        cancellationToken);
                }
                finally
                {
                    _sendLock.Release();
                }


                using var timeoutCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);


                timeoutCancellation.CancelAfter(
                    _commandTimeout);


                await using var registration =
                    timeoutCancellation.Token.Register(
                        () =>
                        {
                            completionSource.TrySetCanceled(
                                timeoutCancellation.Token);
                        });


                var response =
                    await completionSource.Task;


                _logger.LogTrace(
                    "Received response for MPV request {RequestId}.",
                    requestId);


                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "MPV command {RequestId} cancelled or timed out.",
                    requestId);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error sending MPV command {RequestId}.",
                    requestId);

                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        $"Failed sending MPV command {requestId}.",
                        ex));

                throw;
            }
            finally
            {
                _pendingRequests.TryRemove(
                    requestId,
                    out _);
            }
        }

        private async Task ReceiveLoopAsync(
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(
                "MPV receive loop started.");

            try
            {
                if (_reader == null)
                {
                    throw new InvalidOperationException(
                        "MPV reader is not initialized.");
                }


                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line;

                    try
                    {
                        line = await _reader.ReadLineAsync(
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }


                    if (line == null)
                    {
                        _logger.LogWarning(
                            "MPV connection closed by remote endpoint.");

                        break;
                    }


                    if (string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogTrace(
                            "Ignoring empty MPV message.");

                        continue;
                    }


                    _logger.LogTrace(
                        "Received MPV message: {Message}",
                        line);


                    JsonDocument document;

                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Received invalid JSON from MPV: {Message}",
                            line);

                        Error?.Invoke(
                            this,
                            new MpvErrorEventArgs(
                                "Invalid JSON received from MPV.",
                                ex,
                                nameof(ReceiveLoopAsync)));

                        continue;
                    }


                    using (document)
                    {
                        var root = document.RootElement;


                        //
                        // Command response
                        //
                        if (root.TryGetProperty(
                                "request_id",
                                out var requestIdElement))
                        {
                            var requestId =
                                requestIdElement.GetInt64();


                            if (_pendingRequests.TryRemove(
                                    requestId,
                                    out var pendingRequest))
                            {
                                _logger.LogTrace(
                                    "Completing MPV request {RequestId}.",
                                    requestId);


                                pendingRequest.TrySetResult(
                                    root.Clone());
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Received response for unknown MPV request {RequestId}.",
                                    requestId);
                            }


                            continue;
                        }


                        //
                        // Asynchronous MPV event
                        //
                        try
                        {
                            MessageReceived?.Invoke(
                                this,
                                new MpvMessageEventArgs(
                                    root.Clone()));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Error processing MPV message event.");

                            Error?.Invoke(
                                this,
                                new MpvErrorEventArgs(
                                    "Error processing MPV event.",
                                    ex,
                                    nameof(ReceiveLoopAsync)));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "MPV receive loop cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in MPV receive loop.");

                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        "MPV receive loop failed.",
                        ex,
                        nameof(ReceiveLoopAsync)));
            }
            finally
            {
                _logger.LogDebug(
                    "MPV receive loop stopped.");
            }
        }


        private MpvMessageProcessResult ProcessMessage(
            JsonElement message)
        {
            try
            {
                //
                // Command response
                //
                if (message.TryGetProperty(
                        "request_id",
                        out _))
                {
                    CompleteRequest(message);

                    return MpvMessageProcessResult.CommandResponse;
                }


                //
                // MPV asynchronous event
                //
                if (message.TryGetProperty(
                        "event",
                        out _))
                {
                    _logger.LogTrace(
                        "Processing MPV event message.");


                    MessageReceived?.Invoke(
                        this,
                        new MpvMessageEventArgs(
                            message.Clone()));


                    return MpvMessageProcessResult.Event;
                }


                //
                // Unknown MPV message
                //
                _logger.LogWarning(
                    "Received unknown MPV message: {Message}",
                    message);


                return MpvMessageProcessResult.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing MPV message.");

                Error?.Invoke(
                    this,
                    new MpvErrorEventArgs(
                        "Error processing MPV message.",
                        ex,
                        nameof(ProcessMessage)));

                return MpvMessageProcessResult.Unknown;
            }
        }

        private bool CompleteRequest(
            JsonElement message)
        {
            if (!message.TryGetProperty(
                    "request_id",
                    out var requestIdElement))
            {
                return false;
            }


            if (requestIdElement.ValueKind != JsonValueKind.Number ||
                !requestIdElement.TryGetInt64(out var requestId))
            {
                _logger.LogWarning(
                    "Received MPV response with invalid request_id.");

                return true;
            }


            if (!_pendingRequests.TryRemove(
                    requestId,
                    out var pendingRequest))
            {
                _logger.LogWarning(
                    "Received response for unknown MPV request {RequestId}.",
                    requestId);

                return true;
            }


            if (message.TryGetProperty(
                    "error",
                    out var errorElement))
            {
                var error =
                    errorElement.GetString();


                if (!string.Equals(
                        error,
                        "success",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "MPV request {RequestId} failed: {Error}.",
                        requestId,
                        error);


                    pendingRequest.TrySetException(
                        new InvalidOperationException(
                            $"MPV command failed: {error}"));


                    return true;
                }
            }


            _logger.LogTrace(
                "Completed MPV request {RequestId}.",
                requestId);


            pendingRequest.TrySetResult(
                message.Clone());


            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _logger.LogDebug(
                "Disposing MPV transport.");

            try
            {
                await CleanupConnectionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error disposing MPV transport.");
            }

            _connectionLock.Dispose();

            GC.SuppressFinalize(this);
        }

        private async Task CleanupConnectionAsync()
        {
            _logger.LogTrace(
                "Cleaning up MPV transport resources.");


            // Stop receive loop
            if (_receiveCancellation != null)
            {
                try
                {
                    await _receiveCancellation.CancelAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Error cancelling MPV receive loop.");
                }
            }


            // Wait for receive loop to exit
            if (_receiveTask != null)
            {
                try
                {
                    await _receiveTask.WaitAsync(
                        TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "MPV receive loop did not exit before timeout.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Exception while waiting for MPV receive loop shutdown.");
                }
            }


            // Cancel pending requests
            if (!_pendingRequests.IsEmpty)
            {
                _logger.LogDebug(
                    "Cancelling {Count} pending MPV requests.",
                    _pendingRequests.Count);


                foreach (var request in _pendingRequests)
                {
                    request.Value.TrySetCanceled();
                }

                _pendingRequests.Clear();
            }


            // Dispose writer
            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error disposing MPV writer.");
            }

            _writer = null;


            // Dispose reader
            try
            {
                _reader?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Error disposing MPV reader.");
            }

            _reader = null;


            // Close socket
            if (_socket != null)
            {
                try
                {
                    if (_socket.Connected)
                    {
                        _socket.Shutdown(
                            SocketShutdown.Both);
                    }
                }
                catch (SocketException ex)
                {
                    _logger.LogTrace(
                        ex,
                        "Socket shutdown failed during cleanup.");
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed
                }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }


            // Dispose cancellation token source
            _receiveCancellation?.Dispose();

            _receiveCancellation = null;
            _receiveTask = null;


            _logger.LogTrace(
                "MPV transport cleanup complete.");
        }
    }

    public enum MpvMessageProcessResult
    {
        Unknown,

        CommandResponse,

        Event
    }
}
