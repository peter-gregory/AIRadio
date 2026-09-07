using System.Runtime.InteropServices;

namespace AIRadio.Server.Services.Audio;

public interface IPipeWireNativeClient : IAsyncDisposable
{
    bool IsConnected { get; }
    ulong QueuedFrameCount { get; }
    ulong OutstandingFrameCount { get; }

    Task InitializeAsync(
        int sampleRate,
        short channels,
        short bitsPerSample,
        CancellationToken cancellationToken = default);

    Task EnqueueAsync(
        ReadOnlyMemory<byte> pcmData,
        CancellationToken cancellationToken = default);

    Task EnqueueAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> pcmSegments,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
    Task<int> GetVolumeAsync(CancellationToken cancellationToken = default);
}

public sealed class PipeWireNativeClient : IPipeWireNativeClient
{
    private readonly ILogger<PipeWireNativeClient> _logger;
    private readonly SemaphoreSlim _nativeCallLock = new(1, 1);
    private readonly object _completionLock = new();

    private IntPtr _client;
    private GCHandle _selfHandle;
    private PipeWireNativeMethods.PlaybackCallback? _playbackCallback;
    private PipeWireNativeMethods.ErrorCallback? _errorCallback;
    private TaskCompletionSource<object?>? _playbackCompletion;
    private bool _disposed;
    private bool _connected;
    private int _volume = 100;

    public PipeWireNativeClient(ILogger<PipeWireNativeClient> logger) =>
        _logger = logger;

    public bool IsConnected => Volatile.Read(ref _connected);

    public ulong QueuedFrameCount =>
        _client == IntPtr.Zero
            ? 0
            : PipeWireNativeMethods.airadio_pw_queued_frames(_client);

    public ulong OutstandingFrameCount =>
        _client == IntPtr.Zero
            ? 0
            : PipeWireNativeMethods.airadio_pw_outstanding_frames(_client);

    public Task InitializeAsync(
        int sampleRate,
        short channels,
        short bitsPerSample,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        if (bitsPerSample != 16)
            throw new NotSupportedException(
                "PipeWire native playback currently supports 16-bit PCM only.");

        if (IsConnected)
            return Task.CompletedTask;

        _client = PipeWireNativeMethods.airadio_pw_create(
            checked((uint)sampleRate),
            checked((uint)channels),
            checked((uint)bitsPerSample));

        if (_client == IntPtr.Zero)
            throw new InvalidOperationException(
                "Unable to create AIRadio PipeWire native client.");

        _selfHandle = GCHandle.Alloc(this);

        _playbackCallback = OnPlaybackComplete;
        _errorCallback = OnNativeError;

        var userData = GCHandle.ToIntPtr(_selfHandle);

        PipeWireNativeMethods.airadio_pw_set_playback_complete_callback(
            _client,
            _playbackCallback,
            userData);

        PipeWireNativeMethods.airadio_pw_set_error_callback(
            _client,
            _errorCallback,
            userData);

        try
        {
            var result = PipeWireNativeMethods.airadio_pw_start(_client);

            if (result < 0)
            {
                var message = GetNativeError();
                CleanupNative();

                throw new InvalidOperationException(
                    $"PipeWire native startup failed: {result}" +
                    (string.IsNullOrWhiteSpace(message) ? string.Empty : $" ({message})"));
            }

            Volatile.Write(ref _connected, true);

            _logger.LogInformation(
                "Native PipeWire playback connected: {Rate}Hz {Channels}ch {Bits}bit",
                sampleRate,
                channels,
                bitsPerSample);

            return Task.CompletedTask;
        }
        catch
        {
            CleanupNative();
            throw;
        }
    }

    public Task EnqueueAsync(
        ReadOnlyMemory<byte> pcmData,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(new[] { pcmData }, cancellationToken);

    public async Task EnqueueAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> pcmSegments,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException(
                "PipeWire native client is not connected.");

        if (pcmSegments.Count == 0)
            return;

        await _nativeCallLock.WaitAsync(cancellationToken);

        var handles = new GCHandle[pcmSegments.Count];
        var descriptorBytes = checked(
            Marshal.SizeOf<PipeWireNativeMethods.AIRadioPcmSegment>() *
            pcmSegments.Count);

        var descriptorMemory = Marshal.AllocHGlobal(descriptorBytes);

        try
        {
            for (var i = 0; i < pcmSegments.Count; i++)
            {
                var memory = pcmSegments[i];

                if (memory.Length == 0)
                {
                    WriteDescriptor(
                        descriptorMemory,
                        i,
                        default);

                    continue;
                }

                if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) ||
                    segment.Array is null)
                {
                    throw new NotSupportedException(
                        "PipeWire enqueue requires array-backed PCM memory.");
                }

                handles[i] = GCHandle.Alloc(
                    segment.Array,
                    GCHandleType.Pinned);

                var pointer = handles[i].AddrOfPinnedObject();
                pointer += segment.Offset;

                WriteDescriptor(
                    descriptorMemory,
                    i,
                    new PipeWireNativeMethods.AIRadioPcmSegment
                    {
                        Data = pointer,
                        Size = checked((nuint)memory.Length)
                    });
            }

            cancellationToken.ThrowIfCancellationRequested();

            var result = PipeWireNativeMethods.airadio_pw_enqueue(
                _client,
                descriptorMemory,
                checked((nuint)pcmSegments.Count));

            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"Native PipeWire enqueue failed: {result} ({GetNativeError()})");
            }
        }
        finally
        {
            for (var i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsAllocated)
                    handles[i].Free();
            }

            Marshal.FreeHGlobal(descriptorMemory);
            _nativeCallLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            return;

        Task completion;

        lock (_completionLock)
        {
            _playbackCompletion = CreateCompletionSource();
            completion = _playbackCompletion.Task;
        }

        await _nativeCallLock.WaitAsync(cancellationToken);

        try
        {
            var result = PipeWireNativeMethods.airadio_pw_clear(_client);

            if (result < 0)
            {
                lock (_completionLock)
                {
                    _playbackCompletion = null;
                }

                throw new InvalidOperationException(
                    $"Native PipeWire clear failed: {result} ({GetNativeError()})");
            }
        }
        finally
        {
            _nativeCallLock.Release();
        }

        await completion.WaitAsync(cancellationToken);
    }

    public async Task SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            return;

        var clamped = Math.Clamp(volume, 0, 100);

        await _nativeCallLock.WaitAsync(cancellationToken);

        try
        {
            var result = PipeWireNativeMethods.airadio_pw_set_volume(
                _client,
                clamped / 100f);

            if (result < 0)
                throw new InvalidOperationException(
                    $"Native PipeWire volume update failed: {result} ({GetNativeError()})");

            Volatile.Write(ref _volume, clamped);
        }
        finally
        {
            _nativeCallLock.Release();
        }
    }

    public Task<int> GetVolumeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _volume));
    }

    private void OnPlaybackComplete(IntPtr userData)
    {
        try
        {
            TaskCompletionSource<object?>? completion;

            lock (_completionLock)
            {
                completion = _playbackCompletion;
                _playbackCompletion = null;
            }

            completion?.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PipeWire playback completion callback failed.");
        }
    }

    private void OnNativeError(
        IntPtr userData,
        int errorCode,
        IntPtr message)
    {
        var text = message == IntPtr.Zero
            ? "Unknown native PipeWire error."
            : Marshal.PtrToStringAnsi(message) ?? "Unknown native PipeWire error.";

        _logger.LogError(
            "Native PipeWire error {Code}: {Message}",
            errorCode,
            text);
    }

    private static void WriteDescriptor(
        IntPtr baseAddress,
        int index,
        PipeWireNativeMethods.AIRadioPcmSegment descriptor)
    {
        var address = baseAddress +
            index * Marshal.SizeOf<PipeWireNativeMethods.AIRadioPcmSegment>();

        Marshal.StructureToPtr(descriptor, address, false);
    }

    private string GetNativeError()
    {
        if (_client == IntPtr.Zero)
            return string.Empty;

        var pointer = PipeWireNativeMethods.airadio_pw_last_error(_client);
        return pointer == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
    }

    private static TaskCompletionSource<object?> CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CleanupNative()
    {
        Volatile.Write(ref _connected, false);

        if (_client != IntPtr.Zero)
        {
            PipeWireNativeMethods.airadio_pw_set_playback_complete_callback(
                _client,
                null,
                IntPtr.Zero);

            PipeWireNativeMethods.airadio_pw_set_error_callback(
                _client,
                null,
                IntPtr.Zero);

            PipeWireNativeMethods.airadio_pw_destroy(_client);
            _client = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _playbackCallback = null;
        _errorCallback = null;
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

        lock (_completionLock)
        {
            var completion = _playbackCompletion;
            _playbackCompletion = null;
            completion?.TrySetCanceled();
        }

        CleanupNative();

        _nativeCallLock.Dispose();

        _logger.LogInformation("Native PipeWire client disposed.");
        return ValueTask.CompletedTask;
    }
}
