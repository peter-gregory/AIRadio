using System.Runtime.InteropServices;

namespace AIRadio.Server.Services.Audio
{
    internal static class PipeWireNativeMethods
    {
        private const string Library =
            "libpipewire-0.3.so.0";

        //
        // Initialization
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_init(
            IntPtr argc,
            IntPtr argv);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_deinit();



        //
        // Main loop
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_main_loop_new(
            IntPtr props);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_main_loop_get_loop(
            IntPtr mainLoop);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_loop_iterate(
            IntPtr loop,
            int timeout);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_main_loop_destroy(
            IntPtr mainLoop);



        //
        // Context
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_context_new(
            IntPtr loop,
            IntPtr properties,
            int userDataSize);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_context_destroy(
            IntPtr context);



        //
        // Core
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_context_connect(
            IntPtr context,
            IntPtr properties,
            int userDataSize);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_core_disconnect(
            IntPtr core);

        [UnmanagedFunctionPointer(
            CallingConvention.Cdecl)]
        internal delegate void PwProcessCallback(
            IntPtr userData);

        //
        // Stream
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_new_simple(
            IntPtr loop,
            string name,
            IntPtr properties,
            ref PwStreamEvents events,
            IntPtr userData);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_connect(
            IntPtr stream,
            PwDirection direction,
            uint targetId,
            PwStreamFlags flags,
            IntPtr[] parameters,
            uint nParams);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_stream_destroy(
            IntPtr stream);



        //
        // Buffer handling
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_dequeue_buffer(
            IntPtr stream);



        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_queue_buffer(
            IntPtr stream,
            IntPtr buffer);



        //
        // Stream state
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_get_state(
            IntPtr stream,
            out IntPtr error);



        //
        // Helpers
        //

        [DllImport(
            Library,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_properties_new(
            string key,
            string value,
            IntPtr end);

        internal enum PwDirection
        {
            Output = 0,
            Input = 1
        }
    }

    [Flags]
    internal enum PwStreamFlags
    {
        None = 0,

        Autoconnect = 1 << 0,

        MapBuffers = 1 << 1,

        Driver = 1 << 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PwStreamEvents
    {
        public uint Version;

        public IntPtr Destroy;

        public IntPtr StateChanged;

        public IntPtr ControlInfo;

        public IntPtr IoChanged;

        public IntPtr ParamChanged;

        public IntPtr AddBuffer;

        public IntPtr RemoveBuffer;

        public IntPtr Process;

        public IntPtr Drained;

        public IntPtr Command;

        public IntPtr TriggerDone;
    }


}
