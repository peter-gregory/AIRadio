using System.Text.Json;
using AIRadio.Server.Models.Alarms;

namespace AIRadio.Server.Services.Alarms;

public interface IAlarmService
{
    IReadOnlyList<ScheduledEvent> GetEvents(DateTime? timestamp = null);
    IReadOnlyList<ScheduledEvent> GetActiveAlarms(DateTime? timestamp = null);
    ScheduledEvent? GetEvent(Guid id);
    ScheduledEvent AddEvent(ScheduledEvent scheduledEvent);
    bool DeleteEvent(Guid id);
    bool EnableEvent(Guid id);
    bool DisableEvent(Guid id);
    bool AddExclusion(Guid alarmId, RecurrenceTimeRange exclusion);
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

    public AlarmService(IConfiguration configuration, ILogger<AlarmService> logger)
    {
        _logger = logger;
        var dataDirectory = configuration["Application:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new InvalidOperationException("Application:DataDirectory is not configured.");

        _dataFile = Path.GetFullPath(Path.Combine(dataDirectory, DataFileName));
        Directory.CreateDirectory(Path.GetDirectoryName(_dataFile)!);
        Load();
        _logger.LogInformation("Alarm service initialized with {Count} scheduled events.", _events.Count);
    }

    public IReadOnlyList<ScheduledEvent> GetEvents(DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;
        lock (_sync)
            return _events.Where(x => x.Enabled && Matches(x, now)).Select(Clone).ToList();
    }

    public IReadOnlyList<ScheduledEvent> GetActiveAlarms(DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;
        lock (_sync)
            return _events
                .Where(x => x.Enabled && x.Type == ScheduledEventType.Alarm && Matches(x, now))
                .Select(Clone)
                .ToList();
    }

    public ScheduledEvent? GetEvent(Guid id)
    {
        lock (_sync)
        {
            var value = _events.FirstOrDefault(x => x.Id == id);
            return value is null ? null : Clone(value);
        }
    }

    public ScheduledEvent AddEvent(ScheduledEvent scheduledEvent)
    {
        ArgumentNullException.ThrowIfNull(scheduledEvent);
        if (scheduledEvent.Id == Guid.Empty)
            throw new ArgumentException("A scheduled event ID is required.");
        if (string.IsNullOrWhiteSpace(scheduledEvent.Content))
            throw new ArgumentException("Event content cannot be empty.");
        ValidateRange(scheduledEvent.When, scheduledEvent.Type);

        lock (_sync)
            _events.Add(Clone(scheduledEvent));

        Save();
        return Clone(scheduledEvent);
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

    public bool AddExclusion(Guid alarmId, RecurrenceTimeRange exclusion)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        lock (_sync)
        {
            var alarm = _events.FirstOrDefault(x => x.Id == alarmId && x.Type == ScheduledEventType.Alarm);
            if (alarm is null)
                return false;
            alarm.Exclusions.Add(Clone(exclusion));
        }
        Save();
        return true;
    }

    private static bool Matches(ScheduledEvent item, DateTime timestamp)
    {
        var date = DateOnly.FromDateTime(timestamp);
        if (item.Exclusions.Any(x => MatchesDate(x, date)))
            return false;

        if (!MatchesDate(item.When, date))
            return false;

        if (item.Type == ScheduledEventType.Reminder)
            return true;

        var time = item.When.TimeOfDay;
        return time.HasValue &&
               timestamp.TimeOfDay >= time.Value &&
               timestamp.TimeOfDay < time.Value.Add(TimeSpan.FromMinutes(1));
    }

    private static bool MatchesDate(RecurrenceTimeRange range, DateOnly date)
    {
        if (range.StartDate.HasValue && date < range.StartDate.Value)
            return false;
        if (range.EndDate.HasValue && date > range.EndDate.Value)
            return false;

        return range.Type switch
        {
            SchedulePatternType.Once =>
                range.StartDate == date,

            SchedulePatternType.Daily => true,

            SchedulePatternType.Weekly =>
                range.DaysOfWeek.Contains(date.DayOfWeek),

            SchedulePatternType.Monthly =>
                MatchesMonthly(range, date),

            SchedulePatternType.Yearly =>
                MatchesYearly(range, date),

            _ => false
        };
    }

    private static bool MatchesMonthly(RecurrenceTimeRange range, DateOnly date)
    {
        if (range.Month.HasValue && date.Month != range.Month.Value)
            return false;

        if (range.WeekOfMonth.HasValue && range.WeekdayOfMonth.HasValue)
        {
            if (date.DayOfWeek != range.WeekdayOfMonth.Value)
                return false;

            var occurrence = ((date.Day - 1) / 7) + 1;
            return range.WeekOfMonth.Value > 0
                ? occurrence == range.WeekOfMonth.Value
                : date.AddDays(7).Month != date.Month;
        }

        return range.DayOfMonth.HasValue && date.Day == range.DayOfMonth.Value;
    }

    private static bool MatchesYearly(RecurrenceTimeRange range, DateOnly date)
    {
        if (!range.Month.HasValue || date.Month != range.Month.Value)
            return false;

        if (range.WeekOfMonth.HasValue && range.WeekdayOfMonth.HasValue)
        {
            if (date.DayOfWeek != range.WeekdayOfMonth.Value)
                return false;
            var occurrence = ((date.Day - 1) / 7) + 1;
            return range.WeekOfMonth.Value > 0
                ? occurrence == range.WeekOfMonth.Value
                : date.AddDays(7).Month != date.Month;
        }

        return range.DayOfMonth.HasValue && date.Day == range.DayOfMonth.Value;
    }

    private static void ValidateRange(RecurrenceTimeRange range, ScheduledEventType eventType)
    {
        if (range.StartDate.HasValue && range.EndDate.HasValue && range.EndDate < range.StartDate)
            throw new ArgumentException("The end date cannot be before the start date.");

        if (eventType == ScheduledEventType.Alarm && !range.TimeOfDay.HasValue)
            throw new ArgumentException("Alarms require a time.");

        if (eventType == ScheduledEventType.Reminder)
            range.TimeOfDay = null;
    }

    private bool SetEnabled(Guid id, bool enabled)
    {
        lock (_sync)
        {
            var item = _events.FirstOrDefault(x => x.Id == id);
            if (item is null)
                return false;
            item.Enabled = enabled;
        }
        Save();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_dataFile))
            return;
        _events = JsonSerializer.Deserialize<List<ScheduledEvent>>(File.ReadAllText(_dataFile), _jsonOptions) ?? [];
    }

    private void Save()
    {
        List<ScheduledEvent> snapshot;
        lock (_sync)
            snapshot = _events.Select(Clone).ToList();

        var temporary = _dataFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, _jsonOptions));
        File.Move(temporary, _dataFile, true);
    }

    private static ScheduledEvent Clone(ScheduledEvent source) => new()
    {
        Id = source.Id,
        Type = source.Type,
        Content = source.Content,
        When = Clone(source.When),
        Enabled = source.Enabled,
        CreatedAt = source.CreatedAt,
        Exclusions = source.Exclusions.Select(Clone).ToList()
    };

    private static RecurrenceTimeRange Clone(RecurrenceTimeRange source) => new()
    {
        Type = source.Type,
        StartDate = source.StartDate,
        EndDate = source.EndDate,
        TimeOfDay = source.TimeOfDay,
        DaysOfWeek = [.. source.DaysOfWeek],
        Month = source.Month,
        DayOfMonth = source.DayOfMonth,
        WeekOfMonth = source.WeekOfMonth,
        WeekdayOfMonth = source.WeekdayOfMonth
    };
}
