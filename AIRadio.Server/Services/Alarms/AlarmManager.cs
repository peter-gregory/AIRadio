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
                var now = TruncateToMinute(DateTime.Now);

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

                var nextMinute = now.AddMinutes(1);
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
                    "Activating alarm {Id}: {Content}",
                    alarm.Id,
                    alarm.Content);

                await _radioManager.RenderAlarmAsync(
                    alarm.Content,
                    cancellationToken);
            }
        }

        private static DateTime TruncateToMinute(DateTime timestamp) =>
            new(
                timestamp.Year,
                timestamp.Month,
                timestamp.Day,
                timestamp.Hour,
                timestamp.Minute,
                0,
                timestamp.Kind);
    }
}
