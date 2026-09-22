using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Services.Radio;

namespace AIRadio.Server.Services.Alarms
{
    public sealed class AlarmManager : BackgroundService
    {
        private readonly IAlarmService _alarmService;
        private readonly IRadioManagerService _radioManager;
        private readonly IConversationService _conversationService;
        private readonly ILogger<AlarmManager> _logger;
        private TaskCompletionSource<bool>? _conversationCompletion;
        private Guid _activeConversationId;

        public AlarmManager(
            IAlarmService alarmService,
            IRadioManagerService radioManager,
            IConversationService conversationService,
            ILogger<AlarmManager> logger)
        {
            _alarmService = alarmService;
            _radioManager = radioManager;
            _conversationService = conversationService;
            _logger = logger;
            _conversationService.StateChanged += OnConversationStateChanged;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Alarm manager started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;

                try
                {
                    await ProcessAlarmsAsync(now, stoppingToken);
                    _alarmService.RemoveExpiredAlarms(now);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing scheduled alarms.");
                }

                var nextMinute = new DateTime(
                    DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                    DateTime.Now.Hour, DateTime.Now.Minute, 0).AddMinutes(1);
                var delay = nextMinute - DateTime.Now;

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);
            }

            _logger.LogInformation("Alarm manager stopped.");
        }

        private async Task ProcessAlarmsAsync(
            DateTime timestamp,
            CancellationToken cancellationToken)
        {
            var activeAlarms = _alarmService.GetActiveAlarms(timestamp);

            foreach (var alarm in activeAlarms)
            {
                _logger.LogInformation(
                    "Activating alarm {Id} with {ActionCount} actions.",
                    alarm.Id,
                    alarm.Actions.Count);

                try
                {
                    foreach (var action in alarm.Actions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        _logger.LogInformation(
                            "Executing alarm {Id} action: {Action}",
                            alarm.Id,
                            action);

                        _conversationCompletion = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _activeConversationId = Guid.Empty;

                        var conversationId = await _radioManager.ProcessAlarmAsync(
                            action,
                            cancellationToken);

                        // Processing normally arrives before ProcessAlarmAsync
                        // returns, but keep the returned identity as the
                        // authoritative fallback for the event correlation.
                        _activeConversationId = conversationId;

                        await _conversationCompletion.Task.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    _conversationCompletion = null;
                    _activeConversationId = Guid.Empty;

                    // A one-shot alarm is consumed even if an action fails so
                    // a failed action does not repeat on the next poll.
                    if (alarm.When.Type == SchedulePatternType.Once)
                        _alarmService.DisableEvent(alarm.Id);
                }
            }
        }

        private void OnConversationStateChanged(
            object? sender,
            ConversationStateChangedEventArgs args)
        {
            switch (args.Current)
            {
                case ConversationState.Processing:
                    _activeConversationId = args.ConversationId;
                    _logger.LogDebug(
                        "Alarm manager observed conversation {ConversationId} entering Processing.",
                        args.ConversationId);
                    break;

                case ConversationState.WaitingForInput:
                    _logger.LogDebug(
                        "Alarm manager observed conversation {ConversationId} waiting for user input.",
                        args.ConversationId);
                    break;

                case ConversationState.Complete:
                    if (_conversationCompletion is not null &&
                        _activeConversationId == args.ConversationId)
                    {
                        _logger.LogDebug(
                            "Alarm manager observed conversation {ConversationId} complete; releasing current action.",
                            args.ConversationId);
                        _conversationCompletion.TrySetResult(true);
                    }
                    break;
            }
        }

        public override void Dispose()
        {
            _conversationService.StateChanged -= OnConversationStateChanged;
            _conversationCompletion?.TrySetCanceled();
            base.Dispose();
        }
    }
}
