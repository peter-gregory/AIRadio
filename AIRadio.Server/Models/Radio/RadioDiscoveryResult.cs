namespace AIRadio.Server.Models.Radio
{
    public sealed class RadioDiscoveryResult
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string StreamUrl { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Homepage { get; set; }

        public string? Favicon { get; set; }

        public string[] Tags { get; set; } = [];

        public string? Country { get; set; }

        public string? CountryCode { get; set; }

        public string? State { get; set; }

        public string[] Languages { get; set; } = [];

        public string? Codec { get; set; }

        public int? Bitrate { get; set; }

        public string? Source { get; set; }

        public string? SourceId { get; set; }

        public bool IsHttps { get; set; }

        public bool IsOnline { get; set; }

        public int Votes { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }
    }
}
