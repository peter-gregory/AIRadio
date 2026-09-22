namespace AIRadio.Server.Models.Alarms;

public sealed class ScheduledEvent
{
    public Guid Id { get; init; }
    public ScheduledEventType Type { get; init; }
    public string Content { get; init; } = string.Empty;
    public RecurrenceTimeRange When { get; init; } = new();
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; init; }

    // Exclusions are recurrence rules, not expanded occurrence dates.
    // This keeps recurring alarms finite in storage and allows rules such as
    // "every Monday" or "the fourth Thursday of November".
    public List<RecurrenceTimeRange> Exclusions { get; init; } = [];
}

public enum ScheduledEventType
{
    Alarm,
    Reminder
}

public enum SchedulePatternType
{
    Once,
    Daily,
    Weekly,
    Monthly,
    Yearly
}
