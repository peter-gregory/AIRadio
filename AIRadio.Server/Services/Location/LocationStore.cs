using AIRadio.Server.Models.Location;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.Location
{
    public interface ILocationStore
    {
        Task<RadioLocation?> GetAsync(
            CancellationToken cancellationToken = default);

        Task SaveAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default);

        Task ClearAsync(
            CancellationToken cancellationToken = default);
    }

    public sealed class JsonLocationStore : ILocationStore
    {
        private readonly string _filePath;
        private readonly string _legacyFilePath;

        public JsonLocationStore(
            IConfiguration configuration)
        {
            var configDirectory =
                configuration["Application:ConfigDirectory"];

            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                var home =
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile);

                configDirectory =
                    Path.Combine(
                        home,
                        ".radio",
                        "config");
            }

            Directory.CreateDirectory(
                configDirectory);

            _filePath =
                Path.Combine(
                    configDirectory,
                    "location.json");

            var home =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);

            _legacyFilePath =
                Path.Combine(
                    home,
                    ".radio",
                    "data",
                    "location.json");
        }

        public async Task<RadioLocation?> GetAsync(
            CancellationToken cancellationToken = default)
        {
            var filePath = _filePath;
            var migrateLegacy = false;

            if (!File.Exists(filePath) &&
                File.Exists(_legacyFilePath))
            {
                filePath = _legacyFilePath;
                migrateLegacy = true;
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            var json =
                await File.ReadAllTextAsync(
                    filePath,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var location =
                JsonConvert.DeserializeObject<RadioLocation>(
                    json);

            if (location is not null &&
                migrateLegacy)
            {
                await SaveAsync(
                    location,
                    cancellationToken);
            }

            return location;
        }

        public async Task SaveAsync(
            RadioLocation location,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(location);

            var json =
                JsonConvert.SerializeObject(
                    location,
                    Formatting.Indented);

            var temporaryPath =
                _filePath + ".tmp";

            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                cancellationToken);

            File.Move(
                temporaryPath,
                _filePath,
                true);
        }

        public Task ClearAsync(
            CancellationToken cancellationToken = default)
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            return Task.CompletedTask;
        }
    }
}
