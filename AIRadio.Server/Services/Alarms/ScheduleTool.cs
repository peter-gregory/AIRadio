using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Alarms
{
    public sealed class ScheduleTool : ITool
    {
        private readonly IAlarmManagerService _alarmManager;

        public ScheduleTool(
            IAlarmManagerService alarmManager)
        {
            _alarmManager = alarmManager;
        }

        public string Name =>
            "schedule";

        public string GetPromptText() =>
            "schedule{operation=add|update|delete|enable|disable;type=Alarm|Reminder;id?;content?;pattern=Once|Daily|Weekly|Monthly|Yearly;date?;timeOfDay?;daysOfWeek?;month?;dayOfMonth?;weekOfMonth?;weekdayOfMonth?;startOffset?;endOffset?;enabled?=true}";

        public Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var operation =
                request.GetString("operation");

            if (string.IsNullOrWhiteSpace(operation))
            {
                return Task.FromResult(
                    ToolResult.Failed(
                        Name,
                        "No schedule operation was specified."));
            }

            try
            {
                var result =
                    operation.Trim().ToLowerInvariant() switch
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

        private ToolResult AddEvent(ToolRequest request)
        {
            var scheduledEvent = BuildEvent(request);
            var added = _alarmManager.AddEvent(scheduledEvent);
            return ToolResult.Successful(Name, "Scheduled event added.", added);
        }

        private ToolResult UpdateEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            var scheduledEvent = BuildEvent(request, id.Value);
            if (!_alarmManager.UpdateEvent(scheduledEvent))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event updated.", scheduledEvent);
        }

        private ToolResult DeleteEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmManager.DeleteEvent(id.Value))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event deleted.", new { Id = id.Value });
        }

        private ToolResult EnableEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmManager.EnableEvent(id.Value))
                return ToolResult.Failed(Name, $"Scheduled event '{id}' was not found.");

            return ToolResult.Successful(Name, "Scheduled event enabled.", new { Id = id.Value, Enabled = true });
        }

        private ToolResult DisableEvent(ToolRequest request)
        {
            var id = GetRequiredId(request);
            if (id is null)
                return ToolResult.Failed(Name, "An event ID is required.");

            if (!_alarmManager.DisableEvent(id.Value))
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
                DaysOfWeek = request.GetArgument<DayOfWeek[]>("daysOfWeek") ?? [],
                Month = request.GetInt32("month"),
                DayOfMonth = request.GetInt32("dayOfMonth"),
                WeekOfMonth = request.GetInt32("weekOfMonth"),
                WeekdayOfMonth = request.GetArgument<DayOfWeek?>("weekdayOfMonth")
            };
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
