using AIRadio.Server.Models.Weather;

namespace AIRadio.Server.Services.Weather;

public static class WeatherReportFormatter
{
    private const int LowRainChanceThreshold = 20;

    public static string Format(WeatherResult weather)
    {
        ArgumentNullException.ThrowIfNull(weather);

        if (weather.Current is null)
            throw new InvalidOperationException("Current weather data was not returned.");

        var current = weather.Current;
        var today = weather.Forecast.FirstOrDefault();

        var sentences = new List<string>
        {
            $"Here's the weather for {weather.Location}."
        };

        // Current weather_code is the authoritative overall description of
        // what is happening right now. Keep it separate from the daily
        // forecast, whose weather code describes the most severe condition
        // expected during the day.
        var currentCondition = GetConditionSpeech(current.Condition);
        var currentSentence =
            $"Right now, it's {currentCondition} and {Temperature(current.Temperature)} degrees";

        if (Math.Abs(current.FeelsLike - current.Temperature) >= 3)
            currentSentence += $", feeling like {Temperature(current.FeelsLike)}";

        if (today is not null)
            currentSentence += $", with a low of {Temperature(today.Low)} tonight.";

        else
            currentSentence += ".";

        var conditionTag = GetConditionSoundTag(current.Condition);
        if (!string.IsNullOrWhiteSpace(conditionTag))
            currentSentence += $" {conditionTag}";

        sentences.Add(currentSentence);

        if (today is not null)
        {
            var forecastCondition = GetForecastConditionSpeech(today.Condition);
            var forecastSentence =
                $"Today's forecast is {forecastCondition}, with a high of {Temperature(today.High)}.";

            var forecastConditionTag = GetConditionSoundTag(today.Condition);
            if (!string.IsNullOrWhiteSpace(forecastConditionTag))
                forecastSentence += $" {forecastConditionTag}";

            sentences.Add(forecastSentence);

            sentences.Add(
                today.RainChance <= LowRainChanceThreshold
                    ? "There is no chance of rain today in the forecast."
                    : today.RainChance == 100
                        ? "Rain is expected today."
                        : $"There's a {today.RainChance} percent chance of rain today.");
        }

        if (current.WindSpeed >= 15 || current.WindGusts >= 25)
        {
            var wind = $"Winds are from the {ToCompassDirection(current.WindDirection)} at {Speed(current.WindSpeed)}";
            if (current.WindGusts > current.WindSpeed + 5)
                wind += $", with gusts up to {Speed(current.WindGusts)}";
            sentences.Add(wind + " {sound:weather-wind}.");
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

        sentences.Add($"And that's the local weather for {weather.Location}.");

        return string.Join(" ", sentences);
    }

    private static string GetConditionSpeech(string condition)
    {
        return condition.Trim().ToLowerInvariant() switch
        {
            "clear" => "clear",
            "mostly clear" => "mostly clear",
            "partly cloudy" => "partly cloudy",
            "overcast" => "overcast",
            "foggy" => "foggy",
            "drizzle" => "drizzling",
            "light drizzle" => "light drizzle",
            "moderate drizzle" => "moderate drizzle",
            "dense drizzle" => "dense drizzle",
            "freezing drizzle" => "freezing drizzle",
            "rain" => "raining",
            "freezing rain" => "freezing rain",
            "snow" => "snowing",
            "rain showers" => "rain showers",
            "snow showers" => "snow showers",
            "thunderstorms" => "thunderstorms",
            "thunderstorms with hail" => "thunderstorms with hail",
            _ => string.IsNullOrWhiteSpace(condition) ? "experiencing mixed conditions" : condition.ToLowerInvariant()
        };
    }

    private static string GetForecastConditionSpeech(string condition)
    {
        return condition.Trim().ToLowerInvariant() switch
        {
            "light drizzle" => "light drizzle",
            "moderate drizzle" => "moderate drizzle",
            "dense drizzle" => "dense drizzle",
            _ => GetConditionSpeech(condition)
        };
    }

    private static string Temperature(double value) =>
        $"{Math.Round(value, MidpointRounding.AwayFromZero):0}";

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

        if (normalized.Contains("drizzle"))
            return "{sound:weather-drizzle}";

        if (normalized == "foggy")
            return "{sound:weather-fog}";

        if (normalized == "overcast")
            return "{sound:weather-overcast}";

        if (normalized == "partly cloudy")
            return "{sound:weather-partly-cloudy}";

        if (normalized == "mostly clear")
            return "{sound:weather-mostly-clear}";

        if (normalized == "clear")
            return "{sound:weather-nice}";

        return "{sound:weather-nice}";
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
