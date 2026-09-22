using AIRadio.Server.Models.Alarms;
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

                        await _radioManager.ProcessSpeechAsync(
                            action,
                            cancellationToken);
                    }
                }
                finally
                {
                    // A one-shot alarm is consumed even if an action fails so
                    // a failed action does not repeat on the next poll.
                    if (alarm.When.Type == SchedulePatternType.Once)
                        _alarmService.DisableEvent(alarm.Id);
                }
            }
        }
    }
}
