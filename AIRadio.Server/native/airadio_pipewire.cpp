#include "airadio_pipewire.h"
#include <pipewire/pipewire.h>
#include <spa/buffer/buffer.h>
#include <spa/param/param.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/param/props.h>
#include <spa/pod/builder.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {
constexpr size_t kInitialBlocks = 64;
constexpr size_t kPipeWireBuffers = 8;
constexpr size_t kPodBufferBytes = 1024;

struct PcmBlock { std::unique_ptr<uint8_t[]> data; };

struct Generation {
    Generation(size_t capacity, size_t bytes) {
        blocks.reserve(capacity);
        for (size_t i = 0; i < capacity; ++i) {
            auto b = std::make_unique<PcmBlock>();
            b->data = std::make_unique<uint8_t[]>(bytes);
            blocks.push_back(std::move(b));
        }
    }

    std::vector<std::unique_ptr<PcmBlock>> blocks;

    // Each generation is a ring with one slot permanently left empty.
    // tail   = oldest block still owned by the queue/PipeWire.
    // active = oldest block not yet submitted to PipeWire.
    // head   = next block to be written by enqueue().
    //
    // The state of every block is therefore implicit:
    //   tail -> active : submitted to PipeWire, awaiting completion
    //   active -> head  : ready to submit
    //   head -> tail    : free
    //
    // No full flag is required. A generation is full when next(head)==tail.
    size_t head = 0;
    size_t active = 0;
    size_t tail = 0;
    Generation *next = nullptr;
};

class PipeWireBackend {
public:
    PipeWireBackend(uint32_t rate, uint32_t channels, uint32_t bits)
        : sample_rate_(rate), channels_(channels), bits_per_sample_(bits),
          bytes_per_frame_(channels * (bits / 8)),
          block_frames_(rate / 10),
          block_bytes_(block_frames_ * bytes_per_frame_) {}

    ~PipeWireBackend() { destroy(); }

    int start() {
        if (started_) return 0;
        if (!sample_rate_ || !channels_ || bits_per_sample_ != 16 ||
            !block_frames_ || !block_bytes_) return -22;

        pw_init(nullptr, nullptr);
        loop_ = pw_thread_loop_new("AIRadioPipeWire", nullptr);
        if (!loop_) return -12;

        pw_thread_loop_lock(loop_);
        int lr = pw_thread_loop_start(loop_);
        if (lr < 0) { pw_thread_loop_unlock(loop_); return lr; }

        std::string latency = std::to_string(block_frames_) + "/" +
                              std::to_string(sample_rate_);
        pw_properties *props = pw_properties_new(
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Playback",
            PW_KEY_MEDIA_ROLE, "Music",
            PW_KEY_NODE_LATENCY, latency.c_str(),
            PW_KEY_NODE_MAX_LATENCY, latency.c_str(),
            PW_KEY_NODE_STREAM, "true", nullptr);
        if (!props) { pw_thread_loop_unlock(loop_); return -12; }

        static const pw_stream_events events = {
            PW_VERSION_STREAM_EVENTS, nullptr,
            &PipeWireBackend::on_state_changed, nullptr, nullptr, nullptr,
            nullptr, nullptr, &PipeWireBackend::on_process,
            &PipeWireBackend::on_drained, nullptr, nullptr
        };

        stream_ = pw_stream_new_simple(
            pw_thread_loop_get_loop(loop_), "AIRadioAudioOutput",
            props, &events, this);
        if (!stream_) { pw_thread_loop_unlock(loop_); return -12; }

        uint8_t pod[kPodBufferBytes];
        spa_pod_builder builder = SPA_POD_BUILDER_INIT(pod, sizeof(pod));
        spa_audio_info_raw info = SPA_AUDIO_INFO_RAW_INIT(
            .format = SPA_AUDIO_FORMAT_S16, .rate = sample_rate_,
            .channels = channels_);
        const spa_pod *params[1] = {
            spa_format_audio_raw_build(&builder, SPA_PARAM_EnumFormat, &info)
        };
        if (!params[0]) { pw_thread_loop_unlock(loop_); return -22; }

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

        pw_thread_loop_unlock(loop_);
        completion_thread_ = std::thread([this] { completion_worker(); });
        started_ = true;
        return 0;
    }

    int enqueue(const AIRadioPcmSegment *segments, size_t count) {
        if (!started_ || !stream_ || !loop_) return -107;
        if (count && !segments) return -22;

        pw_thread_loop_lock(loop_);
        if (end_of_utterance_) {
            pw_thread_loop_unlock(loop_);
            return -16;
        }

        for (size_t i = 0; i < count; ++i) {
            if (!segments[i].size) continue;
            if (!segments[i].data) {
                pw_thread_loop_unlock(loop_);
                return -22;
            }

            const uint8_t *src = segments[i].data;
            size_t left = segments[i].size;
            while (left) {
                if (!producer_) create_generation();

                size_t space = block_bytes_ - head_fill_bytes_;
                size_t n = std::min(space, left);
                std::memcpy(producer_->blocks[producer_->head]->data.get() +
                                head_fill_bytes_, src, n);
                src += n;
                left -= n;
                head_fill_bytes_ += n;

                if (head_fill_bytes_ == block_bytes_) {
                    producer_->head = next(*producer_, producer_->head);
                    head_fill_bytes_ = 0;

                    // Leave one slot empty. Once the producer reaches the
                    // slot immediately before tail, this generation is full
                    // and all subsequent samples go into a new generation.
                    if (is_full(*producer_))
                        create_generation();
                }
            }
        }

        service_pipewire();
        pw_thread_loop_unlock(loop_);
        return 0;
    }

    int end_utterance(bool cancel) {
        if (!started_ || !stream_ || !loop_) return -107;

        pw_thread_loop_lock(loop_);
        if (end_of_utterance_) {
            pw_thread_loop_unlock(loop_);
            return 0;
        }

        end_of_utterance_ = true;
        cancelled_ = cancel;

        if (cancel) {
            discard_ready();
            head_fill_bytes_ = 0;
        } else if (head_fill_bytes_) {
            std::memset(
                producer_->blocks[producer_->head]->data.get() + head_fill_bytes_,
                0, block_bytes_ - head_fill_bytes_);
            producer_->head = next(*producer_, producer_->head);
            head_fill_bytes_ = 0;

            if (is_full(*producer_))
                create_generation();
        }

        service_pipewire();
        maybe_complete();
        pw_thread_loop_unlock(loop_);
        return 0;
    }

    int clear() { return end_utterance(true); }

    int set_volume(float volume) {
        if (!started_ || !stream_ || !loop_) return -107;
        volume = std::clamp(volume, 0.0f, 1.0f);
        pw_thread_loop_lock(loop_);
        int r = pw_stream_set_control(stream_, SPA_PROP_volume, 1, &volume, nullptr);
        if (r >= 0) volume_.store(volume, std::memory_order_release);
        else last_error_ = "pw_stream_set_control(volume) failed: " + std::to_string(r);
        pw_thread_loop_unlock(loop_);
        return r;
    }

    uint64_t queued_frames() const noexcept {
        return queued_blocks() * block_frames_ +
               head_fill_bytes_ / bytes_per_frame_;
    }

    uint64_t outstanding_frames() const noexcept {
        return outstanding_frames_.load(std::memory_order_acquire);
    }

    float volume() const noexcept { return volume_.load(std::memory_order_acquire); }

    void set_playback_callback(AIRadioPlaybackCallback cb, void *ud) {
        std::lock_guard<std::mutex> l(callback_mutex_);
        playback_callback_ = cb; playback_user_data_ = ud;
    }

    void set_error_callback(AIRadioErrorCallback cb, void *ud) {
        std::lock_guard<std::mutex> l(callback_mutex_);
        error_callback_ = cb; error_user_data_ = ud;
    }

    const char *last_error() const noexcept { return last_error_.c_str(); }

    void destroy() {
        if (loop_) {
            pw_thread_loop_lock(loop_);
            if (stream_) {
                pw_stream_set_active(stream_, false);
                pw_stream_destroy(stream_);
                stream_ = nullptr;
            }
            idle_buffer_ = nullptr;
            pw_thread_loop_unlock(loop_);
            pw_thread_loop_stop(loop_);
            pw_thread_loop_destroy(loop_);
            loop_ = nullptr;
        }

        {
            std::lock_guard<std::mutex> l(completion_mutex_);
            stop_completion_thread_ = true;
        }
        completion_cv_.notify_all();
        if (completion_thread_.joinable()) completion_thread_.join();

        generations_.clear();
        producer_ = tail_generation_ = active_generation_ = nullptr;
        started_ = false;
        pw_deinit();
    }

private:
    static size_t next(const Generation &g, size_t i) {
        return (i + 1) % g.blocks.size();
    }

    static bool is_empty(const Generation &g) noexcept {
        return g.tail == g.head;
    }

    static bool is_full(const Generation &g) noexcept {
        return next(g, g.head) == g.tail;
    }

    static size_t distance(const Generation &g, size_t from, size_t to) noexcept {
        return to >= from ? to - from : g.blocks.size() - from + to;
    }

    static size_t queued(const Generation &g) noexcept {
        return distance(g, g.tail, g.head);
    }

    static size_t outstanding(const Generation &g) noexcept {
        return distance(g, g.tail, g.active);
    }

    static size_t ready(const Generation &g) noexcept {
        return distance(g, g.active, g.head);
    }

    void create_generation() {
        size_t capacity = generations_.empty()
            ? kInitialBlocks
            : generations_.back()->blocks.size() * 2;
        auto g = std::make_unique<Generation>(capacity, block_bytes_);
        Generation *raw = g.get();
        if (!generations_.empty()) generations_.back()->next = raw;
        generations_.push_back(std::move(g));
        producer_ = raw;
        if (!tail_generation_) tail_generation_ = raw;
        if (!active_generation_) active_generation_ = raw;
    }

    void discard_ready() {
        // Keep blocks already submitted to PipeWire. Discard everything
        // from active through head in every generation. After this, the
        // newest generation is the producer and has no ready data.
        for (Generation *g = active_generation_; g; g = g->next) {
            g->head = g->active;
            if (g == producer_) break;
        }
        if (producer_)
            producer_->head = producer_->active;
    }

    size_t queued_blocks() const noexcept {
        size_t n = 0;
        for (Generation *g = tail_generation_; g; g = g->next)
            n += queued(*g) - outstanding(*g);
        return n;
    }

    bool any_ready() const noexcept {
        for (Generation *g = active_generation_; g; g = g->next)
            if (ready(*g) != 0) return true;
        return false;
    }

    Generation *find_ready_generation() {
        for (Generation *g = active_generation_; g; g = g->next) {
            if (ready(*g) != 0)
                return g;
        }
        return nullptr;
    }

    Generation *find_outstanding_generation() {
        for (Generation *g = tail_generation_; g; g = g->next) {
            if (outstanding(*g) != 0)
                return g;
        }
        return nullptr;
    }

    void service_pipewire() {
        if (!stream_) return;

        if (any_ready() && !active_) {
            int r = pw_stream_set_active(stream_, true);
            if (r < 0) {
                last_error_ = "pw_stream_set_active failed: " + std::to_string(r);
                queue_error_callback(r, last_error_);
                return;
            }
            active_ = true;
        }

        if (idle_buffer_ && pipewire_active_count_ < kPipeWireBuffers && any_ready()) {
            pw_buffer *b = idle_buffer_;
            idle_buffer_ = nullptr;
            submit(b);
        }
    }

    void submit(pw_buffer *buffer) {
        Generation *g = find_ready_generation();
        if (!g) {
            idle_buffer_ = buffer;
            return;
        }

        if (!buffer || !buffer->buffer) return;
        spa_buffer *sb = buffer->buffer;
        if (!sb->n_datas || !sb->datas) {
            pw_stream_return_buffer(stream_, buffer);
            return;
        }

        spa_data *d = &sb->datas[0];
        if (!d->data || !d->chunk || d->maxsize < block_bytes_) {
            pw_stream_return_buffer(stream_, buffer);
            queue_error_callback(-5, "PipeWire buffer is smaller than a 100 ms PCM block");
            return;
        }

        PcmBlock *block = g->blocks[g->active].get();
        std::memcpy(d->data, block->data.get(), block_bytes_);
        d->chunk->offset = 0;
        d->chunk->stride = static_cast<int32_t>(bytes_per_frame_);
        d->chunk->size = static_cast<uint32_t>(block_bytes_);
        buffer->size = block_frames_;
        buffer->user_data = block;

        g->active = next(*g, g->active);
        ++pipewire_active_count_;
        outstanding_frames_.fetch_add(block_frames_, std::memory_order_release);

        int r = pw_stream_queue_buffer(stream_, buffer);
        if (r < 0) {
            // Restore active because the block was not accepted by PipeWire.
            g->active = (g->active + g->blocks.size() - 1) % g->blocks.size();
            --pipewire_active_count_;
            uint64_t current = outstanding_frames_.load(std::memory_order_relaxed);
            outstanding_frames_.store(
                current >= block_frames_ ? current - block_frames_ : 0,
                std::memory_order_release);
            queue_error_callback(r, "pw_stream_queue_buffer failed");
        }
    }

    void process() {
        pw_buffer *buffer = pw_stream_dequeue_buffer(stream_);
        if (!buffer || !buffer->buffer) return;

        auto *completed = static_cast<PcmBlock *>(buffer->user_data);
        if (completed) {
            if (pipewire_active_count_) --pipewire_active_count_;
            uint64_t current = outstanding_frames_.load(std::memory_order_relaxed);
            outstanding_frames_.store(
                current >= block_frames_ ? current - block_frames_ : 0,
                std::memory_order_release);
            complete_tail(completed);
        }

        submit(buffer);
        reclaim();
        maybe_complete();
    }

    void complete_tail(PcmBlock *block) {
        Generation *g = find_outstanding_generation();
        if (!g) return;

        // PipeWire returns buffers in playback order. The oldest generation
        // with an outstanding block therefore owns the completed block.
        PcmBlock *expected = g->blocks[g->tail].get();
        if (block && block != expected) {
            last_error_ = "PipeWire completed an unexpected PCM block";
            queue_error_callback(-5, last_error_);
            return;
        }

        g->tail = next(*g, g->tail);
    }

    void reclaim() {
        while (generations_.size() > 1 && tail_generation_) {
            if (!is_empty(*tail_generation_)) break;

            Generation *old = tail_generation_;
            tail_generation_ = old->next;
            if (active_generation_ == old) active_generation_ = tail_generation_;
            if (producer_ == old) producer_ = tail_generation_;
            generations_.pop_front();
        }
    }

    bool unprocessed() const noexcept {
        for (Generation *g = tail_generation_; g; g = g->next) {
            if (!is_empty(*g)) return true;
        }
        return false;
    }

    void maybe_complete() {
        if (!end_of_utterance_) return;
        if (pipewire_active_count_ != 0 || unprocessed()) return;

        if (active_) {
            pw_stream_set_active(stream_, false);
            active_ = false;
        }

        if (!completion_pending_.exchange(true, std::memory_order_acq_rel))
            completion_cv_.notify_one();
    }

    static void on_state_changed(
        void *data, pw_stream_state, pw_stream_state state, const char *error) {
        auto *self = static_cast<PipeWireBackend *>(data);
        if (state == PW_STREAM_STATE_PAUSED || state == PW_STREAM_STATE_STREAMING) {
            self->connection_ready_ = true;
            pw_thread_loop_signal(self->loop_, false);
        } else if (state == PW_STREAM_STATE_ERROR) {
            self->connection_error_ = -5;
            self->last_error_ = error ? error : "PipeWire stream entered an error state";
            self->queue_error_callback(-5, self->last_error_);
            pw_thread_loop_signal(self->loop_, false);
        }
    }

    static void on_process(void *data) {
        static_cast<PipeWireBackend *>(data)->process();
    }

    static void on_drained(void *) {}

    void completion_worker() {
        for (;;) {
            std::unique_lock<std::mutex> l(completion_mutex_);
            completion_cv_.wait(l, [this] {
                return stop_completion_thread_ ||
                       completion_pending_.load(std::memory_order_acquire);
            });
            if (stop_completion_thread_) return;
            completion_pending_.store(false, std::memory_order_release);
            l.unlock();

            /*
             * The completion callback marks the boundary between utterances.
             * Reset the native end-of-utterance barrier before invoking the
             * host callback so the host may immediately enqueue the next
             * utterance from its completion handler.
             */
            if (loop_) {
                pw_thread_loop_lock(loop_);
                end_of_utterance_ = false;
                cancelled_ = false;
                pw_thread_loop_unlock(loop_);
            }

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

    void queue_error_callback(int code, const std::string &message) {
        AIRadioErrorCallback cb;
        void *ud;
        {
            std::lock_guard<std::mutex> l(callback_mutex_);
            cb = error_callback_;
            ud = error_user_data_;
        }
        if (cb) cb(ud, code, message.c_str());
    }

    uint32_t sample_rate_, channels_, bits_per_sample_, bytes_per_frame_, block_frames_;
    size_t block_bytes_;
    std::deque<std::unique_ptr<Generation>> generations_;
    Generation *producer_ = nullptr, *tail_generation_ = nullptr, *active_generation_ = nullptr;
    size_t head_fill_bytes_ = 0;
    size_t pipewire_active_count_ = 0;
    pw_buffer *idle_buffer_ = nullptr;
    pw_thread_loop *loop_ = nullptr;
    pw_stream *stream_ = nullptr;
    bool started_ = false, active_ = false, end_of_utterance_ = false, cancelled_ = false;
    bool connection_ready_ = false;
    int connection_error_ = 0;
    std::atomic<uint64_t> outstanding_frames_{0};
    std::atomic<float> volume_{1.0f};
    std::string last_error_;
    std::thread completion_thread_;
    std::mutex completion_mutex_;
    std::condition_variable completion_cv_;
    bool stop_completion_thread_ = false;
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
AIRADIO_API AIRadioPipeWire *airadio_pw_create(uint32_t r, uint32_t c, uint32_t b) {
    try { return new AIRadioPipeWire(r, c, b); } catch (...) { return nullptr; }
}
AIRADIO_API int airadio_pw_start(AIRadioPipeWire *c)
{
    if (!c)
        return -22;

    try
    {
        return c->backend.start();
    }
    catch (...)
    {
        return -5;
    }
}
AIRADIO_API int airadio_pw_enqueue(
    AIRadioPipeWire *c,
    const AIRadioPcmSegment *s,
    size_t n)
{
    if (!c)
        return -22;

    try
    {
        return c->backend.enqueue(s, n);
    }
    catch (...)
    {
        return -5;
    }
}
AIRADIO_API int airadio_pw_end_utterance(AIRadioPipeWire *c, int cancel)
{
    if (!c)
        return -22;

    try
    {
        return c->backend.end_utterance(cancel != 0);
    }
    catch (...)
    {
        return -5;
    }
}
AIRADIO_API int airadio_pw_clear(AIRadioPipeWire *c)
{
    if (!c)
        return -22;

    try
    {
        return c->backend.clear();
    }
    catch (...)
    {
        return -5;
    }
}
AIRADIO_API int airadio_pw_set_volume(AIRadioPipeWire *c, float v)
{
    if (!c)
        return -22;

    try
    {
        return c->backend.set_volume(v);
    }
    catch (...)
    {
        return -5;
    }
}
AIRADIO_API float airadio_pw_get_volume(AIRadioPipeWire *c) {
    return c ? c->backend.volume() : 0.0f;
}
AIRADIO_API uint64_t airadio_pw_queued_frames(AIRadioPipeWire *c) {
    return c ? c->backend.queued_frames() : 0;
}
AIRADIO_API uint64_t airadio_pw_outstanding_frames(AIRadioPipeWire *c) {
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
AIRADIO_API const char *airadio_pw_last_error(AIRadioPipeWire *c) {
    return c ? c->backend.last_error() : "invalid context";
}
AIRADIO_API void airadio_pw_destroy(AIRadioPipeWire *c) {
    delete c;
}
}
