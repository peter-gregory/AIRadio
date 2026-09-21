using AIRadio.Server.Models.Radio;
using System.Text.Json;

namespace AIRadio.Server.Services.Radio;

public interface IRadioStationStore
{
    bool IsSaved(string stationId);
    void Save(RadioStation station);
    bool Forget(string stationId);
    bool IsFavorite(string stationId);
    void SetFavorite(RadioStation station);
    IReadOnlyList<RadioStation> GetSavedStations();
    IReadOnlyList<RadioStation> GetFavoriteStations();
}

public sealed class RadioStationStore : IRadioStationStore
{
    private const string DataFileName = "radio-stations.json";

    private sealed class StoreData
    {
        public List<RadioStation> SavedStations { get; set; } = [];

        // Kept only for one-time migration from the earlier two-list format.
        public List<RadioStation> FavoriteStations { get; set; } = [];
    }

    private readonly ILogger<RadioStationStore> _logger;
    private readonly string _dataFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly object _sync = new();
    private StoreData _data = new();

    public RadioStationStore(IConfiguration configuration, ILogger<RadioStationStore> logger)
    {
        _logger = logger;
        _dataFile = ResolveDataFile(configuration);
        var directory = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Load();
        _logger.LogInformation(
            "Radio station store initialized with {SavedCount} saved stations ({FavoriteCount} favorites).",
            _data.SavedStations.Count, _data.SavedStations.Count(x => x.IsFavorite));
    }

    public bool IsSaved(string stationId)
    {
        lock (_sync) return Find(_data.SavedStations, stationId) is not null;
    }

    public void Save(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        lock (_sync)
        {
            if (Find(_data.SavedStations, station.Id) is not null) return;
            var saved = station.Clone();
            saved.IsFavorite = false;
            _data.SavedStations.Add(saved);
        }
        SaveToDisk();
    }

    public bool Forget(string stationId)
    {
        bool removed;
        lock (_sync)
        {
            removed = _data.SavedStations.RemoveAll(
                station => string.Equals(station.Id, stationId, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) SaveToDisk();
        return removed;
    }

    public bool IsFavorite(string stationId)
    {
        lock (_sync) return Find(_data.SavedStations, stationId)?.IsFavorite == true;
    }

    public void SetFavorite(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        lock (_sync)
        {
            var existing = Find(_data.SavedStations, station.Id);
            if (existing is null)
            {
                existing = station.Clone();
                _data.SavedStations.Add(existing);
            }
            existing.IsFavorite = true;
            _data.SavedStations.Remove(existing);
            _data.SavedStations.Insert(0, existing);
        }
        SaveToDisk();
    }

    public IReadOnlyList<RadioStation> GetSavedStations()
    {
        lock (_sync) return _data.SavedStations.Select(x => x.Clone()).ToList();
    }

    public IReadOnlyList<RadioStation> GetFavoriteStations()
    {
        lock (_sync) return _data.SavedStations.Where(x => x.IsFavorite).Select(x => x.Clone()).ToList();
    }

    private static RadioStation? Find(IEnumerable<RadioStation> stations, string stationId) =>
        string.IsNullOrWhiteSpace(stationId)
            ? null
            : stations.FirstOrDefault(station => string.Equals(station.Id, stationId, StringComparison.OrdinalIgnoreCase));

    private static string ResolveDataFile(IConfiguration configuration)
    {
        var dataDirectory = configuration["Application:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new InvalidOperationException("Application:DataDirectory is not configured.");
        return Path.GetFullPath(Path.Combine(dataDirectory, DataFileName));
    }

    private void Load()
    {
        if (!File.Exists(_dataFile)) return;
        try
        {
            var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_dataFile), _jsonOptions) ?? new StoreData();

            // Older versions persisted favorites in a separate list. Merge those
            // entries into the single saved list and preserve their favorite flag.
            foreach (var favorite in data.FavoriteStations)
            {
                var existing = Find(data.SavedStations, favorite.Id);
                if (existing is null)
                {
                    existing = favorite.Clone();
                    data.SavedStations.Add(existing);
                }

                existing.IsFavorite = true;
            }

            data.FavoriteStations.Clear();
            data.SavedStations = data.SavedStations
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            _data = data;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unable to load radio station store from {DataFile}.", _dataFile);
            _data = new StoreData();
        }
    }

    private void SaveToDisk()
    {
        StoreData snapshot;
        lock (_sync)
        {
            snapshot = new StoreData
            {
                SavedStations = _data.SavedStations.Select(x => x.Clone()).ToList()
            };
        }
        var directory = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryFile = _dataFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(snapshot, _jsonOptions));
        File.Move(temporaryFile, _dataFile, true);
    }
}