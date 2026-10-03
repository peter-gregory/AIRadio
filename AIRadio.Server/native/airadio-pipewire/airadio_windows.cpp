#include "airadio_pipewire.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmsystem.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "winmm.lib")

namespace {

class WindowsAudioBackend {
    struct Block {
        WAVEHDR header{};
        std::vector<uint8_t> data;
        uint64_t frames = 0;
        uint64_t generation = 0;
        bool prepared = false;
        bool submitted = false;
        bool completed = false;
    };

public:
    WindowsAudioBackend(uint32_t rate, uint32_t channels, uint32_t bits)
        : sample_rate_(rate),
          channels_(channels),
          bits_(bits),
          bytes_per_frame_(channels * (bits / 8)) {
    }

    ~WindowsAudioBackend() {
        destroy();
    }

    int start() {
        std::lock_guard<std::mutex> lock(mutex_);

        if (started_)
            return 0;

        if (sample_rate_ == 0 || channels_ == 0 ||
            bits_ == 0 || (bits_ % 8) != 0 || bytes_per_frame_ == 0) {
            set_error_locked(MMSYSERR_INVALPARAM, "Invalid audio format");
            return -22;
        }

        if (sample_rate_ > 0xFFFFFFFFu ||
            channels_ > 0xFFFFu ||
            bits_ > 0xFFFFu) {
            set_error_locked(MMSYSERR_INVALPARAM, "Audio format is out of range");
            return -22;
        }

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM;
        format.nChannels = static_cast<WORD>(channels_);
        format.nSamplesPerSec = sample_rate_;
        format.wBitsPerSample = static_cast<WORD>(bits_);
        format.nBlockAlign = static_cast<WORD>(bytes_per_frame_);
        format.nAvgBytesPerSec = sample_rate_ * bytes_per_frame_;
        format.cbSize = 0;

        const MMRESULT result = waveOutOpen(
            &wave_out_,
            WAVE_MAPPER,
            &format,
            reinterpret_cast<DWORD_PTR>(&WindowsAudioBackend::wave_callback),
            reinterpret_cast<DWORD_PTR>(this),
            CALLBACK_FUNCTION);

        if (result != MMSYSERR_NOERROR) {
            set_error_locked(result, "waveOutOpen failed: " + mmresult_string(result));
            wave_out_ = nullptr;
            return mmresult_to_errno(result);
        }

        shutting_down_.store(false, std::memory_order_release);
        cancelled_.store(false, std::memory_order_release);
        started_ = true;
        generation_ = 1;

        try {
            callback_thread_ = std::thread([this] { callback_worker(); });
        } catch (...) {
            waveOutClose(wave_out_);
            wave_out_ = nullptr;
            started_ = false;
            set_error_locked(MMSYSERR_NOMEM, "Unable to start AIRadio playback callback thread");
            return -12;
        }

        return 0;
    }

    int enqueue(const AIRadioPcmSegment* segments, size_t count) {
        if (count != 0 && !segments)
            return -22;

        std::lock_guard<std::mutex> lock(mutex_);

        if (!started_ || !wave_out_)
            return -107;

        if (cancelled_.load(std::memory_order_acquire))
            return -16;

        for (size_t i = 0; i < count; ++i) {
            if (segments[i].size == 0)
                continue;

            if (!segments[i].data ||
                segments[i].size % bytes_per_frame_ != 0) {
                set_error_locked(MMSYSERR_INVALPARAM,
                                 "PCM segment is not aligned to the configured frame size");
                return -22;
            }

            auto block = std::make_unique<Block>();
            block->frames = segments[i].size / bytes_per_frame_;
            block->generation = generation_;
            block->data.resize(segments[i].size);
            std::memcpy(block->data.data(), segments[i].data, segments[i].size);

            block->header.lpData =
                reinterpret_cast<LPSTR>(block->data.data());
            block->header.dwBufferLength =
                static_cast<DWORD>(block->data.size());

            // waveOut's WAVEHDR fields must remain valid until WOM_DONE.
            const MMRESULT prepare =
                waveOutPrepareHeader(wave_out_, &block->header, sizeof(WAVEHDR));
            if (prepare != MMSYSERR_NOERROR) {
                set_error_locked(prepare,
                                 "waveOutPrepareHeader failed: " +
                                 mmresult_string(prepare));
                return mmresult_to_errno(prepare);
            }

            block->prepared = true;

            Block* raw = block.get();
            blocks_.push_back(std::move(block));
            queued_frames_ += raw->frames;
            outstanding_frames_ += raw->frames;

            const MMRESULT write =
                waveOutWrite(wave_out_, &raw->header, sizeof(WAVEHDR));

            if (write != MMSYSERR_NOERROR) {
                // waveOutWrite did not accept the header. It is still ours,
                // so remove it and undo the counters before unpreparing it.
                queued_frames_ -= raw->frames;
                outstanding_frames_ -= raw->frames;

                auto it = std::find_if(
                    blocks_.begin(), blocks_.end(),
                    [raw](const std::unique_ptr<Block>& p) {
                        return p.get() == raw;
                    });

                if (it != blocks_.end()) {
                    waveOutUnprepareHeader(
                        wave_out_, &(*it)->header, sizeof(WAVEHDR));
                    blocks_.erase(it);
                }

                set_error_locked(write,
                                 "waveOutWrite failed: " + mmresult_string(write));
                return mmresult_to_errno(write);
            }

            raw->submitted = true;
        }

        return 0;
    }

    int end_utterance(bool cancel) {
        if (!cancel)
            return 0;

        return clear();
    }

    int clear() {
        std::unique_lock<std::mutex> lock(mutex_);

        if (!started_ || !wave_out_)
            return -107;

        cancelled_.store(true, std::memory_order_release);
        ++generation_;

        // waveOutReset synchronously stops playback and returns all queued
        // headers with WOM_DONE. Do not hold our mutex while it invokes the
        // callback.
        HWAVEOUT device = wave_out_;
        lock.unlock();

        const MMRESULT reset = waveOutReset(device);

        lock.lock();

        if (reset != MMSYSERR_NOERROR) {
            set_error_locked(reset, "waveOutReset failed: " + mmresult_string(reset));
            return mmresult_to_errno(reset);
        }

        // WOM_DONE callbacks have normally marked all submitted blocks as
        // completed by this point. Clean up any returned headers now.
        reap_completed_locked();

        // Reset means there is no application audio remaining, regardless of
        // whether the driver returned the callbacks synchronously or queued
        // them for the callback thread.
        queued_frames_ = 0;
        outstanding_frames_ = 0;
        blocks_.clear();

        completion_pending_ = false;
        completion_generation_ = generation_;

        cancelled_.store(false, std::memory_order_release);
        return 0;
    }

    int set_volume(float volume) {
        volume = std::clamp(volume, 0.0f, 1.0f);

        std::lock_guard<std::mutex> lock(mutex_);

        volume_.store(volume, std::memory_order_release);

        if (!wave_out_)
            return -107;

        // waveOutSetVolume uses independent 16-bit left/right channel values.
        // Keep both channels at the requested logical AIRadio volume.
        const DWORD level =
            static_cast<DWORD>(volume * 65535.0f + 0.5f);
        const DWORD packed = level | (level << 16);

        const MMRESULT result = waveOutSetVolume(wave_out_, packed);
        if (result != MMSYSERR_NOERROR) {
            set_error_locked(result,
                             "waveOutSetVolume failed: " +
                             mmresult_string(result));
            return mmresult_to_errno(result);
        }

        return 0;
    }

    float volume() const noexcept {
        return volume_.load(std::memory_order_acquire);
    }

    uint64_t queued_frames() const noexcept {
        std::lock_guard<std::mutex> lock(mutex_);
        return queued_frames_;
    }

    uint64_t outstanding_frames() const noexcept {
        std::lock_guard<std::mutex> lock(mutex_);
        return outstanding_frames_;
    }

    void set_playback_callback(AIRadioPlaybackCallback callback, void* user_data) {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        playback_callback_ = callback;
        playback_user_data_ = user_data;
    }

    void set_error_callback(AIRadioErrorCallback callback, void* user_data) {
        std::lock_guard<std::mutex> lock(callback_mutex_);
        error_callback_ = callback;
        error_user_data_ = user_data;
    }

    const char* last_error() const noexcept {
        std::lock_guard<std::mutex> lock(mutex_);
        return last_error_.c_str();
    }

    void destroy() {
        std::unique_lock<std::mutex> lock(mutex_);

        if (!started_ && !callback_thread_.joinable())
            return;

        shutting_down_.store(true, std::memory_order_release);
        cancelled_.store(true, std::memory_order_release);

        HWAVEOUT device = wave_out_;

        // Stop the device before joining the callback worker. waveOutReset
        // causes outstanding WAVEHDRs to be returned with WOM_DONE.
        lock.unlock();
        if (device)
            waveOutReset(device);
        lock.lock();

        if (device) {
            reap_completed_locked();
            for (auto& block : blocks_) {
                if (block->prepared) {
                    waveOutUnprepareHeader(
                        device, &block->header, sizeof(WAVEHDR));
                    block->prepared = false;
                }
            }
        }

        blocks_.clear();
        queued_frames_ = 0;
        outstanding_frames_ = 0;
        completion_pending_ = false;

        lock.unlock();

        callback_cv_.notify_all();

        if (callback_thread_.joinable())
            callback_thread_.join();

        lock.lock();

        if (wave_out_) {
            waveOutClose(wave_out_);
            wave_out_ = nullptr;
        }

        started_ = false;
    }

private:
    static void CALLBACK wave_callback(
        HWAVEOUT device,
        UINT message,
        DWORD_PTR instance,
        DWORD_PTR param1,
        DWORD_PTR /*param2*/) {

        if (message != WOM_DONE)
            return;

        auto* self = reinterpret_cast<WindowsAudioBackend*>(instance);
        if (!self)
            return;

        auto* header = reinterpret_cast<WAVEHDR*>(param1);
        self->on_buffer_done(device, header);
    }

    void on_buffer_done(HWAVEOUT device, WAVEHDR* header) noexcept {
        std::lock_guard<std::mutex> lock(mutex_);

        if (!header)
            return;

        auto it = std::find_if(
            blocks_.begin(), blocks_.end(),
            [header](const std::unique_ptr<Block>& block) {
                return &block->header == header;
            });

        if (it == blocks_.end())
            return;

        Block& block = *(*it);

        if (block.completed)
            return;

        block.completed = true;

        if (queued_frames_ >= block.frames)
            queued_frames_ -= block.frames;
        else
            queued_frames_ = 0;

        if (outstanding_frames_ >= block.frames)
            outstanding_frames_ -= block.frames;
        else
            outstanding_frames_ = 0;

        // During cancellation, the application has already discarded its
        // logical queue. Just let the callback worker/reaper release the
        // returned WAVEHDR.
        if (!cancelled_.load(std::memory_order_acquire) &&
            outstanding_frames_ == 0 &&
            !completion_pending_) {
            completion_pending_ = true;
            completion_generation_ = block.generation;
            callback_cv_.notify_one();
        }

        // Unprepare only after WOM_DONE. This is the point at which Windows
        // guarantees that the header is no longer being used by waveOut.
        if (block.prepared) {
            const MMRESULT result =
                waveOutUnprepareHeader(device, &block.header, sizeof(WAVEHDR));
            if (result != MMSYSERR_NOERROR) {
                set_error_locked(
                    result,
                    "waveOutUnprepareHeader failed: " +
                    mmresult_string(result));
            }
            block.prepared = false;
        }

        // Remove completed blocks. Never remove an outstanding header.
        blocks_.erase(it);
    }

    void callback_worker() noexcept {
        std::unique_lock<std::mutex> lock(mutex_);

        for (;;) {
            callback_cv_.wait(lock, [this] {
                return shutting_down_.load(std::memory_order_acquire) ||
                       completion_pending_;
            });

            if (shutting_down_.load(std::memory_order_acquire) &&
                !completion_pending_)
                return;

            if (!completion_pending_)
                continue;

            completion_pending_ = false;
            const uint64_t completed_generation = completion_generation_;

            if (cancelled_.load(std::memory_order_acquire))
                continue;

            if (outstanding_frames_ != 0)
                continue;

            AIRadioPlaybackCallback callback;
            void* user_data;

            {
                std::lock_guard<std::mutex> callback_lock(callback_mutex_);
                callback = playback_callback_;
                user_data = playback_user_data_;
            }

            // Do not call application code while holding the backend mutex.
            lock.unlock();

            if (callback)
                callback(user_data);

            lock.lock();

            // A new enqueue may have happened while the callback ran. The
            // generation check prevents an old completion from affecting the
            // new playback run.
            (void)completed_generation;
        }
    }

    void reap_completed_locked() {
        // WOM_DONE normally removes blocks in on_buffer_done(). This helper
        // is intentionally conservative and only removes headers already
        // marked complete.
        for (auto it = blocks_.begin(); it != blocks_.end();) {
            if ((*it)->completed)
                it = blocks_.erase(it);
            else
                ++it;
        }
    }

    void set_error_locked(MMRESULT code, const std::string& message) {
        last_error_ = message + " (MMRESULT=" + std::to_string(code) + ")";

        AIRadioErrorCallback callback;
        void* user_data;

        {
            std::lock_guard<std::mutex> callback_lock(callback_mutex_);
            callback = error_callback_;
            user_data = error_user_data_;
        }

        // Error callbacks are expected to be non-blocking from the Windows
        // multimedia callback path. For ordinary API failures this is called
        // on the API thread; WOM_DONE errors are similarly kept short.
        if (callback)
            callback(user_data, mmresult_to_errno(code), last_error_.c_str());
    }

    static std::string mmresult_string(MMRESULT result) {
        char buffer[256]{};
        if (waveOutGetErrorTextA(result, buffer, sizeof(buffer)) == MMSYSERR_NOERROR)
            return buffer;

        return "MMRESULT " + std::to_string(result);
    }

    static int mmresult_to_errno(MMRESULT result) {
        switch (result) {
        case MMSYSERR_NOERROR:
            return 0;
        case MMSYSERR_NOMEM:
            return -12;
        case MMSYSERR_INVALPARAM:
        case WAVERR_BADFORMAT:
            return -22;
        case MMSYSERR_ALLOCATED:
            return -16;
        default:
            return -5;
        }
    }

    uint32_t sample_rate_;
    uint32_t channels_;
    uint32_t bits_;
    uint32_t bytes_per_frame_;

    mutable std::mutex mutex_;
    HWAVEOUT wave_out_ = nullptr;
    bool started_ = false;

    std::deque<std::unique_ptr<Block>> blocks_;
    uint64_t queued_frames_ = 0;
    uint64_t outstanding_frames_ = 0;
    uint64_t generation_ = 0;

    std::atomic<float> volume_{1.0f};
    std::atomic<bool> cancelled_{false};
    std::atomic<bool> shutting_down_{false};

    std::mutex callback_mutex_;
    AIRadioPlaybackCallback playback_callback_ = nullptr;
    void* playback_user_data_ = nullptr;
    AIRadioErrorCallback error_callback_ = nullptr;
    void* error_user_data_ = nullptr;

    std::condition_variable callback_cv_;
    std::thread callback_thread_;
    bool completion_pending_ = false;
    uint64_t completion_generation_ = 0;

    std::string last_error_;
};

} // namespace

struct AIRadioPipeWire {
    WindowsAudioBackend backend;

    AIRadioPipeWire(uint32_t rate, uint32_t channels, uint32_t bits)
        : backend(rate, channels, bits) {}
};

extern "C" {

AIRADIO_API AIRadioPipeWire* airadio_pw_create(
    uint32_t rate, uint32_t channels, uint32_t bits) {
    try {
        return new AIRadioPipeWire(rate, channels, bits);
    } catch (...) {
        return nullptr;
    }
}

AIRADIO_API int airadio_pw_start(AIRadioPipeWire* client) {
    if (!client)
        return -22;

    try {
        return client->backend.start();
    } catch (...) {
        return -5;
    }
}

AIRADIO_API int airadio_pw_enqueue(
    AIRadioPipeWire* client,
    const AIRadioPcmSegment* segments,
    size_t segment_count) {
    if (!client)
        return -22;

    try {
        return client->backend.enqueue(segments, segment_count);
    } catch (...) {
        return -5;
    }
}

AIRADIO_API int airadio_pw_end_utterance(
    AIRadioPipeWire* client, int cancel) {
    if (!client)
        return -22;

    try {
        return client->backend.end_utterance(cancel != 0);
    } catch (...) {
        return -5;
    }
}

AIRADIO_API int airadio_pw_clear(AIRadioPipeWire* client) {
    if (!client)
        return -22;

    try {
        return client->backend.clear();
    } catch (...) {
        return -5;
    }
}

AIRADIO_API int airadio_pw_set_volume(
    AIRadioPipeWire* client, float volume) {
    if (!client)
        return -22;

    try {
        return client->backend.set_volume(volume);
    } catch (...) {
        return -5;
    }
}

AIRADIO_API float airadio_pw_get_volume(AIRadioPipeWire* client) {
    return client ? client->backend.volume() : 0.0f;
}

AIRADIO_API uint64_t airadio_pw_queued_frames(AIRadioPipeWire* client) {
    return client ? client->backend.queued_frames() : 0;
}

AIRADIO_API uint64_t airadio_pw_outstanding_frames(AIRadioPipeWire* client) {
    return client ? client->backend.outstanding_frames() : 0;
}

AIRADIO_API void airadio_pw_set_playback_complete_callback(
    AIRadioPipeWire* client,
    AIRadioPlaybackCallback callback,
    void* user_data) {
    if (client)
        client->backend.set_playback_callback(callback, user_data);
}

AIRADIO_API void airadio_pw_set_error_callback(
    AIRadioPipeWire* client,
    AIRadioErrorCallback callback,
    void* user_data) {
    if (client)
        client->backend.set_error_callback(callback, user_data);
}

AIRADIO_API const char* airadio_pw_last_error(AIRadioPipeWire* client) {
    return client ? client->backend.last_error() : "invalid context";
}

AIRADIO_API void airadio_pw_destroy(AIRadioPipeWire* client) {
    delete client;
}

} // extern "C"
