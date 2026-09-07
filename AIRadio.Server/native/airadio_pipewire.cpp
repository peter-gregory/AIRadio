#include "airadio_pipewire.h"

#include <pipewire/pipewire.h>
#include <pipewire/keys.h>
#include <pipewire/thread-loop.h>

#include <spa/buffer/buffer.h>
#include <spa/param/param.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/pod/builder.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
constexpr size_t kInitialRingBytes = 256 * 1024;
constexpr size_t kPodBufferBytes = 1024;
constexpr uint32_t kPlaybackLatencyFrames = 4800; // 100 ms @ 48 kHz

class PcmRing
{
public:
    explicit PcmRing(size_t initial_capacity)
    {
        grow_to(std::max(initial_capacity, size_t(4096)));
    }

    size_t size() const noexcept { return size_; }
    bool empty() const noexcept { return size_ == 0; }

    void clear() noexcept
    {
        read_pos_ = 0;
        write_pos_ = 0;
        size_ = 0;
    }

    bool append(const uint8_t *data, size_t bytes)
    {
        if (bytes == 0)
            return true;

        if (bytes > capacity_ - size_)
            grow_to(required_capacity(bytes));

        const size_t first = std::min(bytes, capacity_ - write_pos_);
        std::memcpy(storage_.data() + write_pos_, data, first);

        const size_t second = bytes - first;
        if (second != 0)
            std::memcpy(storage_.data(), data + first, second);

        write_pos_ = (write_pos_ + bytes) % capacity_;
        size_ += bytes;
        return true;
    }

    size_t read(uint8_t *destination, size_t bytes)
    {
        const size_t count = std::min(bytes, size_);
        if (count == 0)
            return 0;

        const size_t first = std::min(count, capacity_ - read_pos_);
        std::memcpy(destination, storage_.data() + read_pos_, first);

        const size_t second = count - first;
        if (second != 0)
            std::memcpy(destination + first, storage_.data(), second);

        read_pos_ = (read_pos_ + count) % capacity_;
        size_ -= count;
        return count;
    }

private:
    size_t required_capacity(size_t incoming) const
    {
        size_t required = size_ + incoming;
        size_t capacity = capacity_ == 0 ? 4096 : capacity_;

        while (capacity < required)
        {
            if (capacity > (static_cast<size_t>(-1) / 2))
                return required;
            capacity *= 2;
        }

        return capacity;
    }

    void grow_to(size_t new_capacity)
    {
        if (new_capacity <= capacity_)
            return;

        std::vector<uint8_t> replacement(new_capacity);

        if (size_ != 0)
        {
            const size_t first = std::min(size_, capacity_ - read_pos_);
            std::memcpy(replacement.data(), storage_.data() + read_pos_, first);

            const size_t second = size_ - first;
            if (second != 0)
                std::memcpy(replacement.data() + first, storage_.data(), second);
        }

        storage_.swap(replacement);
        capacity_ = new_capacity;
        read_pos_ = 0;
        write_pos_ = size_;
    }

    std::vector<uint8_t> storage_;
    size_t capacity_ = 0;
    size_t read_pos_ = 0;
    size_t write_pos_ = 0;
    size_t size_ = 0;
};

class PipeWireBackend
{
public:
    PipeWireBackend(uint32_t sample_rate, uint32_t channels, uint32_t bits_per_sample)
        : sample_rate_(sample_rate),
          channels_(channels),
          bits_per_sample_(bits_per_sample),
          bytes_per_frame_(channels * (bits_per_sample / 8)),
          ring_(kInitialRingBytes)
    {
    }

    ~PipeWireBackend()
    {
        destroy();
    }

    int start()
    {
        if (started_)
            return 0;

        if (sample_rate_ == 0 || channels_ == 0 || bits_per_sample_ != 16)
            return -22; // EINVAL

        pw_init(nullptr, nullptr);

        loop_ = pw_thread_loop_new("AIRadioPipeWire", nullptr);
        if (loop_ == nullptr)
            return -12;

        pw_thread_loop_lock(loop_);

        const int loop_result = pw_thread_loop_start(loop_);
        if (loop_result < 0)
        {
            pw_thread_loop_unlock(loop_);
            return loop_result;
        }

        pw_properties *props = pw_properties_new(
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Playback",
            PW_KEY_MEDIA_ROLE, "Music",
            PW_KEY_NODE_LATENCY, "4800/48000",
            PW_KEY_NODE_MAX_LATENCY, "4800/48000",
            PW_KEY_NODE_STREAM, "true",
            nullptr);

        if (props == nullptr)
        {
            pw_thread_loop_unlock(loop_);
            return -12;
        }

        static const pw_stream_events events = {
            PW_VERSION_STREAM_EVENTS,
            nullptr,
            &PipeWireBackend::on_state_changed,
            nullptr,
            nullptr,
            nullptr,
            nullptr,
            nullptr,
            &PipeWireBackend::on_process,
            &PipeWireBackend::on_drained,
            nullptr,
            nullptr
        };

        stream_ = pw_stream_new_simple(
            pw_thread_loop_get_loop(loop_),
            "AIRadioAudioOutput",
            props,
            &events,
            this);

        if (stream_ == nullptr)
        {
            pw_thread_loop_unlock(loop_);
            return -12;
        }

        uint8_t pod_buffer[kPodBufferBytes];
        spa_pod_builder builder = SPA_POD_BUILDER_INIT(pod_buffer, sizeof(pod_buffer));

        spa_audio_info_raw audio_info = SPA_AUDIO_INFO_RAW_INIT(
            .format = SPA_AUDIO_FORMAT_S16,
            .rate = sample_rate_,
            .channels = channels_);

        const spa_pod *params[1];
        params[0] = spa_format_audio_raw_build(
            &builder,
            SPA_PARAM_EnumFormat,
            &audio_info);

        if (params[0] == nullptr)
        {
            pw_thread_loop_unlock(loop_);
            return -22;
        }

        connection_ready_ = false;
        connection_error_ = 0;
        last_error_.clear();

        const int result = pw_stream_connect(
            stream_,
            PW_DIRECTION_OUTPUT,
            PW_ID_ANY,
            static_cast<pw_stream_flags>(
                PW_STREAM_FLAG_AUTOCONNECT |
                PW_STREAM_FLAG_MAP_BUFFERS |
                PW_STREAM_FLAG_INACTIVE),
            params,
            1);

        if (result < 0)
        {
            last_error_ = "pw_stream_connect failed: " + std::to_string(result);
            pw_thread_loop_unlock(loop_);
            return result;
        }

        while (!connection_ready_ && connection_error_ == 0)
            pw_thread_loop_wait(loop_);

        if (connection_error_ < 0)
        {
            const int result = connection_error_;
            pw_thread_loop_unlock(loop_);
            return result;
        }

        pw_thread_loop_unlock(loop_);

        completion_thread_ = std::thread([this] { completion_worker(); });
        started_ = true;
        return 0;
    }

    int enqueue(const AIRadioPcmSegment *segments, size_t segment_count)
    {
        if (!started_ || stream_ == nullptr || loop_ == nullptr)
            return -107; // ENOTCONN

        if (segment_count != 0 && segments == nullptr)
            return -22;

        pw_thread_loop_lock(loop_);

        if (draining_)
        {
            pw_thread_loop_unlock(loop_);
            return -16; // EBUSY
        }

        for (size_t i = 0; i < segment_count; ++i)
        {
            if (segments[i].size == 0)
                continue;

            if (segments[i].data == nullptr)
            {
                pw_thread_loop_unlock(loop_);
                return -22;
            }

            if (!ring_.append(segments[i].data, segments[i].size))
            {
                pw_thread_loop_unlock(loop_);
                return -12;
            }
        }

        if (segment_count != 0 && !ring_.empty())
        {
            has_pending_audio_ = true;

            if (!active_)
            {
                const int result = pw_stream_set_active(stream_, true);
                if (result < 0)
                {
                    last_error_ = "pw_stream_set_active failed: " + std::to_string(result);
                    pw_thread_loop_unlock(loop_);
                    return result;
                }

                active_ = true;
            }
        }

        pw_thread_loop_unlock(loop_);
        return 0;
    }

    int clear()
    {
        if (!started_ || stream_ == nullptr || loop_ == nullptr)
            return -107;

        pw_thread_loop_lock(loop_);

        ring_.clear();
        has_pending_audio_ = false;
        completion_pending_.store(false, std::memory_order_release);
        draining_ = true;

        const int result = pw_stream_flush(stream_, true);
        if (result < 0)
        {
            draining_ = false;
            last_error_ = "pw_stream_flush(drain) failed: " + std::to_string(result);
        }

        pw_thread_loop_unlock(loop_);
        return result;
    }

    int set_volume(float volume)
    {
        if (!started_ || stream_ == nullptr || loop_ == nullptr)
            return -107;

        volume = std::clamp(volume, 0.0f, 1.0f);

        pw_thread_loop_lock(loop_);
        const int result = pw_stream_set_control(
            stream_,
            SPA_PROP_volume,
            1,
            &volume,
            nullptr);
        if (result >= 0)
            volume_ = volume;
        else
            last_error_ = "pw_stream_set_control(volume) failed: " + std::to_string(result);
        pw_thread_loop_unlock(loop_);

        return result;
    }

    uint64_t queued_frames() const noexcept
    {
        if (bytes_per_frame_ == 0)
            return 0;

        return static_cast<uint64_t>(ring_.size() / bytes_per_frame_);
    }

    uint64_t outstanding_frames() const noexcept
    {
        return outstanding_frames_.load(std::memory_order_acquire);
    }

    float volume() const noexcept
    {
        return volume_.load(std::memory_order_acquire);
    }

    void set_playback_callback(AIRadioPlaybackCallback callback, void *user_data)
    {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        playback_callback_ = callback;
        playback_user_data_ = user_data;
    }

    void set_error_callback(AIRadioErrorCallback callback, void *user_data)
    {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        error_callback_ = callback;
        error_user_data_ = user_data;
    }

    const char *last_error() const noexcept
    {
        return last_error_.c_str();
    }

    void destroy()
    {
        if (!loop_)
        {
            if (started_)
                pw_deinit();
            started_ = false;
            return;
        }

        pw_thread_loop_lock(loop_);

        if (stream_)
        {
            pw_stream_set_active(stream_, false);
            pw_stream_destroy(stream_);
            stream_ = nullptr;
        }

        pw_thread_loop_unlock(loop_);

        pw_thread_loop_stop(loop_);
        pw_thread_loop_destroy(loop_);
        loop_ = nullptr;

        {
            std::lock_guard<std::mutex> lock(completion_mutex_);
            stop_completion_thread_ = true;
        }
        completion_cv_.notify_all();

        if (completion_thread_.joinable())
            completion_thread_.join();

        started_ = false;
        pw_deinit();
    }

private:
    static void on_state_changed(
        void *data,
        pw_stream_state /*old_state*/,
        pw_stream_state state,
        const char *error)
    {
        auto *self = static_cast<PipeWireBackend *>(data);

        if (state == PW_STREAM_STATE_PAUSED ||
            state == PW_STREAM_STATE_STREAMING)
        {
            self->connection_ready_ = true;
            pw_thread_loop_signal(self->loop_, false);
            return;
        }

        if (state == PW_STREAM_STATE_ERROR)
        {
            self->connection_error_ = -5;
            self->last_error_ = error ? error : "PipeWire stream entered an error state";
            self->connection_ready_ = false;

            self->queue_error_callback(-5, self->last_error_);
            pw_thread_loop_signal(self->loop_, false);
        }
    }

    static void on_process(void *data)
    {
        auto *self = static_cast<PipeWireBackend *>(data);
        self->process();
    }

    static void on_drained(void *data)
    {
        auto *self = static_cast<PipeWireBackend *>(data);

        if (self->loop_ != nullptr && self->stream_ != nullptr)
        {
            const int result = pw_stream_set_active(self->stream_, true);
            if (result >= 0)
                self->active_ = true;
        }

        self->draining_ = false;
        self->has_pending_audio_ = false;
        self->completion_pending_.store(true, std::memory_order_release);
        self->completion_cv_.notify_one();
    }

    void process()
    {
        pw_buffer *buffer = pw_stream_dequeue_buffer(stream_);
        if (buffer == nullptr || buffer->buffer == nullptr)
            return;

        const uint64_t previously_queued =
            reinterpret_cast<uintptr_t>(buffer->user_data);

        if (previously_queued != 0)
        {
            const uint64_t current =
                outstanding_frames_.load(std::memory_order_relaxed);
            outstanding_frames_.store(
                current >= previously_queued ? current - previously_queued : 0,
                std::memory_order_release);
        }

        spa_buffer *spa_buffer = buffer->buffer;
        if (spa_buffer->n_datas == 0 || spa_buffer->datas == nullptr)
        {
            pw_stream_return_buffer(stream_, buffer);
            return;
        }

        spa_data *data = &spa_buffer->datas[0];
        if (data->data == nullptr || data->chunk == nullptr || data->maxsize == 0)
        {
            pw_stream_return_buffer(stream_, buffer);
            return;
        }

        const uint32_t stride = bytes_per_frame_;
        const uint32_t capacity_bytes = data->maxsize;
        uint64_t requested_frames = buffer->requested;

        if (requested_frames == 0)
            requested_frames = capacity_bytes / stride;

        const uint64_t capacity_frames = capacity_bytes / stride;
        const uint64_t frames = std::min(requested_frames, capacity_frames);
        const size_t requested_bytes = static_cast<size_t>(frames * stride);

        size_t copied = 0;
        if (!ring_.empty())
            copied = ring_.read(
                static_cast<uint8_t *>(data->data),
                requested_bytes);

        if (copied < requested_bytes)
            std::memset(
                static_cast<uint8_t *>(data->data) + copied,
                0,
                requested_bytes - copied);

        data->chunk->offset = 0;
        data->chunk->stride = static_cast<int32_t>(stride);
        data->chunk->size = static_cast<uint32_t>(requested_bytes);

        const bool submitted_audio = copied != 0;
        const uint64_t submitted_frames =
            submitted_audio ? frames : 0;

        buffer->size = submitted_frames;
        buffer->user_data = reinterpret_cast<void *>(
            static_cast<uintptr_t>(submitted_frames));

        if (submitted_audio)
        {
            outstanding_frames_.fetch_add(
                submitted_frames,
                std::memory_order_release);
        }

        if (ring_.empty() && has_pending_audio_ && submitted_audio)
        {
            // The final buffer may contain zero padding. Completion is not
            // signaled until this buffer is returned by PipeWire.
            completion_candidate_ = true;
        }

        if (ring_.empty() &&
            completion_candidate_ &&
            outstanding_frames_.load(std::memory_order_acquire) == 0)
        {
            has_pending_audio_ = false;
            completion_candidate_ = false;
            completion_pending_.store(true, std::memory_order_release);
            completion_cv_.notify_one();
        }

        const int result = pw_stream_queue_buffer(stream_, buffer);
        if (result < 0)
            queue_error_callback(result, "pw_stream_queue_buffer failed");
    }

    void completion_worker()
    {
        for (;;)
        {
            {
                std::unique_lock<std::mutex> lock(completion_mutex_);
                completion_cv_.wait_for(
                    lock,
                    std::chrono::milliseconds(5),
                    [this] {
                        return stop_completion_thread_ ||
                               completion_pending_.load(std::memory_order_acquire);
                    });

                if (stop_completion_thread_)
                    return;
            }

            if (!completion_pending_.exchange(false, std::memory_order_acq_rel))
                continue;

            bool fire = false;

            if (loop_ != nullptr)
            {
                pw_thread_loop_lock(loop_);

                if (draining_)
                {
                    // The drained callback owns completion for a clear/stop.
                    pw_thread_loop_unlock(loop_);
                    continue;
                }

                if (has_pending_audio_ &&
                    ring_.empty() &&
                    outstanding_frames_.load(std::memory_order_acquire) == 0)
                {
                    has_pending_audio_ = false;
                    completion_candidate_ = false;

                    if (stream_ != nullptr && active_)
                    {
                        pw_stream_set_active(stream_, false);
                        active_ = false;
                    }

                    fire = true;
                }

                pw_thread_loop_unlock(loop_);
            }

            if (fire)
                queue_playback_callback();
        }
    }

    void queue_playback_callback()
    {
        AIRadioPlaybackCallback callback;
        void *user_data;

        {
            std::lock_guard<std::mutex> lock(callback_mutex_);
            callback = playback_callback_;
            user_data = playback_user_data_;
        }

        if (callback)
            callback(user_data);
    }

    void queue_error_callback(int error_code, const std::string &message)
    {
        AIRadioErrorCallback callback;
        void *user_data;

        {
            std::lock_guard<std::mutex> lock(callback_mutex_);
            callback = error_callback_;
            user_data = error_user_data_;
        }

        if (callback)
            callback(user_data, error_code, message.c_str());
    }

    uint32_t sample_rate_;
    uint32_t channels_;
    uint32_t bits_per_sample_;
    uint32_t bytes_per_frame_;

    PcmRing ring_;

    pw_thread_loop *loop_ = nullptr;
    pw_stream *stream_ = nullptr;

    bool started_ = false;
    bool active_ = false;
    bool draining_ = false;
    bool has_pending_audio_ = false;
    bool completion_candidate_ = false;

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

} // namespace

struct AIRadioPipeWire
{
    PipeWireBackend backend;

    AIRadioPipeWire(uint32_t sample_rate, uint32_t channels, uint32_t bits_per_sample)
        : backend(sample_rate, channels, bits_per_sample)
    {
    }
};

extern "C"
{
AIRADIO_API AIRadioPipeWire *airadio_pw_create(
    uint32_t sample_rate,
    uint32_t channels,
    uint32_t bits_per_sample)
{
    try
    {
        return new AIRadioPipeWire(sample_rate, channels, bits_per_sample);
    }
    catch (...)
    {
        return nullptr;
    }
}

AIRADIO_API int airadio_pw_start(AIRadioPipeWire *client)
{
    if (!client)
        return -22;

    try
    {
        return client->backend.start();
    }
    catch (...)
    {
        return -5;
    }
}

AIRADIO_API int airadio_pw_enqueue(
    AIRadioPipeWire *client,
    const AIRadioPcmSegment *segments,
    size_t segment_count)
{
    if (!client)
        return -22;

    try
    {
        return client->backend.enqueue(segments, segment_count);
    }
    catch (...)
    {
        return -5;
    }
}

AIRADIO_API int airadio_pw_clear(AIRadioPipeWire *client)
{
    if (!client)
        return -22;

    try
    {
        return client->backend.clear();
    }
    catch (...)
    {
        return -5;
    }
}

AIRADIO_API int airadio_pw_set_volume(
    AIRadioPipeWire *client,
    float volume)
{
    if (!client)
        return -22;

    try
    {
        return client->backend.set_volume(volume);
    }
    catch (...)
    {
        return -5;
    }
}

AIRADIO_API float airadio_pw_get_volume(AIRadioPipeWire *client)
{
    if (!client)
        return 0.0f;

    return client->backend.volume();
}

AIRADIO_API uint64_t airadio_pw_queued_frames(AIRadioPipeWire *client)
{
    if (!client)
        return 0;

    return client->backend.queued_frames();
}

AIRADIO_API uint64_t airadio_pw_outstanding_frames(AIRadioPipeWire *client)
{
    if (!client)
        return 0;

    return client->backend.outstanding_frames();
}

AIRADIO_API void airadio_pw_set_playback_complete_callback(
    AIRadioPipeWire *client,
    AIRadioPlaybackCallback callback,
    void *user_data)
{
    if (client)
        client->backend.set_playback_callback(callback, user_data);
}

AIRADIO_API void airadio_pw_set_error_callback(
    AIRadioPipeWire *client,
    AIRadioErrorCallback callback,
    void *user_data)
{
    if (client)
        client->backend.set_error_callback(callback, user_data);
}

AIRADIO_API const char *airadio_pw_last_error(AIRadioPipeWire *client)
{
    if (!client)
        return "Invalid AIRadioPipeWire handle.";

    return client->backend.last_error();
}

AIRADIO_API void airadio_pw_destroy(AIRadioPipeWire *client)
{
    delete client;
}
}
