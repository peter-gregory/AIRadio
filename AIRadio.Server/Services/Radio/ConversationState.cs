namespace AIRadio.Server.Services.Radio
{
    public enum ConversationState
    {
        Idle,
        Processing,
        WaitingForInput,
        Complete
    }

    public sealed class ConversationStateChangedEventArgs : EventArgs
    {
        public ConversationStateChangedEventArgs(
            ConversationState previous,
            ConversationState current,
            Guid conversationId)
        {
            Previous = previous;
            Current = current;
            ConversationId = conversationId;
        }

        public ConversationState Previous { get; }
        public ConversationState Current { get; }
        public Guid ConversationId { get; }
    }
}
