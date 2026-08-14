using AIRadio.Server.Models.Network;
using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Network
{
    public sealed class WifiTool : ITool
    {
        private readonly IWifiManager _wifiManager;

        public WifiTool(
            IWifiManager wifiManager)
        {
            _wifiManager =
                wifiManager ??
                throw new ArgumentNullException(nameof(wifiManager));
        }

        public string Name =>
            "wifi";

        public async Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var action =
                request.GetString("action")?.Trim();

            try
            {
                return action?.ToLowerInvariant() switch
                {
                    "scan" =>
                        await ScanAsync(cancellationToken),

                    "status" =>
                        await GetStatusAsync(cancellationToken),

                    "connect" =>
                        await ConnectAsync(
                            request,
                            cancellationToken),

                    _ => ToolResult.Failed(
                        Name,
                        "The WiFi action must be scan, status, or connect.")
                };
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Failed(
                    Name,
                    ex.Message);
            }
        }

        private async Task<ToolResult> ScanAsync(
            CancellationToken cancellationToken)
        {
            var networks =
                await _wifiManager.ScanAsync(
                    cancellationToken);

            var choices =
                networks
                    .Select((network, index) =>
                        new WifiChoice
                        {
                            Number = index + 1,
                            Network = network
                        })
                    .ToArray();

            return ToolResult.Successful(
                Name,
                choices.Length == 0
                    ? "No WiFi networks were found."
                    : $"Found {choices.Length} WiFi networks. " +
                      "The networks are numbered in signal-strength order.",
                choices);
        }

        private async Task<ToolResult> GetStatusAsync(
            CancellationToken cancellationToken)
        {
            var connected =
                await _wifiManager.IsConnectedAsync(
                    cancellationToken);

            return ToolResult.Successful(
                Name,
                connected
                    ? "The radio is connected to WiFi."
                    : "The radio is not connected to WiFi.",
                new { Connected = connected });
        }

        private async Task<ToolResult> ConnectAsync(
            ToolRequest request,
            CancellationToken cancellationToken)
        {
            var ssid =
                request.GetString("ssid")?.Trim();

            var number =
                request.GetInt32("number");

            if (string.IsNullOrWhiteSpace(ssid) && number is null)
            {
                return ToolResult.Failed(
                    Name,
                    "Specify the WiFi network name or its number from the most recent scan.");
            }

            if (number is not null)
            {
                var networks =
                    await _wifiManager.ScanAsync(
                        cancellationToken);

                if (number.Value < 1 ||
                    number.Value > networks.Count)
                {
                    return ToolResult.Failed(
                        Name,
                        $"WiFi network number {number.Value} is not in the current scan results.");
                }

                ssid =
                    networks[number.Value - 1].Ssid;
            }

            var password =
                request.GetString("password");

            var connected =
                await _wifiManager.ConnectAsync(
                    ssid!,
                    password,
                    cancellationToken);

            if (!connected)
            {
                return ToolResult.Failed(
                    Name,
                    $"Unable to connect to WiFi network '{ssid}'.");
            }

            return ToolResult.Successful(
                Name,
                $"Connected to WiFi network '{ssid}'.");
        }

        private sealed class WifiChoice
        {
            public int Number { get; init; }

            public WifiNetwork Network { get; init; } = null!;
        }
    }
}
