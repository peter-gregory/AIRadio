using AIRadio.Server.Models.Network;
using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Network
{
    public sealed class WifiTool : ITool
    {
        private readonly IWifiManager _wifiManager;

        public WifiTool(IWifiManager wifiManager)
        {
            _wifiManager = wifiManager ?? throw new ArgumentNullException(nameof(wifiManager));
        }

        public string Name => "wifi";
        public string GetPromptText() => "wifi{action=scan|status|connect;connect:number|ssid;password?}";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var action = request.GetString("action")?.Trim();
            try
            {
                return action?.ToLowerInvariant() switch
                {
                    "scan" => await ScanAsync(cancellationToken),
                    "status" => await GetStatusAsync(cancellationToken),
                    "connect" => await ConnectAsync(request, cancellationToken),
                    _ => ToolResult.Failed(Name, "The WiFi action must be scan, status, or connect.")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return ToolResult.Failed(Name, ex.Message); }
        }

        private async Task<ToolResult> ScanAsync(CancellationToken cancellationToken)
        {
            var networks = await _wifiManager.ScanAsync(cancellationToken);
            var choices = networks.Select((network, index) => new
            {
                Number = index + 1,
                network.Ssid,
                network.SignalStrength,
                network.Security,
                network.IsSecured
            }).ToArray();
            return ToolResult.Successful(
                Name,
                choices.Length == 0 ? "No WiFi networks were found." : $"Found {choices.Length} WiFi networks. Choose one by number, or say none of these to scan again.",
                choices);
        }

        private async Task<ToolResult> GetStatusAsync(CancellationToken cancellationToken)
        {
            var connected = await _wifiManager.IsConnectedAsync(cancellationToken);
            return ToolResult.Successful(Name, connected ? "The radio is connected to WiFi." : "The radio is not connected to WiFi.", new { Connected = connected });
        }

        private async Task<ToolResult> ConnectAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var number = request.GetInt32("number");
            if (number.HasValue)
            {
                if (!_wifiManager.TryGetScannedNetwork(number.Value, out var network) || network is null)
                    return ToolResult.Failed(Name, $"WiFi network number {number.Value} is not in the current scan. Ask the user to choose one of the listed networks or say none of these to scan again.");
                return await ConnectToNetworkAsync(network, request.GetString("password"), cancellationToken);
            }

            var ssid = request.GetString("ssid")?.Trim();
            if (string.IsNullOrWhiteSpace(ssid))
                return ToolResult.Failed(Name, "The WiFi network number or network name was not specified.");
            return await ConnectAsync(ssid, request.GetString("password"), cancellationToken);
        }

        private Task<ToolResult> ConnectToNetworkAsync(WifiNetwork network, string? password, CancellationToken cancellationToken) =>
            ConnectAsync(network.Ssid, password, cancellationToken);

        private async Task<ToolResult> ConnectAsync(string ssid, string? password, CancellationToken cancellationToken)
        {
            var connected = await _wifiManager.ConnectAsync(ssid, password, cancellationToken);
            return connected
                ? ToolResult.Successful(Name, $"Connected to WiFi network '{ssid}'.")
                : ToolResult.Failed(Name, $"Unable to connect to WiFi network '{ssid}'.");
        }
    }
}
