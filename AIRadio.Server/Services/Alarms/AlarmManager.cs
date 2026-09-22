using AIRadio.Server.Services.Radio;

namespace AIRadio.Server.Services.Alarms
{
    public sealed class AlarmManager : BackgroundService
    {
        private readonly IAlarmService _alarmService;
        private readonly IRadioManagerService _radioManager;
        private readonly ILogger<AlarmManager> _logger;

        public AlarmManager(
            IAlarmService alarmService,
            IRadioManagerService radioManager,
            ILogger<AlarmManager> logger)
        {
            _alarmService = alarmService;
            _radioManager = radioManager;
            _logger = logger;
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
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing scheduled alarms.");
                }

                // Poll frequently enough to catch a relative timer whose due time
                // includes seconds, while still keeping the manager lightweight.
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
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
                    "Activating alarm {Id}: {Content}",
                    alarm.Id,
                    alarm.Content);

                await _radioManager.RenderAlarmAsync(
                    alarm.Content,
                    cancellationToken);

                // A one-shot alarm must not fire again on the next poll.
                if (alarm.When.Type == SchedulePatternType.Once)
                    _alarmService.DisableEvent(alarm.Id);
            }
        }

    }
}
