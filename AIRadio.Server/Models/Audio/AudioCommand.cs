namespace AIRadio.Server.Models.Audio
{
    /// <summary>
    /// Represents a control command for the AudioManager.
    /// </summary>
    public sealed class AudioCommand
    {
        /// <summary>
        /// Gets the command type.
        /// </summary>
        public AudioCommandType Type { get; init; }


        /// <summary>
        /// Gets an optional integer value associated with the command.
        /// For example, a volume percentage.
        /// </summary>
        public int? IntValue { get; init; }


        /// <summary>
        /// Gets an optional string value associated with the command.
        /// </summary>
        public string? StringValue { get; init; }


        /// <summary>
        /// Gets an optional binary payload.
        /// Reserved for future expansion.
        /// </summary>
        public ReadOnlyMemory<byte>? Data { get; init; }


        /// <summary>
        /// Gets an optional identifier for diagnostics.
        /// </summary>
        public string? CommandId { get; init; }


        public override string ToString()
        {
            return Type switch
            {
                AudioCommandType.SetMasterVolume =>
                    $"SetMasterVolume({IntValue})",

                AudioCommandType.StopPlayback =>
                    "StopPlayback",

                AudioCommandType.ClearQueue =>
                    "ClearQueue",

                AudioCommandType.WaitForCompletion =>
                    "WaitForCompletion",

                _ =>
                    Type.ToString()
            };
        }
    }

    /// <summary>
    /// Commands understood by the AudioManager.
    /// </summary>
    public enum AudioCommandType
    {
        /// <summary>
        /// Stop playback immediately and clear queued audio.
        /// </summary>
        StopPlayback = 0,

        /// <summary>
        /// Remove queued audio that has not yet begun playing.
        /// </summary>
        ClearQueue = 1,

        /// <summary>
        /// Wait until all queued audio has completed.
        /// </summary>
        WaitForCompletion = 2,

        /// <summary>
        /// Set the appliance master volume.
        /// </summary>
        SetMasterVolume = 3,

        Speech = 4,
        WaveEffect = 5,
        MediaCommand = 6,
        Duck = 7,
        Resume = 8
    }
}
