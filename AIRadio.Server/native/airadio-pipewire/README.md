# AIRadio native PipeWire backend

The native backend owns the PipeWire stream and the playback PCM queue. C# only supplies borrowed PCM buffers through one P/Invoke call per batch.

## Native responsibilities

- PipeWire initialization and stream creation.
- SPA format construction using PipeWire's `spa_format_audio_raw_build()`.
- 48 kHz/S16 playback format negotiated from the C# configuration.
- Native expanding PCM ring buffer.
- Native PipeWire process callback.
- Volume control.
- Drain/clear handling.
- Playback-complete notification on a worker thread, never from the PipeWire process callback.

The C# buffers are borrowed only for the duration of `airadio_pw_enqueue()`. The native library copies the PCM into its own ring buffer before returning.

## Build on the Orange Pi

Install the development package:

```bash
sudo apt install -y build-essential cmake pkg-config libpipewire-0.3-dev
```

From this directory:

```bash
chmod +x build-linux-arm64.sh
./build-linux-arm64.sh
```

The resulting library is:

```text
build/libairadio-pipewire.so
```

For the current deployment layout, copy it into the server project as:

```bash
mkdir -p ../native/linux-arm64
cp build/libairadio-pipewire.so ../native/linux-arm64/
```

Then the Linux ARM64 .NET publish copies the library beside `AIRadio.Server`.

## Verify the native library

On the Orange Pi:

```bash
file ../native/linux-arm64/libairadio-pipewire.so
ldd ../native/linux-arm64/libairadio-pipewire.so
```

The library should be ARM64 and should resolve `libpipewire-0.3.so.0`.

## Design

The native queue is a byte-oriented ring buffer. It starts at 256 KiB and doubles when additional capacity is required. Consumed space is immediately reusable.

PipeWire's requested buffer size is independent of the application queue. The stream requests approximately 100 ms of latency through `node.latency`; the final partial PCM buffer is zero padded before it is submitted.

On cancellation, the native ring is cleared and PipeWire is drained. The completion callback is raised after the already-submitted PipeWire audio has drained.
