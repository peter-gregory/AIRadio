namespace AIRadio.Server.Models.Mpv
{
    public sealed class MpvErrorEventArgs : EventArgs
    {
        public MpvErrorEventArgs(
            string message,
            Exception? exception = null,
            string? operation = null)
        {
            Message = message;
            Exception = exception;
            Operation = operation;
            Timestamp = DateTimeOffset.UtcNow;
        }


        /// <summary>
        /// Human-readable description of the error.
        /// </summary>
        public string Message { get; }


        /// <summary>
        /// Exception that caused the failure, if available.
        /// </summary>
        public Exception? Exception { get; }


        /// <summary>
        /// Operation being performed when the error occurred.
        /// Examples: Connect, SendCommand, ReceiveLoop.
        /// </summary>
        public string? Operation { get; }


        /// <summary>
        /// UTC timestamp when the error was generated.
        /// </summary>
        public DateTimeOffset Timestamp { get; }


        public override string ToString()
        {
            if (Exception == null)
            {
                return $"{Operation}: {Message}";
            }

            return $"{Operation}: {Message} - {Exception.Message}";
        }
    }
}
