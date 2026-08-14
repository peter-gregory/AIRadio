namespace AIRadio.Server.Services.Audio
{
    /// <summary>
    /// Plays WAV sound effects from the application's configured
    /// SoundsDirectory.
    ///
    /// Audio sequencing, ducking, cancellation, and output routing
    /// remain the responsibility of AudioManager.
    /// </summary>
    public interface IWavAudioEventPlayer
    {
        Task PlayAsync(
            string soundName,
            CancellationToken cancellationToken = default);
    }

    public sealed class WavAudioEventPlayer
        : IWavAudioEventPlayer
    {
        private readonly IConfiguration _configuration;
        private readonly IAudioManager _audioManager;
        private readonly ILogger<WavAudioEventPlayer> _logger;

        public WavAudioEventPlayer(
            IConfiguration configuration,
            IAudioManager audioManager,
            ILogger<WavAudioEventPlayer> logger)
        {
            _configuration = configuration;
            _audioManager = audioManager;
            _logger = logger;
        }

        // ============================================================
        // CONFIGURATION
        // ============================================================

        private string SoundsDirectory =>
            _configuration[
                "Application:SoundsDirectory"]
            ?? throw new InvalidOperationException(
                "Application:SoundsDirectory is not configured.");

        // ============================================================
        // PLAY
        // ============================================================

        public async Task PlayAsync(
            string soundName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                soundName);

            var filePath =
                ResolveSoundPath(
                    soundName);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Sound effect '{SoundName}' was not found at {FilePath}.",
                    soundName,
                    filePath);

                throw new FileNotFoundException(
                    $"Sound effect '{soundName}' was not found.",
                    filePath);
            }

            _logger.LogDebug(
                "Playing sound effect {SoundName} from {FilePath}.",
                soundName,
                filePath);

            await _audioManager.PlaySoundAsync(
                filePath,
                cancellationToken);
        }

        // ============================================================
        // PATH RESOLUTION
        // ============================================================

        private string ResolveSoundPath(
            string soundName)
        {
            var name =
                soundName.Trim();

            /*
             * Sound tags normally contain the logical name:
             *
             *     {sound:startup}
             *
             * The actual file is:
             *
             *     /home/radio/.radio/sounds/startup.wav
             *
             * Permit callers to omit the .wav extension.
             */

            if (!name.EndsWith(
                    ".wav",
                    StringComparison.OrdinalIgnoreCase))
            {
                name += ".wav";
            }

            /*
             * Prevent a sound tag from escaping the configured
             * SoundsDirectory through ../ path traversal.
             */
            var root =
                Path.GetFullPath(
                    SoundsDirectory);

            var path =
                Path.GetFullPath(
                    Path.Combine(
                        root,
                        name));

            if (!IsUnderDirectory(
                    path,
                    root))
            {
                throw new ArgumentException(
                    "Sound name resolves outside the configured " +
                    "SoundsDirectory.",
                    nameof(soundName));
            }

            return path;
        }

        private static bool IsUnderDirectory(
            string path,
            string directory)
        {
            var normalizedDirectory =
                directory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return path.StartsWith(
                normalizedDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
