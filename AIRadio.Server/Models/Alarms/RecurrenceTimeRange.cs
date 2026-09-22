namespace AIRadio.Server.Models.Alarms;

public enum SchedulePatternType
{
    Once,
    Daily,
    Weekly,
    Monthly,
    Yearly
}

public sealed class RecurrenceTimeRange
{
    public SchedulePatternType Type { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    // Minute at which a relative one-shot timer is due.
    public DateTime? DueAt { get; set; }

    public TimeSpan? TimeOfDay { get; set; }
    public DayOfWeek[] DaysOfWeek { get; set; } = [];
    public int? Month { get; set; }
    public int? DayOfMonth { get; set; }
    public int? WeekOfMonth { get; set; }
    public DayOfWeek? WeekdayOfMonth { get; set; }
}
