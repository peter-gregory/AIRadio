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

        public JsonLocationStore(
            IConfiguration configuration)
        {
            var dataDirectory =
                configuration["Radio:DataDirectory"];

            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                var home =
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile);

                dataDirectory =
                    Path.Combine(
                        home,
                        ".radio",
                        "data");
            }

            Directory.CreateDirectory(
                dataDirectory);

            _filePath =
                Path.Combine(
                    dataDirectory,
                    "location.json");
        }

        public async Task<RadioLocation?> GetAsync(
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

            return JsonConvert.DeserializeObject<RadioLocation>(
                json);
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
