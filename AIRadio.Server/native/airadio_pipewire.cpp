#include "airadio_pipewire.h"
#include <pipewire/pipewire.h>
#include <spa/buffer/buffer.h>
#include <spa/param/param.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/param/audio/raw-types.h>
#include <spa/param/buffers.h>
#include <spa/param/props.h>
#include <spa/pod/builder.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <condition_variable>
#include <string>
#include <thread>
#include <vector>
#include <unordered_map>
#include <array>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <semaphore>

namespace {
constexpr size_t kPodBufferBytes = 1024;
constexpr size_t kMinPipeWireBuffers = 2;
constexpr size_t kMaxPipeWireBuffers = 4;

// AIRadio audio is standardized as 16 kHz, 16-bit, mono PCM.
// PipeWire buffer size is negotiated and then used as the AIRadio ring block size.
// Prefer about 400 ms while keeping the negotiated range below 500 ms so
// two active buffers remain under one second for responsive cancellation.
constexpr uint32_t kAIRadioSampleRate = 16000;
constexpr uint32_t kAIRadioChannels = 1;
constexpr uint32_t kAIRadioBits = 16;
constexpr size_t kPreferredBufferMilliseconds = 400;
constexpr size_t kMinimumBufferMilliseconds = 200;
constexpr size_t kMaximumBufferMilliseconds = 499;
constexpr size_t kDataBlocks =
    (300 * 1000) / kMinimumBufferMilliseconds;
constexpr size_t kReserveBlocks = 2;
constexpr size_t kRingBlocks = kDataBlocks + kReserveBlocks;

struct PcmBlock {
    std::unique_ptr<uint8_t[]> data;
};


class ManualResetEvent {
public:
    explicit ManualResetEvent(bool set = false)
        : state_(set) {}

    void set() noexcept {
        state_.store(true, std::memory_order_release);
        state_.notify_one();
    }

    void reset() noexcept {
        state_.store(false, std::memory_order_release);
    }

    void wait() const noexcept {
        bool expected = false;
        while (!state_.load(std::memory_order_acquire)) {
            state_.wait(expected, std::memory_order_relaxed);
        }
    }

private:
    std::atomic<bool> state_;
};

class AutoResetEvent {
public:
    explicit AutoResetEvent(bool set = false) : semaphore_(set ? 1 : 0) {}
    void set() noexcept {
        semaphore_.try_acquire();
        semaphore_.release();
    }
    void wait() noexcept { semaphore_.acquire(); }
private:
    std::binary_semaphore semaphore_;
};
class PipeWireBackend {
public:
    PipeWireBackend(uint32_t rate, uint32_t channels, uint32_t bits)
        : sample_rate_(rate), channels_(channels), bits_(bits),
          bytes_per_frame_(channels * (bits / 8)),
          debug_enabled_(std::getenv("AIRADIO_PIPEWIRE_DEBUG") != nullptr) {}

    ~PipeWireBackend() { destroy(); }

    int start() {
        if (started_) return 0;
        if (sample_rate_ != 16000 || channels_ != 1 || bits_ != 16 || !bytes_per_frame_) {
            last_error_ = "AIRadio PipeWire audio must be 16 kHz, 16-bit, mono";
            return -22;
        }

        fifo_capacity_frames_ = static_cast<size_t>(sample_rate_) * 60;
        target_frames_ = static_cast<uint64_t>(sample_rate_) * 2;
        try { fifo_.resize(fifo_capacity_frames_ * bytes_per_frame_); }
        catch (...) { last_error_ = "Unable to allocate AIRadio PCM FIFO"; return -12; }

        debug("START %u Hz %u ch %u-bit; FIFO=%zu frames (%.1fs), PipeWire target=%llu frames (%.1fs)",
              sample_rate_, channels_, bits_, fifo_capacity_frames_,
              1.0 * fifo_capacity_frames_ / sample_rate_,
              static_cast<unsigned long long>(target_frames_),
              1.0 * target_frames_ / sample_rate_);

        {
            std::lock_guard<std::mutex> lock(fifo_mutex_);
            read_frame_ = write_frame_ = 0;
        }
        queued_frames_.store(0);
        shutting_down_.store(false);
        stop_worker_.store(false);
        stop_completion_.store(false);
        end_of_utterance_.store(false);
        cancelled_.store(false);
        worker_requested_.store(false);
        completion_pending_.store(false);
        connection_ready_ = false;
        connection_error_ = 0;

        pw_init(nullptr, nullptr);
        loop_ = pw_thread_loop_new("AIRadioPipeWire", nullptr);
        if (!loop_) return -12;

        pw_thread_loop_lock(loop_);
        const int lr = pw_thread_loop_start(loop_);
        if (lr < 0) {
            pw_thread_loop_unlock(loop_);
            return lr;
        }

        const uint32_t preferred = sample_rate_ * 400 / 1000;
        const std::string latency =
            std::to_string(preferred) + "/" + std::to_string(sample_rate_);

        pw_properties *props = pw_properties_new(
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Playback",
            PW_KEY_MEDIA_ROLE, "Music",
            PW_KEY_NODE_LATENCY, latency.c_str(),
            PW_KEY_NODE_MAX_LATENCY, latency.c_str(),
            PW_KEY_NODE_STREAM, "true", nullptr);
        if (!props) {
            pw_thread_loop_unlock(loop_);
            return -12;
        }

        static const pw_stream_events events = {
            PW_VERSION_STREAM_EVENTS,
            nullptr,
            &PipeWireBackend::on_state_changed,
            nullptr, nullptr,
            &PipeWireBackend::on_param_changed,
            &PipeWireBackend::on_add_buffer,
            nullptr,
            &PipeWireBackend::on_process,
            &PipeWireBackend::on_drained,
            nullptr, nullptr
        };

        stream_ = pw_stream_new_simple(
            pw_thread_loop_get_loop(loop_), "AIRadioAudioOutput",
            props, &events, this);
        if (!stream_) {
            pw_thread_loop_unlock(loop_);
            return -12;
        }

        uint8_t pod[1024];
        spa_pod_builder builder = SPA_POD_BUILDER_INIT(pod, sizeof(pod));
        spa_audio_info_raw info = SPA_AUDIO_INFO_RAW_INIT(
            .format = SPA_AUDIO_FORMAT_S16,
            .rate = sample_rate_, .channels = channels_);
        const spa_pod *params[1] = {
            spa_format_audio_raw_build(&builder, SPA_PARAM_EnumFormat, &info)
        };
        if (!params[0]) {
            pw_thread_loop_unlock(loop_);
            return -22;
        }

        const auto flags = static_cast<pw_stream_flags>(
            PW_STREAM_FLAG_AUTOCONNECT |
            PW_STREAM_FLAG_MAP_BUFFERS |
            PW_STREAM_FLAG_INACTIVE |
            PW_STREAM_FLAG_ASYNC |
            PW_STREAM_FLAG_EARLY_PROCESS);

        const int r = pw_stream_connect(
            stream_, PW_DIRECTION_OUTPUT, PW_ID_ANY, flags, params, 1);
        if (r < 0) {
            last_error_ = "pw_stream_connect failed: " + std::to_string(r);
            pw_thread_loop_unlock(loop_);
            return r;
        }

        while (!connection_ready_ && !connection_error_)
            pw_thread_loop_wait(loop_);

        if (connection_error_ < 0) {
            int e = connection_error_;
            pw_thread_loop_unlock(loop_);
            return e;
        }

        pw_thread_loop_unlock(loop_);

        pipewire_thread_ = std::thread([this] { pipewire_worker(); });
        completion_thread_ = std::thread([this] { completion_worker(); });
        started_ = true;
        return 0;
    }

    int enqueue(const AIRadioPcmSegment *segments, size_t count) {
        if (!started_ || !stream_ || !loop_) return -107;
        if (count && !segments) return -22;

        for (size_t i = 0; i < count; ++i) {
            if (!segments[i].size) continue;
            if (!segments[i].data || segments[i].size % bytes_per_frame_)
                return -22;

            const uint8_t *src = segments[i].data;
            size_t left = segments[i].size;

            while (left) {
                std::unique_lock<std::mutex> lock(fifo_mutex_);
                fifo_space_cv_.wait(lock, [this] {
                    return shutting_down_.load() ||
                           end_of_utterance_.load() ||
                           cancelled_.load() ||
                           fifo_available_locked() < fifo_capacity_frames_;
                });

                if (shutting_down_.load() || end_of_utterance_.load() ||
                    cancelled_.load())
                    return -16;

                const size_t free_frames =
                    fifo_capacity_frames_ - fifo_available_locked();
                const size_t frames =
                    std::min(free_frames, left / bytes_per_frame_);
                if (!frames) continue;

                write_fifo_locked(src, frames);
                src += frames * bytes_per_frame_;
                left -= frames * bytes_per_frame_;
                lock.unlock();
                request_worker();
            }
        }
        return 0;
    }

    int end_utterance(bool cancel) {
        if (!started_ || !stream_ || !loop_) return -107;
        {
            std::lock_guard<std::mutex> lock(fifo_mutex_);
            end_of_utterance_.store(true);
            cancelled_.store(cancel);
            if (cancel) read_frame_ = write_frame_;
        }
        fifo_space_cv_.notify_all();
        request_worker();
        return 0;
    }

    int clear() { return end_utterance(true); }

    int set_volume(float volume) {
        if (!started_ || !stream_ || !loop_) return -107;
        volume = std::clamp(volume, 0.0f, 1.0f);
        pw_thread_loop_lock(loop_);
        const int r = pw_stream_set_control(
            stream_, SPA_PROP_volume, 1, &volume, nullptr);
        if (r >= 0) volume_.store(volume);
        else last_error_ = "pw_stream_set_control(volume) failed: " + std::to_string(r);
        pw_thread_loop_unlock(loop_);
        return r;
    }

    uint64_t queued_frames() const noexcept {
        std::lock_guard<std::mutex> lock(fifo_mutex_);
        return fifo_available_locked();
    }

    uint64_t outstanding_frames() const noexcept {
        return queued_frames_.load() + queued_frames();
    }

    float volume() const noexcept { return volume_.load(); }

    void set_playback_callback(AIRadioPlaybackCallback cb, void *ud) {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        playback_callback_ = cb; playback_user_data_ = ud;
    }

    void set_error_callback(AIRadioErrorCallback cb, void *ud) {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        error_callback_ = cb; error_user_data_ = ud;
    }

    const char *last_error() const noexcept { return last_error_.c_str(); }

    void destroy() {
        shutting_down_.store(true);
        stop_worker_.store(true);
        stop_completion_.store(true);
        worker_requested_.store(true);
        completion_pending_.store(true);
        fifo_space_cv_.notify_all();
        worker_cv_.notify_all();
        completion_cv_.notify_all();

        if (pipewire_thread_.joinable()) pipewire_thread_.join();
        if (completion_thread_.joinable()) completion_thread_.join();

        if (loop_) {
            pw_thread_loop_lock(loop_);
            if (stream_) {
                pw_stream_set_active(stream_, false);
                pw_stream_destroy(stream_);
                stream_ = nullptr;
            }
            queued_buffers_.clear();
            pw_thread_loop_unlock(loop_);
            pw_thread_loop_stop(loop_);
            pw_thread_loop_destroy(loop_);
            loop_ = nullptr;
        }

        fifo_.clear();
        started_ = false;
        pw_deinit();
    }

private:
    size_t fifo_available_locked() const noexcept {
        return static_cast<size_t>(write_frame_ - read_frame_);
    }

    void write_fifo_locked(const uint8_t *src, size_t frames) {
        const size_t pos = static_cast<size_t>(write_frame_ % fifo_capacity_frames_);
        const size_t first = std::min(frames, fifo_capacity_frames_ - pos);
        std::memcpy(fifo_.data() + pos * bytes_per_frame_,
                    src, first * bytes_per_frame_);
        if (first < frames)
            std::memcpy(fifo_.data(), src + first * bytes_per_frame_,
                        (frames - first) * bytes_per_frame_);
        write_frame_ += frames;
    }

    size_t read_fifo_locked(uint8_t *dst, size_t frames) {
        frames = std::min(frames, fifo_available_locked());
        if (!frames) return 0;
        const size_t pos = static_cast<size_t>(read_frame_ % fifo_capacity_frames_);
        const size_t first = std::min(frames, fifo_capacity_frames_ - pos);
        std::memcpy(dst, fifo_.data() + pos * bytes_per_frame_,
                    first * bytes_per_frame_);
        if (first < frames)
            std::memcpy(dst + first * bytes_per_frame_, fifo_.data(),
                        (frames - first) * bytes_per_frame_);
        read_frame_ += frames;
        return frames;
    }

    void request_worker() {
        worker_requested_.store(true, std::memory_order_release);
        worker_cv_.notify_one();
    }

    void pipewire_worker() {
        for (;;) {
            std::unique_lock<std::mutex> wait_lock(worker_mutex_);
            worker_cv_.wait(wait_lock, [this] {
                return stop_worker_.load() || worker_requested_.load();
            });
            if (stop_worker_.load()) return;
            worker_requested_.store(false);
            wait_lock.unlock();

            pw_thread_loop_lock(loop_);
            if (shutting_down_.load()) {
                pw_thread_loop_unlock(loop_);
                continue;
            }

            if (!active_) {
                const int r = pw_stream_set_active(stream_, true);
                if (r < 0) {
                    last_error_ = "pw_stream_set_active failed: " + std::to_string(r);
                    queue_error_callback(r, last_error_);
                    pw_thread_loop_unlock(loop_);
                    continue;
                }
                active_ = true;
            }

            while (true) {
                pw_buffer *buffer = pw_stream_dequeue_buffer(stream_);
                if (!buffer) break;

                retire_buffer(buffer);

                if (!fill_and_queue(buffer)) {
                    pw_stream_return_buffer(stream_, buffer);
                    break;
                }

                if (queued_frames_.load() >= target_frames_)
                    break;
            }

            maybe_complete_locked();
            pw_thread_loop_unlock(loop_);
        }
    }

    void retire_buffer(pw_buffer *buffer) {
        const auto it = queued_buffers_.find(buffer);
        if (it == queued_buffers_.end()) {
            debug("DEQUEUE available buffer=%p", static_cast<void *>(buffer));
            return;
        }

        const uint32_t frames = it->second;
        queued_buffers_.erase(it);
        const uint64_t old = queued_frames_.fetch_sub(frames);
        if (old < frames) queued_frames_.store(0);

        debug("DEQUEUE retired buffer=%p frames=%u queued=%llu buffers=%zu",
              static_cast<void *>(buffer), frames,
              static_cast<unsigned long long>(queued_frames_.load()),
              queued_buffers_.size());
        fifo_space_cv_.notify_all();
    }

    bool fill_and_queue(pw_buffer *buffer) {
        if (!buffer || !buffer->buffer || !buffer->buffer->n_datas ||
            !buffer->buffer->datas)
            return false;

        spa_data *d = &buffer->buffer->datas[0];
        if (!d->data || !d->chunk || !d->maxsize) return false;

        const size_t capacity = d->maxsize / bytes_per_frame_;
        if (!capacity) return false;

        const size_t requested =
            buffer->requested ? static_cast<size_t>(buffer->requested) : capacity;

        std::lock_guard<std::mutex> lock(fifo_mutex_);
        const size_t frames =
            std::min({requested, capacity, fifo_available_locked()});
        if (!frames) return false;

        read_fifo_locked(static_cast<uint8_t *>(d->data), frames);
        d->chunk->offset = 0;
        d->chunk->stride = static_cast<int32_t>(bytes_per_frame_);
        d->chunk->size = static_cast<uint32_t>(frames * bytes_per_frame_);
        buffer->size = frames;

        const int r = pw_stream_queue_buffer(stream_, buffer);
        if (r < 0) {
            queue_error_callback(r, "pw_stream_queue_buffer failed");
            return false;
        }

        queued_buffers_[buffer] = static_cast<uint32_t>(frames);
        queued_frames_.fetch_add(frames);

        debug("QUEUE buffer=%p requested=%llu capacity=%zu frames=%zu queued=%llu buffers=%zu fifo=%zu",
              static_cast<void *>(buffer),
              static_cast<unsigned long long>(buffer->requested),
              capacity, frames,
              static_cast<unsigned long long>(queued_frames_.load()),
              queued_buffers_.size(), fifo_available_locked());
        return true;
    }

    void maybe_complete_locked() {
        if (!end_of_utterance_.load()) return;
        std::lock_guard<std::mutex> lock(fifo_mutex_);
        if (fifo_available_locked() || queued_frames_.load()) return;
        if (!completion_pending_.exchange(true))
            completion_cv_.notify_one();
    }

    static void on_state_changed(void *data, pw_stream_state,
                                 pw_stream_state state, const char *error) {
        auto *self = static_cast<PipeWireBackend *>(data);
        if (state == PW_STREAM_STATE_PAUSED || state == PW_STREAM_STATE_STREAMING) {
            self->connection_ready_ = true;
            pw_thread_loop_signal(self->loop_, false);
        } else if (state == PW_STREAM_STATE_ERROR) {
            self->connection_error_ = -5;
            self->last_error_ = error ? error : "PipeWire stream error";
            self->queue_error_callback(-5, self->last_error_);
            pw_thread_loop_signal(self->loop_, false);
        }
    }

    static void on_add_buffer(void *data, pw_buffer *buffer) {
        auto *self = static_cast<PipeWireBackend *>(data);
        if (!buffer || !buffer->buffer || !buffer->buffer->n_datas ||
            !buffer->buffer->datas || !buffer->buffer->datas[0].data) {
            self->connection_error_ = -5;
            self->last_error_ = "PipeWire added an invalid audio buffer";
            pw_thread_loop_signal(self->loop_, false);
            return;
        }
        self->debug("ADD_BUFFER buffer=%p maxsize=%u",
                    static_cast<void *>(buffer),
                    buffer->buffer->datas[0].maxsize);
        pw_thread_loop_signal(self->loop_, false);
    }

    static void on_param_changed(void *data, uint32_t id,
                                 const spa_pod *param) {
        auto *self = static_cast<PipeWireBackend *>(data);
        if (!param || id != SPA_PARAM_Format) return;
        spa_audio_info_raw info{};
        if (spa_format_audio_raw_parse(param, &info) < 0) return;
        self->debug("FORMAT negotiated: format=%s rate=%u channels=%u",
                    spa_type_audio_format_to_short_name(info.format),
                    info.rate, info.channels);
        if (info.rate != self->sample_rate_ ||
            info.channels != self->channels_ ||
            info.format != SPA_AUDIO_FORMAT_S16) {
            self->debug("FORMAT WARNING: source=%u/%u/S16 negotiated=%u/%u/%s",
                        self->sample_rate_, self->channels_,
                        info.rate, info.channels,
                        spa_type_audio_format_to_short_name(info.format));
        }
    }

    // Notification only. No dequeue/copy/queue work occurs here.
    static void on_process(void *data) {
        static_cast<PipeWireBackend *>(data)->request_worker();
    }

    static void on_drained(void *data) {
        static_cast<PipeWireBackend *>(data)->debug("DRAINED callback");
    }

    void completion_worker() {
        std::unique_lock<std::mutex> lock(completion_mutex_);
        while (!stop_completion_.load()) {
            completion_cv_.wait(lock, [this] {
                return stop_completion_.load() || completion_pending_.load();
            });
            if (stop_completion_.load()) break;
            if (!completion_pending_.exchange(false)) continue;

            end_of_utterance_.store(false);
            cancelled_.store(false);
            queue_playback_callback();
        }
    }

    void queue_playback_callback() {
        AIRadioPlaybackCallback cb;
        void *ud;
        {
            std::lock_guard<std::mutex> lock(callback_mutex_);
            cb = playback_callback_;
            ud = playback_user_data_;
        }
        if (cb) cb(ud);
    }

    void queue_error_callback(int code, const std::string &message) {
        AIRadioErrorCallback cb;
        void *ud;
        {
            std::lock_guard<std::mutex> lock(callback_mutex_);
            cb = error_callback_;
            ud = error_user_data_;
        }
        if (cb) cb(ud, code, message.c_str());
    }

    void debug(const char *fmt, ...) const {
        if (!debug_enabled_) return;
        va_list args;
        va_start(args, fmt);
        std::fprintf(stderr, "[AIRadioPipeWire] ");
        std::vfprintf(stderr, fmt, args);
        std::fprintf(stderr, "\\n");
        va_end(args);
    }

    uint32_t sample_rate_, channels_, bits_, bytes_per_frame_;
    size_t fifo_capacity_frames_ = 0;
    uint64_t target_frames_ = 0;
    std::vector<uint8_t> fifo_;
    uint64_t read_frame_ = 0, write_frame_ = 0;
    mutable std::mutex fifo_mutex_;
    std::condition_variable fifo_space_cv_;

    pw_thread_loop *loop_ = nullptr;
    pw_stream *stream_ = nullptr;
    bool started_ = false;
    bool active_ = false;
    bool connection_ready_ = false;
    int connection_error_ = 0;
    const bool debug_enabled_;

    std::atomic<bool> shutting_down_{false};
    std::atomic<bool> stop_worker_{false};
    std::atomic<bool> worker_requested_{false};
    std::atomic<bool> end_of_utterance_{false};
    std::atomic<bool> cancelled_{false};
    std::atomic<uint64_t> queued_frames_{0};
    std::atomic<float> volume_{1.0f};
    std::string last_error_;

    // Only accessed by the helper thread while holding the PipeWire loop lock.
    std::unordered_map<pw_buffer *, uint32_t> queued_buffers_;

    std::mutex worker_mutex_;
    std::condition_variable worker_cv_;
    std::thread pipewire_thread_;

    std::mutex completion_mutex_;
    std::condition_variable completion_cv_;
    std::thread completion_thread_;
    std::atomic<bool> stop_completion_{false};
    std::atomic<bool> completion_pending_{false};

    std::mutex callback_mutex_;
    AIRadioPlaybackCallback playback_callback_ = nullptr;
    void *playback_user_data_ = nullptr;
    AIRadioErrorCallback error_callback_ = nullptr;
    void *error_user_data_ = nullptr;
};}

struct AIRadioPipeWire {
    PipeWireBackend backend;
    AIRadioPipeWire(uint32_t rate, uint32_t channels, uint32_t bits)
        : backend(rate, channels, bits) {}
};

extern "C" {
AIRADIO_API AIRadioPipeWire *airadio_pw_create(
    uint32_t r, uint32_t c, uint32_t b) {
    try { return new AIRadioPipeWire(r, c, b); }
    catch (...) { return nullptr; }
}

AIRADIO_API int airadio_pw_start(AIRadioPipeWire *c) {
    if (!c) return -22;
    try { return c->backend.start(); }
    catch (...) { return -5; }
}

AIRADIO_API int airadio_pw_enqueue(
    AIRadioPipeWire *c,
    const AIRadioPcmSegment *s,
    size_t n) {
    if (!c) return -22;
    try { return c->backend.enqueue(s, n); }
    catch (...) { return -5; }
}

AIRADIO_API int airadio_pw_end_utterance(
    AIRadioPipeWire *c, int cancel) {
    if (!c) return -22;
    try { return c->backend.end_utterance(cancel != 0); }
    catch (...) { return -5; }
}

AIRADIO_API int airadio_pw_clear(AIRadioPipeWire *c) {
    if (!c) return -22;
    try { return c->backend.clear(); }
    catch (...) { return -5; }
}

AIRADIO_API int airadio_pw_set_volume(
    AIRadioPipeWire *c, float v) {
    if (!c) return -22;
    try { return c->backend.set_volume(v); }
    catch (...) { return -5; }
}

AIRADIO_API float airadio_pw_get_volume(AIRadioPipeWire *c) {
    return c ? c->backend.volume() : 0.0f;
}

AIRADIO_API uint64_t airadio_pw_queued_frames(
    AIRadioPipeWire *c) {
    return c ? c->backend.queued_frames() : 0;
}

AIRADIO_API uint64_t airadio_pw_outstanding_frames(
    AIRadioPipeWire *c) {
    return c ? c->backend.outstanding_frames() : 0;
}

AIRADIO_API void airadio_pw_set_playback_complete_callback(
    AIRadioPipeWire *c, AIRadioPlaybackCallback cb, void *ud) {
    if (c) c->backend.set_playback_callback(cb, ud);
}

AIRADIO_API void airadio_pw_set_error_callback(
    AIRadioPipeWire *c, AIRadioErrorCallback cb, void *ud) {
    if (c) c->backend.set_error_callback(cb, ud);
}

AIRADIO_API const char *airadio_pw_last_error(
    AIRadioPipeWire *c) {
    return c ? c->backend.last_error() : "invalid context";
}

AIRADIO_API void airadio_pw_destroy(AIRadioPipeWire *c) {
    delete c;
}
}
