using AIRadio.Server.Services.Radio;

namespace AIRadio.Server.Services.AI
{
    public interface IInterruptionDetector
    {
        Task<InterruptionResult> EvaluateAsync(
            string text,
            RadioEngineState state,
            CancellationToken cancellationToken);
    }

    public class InterruptionDetector
    {
    }

    public sealed record InterruptionResult(
    InterruptionType Type,
    string? Reason = null);

    public enum InterruptionType
    {
        None,

        // User is continuing the current conversation.
        Continue,

        // New request should temporarily coexist with radio.
        Duck,

        // Current operation should be terminated and replaced.
        Replace
    }
}
