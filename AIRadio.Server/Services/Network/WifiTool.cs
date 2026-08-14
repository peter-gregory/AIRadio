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

            return ToolResult.Successful(
                Name,
                networks.Count == 0
                    ? "No WiFi networks were found."
                    : $"Found {networks.Count} WiFi networks.",
                networks);
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

            if (string.IsNullOrWhiteSpace(ssid))
            {
                return ToolResult.Failed(
                    Name,
                    "The WiFi network name was not specified.");
            }

            var password =
                request.GetString("password");

            var connected =
                await _wifiManager.ConnectAsync(
                    ssid,
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
    }
}
