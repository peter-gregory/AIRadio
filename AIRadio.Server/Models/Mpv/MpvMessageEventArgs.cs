using System.Text.Json;

namespace AIRadio.Server.Models.Mpv
{
    public sealed class MpvMessageEventArgs : EventArgs
    {
        public MpvMessageEventArgs(
            JsonElement message)
        {
            Message = message;

            EventName = GetStringProperty(
                "event");

            PropertyName = GetStringProperty(
                "name");

            Data = GetProperty(
                "data");
        }


        /// <summary>
        /// Complete raw JSON message from MPV.
        /// </summary>
        public JsonElement Message { get; }


        /// <summary>
        /// MPV event name.
        /// Example: property-change, file-loaded, end-file.
        /// </summary>
        public string? EventName { get; }


        /// <summary>
        /// Property name for property-change events.
        /// Example: volume, pause, metadata.
        /// </summary>
        public string? PropertyName { get; }


        /// <summary>
        /// Event data payload.
        /// </summary>
        public JsonElement? Data { get; }



        private string? GetStringProperty(
            string propertyName)
        {
            if (!Message.TryGetProperty(
                    propertyName,
                    out var property))
            {
                return null;
            }


            if (property.ValueKind != JsonValueKind.String)
            {
                return null;
            }


            return property.GetString();
        }



        private JsonElement? GetProperty(
            string propertyName)
        {
            if (!Message.TryGetProperty(
                    propertyName,
                    out var property))
            {
                return null;
            }


            return property;
        }
    }
}
