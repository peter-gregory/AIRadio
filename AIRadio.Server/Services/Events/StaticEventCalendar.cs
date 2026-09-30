using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRadio.Server.Services.Events;

public interface IStaticEventCalendar
{
    IReadOnlyList<StaticCalendarEvent> GetEvents(DateOnly date);
}

public sealed class StaticEventCalendar : IStaticEventCalendar
{
    private const string FileName = "StaticEvents.json";

    private readonly ILogger<StaticEventCalendar> _logger;
    private readonly string _configFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly List<StaticCalendarEventDefinition> _definitions;

    public StaticEventCalendar(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<StaticEventCalendar> logger)
    {
        _logger = logger;

        var configDirectory = configuration["Application:ConfigDirectory"];
        if (string.IsNullOrWhiteSpace(configDirectory))
            throw new InvalidOperationException("Application:ConfigDirectory is not configured.");

        _configFile = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "Config",
            FileName));

        if (!File.Exists(_configFile))
            throw new FileNotFoundException($"Static event configuration was not found: {_configFile}");
        _definitions = Load();

        _logger.LogInformation(
            "Static event calendar initialized with {Count} definitions.",
            _definitions.Count);
    }

    public IReadOnlyList<StaticCalendarEvent> GetEvents(DateOnly date)
    {
        var result = new List<StaticCalendarEvent>();

        foreach (var definition in _definitions)
        {
            if (!definition.Enabled)
                continue;

            var range = GetRange(definition.Rule, date.Year);

            if (date < range.Start || date > range.End)
                continue;

            result.Add(new StaticCalendarEvent
            {
                Id = CreateStableId(definition),
                Category = definition.Category,
                Content = definition.Content,
                Speech = definition.Speech,
                Sound = definition.Sound
            });
        }

        return result;
    }

    private List<StaticCalendarEventDefinition> Load()
    {
        try
        {
            var json = File.ReadAllText(_configFile);
            var config = JsonSerializer.Deserialize<StaticCalendarConfiguration>(json, _jsonOptions);

            if (config?.Events is null)
                throw new InvalidOperationException("Static event configuration does not contain an events array.");

            return config.Events;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to load static event configuration from {Path}.", _configFile);
            throw;
        }
    }

    private static DateRange GetRange(StaticEventRule rule, int year)
    {
        return rule.Type.ToLowerInvariant() switch
        {
            "fixeddate" => SingleDay(new DateOnly(year, rule.Month!.Value, rule.Day!.Value)),
            "nthweekday" => NthWeekday(year, rule.Month!.Value, ParseWeekday(rule.Weekday!), rule.Occurrence!.Value),
            "lastweekday" => LastWeekday(year, rule.Month!.Value, ParseWeekday(rule.Weekday!)),
            "month" => MonthRange(year, rule.Month!.Value),
            "daterange" => ExplicitRange(year, rule.Start!, rule.End!),
            "nthweekdayrange" => NthWeekdayRange(
                year,
                rule.Month!.Value,
                ParseWeekday(rule.Weekday!),
                rule.Occurrence!.Value,
                rule.LengthDays ?? 7),
            "easter" => SingleDay(EasterSunday(year)),
            "easteroffset" => OffsetRange(EasterSunday(year), rule.Days ?? 0),
            "dststart" => SingleDay(DaylightSavingStart(year)),
            "dststartoffset" => OffsetRange(DaylightSavingStart(year), rule.Days ?? 0),
            "dstend" => SingleDay(DaylightSavingEnd(year)),
            "dstendoffset" => OffsetRange(DaylightSavingEnd(year), rule.Days ?? 0),
            _ => throw new InvalidOperationException($"Unknown static event rule type '{rule.Type}'.")
        };
    }

    private static DateRange ExplicitRange(int year, StaticDate start, StaticDate end)
    {
        var startDate = new DateOnly(year, start.Month, start.Day);
        var endDate = new DateOnly(year, end.Month, end.Day);
        return new(startDate, endDate);
    }

    private static DateRange MonthRange(int year, int month) =>
        new(
            new DateOnly(year, month, 1),
            new DateOnly(year, month, DateTime.DaysInMonth(year, month)));

    private static DateRange NthWeekdayRange(
        int year,
        int month,
        DayOfWeek weekday,
        int occurrence,
        int lengthDays)
    {
        var start = NthWeekday(year, month, weekday, occurrence).Start;
        return new(start, start.AddDays(lengthDays - 1));
    }

    private static DateRange SingleDay(DateOnly date) => new(date, date);

    private static DateRange OffsetRange(DateOnly date, int offsetDays)
    {
        var adjusted = date.AddDays(offsetDays);
        return SingleDay(adjusted);
    }

    private static DateRange NthWeekday(
        int year,
        int month,
        DayOfWeek weekday,
        int occurrence)
    {
        if (occurrence < 1 || occurrence > 5)
            throw new InvalidOperationException("Weekday occurrence must be between 1 and 5.");

        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        var date = first.AddDays(offset + ((occurrence - 1) * 7));

        if (date.Month != month)
            throw new InvalidOperationException(
                $"The {occurrence}th {weekday} does not occur in {first:MMMM} {year}.");

        return SingleDay(date);
    }

    private static DateRange LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
        return SingleDay(last.AddDays(-offset));
    }

    private static DateOnly EasterSunday(int year)
    {
        // Gregorian computus.
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }

    private static DateOnly DaylightSavingStart(int year)
    {
        var date = new DateOnly(year, 3, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset + 7);
    }

    private static DateOnly DaylightSavingEnd(int year)
    {
        var date = new DateOnly(year, 11, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset);
    }

    private static DayOfWeek ParseWeekday(string value) =>
        Enum.TryParse<DayOfWeek>(value, true, out var weekday)
            ? weekday
            : throw new InvalidOperationException($"Invalid weekday '{value}'.");

    private static Guid CreateStableId(StaticCalendarEventDefinition definition)
    {
        var key = $"{definition.Category}|{definition.Content}|{definition.Sound}|{definition.Rule.Type}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

public sealed class StaticCalendarConfiguration
{
    public List<StaticCalendarEventDefinition> Events { get; set; } = [];
}

public sealed class StaticCalendarEventDefinition
{
    public bool Enabled { get; set; } = true;
    public string Category { get; set; } = "observance";
    public string Content { get; set; } = string.Empty;
    public string Speech { get; set; } = string.Empty;
    public string? Sound { get; set; }
    public StaticEventRule Rule { get; set; } = new();
}

public sealed class StaticEventRule
{
    public string Type { get; set; } = string.Empty;
    public int? Month { get; set; }
    public int? Day { get; set; }
    public string? Weekday { get; set; }
    public int? Occurrence { get; set; }
    public int? LengthDays { get; set; }
    public int? Days { get; set; }
    public StaticDate? Start { get; set; }
    public StaticDate? End { get; set; }
}

public sealed class StaticDate
{
    public int Month { get; set; }
    public int Day { get; set; }
}

public sealed record DateRange(DateOnly Start, DateOnly End);

public sealed class StaticCalendarEvent
{
    public Guid Id { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Speech { get; init; } = string.Empty;
    public string? Sound { get; init; }
}
