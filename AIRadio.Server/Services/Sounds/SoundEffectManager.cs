using AIRadio.Server.Models.Sounds;
using Microsoft.Extensions.Logging;

namespace AIRadio.Server.Services.Sounds
{
    public interface ISoundEffectManager
    {
        bool IsInitialized { get; }

        int SoundEffectCount { get; }

        Task InitializeAsync(
            string soundsDirectory,
            CancellationToken cancellationToken = default);

        bool HasSoundEffect(string tag);

        bool TryGetSoundEffect(
            string tag,
            out SoundEffect? soundEffect);

        SoundEffect? GetSoundEffect(string tag);

        SoundEffectWave? GetRandomSound(string tag);

        IReadOnlyList<string> GetTags();

        void Clear();
    }

    public sealed class SoundEffectManager : ISoundEffectManager
    {
        private readonly ILogger<SoundEffectManager> _logger;

        private readonly Dictionary<string, SoundEffect> _soundEffects =
            new(StringComparer.OrdinalIgnoreCase);

        public bool IsInitialized { get; private set; }

        public int SoundEffectCount =>
            _soundEffects.Count;

        public SoundEffectManager(
            ILogger<SoundEffectManager> logger)
        {
            _logger = logger ??
                throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeAsync(
            string soundsDirectory,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(soundsDirectory);

            if (IsInitialized)
            {
                return;
            }

            if (!Directory.Exists(soundsDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Sound effects directory was not found: {soundsDirectory}");
            }

            var discoveredEffects =
                new Dictionary<string, List<SoundEffectWave>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(
                         soundsDirectory,
                         "*.wav",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directory = Path.GetDirectoryName(file);

                if (string.IsNullOrWhiteSpace(directory))
                {
                    _logger.LogWarning(
                        "Ignoring sound effect with no parent directory: {File}",
                        file);

                    continue;
                }

                // The directory immediately containing the WAV file
                // is the sound-effect tag.
                //
                // Example:
                //   sounds/alarm/dingdongding/doorbell.wav
                //
                // Tag = "dingdongding"
                var tag = Path.GetFileName(directory);

                if (string.IsNullOrWhiteSpace(tag))
                {
                    _logger.LogWarning(
                        "Ignoring sound effect with invalid tag directory: {File}",
                        file);

                    continue;
                }

                byte[] wavData;

                try
                {
                    wavData = await File.ReadAllBytesAsync(
                        file,
                        cancellationToken);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unable to load sound effect WAV file: {File}",
                        file);

                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Access denied loading sound effect WAV file: {File}",
                        file);

                    continue;
                }

                if (wavData.Length == 0)
                {
                    _logger.LogWarning(
                        "Ignoring empty sound effect WAV file: {File}",
                        file);

                    continue;
                }

                if (!discoveredEffects.TryGetValue(
                        tag,
                        out var effects))
                {
                    effects = [];
                    discoveredEffects.Add(tag, effects);
                }

                effects.Add(
                    new SoundEffectWave
                    {
                        FileName = Path.GetFileName(file),
                        WavData = wavData
                    });
            }

            foreach (var entry in discoveredEffects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Value.Count == 0)
                {
                    _logger.LogWarning(
                        "Sound effect tag '{Tag}' contains no usable WAV files.",
                        entry.Key);

                    continue;
                }

                _soundEffects.Add(
                    entry.Key,
                    new SoundEffect
                    {
                        Tag = entry.Key,
                        Effects = entry.Value
                    });
            }

            IsInitialized = true;

            var waveCount = _soundEffects.Values.Sum(
                static soundEffect => soundEffect.Effects.Count);

            _logger.LogInformation(
                "Sound effect library initialized: {SoundEffectCount} tags, {WaveCount} WAV files.",
                _soundEffects.Count,
                waveCount);
        }

        public bool HasSoundEffect(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);

            return _soundEffects.ContainsKey(tag);
        }

        public bool TryGetSoundEffect(
            string tag,
            out SoundEffect? soundEffect)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);

            return _soundEffects.TryGetValue(
                tag,
                out soundEffect);
        }

        public SoundEffect? GetSoundEffect(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);

            return _soundEffects.TryGetValue(
                tag,
                out var soundEffect)
                    ? soundEffect
                    : null;
        }

        public SoundEffectWave? GetRandomSound(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);

            if (!_soundEffects.TryGetValue(
                    tag,
                    out var soundEffect))
            {
                return null;
            }

            return soundEffect.GetRandom();
        }

        public IReadOnlyList<string> GetTags()
        {
            return _soundEffects.Keys
                .OrderBy(static tag => tag)
                .ToArray();
        }

        public void Clear()
        {
            _soundEffects.Clear();
            IsInitialized = false;
        }
    }
}
