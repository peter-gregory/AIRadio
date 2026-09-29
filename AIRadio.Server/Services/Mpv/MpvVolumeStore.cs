using Newtonsoft.Json;

namespace AIRadio.Server.Services.Mpv
{
    public interface IMpvVolumeStore
    {
        Task<int?> GetAsync(
            CancellationToken cancellationToken = default);

        Task SaveAsync(
            int volume,
            CancellationToken cancellationToken = default);
    }

    public sealed class JsonMpvVolumeStore : IMpvVolumeStore
    {
        private readonly string _filePath;

        public JsonMpvVolumeStore(
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
                    "volume.json");
        }

        public async Task<int?> GetAsync(
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json =
                await File.ReadAllTextAsync(
                    _filePath,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var value =
                JsonConvert.DeserializeObject<int>(json);

            return Math.Clamp(value, 0, 100);
        }

        public async Task SaveAsync(
            int volume,
            CancellationToken cancellationToken = default)
        {
            volume = Math.Clamp(volume, 0, 100);

            var json =
                JsonConvert.SerializeObject(
                    volume,
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
    }
}
