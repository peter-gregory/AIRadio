using System.Runtime.InteropServices;
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
        private readonly ILogger<PipeWireNativeClient> _logger;
        private bool _disposed;
        private bool _connected;
        private bool _bufferAvailable;
        private int _volume = 100;
        private IntPtr _mainLoop, _context, _core, _stream;
        private GCHandle _selfHandle;
        private PwProcessCallback? _processCallback;
        private PwDrainedCallback? _drainedCallback;

        public PipeWireNativeClient(ILogger<PipeWireNativeClient> logger) => _logger = logger;
        public bool IsConnected => _connected;
        public bool BufferAvailable => _bufferAvailable;
        public event EventHandler<PipeWireBufferEventArgs>? BufferRequested;
        public event EventHandler? Drained;

        public Task InitializeAsync(int sampleRate, short channels, short bitsPerSample, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_connected) return Task.CompletedTask;

            try
            {
                PipeWireNativeMethods.pw_init(IntPtr.Zero, IntPtr.Zero);
                _mainLoop = PipeWireNativeMethods.pw_main_loop_new(IntPtr.Zero);
                if (_mainLoop == IntPtr.Zero) throw new InvalidOperationException("Unable to create PipeWire main loop.");

                _context = PipeWireNativeMethods.pw_context_new(PipeWireNativeMethods.pw_main_loop_get_loop(_mainLoop), IntPtr.Zero, 0);
                if (_context == IntPtr.Zero) throw new InvalidOperationException("Unable to create PipeWire context.");

                _core = PipeWireNativeMethods.pw_context_connect(_context, IntPtr.Zero, 0);
                if (_core == IntPtr.Zero) throw new InvalidOperationException("Unable to connect PipeWire core.");

                _processCallback = ProcessCallback;
                _drainedCallback = DrainedCallback;
                var events = new PwStreamEvents
                {
                    Version = 1,
                    Process = Marshal.GetFunctionPointerForDelegate(_processCallback),
                    Drained = Marshal.GetFunctionPointerForDelegate(_drainedCallback)
                };

                _selfHandle = GCHandle.Alloc(this);
                _stream = PipeWireNativeMethods.pw_stream_new_simple(
                    PipeWireNativeMethods.pw_main_loop_get_loop(_mainLoop),
                    "RadioAudioOutput", IntPtr.Zero, ref events,
                    GCHandle.ToIntPtr(_selfHandle));
                if (_stream == IntPtr.Zero) throw new InvalidOperationException("Unable to create PipeWire stream.");

                var result = PipeWireNativeMethods.pw_stream_connect(
                    _stream, PwDirection.Output, 0, PwStreamFlags.Autoconnect, null!, 0);
                if (result < 0) throw new InvalidOperationException($"PipeWire stream connection failed: {result}");

                _connected = true;
                _logger.LogInformation("PipeWire stream connected.");
                return Task.CompletedTask;
            }
            catch
            {
                CleanupNativeResources();
                throw;
            }
        }

        private void ProcessCallback(IntPtr userData)
        {
            try
            {
                _bufferAvailable = true;
                var nativeBuffer = PipeWireNativeMethods.pw_stream_dequeue_buffer(_stream);
                if (nativeBuffer == IntPtr.Zero)
                    return;

                // The native buffer mapping remains a prerequisite for actual PCM playback.
                BufferRequested?.Invoke(this, new PipeWireBufferEventArgs(Memory<byte>.Empty));
                PipeWireNativeMethods.pw_stream_queue_buffer(_stream, nativeBuffer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire process callback failed.");
            }
            finally
            {
                _bufferAvailable = false;
            }
        }

        private void DrainedCallback(IntPtr userData) => Drained?.Invoke(this, EventArgs.Empty);

        public Task FlushAsync(bool drain = false, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var result = PipeWireNativeMethods.pw_stream_flush(_stream, drain);
            if (result < 0)
                throw new InvalidOperationException($"PipeWire stream flush failed: {result}");
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _volume = Math.Clamp(volume, 0, 100);
            return Task.CompletedTask;
        }

        public Task<int> GetVolumeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return Task.FromResult(_volume);
        }

        private void CleanupNativeResources()
        {
            if (_stream != IntPtr.Zero) { PipeWireNativeMethods.pw_stream_destroy(_stream); _stream = IntPtr.Zero; }
            if (_core != IntPtr.Zero) { PipeWireNativeMethods.pw_core_disconnect(_core); _core = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { PipeWireNativeMethods.pw_context_destroy(_context); _context = IntPtr.Zero; }
            if (_mainLoop != IntPtr.Zero) { PipeWireNativeMethods.pw_main_loop_destroy(_mainLoop); _mainLoop = IntPtr.Zero; }
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            PipeWireNativeMethods.pw_deinit();
            _connected = false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PipeWireNativeClient));
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            CleanupNativeResources();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class PipeWireBufferEventArgs : EventArgs
    {
        private readonly Memory<byte> _buffer;
        private int _dataLength;
        internal PipeWireBufferEventArgs(Memory<byte> buffer) => _buffer = buffer;
        public Memory<byte> Buffer => _buffer;
        public int DataLength => _dataLength;
        public void CopyFrom(ReadOnlyMemory<byte> source)
        {
            var length = Math.Min(source.Length, _buffer.Length);
            source[..length].CopyTo(_buffer);
            _dataLength = length;
            if (length < _buffer.Length) FillSilence(length, _buffer.Length - length);
        }
        public void FillSilence() => FillSilence(0, _buffer.Length);
        private void FillSilence(int offset, int length)
        {
            _buffer.Slice(offset, length).Span.Clear();
            _dataLength = _buffer.Length;
        }
    }
}
