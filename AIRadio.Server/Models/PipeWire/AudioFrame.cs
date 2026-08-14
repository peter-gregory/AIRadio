namespace AIRadio.Server.Models.PipeWire
{
    public sealed class AudioFrame
    {
        public required ReadOnlyMemory<byte> Data { get; init; }
    }
}
