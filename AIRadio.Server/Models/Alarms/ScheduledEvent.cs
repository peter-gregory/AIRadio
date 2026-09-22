namespace AIRadio.Server.Models.Alarms;

public sealed class ScheduledEvent
{
    public Guid Id { get; init; }
    public ScheduledEventType Type { get; init; }

    // Content is used by reminders. Alarms store an ordered list of
    // human-language prompts in Actions so each action can use the normal
    // AIRadio intent/tool pipeline when the alarm fires.
    public string Content { get; init; } = string.Empty;
    public List<string> Actions { get; init; } = [];
    public RecurrenceTimeRange When { get; init; } = new();
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; init; }

    // Exclusions are recurrence rules, not expanded occurrence dates.
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
