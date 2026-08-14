using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Services.Radio;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIRadio.Server.Services.Alarms
{
    public interface IAlarmManagerService
    {
        IReadOnlyList<ScheduledEvent> GetEvents(
            DateTime? timestamp = null);

        ScheduledEvent? GetEvent(Guid id);

        ScheduledEvent AddEvent(
            ScheduledEvent scheduledEvent);

        bool UpdateEvent(
            ScheduledEvent scheduledEvent);

        bool DeleteEvent(
            Guid id);

        bool EnableEvent(Guid id);

        bool DisableEvent(Guid id);
    }

    public sealed class AlarmManagerService :
        BackgroundService,
        IAlarmManagerService
    {
        private static readonly TimeSpan AlarmInterval =
            TimeSpan.FromMinutes(1);

        private readonly IConfiguration _configuration;
        private readonly IRadioEngineService _radioEngine;
        private readonly ILogger<AlarmManagerService> _logger;

        private readonly string _dataFile;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly object _sync = new();

        private List<ScheduledEvent> _events = [];

        /// <summary>
        /// Events that have already been activated during the
        /// current active minute.
        ///
        /// This is intentionally runtime-only state.
        /// </summary>
        private readonly HashSet<Guid> _activatedEvents = [];

        public AlarmManagerService(
            IConfiguration configuration,
            IRadioEngineService radioEngine,
            ILogger<AlarmManagerService> logger)
        {
            _configuration = configuration;
            _radioEngine = radioEngine;
            _logger = logger;

            _dataFile = ResolveDataFile(
                configuration);
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            await LoadAsync(
                stoppingToken);

            _logger.LogInformation(
                "Alarm manager started with {Count} scheduled events.",
                _events.Count);

            while (!stoppingToken.IsCancellationRequested)
            {
                var loopStart = DateTime.Now;

                try
                {
                    await ProcessEventsAsync(
                        loopStart,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing scheduled events.");
                }

                /*
                 * Calculate the next minute boundary from the
                 * beginning of this polling cycle.
                 *
                 * If processing crossed the boundary, delay is
                 * zero/negative and the next iteration happens
                 * immediately.
                 */
                var now = DateTime.Now;

                var nextMinute = new DateTime(
                    loopStart.Year,
                    loopStart.Month,
                    loopStart.Day,
                    loopStart.Hour,
                    loopStart.Minute,
                    0,
                    loopStart.Kind)
                    .AddMinutes(1);

                var delay = nextMinute - now;

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(
                            delay,
                            stoppingToken);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation(
                "Alarm manager stopped.");
        }

        public IReadOnlyList<ScheduledEvent> GetEvents(
            DateTime? timestamp = null)
        {
            var now = timestamp ?? DateTime.Now;

            lock (_sync)
            {
                return _events
                    .Where(x => x.Enabled)
                    .Where(x => IsActive(
                        x,
                        now))
                    .Select(Clone)
                    .ToList();
            }
        }

        public ScheduledEvent? GetEvent(Guid id)
        {
            lock (_sync)
            {
                var scheduledEvent = _events
                    .FirstOrDefault(x => x.Id == id);

                return scheduledEvent is null
                    ? null
                    : Clone(scheduledEvent);
            }
        }

        public ScheduledEvent AddEvent(
            ScheduledEvent scheduledEvent)
        {
            ArgumentNullException.ThrowIfNull(
                scheduledEvent);

            if (scheduledEvent.Id == Guid.Empty)
            {
                scheduledEvent.Id = Guid.NewGuid();
            }

            if (string.IsNullOrWhiteSpace(
                    scheduledEvent.Content))
            {
                throw new ArgumentException(
                    "Event content cannot be empty.",
                    nameof(scheduledEvent));
            }

            lock (_sync)
            {
                _events.Add(
                    Clone(scheduledEvent));
            }

            Save();

            _logger.LogInformation(
                "Added scheduled event {EventId}.",
                scheduledEvent.Id);

            return Clone(scheduledEvent);
        }

        public bool UpdateEvent(
            ScheduledEvent scheduledEvent)
        {
            ArgumentNullException.ThrowIfNull(
                scheduledEvent);

            lock (_sync)
            {
                var index = _events.FindIndex(
                    x => x.Id == scheduledEvent.Id);

                if (index < 0)
                {
                    return false;
                }

                _events[index] = Clone(
                    scheduledEvent);

                /*
                 * Updating an event should reset any runtime
                 * activation state associated with it.
                 */
                _activatedEvents.Remove(
                    scheduledEvent.Id);
            }

            Save();

            _logger.LogInformation(
                "Updated scheduled event {EventId}.",
                scheduledEvent.Id);

            return true;
        }

        public bool DeleteEvent(Guid id)
        {
            lock (_sync)
            {
                var removed = _events.RemoveAll(
                    x => x.Id == id) > 0;

                if (!removed)
                {
                    return false;
                }

                _activatedEvents.Remove(id);
            }

            Save();

            _logger.LogInformation(
                "Deleted scheduled event {EventId}.",
                id);

            return true;
        }

        public bool EnableEvent(Guid id)
        {
            return SetEnabled(
                id,
                true);
        }

        public bool DisableEvent(Guid id)
        {
            return SetEnabled(
                id,
                false);
        }

        private static string ResolveDataFile(
            IConfiguration configuration)
        {
            var path = configuration[
                "AlarmManager:DataFile"];

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "AlarmManager:DataFile is not configured.");
            }

            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path));
        }

        private async Task LoadAsync(
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_dataFile))
            {
                _logger.LogInformation(
                    "Alarm data file does not exist. " +
                    "Starting with no scheduled events.");

                lock (_sync)
                {
                    _events = [];
                }

                return;
            }

            try
            {
                await using var stream =
                    File.OpenRead(_dataFile);

                var events =
                    await JsonSerializer.DeserializeAsync<
                        List<ScheduledEvent>>(
                        stream,
                        _jsonOptions,
                        cancellationToken);

                lock (_sync)
                {
                    _events = events ?? [];
                }

                _logger.LogInformation(
                    "Loaded {Count} scheduled events.",
                    _events.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to load alarm data from {File}.",
                    _dataFile);

                throw;
            }
        }

        private void Save()
        {
            List<ScheduledEvent> events;

            lock (_sync)
            {
                events = _events
                    .Select(Clone)
                    .ToList();
            }

            var directory =
                Path.GetDirectoryName(_dataFile);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryFile =
                _dataFile + ".tmp";

            var json = JsonSerializer.Serialize(
                events,
                _jsonOptions);

            File.WriteAllText(
                temporaryFile,
                json);

            File.Move(
                temporaryFile,
                _dataFile,
                true);
        }

        private static ScheduledEvent Clone(
            ScheduledEvent source)
        {
            return new ScheduledEvent
            {
                Id = source.Id,
                Type = source.Type,
                Content = source.Content,
                Schedule = new SchedulePattern
                {
                    Type = source.Schedule.Type,
                    Date = source.Schedule.Date,
                    TimeOfDay = source.Schedule.TimeOfDay,
                    DaysOfWeek =
                        [.. source.Schedule.DaysOfWeek],
                    Month = source.Schedule.Month,
                    DayOfMonth = source.Schedule.DayOfMonth,
                    WeekOfMonth = source.Schedule.WeekOfMonth,
                    WeekdayOfMonth =
                        source.Schedule.WeekdayOfMonth
                },
                StartOffset = source.StartOffset,
                EndOffset = source.EndOffset,
                Enabled = source.Enabled,
                CreatedAt = source.CreatedAt
            };
        }

        private async Task ProcessEventsAsync(
            DateTime now,
            CancellationToken cancellationToken)
        {
            List<ScheduledEvent> eventsToActivate;

            lock (_sync)
            {
                eventsToActivate = [];

                foreach (var scheduledEvent in _events)
                {
                    if (!scheduledEvent.Enabled)
                    {
                        _activatedEvents.Remove(
                            scheduledEvent.Id);

                        continue;
                    }

                    /*
                     * Reminders are not alarms and are never
                     * directly activated by AlarmManager.
                     */
                    if (scheduledEvent.Type !=
                        ScheduledEventType.Alarm)
                    {
                        continue;
                    }

                    var active = IsActive(
                        scheduledEvent,
                        now);

                    if (active)
                    {
                        if (_activatedEvents.Add(
                                scheduledEvent.Id))
                        {
                            eventsToActivate.Add(
                                Clone(scheduledEvent));
                        }
                    }
                    else
                    {
                        /*
                         * Once the alarm's minute has passed,
                         * allow the event to activate again the
                         * next time its recurrence occurs.
                         */
                        _activatedEvents.Remove(
                            scheduledEvent.Id);
                    }
                }
            }

            foreach (var scheduledEvent in eventsToActivate)
            {
                await ActivateAlarmAsync(
                    scheduledEvent,
                    cancellationToken);
            }
        }

        private static bool IsActive(
            ScheduledEvent scheduledEvent,
            DateTime timestamp)
        {
            if (!scheduledEvent.Enabled)
            {
                return false;
            }

            if (scheduledEvent.Type ==
                ScheduledEventType.Reminder)
            {
                return IsReminderActive(
                    scheduledEvent,
                    DateOnly.FromDateTime(timestamp));
            }

            return IsAlarmActive(
                scheduledEvent,
                timestamp);
        }

        private async Task ActivateAlarmAsync(
            ScheduledEvent scheduledEvent,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Activating alarm {EventId}: {Content}",
                scheduledEvent.Id,
                scheduledEvent.Content);

            try
            {
                await _radioEngine.RenderAlarmAsync(
                    scheduledEvent.Content,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to render alarm {EventId}.",
                    scheduledEvent.Id);
            }
        }

        private static bool IsAlarmActive(
            ScheduledEvent scheduledEvent,
            DateTime timestamp)
        {
            var schedule = scheduledEvent.Schedule;

            if (!schedule.TimeOfDay.HasValue)
            {
                return false;
            }

            if (!MatchesDate(
                    schedule,
                    DateOnly.FromDateTime(timestamp)))
            {
                return false;
            }

            var alarmTime = schedule.TimeOfDay.Value;

            return timestamp.TimeOfDay >= alarmTime &&
                   timestamp.TimeOfDay <
                   alarmTime.Add(
                       TimeSpan.FromMinutes(1));
        }

        private static bool MatchesDate(
            SchedulePattern schedule,
            DateOnly date)
        {
            return schedule.Type switch
            {
                SchedulePatternType.Once =>
                    schedule.Date == date,

                SchedulePatternType.Daily =>
                    true,

                SchedulePatternType.Weekly =>
                    schedule.DaysOfWeek
                        .Contains(date.DayOfWeek),

                SchedulePatternType.Monthly =>
                    MatchesMonthly(
                        schedule,
                        date),

                SchedulePatternType.Yearly =>
                    MatchesYearly(
                        schedule,
                        date),

                _ => false
            };
        }

        private static bool MatchesMonthly(
            SchedulePattern schedule,
            DateOnly date)
        {
            if (schedule.WeekOfMonth.HasValue &&
                schedule.WeekdayOfMonth.HasValue)
            {
                if (date.DayOfWeek !=
                    schedule.WeekdayOfMonth.Value)
                {
                    return false;
                }

                var occurrence =
                    ((date.Day - 1) / 7) + 1;

                if (schedule.WeekOfMonth.Value > 0)
                {
                    return occurrence ==
                           schedule.WeekOfMonth.Value;
                }

                /*
                 * -1 means last occurrence of the weekday
                 * in the month.
                 */
                return date.AddDays(7).Month !=
                       date.Month;
            }

            return schedule.DayOfMonth.HasValue &&
                   date.Day ==
                   schedule.DayOfMonth.Value;
        }

        private static bool IsReminderActive(
    ScheduledEvent scheduledEvent,
    DateOnly date)
        {
            var eventDate = GetOccurrenceDate(
                scheduledEvent.Schedule,
                date);

            if (!eventDate.HasValue)
            {
                return false;
            }

            var startDate = eventDate.Value.AddDays(
                scheduledEvent.StartOffset);

            var endDate = eventDate.Value.AddDays(
                scheduledEvent.EndOffset);

            return date >= startDate &&
                   date <= endDate;
        }

        private static bool MatchesYearly(
            SchedulePattern schedule,
            DateOnly date)
        {
            return schedule.Month.HasValue &&
                   schedule.DayOfMonth.HasValue &&
                   date.Month ==
                   schedule.Month.Value &&
                   date.Day ==
                   schedule.DayOfMonth.Value;
        }

        private static DateOnly? GetOccurrenceDate(
    SchedulePattern schedule,
    DateOnly date)
        {
            if (MatchesDate(schedule, date))
            {
                return date;
            }

            return null;
        }

        private bool SetEnabled(
            Guid id,
            bool enabled)
        {
            lock (_sync)
            {
                var scheduledEvent = _events
                    .FirstOrDefault(x => x.Id == id);

                if (scheduledEvent is null)
                {
                    return false;
                }

                scheduledEvent.Enabled = enabled;

                if (!enabled)
                {
                    _activatedEvents.Remove(id);
                }
            }

            Save();

            return true;
        }
    }
}
