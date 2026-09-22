namespace AIRadio.Server.Models.Alarms;

public sealed class RecurrenceTimeRange
{
    public SchedulePatternType Type { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    // Exact local due time for relative one-shot timers such as "in 5 minutes".
    public DateTime? DueAt { get; set; }

    public TimeSpan? TimeOfDay { get; set; }
    public DayOfWeek[] DaysOfWeek { get; set; } = [];
    public int? Month { get; set; }
    public int? DayOfMonth { get; set; }
    public int? WeekOfMonth { get; set; }
    public DayOfWeek? WeekdayOfMonth { get; set; }
}
