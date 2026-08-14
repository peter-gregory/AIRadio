using System.Runtime.InteropServices;
using static AIRadio.Server.Services.Audio.PipeWireNativeMethods;

namespace AIRadio.Server.Services.Audio
{
    public interface IPipeWireNativeClient : IAsyncDisposable
    {
        /// <summary>
        /// Gets whether the native PipeWire stream is connected.
        /// </summary>
        bool IsConnected { get; }


        /// <summary>
        /// Gets whether PipeWire currently has a playback buffer available.
        /// </summary>
        bool BufferAvailable { get; }


        /// <summary>
        /// Event raised when PipeWire requests audio data.
        /// The subscriber must fill the provided buffer.
        /// </summary>
        event EventHandler<PipeWireBufferEventArgs>? BufferRequested;


        /// <summary>
        /// Initializes the PipeWire playback stream.
        /// </summary>
        Task InitializeAsync(
            int sampleRate,
            short channels,
            short bitsPerSample,
            CancellationToken cancellationToken = default);


        /// <summary>
        /// Flushes queued buffers from the native PipeWire stream.
        /// Does not stop the stream.
        /// </summary>
        Task FlushAsync(
            CancellationToken cancellationToken = default);


        /// <summary>
        /// Sets the PipeWire sink/master volume.
        /// </summary>
        Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default);


        /// <summary>
        /// Gets the current PipeWire sink/master volume.
        /// </summary>
        Task<int> GetVolumeAsync(
            CancellationToken cancellationToken = default);
    }

    public sealed class PipeWireNativeClient : IPipeWireNativeClient
    {
        private readonly ILogger<PipeWireNativeClient> _logger;


        private bool _disposed;

        private bool _connected;

        private bool _bufferAvailable;


        private int _sampleRate;

        private short _channels;

        private short _bitsPerSample;


        private int _volume = 100;


        private IntPtr _mainLoop;

        private IntPtr _context;

        private IntPtr _core;

        private IntPtr _stream;



        private GCHandle _selfHandle;


        private PipeWireNativeMethods.PwProcessCallback?
            _processCallback;



        public PipeWireNativeClient(
            ILogger<PipeWireNativeClient> logger)
        {
            _logger = logger;
        }



        public bool IsConnected =>
            _connected;


        public bool BufferAvailable =>
            _bufferAvailable;



        public event EventHandler<PipeWireBufferEventArgs>? BufferRequested;



        public Task InitializeAsync(
            int sampleRate,
            short channels,
            short bitsPerSample,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();


            if (_connected)
            {
                _logger.LogDebug(
                    "PipeWire already initialized.");

                return Task.CompletedTask;
            }


            _sampleRate = sampleRate;
            _channels = channels;
            _bitsPerSample = bitsPerSample;



            try
            {
                _logger.LogInformation(
                    "Initializing PipeWire. Rate={Rate} Channels={Channels} Bits={Bits}",
                    _sampleRate,
                    _channels,
                    _bitsPerSample);



                PipeWireNativeMethods.pw_init(
                    IntPtr.Zero,
                    IntPtr.Zero);



                CreateMainLoop();


                CreateContext();


                ConnectCore();


                CreateStream();


                ConnectStream();


                _connected = true;


                _logger.LogInformation(
                    "PipeWire stream connected.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to initialize PipeWire.");

                CleanupNativeResources();

                throw;
            }


            return Task.CompletedTask;
        }



        private void CreateMainLoop()
        {
            _mainLoop =
                PipeWireNativeMethods.pw_main_loop_new(
                    IntPtr.Zero);


            if (_mainLoop == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Unable to create PipeWire main loop.");
            }


            _logger.LogDebug(
                "PipeWire main loop created.");
        }



        private void CreateContext()
        {
            var loop =
                PipeWireNativeMethods.pw_main_loop_get_loop(
                    _mainLoop);


            _context =
                PipeWireNativeMethods.pw_context_new(
                    loop,
                    IntPtr.Zero,
                    0);


            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Unable to create PipeWire context.");
            }


            _logger.LogDebug(
                "PipeWire context created.");
        }



        private void ConnectCore()
        {
            _core =
                PipeWireNativeMethods.pw_context_connect(
                    _context,
                    IntPtr.Zero,
                    0);


            if (_core == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Unable to connect PipeWire core.");
            }


            _logger.LogDebug(
                "PipeWire core connected.");
        }



        private void CreateStream()
        {
            _processCallback =
                ProcessCallback;


            var events =
                new PwStreamEvents
                {
                    Version = 1,

                    Process =
                        Marshal.GetFunctionPointerForDelegate(
                            _processCallback)
                };


            _selfHandle =
                GCHandle.Alloc(
                    this);



            _stream =
                PipeWireNativeMethods.pw_stream_new_simple(
                    PipeWireNativeMethods.pw_main_loop_get_loop(
                        _mainLoop),
                    "RadioAudioOutput",
                    IntPtr.Zero,
                    ref events,
                    GCHandle.ToIntPtr(
                        _selfHandle));



            if (_stream == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Unable to create PipeWire stream.");
            }


            _logger.LogDebug(
                "PipeWire stream created.");
        }



        private void ConnectStream()
        {
            var result =
                PipeWireNativeMethods.pw_stream_connect(
                    _stream,
                    PwDirection.Output,
                    0,
                    PwStreamFlags.Autoconnect,
                    null!,
                    0);


            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire stream connection failed: {result}");
            }


            _logger.LogDebug(
                "PipeWire stream connection requested.");
        }



        private void ProcessCallback(
            IntPtr userData)
        {
            try
            {
                _bufferAvailable = true;


                var nativeBuffer =
                    PipeWireNativeMethods.pw_stream_dequeue_buffer(
                        _stream);


                if (nativeBuffer == IntPtr.Zero)
                {
                    _logger.LogWarning(
                        "PipeWire requested buffer but none available.");

                    return;
                }



                //
                // The final implementation will:
                //
                // 1. Map pw_buffer
                // 2. Obtain the data pointer
                // 3. Wrap as Memory<byte>
                // 4. Raise BufferRequested
                // 5. Queue buffer back
                //



                BufferRequested?.Invoke(
                    this,
                    new PipeWireBufferEventArgs(
                        Memory<byte>.Empty));



                PipeWireNativeMethods.pw_stream_queue_buffer(
                    _stream,
                    nativeBuffer);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "PipeWire process callback failed.");
            }
            finally
            {
                _bufferAvailable = false;
            }
        }



        public Task FlushAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();


            _logger.LogDebug(
                "Flushing PipeWire stream.");


            //
            // pw_stream_flush()
            //


            return Task.CompletedTask;
        }



        public Task SetVolumeAsync(
            int volume,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();


            _volume =
                Math.Clamp(
                    volume,
                    0,
                    100);


            _logger.LogDebug(
                "PipeWire master volume set to {Volume}%.",
                _volume);



            //
            // Future:
            // WirePlumber / PipeWire metadata API
            //


            return Task.CompletedTask;
        }



        public Task<int> GetVolumeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();


            return Task.FromResult(
                _volume);
        }



        private void CleanupNativeResources()
        {
            _logger.LogDebug(
                "Cleaning PipeWire resources.");


            if (_stream != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_stream_destroy(
                    _stream);

                _stream = IntPtr.Zero;
            }


            if (_core != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_core_disconnect(
                    _core);

                _core = IntPtr.Zero;
            }


            if (_context != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_context_destroy(
                    _context);

                _context = IntPtr.Zero;
            }


            if (_mainLoop != IntPtr.Zero)
            {
                PipeWireNativeMethods.pw_main_loop_destroy(
                    _mainLoop);

                _mainLoop = IntPtr.Zero;
            }


            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }


            PipeWireNativeMethods.pw_deinit();


            _connected = false;
        }



        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PipeWireNativeClient));
            }
        }



        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }


            _disposed = true;


            _logger.LogInformation(
                "Disposing PipeWire native client.");


            CleanupNativeResources();


            return ValueTask.CompletedTask;
        }
    }

    public sealed class PipeWireBufferEventArgs : EventArgs
    {
        private readonly Memory<byte> _buffer;


        private int _dataLength;



        internal PipeWireBufferEventArgs(
            Memory<byte> buffer)
        {
            _buffer = buffer;
        }



        /// <summary>
        /// Gets the writable PipeWire buffer memory.
        /// </summary>
        public Memory<byte> Buffer =>
            _buffer;



        /// <summary>
        /// Gets the number of valid PCM bytes written.
        /// </summary>
        public int DataLength =>
            _dataLength;



        /// <summary>
        /// Copies PCM data into the PipeWire buffer.
        /// Remaining space is filled with silence.
        /// </summary>
        public void CopyFrom(
            ReadOnlyMemory<byte> source)
        {
            var length =
                Math.Min(
                    source.Length,
                    _buffer.Length);


            source
                .Slice(
                    0,
                    length)
                .CopyTo(
                    _buffer);


            _dataLength =
                length;


            if (length < _buffer.Length)
            {
                FillSilence(
                    length,
                    _buffer.Length - length);
            }
        }



        /// <summary>
        /// Clears the buffer with PCM silence.
        /// For signed 16-bit PCM this is zero.
        /// </summary>
        public void FillSilence()
        {
            FillSilence(
                0,
                _buffer.Length);
        }



        private void FillSilence(
            int offset,
            int length)
        {
            _buffer
                .Slice(
                    offset,
                    length)
                .Span
                .Clear();


            _dataLength =
                _buffer.Length;
        }
    }
}
