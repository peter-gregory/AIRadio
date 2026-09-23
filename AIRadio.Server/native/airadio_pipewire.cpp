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
#include <string>
#include <thread>
#include <vector>
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
        : sample_rate_(rate),
          channels_(channels),
          bits_per_sample_(bits),
          bytes_per_frame_(channels * (bits / 8)),
          block_frames_(0),
          block_bytes_(0),
          blocks_(kRingBlocks),
          debug_enabled_(std::getenv("AIRADIO_PIPEWIRE_DEBUG") != nullptr) {}

    ~PipeWireBackend() { destroy(); }

    int start() {
        if (started_) return 0;
        if (sample_rate_ != kAIRadioSampleRate ||
            channels_ != kAIRadioChannels ||
            bits_per_sample_ != kAIRadioBits ||
            !bytes_per_frame_) {
            last_error_ = "AIRadio PipeWire audio must be 16 kHz, 16-bit, mono";
            return -22;
        }

        debug("START source format: %u Hz, %u ch, %u-bit, %u bytes/frame",
              sample_rate_, channels_, bits_per_sample_, bytes_per_frame_);

        const uint32_t preferred_frames = static_cast<uint32_t>(((static_cast<uint64_t>(sample_rate_) * kPreferredBufferMilliseconds) / 1000));
        const uint32_t minimum_frames = static_cast<uint32_t>(((static_cast<uint64_t>(sample_rate_) * kMinimumBufferMilliseconds) / 1000));
        const uint32_t maximum_frames = static_cast<uint32_t>(((static_cast<uint64_t>(sample_rate_) * kMaximumBufferMilliseconds) / 1000));
        debug("REQUEST buffers: preferred=%u frames (%zu ms), range=%u..%u frames, buffers=%zu..%zu",
              preferred_frames, kPreferredBufferMilliseconds,
              minimum_frames, maximum_frames,
              kMinPipeWireBuffers, kMaxPipeWireBuffers);

        head_index_.store(0, std::memory_order_relaxed);
        active_index_.store(0, std::memory_order_relaxed);
        tail_index_.store(0, std::memory_order_relaxed);
        head_fill_bytes_ = 0;
        max_active_buffers_ = kMinPipeWireBuffers;
        available_buffers_.clear();
        available_buffer_count_ = 0;
        active_ = false;
        end_of_utterance_ = false;
        cancelled_ = false;
        shutting_down_.store(false, std::memory_order_release);
        buffer_full_event_.set();
        buffer_ready_event_.reset();

        pw_init(nullptr, nullptr);
        loop_ = pw_thread_loop_new("AIRadioPipeWire", nullptr);
        if (!loop_) return -12;

        pw_thread_loop_lock(loop_);
        int lr = pw_thread_loop_start(loop_);
        if (lr < 0) {
            pw_thread_loop_unlock(loop_);
            return lr;
        }

        std::string latency = std::to_string(preferred_frames) + "/" +
                              std::to_string(sample_rate_);
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
            .version = PW_VERSION_STREAM_EVENTS,
            .destroy = nullptr,
            .state_changed = &PipeWireBackend::on_state_changed,
            .control_info = nullptr,
            .io_changed = nullptr,
            .param_changed = &PipeWireBackend::on_param_changed,
            .add_buffer = &PipeWireBackend::on_add_buffer,
            .remove_buffer = nullptr,
            .process = &PipeWireBackend::on_process,
            .drained = &PipeWireBackend::on_drained,
            .command = nullptr,
            .trigger_done = nullptr
        };

        stream_ = pw_stream_new_simple(
            pw_thread_loop_get_loop(loop_), "AIRadioAudioOutput",
            props, &events, this);
        if (!stream_) {
            pw_thread_loop_unlock(loop_);
            return -12;
        }

        uint8_t pod[kPodBufferBytes];
        spa_pod_builder builder = SPA_POD_BUILDER_INIT(pod, sizeof(pod));
        spa_audio_info_raw info = SPA_AUDIO_INFO_RAW_INIT(
            .format = SPA_AUDIO_FORMAT_S16, .rate = sample_rate_,
            .channels = channels_);
        // Let PipeWire negotiate the actual buffer layout. The standalone
        // WAV test shows that the graph chooses a usable native buffer size;
        // AIRadio uses that negotiated size as its ring block size in
        // on_add_buffer(). Supplying our own SPA_PARAM_Buffers constraint here
        // can prevent the graph from allocating any buffers at all.
        const spa_pod *params[1] = {
            spa_format_audio_raw_build(&builder, SPA_PARAM_EnumFormat, &info)
        };
        if (!params[0]) {
            pw_thread_loop_unlock(loop_);
            return -22;
        }

        connection_ready_ = false;
        connection_error_ = 0;
        const int result = pw_stream_connect(
            stream_, PW_DIRECTION_OUTPUT, PW_ID_ANY,
            static_cast<pw_stream_flags>(
                PW_STREAM_FLAG_AUTOCONNECT |
                PW_STREAM_FLAG_MAP_BUFFERS |
                PW_STREAM_FLAG_INACTIVE),
            params, 1);
        if (result < 0) {
            last_error_ = "pw_stream_connect failed: " + std::to_string(result);
            pw_thread_loop_unlock(loop_);
            return result;
        }

        while (!connection_ready_ && !connection_error_)
            pw_thread_loop_wait(loop_);

        if (connection_error_ < 0) {
            int e = connection_error_;
            pw_thread_loop_unlock(loop_);
            return e;
        }

        // This PipeWire graph does not deliver add_buffer() callbacks for
        // MAP_BUFFERS, so discover the native buffer from the first process
        // callback. Activate the stream only long enough to obtain that
        // buffer, then immediately return it and deactivate the stream.
        // This avoids both the startup deadlock (waiting for add_buffer)
        // and the idle process busy-loop caused by leaving an empty stream
        // active.
        int activate_result = pw_stream_set_active(stream_, true);
        if (activate_result < 0) {
            last_error_ = "pw_stream_set_active failed during buffer discovery: " +
                          std::to_string(activate_result);
            pw_thread_loop_unlock(loop_);
            return activate_result;
        }

        while (!block_bytes_ && !connection_error_)
            pw_thread_loop_wait(loop_);

        if (connection_error_ < 0) {
            int e = connection_error_;
            pw_thread_loop_unlock(loop_);
            return e;
        }

        if (!block_bytes_ || !block_frames_) {
            last_error_ = "PipeWire did not provide a usable audio buffer";
            pw_stream_set_active(stream_, false);
            pw_thread_loop_unlock(loop_);
            return -22;
        }

        pw_stream_set_active(stream_, false);
        active_ = false;

        debug("STREAM connected; native buffer=%zu bytes, %u frames (%.1f ms), active limit=%zu buffers",
              block_bytes_, block_frames_,
              1000.0 * block_frames_ / sample_rate_,
              max_active_buffers_);
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
            if (!segments[i].data) return -22;

            const uint8_t *src = segments[i].data;
            size_t left = segments[i].size;

            while (left) {
                if (end_of_utterance_ || cancelled_ ||
                    shutting_down_.load(std::memory_order_acquire)) {
                    return -16;
                }

                // Never wait while holding ring_mutex_: PipeWire must be
                // able to advance the tail and reopen the producer gate.
                if (head_fill_bytes_ == 0)
                    buffer_full_event_.wait();

                std::unique_lock<std::mutex> ring_lock(ring_mutex_);

                // The gate may have been reset after the wait but before the
                // producer acquired the mutex. Recheck the protected state.
                if (head_fill_bytes_ == 0 &&
                    next_index(head_index_.load(std::memory_order_relaxed)) ==
                        tail_index_.load(std::memory_order_acquire)) {
                    ring_lock.unlock();
                    buffer_full_event_.wait();
                    continue;
                }

                if (shutting_down_.load(std::memory_order_acquire) ||
                    end_of_utterance_ || cancelled_) {
                    return -16;
                }

                size_t space = block_bytes_ - head_fill_bytes_;
                size_t n = std::min(space, left);
                std::memcpy(
                    blocks_[head_index_.load(std::memory_order_relaxed)]
                        .data.get() + head_fill_bytes_,
                    src, n);

                src += n;
                left -= n;
                head_fill_bytes_ += n;

                if (head_fill_bytes_ == block_bytes_) {
                    size_t next = next_index(head_index_.load(std::memory_order_relaxed));
                    head_index_.store(next, std::memory_order_release);
                    head_fill_bytes_ = 0;

                    if (next_index(next) == tail_index_.load(std::memory_order_acquire))
                        buffer_full_event_.reset();
                    else
                        buffer_full_event_.set();

                    buffer_ready_event_.set();
                }
            }
        }

        return 0;
    }

    int end_utterance(bool cancel) {
        if (!started_ || !stream_ || !loop_) return -107;

        pw_thread_loop_lock(loop_);
        if (end_of_utterance_) {
            pw_thread_loop_unlock(loop_);
            return 0;
        }

        std::lock_guard<std::mutex> ring_lock(ring_mutex_);
        end_of_utterance_ = true;
        cancelled_ = cancel;

        if (cancel) {
            // Discard only blocks which have not yet been submitted to
            // PipeWire. Already submitted blocks must remain untouched.
            head_index_.store(active_index_.load(std::memory_order_acquire), std::memory_order_release);
            head_fill_bytes_ = 0;
            buffer_full_event_.set();
        } else if (head_fill_bytes_) {
            std::memset(
                blocks_[head_index_.load(std::memory_order_relaxed)]
                    .data.get() + head_fill_bytes_,
                0, block_bytes_ - head_fill_bytes_);

            size_t next = next_index(head_index_.load(std::memory_order_relaxed));
            head_index_.store(next, std::memory_order_release);
            head_fill_bytes_ = 0;
        }

        // Ending/cancelling releases the producer gate so it cannot remain
        // closed across the next utterance.
        buffer_full_event_.set();
        buffer_ready_event_.set();
        maybe_complete();
        pw_thread_loop_unlock(loop_);

        return 0;
    }

    int clear() { return end_utterance(true); }

    int set_volume(float volume) {
        if (!started_ || !stream_ || !loop_) return -107;
        volume = std::clamp(volume, 0.0f, 1.0f);

        pw_thread_loop_lock(loop_);
        int r = pw_stream_set_control(
            stream_, SPA_PROP_volume, 1, &volume, nullptr);
        if (r >= 0)
            volume_.store(volume, std::memory_order_release);
        else
            last_error_ = "pw_stream_set_control(volume) failed: " +
                          std::to_string(r);
        pw_thread_loop_unlock(loop_);
        return r;
    }

    uint64_t queued_frames() const noexcept {
        size_t active = active_index_.load(std::memory_order_acquire);
        size_t head = head_index_.load(std::memory_order_acquire);
        return distance(active, head) * block_frames_ +
               head_fill_bytes_ / bytes_per_frame_;
    }

    uint64_t outstanding_frames() const noexcept {
        return outstanding_frames_.load(std::memory_order_acquire);
    }

    float volume() const noexcept {
        return volume_.load(std::memory_order_acquire);
    }

    void set_playback_callback(AIRadioPlaybackCallback cb, void *ud) {
        std::lock_guard<std::mutex> l(callback_mutex_);
        playback_callback_ = cb;
        playback_user_data_ = ud;
    }

    void set_error_callback(AIRadioErrorCallback cb, void *ud) {
        std::lock_guard<std::mutex> l(callback_mutex_);
        error_callback_ = cb;
        error_user_data_ = ud;
    }

    const char *last_error() const noexcept {
        return last_error_.c_str();
    }

    void destroy() {
        shutting_down_.store(true, std::memory_order_release);
        buffer_full_event_.set();
        buffer_ready_event_.set();

        stop_completion_thread_.store(true, std::memory_order_release);
        completion_event_.set();

        if (pipewire_thread_.joinable())
            pipewire_thread_.join();

        if (completion_thread_.joinable())
            completion_thread_.join();

        if (loop_) {
            pw_thread_loop_lock(loop_);
            if (stream_) {
                pw_stream_set_active(stream_, false);
                pw_stream_destroy(stream_);
                stream_ = nullptr;
            }
            available_buffers_.clear();
            available_buffer_count_ = 0;
            pw_thread_loop_unlock(loop_);
            pw_thread_loop_stop(loop_);
            pw_thread_loop_destroy(loop_);
            loop_ = nullptr;
        }

        blocks_.clear();
        started_ = false;
        pw_deinit();
    }

private:
    size_t next_index(size_t index) const noexcept {
        return (index + 1) % kRingBlocks;
    }

    size_t distance(size_t from, size_t to) const noexcept {
        return to >= from ? to - from : kRingBlocks - from + to;
    }

    size_t free_blocks() const noexcept {
        size_t tail = tail_index_.load(std::memory_order_acquire);
        size_t head = head_index_.load(std::memory_order_acquire);
        size_t used = distance(tail, head);
        if (head_fill_bytes_)
            ++used;
        return kRingBlocks - used;
    }

    bool any_ready() const noexcept {
        return active_index_.load(std::memory_order_acquire) !=
               head_index_.load(std::memory_order_acquire);
    }

    size_t active_count() const noexcept {
        return distance(
            tail_index_.load(std::memory_order_acquire),
            active_index_.load(std::memory_order_acquire));
    }

    void pipewire_worker() {
        for (;;) {
            buffer_ready_event_.wait();
            if (shutting_down_.load(std::memory_order_acquire))
                return;

            pw_thread_loop_lock(loop_);

            for (;;) {
                if (shutting_down_.load(std::memory_order_acquire))
                    break;

                if (any_ready() && !active_) {
                    int r = pw_stream_set_active(stream_, true);
                    if (r < 0) {
                        last_error_ = "pw_stream_set_active failed: " +
                                      std::to_string(r);
                        queue_error_callback(r, last_error_);
                        break;
                    }
                    active_ = true;
                }

                while (any_ready() &&
                       active_count() < max_active_buffers_ &&
                       available_buffer_count_) {
                    pw_buffer *buffer =
                        available_buffers_[--available_buffer_count_];
                    submit(buffer);
                }

                const bool ready = any_ready();

                if (end_of_utterance_ && !ready && active_count() == 0 &&
                    completion_pending_.load(std::memory_order_acquire)) {
                    if (active_) {
                        pw_stream_set_active(stream_, false);
                        active_ = false;
                    }
                }

                const bool can_submit =
                    ready &&
                    active_count() < max_active_buffers_ &&
                    available_buffer_count_;

                if (can_submit)
                    continue;

                // Producer publication and this reset are serialized by
                // ring_mutex_, preventing a ready notification from being
                // lost between the predicate check and reset.
                {
                    std::lock_guard<std::mutex> ring_lock(ring_mutex_);
                    const bool still_ready = any_ready();
                    const bool can_send =
                        still_ready &&
                        active_count() < max_active_buffers_ &&
                        available_buffer_count_;

                    if (can_send) {
                        buffer_ready_event_.set();
                        continue;
                    }

                    buffer_ready_event_.reset();
                }
                break;
            }

            pw_thread_loop_unlock(loop_);
        }
    }

    void submit(pw_buffer *buffer) {
        const size_t head = head_index_.load(std::memory_order_acquire);
        const size_t active =
            active_index_.load(std::memory_order_acquire);

        if (active == head) {
            if (available_buffer_count_ < available_buffers_.size())
                available_buffers_[available_buffer_count_++] = buffer;
            return;
        }

        if (!buffer || !buffer->buffer)
            return;

        spa_buffer *sb = buffer->buffer;
        if (!sb->n_datas || !sb->datas) {
            pw_stream_return_buffer(stream_, buffer);
            return;
        }

        spa_data *d = &sb->datas[0];
        if (!d->data || !d->chunk || d->maxsize < block_bytes_) {
            pw_stream_return_buffer(stream_, buffer);
            queue_error_callback(
                -5, "PipeWire buffer is smaller than the negotiated AIRadio ring block");
            return;
        }

        PcmBlock *block = &blocks_[active];

        debug("SUBMIT block=%zu head=%zu tail=%zu buffer=%p maxsize=%u requested=%u pre_chunk(size=%u offset=%u stride=%d)",
              active, head, tail_index_.load(std::memory_order_acquire),
              static_cast<void *>(buffer), d->maxsize, buffer->requested,
              d->chunk->size, d->chunk->offset, d->chunk->stride);

        std::memcpy(d->data, block->data.get(), block_bytes_);
        d->chunk->offset = 0;
        d->chunk->stride = static_cast<int32_t>(bytes_per_frame_);
        d->chunk->size = static_cast<uint32_t>(block_bytes_);
        buffer->size = block_frames_;

        active_index_.store(next_index(active), std::memory_order_release);
        outstanding_frames_.fetch_add(
            block_frames_, std::memory_order_release);

        debug("SUBMIT queued block=%zu buffer=%p size=%u frames=%u stride=%d",
              active, static_cast<void *>(buffer),
              d->chunk->size, buffer->size, d->chunk->stride);

        int r = pw_stream_queue_buffer(stream_, buffer);
        if (r < 0) {
            active_index_.store(active, std::memory_order_release);

            outstanding_frames_.fetch_sub(
                block_frames_, std::memory_order_acq_rel);

            queue_error_callback(r, "pw_stream_queue_buffer failed");
        }
    }

    void process() {
        pw_buffer *buffer = pw_stream_dequeue_buffer(stream_);
        if (!buffer || !buffer->buffer)
            return;

        // No user_data mapping is needed. If AIRadio has outstanding
        // submissions, this process event retires one ring block. Otherwise
        // it simply makes a PipeWire buffer available to the queue thread.
        const size_t active_before = active_count();

        if (!block_bytes_) {
            spa_data *d = &buffer->buffer->datas[0];
            const uint32_t native_bytes =
                d->maxsize - (d->maxsize % bytes_per_frame_);
            if (!d->data || !d->maxsize || !native_bytes) {
                connection_error_ = -22;
                last_error_ = "PipeWire provided an unusable audio buffer";
                pw_stream_return_buffer(stream_, buffer);
                pw_thread_loop_signal(loop_, false);
                return;
            }

            block_bytes_ = native_bytes;
            block_frames_ = block_bytes_ / bytes_per_frame_;
            try {
                for (auto &block : blocks_)
                    block.data = std::make_unique<uint8_t[]>(block_bytes_);
            } catch (...) {
                connection_error_ = -12;
                last_error_ = "Unable to allocate AIRadio audio ring buffer";
                pw_stream_return_buffer(stream_, buffer);
                pw_thread_loop_signal(loop_, false);
                return;
            }

            const size_t under_one_second =
                sample_rate_ > 1 ? (sample_rate_ - 1) / block_frames_ : 0;
            max_active_buffers_ =
                std::max(kMinPipeWireBuffers, under_one_second);

            debug("PROCESS established native size=%u -> ring block=%zu bytes, %u frames (%.1f ms), active limit=%zu",
                  d->maxsize, block_bytes_, block_frames_,
                  1000.0 * block_frames_ / sample_rate_,
                  max_active_buffers_);

            pw_stream_return_buffer(stream_, buffer);
            pw_stream_set_active(stream_, false);
            active_ = false;
            pw_thread_loop_signal(loop_, false);
            return;
        }

        debug("PROCESS buffer=%p active_before=%zu tail=%zu active=%zu head=%zu",
              static_cast<void *>(buffer), active_before,
              tail_index_.load(std::memory_order_acquire),
              active_index_.load(std::memory_order_acquire),
              head_index_.load(std::memory_order_acquire));

        if (active_before) {
            tail_index_.store(
                next_index(tail_index_.load(std::memory_order_relaxed)),
                std::memory_order_release);
            outstanding_frames_.fetch_sub(
                block_frames_, std::memory_order_acq_rel);
            buffer_full_event_.set();

            debug("PROCESS retired block; new tail=%zu active=%zu",
                  tail_index_.load(std::memory_order_acquire),
                  active_count());
        }

        // process() and the queue thread both run under the PipeWire thread
        // loop serialization. The fixed-size pool avoids allocation here.
        if (available_buffer_count_ < available_buffers_.size())
            available_buffers_[available_buffer_count_++] = buffer;

        if (any_ready())
            buffer_ready_event_.set();
        else
            maybe_complete();
    }

    bool unprocessed() const noexcept {
        return tail_index_.load(std::memory_order_acquire) !=
               head_index_.load(std::memory_order_acquire);
    }

    void maybe_complete() {
        if (!end_of_utterance_) return;
        if (active_count() != 0 || unprocessed()) return;

        // This is only a state transition/signal. The PipeWire process
        // callback must never invoke managed code or perform potentially
        // expensive completion work.
        if (!completion_pending_.exchange(
                true, std::memory_order_acq_rel)) {
            completion_event_.set();
        }
    }

    static void on_state_changed(
        void *data, pw_stream_state, pw_stream_state state,
        const char *error) {
        auto *self = static_cast<PipeWireBackend *>(data);

        if (state == PW_STREAM_STATE_PAUSED ||
            state == PW_STREAM_STATE_STREAMING) {
            self->connection_ready_ = true;
            pw_thread_loop_signal(self->loop_, false);
        } else if (state == PW_STREAM_STATE_ERROR) {
            self->connection_error_ = -5;
            self->last_error_ = error
                ? error
                : "PipeWire stream entered an error state";
            self->queue_error_callback(-5, self->last_error_);
            pw_thread_loop_signal(self->loop_, false);
        }
    }

    static void on_add_buffer(void *data, pw_buffer *buffer) {
        auto *self = static_cast<PipeWireBackend *>(data);

        if (!buffer || !buffer->buffer || !buffer->buffer->n_datas ||
            !buffer->buffer->datas) {
            self->connection_error_ = -5;
            self->last_error_ = "PipeWire added an invalid audio buffer";
            pw_thread_loop_signal(self->loop_, false);
            return;
        }

        spa_data *d = &buffer->buffer->datas[0];
        if (!d->data || !d->maxsize || !self->bytes_per_frame_) {
            self->connection_error_ = -5;
            self->last_error_ = "PipeWire added an unusable audio buffer";
            pw_thread_loop_signal(self->loop_, false);
            return;
        }

        const uint32_t native_bytes = d->maxsize - (d->maxsize % self->bytes_per_frame_);
        if (!native_bytes) {
            self->connection_error_ = -22;
            self->last_error_ = "PipeWire audio buffer is smaller than one frame";
            pw_thread_loop_signal(self->loop_, false);
            return;
        }

        if (!self->block_bytes_) {
            self->block_bytes_ = native_bytes;
            self->block_frames_ = self->block_bytes_ / self->bytes_per_frame_;

            try {
                for (auto &block : self->blocks_)
                    block.data = std::make_unique<uint8_t[]>(self->block_bytes_);
            } catch (...) {
                self->connection_error_ = -12;
                self->last_error_ = "Unable to allocate AIRadio audio ring buffer";
                pw_thread_loop_signal(self->loop_, false);
                return;
            }

            const size_t under_one_second = self->sample_rate_ > 1 ? (self->sample_rate_ - 1) / self->block_frames_ : 0;
            self->max_active_buffers_ = std::max(kMinPipeWireBuffers, under_one_second);

            self->debug("ADD_BUFFER native size=%u -> ring block=%zu bytes, %u frames (%.1f ms), active limit=%zu",
                        d->maxsize, self->block_bytes_, self->block_frames_,
                        1000.0 * self->block_frames_ / self->sample_rate_,
                        self->max_active_buffers_);
        } else if (native_bytes != self->block_bytes_) {
            self->connection_error_ = -22;
            self->last_error_ = "PipeWire negotiated inconsistent audio buffer sizes";
            self->debug("ADD_BUFFER size mismatch: first=%zu current=%u", self->block_bytes_, d->maxsize);
            pw_thread_loop_signal(self->loop_, false);
            return;
        }

        ++self->pipewire_buffer_count_;
        // Wake start(): the stream state may have become ready before this
        // callback, so buffer availability is a separate startup condition.
        pw_thread_loop_signal(self->loop_, false);
    }

    static void on_param_changed(
        void *data, uint32_t id, const spa_pod *param) {
        auto *self = static_cast<PipeWireBackend *>(data);

        if (!param) {
            self->debug("PARAM id=%u param=NULL", id);
            return;
        }

        if (id != SPA_PARAM_Format) {
            self->debug("PARAM id=%u", id);
            return;
        }

        spa_audio_info_raw info{};
        if (spa_format_audio_raw_parse(param, &info) < 0) {
            self->debug("FORMAT unable to parse negotiated format");
            return;
        }

        self->debug(
            "FORMAT negotiated: format=%s rate=%u channels=%u",
            spa_type_audio_format_to_short_name(info.format),
            info.rate, info.channels);

        if (info.rate != self->sample_rate_ ||
            info.channels != self->channels_ ||
            info.format != SPA_AUDIO_FORMAT_S16) {
            self->debug(
                "FORMAT WARNING: source=%u Hz/%u ch/S16 but PipeWire negotiated %u Hz/%u ch/%s",
                self->sample_rate_, self->channels_,
                info.rate, info.channels,
                spa_type_audio_format_to_short_name(info.format));
        }
    }

    static void on_process(void *data) {
        static_cast<PipeWireBackend *>(data)->process();
    }

    static void on_drained(void *data) {
        static_cast<PipeWireBackend *>(data)->debug("DRAINED callback");
    }

    void debug(const char *format, ...) const {
        if (!debug_enabled_) return;

        va_list args;
        va_start(args, format);
        std::fprintf(stderr, "[AIRadioPipeWire] ");
        std::vfprintf(stderr, format, args);
        std::fprintf(stderr, "\n");
        va_end(args);
    }

    void completion_worker() {
        for (;;) {
            completion_event_.wait();

            if (stop_completion_thread_.load(std::memory_order_acquire))
                return;

            // Consume exactly one completion notification. If another
            // completion is signalled while the callback is running, the
            // pending flag remains set and the event remains set until the
            // next loop iteration.
            if (!completion_pending_.exchange(
                    false, std::memory_order_acq_rel))
                continue;

            // Completion is the boundary between utterances. Clear the
            // producer state before entering managed code so the callback can
            // immediately enqueue the next utterance.

            // Managed/C# completion is deliberately isolated from the
            // PipeWire process callback. This callback may block or perform
            // arbitrary worker-thread work.
            end_of_utterance_ = false;
            cancelled_ = false;
            queue_playback_callback();
        }
    }

    void queue_playback_callback() {
        AIRadioPlaybackCallback cb;
        void *ud;
        {
            std::lock_guard<std::mutex> l(callback_mutex_);
            cb = playback_callback_;
            ud = playback_user_data_;
        }
        if (cb) cb(ud);
    }

    void queue_error_callback(
        int code, const std::string &message) {
        AIRadioErrorCallback cb;
        void *ud;
        {
            std::lock_guard<std::mutex> l(callback_mutex_);
            cb = error_callback_;
            ud = error_user_data_;
        }
        if (cb) cb(ud, code, message.c_str());
    }

    uint32_t sample_rate_;
    uint32_t channels_;
    uint32_t bits_per_sample_;
    uint32_t bytes_per_frame_;
    uint32_t block_frames_;
    size_t block_bytes_;

    // One fixed-size ring. There are no generations and no reallocations.
    std::vector<PcmBlock> blocks_;
    std::atomic<size_t> head_index_{0};
    std::atomic<size_t> active_index_{0};
    std::atomic<size_t> tail_index_{0};
    size_t head_fill_bytes_ = 0;

    size_t max_active_buffers_ = kMinPipeWireBuffers;
    std::array<pw_buffer *, kMaxPipeWireBuffers> available_buffers_{};
    size_t available_buffer_count_ = 0;
    pw_thread_loop *loop_ = nullptr;
    pw_stream *stream_ = nullptr;

    bool started_ = false;
    bool active_ = false;
    bool end_of_utterance_ = false;
    bool cancelled_ = false;
    bool connection_ready_ = false;
    int connection_error_ = 0;
    const bool debug_enabled_;

    std::atomic<bool> shutting_down_{false};
    std::atomic<uint64_t> outstanding_frames_{0};
    std::atomic<float> volume_{1.0f};
    std::string last_error_;

    // Protects ring head/tail and gate state transitions shared by the
    // producer and PipeWire consumer. The producer never waits while holding it.
    std::mutex ring_mutex_;

    // SET means next(head) != tail. RESET means exactly one free slot remains.
    ManualResetEvent buffer_full_event_{true};

    // SET means the queue thread should inspect ring data and reusable
    // PipeWire buffers.
    ManualResetEvent buffer_ready_event_{false};

    std::thread pipewire_thread_;
    std::thread completion_thread_;
    AutoResetEvent completion_event_{false};
    std::atomic<bool> stop_completion_thread_{false};
    std::atomic<bool> completion_pending_{false};

    std::mutex callback_mutex_;
    AIRadioPlaybackCallback playback_callback_ = nullptr;
    void *playback_user_data_ = nullptr;
    AIRadioErrorCallback error_callback_ = nullptr;
    void *error_user_data_ = nullptr;
};
}

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
