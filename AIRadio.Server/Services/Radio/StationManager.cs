using AIRadio.Server.Models.Radio;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.Radio
{
    public interface IStationManager
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RadioStation>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RadioStation>> GetFavoritesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RadioStation>> GetSavedStationsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RadioStation>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RadioStation>> SearchAsync(RadioSearchCriteria criteria, CancellationToken cancellationToken = default);
        Task<RadioStation?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
        Task SaveCurrentStationAsync(RadioStation station, CancellationToken cancellationToken = default);
        Task SaveAsync(RadioStation station, CancellationToken cancellationToken = default);
        Task RemoveAsync(string id, CancellationToken cancellationToken = default);
        Task SetFavoriteAsync(string id, bool favorite, CancellationToken cancellationToken = default);
        Task<RadioStation?> GetFavoriteAsync(int favoriteNumber, CancellationToken cancellationToken = default);
        Task MarkPlayedAsync(RadioStation station, CancellationToken cancellationToken = default);
    }

    public sealed class StationManager : IStationManager
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<StationManager> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private StationRegistry _registry = new();

        private string DataDirectory =>
            _configuration["Application:DataDirectory"]
            ?? throw new InvalidOperationException("Application:DataDirectory is not configured.");

        private string StationsFilePath => Path.Combine(DataDirectory, "stations.json");

        public StationManager(IConfiguration configuration, ILogger<StationManager> logger)
        {
            _configuration = configuration;
            _logger = logger;
            Directory.CreateDirectory(DataDirectory);
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(StationsFilePath))
                {
                    _registry = new StationRegistry();
                    await SaveRegistryAsync(cancellationToken);
                    return;
                }

                var json = await File.ReadAllTextAsync(StationsFilePath, cancellationToken);
                _registry = JsonConvert.DeserializeObject<StationRegistry>(json) ?? new StationRegistry();
                _logger.LogInformation("Loaded {StationCount} stations from {FilePath}.", _registry.Stations.Count, StationsFilePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to initialize station registry from {FilePath}.", StationsFilePath);
                _registry = new StationRegistry();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<RadioStation>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try { return CloneStations(_registry.Stations); }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<RadioStation>> GetFavoritesAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try { return CloneStations(_registry.Stations.Where(x => x.FavoriteNumber.HasValue).OrderBy(x => x.FavoriteNumber)); }
            finally { _lock.Release(); }
        }

        public async Task<RadioStation?> GetFavoriteAsync(int favoriteNumber, CancellationToken cancellationToken = default)
        {
            if (favoriteNumber < 1) return null;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var station = _registry.Stations.FirstOrDefault(x => x.FavoriteNumber == favoriteNumber);
                return station?.Clone();
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<RadioStation>> GetSavedStationsAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return CloneStations(_registry.Stations.Where(x => string.Equals(x.Source, "Saved", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name));
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<RadioStation>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default)
        {
            limit = Math.Clamp(limit, 1, 100);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return CloneStations(_registry.Stations.Where(x => x.LastPlayed.HasValue).OrderByDescending(x => x.LastPlayed).Take(limit));
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<RadioStation>> SearchAsync(RadioSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(criteria);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                IEnumerable<RadioStation> query = _registry.Stations;
                if (!string.IsNullOrWhiteSpace(criteria.StationName))
                {
                    var value = criteria.StationName.Trim();
                    query = query.Where(x => x.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
                }
                if (!string.IsNullOrWhiteSpace(criteria.Genre))
                {
                    var value = criteria.Genre.Trim();
                    query = query.Where(x => x.Tags.Any(tag => tag.Contains(value, StringComparison.OrdinalIgnoreCase)));
                }
                if (!string.IsNullOrWhiteSpace(criteria.Tag))
                {
                    var value = criteria.Tag.Trim();
                    query = query.Where(x => x.Tags.Any(tag => tag.Contains(value, StringComparison.OrdinalIgnoreCase)));
                }
                if (!string.IsNullOrWhiteSpace(criteria.Language))
                {
                    var value = criteria.Language.Trim();
                    query = query.Where(x => x.Languages.Any(language => language.Contains(value, StringComparison.OrdinalIgnoreCase)));
                }
                if (!string.IsNullOrWhiteSpace(criteria.Country))
                {
                    var value = criteria.Country.Trim();
                    query = query.Where(x => string.Equals(x.Country, value, StringComparison.OrdinalIgnoreCase) || string.Equals(x.CountryCode, value, StringComparison.OrdinalIgnoreCase));
                }
                if (!string.IsNullOrWhiteSpace(criteria.State))
                {
                    var value = criteria.State.Trim();
                    query = query.Where(x => x.State?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
                }
                var results = query.OrderByDescending(x => x.FavoriteNumber.HasValue).ThenByDescending(x => x.PlayCount).ThenBy(x => x.Name).Select(x => x.Clone());
                if (criteria.Limit > 0) results = results.Take(criteria.Limit);
                return results.ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task<RadioStation?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var station = _registry.Stations.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                return station?.Clone();
            }
            finally { _lock.Release(); }
        }

        public async Task SaveCurrentStationAsync(RadioStation station, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(station);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var existing = FindStation(station);
                if (existing is null) _registry.Stations.Add(existing = station.Clone());
                else CopyStationMetadata(station, existing);
                existing.LastPlayed = DateTimeOffset.UtcNow;
                existing.PlayCount++;
                await SaveRegistryAsync(cancellationToken);
            }
            finally { _lock.Release(); }
        }

        public async Task SaveAsync(RadioStation station, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(station);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var existing = FindStation(station);
                if (existing is null) _registry.Stations.Add(station.Clone());
                else CopyStationMetadata(station, existing);
                await SaveRegistryAsync(cancellationToken);
            }
            finally { _lock.Release(); }
        }

        public async Task MarkPlayedAsync(RadioStation station, CancellationToken cancellationToken = default) =>
            await SaveCurrentStationAsync(station, cancellationToken);

        public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var station = _registry.Stations.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (station is null) return;
                _registry.Stations.Remove(station);
                await SaveRegistryAsync(cancellationToken);
            }
            finally { _lock.Release(); }
        }

        public async Task SetFavoriteAsync(string id, bool favorite, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var station = _registry.Stations.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (station is null) return;
                station.FavoriteNumber = favorite ? station.FavoriteNumber ?? GetNextFavoriteNumber() : null;
                await SaveRegistryAsync(cancellationToken);
            }
            finally { _lock.Release(); }
        }

        private RadioStation? FindStation(RadioStation station)
        {
            if (!string.IsNullOrWhiteSpace(station.Id))
            {
                var result = _registry.Stations.FirstOrDefault(x => string.Equals(x.Id, station.Id, StringComparison.OrdinalIgnoreCase));
                if (result is not null) return result;
            }
            if (!string.IsNullOrWhiteSpace(station.Source) && !string.IsNullOrWhiteSpace(station.SourceId))
            {
                var result = _registry.Stations.FirstOrDefault(x => string.Equals(x.Source, station.Source, StringComparison.OrdinalIgnoreCase) && string.Equals(x.SourceId, station.SourceId, StringComparison.OrdinalIgnoreCase));
                if (result is not null) return result;
            }
            if (!string.IsNullOrWhiteSpace(station.StreamUrl))
                return _registry.Stations.FirstOrDefault(x => string.Equals(x.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        private int GetNextFavoriteNumber()
        {
            var number = 1;
            while (_registry.Stations.Any(x => x.FavoriteNumber == number)) number++;
            return number;
        }

        private static void CopyStationMetadata(RadioStation source, RadioStation destination)
        {
            destination.Id = source.Id;
            destination.Name = source.Name;
            destination.StreamUrl = source.StreamUrl;
            destination.Description = source.Description;
            destination.Homepage = source.Homepage;
            destination.Favicon = source.Favicon;
            destination.Tags = [.. source.Tags];
            destination.Country = source.Country;
            destination.CountryCode = source.CountryCode;
            destination.State = source.State;
            destination.Languages = [.. source.Languages];
            destination.Codec = source.Codec;
            destination.Bitrate = source.Bitrate;
            destination.Source = source.Source;
            destination.SourceId = source.SourceId;
            destination.IsHttps = source.IsHttps;
            destination.IsOnline = source.IsOnline;
            destination.Votes = source.Votes;
            destination.Latitude = source.Latitude;
            destination.Longitude = source.Longitude;
        }

        private async Task SaveRegistryAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonConvert.SerializeObject(_registry, Formatting.Indented);
            var tempPath = $"{StationsFilePath}.tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, StationsFilePath, true);
        }

        private static IReadOnlyList<RadioStation> CloneStations(IEnumerable<RadioStation> stations) =>
            stations.Select(x => x.Clone()).ToList();
    }
}
