using System.Runtime.InteropServices;

namespace AIRadio.Server.Services.Audio
{
    internal static class PipeWireNativeMethods
    {
        private const string Library = "libpipewire-0.3.so.0";

        internal const uint PwIdAny = uint.MaxValue;
        internal const int SpaTypeObject = 15;
        internal const int SpaTypeObjectFormat = 0x40002;
        internal const int SpaParamEnumFormat = 3;
        internal const int SpaMediaTypeAudio = 1;
        internal const int SpaMediaSubtypeRaw = 1;
        internal const int SpaAudioFormatS16 = 0x103;
        internal const int SpaFormatMediaType = 1;
        internal const int SpaFormatMediaSubtype = 2;
        internal const int SpaFormatAudioFormat = 0x10001;
        internal const int SpaFormatAudioRate = 0x10003;
        internal const int SpaFormatAudioChannels = 0x10004;
        internal const int SpaPropVolume = 0x10003;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_init(IntPtr argc, IntPtr argv);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_deinit();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_main_loop_new(IntPtr props);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_main_loop_get_loop(IntPtr mainLoop);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_main_loop_run(IntPtr mainLoop);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_main_loop_quit(IntPtr mainLoop);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_main_loop_destroy(IntPtr mainLoop);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_context_new(IntPtr loop, IntPtr properties, int userDataSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_context_destroy(IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_context_connect(IntPtr context, IntPtr properties, int userDataSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_core_disconnect(IntPtr core);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PwProcessCallback(IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PwDrainedCallback(IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PwStateChangedCallback(
            IntPtr userData,
            int oldState,
            int state,
            IntPtr error);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_new_simple(
            IntPtr loop,
            string name,
            IntPtr properties,
            ref PwStreamEvents events,
            IntPtr userData);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_connect(
            IntPtr stream,
            PwDirection direction,
            uint targetId,
            PwStreamFlags flags,
            IntPtr[] parameters,
            uint nParams);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_stream_destroy(IntPtr stream);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_return_buffer(IntPtr stream, IntPtr buffer);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern int pw_stream_flush(IntPtr stream, [MarshalAs(UnmanagedType.I1)] bool drain);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_stream_set_control(
            IntPtr stream,
            uint id,
            uint nValues,
            [In] float[] values,
            IntPtr terminator);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_get_control(IntPtr stream, uint id);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_stream_get_state(IntPtr stream, out IntPtr error);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pw_properties_new(string key, string value, IntPtr end);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pw_properties_set(IntPtr properties, string key, string value);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pw_properties_free(IntPtr properties);

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
        MapBuffers = 1 << 2,
        Driver = 1 << 3,
        RtProcess = 1 << 4
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct PwBuffer
    {
        public IntPtr Buffer;
        public IntPtr UserData;
        public ulong Size;
        public ulong Requested;
        public ulong Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaBuffer
    {
        public uint MetaCount;
        public uint DataCount;
        public IntPtr Metas;
        public IntPtr Datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public IntPtr Data;
        public IntPtr Chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }
}
