using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Services.Radio;

namespace AIRadio.Server.Services.Alarms
{
    public sealed class AlarmManager : BackgroundService
    {
        private readonly IAlarmService _alarmService;
        private readonly IRadioManagerService _radioManager;
        private readonly ILogger<AlarmManager> _logger;
        private readonly HashSet<Guid> _activatedEvents = [];

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
                var loopStart = DateTime.Now;
                try
                {
                    await ProcessEventsAsync(loopStart, stoppingToken);
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
                    loopStart.Year,
                    loopStart.Month,
                    loopStart.Day,
                    loopStart.Hour,
                    loopStart.Minute,
                    0,
                    loopStart.Kind).AddMinutes(1);

                var delay = nextMinute - DateTime.Now;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);
            }

            _logger.LogInformation("Alarm manager stopped.");
        }

        private async Task ProcessEventsAsync(DateTime now, CancellationToken cancellationToken)
        {
            var activeEvents = _alarmService.GetActiveAlarms(now);
            var activeIds = activeEvents.Select(x => x.Id).ToHashSet();
            _activatedEvents.RemoveWhere(id => !activeIds.Contains(id));

            foreach (var scheduledEvent in activeEvents)
            {
                if (!_activatedEvents.Add(scheduledEvent.Id))
                    continue;

                await _radioManager.RenderAlarmAsync(
                    scheduledEvent.Content,
                    cancellationToken);
            }
        }
    }
}
