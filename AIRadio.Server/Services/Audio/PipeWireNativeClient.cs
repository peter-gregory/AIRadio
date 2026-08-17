using System.Runtime.InteropServices;
using System.Text;
using static AIRadio.Server.Services.Audio.PipeWireNativeMethods;

namespace AIRadio.Server.Services.Audio
{
    public interface IPipeWireNativeClient : IAsyncDisposable
    {
        bool IsConnected { get; }
        bool BufferAvailable { get; }
        event EventHandler<PipeWireBufferEventArgs>? BufferRequested;
        event EventHandler? Drained;
        Task InitializeAsync(int sampleRate, short channels, short bitsPerSample, CancellationToken cancellationToken = default);
        Task FlushAsync(bool drain = false, CancellationToken cancellationToken = default);
        Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
        Task<int> GetVolumeAsync(CancellationToken cancellationToken = default);
    }

    public sealed class PipeWireNativeClient : IPipeWireNativeClient
    {
        private const int StreamStateError = -1;
        private const int StreamStatePaused = 2;
        private const int StreamStateStreaming = 3;
        private const uint PcmDataType = 1;

        private readonly ILogger<PipeWireNativeClient> _logger;
        private readonly object _stateLock = new();
        private bool _disposed;
        private bool _connected;
        private bool _bufferAvailable;
        private int _volume = 100;
        private IntPtr _mainLoop;
        private IntPtr _context;
        private IntPtr _core;
        private IntPtr _stream;
        private GCHandle _selfHandle;
        private Thread? _mainLoopThread;
        private PwProcessCallback? _processCallback;
        private PwDrainedCallback? _drainedCallback;
        private PwStateChangedCallback? _stateChangedCallback;
        private TaskCompletionSource<object?>? _connectionCompletion;
        private Exception? _connectionError;

        public PipeWireNativeClient(ILogger<PipeWireNativeClient> logger) => _logger = logger;

        public bool IsConnected => Volatile.Read(ref _connected);
        public bool BufferAvailable => Volatile.Read(ref _bufferAvailable);

        public event EventHandler<PipeWireBufferEventArgs>? BufferRequested;
        public event EventHandler? Drained;

        public async Task InitializeAsync(
            int sampleRate,
            short channels,
            short bitsPerSample,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (IsConnected)
                return;

            if (bitsPerSample != 16)
                throw new NotSupportedException("PipeWireNativeClient currently supports 16-bit PCM only.");

            if (channels < 1)
                throw new ArgumentOutOfRangeException(nameof(channels));

            try
            {
                PipeWireNativeMethods.pw_init(IntPtr.Zero, IntPtr.Zero);

                _mainLoop = PipeWireNativeMethods.pw_main_loop_new(IntPtr.Zero);
                if (_mainLoop == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to create PipeWire main loop.");

                var loop = PipeWireNativeMethods.pw_main_loop_get_loop(_mainLoop);
                if (loop == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to obtain PipeWire loop.");

                _context = PipeWireNativeMethods.pw_context_new(loop, IntPtr.Zero, 0);
                if (_context == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to create PipeWire context.");

                _core = PipeWireNativeMethods.pw_context_connect(_context, IntPtr.Zero, 0);
                if (_core == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to connect PipeWire core.");

                _processCallback = ProcessCallback;
                _drainedCallback = DrainedCallback;
                _stateChangedCallback = StateChangedCallback;

                var events = new PwStreamEvents
                {
                    Version = 2,
                    StateChanged = Marshal.GetFunctionPointerForDelegate(_stateChangedCallback),
                    Process = Marshal.GetFunctionPointerForDelegate(_processCallback),
                    Drained = Marshal.GetFunctionPointerForDelegate(_drainedCallback)
                };

                _selfHandle = GCHandle.Alloc(this);

                var properties = PipeWireNativeMethods.pw_properties_new(
                    "media.type", "Audio", IntPtr.Zero);
                if (properties == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to create PipeWire stream properties.");

                PipeWireNativeMethods.pw_properties_set(properties, "media.category", "Playback");
                PipeWireNativeMethods.pw_properties_set(properties, "media.role", "Music");

                _stream = PipeWireNativeMethods.pw_stream_new_simple(
                    loop,
                    "AIRadioAudioOutput",
                    properties,
                    ref events,
                    GCHandle.ToIntPtr(_selfHandle));

                if (_stream == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to create PipeWire stream.");

                var formatPod = BuildAudioFormatPod(sampleRate, channels);
                var podMemory = GCHandle.Alloc(formatPod, GCHandleType.Pinned);
                try
                {
                    var parameters = new[] { podMemory.AddrOfPinnedObject() };

                    var result = PipeWireNativeMethods.pw_stream_connect(
                        _stream,
                        PwDirection.Output,
                        PwIdAny,
                        PwStreamFlags.Autoconnect |
                        PwStreamFlags.MapBuffers |
                        PwStreamFlags.RtProcess,
                        parameters,
                        1);

                    if (result < 0)
                        throw new InvalidOperationException($"PipeWire stream connection failed: {result}");
                }
                finally
                {
                    podMemory.Free();
                }

                lock (_stateLock)
                {
                    _connectionCompletion = new TaskCompletionSource<object?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _connectionError = null;
                }

                _mainLoopThread = new Thread(RunMainLoop)
                {
                    IsBackground = true,
                    Name = "PipeWireMainLoop"
                };
                _mainLoopThread.Start();

                Task connectionTask;
                lock (_stateLock)
                    connectionTask = _connectionCompletion.Task;

                await connectionTask.WaitAsync(cancellationToken);
                Volatile.Write(ref _connected, true);

                _logger.LogInformation(
                    "PipeWire stream connected: {Rate}Hz {Channels}ch {Bits}bit",
                    sampleRate,
                    channels,
                    bitsPerSample);
            }
            catch
            {
                CleanupNativeResources();
                throw;
            }
        }

        private void RunMainLoop()
        {
            try
            {
                PipeWireNativeMethods.pw_main_loop_run(_mainLoop);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire main loop failed.");
                CompleteConnection(ex);
            }
        }

        private void StateChangedCallback(IntPtr userData, int oldState, int state, IntPtr error)
        {
            try
            {
                if (state == StreamStateError)
                {
                    var message = error == IntPtr.Zero
                        ? "PipeWire stream entered an error state."
                        : Marshal.PtrToStringAnsi(error) ?? "PipeWire stream entered an error state.";

                    var exception = new InvalidOperationException(message);
                    Volatile.Write(ref _connected, false);
                    CompleteConnection(exception);
                    _logger.LogError("{Message}", message);
                    return;
                }

                if (state is StreamStatePaused or StreamStateStreaming)
                    CompleteConnection(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire state callback failed.");
                CompleteConnection(ex);
            }
        }

        private void CompleteConnection(Exception? exception)
        {
            TaskCompletionSource<object?>? completion;
            lock (_stateLock)
            {
                completion = _connectionCompletion;
                _connectionError = exception;
                if (exception is null)
                    _connectionCompletion = null;
            }

            if (exception is null)
                completion?.TrySetResult(null);
            else
                completion?.TrySetException(exception);
        }

        private void ProcessCallback(IntPtr userData)
        {
            try
            {
                Volatile.Write(ref _bufferAvailable, true);

                var nativeBuffer = PipeWireNativeMethods.pw_stream_dequeue_buffer(_stream);
                if (nativeBuffer == IntPtr.Zero)
                    return;

                var buffer = Marshal.PtrToStructure<PwBuffer>(nativeBuffer);
                if (buffer.Buffer == IntPtr.Zero)
                {
                    PipeWireNativeMethods.pw_stream_return_buffer(_stream, nativeBuffer);
                    return;
                }

                var spaBuffer = Marshal.PtrToStructure<SpaBuffer>(buffer.Buffer);
                if (spaBuffer.DataCount == 0 || spaBuffer.Datas == IntPtr.Zero)
                {
                    PipeWireNativeMethods.pw_stream_return_buffer(_stream, nativeBuffer);
                    return;
                }

                var data = Marshal.PtrToStructure<SpaData>(spaBuffer.Datas);
                if (data.Data == IntPtr.Zero || data.Chunk == IntPtr.Zero || data.MaxSize == 0)
                {
                    PipeWireNativeMethods.pw_stream_return_buffer(_stream, nativeBuffer);
                    return;
                }

                var chunk = Marshal.PtrToStructure<SpaChunk>(data.Chunk);
                var capacity = checked((int)data.MaxSize);
                var requestedBytes = buffer.Requested == 0
                    ? capacity
                    : Math.Min(capacity, checked((int)buffer.Requested) * 2);

                var args = new PipeWireBufferEventArgs(
                    data.Data,
                    capacity,
                    requestedBytes,
                    this);

                BufferRequested?.Invoke(this, args);

                chunk.Offset = 0;
                chunk.Size = (uint)Math.Min(args.DataLength, capacity);
                chunk.Stride = 2;
                chunk.Flags = 0;
                Marshal.StructureToPtr(chunk, data.Chunk, false);

                buffer.Size = chunk.Size / 2;
                Marshal.StructureToPtr(buffer, nativeBuffer, false);
                PipeWireNativeMethods.pw_stream_queue_buffer(_stream, nativeBuffer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire process callback failed.");
            }
            finally
            {
                Volatile.Write(ref _bufferAvailable, false);
            }
        }

        private void DrainedCallback(IntPtr userData)
        {
            try
            {
                Drained?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire drained callback failed.");
            }
        }

        public Task FlushAsync(bool drain = false, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_stream == IntPtr.Zero)
                return Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
            var result = PipeWireNativeMethods.pw_stream_flush(_stream, drain);
            if (result < 0)
                throw new InvalidOperationException($"PipeWire stream flush failed: {result}");

            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var clamped = Math.Clamp(volume, 0, 100);
            if (_stream != IntPtr.Zero)
            {
                var values = new[] { clamped / 100f };
                var result = PipeWireNativeMethods.pw_stream_set_control(
                    _stream,
                    SpaPropVolume,
                    1,
                    values,
                    IntPtr.Zero);

                if (result < 0)
                    throw new InvalidOperationException($"PipeWire volume update failed: {result}");
            }

            Volatile.Write(ref _volume, clamped);
            return Task.CompletedTask;
        }

        public Task<int> GetVolumeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Volatile.Read(ref _volume));
        }

        private static byte[] BuildAudioFormatPod(int sampleRate, short channels)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels));

            // SPA_TYPE_OBJECT_Format / SPA_PARAM_EnumFormat with four fixed
            // properties: media type, media subtype, S16 format, rate, channels.
            const int propertySize = 24;
            const int objectBodySize = 8 + propertySize * 5;
            const int podSize = 8 + objectBodySize;
            var data = new byte[podSize];

            WriteUInt32(data, 0, objectBodySize);
            WriteUInt32(data, 4, SpaTypeObject);
            WriteUInt32(data, 8, SpaTypeObjectFormat);
            WriteUInt32(data, 12, SpaParamEnumFormat);

            var offset = 16;
            WriteIdProperty(data, ref offset, SpaFormatMediaType, SpaMediaTypeAudio);
            WriteIdProperty(data, ref offset, SpaFormatMediaSubtype, SpaMediaSubtypeRaw);
            WriteIdProperty(data, ref offset, SpaFormatAudioFormat, SpaAudioFormatS16);
            WriteIntProperty(data, ref offset, SpaFormatAudioRate, sampleRate);
            WriteIntProperty(data, ref offset, SpaFormatAudioChannels, channels);

            return data;
        }

        private static void WriteIdProperty(byte[] data, ref int offset, int key, int value)
        {
            WriteUInt32(data, offset, unchecked((uint)key));
            WriteUInt32(data, offset + 4, 0);
            WriteUInt32(data, offset + 8, 4);
            WriteUInt32(data, offset + 12, 2);
            WriteUInt32(data, offset + 16, unchecked((uint)value));
            offset += 24;
        }

        private static void WriteIntProperty(byte[] data, ref int offset, int key, int value)
        {
            WriteUInt32(data, offset, unchecked((uint)key));
            WriteUInt32(data, offset + 4, 0);
            WriteUInt32(data, offset + 8, 4);
            WriteUInt32(data, offset + 12, 3);
            WriteUInt32(data, offset + 16, unchecked((uint)value));
            offset += 24;
        }

        private static void WriteUInt32(byte[] data, int offset, uint value) =>
            BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);

        private void CleanupNativeResources()
        {
            Volatile.Write(ref _connected, false);

            if (_mainLoop != IntPtr.Zero)
            {
                try
                {
                    PipeWireNativeMethods.pw_main_loop_quit(_mainLoop);
                }
                catch
                {
                    // Cleanup must continue even if PipeWire is already gone.
                }
            }

            if (_mainLoopThread is not null &&
                _mainLoopThread != Thread.CurrentThread)
            {
                _mainLoopThread.Join(TimeSpan.FromSeconds(2));
                _mainLoopThread = null;
            }

            if (_stream != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_stream_destroy(_stream);
                _stream = IntPtr.Zero;
            }

            if (_core != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_core_disconnect(_core);
                _core = IntPtr.Zero;
            }

            if (_context != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_context_destroy(_context);
                _context = IntPtr.Zero;
            }

            if (_mainLoop != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_main_loop_destroy(_mainLoop);
                _mainLoop = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();

            _processCallback = null;
            _drainedCallback = null;
            _stateChangedCallback = null;

            PipeWireNativeMethods.pw_deinit();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipeWireNativeClient));
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
            CleanupNativeResources();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class PipeWireBufferEventArgs : EventArgs
    {
        private readonly IntPtr _buffer;
        private readonly int _capacity;
        private readonly int _requested;
        private readonly PipeWireNativeClient _owner;
        private int _dataLength;

        internal PipeWireBufferEventArgs(
            IntPtr buffer,
            int capacity,
            int requested,
            PipeWireNativeClient owner)
        {
            _buffer = buffer;
            _capacity = capacity;
            _requested = Math.Clamp(requested, 0, capacity);
            _owner = owner;
        }

        public int Capacity => _capacity;
        public int Requested => _requested;
        public int DataLength => _dataLength;

        public void CopyFrom(ReadOnlyMemory<byte> source)
        {
            var length = Math.Min(source.Length, _requested);
            if (length == 0)
            {
                FillSilence();
                return;
            }

            if (MemoryMarshal.TryGetArray(source, out ArraySegment<byte> segment) &&
                segment.Array is not null)
            {
                Marshal.Copy(segment.Array, segment.Offset, _buffer, length);
            }
            else
            {
                var copy = source[..length].ToArray();
                Marshal.Copy(copy, 0, _buffer, length);
            }

            if (length < _requested)
                Zero(_buffer + length, _requested - length);

            _dataLength = _requested;
        }

        public void FillSilence()
        {
            Zero(_buffer, _requested);
            _dataLength = _requested;
        }

        private static void Zero(IntPtr address, int length)
        {
            if (length <= 0)
                return;

            Span<byte> zeros = stackalloc byte[Math.Min(length, 256)];
            var remaining = length;
            var current = address;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, zeros.Length);
                Marshal.Copy(zeros[..count].ToArray(), 0, current, count);
                current += count;
                remaining -= count;
            }
        }
    }
}
