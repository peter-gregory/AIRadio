namespace AIRadio.Server.Models.Network
{
    public sealed class WifiNetwork
    {
        public string Ssid { get; init; } =
            string.Empty;

        public int SignalStrength { get; init; }

        public string Security { get; init; } =
            string.Empty;

        public bool IsSecured =>
            !string.IsNullOrWhiteSpace(Security) &&
            !string.Equals(Security, "--", StringComparison.Ordinal);
    }
}
