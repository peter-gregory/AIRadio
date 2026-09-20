using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Alarms
{
    public sealed class ScheduleTool : ITool
    {
        private readonly IAlarmService _alarmService;

        public ScheduleTool(
            IAlarmService alarmService)
        {
            _alarmService = alarmService;
        }

        public string Name =>
            "schedule";
    public string Intent => "Manage scheduled alarms and reminders.";

        public string GetLlmInstructions()
        {
            return """
SCHEDULE TOOL

Purpose: Create, change, remove, enable, or disable scheduled alarms and reminders.
Use this tool when the user wants AIRadio to remember something for a future date/time or to manage an existing scheduled event.

The schedule tool uses the following syntax:
{tool:schedule,operation=<operation>,parameter=value}

Operations:
- add: Create a new alarm or reminder. Requires type, content, and pattern, plus the schedule fields required by that pattern.
- update: Modify an existing scheduled event. Requires id, type, content, and pattern. Include the schedule fields that should be set on the event.
- delete: Delete an existing scheduled event. Requires id.
- enable: Enable an existing scheduled event. Requires id.
- disable: Disable an existing scheduled event. Requires id.

Parameters:
- operation: Required. One of add, update, delete, enable, disable.
- type: Required for add/update. One of Alarm or Reminder.
- id: Required for update/delete/enable/disable. The event ID returned by an earlier schedule or events tool result. Do not invent an ID.
- content: Required for add/update. The message or instruction associated with the event. Preserve the user's requested wording and intent.
- pattern: Required for add/update. One of Once, Daily, Weekly, Monthly, Yearly.
- date: Optional date for a Once schedule. Use a calendar date such as 2026-09-20.
- timeOfDay: Optional time of day for an alarm, such as 07:30 or 7:30 AM. Alarms require this value. Reminders do not use timeOfDay.
- daysOfWeek: Required for a Weekly schedule. Give one or more weekday names in a quoted string, separated by spaces. Also accept commas or the word 'and'. Use Weekdays for Monday-Friday and Weekends for Saturday-Sunday. Examples: "Monday Wednesday Friday", "Monday and Thursday", "Weekdays", "Weekends".
- month: Optional month number 1-12 for a Yearly schedule. January is 1 and December is 12.
- dayOfMonth: Optional day of month 1-31 for Monthly or Yearly schedules.
- weekOfMonth: Optional week number for a monthly ordinal weekday schedule. Use 1, 2, 3, 4, or -1 for last.
- weekdayOfMonth: Optional weekday used with weekOfMonth, such as Monday or Thursday. For example, weekOfMonth=1 and weekdayOfMonth=Thursday means the first Thursday of the month.
- startOffset: Optional number of calendar days before the scheduled date when a reminder becomes active. 0 means the event day; -1 means one day before; -6 means six days before. Ignored for alarms.
- endOffset: Optional number of calendar days after the scheduled date through which a reminder remains active. 0 means event day only; 1 means through the following day; 6 means through six days after. Must be greater than or equal to startOffset. Ignored for alarms.
- enabled: Optional boolean. Defaults to true.

Event type rules:
- Alarm: requires timeOfDay and cannot use startOffset or endOffset.
- Reminder: does not use timeOfDay. startOffset and endOffset may define the reminder's active date range.

Pattern rules:
- Once: use date. For an alarm, also use timeOfDay.
- Daily: use timeOfDay for an alarm. A reminder is date-based and does not use timeOfDay.
- Weekly: use daysOfWeek. For an alarm, also use timeOfDay.
- Monthly: use dayOfMonth, or use weekOfMonth together with weekdayOfMonth. For an alarm, also use timeOfDay.
- Yearly: use month and dayOfMonth, or use month together with weekOfMonth and weekdayOfMonth. For an alarm, also use timeOfDay.

Important rules:
- Use operation=add for a new event. Do not use update unless an existing event ID is known.
- Use the events tool to find existing event IDs before updating, deleting, enabling, or disabling an event when the ID is not already known.
- Never invent an event ID.
- For alarms, timeOfDay is required and offsets must be zero or omitted.
- For reminders, timeOfDay is not used. Reminders are active by date and may use startOffset/endOffset.
- enabled defaults to true when omitted.
- Keep content concise but faithful to what the user wants AIRadio to say or do when the event becomes active.

Examples:
User: "Set an alarm for 7 AM tomorrow."
{tool:schedule,operation=add,type=Alarm,content="Wake me up",pattern=Once,date=2026-09-16,timeOfDay=07:00}

User: "Remind me to take out the trash tomorrow."
{tool:schedule,operation=add,type=Reminder,content="Take out the trash",pattern=Once,date=2026-09-16}

User: "Set a daily alarm for 6:30 AM."
{tool:schedule,operation=add,type=Alarm,content="Wake me up",pattern=Daily,timeOfDay=06:30}

User: "Remind me about the meeting every Monday."
{tool:schedule,operation=add,type=Reminder,content="Meeting",pattern=Weekly,daysOfWeek="Monday"}

User: "Remind me about trash day every Monday Wednesday and Friday."
{tool:schedule,operation=add,type=Reminder,content="Take out the trash",pattern=Weekly,daysOfWeek="Monday Wednesday Friday"}

User: "Remind me every weekday to check the mail."
{tool:schedule,operation=add,type=Reminder,content="Check the mail",pattern=Weekly,daysOfWeek="Weekdays"}

User: "Remind me every weekend to check the pool."
{tool:schedule,operation=add,type=Reminder,content="Check the pool",pattern=Weekly,daysOfWeek="Weekends"}

User: "Remind me on the first Thursday of every month to check the meter."
{tool:schedule,operation=add,type=Reminder,content="Check the meter",pattern=Monthly,weekOfMonth=1,weekdayOfMonth=Thursday}

User: "Remind me every December 25th that it's Christmas."
{tool:schedule,operation=add,type=Reminder,content="It's Christmas",pattern=Yearly,month=12,dayOfMonth=25}

User: "Disable alarm 8c1a7d4e-3b2f-4e91-9d4a-123456789abc."
{tool:schedule,operation=disable,id=8c1a7d4e-3b2f-4e91-9d4a-123456789abc}

User: "Delete that reminder."
First retrieve the event ID if it is not already known, then use:
{tool:schedule,operation=delete,id=<known-event-id>}
""";
        }

        public Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var operation = request.GetString("operation");

            var missing = FindMissingParameter(request, operation);
            if (missing is not null)
                return Task.FromResult(missing);

            try
            {
                var result =
                    operation!.Trim().ToLowerInvariant() switch
                    {
                        "add" => AddEvent(request),
                        "update" => UpdateEvent(request),
                        "delete" => DeleteEvent(request),
                        "enable" => EnableEvent(request),
                        "disable" => DisableEvent(request),
                        _ => ToolResult.Failed(Name, $"Unknown schedule operation '{operation}'.")
                    };

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Failed(Name, ex.Message));
            }
        }

        private ToolResult? FindMissingParameter(ToolRequest request, string? operation)
        {
            var normalizedOperation = operation?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(normalizedOperation))
                return Missing(request, "What would you like me to do with the scheduled event?");

            if (normalizedOperation is not ("add" or "update" or "delete" or "enable" or "disable"))
                return null;

            if (normalizedOperation is "update" or "delete" or "enable" or "disable")
            {
                if (string.IsNullOrWhiteSpace(request.GetString("id")))
                    return Missing(request, "Which scheduled event should I use?");
            }

            if (normalizedOperation is "add" or "update")
            {
                if (string.IsNullOrWhiteSpace(request.GetString("type")))
                    return Missing(request, "Is this an alarm or a reminder?");

                if (string.IsNullOrWhiteSpace(request.GetString("content")))
                    return Missing(request, "What would you like me to remember or say?");

                if (string.IsNullOrWhiteSpace(request.GetString("pattern")))
                    return Missing(request, "How often should I schedule it?");
                
                var pattern = request.GetString("pattern")!.Trim();
                if (pattern.Equals("Once", StringComparison.OrdinalIgnoreCase) &&
                    !request.HasArgument("date"))
                    return Missing(request, "What date should I schedule it for?");

                if (pattern.Equals("Weekly", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(request.GetString("daysOfWeek")))
                    return Missing(request, "Which days of the week should I use?");

                if (pattern.Equals("Monthly", StringComparison.OrdinalIgnoreCase) &&
                    !request.HasArgument("dayOfMonth") &&
                    !request.HasArgument("weekOfMonth"))
                    return Missing(request, "Which day of the month should I use?");

                if (pattern.Equals("Yearly", StringComparison.OrdinalIgnoreCase) &&
                    !request.HasArgument("month"))
                    return Missing(request, "Which month should I use?");

                var type = request.GetString("type");
                if (type?.Equals("Alarm", StringComparison.OrdinalIgnoreCase) == true &&
                    !request.HasArgument("timeOfDay"))
                    return Missing(request, "What time should the alarm go off?");
            }

            return null;
        }

        private ToolResult Missing(ToolRequest request, string prompt)
        {
            var pending = new ToolRequest
            {
                Name = Name,
                Arguments = (JObject)request.Arguments.DeepClone()
            };

            var parameter = request.Arguments.Properties()
                .FirstOrDefault(p => string.Equals(p.Value.Value<string>(), ToolRequest.RequiredValue, StringComparison.Ordinal));

            if (parameter is null)
            {
                var next = FindParameterName(request);
                pending.Arguments[next] = ToolRequest.RequiredValue;
            }

            return ToolResult.MissingParameter(Name, prompt, pending);
        }

        private static string FindParameterName(ToolRequest request)
        {
            var operation = request.GetString("operation")?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(operation))
                return "operation";

            if (operation is "update" or "delete" or "enable" or "disable")
                return "id";

            if (string.IsNullOrWhiteSpace(request.GetString("type")))
                return "type";

            if (string.IsNullOrWhiteSpace(request.GetString("content")))
                return "content";

            if (string.IsNullOrWhiteSpace(request.GetString("pattern")))
                return "pattern";

            var pattern = request.GetString("pattern")!.Trim();
            if (pattern.Equals("Once", StringComparison.OrdinalIgnoreCase) && !request.HasArgument("date"))
                return "date";
            if (pattern.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && !request.HasArgument("daysOfWeek"))
                return "daysOfWeek";
            if (pattern.Equals("Monthly", StringComparison.OrdinalIgnoreCase) && !request.HasArgument("dayOfMonth") && !request.HasArgument("weekOfMonth"))
                return "dayOfMonth";
            if (pattern.Equals("Yearly", StringComparison.OrdinalIgnoreCase) && !request.HasArgument("month"))
                return "month";

            if (request.GetString("type")?.Equals("Alarm", StringComparison.OrdinalIgnoreCase) == true &&
                !request.HasArgument("timeOfDay"))
                return "timeOfDay";

            return "parameter";
        }

        private ToolResult AddEvent(ToolRequest request)
        {
            var scheduledEvent = BuildEvent(request);
            var added = _alarmService.AddEvent(scheduledEvent);
            return ToolResult.Successful(Name, "Scheduled event added.", added);
        }

        private ToolResult UpdateEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            var scheduledEvent = BuildEvent(request, id.Value);
            if (!_alarmService.UpdateEvent(scheduledEvent))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event updated.", scheduledEvent);
        }

        private ToolResult DeleteEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmService.DeleteEvent(id.Value))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event deleted.", new { Id = id.Value });
        }

        private ToolResult EnableEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmService.EnableEvent(id.Value))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event enabled.", new { Id = id.Value, Enabled = true });
        }

        private ToolResult DisableEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmService.DisableEvent(id.Value))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event disabled.", new { Id = id.Value, Enabled = false });
        }

        private static ScheduledEvent BuildEvent(ToolRequest request, Guid? id = null)
        {
            var type = ParseEventType(request.GetString("type"));
            var content = request.GetString("content");
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Event content is required.");

            var schedule = BuildSchedule(request);
            var startOffset = request.GetInt32("startOffset") ?? 0;
            var endOffset = request.GetInt32("endOffset") ?? startOffset;

            if (endOffset < startOffset)
                throw new ArgumentException("endOffset must be greater than or equal to startOffset.");

            ValidateEvent(type, schedule, startOffset, endOffset);

            return new ScheduledEvent
            {
                Id = id ?? Guid.NewGuid(),
                Type = type,
                Content = content,
                Schedule = schedule,
                StartOffset = startOffset,
                EndOffset = endOffset,
                Enabled = request.GetBoolean("enabled") ?? true,
                CreatedAt = DateTime.Now
            };
        }

        private static SchedulePattern BuildSchedule(ToolRequest request)
        {
            var typeText = request.GetString("pattern");
            if (string.IsNullOrWhiteSpace(typeText))
                throw new ArgumentException("A schedule pattern is required.");

            if (!Enum.TryParse<SchedulePatternType>(typeText, true, out var type))
                throw new ArgumentException($"Unknown schedule pattern '{typeText}'.");

            return new SchedulePattern
            {
                Type = type,
                Date = request.GetArgument<DateOnly?>("date"),
                TimeOfDay = request.GetArgument<TimeSpan?>("timeOfDay"),
                DaysOfWeek = ParseDaysOfWeek(request.GetString("daysOfWeek")),
                Month = request.GetInt32("month"),
                DayOfMonth = request.GetInt32("dayOfMonth"),
                WeekOfMonth = request.GetInt32("weekOfMonth"),
                WeekdayOfMonth = request.GetArgument<DayOfWeek?>("weekdayOfMonth")
            };
        }

        private static DayOfWeek[] ParseDaysOfWeek(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var normalized = value.Trim();

            if (normalized.Equals("weekdays", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("every weekday", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    DayOfWeek.Monday,
                    DayOfWeek.Tuesday,
                    DayOfWeek.Wednesday,
                    DayOfWeek.Thursday,
                    DayOfWeek.Friday
                ];
            }

            if (normalized.Equals("weekends", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("every weekend", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    DayOfWeek.Saturday,
                    DayOfWeek.Sunday
                ];
            }

            var tokens = normalized
                .Replace(",", " ", StringComparison.Ordinal)
                .Replace(" and ", " ", StringComparison.OrdinalIgnoreCase)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var days = new List<DayOfWeek>();

            foreach (var token in tokens)
            {
                if (!Enum.TryParse<DayOfWeek>(token, true, out var day))
                    throw new ArgumentException($"Unknown weekday '{token}'. Use Monday through Sunday, Weekdays, or Weekends.");

                if (!days.Contains(day))
                    days.Add(day);
            }

            return [.. days];
        }

        private static ScheduledEventType ParseEventType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Event type is required.");

            if (!Enum.TryParse<ScheduledEventType>(value, true, out var type))
                throw new ArgumentException($"Unknown event type '{value}'.");

            return type;
        }

        private static Guid? GetRequiredId(ToolRequest request)
        {
            var value = request.GetString("id");
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!Guid.TryParse(value, out var id))
                throw new ArgumentException($"Invalid event ID '{value}'.");

            return id;
        }

        private static void ValidateEvent(
            ScheduledEventType type,
            SchedulePattern schedule,
            int startOffset,
            int endOffset)
        {
            if (type == ScheduledEventType.Alarm)
            {
                if (!schedule.TimeOfDay.HasValue)
                    throw new ArgumentException("Alarms require a timeOfDay.");

                if (startOffset != 0 || endOffset != 0)
                    throw new ArgumentException("Alarms cannot use startOffset or endOffset.");
            }

            if (type == ScheduledEventType.Reminder)
            {
                if (startOffset > endOffset)
                    throw new ArgumentException("Reminder endOffset must be greater than or equal to startOffset.");

                schedule.TimeOfDay = null;
            }
        }
    }
}
