namespace AIRadio.Server.Services.Time;

public static class TimeSpeechFormatter
{
    private static readonly string[] Hours =
    [
        "twelve", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine", "ten", "eleven"
    ];

    private static readonly string[] Teens =
    [
        "ten", "eleven", "twelve", "thirteen", "fourteen",
        "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
    ];

    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine"
    ];

    private static readonly string[] Tens =
    [
        "", "", "twenty", "thirty", "forty", "fifty"
    ];

    public static string Format(DateTime value) =>
        Format(value.TimeOfDay);

    public static string Format(DateTimeOffset value) =>
        Format(value.TimeOfDay);

    public static string Format(TimeSpan value)
    {
        var totalMinutes = (int)value.TotalMinutes;
        var hour = (totalMinutes / 60) % 24;
        var minute = totalMinutes % 60;

        var hourText = Hours[hour % 12];
        var period = hour < 12 ? "AM" : "PM";

        if (minute == 0)
            return $"{hourText} {period}";

        var minuteText = minute switch
        {
            < 10 => $"oh {Ones[minute]}",
            >= 10 and < 20 => Teens[minute - 10],
            _ => Tens[minute / 10] + (minute % 10 == 0
                ? string.Empty
                : $"-{Ones[minute % 10]}")
        };

        return $"{hourText} {minuteText} {period}";
    }
}
