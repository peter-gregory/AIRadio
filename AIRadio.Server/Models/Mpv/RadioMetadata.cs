namespace AIRadio.Server.Models.Mpv
{
    public sealed class RadioMetadata
    {
        public string? StationName { get; init; }

        public string? StationDescription { get; init; }

        public string? ProgramName { get; init; }

        public string? Title { get; init; }

        public string? Artist { get; init; }

        public string? Album { get; init; }

        public string? Genre { get; init; }

        public string? Composer { get; init; }

        public string? Year { get; init; }

        public string? Comment { get; init; }

        public string? StreamUrl { get; init; }

        public string? ArtworkUrl { get; init; }

        public IReadOnlyDictionary<string, string> AdditionalProperties
        { get; init; }
            = new Dictionary<string, string>();
    }
}
