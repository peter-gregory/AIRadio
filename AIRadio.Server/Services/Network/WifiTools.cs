using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Network;

public sealed class WifiStatusTool : ITool
{
    private readonly IWifiManager _manager;
    public WifiStatusTool(IWifiManager manager) => _manager = manager;
    public string Name => "wifiStatus";
    public string Intent => "Check WiFi connection status.";
    public string GetLlmInstructions() => """
WIFI STATUS
Check WiFi connection status.
Parameters: none.
Example: "Is WiFi connected?" -> {tool:wifiStatus}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connected = await _manager.IsConnectedAsync(cancellationToken);
        return ToolResult.Successful(Name, connected ? "The radio is connected to WiFi." : "The radio is not connected to WiFi.", new { Connected = connected });
    }
}

public sealed class WifiNetworksTool : ITool
{
    private readonly IWifiManager _manager;
    public WifiNetworksTool(IWifiManager manager) => _manager = manager;
    public string Name => "wifiNetworks";
    public string Intent => "Scan for nearby WiFi networks.";
    public string GetLlmInstructions() => """
WIFI NETWORKS
Scan for nearby WiFi networks.
Parameters: none.
The results are numbered so the user can choose a network.
Example: "What WiFi networks are nearby?" -> {tool:wifiNetworks}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var networks = await _manager.ScanAsync(cancellationToken);
        var choices = networks.Select((network, index) => new
        {
            Number = index + 1, network.Ssid, network.SignalStrength, network.Security, network.IsSecured
        }).ToArray();
        return ToolResult.Successful(Name,
            choices.Length == 0 ? "No WiFi networks were found." : $"Found {choices.Length} WiFi networks.",
            choices);
    }
}

public sealed class WifiConnectTool : ITool
{
    private readonly IWifiManager _manager;
    public WifiConnectTool(IWifiManager manager) => _manager = manager;
    public bool HasParameters => true;
    public string Name => "wifiConnect";
    public string Intent => "Connect to a WiFi network.";
    public string GetLlmInstructions() => """
WIFI CONNECT
Connect to a WiFi network.
Parameters:
- number: optional scanned network number.
- ssid: optional network name.
- password: optional network password.
Provide either number or ssid. When a scanned number is available, prefer number.
If password is needed but not supplied, ask for it.
Examples:
"Connect to network 2" -> {tool:wifiConnect,number=2}
"Connect to MyWiFi" -> {tool:wifiConnect,ssid="MyWiFi"}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var number = request.GetInt32("number");
        var ssid = request.GetString("ssid")?.Trim();

        if (number is null && string.IsNullOrWhiteSpace(ssid))
        {
            var pending = new ToolRequest
            {
                Name = Name,
                Arguments = new JObject
                {
                    ["ssid"] = ToolRequest.RequiredValue
                }
            };

            return ToolResult.MissingParameter(
                Name,
                "Which WiFi network should I connect to? Give me the network name.",
                pending);
        }

        if (number.HasValue)
        {
            if (!_manager.TryGetScannedNetwork(number.Value, out var network) || network is null)
                return ToolResult.Failed(Name, $"WiFi network number {number.Value} is not in the current scan.");

            ssid = network.Ssid;

            if (network.IsSecured && string.IsNullOrWhiteSpace(request.GetString("password")))
            {
                var pending = new ToolRequest
                {
                    Name = Name,
                    Arguments = (JObject)request.Arguments.DeepClone()
                };
                pending.Arguments["password"] = ToolRequest.RequiredValue;

                return ToolResult.MissingParameter(
                    Name,
                    $"What's the password for {ssid}?",
                    pending);
            }
        }
        else if (!string.IsNullOrWhiteSpace(ssid) &&
                 string.IsNullOrWhiteSpace(request.GetString("password")))
        {
            var scanned = await _manager.ScanAsync(cancellationToken);
            var network = scanned.FirstOrDefault(x =>
                string.Equals(x.Ssid, ssid, StringComparison.Ordinal));

            if (network?.IsSecured == true)
            {
                var pending = new ToolRequest
                {
                    Name = Name,
                    Arguments = (JObject)request.Arguments.DeepClone()
                };
                pending.Arguments["password"] = ToolRequest.RequiredValue;

                return ToolResult.MissingParameter(
                    Name,
                    $"What's the password for {ssid}?",
                    pending);
            }
        }

        var connected = await _manager.ConnectAsync(ssid!, request.GetString("password"), cancellationToken);
        return connected
            ? ToolResult.Successful(Name, $"Connected to WiFi network '{ssid}'.")
            : ToolResult.Failed(Name, $"Unable to connect to WiFi network '{ssid}'.");
    }
}
