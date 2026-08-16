using System.Security.Cryptography;
using System.Text;

namespace AIRadio.Server.Services.Tts;

public sealed class PiperAudioCache
{
    private sealed class Entry
    {
        public required string Key { get; init; }
        public required byte[] Audio { get; init; }
        public required LinkedListNode<string> Node { get; init; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly LinkedList<string> _lru = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private long _currentBytes;
    private readonly ILogger<PiperAudioCache> _logger;

    public PiperAudioCache(
        IConfiguration configuration,
        ILogger<PiperAudioCache> logger)
    {
        _logger = logger;

        _directory = configuration["Piper:CacheDirectory"]
            ?? throw new InvalidOperationException(
                "Piper cache directory is not configured.");

        var sizeMb = int.TryParse(
            configuration["Piper:CacheSizeMB"],
            out var size)
                ? size
                : 50;

        if (sizeMb <= 0)
            throw new InvalidOperationException(
                "Piper cache size must be greater than zero.");

        _maxBytes = sizeMb * 1024L * 1024L;
        Directory.CreateDirectory(_directory);
    }

    public async Task<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                Touch(entry);
                return entry.Audio;
            }
        }

        var path = GetPath(key);
        if (!File.Exists(path))
            return null;

        try
        {
            var audio = await File.ReadAllBytesAsync(path, cancellationToken);
            Add(key, audio);
            return audio;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Unable to read Piper cache entry {CacheId}",
                GetCacheId(key));
            return null;
        }
    }

    public async Task SetAsync(
        string key,
        byte[] audio,
        CancellationToken cancellationToken = default)
    {
        Add(key, audio);

        var path = GetPath(key);
        var temporaryPath = path + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                audio,
                cancellationToken);

            File.Move(temporaryPath, path, true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            _logger.LogWarning(
                ex,
                "Unable to persist Piper cache entry {CacheId}",
                GetCacheId(key));
        }
    }

    private void Add(string key, byte[] audio)
    {
        if (audio.LongLength > _maxBytes)
            return;

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Audio.LongLength;
                _lru.Remove(existing.Node);
            }

            var node = _lru.AddFirst(key);
            _entries[key] = new Entry
            {
                Key = key,
                Audio = audio,
                Node = node
            };
            _currentBytes += audio.LongLength;

            while (_currentBytes > _maxBytes && _lru.Last is not null)
            {
                var keyToRemove = _lru.Last.Value;
                _lru.RemoveLast();

                if (_entries.Remove(keyToRemove, out var removed))
                    _currentBytes -= removed.Audio.LongLength;
            }
        }
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node);
        entry.Node = _lru.AddFirst(entry.Key);
    }

    private string GetPath(string key) =>
        Path.Combine(_directory, GetCacheId(key) + ".wav");

    private static string GetCacheId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
