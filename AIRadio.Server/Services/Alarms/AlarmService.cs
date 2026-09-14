using AIRadio.Server.Models.Alarms;
using System.Text.Json;

namespace AIRadio.Server.Services.Alarms
{
    public interface IAlarmService
    {
        IReadOnlyList<ScheduledEvent> GetEvents(DateTime? timestamp = null);
        IReadOnlyList<ScheduledEvent> GetActiveAlarms(DateTime? timestamp = null);
        ScheduledEvent? GetEvent(Guid id);
        ScheduledEvent AddEvent(ScheduledEvent scheduledEvent);
        bool UpdateEvent(ScheduledEvent scheduledEvent);
        bool DeleteEvent(Guid id);
        bool EnableEvent(Guid id);
        bool DisableEvent(Guid id);
    }

    public sealed class AlarmService : IAlarmService
    {
        private const string DataFileName = "scheduled-events.json";

        private readonly ILogger<AlarmService> _logger;
        private readonly string _dataFile;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        private readonly object _sync = new();
        private List<ScheduledEvent> _events = [];

        public AlarmService(
            IConfiguration configuration,
            ILogger<AlarmService> logger)
        {
            _logger = logger;
            _dataFile = ResolveDataFile(configuration);

            var directory = Path.GetDirectoryName(_dataFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            Load();
            _logger.LogInformation("Alarm service initialized with {Count} scheduled events.", _events.Count);
        }

        public IReadOnlyList<ScheduledEvent> GetEvents(DateTime? timestamp = null)
        {
            var now = timestamp ?? DateTime.Now;
            lock (_sync)
                return _events
                    .Where(x => x.Enabled && IsActive(x, now))
                    .Select(Clone)
                    .ToList();
        }

        public IReadOnlyList<ScheduledEvent> GetActiveAlarms(DateTime? timestamp = null)
        {
            var now = timestamp ?? DateTime.Now;
            lock (_sync)
                return _events
                    .Where(x => x.Enabled && x.Type == ScheduledEventType.Alarm && IsActive(x, now))
                    .Select(Clone)
                    .ToList();
        }

        public ScheduledEvent? GetEvent(Guid id)
        {
            lock (_sync)
            {
                var scheduledEvent = _events.FirstOrDefault(x => x.Id == id);
                return scheduledEvent is null ? null : Clone(scheduledEvent);
            }
        }

        public ScheduledEvent AddEvent(ScheduledEvent scheduledEvent)
        {
            ArgumentNullException.ThrowIfNull(scheduledEvent);
            if (scheduledEvent.Id == Guid.Empty)
                scheduledEvent.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(scheduledEvent.Content))
                throw new ArgumentException("Event content cannot be empty.", nameof(scheduledEvent));

            lock (_sync)
                _events.Add(Clone(scheduledEvent));

            Save();
            return Clone(scheduledEvent);
        }

        public bool UpdateEvent(ScheduledEvent scheduledEvent)
        {
            ArgumentNullException.ThrowIfNull(scheduledEvent);

            lock (_sync)
            {
                var index = _events.FindIndex(x => x.Id == scheduledEvent.Id);
                if (index < 0)
                    return false;

                _events[index] = Clone(scheduledEvent);
            }

            Save();
            return true;
        }

        public bool DeleteEvent(Guid id)
        {
            lock (_sync)
            {
                if (_events.RemoveAll(x => x.Id == id) == 0)
                    return false;
            }

            Save();
            return true;
        }

        public bool EnableEvent(Guid id) => SetEnabled(id, true);

        public bool DisableEvent(Guid id) => SetEnabled(id, false);

        private static string ResolveDataFile(IConfiguration configuration)
        {
            var dataDirectory = configuration["Application:DataDirectory"];
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new InvalidOperationException("Application:DataDirectory is not configured.");

            return Path.GetFullPath(Path.Combine(dataDirectory, DataFileName));
        }

        private void Load()
        {
            if (!File.Exists(_dataFile))
                return;

            var json = File.ReadAllText(_dataFile);
            var events = JsonSerializer.Deserialize<List<ScheduledEvent>>(json, _jsonOptions);
            _events = events ?? [];
        }

        private void Save()
        {
            List<ScheduledEvent> events;
            lock (_sync)
                events = _events.Select(Clone).ToList();

            var directory = Path.GetDirectoryName(_dataFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryFile = _dataFile + ".tmp";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(events, _jsonOptions));
            File.Move(temporaryFile, _dataFile, true);
        }

        private static bool IsActive(ScheduledEvent scheduledEvent, DateTime timestamp)
        {
            if (!scheduledEvent.Enabled)
                return false;

            if (scheduledEvent.Type == ScheduledEventType.Reminder)
                return IsReminderActive(scheduledEvent, DateOnly.FromDateTime(timestamp));

            var schedule = scheduledEvent.Schedule;
            if (!schedule.TimeOfDay.HasValue || !MatchesDate(schedule, DateOnly.FromDateTime(timestamp)))
                return false;

            var alarmTime = schedule.TimeOfDay.Value;
            return timestamp.TimeOfDay >= alarmTime &&
                   timestamp.TimeOfDay < alarmTime.Add(TimeSpan.FromMinutes(1));
        }

        private static bool MatchesDate(SchedulePattern schedule, DateOnly date) =>
            schedule.Type switch
            {
                SchedulePatternType.Once => schedule.Date == date,
                SchedulePatternType.Daily => true,
                SchedulePatternType.Weekly => schedule.DaysOfWeek.Contains(date.DayOfWeek),
                SchedulePatternType.Monthly => MatchesMonthly(schedule, date),
                SchedulePatternType.Yearly => MatchesYearly(schedule, date),
                _ => false
            };

        private static bool MatchesMonthly(SchedulePattern schedule, DateOnly date)
        {
            if (schedule.WeekOfMonth.HasValue && schedule.WeekdayOfMonth.HasValue)
            {
                if (date.DayOfWeek != schedule.WeekdayOfMonth.Value)
                    return false;

                var occurrence = ((date.Day - 1) / 7) + 1;
                return schedule.WeekOfMonth.Value > 0
                    ? occurrence == schedule.WeekOfMonth.Value
                    : date.AddDays(7).Month != date.Month;
            }

            return schedule.DayOfMonth.HasValue && date.Day == schedule.DayOfMonth.Value;
        }

        private static bool IsReminderActive(ScheduledEvent scheduledEvent, DateOnly date) =>
            scheduledEvent.Schedule.Date == date;

        private static bool MatchesYearly(SchedulePattern schedule, DateOnly date) =>
            schedule.Month.HasValue &&
            schedule.DayOfMonth.HasValue &&
            date.Month == schedule.Month.Value &&
            date.Day == schedule.DayOfMonth.Value;

        private bool SetEnabled(Guid id, bool enabled)
        {
            lock (_sync)
            {
                var scheduledEvent = _events.FirstOrDefault(x => x.Id == id);
                if (scheduledEvent is null)
                    return false;

                scheduledEvent.Enabled = enabled;
            }

            Save();
            return true;
        }

        private static ScheduledEvent Clone(ScheduledEvent source) => new()
        {
            Id = source.Id,
            Type = source.Type,
            Content = source.Content,
            Schedule = new SchedulePattern
            {
                Type = source.Schedule.Type,
                Date = source.Schedule.Date,
                TimeOfDay = source.Schedule.TimeOfDay,
                DaysOfWeek = [.. source.Schedule.DaysOfWeek],
                Month = source.Schedule.Month,
                DayOfMonth = source.Schedule.DayOfMonth,
                WeekOfMonth = source.Schedule.WeekOfMonth,
                WeekdayOfMonth = source.Schedule.WeekdayOfMonth
            },
            StartOffset = source.StartOffset,
            EndOffset = source.EndOffset,
            Enabled = source.Enabled,
            CreatedAt = source.CreatedAt
        };
    }
}
