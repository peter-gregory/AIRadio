namespace AIRadio.Server.Models.Alarms;

public sealed class RecurrenceTimeRange
{
    public SchedulePatternType Type { get; set; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public TimeSpan? TimeOfDay { get; init; }
    public DayOfWeek[] DaysOfWeek { get; init; } = [];
    public int? Month { get; init; }
    public int? DayOfMonth { get; init; }
    public int? WeekOfMonth { get; init; }
    public DayOfWeek? WeekdayOfMonth { get; init; }

    public SchedulePattern ToSchedulePattern() => new()
    {
        Type = Type,
        Date = StartDate,
        TimeOfDay = TimeOfDay,
        DaysOfWeek = [.. DaysOfWeek],
        Month = Month,
        DayOfMonth = DayOfMonth,
        WeekOfMonth = WeekOfMonth,
        WeekdayOfMonth = WeekdayOfMonth
    };
}
