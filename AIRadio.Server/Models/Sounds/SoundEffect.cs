namespace AIRadio.Server.Models.Sounds
{
    public sealed class SoundEffect
    {
        public required string Tag { get; init; }

        public required IReadOnlyList<SoundEffectWave> Effects { get; init; }

        public SoundEffectWave GetRandom()
        {
            if (Effects.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Sound effect '{Tag}' contains no WAV files.");
            }

            return Effects[Random.Shared.Next(Effects.Count)];
        }
    }

    public sealed class SoundEffectWave
    {
        public required string FileName { get; init; }

        public required ReadOnlyMemory<byte> WavData { get; init; }
    }
}
