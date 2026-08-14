namespace AIRadio.Server.Models.Alarms
{
    public sealed class ScheduledEvent
    {
        /// <summary>
        /// Unique identifier for this scheduled event.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Determines how the event is handled.
        /// </summary>
        public ScheduledEventType Type { get; set; }

        /// <summary>
        /// The user's original request describing what should happen
        /// when this event becomes active.
        ///
        /// For alarms, this content is rendered when the alarm activates.
        ///
        /// For reminders, this content is supplied to the reminder
        /// report when the event is active.
        ///
        /// Content is not pre-expanded. The conversation system can
        /// interpret the content at execution time and generate
        /// multimedia tags such as {sound:...} and {report:...}.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Defines the date/time and recurrence pattern for the event.
        ///
        /// Alarms use TimeOfDay.
        /// Reminders are date-based and do not use TimeOfDay.
        /// </summary>
        public SchedulePattern Schedule { get; set; } = new();

        /// <summary>
        /// Number of calendar days before the scheduled event that
        /// a reminder becomes active.
        ///
        /// Examples:
        ///     0  = event day
        ///    -1  = day before
        ///    -6  = six days before
        ///
        /// Ignored for alarms.
        /// </summary>
        public int StartOffset { get; set; }

        /// <summary>
        /// Number of calendar days after the scheduled event through
        /// which a reminder remains active.
        ///
        /// Examples:
        ///     0  = event day only
        ///     1  = through the following day
        ///     6  = through six days after
        ///
        /// Must be greater than or equal to StartOffset.
        ///
        /// Ignored for alarms.
        /// </summary>
        public int EndOffset { get; set; }

        /// <summary>
        /// Determines whether this event participates in scheduling
        /// and reporting.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Timestamp at which the event was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }
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
        /// <summary>
        /// Defines the recurrence pattern.
        /// </summary>
        public SchedulePatternType Type { get; set; }

        /// <summary>
        /// Date for a one-time event.
        ///
        /// Used when Type is Once.
        /// </summary>
        public DateOnly? Date { get; set; }

        /// <summary>
        /// Time of day for an alarm.
        ///
        /// Reminders do not use a time of day because they are
        /// active for the entire calendar day.
        /// </summary>
        public TimeSpan? TimeOfDay { get; set; }

        /// <summary>
        /// Days of the week used by a Weekly schedule.
        ///
        /// Examples:
        /// Monday-Friday
        /// Monday, Wednesday, Friday
        /// </summary>
        public DayOfWeek[] DaysOfWeek { get; set; } = [];

        /// <summary>
        /// Month used by a Yearly schedule.
        ///
        /// 1 = January
        /// 12 = December
        /// </summary>
        public int? Month { get; set; }

        /// <summary>
        /// Day of the month used by Monthly or Yearly schedules.
        ///
        /// Valid values are 1-31.
        /// </summary>
        public int? DayOfMonth { get; set; }

        /// <summary>
        /// Ordinal week within a month.
        ///
        /// 1  = first
        /// 2  = second
        /// 3  = third
        /// 4  = fourth
        /// -1 = last
        ///
        /// Used with WeekdayOfMonth for patterns such as
        /// "the first Thursday of every month."
        /// </summary>
        public int? WeekOfMonth { get; set; }

        /// <summary>
        /// Day of the week used with WeekOfMonth.
        ///
        /// Example:
        /// WeekOfMonth = 1
        /// WeekdayOfMonth = Thursday
        ///
        /// Represents the first Thursday of the month.
        /// </summary>
        public DayOfWeek? WeekdayOfMonth { get; set; }
    }

}
