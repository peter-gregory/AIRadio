namespace AIRadio.Server.Services.Time;

public static class TimeSpeechFormatter
{
    private static readonly string[] Hours =
    [
        "twelve", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine", "ten", "eleven"
    ];

    private static readonly string[] Tens =
    [
        "", "", "twenty", "thirty", "forty", "fifty"
    ];

    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine"
    ];

    public static string Format(DateTime value) =>
        Format(value.TimeOfDay);

    public static string Format(TimeSpan value)
    {
        var totalMinutes = (int)value.TotalMinutes;
        var hour = (totalMinutes / 60) % 24;
        var minute = totalMinutes % 60;

        var hour12 = hour % 12;
        var period = hour < 12 ? "AM" : "PM";
        var hourText = Hours[hour12];

        if (minute == 0)
            return $"{hourText} {period}";

        var minuteText = minute < 10
            ? $"oh {Ones[minute]}"
            : minute < 20
                ? Ones[minute - 10] == "zero"
                    ? "ten"
                    : $"one {Ones[minute - 10]}"
                : Tens[minute / 10] + (minute % 10 == 0 ? "" : $"-{Ones[minute % 10]}");

        if (minute is >= 10 and < 20)
            minuteText = minute switch
            {
                10 => "ten",
                11 => "eleven",
                12 => "twelve",
                13 => "thirteen",
                14 => "fourteen",
                15 => "fifteen",
                16 => "sixteen",
                17 => "seventeen",
                18 => "eighteen",
                19 => "nineteen",
                _ => minuteText
            };

        return $"{hourText} {minuteText} {period}";
    }
}
