namespace AIRadio.Server.Models.Weather
{
    public sealed class WeatherResult
    {
        public string Location { get; init; } =
            string.Empty;

        public WeatherCurrent? Current { get; init; }

        public IReadOnlyList<WeatherDay> Forecast { get; init; } =
            Array.Empty<WeatherDay>();

        public DateTimeOffset RetrievedAt { get; init; }
    }

    public sealed class WeatherCurrent
    {
        public double Temperature { get; init; }

        public double FeelsLike { get; init; }

        public string Condition { get; init; } =
            string.Empty;

        public int Humidity { get; init; }

        public double WindSpeed { get; init; }

        public double WindDirection { get; init; }

        public double WindGusts { get; init; }

        public double Precipitation { get; init; }

        public bool IsDay { get; init; }
    }

    public sealed class WeatherDay
    {
        public DateOnly Date { get; init; }

        public double High { get; init; }

        public double Low { get; init; }

        public string Condition { get; init; } =
            string.Empty;

        public int RainChance { get; init; }

        public double Precipitation { get; init; }

        public double WindSpeed { get; init; }

        public DateTimeOffset? Sunrise { get; init; }

        public DateTimeOffset? Sunset { get; init; }
    }
}
