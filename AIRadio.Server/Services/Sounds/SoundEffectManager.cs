using AIRadio.Server.Models.Sounds;

namespace AIRadio.Server.Services.Sounds
{
    public interface ISoundEffectManager
    {
        bool IsInitialized { get; }
        int SoundEffectCount { get; }
        Task InitializeAsync(string soundsDirectory, CancellationToken cancellationToken = default);
        bool HasSoundEffect(string tag);
        bool TryGetSoundEffect(string tag, out SoundEffect? soundEffect);
        SoundEffect? GetSoundEffect(string tag);
        SoundEffectWave? GetRandomSound(string tag);
        IReadOnlyList<string> GetTags();
        string GetPromptText();
        void Clear();
    }

    public sealed class SoundEffectManager : ISoundEffectManager
    {
        private readonly ILogger<SoundEffectManager> _logger;
        private Dictionary<string, SoundEffect> _soundEffects = new(StringComparer.OrdinalIgnoreCase);
        private string _promptText = string.Empty;

        public bool IsInitialized { get; private set; }
        public int SoundEffectCount => _soundEffects.Count;

        public SoundEffectManager(ILogger<SoundEffectManager> logger) => _logger = logger;

        public async Task InitializeAsync(string soundsDirectory, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(soundsDirectory);
            if (IsInitialized) return;
            if (!Directory.Exists(soundsDirectory))
                throw new DirectoryNotFoundException($"Sound effects directory was not found: {soundsDirectory}");

            var discovered = new Dictionary<string, List<SoundEffectWave>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(soundsDirectory, "*.wav", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(file);
                var tag = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(tag))
                {
                    _logger.LogWarning("Ignoring sound effect with invalid tag directory: {File}", file);
                    continue;
                }

                byte[] wavData;
                try
                {
                    wavData = await File.ReadAllBytesAsync(file, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Unable to load sound effect WAV file: {File}", file);
                    continue;
                }

                if (wavData.Length == 0)
                {
                    _logger.LogWarning("Ignoring empty sound effect WAV file: {File}", file);
                    continue;
                }

                if (!discovered.TryGetValue(tag, out var effects))
                {
                    effects = [];
                    discovered.Add(tag, effects);
                }

                effects.Add(new SoundEffectWave { FileName = Path.GetFileName(file), WavData = wavData });
            }

            var soundEffects = new Dictionary<string, SoundEffect>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, effects) in discovered)
            {
                if (effects.Count == 0) continue;
                soundEffects.Add(tag, new SoundEffect { Tag = tag, Effects = effects });
            }

            _soundEffects = soundEffects;
            _promptText = await BuildPromptTextAsync(soundsDirectory, _soundEffects.Keys, cancellationToken);
            IsInitialized = true;

            _logger.LogInformation(
                "Sound effect library initialized: {SoundEffectCount} tags, {WaveCount} WAV files.",
                _soundEffects.Count,
                _soundEffects.Values.Sum(x => x.Effects.Count));
        }

        public bool HasSoundEffect(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            return _soundEffects.ContainsKey(tag);
        }

        public bool TryGetSoundEffect(string tag, out SoundEffect? soundEffect)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            return _soundEffects.TryGetValue(tag, out soundEffect);
        }

        public SoundEffect? GetSoundEffect(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            return _soundEffects.GetValueOrDefault(tag);
        }

        public SoundEffectWave? GetRandomSound(string tag)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
            return _soundEffects.TryGetValue(tag, out var soundEffect) ? soundEffect.GetRandom() : null;
        }

        public IReadOnlyList<string> GetTags() => _soundEffects.Keys.OrderBy(static x => x).ToArray();
        public string GetPromptText() => _promptText;

        public void Clear()
        {
            _soundEffects = new(StringComparer.OrdinalIgnoreCase);
            _promptText = string.Empty;
            IsInitialized = false;
        }

        private async Task<string> BuildPromptTextAsync(
            string soundsDirectory,
            IEnumerable<string> tags,
            CancellationToken cancellationToken)
        {
            var descriptions = new List<string>();

            foreach (var tag in tags.OrderBy(static x => x))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tagDirectory = FindTagDirectory(soundsDirectory, tag);
                if (tagDirectory is null)
                {
                    _logger.LogWarning("Unable to locate sound directory for tag '{Tag}'.", tag);
                    continue;
                }

                var usageFile = Path.Combine(tagDirectory, "usage.md");
                if (!File.Exists(usageFile)) continue;

                var usage = (await File.ReadAllTextAsync(usageFile, cancellationToken)).Trim();
                if (string.IsNullOrWhiteSpace(usage)) continue;

                // Keep only functional guidance. Markdown formatting and repeated whitespace
                // add tokens without improving tool selection.
                usage = string.Join(
                    ' ',
                    usage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                descriptions.Add($"{{sound:{tag}}}={usage}");
            }

            return string.Join('\n', descriptions);
        }

        private static string? FindTagDirectory(string soundsDirectory, string tag)
        {
            foreach (var categoryDirectory in Directory.EnumerateDirectories(soundsDirectory))
            {
                var tagDirectory = Path.Combine(categoryDirectory, tag);
                if (Directory.Exists(tagDirectory)) return tagDirectory;
            }

            return null;
        }
    }
}
