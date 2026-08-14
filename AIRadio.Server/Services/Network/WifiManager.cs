using AIRadio.Server.Models.Network;
using System.Diagnostics;
using System.Text;

namespace AIRadio.Server.Services.Network
{
    public interface IWifiManager
    {
        Task<IReadOnlyList<WifiNetwork>> ScanAsync(
            CancellationToken cancellationToken = default);

        Task<bool> IsConnectedAsync(
            CancellationToken cancellationToken = default);

        Task<bool> ConnectAsync(
            string ssid,
            string? password = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class WifiManager : IWifiManager
    {
        private const string Command =
            "nmcli";

        public async Task<IReadOnlyList<WifiNetwork>> ScanAsync(
            CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(
                "-t",
                "--separator",
                "\t",
                "-f",
                "SSID,SIGNAL,SECURITY",
                "device",
                "wifi",
                "list",
                "--rescan",
                "yes",
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "Unable to scan for WiFi networks."
                        : result.Error.Trim());
            }

            var networks = new Dictionary<string, WifiNetwork>(
                StringComparer.Ordinal);

            foreach (var line in result.Output.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');

                if (fields.Length < 3)
                    continue;

                var ssid = fields[0].Trim();

                if (string.IsNullOrWhiteSpace(ssid))
                    continue;

                if (!int.TryParse(
                        fields[1].Trim(),
                        out var signal))
                {
                    signal = 0;
                }

                networks[ssid] = new WifiNetwork
                {
                    Ssid = ssid,
                    SignalStrength = signal,
                    Security = fields[2].Trim()
                };
            }

            return networks.Values
                .OrderByDescending(network => network.SignalStrength)
                .ToArray();
        }

        public async Task<bool> IsConnectedAsync(
            CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(
                "-t",
                "-f",
                "DEVICE,TYPE,STATE",
                "device",
                cancellationToken);

            if (result.ExitCode != 0)
                return false;

            foreach (var line in result.Output.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(':');

                if (fields.Length < 3)
                    continue;

                if (fields[1].Equals(
                        "wifi",
                        StringComparison.OrdinalIgnoreCase) &&
                    fields[2].Equals(
                        "connected",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<bool> ConnectAsync(
            string ssid,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ssid);

            var arguments = new List<string>
            {
                "device",
                "wifi",
                "connect",
                ssid
            };

            if (!string.IsNullOrEmpty(password))
            {
                arguments.Add("password");
                arguments.Add(password);
            }

            var result = await RunAsync(
                arguments,
                cancellationToken);

            return result.ExitCode == 0;
        }

        private static async Task<CommandResult> RunAsync(
            string argument1,
            string argument2,
            string argument3,
            string argument4,
            string argument5,
            string argument6,
            string argument7,
            string argument8,
            CancellationToken cancellationToken)
        {
            return await RunAsync(
                new[]
                {
                    argument1,
                    argument2,
                    argument3,
                    argument4,
                    argument5,
                    argument6,
                    argument7,
                    argument8
                },
                cancellationToken);
        }

        private static async Task<CommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Unable to start nmcli.");
            }

            var outputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);

            var errorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(
                cancellationToken);

            return new CommandResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }

        private sealed record CommandResult(
            int ExitCode,
            string Output,
            string Error);
    }
}
