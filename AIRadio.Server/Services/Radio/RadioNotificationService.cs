using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Models.Radio;

namespace AIRadio.Server.Services.Radio;

public interface IRadioNotificationService
{
    Task HandleWebSocketAsync(WebSocket socket, CancellationToken cancellationToken = default);
}

public sealed class RadioNotificationService : IRadioNotificationService, IDisposable
{
    private readonly IMpvState _state;
    private readonly ILogger<RadioNotificationService> _logger;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly object _snapshotLock = new();
    private readonly SemaphoreSlim _broadcastLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private MpvStateSnapshot _previousSnapshot;
    private bool _disposed;

    public RadioNotificationService(
        IMpvState state,
        ILogger<RadioNotificationService> logger)
    {
        _state = state;
        _logger = logger;
        _previousSnapshot = CreateSnapshot();
        _state.StateChanged += OnStateChanged;
    }

    public async Task HandleWebSocketAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var clientId = Guid.NewGuid();
        _clients[clientId] = socket;

        _logger.LogInformation(
            "Radio WebSocket client connected. ClientId={ClientId}. Clients={ClientCount}.",
            clientId,
            _clients.Count);

        try
        {
            var buffer = new byte[1024];

            while (socket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(
                    buffer,
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // The browser only receives notifications. Ignore client messages.
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(
                ex,
                "Radio WebSocket client disconnected. ClientId={ClientId}.",
                clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);

            try
            {
                // During application shutdown, abort immediately rather than waiting
                // for a graceful WebSocket close to complete.
                if (_shutdownCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    socket.Abort();
                }
                else if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                }
            }
            catch
            {
                socket.Abort();
            }
            finally
            {
                socket.Dispose();
            }

            _logger.LogInformation(
                "Radio WebSocket client disconnected. ClientId={ClientId}. Clients={ClientCount}.",
                clientId,
                _clients.Count);
        }
    }

    private void OnStateChanged(
        object? sender,
        MpvStateChangedEventArgs e)
    {
        MpvStateSnapshot previous;
        MpvStateSnapshot current;

        lock (_snapshotLock)
        {
            previous = _previousSnapshot;
            current = e.State.CreateSnapshot();
            _previousSnapshot = current;
        }

        var notifications = BuildNotifications(previous, current);

        foreach (var notification in notifications)
        {
            _ = BroadcastAsync(notification);
        }
    }

    private MpvStateSnapshot CreateSnapshot()
    {
        return _state.CreateSnapshot();
    }

    private static IReadOnlyList<RadioNotification> BuildNotifications(
        MpvStateSnapshot previous,
        MpvStateSnapshot current)
    {
        var notifications = new List<RadioNotification>();

        if (!SamePlaylist(previous.RadioPlaylist, current.RadioPlaylist))
        {
            notifications.Add(
                new RadioNotification("stationPlaylistChanged"));
        }

        var previousStationId = previous.RadioStation?.Id;
        var currentStationId = current.RadioStation?.Id;

        if (!string.Equals(
                previousStationId,
                currentStationId,
                StringComparison.OrdinalIgnoreCase))
        {
            notifications.Add(
                new RadioNotification(
                    "selectedStationChanged",
                    currentStationId));
        }

        if (previous.IsPlaying != current.IsPlaying)
        {
            notifications.Add(
                new RadioNotification(
                    current.IsPlaying
                        ? "stationPlaybackStarted"
                        : "stationPlaybackStopped",
                    currentStationId));
        }

        var hasCurrentMetadata =
            !string.IsNullOrWhiteSpace(current.Title) ||
            !string.IsNullOrWhiteSpace(current.Artist) ||
            !string.IsNullOrWhiteSpace(current.Album);

        if (current.IsPlaying &&
            hasCurrentMetadata &&
            (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal) ||
             !string.Equals(previous.Artist, current.Artist, StringComparison.Ordinal) ||
             !string.Equals(previous.Album, current.Album, StringComparison.Ordinal)))
        {
            notifications.Add(
                new RadioNotification(
                    "songMetadataChanged",
                    currentStationId,
                    current.Title,
                    current.Artist,
                    current.Album));
        }

        if (previous.Volume != current.Volume)
        {
            notifications.Add(
                new RadioNotification(
                    "volumeChanged",
                    Volume: current.Volume));
        }

        return notifications;
    }

    private static bool SamePlaylist(
        IReadOnlyList<RadioStation> left,
        IReadOnlyList<RadioStation> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(
                    left[i].Id,
                    right[i].Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task BroadcastAsync(
        RadioNotification notification)
    {
        if (_disposed || _shutdownCts.IsCancellationRequested || _clients.IsEmpty)
        {
            return;
        }

        var shutdownToken = _shutdownCts.Token;
        var lockTaken = false;

        try
        {
            await _broadcastLock.WaitAsync(shutdownToken);
            lockTaken = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                notification,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

            var bytes = Encoding.UTF8.GetBytes(json);
            var deadClients = new List<Guid>();

            foreach (var client in _clients)
            {
                if (client.Value.State != WebSocketState.Open)
                {
                    deadClients.Add(client.Key);
                    continue;
                }

                try
                {
                    await client.Value.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        shutdownToken);
                }
                catch (Exception ex) when (
                    ex is WebSocketException or ObjectDisposedException)
                {
                    deadClients.Add(client.Key);
                }
            }

            foreach (var clientId in deadClients)
            {
                if (_clients.TryRemove(clientId, out var socket))
                {
                    socket.Dispose();
                }
            }
        }
        finally
        {
            if (lockTaken)
            {
                _broadcastLock.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.StateChanged -= OnStateChanged;

        // Cancel all in-flight sends and tell active WebSocket handlers to abort
        // rather than waiting for a graceful close during host shutdown.
        _shutdownCts.Cancel();

        foreach (var socket in _clients.Values)
        {
            try
            {
                socket.Abort();
            }
            catch
            {
            }
        }

        _clients.Clear();

        // Do not dispose the cancellation source or broadcast lock here. The
        // WebSocket handlers and fire-and-forget broadcasts may still be unwinding
        // after cancellation; both will be collected with the service when complete.
        // fire-and-forget and may still be unwinding after cancellation.
    }

    private sealed record RadioNotification(
        string Type,
        string? StationId = null,
        string? Title = null,
        string? Artist = null,
        string? Album = null,
        int? Volume = null);
}
