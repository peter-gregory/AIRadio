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

    public RadioStationStore(
        IConfiguration configuration,
        ILogger<RadioStationStore> logger)
    {
        _logger = logger;
        _dataFile = ResolveDataFile(configuration);

        var directory = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Load();

        _logger.LogInformation(
            "Radio station store initialized with {SavedCount} saved and {FavoriteCount} favorite stations.",
            _data.SavedStations.Count,
            _data.FavoriteStations.Count);
    }

    public bool IsSaved(string stationId)
    {
        lock (_sync)
            return Contains(_data.SavedStations, stationId);
    }

    public void Save(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        lock (_sync)
        {
            if (Contains(_data.SavedStations, station.Id))
                return;

            _data.SavedStations.Add(station.Clone());
        }

        SaveToDisk();
    }

    public bool Forget(string stationId)
    {
        bool removed;

        lock (_sync)
        {
            removed =
                _data.SavedStations.RemoveAll(
                    station => string.Equals(
                        station.Id,
                        stationId,
                        StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (removed)
            SaveToDisk();

        return removed;
    }

    public bool IsFavorite(string stationId)
    {
        lock (_sync)
            return Contains(_data.FavoriteStations, stationId);
    }

    public void SetFavorite(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        lock (_sync)
        {
            if (Contains(_data.FavoriteStations, station.Id))
                return;

            _data.FavoriteStations.Add(station.Clone());
        }

        SaveToDisk();
    }

    public IReadOnlyList<RadioStation> GetSavedStations()
    {
        lock (_sync)
            return _data.SavedStations.Select(x => x.Clone()).ToList();
    }

    public IReadOnlyList<RadioStation> GetFavoriteStations()
    {
        lock (_sync)
            return _data.FavoriteStations.Select(x => x.Clone()).ToList();
    }

    private static bool Contains(
        IEnumerable<RadioStation> stations,
        string stationId) =>
        !string.IsNullOrWhiteSpace(stationId) &&
        stations.Any(
            station => string.Equals(
                station.Id,
                stationId,
                StringComparison.OrdinalIgnoreCase));

    private static string ResolveDataFile(IConfiguration configuration)
    {
        var dataDirectory = configuration["Application:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new InvalidOperationException(
                "Application:DataDirectory is not configured.");

        return Path.GetFullPath(
            Path.Combine(dataDirectory, DataFileName));
    }

    private void Load()
    {
        if (!File.Exists(_dataFile))
            return;

        var json = File.ReadAllText(_dataFile);
        var data =
            JsonSerializer.Deserialize<StoreData>(
                json,
                _jsonOptions);

        _data = data ?? new StoreData();
    }

    private void SaveToDisk()
    {
        StoreData snapshot;

        lock (_sync)
        {
            snapshot = new StoreData
            {
                SavedStations =
                    _data.SavedStations
                        .Select(x => x.Clone())
                        .ToList(),

                FavoriteStations =
                    _data.FavoriteStations
                        .Select(x => x.Clone())
                        .ToList()
            };
        }

        var directory = Path.GetDirectoryName(_dataFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryFile = _dataFile + ".tmp";

        File.WriteAllText(
            temporaryFile,
            JsonSerializer.Serialize(
                snapshot,
                _jsonOptions));

        File.Move(
            temporaryFile,
            _dataFile,
            true);
    }
}
