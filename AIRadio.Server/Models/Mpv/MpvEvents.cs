namespace AIRadio.Server.Models.Mpv
{
    public static class MpvEvents
    {
        /// <summary>
        /// MPV reports that a property value changed.
        /// </summary>
        public const string PropertyChange = "property-change";


        /// <summary>
        /// MPV started loading a new media item.
        /// For this application, this represents beginning radio stream loading.
        /// </summary>
        public const string StartFile = "start-file";


        /// <summary>
        /// MPV successfully loaded the media item.
        /// For this application, this represents the radio stream becoming active.
        /// </summary>
        public const string FileLoaded = "file-loaded";


        /// <summary>
        /// MPV finished playback of the current media item.
        /// </summary>
        public const string EndFile = "end-file";


        /// <summary>
        /// MPV entered idle state.
        /// </summary>
        public const string Idle = "idle";


        /// <summary>
        /// MPV reports that the client is ready after initialization.
        /// </summary>
        public const string IdleActive = "idle-active";


        /// <summary>
        /// MPV reports a client message.
        /// </summary>
        public const string ClientMessage = "client-message";


        /// <summary>
        /// MPV reports a shutdown event.
        /// </summary>
        public const string Shutdown = "shutdown";
    }
}
