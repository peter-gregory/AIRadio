using System.Runtime.InteropServices;

namespace AIRadio.Server.Services.Audio;

internal static class PipeWireNativeMethods
{
    private const string Library = "libairadio-pipewire.so";

    [StructLayout(LayoutKind.Sequential)]
    internal struct AIRadioPcmSegment
    {
        public IntPtr Data;
        public nuint Size;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PlaybackCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ErrorCallback(
        IntPtr userData,
        int errorCode,
        IntPtr message);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr airadio_pw_create(
        uint sampleRate,
        uint channels,
        uint bitsPerSample);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airadio_pw_start(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airadio_pw_enqueue(
        IntPtr client,
        IntPtr segments,
        nuint segmentCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airadio_pw_clear(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airadio_pw_set_volume(
        IntPtr client,
        float volume);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float airadio_pw_get_volume(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong airadio_pw_queued_frames(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong airadio_pw_outstanding_frames(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void airadio_pw_set_playback_complete_callback(
        IntPtr client,
        PlaybackCallback? callback,
        IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void airadio_pw_set_error_callback(
        IntPtr client,
        ErrorCallback? callback,
        IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr airadio_pw_last_error(IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void airadio_pw_destroy(IntPtr client);
}
