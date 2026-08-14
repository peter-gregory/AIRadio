namespace AIRadio.Server.Models.Radio
{
    public sealed class RadioSearchResponse
    {
        public bool Success { get; init; }

        public bool LocalResults { get; init; }

        public bool WebSearchPerformed { get; init; }

        public string Message { get; init; } = string.Empty;

        public IReadOnlyList<RadioStation> LocalStations { get; init; }
            = Array.Empty<RadioStation>();

        public IReadOnlyList<RadioDiscoveryResult> WebStations { get; init; }
            = Array.Empty<RadioDiscoveryResult>();

        public static RadioSearchResponse Local(
            IReadOnlyList<RadioStation> stations)
        {
            return new RadioSearchResponse
            {
                Success = true,
                LocalResults = true,
                WebSearchPerformed = false,
                Message = $"Found {stations.Count} local station(s).",
                LocalStations = stations
            };
        }

        public static RadioSearchResponse Web(
            IReadOnlyList<RadioDiscoveryResult> stations)
        {
            return new RadioSearchResponse
            {
                Success = true,
                LocalResults = false,
                WebSearchPerformed = true,
                Message = $"Found {stations.Count} station(s).",
                WebStations = stations
            };
        }

        public static RadioSearchResponse Fail(
            string message)
        {
            return new RadioSearchResponse
            {
                Success = false,
                Message = message
            };
        }
    }
}
