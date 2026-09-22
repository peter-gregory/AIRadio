namespace AIRadio.Server.Models.Alarms
{
    public sealed class ScheduledEvent
    {
        public Guid Id { get; set; }
        public ScheduledEventType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public SchedulePattern Schedule { get; set; } = new();

        // Used by reminders. Ignored for alarms.
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }

        public bool Enabled { get; set; } = true;
        public DateTime CreatedAt { get; set; }

        // Alarm occurrences can be suppressed without changing the recurrence.
        // An exclusion may be a finite date range or a recurring calendar rule.
        public List<ExclusionRule> Exclusions { get; set; } = [];
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

    public sealed class SchedulePattern
    {
        public SchedulePatternType Type { get; set; }
        public DateOnly? Date { get; set; }
        public TimeSpan? TimeOfDay { get; set; }
        public DayOfWeek[] DaysOfWeek { get; set; } = [];
        public int? Month { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public DayOfWeek? WeekdayOfMonth { get; set; }
    }

    public enum ExclusionRuleType
    {
        DateRange,
        Daily,
        Weekly,
        Monthly,
        Yearly
    }

    public sealed class ExclusionRule
    {
        public ExclusionRuleType Type { get; set; }

        // Used by DateRange. Start and end are inclusive.
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }

        // Used by Weekly.
        public DayOfWeek[] DaysOfWeek { get; set; } = [];

        // Used by Monthly/Yearly.
        public int? Month { get; set; }
        public int? DayOfMonth { get; set; }

        // Used by Monthly ordinal rules, e.g. fourth Thursday in November.
        public int? WeekOfMonth { get; set; }
        public DayOfWeek? WeekdayOfMonth { get; set; }
    }
}
