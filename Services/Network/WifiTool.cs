using AIRadio.Server.Models.Network;
using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Network
{
    public sealed class WifiTool : ITool
    {
        private readonly ILogger<WifiTool> _logger;
        private readonly IWifiManager _wifiManager;

        public WifiTool(IWifiManager wifiManager, ILogger<WifiTool> logger)
        {
            _wifiManager = wifiManager ?? throw new ArgumentNullException(nameof(wifiManager));
            _logger = logger;
            _logger.LogInformation("Fininshed construct WifiTool");
        }

        public string Name => "wifi";

        public string GetLlmInstructions() => """
WIFI TOOL
Use the WiFi tool to scan for nearby networks, check WiFi status, or connect to a network.

Actions:
- scan: Scan for nearby WiFi networks. No parameters. The result contains numbered networks that can be selected by number.
- status: Check whether the radio is connected to WiFi. No parameters.
- connect: Connect to a WiFi network.
  number: Optional integer from the most recent WiFi scan. Use this when the user selects a network by its displayed number.
  ssid: Optional WiFi network name. Use this when the user gives the network name instead of selecting a scan result.
  password: Optional WiFi password. Omit it for an open network.
  For connect, provide either number or ssid.

Important:
- After scanning, remember the numbered choices from the tool result and use the selected number for connect.
- If the user says "the second network" or similar after a scan, translate that to the corresponding number from the scan result.
- If the user asks to connect to a named network, use ssid.
- Do not invent network numbers or SSIDs.
- Never expose or repeat a WiFi password in spoken text.
- If a password contains punctuation, quote it in the tool tag.
- If no suitable network is available, ask the user which network they want or scan again.

Examples:
User: "Scan for WiFi networks"
{tool:wifi,action=scan}

User: "Am I connected to WiFi?"
{tool:wifi,action=status}

User: "Connect to network 2"
{tool:wifi,action=connect,number=2}

User: "Connect to my home WiFi"
{tool:wifi,action=connect,ssid="My Home WiFi",password="secret password"}

User: "Connect to Coffee Shop"
{tool:wifi,action=connect,ssid="Coffee Shop"}
""";

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
