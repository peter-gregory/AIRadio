namespace AIRadio.Server.Models.Radio
{
    public sealed class RadioSearchCriteria
    {
        public string? Query { get; init; }

        public string? StationName { get; init; }

        public string? Country { get; init; }

        public string? State { get; init; }

        public string? City { get; init; }

        public string? Language { get; init; }

        public string? Genre { get; init; }

        public string? Tag { get; init; }

        public int Limit { get; init; } = 20;

        public int Offset { get; init; }

        public bool HasCriteria =>
            !string.IsNullOrWhiteSpace(Query) ||
            !string.IsNullOrWhiteSpace(StationName) ||
            !string.IsNullOrWhiteSpace(Country) ||
            !string.IsNullOrWhiteSpace(State) ||
            !string.IsNullOrWhiteSpace(City) ||
            !string.IsNullOrWhiteSpace(Language) ||
            !string.IsNullOrWhiteSpace(Genre) ||
            !string.IsNullOrWhiteSpace(Tag);

        public bool HasQuery =>
            !string.IsNullOrWhiteSpace(Query);

        public bool HasLocation =>
            !string.IsNullOrWhiteSpace(Country) ||
            !string.IsNullOrWhiteSpace(State) ||
            !string.IsNullOrWhiteSpace(City);

        public bool HasFilters =>
            !string.IsNullOrWhiteSpace(StationName) ||
            !string.IsNullOrWhiteSpace(Country) ||
            !string.IsNullOrWhiteSpace(State) ||
            !string.IsNullOrWhiteSpace(City) ||
            !string.IsNullOrWhiteSpace(Language) ||
            !string.IsNullOrWhiteSpace(Genre) ||
            !string.IsNullOrWhiteSpace(Tag);

        public int GetLimit()
        {
            return Math.Clamp(
                Limit,
                1,
                100);
        }

        public int GetOffset()
        {
            return Math.Max(
                Offset,
                0);
        }
    }
}
