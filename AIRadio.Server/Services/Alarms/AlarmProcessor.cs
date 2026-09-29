using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Services.Radio;
using System.Text.RegularExpressions;

namespace AIRadio.Server.Services.Alarms;

internal sealed class AlarmProcessor : IAsyncDisposable
{
    private readonly ScheduledEvent _alarm;
    private readonly Queue<string> _commands;
    private readonly IConversationService _conversationService;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Guid _activeConversationId;
    private bool _cancelled;
    private bool _completed;

    public AlarmProcessor(
        ScheduledEvent alarm,
        string preamble,
        IConversationService conversationService,
        ILogger logger)
    {
        _alarm = alarm;
        _conversationService = conversationService;
        _logger = logger;

        _commands = new Queue<string>();
        _commands.Enqueue(preamble);

        foreach (var action in alarm.Actions)
            _commands.Enqueue(AppendWakeUpSound(action));

        _conversationService.ConversationCompleted += OnConversationCompleted;
        _conversationService.ConversationCancelled += OnConversationCancelled;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await StartNextCommandAsync(cancellationToken);
        await _completion.Task.WaitAsync(cancellationToken);
    }

    private async Task StartNextCommandAsync(CancellationToken cancellationToken)
    {
        if (_completed || _cancelled)
            return;

        string command;
        lock (_commands)
        {
            if (_commands.Count == 0)
            {
                Complete();
                return;
            }

            command = _commands.Dequeue();
        }

        _logger.LogInformation(
            "Executing alarm {Id} command: {Command}",
            _alarm.Id,
            command);

        try
        {
            var conversationId = await _conversationService.StartAlarmAsync(
                command,
                cancellationToken);

            _activeConversationId = conversationId;

            // Close the race where the conversation completes before the
            // processor receives the returned conversation ID.
            if (_conversationService.State == ConversationState.Idle &&
                _conversationService.ConversationId == conversationId)
            {
                _ = HandleConversationCompletedAsync(conversationId);
            }
        }
        catch
        {
            Complete();
            throw;
        }
    }

    private void OnConversationCompleted(
        object? sender,
        ConversationCompletedEventArgs args)
    {
        if (args.ConversationId != _activeConversationId)
            return;

        _ = HandleConversationCompletedAsync(args.ConversationId);
    }

    private async Task HandleConversationCompletedAsync(Guid conversationId)
    {
        if (_completed || conversationId != _activeConversationId)
            return;

        _activeConversationId = Guid.Empty;

        if (_cancelled)
        {
            Complete();
            return;
        }

        bool hasMoreCommands;
        lock (_commands)
            hasMoreCommands = _commands.Count > 0;

        if (!hasMoreCommands)
        {
            Complete();
            return;
        }

        try
        {
            await StartNextCommandAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start the next command for alarm {Id}.",
                _alarm.Id);
            Complete();
        }
    }

    private void OnConversationCancelled(
        object? sender,
        ConversationCancelledEventArgs args)
    {
        if (_completed)
            return;

        _cancelled = true;

        lock (_commands)
            _commands.Clear();

        _logger.LogInformation(
            "Alarm {Id} cancelled; discarded all pending commands.",
            _alarm.Id);
    }

    private void Complete()
    {
        if (_completed)
            return;

        _completed = true;
        _activeConversationId = Guid.Empty;

        _conversationService.ConversationCompleted -= OnConversationCompleted;
        _conversationService.ConversationCancelled -= OnConversationCancelled;

        _logger.LogDebug(
            "Alarm {Id} command processing is complete.",
            _alarm.Id);

        _completion.TrySetResult(true);
    }

    private static string AppendWakeUpSound(string action)
    {
        if (!Regex.IsMatch(action, @"\bwake\s+up\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(action, @"\{sound:wake-up\}", RegexOptions.IgnoreCase))
        {
            return action;
        }

        return $"{action.Trim()} {{sound:wake-up}}";
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
