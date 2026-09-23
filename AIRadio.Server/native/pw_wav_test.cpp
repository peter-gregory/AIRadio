#include <pipewire/pipewire.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/param/audio/format-utils.h>
#include <spa/pod/builder.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cerrno>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {

#pragma pack(push, 1)
struct RiffHeader {
    char id[4];
    uint32_t size;
    char format[4];
};

struct ChunkHeader {
    char id[4];
    uint32_t size;
};

struct WaveFormat {
    uint16_t audio_format;
    uint16_t channels;
    uint32_t sample_rate;
    uint32_t byte_rate;
    uint16_t block_align;
    uint16_t bits_per_sample;
};
#pragma pack(pop)

struct WavFile {
    uint16_t channels = 0;
    uint32_t sample_rate = 0;
    uint16_t bits_per_sample = 0;
    std::vector<uint8_t> pcm;
};

struct Player {
    pw_thread_loop *loop = nullptr;
    pw_stream *stream = nullptr;
    WavFile wav;

    size_t offset = 0;
    uint64_t buffers_queued = 0;
    uint64_t frames_queued = 0;

    bool connected = false;
    bool failed = false;
    bool final_buffer_queued = false;
    bool drained = false;
    int connection_error = 0;

    bool debug = true;
    uint32_t latency_ms = 100;
    bool latency_explicit = false;
};

static bool read_wav(const char *filename, WavFile &wav)
{
    std::ifstream file(filename, std::ios::binary);
    if (!file) {
        std::cerr << "ERROR: unable to open " << filename << "\n";
        return false;
    }

    RiffHeader riff{};
    file.read(reinterpret_cast<char *>(&riff), sizeof(riff));

    if (!file ||
        std::memcmp(riff.id, "RIFF", 4) != 0 ||
        std::memcmp(riff.format, "WAVE", 4) != 0) {
        std::cerr << "ERROR: not a RIFF/WAVE file\n";
        return false;
    }

    WaveFormat fmt{};
    bool have_fmt = false;
    std::vector<uint8_t> data;

    while (file) {
        ChunkHeader chunk{};
        file.read(reinterpret_cast<char *>(&chunk), sizeof(chunk));
        if (!file)
            break;

        const uint32_t padded = chunk.size + (chunk.size & 1u);

        if (std::memcmp(chunk.id, "fmt ", 4) == 0) {
            if (chunk.size < sizeof(WaveFormat)) {
                std::cerr << "ERROR: invalid fmt chunk\n";
                return false;
            }

            file.read(reinterpret_cast<char *>(&fmt), sizeof(fmt));
            if (!file)
                return false;

            if (chunk.size > sizeof(WaveFormat))
                file.seekg(chunk.size - sizeof(WaveFormat), std::ios::cur);

            have_fmt = true;
        }
        else if (std::memcmp(chunk.id, "data", 4) == 0) {
            data.resize(chunk.size);
            file.read(reinterpret_cast<char *>(data.data()), chunk.size);
            if (!file)
                return false;
            break;
        }
        else {
            file.seekg(padded, std::ios::cur);
        }
    }

    if (!have_fmt || data.empty()) {
        std::cerr << "ERROR: WAV has no usable fmt/data chunk\n";
        return false;
    }

    if (fmt.audio_format != 1) {
        std::cerr << "ERROR: only PCM WAV is supported; format="
                  << fmt.audio_format << "\n";
        return false;
    }

    if (fmt.bits_per_sample != 16) {
        std::cerr << "ERROR: only 16-bit PCM is supported; bits="
                  << fmt.bits_per_sample << "\n";
        return false;
    }

    if (!fmt.channels || !fmt.sample_rate) {
        std::cerr << "ERROR: invalid WAV format\n";
        return false;
    }

    const uint32_t bytes_per_frame =
        static_cast<uint32_t>(fmt.channels) * 2;

    if (fmt.block_align != bytes_per_frame) {
        std::cerr << "ERROR: unexpected WAV block alignment\n";
        return false;
    }

    if (data.size() % bytes_per_frame != 0) {
        std::cerr << "WARNING: data size is not an integral number of frames\n";
    }

    wav.channels = fmt.channels;
    wav.sample_rate = fmt.sample_rate;
    wav.bits_per_sample = fmt.bits_per_sample;
    wav.pcm = std::move(data);
    return true;
}

static const char *format_name(uint32_t format)
{
    switch (format) {
    case SPA_AUDIO_FORMAT_S8:   return "S8";
    case SPA_AUDIO_FORMAT_U8:   return "U8";
    case SPA_AUDIO_FORMAT_S16:  return "S16";
    case SPA_AUDIO_FORMAT_U16:  return "U16";
    case SPA_AUDIO_FORMAT_S24:  return "S24";
    case SPA_AUDIO_FORMAT_U24:  return "U24";
    case SPA_AUDIO_FORMAT_S32:  return "S32";
    case SPA_AUDIO_FORMAT_U32:  return "U32";
    case SPA_AUDIO_FORMAT_S24_32:return "S24_32";
    case SPA_AUDIO_FORMAT_U24_32:return "U24_32";
    case SPA_AUDIO_FORMAT_S20:  return "S20";
    case SPA_AUDIO_FORMAT_U20:  return "U20";
    case SPA_AUDIO_FORMAT_S18:  return "S18";
    case SPA_AUDIO_FORMAT_U18:  return "U18";
    case SPA_AUDIO_FORMAT_F32:  return "F32";
    case SPA_AUDIO_FORMAT_F64:  return "F64";
    default: return "unknown";
    }
}

static void state_changed(
    void *userdata,
    pw_stream_state old_state,
    pw_stream_state state,
    const char *error)
{
    auto *p = static_cast<Player *>(userdata);

    std::cerr << "[TEST] STATE "
              << pw_stream_state_as_string(old_state)
              << " -> "
              << pw_stream_state_as_string(state);

    if (error)
        std::cerr << " error=" << error;

    std::cerr << "\n";

    if (state == PW_STREAM_STATE_PAUSED ||
        state == PW_STREAM_STATE_STREAMING) {
        p->connected = true;
        p->connection_error = 0;
        pw_thread_loop_signal(p->loop, false);
    }
    else if (state == PW_STREAM_STATE_ERROR) {
        p->failed = true;
        p->connection_error = -5;
        pw_thread_loop_signal(p->loop, false);
    }
}

static void param_changed(
    void *userdata,
    uint32_t id,
    const spa_pod *param)
{
    auto *p = static_cast<Player *>(userdata);

    if (!param) {
        std::cerr << "[TEST] PARAM id=" << id << " param=NULL\n";
        return;
    }

    if (id != SPA_PARAM_Format) {
        std::cerr << "[TEST] PARAM id=" << id << "\n";
        return;
    }

    spa_audio_info_raw info{};
    const int r = spa_format_audio_raw_parse(param, &info);

    if (r < 0) {
        std::cerr << "[TEST] FORMAT parse failed: " << r << "\n";
        return;
    }

    std::cerr << "[TEST] NEGOTIATED FORMAT"
              << " format=" << format_name(info.format)
              << " rate=" << info.rate
              << " channels=" << info.channels
              << "\n";

    std::cerr << "[TEST] REQUESTED FORMAT"
              << " format=S16"
              << " rate=" << p->wav.sample_rate
              << " channels=" << p->wav.channels
              << "\n";

    if (info.format != SPA_AUDIO_FORMAT_S16 ||
        info.rate != p->wav.sample_rate ||
        info.channels != p->wav.channels) {
        std::cerr
            << "[TEST] FORMAT WARNING: PipeWire changed the requested "
            << "sample format/rate/channel count. This test is useful for "
            << "checking whether conversion is occurring.\n";
    }
}

static void process(void *userdata)
{
    auto *p = static_cast<Player *>(userdata);

    pw_buffer *buffer = pw_stream_dequeue_buffer(p->stream);

    if (!buffer || !buffer->buffer) {
        std::cerr << "[TEST] PROCESS ERROR: no buffer returned\n";
        p->failed = true;
        pw_thread_loop_signal(p->loop, false);
        return;
    }

    if (!buffer->buffer->n_datas || !buffer->buffer->datas) {
        std::cerr << "[TEST] PROCESS ERROR: no spa data\n";
        p->failed = true;
        pw_stream_return_buffer(p->stream, buffer);
        pw_thread_loop_signal(p->loop, false);
        return;
    }

    spa_data *d = &buffer->buffer->datas[0];

    if (!d->data || !d->chunk) {
        std::cerr << "[TEST] PROCESS ERROR: invalid spa data/chunk\n";
        p->failed = true;
        pw_stream_return_buffer(p->stream, buffer);
        pw_thread_loop_signal(p->loop, false);
        return;
    }

    const size_t bytes_per_frame =
        static_cast<size_t>(p->wav.channels) * 2;

    const size_t remaining = p->wav.pcm.size() - p->offset;
    size_t bytes = std::min(remaining, static_cast<size_t>(d->maxsize));
    bytes -= bytes % bytes_per_frame;

    const uint64_t max_frames = d->maxsize / bytes_per_frame;
    const double max_ms = p->wav.sample_rate
        ? (1000.0 * static_cast<double>(max_frames) / p->wav.sample_rate)
        : 0.0;
    const double requested_ms = p->wav.sample_rate
        ? (1000.0 * static_cast<double>(buffer->requested) / p->wav.sample_rate)
        : 0.0;

    std::cerr
        << "[TEST] BUFFER #" << p->buffers_queued
        << " ptr=" << static_cast<void *>(buffer)
        << " maxsize=" << d->maxsize << " bytes"
        << " max_frames=" << max_frames
        << " max_ms=" << max_ms
        << " requested=" << buffer->requested << " frames"
        << " requested_ms=" << requested_ms
        << " offset=" << d->chunk->offset
        << " old_size=" << d->chunk->size
        << " stride=" << d->chunk->stride
        << " flags=0x" << std::hex << d->flags << std::dec
        << " wav_remaining=" << remaining
        << " submit_bytes=" << bytes
        << "\n";

    if (bytes == 0) {
        std::cerr << "[TEST] No more WAV data; returning empty buffer\n";
        d->chunk->offset = 0;
        d->chunk->size = 0;
        d->chunk->stride = static_cast<int32_t>(bytes_per_frame);
        pw_stream_queue_buffer(p->stream, buffer);
        return;
    }

    std::memcpy(d->data, p->wav.pcm.data() + p->offset, bytes);

    d->chunk->offset = 0;
    d->chunk->size = static_cast<uint32_t>(bytes);
    d->chunk->stride = static_cast<int32_t>(bytes_per_frame);

    p->offset += bytes;
    p->frames_queued += bytes / bytes_per_frame;
    ++p->buffers_queued;

    const bool final_buffer = p->offset >= p->wav.pcm.size();

    std::cerr
        << "[TEST] QUEUE buffer #" << (p->buffers_queued - 1)
        << " bytes=" << d->chunk->size
        << " frames=" << (bytes / bytes_per_frame)
        << " final=" << (final_buffer ? "yes" : "no")
        << "\n";

    const int r = pw_stream_queue_buffer(p->stream, buffer);

    if (r < 0) {
        std::cerr << "[TEST] QUEUE ERROR: " << r << "\n";
        p->failed = true;
        pw_thread_loop_signal(p->loop, false);
        return;
    }

    if (final_buffer && !p->final_buffer_queued) {
        p->final_buffer_queued = true;

        /*
         * Ask PipeWire to drain the stream. With drain=true the drained
         * callback is delivered after queued playback has completed.
         */
        const int fr = pw_stream_flush(p->stream, true);
        std::cerr << "[TEST] FINAL BUFFER QUEUED; flush(drain=true) => "
                  << fr << "\n";

        if (fr < 0) {
            p->failed = true;
            pw_thread_loop_signal(p->loop, false);
        }
    }
}

static void drained(void *userdata)
{
    auto *p = static_cast<Player *>(userdata);

    std::cerr << "[TEST] DRAINED: all queued audio has completed\n";
    p->drained = true;
    pw_thread_loop_signal(p->loop, false);
}

static const pw_stream_events events = {
    .version = PW_VERSION_STREAM_EVENTS,
    nullptr,
    &state_changed,
    nullptr,
    nullptr,
    &param_changed,
    nullptr,
    nullptr,
    &process,
    &drained,
    nullptr,
    nullptr
};

} // namespace

static bool parse_latency_ms(const char *value, uint32_t &latency_ms)
{
    if (!value || !*value)
        return false;

    char *end = nullptr;
    errno = 0;
    const unsigned long value_ms = std::strtoul(value, &end, 10);
    if (errno != 0 || end == value || value_ms == 0 || value_ms > 600000)
        return false;

    if (*end == 'm' && end[1] == 's' && end[2] == '\0') {
        latency_ms = static_cast<uint32_t>(value_ms);
        return true;
    }

    if (*end == '\0') {
        latency_ms = static_cast<uint32_t>(value_ms);
        return true;
    }

    return false;
}

static void print_usage(const char *program)
{
    std::cerr
        << "Usage: " << program << " [--latency=MS] <file.wav>\n"
        << "       " << program << " [--latency MS] <file.wav>\n"
        << "  --latency=MS   Requested PipeWire node latency (default: 100ms)\n";
}

} // namespace

int main(int argc, char **argv)
{
    Player player;
    const char *filename = nullptr;

    for (int i = 1; i < argc; ++i) {
        const std::string arg(argv[i]);
        if (arg == "--latency") {
            if (i + 1 >= argc || !parse_latency_ms(argv[++i], player.latency_ms)) {
                print_usage(argv[0]);
                return 2;
            }
            player.latency_explicit = true;
        }
        else if (arg.rfind("--latency=", 0) == 0) {
            if (!parse_latency_ms(arg.c_str() + 10, player.latency_ms)) {
                print_usage(argv[0]);
                return 2;
            }
            player.latency_explicit = true;
        }
        else if (!filename) {
            filename = argv[i];
        }
        else {
            print_usage(argv[0]);
            return 2;
        }
    }

    if (!filename) {
        print_usage(argv[0]);
        return 2;
    }

    if (!read_wav(filename, player.wav))
        return 1;

    const size_t bytes_per_frame =
        static_cast<size_t>(player.wav.channels) * 2;

    const uint64_t total_frames =
        player.wav.pcm.size() / bytes_per_frame;

    const double seconds =
        static_cast<double>(total_frames) / player.wav.sample_rate;

    std::cerr
        << "[TEST] WAV"
        << " rate=" << player.wav.sample_rate
        << " channels=" << player.wav.channels
        << " bits=" << player.wav.bits_per_sample
        << " bytes=" << player.wav.pcm.size()
        << " frames=" << total_frames
        << " duration=" << seconds << " sec\n";

    pw_init(nullptr, nullptr);

    player.loop = pw_thread_loop_new("AIRadioWavTest", nullptr);
    if (!player.loop) {
        std::cerr << "ERROR: pw_thread_loop_new failed\n";
        pw_deinit();
        return 1;
    }

    pw_thread_loop_lock(player.loop);

    if (pw_thread_loop_start(player.loop) < 0) {
        std::cerr << "ERROR: pw_thread_loop_start failed\n";
        pw_thread_loop_unlock(player.loop);
        pw_thread_loop_destroy(player.loop);
        pw_deinit();
        return 1;
    }

    const uint64_t latency_frames =
        (static_cast<uint64_t>(player.wav.sample_rate) * player.latency_ms + 999) / 1000;
    const std::string latency =
        std::to_string(latency_frames) + "/" + std::to_string(player.wav.sample_rate);

    std::cerr
        << "[TEST] REQUEST LATENCY "
        << player.latency_ms << " ms"
        << " (" << latency_frames << " frames/" << player.wav.sample_rate << ")\n";

    pw_properties *props = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Audio",
        PW_KEY_MEDIA_CATEGORY, "Playback",
        PW_KEY_MEDIA_ROLE, "Music",
        PW_KEY_NODE_STREAM, "true",
        PW_KEY_NODE_LATENCY, latency.c_str(),
        nullptr);

    player.stream = pw_stream_new_simple(
        pw_thread_loop_get_loop(player.loop),
        "AIRadioWavTest",
        props,
        &events,
        &player);

    if (!player.stream) {
        std::cerr << "ERROR: pw_stream_new_simple failed\n";
        pw_thread_loop_unlock(player.loop);
        pw_thread_loop_stop(player.loop);
        pw_thread_loop_destroy(player.loop);
        pw_deinit();
        return 1;
    }

    uint8_t pod_buffer[1024];
    spa_pod_builder builder =
        SPA_POD_BUILDER_INIT(pod_buffer, sizeof(pod_buffer));

    spa_audio_info_raw info = SPA_AUDIO_INFO_RAW_INIT(
        .format = SPA_AUDIO_FORMAT_S16,
        .rate = player.wav.sample_rate,
        .channels = player.wav.channels);

    const spa_pod *params[1] = {
        spa_format_audio_raw_build(
            &builder,
            SPA_PARAM_EnumFormat,
            &info)
    };

    if (!params[0]) {
        std::cerr << "ERROR: unable to build audio format pod\n";
        pw_stream_destroy(player.stream);
        player.stream = nullptr;
        pw_thread_loop_unlock(player.loop);
        pw_thread_loop_stop(player.loop);
        pw_thread_loop_destroy(player.loop);
        pw_deinit();
        return 1;
    }

    const int r = pw_stream_connect(
        player.stream,
        PW_DIRECTION_OUTPUT,
        PW_ID_ANY,
        static_cast<pw_stream_flags>(
            PW_STREAM_FLAG_AUTOCONNECT |
            PW_STREAM_FLAG_MAP_BUFFERS |
            PW_STREAM_FLAG_INACTIVE),
        params,
        1);

    if (r < 0) {
        std::cerr << "ERROR: pw_stream_connect failed: " << r << "\n";
        pw_stream_destroy(player.stream);
        player.stream = nullptr;
        pw_thread_loop_unlock(player.loop);
        pw_thread_loop_stop(player.loop);
        pw_thread_loop_destroy(player.loop);
        pw_deinit();
        return 1;
    }

    while (!player.connected && !player.failed)
        pw_thread_loop_wait(player.loop);

    if (player.failed) {
        std::cerr << "ERROR: PipeWire connection failed\n";
        pw_thread_loop_unlock(player.loop);
        pw_stream_destroy(player.stream);
        player.stream = nullptr;
        pw_thread_loop_stop(player.loop);
        pw_thread_loop_destroy(player.loop);
        pw_deinit();
        return 1;
    }

    std::cerr << "[TEST] Starting playback\n";
    const int ar = pw_stream_set_active(player.stream, true);

    if (ar < 0) {
        std::cerr << "ERROR: pw_stream_set_active failed: " << ar << "\n";
        player.failed = true;
    }

    while (!player.failed && !player.drained)
        pw_thread_loop_wait(player.loop);

    std::cerr
        << "[TEST] RESULT "
        << (player.failed ? "FAILED" : "OK")
        << " buffers=" << player.buffers_queued
        << " frames=" << player.frames_queued
        << " expected_frames=" << total_frames
        << " bytes_read=" << player.offset
        << "/" << player.wav.pcm.size()
        << "\n";

    pw_stream_set_active(player.stream, false);
    pw_stream_destroy(player.stream);
    player.stream = nullptr;

    pw_thread_loop_unlock(player.loop);
    pw_thread_loop_stop(player.loop);
    pw_thread_loop_destroy(player.loop);
    player.loop = nullptr;

    pw_deinit();

    return player.failed ? 1 : 0;
}
