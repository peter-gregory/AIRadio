using AIRadio.Server.Models.Weather;

namespace AIRadio.Server.Services.Weather;

public static class WeatherReportFormatter
{
    public static string Format(WeatherResult weather)
    {
        ArgumentNullException.ThrowIfNull(weather);

        if (weather.Current is null)
            throw new InvalidOperationException("Current weather data was not returned.");

        var current = weather.Current;
        var today = weather.Forecast.FirstOrDefault();

        var sentences = new List<string>
        {
            $"Here's the weather for {weather.Location}.",
            $"It's {current.Condition.ToLowerInvariant()} and {Temperature(current.Temperature)} right now"
        };

        if (Math.Abs(current.FeelsLike - current.Temperature) >= 3)
            sentences[^1] += $", feeling like {Temperature(current.FeelsLike)}";

        sentences[^1] += ".";

        if (today is not null)
        {
            sentences.Add(
                $"Today's high will be {Temperature(today.High)}, with a low of {Temperature(today.Low)} tonight.");

            if (today.RainChance > 0)
                sentences.Add(
                    today.RainChance == 100
                        ? "Rain is expected today."
                        : $"There's a {today.RainChance} percent chance of rain today.");
        }

        if (current.WindSpeed >= 15 || current.WindGusts >= 25)
        {
            var wind = $"Winds are from the {ToCompassDirection(current.WindDirection)} at {Speed(current.WindSpeed)}";
            if (current.WindGusts > current.WindSpeed + 5)
                wind += $", with gusts up to {Speed(current.WindGusts)}";
            sentences.Add(wind + ".");
        }
        else if (current.WindSpeed >= 5)
        {
            sentences.Add(
                $"There's a {Speed(current.WindSpeed)} breeze from the {ToCompassDirection(current.WindDirection)}.");
        }

        if (current.Humidity >= 85 || current.Humidity <= 25)
            sentences.Add($"Humidity is {current.Humidity} percent.");

        if (current.Precipitation > 0)
            sentences.Add($"There's currently {Precipitation(current.Precipitation)} of precipitation.");

        var conditionTag = GetConditionSoundTag(current.Condition);
        if (!string.IsNullOrWhiteSpace(conditionTag))
            sentences[1] += $" {conditionTag}";

        if (current.WindSpeed >= 15 || current.WindGusts >= 25)
        {
            var windSentenceIndex = sentences.FindIndex(
                sentence => sentence.StartsWith("Winds are from the ", StringComparison.Ordinal));

            if (windSentenceIndex >= 0)
                sentences[windSentenceIndex] += " {sound:weather-wind}";
        }

        return string.Join(" ", sentences);
    }

    private static string Temperature(double value) =>
        $"{Math.Round(value, MidpointRounding.AwayFromZero):0} degrees";

    private static string Speed(double value) =>
        $"{Math.Round(value, MidpointRounding.AwayFromZero):0} miles per hour";

    private static string Precipitation(double value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return $"{rounded:0.##} millimeters";
    }

    private static string? GetConditionSoundTag(string condition)
    {
        var normalized = condition.Trim().ToLowerInvariant();

        if (normalized.Contains("thunderstorm"))
            return "{sound:weather-thunderstorm}";
        if (normalized.Contains("freezing rain"))
            return "{sound:weather-freezing-rain}";
        if (normalized.Contains("freezing drizzle"))
            return "{sound:weather-freezing-drizzle}";
        if (normalized.Contains("snow shower"))
            return "{sound:weather-snow-shower}";
        if (normalized == "snow")
            return "{sound:weather-snow}";
        if (normalized.Contains("rain shower"))
            return "{sound:weather-rain-shower}";
        if (normalized == "rain")
            return "{sound:weather-rain}";
        if (normalized == "drizzle")
            return "{sound:weather-drizzle}";
        if (normalized == "foggy")
            return "{sound:weather-fog}";

        return null;
    }

    private static string ToCompassDirection(double degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        var index = (int)Math.Round(
            normalized / 22.5,
            MidpointRounding.AwayFromZero) % 16;

        return new[]
        {
            "north", "north-northeast", "northeast", "east-northeast",
            "east", "east-southeast", "southeast", "south-southeast",
            "south", "south-southwest", "southwest", "west-southwest",
            "west", "west-northwest", "northwest", "north-northwest"
        }[index];
    }
}
