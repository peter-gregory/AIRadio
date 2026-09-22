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
        bool AddExclusion(Guid alarmId, ExclusionRule exclusion);
        bool RemoveExclusion(Guid alarmId, int exclusionIndex);
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
            {
                return _events
                    .Where(x => x.Enabled && IsActive(x, now))
                    .Select(Clone)
                    .ToList();
            }
        }

        public IReadOnlyList<ScheduledEvent> GetActiveAlarms(DateTime? timestamp = null)
        {
            var now = timestamp ?? DateTime.Now;
            lock (_sync)
            {
                return _events
                    .Where(x =>
                        x.Enabled &&
                        x.Type == ScheduledEventType.Alarm &&
                        IsActive(x, now))
                    .Select(Clone)
                    .ToList();
            }
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
                throw new ArgumentException(
                    "Event content cannot be empty.",
                    nameof(scheduledEvent));

            if (scheduledEvent.CreatedAt == default)
                scheduledEvent.CreatedAt = DateTime.Now;

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

        public bool AddExclusion(Guid alarmId, ExclusionRule exclusion)
        {
            ArgumentNullException.ThrowIfNull(exclusion);

            lock (_sync)
            {
                var alarm = _events.FirstOrDefault(x =>
                    x.Id == alarmId &&
                    x.Type == ScheduledEventType.Alarm);

                if (alarm is null)
                    return false;

                alarm.Exclusions.Add(Clone(exclusion));
            }

            Save();
            return true;
        }

        public bool RemoveExclusion(Guid alarmId, int exclusionIndex)
        {
            lock (_sync)
            {
                var alarm = _events.FirstOrDefault(x =>
                    x.Id == alarmId &&
                    x.Type == ScheduledEventType.Alarm);

                if (alarm is null ||
                    exclusionIndex < 0 ||
                    exclusionIndex >= alarm.Exclusions.Count)
                    return false;

                alarm.Exclusions.RemoveAt(exclusionIndex);
            }

            Save();
            return true;
        }

        private static string ResolveDataFile(IConfiguration configuration)
        {
            var dataDirectory = configuration["Application:DataDirectory"];
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new InvalidOperationException(
                    "Application:DataDirectory is not configured.");

            return Path.GetFullPath(Path.Combine(dataDirectory, DataFileName));
        }

        private void Load()
        {
            if (!File.Exists(_dataFile))
                return;

            var json = File.ReadAllText(_dataFile);
            var events = JsonSerializer.Deserialize<List<ScheduledEvent>>(
                json,
                _jsonOptions);

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
            File.WriteAllText(
                temporaryFile,
                JsonSerializer.Serialize(events, _jsonOptions));

            File.Move(temporaryFile, _dataFile, true);
        }

        private static bool IsActive(
            ScheduledEvent scheduledEvent,
            DateTime timestamp)
        {
            if (!scheduledEvent.Enabled)
                return false;

            var date = DateOnly.FromDateTime(timestamp);

            if (scheduledEvent.Exclusions.Any(x => MatchesExclusion(x, date)))
                return false;

            if (scheduledEvent.Type == ScheduledEventType.Reminder)
                return scheduledEvent.Schedule.Date == date;

            var schedule = scheduledEvent.Schedule;

            if (!schedule.TimeOfDay.HasValue ||
                !MatchesDate(schedule, date))
                return false;

            var alarmTime = schedule.TimeOfDay.Value;

            return timestamp.TimeOfDay >= alarmTime &&
                   timestamp.TimeOfDay < alarmTime.Add(TimeSpan.FromMinutes(1));
        }

        private static bool MatchesDate(
            SchedulePattern schedule,
            DateOnly date) =>
            schedule.Type switch
            {
                SchedulePatternType.Once =>
                    schedule.Date == date,

                SchedulePatternType.Daily =>
                    true,

                SchedulePatternType.Weekly =>
                    schedule.DaysOfWeek.Contains(date.DayOfWeek),

                SchedulePatternType.Monthly =>
                    MatchesMonthly(schedule, date),

                SchedulePatternType.Yearly =>
                    MatchesYearly(schedule, date),

                _ => false
            };

        private static bool MatchesMonthly(
            SchedulePattern schedule,
            DateOnly date)
        {
            if (schedule.WeekOfMonth.HasValue &&
                schedule.WeekdayOfMonth.HasValue)
            {
                if (date.DayOfWeek != schedule.WeekdayOfMonth.Value)
                    return false;

                var occurrence = ((date.Day - 1) / 7) + 1;

                return schedule.WeekOfMonth.Value > 0
                    ? occurrence == schedule.WeekOfMonth.Value
                    : date.AddDays(7).Month != date.Month;
            }

            return schedule.DayOfMonth.HasValue &&
                   date.Day == schedule.DayOfMonth.Value;
        }

        private static bool MatchesYearly(
            SchedulePattern schedule,
            DateOnly date) =>
            schedule.Month.HasValue &&
            schedule.DayOfMonth.HasValue &&
            date.Month == schedule.Month.Value &&
            date.Day == schedule.DayOfMonth.Value;

        private static bool MatchesExclusion(
            ExclusionRule exclusion,
            DateOnly date) =>
            exclusion.Type switch
            {
                ExclusionRuleType.DateRange =>
                    exclusion.StartDate.HasValue &&
                    exclusion.EndDate.HasValue &&
                    date >= exclusion.StartDate.Value &&
                    date <= exclusion.EndDate.Value,

                ExclusionRuleType.Daily =>
                    true,

                ExclusionRuleType.Weekly =>
                    exclusion.DaysOfWeek.Contains(date.DayOfWeek),

                ExclusionRuleType.Monthly =>
                    MatchesMonthlyExclusion(exclusion, date),

                ExclusionRuleType.Yearly =>
                    MatchesYearlyExclusion(exclusion, date),

                _ => false
            };

        private static bool MatchesMonthlyExclusion(
            ExclusionRule exclusion,
            DateOnly date)
        {
            if (exclusion.Month.HasValue &&
                date.Month != exclusion.Month.Value)
                return false;

            if (exclusion.WeekOfMonth.HasValue &&
                exclusion.WeekdayOfMonth.HasValue)
            {
                if (date.DayOfWeek != exclusion.WeekdayOfMonth.Value)
                    return false;

                var occurrence = ((date.Day - 1) / 7) + 1;

                return exclusion.WeekOfMonth.Value > 0
                    ? occurrence == exclusion.WeekOfMonth.Value
                    : date.AddDays(7).Month != date.Month;
            }

            return exclusion.DayOfMonth.HasValue &&
                   date.Day == exclusion.DayOfMonth.Value;
        }

        private static bool MatchesYearlyExclusion(
            ExclusionRule exclusion,
            DateOnly date) =>
            exclusion.Month.HasValue &&
            exclusion.DayOfMonth.HasValue &&
            date.Month == exclusion.Month.Value &&
            date.Day == exclusion.DayOfMonth.Value;

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
            Schedule = Clone(source.Schedule),
            StartOffset = source.StartOffset,
            EndOffset = source.EndOffset,
            Enabled = source.Enabled,
            CreatedAt = source.CreatedAt,
            Exclusions = source.Exclusions.Select(Clone).ToList()
        };

        private static SchedulePattern Clone(SchedulePattern source) => new()
        {
            Type = source.Type,
            Date = source.Date,
            TimeOfDay = source.TimeOfDay,
            DaysOfWeek = [.. source.DaysOfWeek],
            Month = source.Month,
            DayOfMonth = source.DayOfMonth,
            WeekOfMonth = source.WeekOfMonth,
            WeekdayOfMonth = source.WeekdayOfMonth
        };

        private static ExclusionRule Clone(ExclusionRule source) => new()
        {
            Type = source.Type,
            StartDate = source.StartDate,
            EndDate = source.EndDate,
            DaysOfWeek = [.. source.DaysOfWeek],
            Month = source.Month,
            DayOfMonth = source.DayOfMonth,
            WeekOfMonth = source.WeekOfMonth,
            WeekdayOfMonth = source.WeekdayOfMonth
        };
    }
}
