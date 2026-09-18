using System.Globalization;

namespace AIRadio.Server.Services.Time;

public interface ITimeService
{
    DateTimeOffset GetNow();
    string FormatDate(DateTimeOffset value);
}

public sealed class TimeService : ITimeService
{
    public DateTimeOffset GetNow() => DateTimeOffset.Now;

    public string FormatDate(DateTimeOffset value)
    {
        var day = value.Day;
        var suffix = day % 100 is >= 11 and <= 13 ? "th" : (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };

        return $"{value:dddd, MMMM} {day}{suffix}";
    }
}
