#include <pipewire/pipewire.h>
#include <spa/param/audio/format-utils.h>
#include <spa/param/audio/raw.h>

#include <curl/curl.h>
#include <sherpa-onnx/c-api/cxx-api.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <csignal>
#include <cstdint>
#include <cstring>
#include <deque>
#include <iostream>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <utility>

namespace {

// -----------------------------------------------------------------------------
// Audio / processing constants.
// -----------------------------------------------------------------------------

constexpr int kRecognizerSampleRate = 16000;

constexpr size_t kAudioFrameSampleCount = 1600;   // 100 ms @ 16 kHz
constexpr size_t kVadWindowSize = 512;   // 32 ms @ 16 kHz
constexpr size_t kAudioRingFrameCount = 128;  // 12.8 seconds

// -----------------------------------------------------------------------------
// Default VAD configuration.
// -----------------------------------------------------------------------------

constexpr float kDefaultVadThreshold = 0.5f;
constexpr float kDefaultVadMinSilenceDuration = 0.1f;
constexpr float kDefaultVadMinSpeechDuration = 0.25f;
constexpr float kDefaultVadMaxSpeechDuration = 30.0f;

// AIRadio's own paragraph-style endpoint detector.
// The active utterance is finalized only after this much continuous silence.
constexpr float kDefaultEndSilenceDuration = 1.5f;
constexpr uint32_t kDefaultVadPreRollMs = 800;

// -----------------------------------------------------------------------------
// HTTP configuration.
// -----------------------------------------------------------------------------

constexpr bool kEnableAudioDiagnostics = true;

constexpr size_t kHttpQueueLimit = 16;
constexpr long kHttpConnectTimeoutMs = 300;
constexpr long kHttpTotalTimeoutMs = 1000;

// -----------------------------------------------------------------------------
// Application configuration.
// -----------------------------------------------------------------------------

struct AppConfig {
  std::string data_dir = ".";
  std::string pipewire_source = "radio-aec-source";
  std::string post_url = "http://localhost:5200/api/radio/text";

  // VAD configuration.
  float vad_threshold = kDefaultVadThreshold;
  float vad_min_silence_duration = kDefaultVadMinSilenceDuration;
  float vad_min_speech_duration = kDefaultVadMinSpeechDuration;
  float vad_max_speech_duration = kDefaultVadMaxSpeechDuration;
  uint32_t vad_pre_roll_ms = kDefaultVadPreRollMs;

  // AIRadio paragraph endpoint.
  float end_silence_duration = kDefaultEndSilenceDuration;

  // Streaming ASR model.
  //
  // Defaults are the existing EN20M model.
  std::string encoder = "encoder-epoch-99-avg-1.int8.onnx";
  std::string decoder = "decoder-epoch-99-avg-1.onnx";
  std::string joiner = "joiner-epoch-99-avg-1.int8.onnx";
  std::string tokens = "tokens.txt";

  int num_threads = 2;

  bool trace_enabled = false;
};

AppConfig g_config;

std::atomic<bool> g_running{true};
std::mutex g_trace_mutex;

// -----------------------------------------------------------------------------
// Trace logging.
// -----------------------------------------------------------------------------

template <typename... Args>
void Trace(Args&&... args) {
  if (!g_config.trace_enabled) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_trace_mutex);

  std::cout << "[TRACE] ";
  (std::cout << ... << std::forward<Args>(args));
  std::cout << '\n';
}

// -----------------------------------------------------------------------------
// Path helpers.
// -----------------------------------------------------------------------------

std::string JoinPath(const std::string& dir, const char* file) {
  if (dir.empty() || dir == ".") {
    return file;
  }

  return dir.back() == '/' ? dir + file : dir + "/" + file;
}

std::string ModelPath(const std::string& file) {
  if (file.empty()) {
    return file;
  }

  // Absolute paths should be used unchanged.
  if (file.front() == '/') {
    return file;
  }

  return JoinPath(g_config.data_dir, file.c_str());
}

void SignalHandler(int) {
  g_running.store(false, std::memory_order_relaxed);
}

// -----------------------------------------------------------------------------
// Fixed PCM frame ring.
// -----------------------------------------------------------------------------

class PcmFrameRing {
 public:
  static constexpr uint64_t kRingCapacitySamples =
      kAudioFrameSampleCount * kAudioRingFrameCount;  // 12.8 seconds @ 16 kHz

  // The ring deliberately leaves one sample unused, just like a conventional
  // circular buffer.  The producer never blocks.  If advancing published_sample_count would
  // reach the protected consumer position, incoming samples are dropped.
  void WriteSamplesS16(
      const int16_t* src,
      size_t sample_count) {

    if (!src || sample_count == 0) {
      return;
    }

    input_samples_.fetch_add(
        sample_count,
        std::memory_order_relaxed);

    for (size_t i = 0; i < sample_count; ++i) {
      const uint64_t published_sample_count =
          published_sample_count_.load(std::memory_order_relaxed);

      const int64_t recognizer_start =
          recognizer_start_index_.load(
              std::memory_order_acquire);

      const int64_t recognizer_next_read =
          recognizer_next_read_index_.load(
              std::memory_order_acquire);

      const uint64_t vad_read_index =
          vad_read_index_.load(std::memory_order_acquire);

      uint64_t protected_index = vad_read_index;

      if (recognizer_start >= 0) {
        // Protect only audio the recognizer has not copied into its own
        // chunk buffer yet. The utterance start is metadata, not a permanent
        // retention cursor; pinning it for the full utterance fills the ring.
        const uint64_t read_index =
            recognizer_next_read >= 0
                ? static_cast<uint64_t>(recognizer_next_read)
                : static_cast<uint64_t>(recognizer_start);

        protected_index =
            std::min(protected_index, read_index);
      }

      // Advancing published_sample_count by one would make the ring full.
      if (published_sample_count - protected_index >=
          kRingCapacitySamples - 1) {

        const size_t dropped =
            sample_count - i;

        dropped_samples_.fetch_add(
            dropped,
            std::memory_order_relaxed);

        // This code runs on the PipeWire capture thread. Never log here:
        // stdout/stderr may block under systemd, and Trace takes a shared
        // mutex. The dropped-sample counter records this condition instead.

        return;
      }

      samples_[published_sample_count % kRingCapacitySamples] =
          static_cast<float>(src[i]) / 32768.0f;

      published_sample_count_.store(
          published_sample_count + 1,
          std::memory_order_release);

      published_samples_.fetch_add(
          1,
          std::memory_order_relaxed);
    }

    cv_.notify_all();
  }

  // The capture producer updates atomic sample positions without taking mutex_ so it
  // never blocks on this consumer. A notification can therefore race with a
  // waiter entering sleep; timed waits ensure a missed notification cannot
  // stall the audio pipeline indefinitely.
  // Copy the VAD window starting at vad_read_index_ without advancing it.
  // The caller advances vad_read_index only after it has processed the window.  This is
  // important when speech starts: recognizer_index can be set while the
  // detected audio is still protected by vad_read_index_.
  bool CopyVadWindow(
      float* destination,
      size_t sample_count,
      uint64_t* start_sample) {

    if (!destination ||
        sample_count == 0 ||
        !start_sample) {

      return false;
    }

    std::unique_lock<std::mutex> lock(mutex_);

    while (AvailableFromTail() < sample_count &&
           g_running.load(std::memory_order_relaxed)) {
      cv_.wait_for(lock, std::chrono::milliseconds(100));
    }

    if (AvailableFromTail() < sample_count) {
      return false;
    }

    const uint64_t start =
        vad_read_index_.load(std::memory_order_relaxed);

    CopyRingSamplesUnchecked(
        start,
        destination,
        sample_count);

    *start_sample = start;

    return true;
  }

  void AdvanceVadReadIndex(size_t sample_count) {
    if (sample_count == 0) {
      return;
    }

    const uint64_t vad_read_index =
        vad_read_index_.load(std::memory_order_relaxed);

    vad_read_index_.store(
        vad_read_index + sample_count,
        std::memory_order_release);

    consumed_samples_.fetch_add(
        sample_count,
        std::memory_order_relaxed);

    cv_.notify_all();
  }

  int64_t RecognizerStartIndex() const {
    return recognizer_start_index_.load(
        std::memory_order_acquire);
  }

  void SetRecognizerStartIndex(
      uint64_t sample_index) {

    // Initialize the moving read cursor before publishing the active
    // utterance, so the producer never sees an active recognizer_start with an
    // uninitialized retention position.
    recognizer_next_read_index_.store(
        static_cast<int64_t>(sample_index),
        std::memory_order_release);

    recognizer_start_index_.store(
        static_cast<int64_t>(sample_index),
        std::memory_order_release);

    cv_.notify_all();
  }

  // Called only after CopyPublishedSamples has copied the requested data into the
  // recognizer worker's private chunk buffer. The ring may then reuse those
  // sample slots while Sherpa processes the private copy.
  void SetRecognizerNextReadIndex(uint64_t sample_index) {
    recognizer_next_read_index_.store(
        static_cast<int64_t>(sample_index),
        std::memory_order_release);
  }

  int64_t UtteranceEnd() const {
    return utterance_end_.load(
        std::memory_order_acquire);
  }

  void SetUtteranceEnd(
      uint64_t sample_index) {

    utterance_end_.store(
        static_cast<int64_t>(sample_index),
        std::memory_order_release);

    cv_.notify_all();
  }

  void ClearRecognizerState() {
    utterance_end_.store(
        -1,
        std::memory_order_release);

    // Clear the moving cursor first. Keep the utterance identity published
    // until both state transitions are safe for the producer/VAD.
    recognizer_next_read_index_.store(
        -1,
        std::memory_order_release);

    recognizer_start_index_.store(
        -1,
        std::memory_order_release);

    cv_.notify_all();
  }

  // Wait until the requested sample is published, or shutdown occurs.
  bool WaitForPublishedSample(uint64_t sample_index) {
    std::unique_lock<std::mutex> lock(mutex_);

    while (published_sample_count_.load(std::memory_order_acquire) <= sample_index &&
           g_running.load(std::memory_order_relaxed)) {
      cv_.wait_for(lock, std::chrono::milliseconds(100));
    }

    return
        published_sample_count_.load(std::memory_order_acquire) >
        sample_index;
  }

  // Wait until the VAD publishes an active utterance start.
  bool WaitForActiveUtterance() {
    std::unique_lock<std::mutex> lock(mutex_);

    while (recognizer_start_index_.load(std::memory_order_acquire) < 0 &&
           g_running.load(std::memory_order_relaxed)) {
      cv_.wait_for(lock, std::chrono::milliseconds(100));
    }

    return
        recognizer_start_index_.load(
            std::memory_order_acquire) >= 0;
  }

  // Copy already-published samples. The producer retains unread samples
  // until this copy completes; the caller advances the read cursor only after
  // the samples are in its private chunk buffer.
  bool CopyPublishedSamples(
      uint64_t sample_index,
      float* destination,
      size_t sample_count) const {

    if (!destination ||
        sample_count == 0) {

      return false;
    }

    const uint64_t published_sample_count =
        published_sample_count_.load(std::memory_order_acquire);

    if (sample_index > published_sample_count ||
        sample_count > published_sample_count - sample_index) {

      return false;
    }

    const int64_t recognizer_start =
        recognizer_start_index_.load(
            std::memory_order_acquire);

    const int64_t recognizer_next_read =
        recognizer_next_read_index_.load(
            std::memory_order_acquire);

    const uint64_t vad_read_index =
        vad_read_index_.load(std::memory_order_acquire);

    const uint64_t oldest =
        published_sample_count > (kRingCapacitySamples - 1)
            ? published_sample_count - (kRingCapacitySamples - 1)
            : 0;

    // Validate against the same moving cursor used by the producer. The
    // fixed utterance start is retained separately for diagnostics/finalization.
    const uint64_t protected_index =
        recognizer_next_read >= 0
            ? static_cast<uint64_t>(recognizer_next_read)
            : (recognizer_start >= 0
                ? static_cast<uint64_t>(recognizer_start)
                : vad_read_index);

    const uint64_t protected_oldest =
        recognizer_start >= 0
            ? std::min(vad_read_index, protected_index)
            : vad_read_index;

    const uint64_t available_oldest =
        std::max(oldest, protected_oldest);

    if (sample_index < available_oldest) {
      return false;
    }

    for (size_t i = 0; i < sample_count; ++i) {
      destination[i] =
          samples_[
              (sample_index + i) %
              kRingCapacitySamples];
    }

    return true;
  }

  void Wake() {
    cv_.notify_all();
  }

  uint64_t InputSamples() const {
    return input_samples_.load(
        std::memory_order_relaxed);
  }

  uint64_t DroppedSamples() const {
    return dropped_samples_.load(
        std::memory_order_relaxed);
  }

  uint64_t PublishedSamples() const {
    return published_samples_.load(
        std::memory_order_relaxed);
  }

  uint64_t ConsumedSamples() const {
    return consumed_samples_.load(
        std::memory_order_relaxed);
  }

 private:
  uint64_t AvailableFromTail() const {
    const uint64_t published_sample_count =
        published_sample_count_.load(std::memory_order_acquire);

    const uint64_t vad_read_index =
        vad_read_index_.load(std::memory_order_relaxed);

    return published_sample_count >= vad_read_index
        ? published_sample_count - vad_read_index
        : 0;
  }

  void CopyRingSamplesUnchecked(
      uint64_t sample_index,
      float* destination,
      size_t sample_count) const {

    for (size_t i = 0; i < sample_count; ++i) {
      destination[i] =
          samples_[
              (sample_index + i) %
              kRingCapacitySamples];
    }
  }

  std::array<float, kRingCapacitySamples> samples_{};

  // Absolute sample positions: published_sample_count_ is the producer's next
  // write position, vad_read_index_ is the VAD consumer position, and the
  // recognizer_start indices track the active utterance and its next unread sample.
  std::atomic<uint64_t> published_sample_count_{0};
  std::atomic<uint64_t> vad_read_index_{0};

  // Fixed start sample for the active utterance. Used as identity/metadata.
  std::atomic<int64_t> recognizer_start_index_{-1};

  // Next sample the recognizer_start still needs copied from the ring. This moves
  // forward after each successful copy into the recognizer's private buffer.
  std::atomic<int64_t> recognizer_next_read_index_{-1};

  // -1 means the VAD has not detected the end of the active utterance.
  std::atomic<int64_t> utterance_end_{-1};

  mutable std::mutex mutex_;
  std::condition_variable cv_;

  std::atomic<uint64_t> input_samples_{0};
  std::atomic<uint64_t> dropped_samples_{0};
  std::atomic<uint64_t> published_samples_{0};
  std::atomic<uint64_t> consumed_samples_{0};
};

// -----------------------------------------------------------------------------
// Async HTTP poster.
// -----------------------------------------------------------------------------

class HttpPoster {
 public:
  explicit HttpPoster(std::string url)
      : url_(std::move(url)) {}

  bool Start() {
    const CURLcode rc =
        curl_global_init(CURL_GLOBAL_DEFAULT);

    if (rc != CURLE_OK) {
      std::cerr
          << "curl_global_init failed: "
          << curl_easy_strerror(rc)
          << '\n';

      return false;
    }

    curl_initialized_ = true;

    worker_ = std::thread([this] {
      Run();
    });

    return true;
  }

  void Stop() {
    Wake();

    if (worker_.joinable()) {
      worker_.join();
    }

    if (curl_initialized_) {
      curl_global_cleanup();
      curl_initialized_ = false;
    }
  }

  bool Enqueue(std::string text) {
    std::lock_guard<std::mutex> lock(mutex_);

    if (queue_.size() >= kHttpQueueLimit) {
      ++dropped_;

      Trace(
          "HTTP queue full; dropping post");

      return false;
    }

    queue_.push_back(std::move(text));
    cv_.notify_one();

    return true;
  }

  void Wake() {
    cv_.notify_all();
  }

  uint64_t Sent() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return sent_;
  }

  uint64_t Failed() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return failed_;
  }

  uint64_t Dropped() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return dropped_;
  }

 private:
  static std::string JsonEscape(
      const std::string& s) {
    std::string out;
    out.reserve(s.size() + 16);

    for (char c : s) {
      switch (c) {
        case '\"':
          out += "\\\"";
          break;

        case '\\':
          out += "\\\\";
          break;

        case '\b':
          out += "\\b";
          break;

        case '\f':
          out += "\\f";
          break;

        case '\n':
          out += "\\n";
          break;

        case '\r':
          out += "\\r";
          break;

        case '\t':
          out += "\\t";
          break;

        default:
          if (static_cast<unsigned char>(c) < 0x20) {
            out += ' ';
          } else {
            out += c;
          }
          break;
      }
    }

    return out;
  }

  bool PostText(const std::string& text) {
    CURL* curl = curl_easy_init();

    if (!curl) {
      return false;
    }

    const std::string body =
        std::string("{\"text\":\"") +
        JsonEscape(text) +
        "\"}";

    struct curl_slist* headers = nullptr;

    headers = curl_slist_append(
        headers,
        "Content-Type: application/json");

    curl_easy_setopt(
        curl,
        CURLOPT_URL,
        url_.c_str());

    curl_easy_setopt(
        curl,
        CURLOPT_POST,
        1L);

    curl_easy_setopt(
        curl,
        CURLOPT_HTTPHEADER,
        headers);

    curl_easy_setopt(
        curl,
        CURLOPT_POSTFIELDS,
        body.c_str());

    curl_easy_setopt(
        curl,
        CURLOPT_POSTFIELDSIZE,
        static_cast<long>(body.size()));

    curl_easy_setopt(
        curl,
        CURLOPT_CONNECTTIMEOUT_MS,
        kHttpConnectTimeoutMs);

    curl_easy_setopt(
        curl,
        CURLOPT_TIMEOUT_MS,
        kHttpTotalTimeoutMs);

    curl_easy_setopt(
        curl,
        CURLOPT_NOSIGNAL,
        1L);

    const CURLcode rc =
        curl_easy_perform(curl);

    long status = 0;

    curl_easy_getinfo(
        curl,
        CURLINFO_RESPONSE_CODE,
        &status);

    curl_slist_free_all(headers);
    curl_easy_cleanup(curl);

    if (rc != CURLE_OK) {
      Trace(
          "HTTP POST failed: ",
          curl_easy_strerror(rc));

      return false;
    }

    if (status < 200 || status >= 300) {
      Trace(
          "HTTP POST non-2xx status=",
          status);

      return false;
    }

    return true;
  }

  void Run() {
    while (g_running.load(
        std::memory_order_relaxed)) {

      std::string text;

      {
        std::unique_lock<std::mutex> lock(mutex_);

        cv_.wait(lock, [&] {
          return
              !queue_.empty() ||
              !g_running.load(
                  std::memory_order_relaxed);
        });

        if (queue_.empty()) {
          continue;
        }

        text = std::move(queue_.front());
        queue_.pop_front();
      }

      const bool ok = PostText(text);

      std::lock_guard<std::mutex> lock(mutex_);

      if (ok) {
        ++sent_;
      } else {
        ++failed_;
      }
    }

    // Drain anything remaining in the queue.
    for (;;) {
      std::string text;

      {
        std::lock_guard<std::mutex> lock(mutex_);

        if (queue_.empty()) {
          break;
        }

        text = std::move(queue_.front());
        queue_.pop_front();
      }

      const bool ok = PostText(text);

      std::lock_guard<std::mutex> lock(mutex_);

      if (ok) {
        ++sent_;
      } else {
        ++failed_;
      }
    }
  }

  std::string url_;

  mutable std::mutex mutex_;
  std::condition_variable cv_;

  std::deque<std::string> queue_;
  std::thread worker_;

  bool curl_initialized_ = false;

  uint64_t sent_ = 0;
  uint64_t failed_ = 0;
  uint64_t dropped_ = 0;
};

// -----------------------------------------------------------------------------
// PipeWire capture.
// -----------------------------------------------------------------------------

class PipeWireCapture {
 public:
  bool Start(PcmFrameRing* ring) {
    ring_ = ring;

    if (!ring_) {
      std::cerr
          << "PipeWireCapture: null ring buffer\n";

      return false;
    }

    pw_init(nullptr, nullptr);
    pw_initialized_ = true;

    loop_ = pw_main_loop_new(nullptr);

    if (!loop_) {
      std::cerr
          << "Failed to create PipeWire main loop\n";

      Cleanup();
      return false;
    }

    pw_loop_ =
        pw_main_loop_get_loop(loop_);

    if (!pw_loop_) {
      std::cerr
          << "Failed to get PipeWire loop\n";

      Cleanup();
      return false;
    }

    events_ = {};
    events_.version =
        PW_VERSION_STREAM_EVENTS;

    events_.state_changed =
        &PipeWireCapture::OnStateChanged;

    events_.process =
        &PipeWireCapture::OnProcess;

    props_ = pw_properties_new(
        PW_KEY_MEDIA_TYPE,
        "Audio",

        PW_KEY_MEDIA_CATEGORY,
        "Capture",

        PW_KEY_MEDIA_ROLE,
        "Communication",

        PW_KEY_NODE_NAME,
        "stt-daemon-capture",

        PW_KEY_TARGET_OBJECT,
        g_config.pipewire_source.c_str(),

        nullptr);

    if (!props_) {
      std::cerr
          << "Failed to create PipeWire properties\n";

      Cleanup();
      return false;
    }

    stream_ = pw_stream_new_simple(
        pw_loop_,
        "stt-daemon-capture",
        props_,
        &events_,
        this);

    // pw_stream_new_simple takes ownership
    // of props_ on success.
    if (stream_) {
      props_ = nullptr;
    }

    if (!stream_) {
      std::cerr
          << "Failed to create PipeWire simple stream\n";

      Cleanup();
      return false;
    }

    spa_audio_info_raw format{};

    format.format =
        SPA_AUDIO_FORMAT_S16_LE;

    format.channels = 1;
    format.rate = kRecognizerSampleRate;

    format.position[0] =
        SPA_AUDIO_CHANNEL_MONO;

    spa_pod_builder builder =
        SPA_POD_BUILDER_INIT(
            buffer_.data(),
            buffer_.size());

    const spa_pod* params[] = {
      spa_format_audio_raw_build(
          &builder,
          SPA_PARAM_EnumFormat,
          &format)
    };

    const int rc =
        pw_stream_connect(
            stream_,
            PW_DIRECTION_INPUT,
            PW_ID_ANY,
            static_cast<pw_stream_flags>(
                PW_STREAM_FLAG_AUTOCONNECT |
                PW_STREAM_FLAG_MAP_BUFFERS |
                PW_STREAM_FLAG_RT_PROCESS),
            params,
            1);

    if (rc < 0) {
      std::cerr
          << "PipeWire stream connect failed: "
          << rc
          << '\n';

      Cleanup();
      return false;
    }

    loop_thread_ = std::thread([this] {
      pw_main_loop_run(loop_);
    });

    started_ = true;

    return true;
  }

  void Stop() {
    if (!started_) {
      Cleanup();
      return;
    }

    started_ = false;

    if (loop_) {
      pw_main_loop_quit(loop_);
    }

    if (loop_thread_.joinable()) {
      loop_thread_.join();
    }

    Cleanup();
  }

 private:
  static void OnStateChanged(
      void* userdata,
      enum pw_stream_state old_state,
      enum pw_stream_state state,
      const char* error) {

    auto* self =
        static_cast<PipeWireCapture*>(userdata);

    self->HandleStateChanged(
        old_state,
        state,
        error);
  }

  static void OnProcess(void* userdata) {
    auto* self =
        static_cast<PipeWireCapture*>(userdata);

    self->Process();
  }

  void HandleStateChanged(
      enum pw_stream_state old_state,
      enum pw_stream_state state,
      const char* error) {

    (void)old_state;
    (void)state;
    (void)error;

    // PipeWire invokes this on its loop thread. Avoid synchronous stdout
    // logging here: with systemd, a full journal socket can block the loop.
    // State is intentionally not logged from this callback.
  }

  void Process() {
    if (!stream_ || !ring_) {
      return;
    }

    pw_buffer* b =
        pw_stream_dequeue_buffer(stream_);

    if (!b) {
      return;
    }

    spa_buffer* buf = b->buffer;

    if (!buf || buf->n_datas < 1) {
      pw_stream_queue_buffer(
          stream_,
          b);

      return;
    }

    spa_data& data =
        buf->datas[0];

    if (!data.data ||
        data.maxsize == 0) {

      pw_stream_queue_buffer(
          stream_,
          b);

      return;
    }

    constexpr size_t kSampleStride =
        sizeof(int16_t);

    size_t bytes =
        data.chunk
            ? data.chunk->size
            : 0;

    size_t offset =
        data.chunk
            ? data.chunk->offset
            : 0;

    if (offset >= data.maxsize) {
      bytes = 0;
    } else {
      bytes =
          std::min(
              bytes,
              data.maxsize - offset);
    }

    if (bytes >= kSampleStride) {
      const auto* raw =
          static_cast<const uint8_t*>(
              data.data);

      const size_t samples =
          bytes / kSampleStride;

      // Do not log from the PipeWire process callback. Keep this path free
      // of logger locks and stdout/stderr I/O.

      temp_samples_.resize(samples);

      for (size_t i = 0; i < samples; ++i) {
        std::memcpy(
            &temp_samples_[i],
            raw +
                offset +
                i * sizeof(int16_t),
            sizeof(int16_t));
      }

      ring_->WriteSamplesS16(
          temp_samples_.data(),
          temp_samples_.size());
    }

    pw_stream_queue_buffer(
        stream_,
        b);
  }

  void Cleanup() {
    if (stream_) {
      pw_stream_destroy(stream_);
      stream_ = nullptr;
    }

    if (props_) {
      pw_properties_free(props_);
      props_ = nullptr;
    }

    if (loop_) {
      pw_main_loop_destroy(loop_);
      loop_ = nullptr;
      pw_loop_ = nullptr;
    }

    if (pw_initialized_) {
      pw_deinit();
      pw_initialized_ = false;
    }
  }

 private:
  pw_main_loop* loop_ = nullptr;
  pw_loop* pw_loop_ = nullptr;
  pw_properties* props_ = nullptr;
  pw_stream* stream_ = nullptr;
  pw_stream_events events_{};
  std::array<uint8_t, 4096> buffer_{};
  std::vector<int16_t> temp_samples_;
  std::thread loop_thread_;

  PcmFrameRing* ring_ = nullptr;

  bool pw_initialized_ = false;
  bool started_ = false;
};

// -----------------------------------------------------------------------------
// Sherpa-ONNX model factories.
// -----------------------------------------------------------------------------

sherpa_onnx::cxx::OnlineRecognizer CreateRecognizer() {
  using namespace sherpa_onnx::cxx;

  OnlineRecognizerConfig config;

  config.feat_config.sample_rate =
      kRecognizerSampleRate;
  config.feat_config.feature_dim = 80;

  config.model_config.transducer.encoder =
      ModelPath(g_config.encoder);
  config.model_config.transducer.decoder =
      ModelPath(g_config.decoder);
  config.model_config.transducer.joiner =
      ModelPath(g_config.joiner);
  config.model_config.tokens =
      ModelPath(g_config.tokens);

  config.model_config.num_threads =
      g_config.num_threads;
  config.model_config.provider = "cpu";
  config.model_config.debug = false;

  config.decoding_method = "greedy_search";
  config.enable_endpoint = false;

  auto recognizer_start =
      OnlineRecognizer::Create(config);

  if (!recognizer_start.Get()) {
    std::cerr
        << "Failed to create Sherpa-ONNX recognizer_start\n";
  }

  return recognizer_start;
}

sherpa_onnx::cxx::VoiceActivityDetector CreateVad() {
  using namespace sherpa_onnx::cxx;

  VadModelConfig config;

  config.silero_vad.model =
      ModelPath("silero_vad.onnx");
  config.silero_vad.threshold =
      g_config.vad_threshold;
  config.silero_vad.min_silence_duration =
      g_config.vad_min_silence_duration;
  config.silero_vad.min_speech_duration =
      g_config.vad_min_speech_duration;
  config.silero_vad.max_speech_duration =
      g_config.vad_max_speech_duration;
  config.sample_rate =
      kRecognizerSampleRate;
  config.debug = false;

  auto vad =
      VoiceActivityDetector::Create(
          config,
          20.0f);

  if (!vad.Get()) {
    std::cerr
        << "Failed to create Sherpa-ONNX VAD\n";
  }

  return vad;
}

// -----------------------------------------------------------------------------

class SpeechProcessor {
 public:
  SpeechProcessor(
      PcmFrameRing* ring,
      HttpPoster* poster,
      sherpa_onnx::cxx::OnlineRecognizer recognizer_start,
      sherpa_onnx::cxx::VoiceActivityDetector vad)

      : ring_(ring),
        poster_(poster),
        recognizer_(std::move(recognizer_start)),
        vad_(std::move(vad)) {}

  void Start() {
    vad_worker_ = std::thread([this] {
      RunVad();
    });

    recognizer_worker_ = std::thread([this] {
      RunRecognizer();
    });
  }

  void Stop() {
    ring_->Wake();

    if (vad_worker_.joinable()) {
      vad_worker_.join();
    }

    if (recognizer_worker_.joinable()) {
      recognizer_worker_.join();
    }
  }

 private:
  uint64_t PreRollSamples() const {
    const double samples =
        static_cast<double>(g_config.vad_pre_roll_ms) *
        static_cast<double>(kRecognizerSampleRate) / 1000.0;

    return static_cast<uint64_t>(std::ceil(samples));
  }

  uint64_t EndSilenceSamples() const {
    const double samples =
        static_cast<double>(
            g_config.end_silence_duration) *
        static_cast<double>(
            kRecognizerSampleRate);

    return std::max<uint64_t>(
        1,
        static_cast<uint64_t>(
            std::ceil(samples)));
  }

  void RunVad() {
    std::array<float, kVadWindowSize> window{};

    while (g_running.load(
        std::memory_order_relaxed)) {

      uint64_t window_start = 0;

      if (!ring_->CopyVadWindow(
              window.data(),
              window.size(),
              &window_start)) {

        break;
      }

      vad_.AcceptWaveform(
          window.data(),
          static_cast<int32_t>(
              window.size()));

      const bool detected =
          vad_.IsDetected();

      Trace(
          "VAD window start=",
          window_start,
          " end=",
          window_start + kVadWindowSize,
          " detected=",
          detected ? "yes" : "no");

      // Keep vad_read_index at the beginning of the VAD window while deciding whether
      // this is speech.  If speech starts here, recognizer_index can safely
      // point back into this retained audio.
      if (detected &&
          !utterance_active_ &&
          ring_->RecognizerStartIndex() < 0) {

        utterance_active_ = true;
        ++utterance_id_;

        const uint64_t pre_roll =
            std::min<uint64_t>(
                PreRollSamples(),
                window_start);

        utterance_start_sample_ =
            window_start - pre_roll;

        ring_->SetRecognizerStartIndex(
            utterance_start_sample_);

        silence_samples_ = 0;

        std::cout
            << "[VAD] UTTERANCE START id="
            << utterance_id_
            << " start_sample="
            << utterance_start_sample_
            << " detected_sample="
            << window_start
            << '\n';
      }

      if (utterance_active_) {
        if (detected) {
          silence_samples_ = 0;

          Trace(
              "VAD speech resumed; "
              "silence samples reset");

        } else {
          silence_samples_ +=
              kVadWindowSize;

          const uint64_t required_samples =
              EndSilenceSamples();

          Trace(
              "VAD trailing silence samples=",
              silence_samples_,
              "/",
              required_samples);

          if (silence_samples_ >=
              required_samples) {

            const uint64_t end_sample =
                window_start +
                kVadWindowSize;

            std::cout
                << "[VAD] UTTERANCE END id="
                << utterance_id_
                << " start_sample="
                << utterance_start_sample_
                << " end_sample="
                << end_sample
                << " silence="
                << g_config.end_silence_duration
                << " sec\n";

            ring_->SetUtteranceEnd(
                end_sample);

            utterance_active_ = false;
            silence_samples_ = 0;
          }
        }
      }

      ring_->AdvanceVadReadIndex(
          kVadWindowSize);
    }
  }

  void FinalizeUtterance(
      sherpa_onnx::cxx::OnlineStream* stream,
      uint64_t start_sample,
      uint64_t end_sample) {

    if (!stream) {
      return;
    }

    Trace(
        "ASR finalize start_sample=",
        start_sample,
        " end_sample=",
        end_sample);

    stream->InputFinished();

    while (recognizer_.IsReady(stream)) {
      recognizer_.Decode(stream);
    }

    auto result =
        recognizer_.GetResult(stream);

    const std::string text =
        result.text;

    std::cout
        << "[ASR] FINAL start_sample="
        << start_sample
        << " end_sample="
        << end_sample
        << " text="
        << text
        << '\n';

    if (!text.empty()) {
      if (!poster_->Enqueue(text)) {
        std::cerr
            << "[HTTP] queue full, dropping text"
            << '\n';
      }
    }
  }

  void RunRecognizer() {
    while (g_running.load(
        std::memory_order_relaxed)) {

      const int64_t recognizer_index =
          ring_->RecognizerStartIndex();

      if (recognizer_index < 0) {
        if (!ring_->WaitForActiveUtterance()) {
          break;
        }

        continue;
      }

      const uint64_t start_sample =
          static_cast<uint64_t>(
              recognizer_index);

      auto stream =
          recognizer_.CreateStream();

      uint64_t sample_index =
          start_sample;

      bool aborted = false;

      std::array<float, kAsrChunkSamples> chunk{};

      while (g_running.load(
          std::memory_order_relaxed)) {

        const int64_t current_start =
            ring_->RecognizerStartIndex();

        if (current_start < 0) {
          aborted = true;
          break;
        }

        const int64_t end_value =
            ring_->UtteranceEnd();

        uint64_t target_end =
            std::numeric_limits<uint64_t>::max();

        if (end_value >= 0) {
          target_end =
              static_cast<uint64_t>(
                  end_value);

          if (sample_index >= target_end) {
            break;
          }
        }

        const uint64_t published_sample_count =
            ring_->PublishedSamples();

        if (sample_index >= published_sample_count) {
          if (!ring_->WaitForPublishedSample(
                  sample_index)) {

            aborted = true;
            break;
          }

          continue;
        }

        uint64_t available =
            published_sample_count - sample_index;

        if (target_end !=
            std::numeric_limits<uint64_t>::max()) {

          available =
              std::min(
                  available,
                  target_end - sample_index);
        }

        const size_t count =
            std::min<uint64_t>(
                available,
                chunk.size());

        if (count == 0) {
          continue;
        }

        if (!ring_->CopyPublishedSamples(
                sample_index,
                chunk.data(),
                count)) {

          std::cerr
              << "[ASR] ring data was overwritten; "
                 "dropping current utterance"
              << '\n';

          aborted = true;
          break;
        }

        const uint64_t chunk_start = sample_index;

        // The data is now in chunk, owned by this worker. Release the ring
        // slots before inference/decode so a long utterance cannot pin its
        // original pre-roll region and fill the ring.
        sample_index += count;
        ring_->SetRecognizerNextReadIndex(sample_index);

        stream.AcceptWaveform(
            kRecognizerSampleRate,
            chunk.data(),
            static_cast<int32_t>(
                count));

        int decode_count = 0;

        while (recognizer_.IsReady(
            &stream)) {

          recognizer_.Decode(&stream);
          ++decode_count;
        }

        Trace(
            "ASR samples=",
            chunk_start,
            "..",
            chunk_start + count,
            " decode_count=",
            decode_count);

      }

      if (aborted ||
          !g_running.load(
              std::memory_order_relaxed)) {

        break;
      }

      const int64_t end_value =
          ring_->UtteranceEnd();

      if (end_value < 0) {
        continue;
      }

      const uint64_t end_sample =
          static_cast<uint64_t>(
              end_value);

      if (sample_index < end_sample) {
        continue;
      }

      const int64_t active_start =
          ring_->RecognizerStartIndex();

      if (active_start < 0) {
        continue;
      }

      const uint64_t start =
          static_cast<uint64_t>(
              active_start);

      // The ring state is no longer needed once every sample through the
      // endpoint has been copied into the recognizer_start.
      ring_->ClearRecognizerState();

      FinalizeUtterance(
          &stream,
          start,
          end_sample);
    }
  }

  static constexpr size_t kAsrChunkSamples =
      1600;  // 100 ms

  PcmFrameRing* ring_ = nullptr;
  HttpPoster* poster_ = nullptr;

  sherpa_onnx::cxx::OnlineRecognizer recognizer_;
  sherpa_onnx::cxx::VoiceActivityDetector vad_;

  std::thread vad_worker_;
  std::thread recognizer_worker_;

  bool utterance_active_ = false;
  uint64_t utterance_id_ = 0;
  uint64_t utterance_start_sample_ = 0;
  uint64_t silence_samples_ = 0;
};

// -----------------------------------------------------------------------------
// Numeric argument helpers.
// -----------------------------------------------------------------------------

bool ParseFloat(
    const std::string& value,
    float* output) {

  if (!output || value.empty()) {
    return false;
  }

  try {
    size_t consumed = 0;

    const float parsed =
        std::stof(value, &consumed);

    if (consumed != value.size()) {
      return false;
    }

    if (!std::isfinite(parsed)) {
      return false;
    }

    *output = parsed;
    return true;

  } catch (...) {
    return false;
  }
}

bool ParsePositiveFloat(
    const std::string& value,
    float* output) {

  float parsed = 0.0f;

  if (!ParseFloat(value, &parsed) ||
      parsed <= 0.0f) {

    return false;
  }

  *output = parsed;
  return true;
}

bool ParseNonNegativeFloat(
    const std::string& value,
    float* output) {

  float parsed = 0.0f;

  if (!ParseFloat(value, &parsed) ||
      parsed < 0.0f) {

    return false;
  }

  *output = parsed;
  return true;
}

bool ParseNonNegativeInt(
    const std::string& value,
    int* output) {

  if (!output || value.empty()) {
    return false;
  }

  try {
    size_t consumed = 0;

    const long parsed =
        std::stol(value, &consumed);

    if (consumed != value.size() ||
        parsed < 0 ||
        parsed >
            std::numeric_limits<int>::max()) {

      return false;
    }

    *output =
        static_cast<int>(parsed);

    return true;

  } catch (...) {
    return false;
  }
}

bool ParsePositiveInt(
    const std::string& value,
    int* output) {

  if (!output || value.empty()) {
    return false;
  }

  try {
    size_t consumed = 0;

    const long parsed =
        std::stol(value, &consumed);

    if (consumed != value.size()) {
      return false;
    }

    if (parsed <= 0 ||
        parsed >
            std::numeric_limits<int>::max()) {

      return false;
    }

    *output =
        static_cast<int>(parsed);

    return true;

  } catch (...) {
    return false;
  }
}

// -----------------------------------------------------------------------------
// Usage.
// -----------------------------------------------------------------------------

void PrintUsage(const char* argv0) {
  std::cout
      << "Usage: " << argv0 << "\n"
      << "  [--data-dir PATH]\n"
      << "  [--pipewire-source NAME]\n"
      << "  [--post-url URL]\n"
      << "  [--vad-threshold FLOAT]\n"
      << "  [--vad-min-silence FLOAT]\n"
      << "  [--vad-min-speech FLOAT]\n"
      << "  [--vad-max-speech FLOAT]\n"
      << "  [--vad-pre-roll-ms N]\n"
      << "  [--end-silence FLOAT]\n"
      << "  [--encoder FILE]\n"
      << "  [--decoder FILE]\n"
      << "  [--joiner FILE]\n"
      << "  [--tokens FILE]\n"
      << "  [--threads N]\n"
      << "  [--trace]\n"
      << "\n"
      << "Defaults:\n"
      << "  --vad-threshold     "
      << kDefaultVadThreshold
      << "\n"
      << "  --vad-min-silence   "
      << kDefaultVadMinSilenceDuration
      << " sec\n"
      << "  --vad-min-speech    "
      << kDefaultVadMinSpeechDuration
      << " sec\n"
      << "  --vad-max-speech    "
      << kDefaultVadMaxSpeechDuration
      << " sec\n"
      << "  --vad-pre-roll-ms   "
      << kDefaultVadPreRollMs
      << " ms\n"
      << "  --end-silence       "
      << kDefaultEndSilenceDuration
      << " sec\n"
      << "  --threads            2\n"
      << "\n"
      << "Default ASR model:\n"
      << "  Encoder: "
      << g_config.encoder
      << "\n"
      << "  Decoder: "
      << g_config.decoder
      << "\n"
      << "  Joiner:  "
      << g_config.joiner
      << "\n"
      << "  Tokens:  "
      << g_config.tokens
      << "\n";
}

// -----------------------------------------------------------------------------
// Main pipeline.
// -----------------------------------------------------------------------------

int Run() {
  auto recognizer_start =
      CreateRecognizer();

  auto vad =
      CreateVad();

  PcmFrameRing ring;

  HttpPoster poster(
      g_config.post_url);

  if (!poster.Start()) {
    return EXIT_FAILURE;
  }

  SpeechProcessor processor(
      &ring,
      &poster,
      std::move(recognizer_start),
      std::move(vad));

  processor.Start();

  PipeWireCapture capture;

  if (!capture.Start(&ring)) {
    g_running.store(
        false,
        std::memory_order_relaxed);

    ring.Wake();
    poster.Wake();

    processor.Stop();
    poster.Stop();

    return EXIT_FAILURE;
  }

  const uint64_t endpoint_frames =
      std::max<uint64_t>(
          1,
          static_cast<uint64_t>(
              std::ceil(
                  g_config.end_silence_duration *
                  10.0f)));

  std::cout
      << "\n===== STT PIPELINE =====\n"

      << "Data directory:     "
      << g_config.data_dir
      << '\n'

      << "PipeWire source:    "
      << g_config.pipewire_source
      << '\n'

      << "HTTP post URL:      "
      << g_config.post_url
      << '\n'

      << "Capture rate:       "
      << kRecognizerSampleRate
      << " Hz\n"

      << "Capture format:     "
      << "S16_LE mono\n"

      << "Recognizer rate:    "
      << kRecognizerSampleRate
      << " Hz\n"

      << "Recognizer format:  "
      << "Float32 mono\n"

      << "ASR chunk size:     "
      << 1600
      << " samples / 100 ms\n"

      << "Ring capacity:      "
      << PcmFrameRing::kRingCapacitySamples
      << " samples / 12.8 sec\n"

      << "VAD window:         "
      << kVadWindowSize
      << " samples / 32 ms\n"

      << "VAD threshold:      "
      << g_config.vad_threshold
      << '\n'

      << "VAD min speech:     "
      << g_config.vad_min_speech_duration
      << " sec\n"

      << "VAD min silence:    "
      << g_config.vad_min_silence_duration
      << " sec\n"

      << "VAD max speech:     "
      << g_config.vad_max_speech_duration
      << " sec\n"

      << "VAD pre-roll:       "
      << g_config.vad_pre_roll_ms
      << " ms\n"

      << "Endpoint silence:   "
      << g_config.end_silence_duration
      << " sec / "
      << endpoint_frames
      << " frames\n"

      << "Encoder:            "
      << ModelPath(g_config.encoder)
      << '\n'

      << "Decoder:            "
      << ModelPath(g_config.decoder)
      << '\n'

      << "Joiner:             "
      << ModelPath(g_config.joiner)
      << '\n'

      << "Tokens:             "
      << ModelPath(g_config.tokens)
      << '\n'

      << "ASR threads:        "
      << g_config.num_threads
      << '\n'

      << "HTTP queue limit:   "
      << kHttpQueueLimit
      << '\n'

      << "========================\n\n";

  std::cout
      << "Speak a phrase, then pause.\n"
      << "Press Ctrl+C to stop.\n\n";

  while (g_running.load(
      std::memory_order_relaxed)) {

    std::this_thread::sleep_for(
        std::chrono::milliseconds(100));
  }

  ring.Wake();
  poster.Wake();

  processor.Stop();
  capture.Stop();
  poster.Stop();

  if (kEnableAudioDiagnostics) {
    std::cout
        << "\n===== FINAL DIAGNOSTICS =====\n"

        << "[AUDIO] input_samples="
        << ring.InputSamples()

        << " published_samples="
        << ring.PublishedSamples()

        << " consumed_samples="
        << ring.ConsumedSamples()

        << " dropped_samples="
        << ring.DroppedSamples()
        << '\n'

        << "[HTTP] sent="
        << poster.Sent()

        << " failed="
        << poster.Failed()

        << " dropped="
        << poster.Dropped()
        << '\n';
  }

  std::cout
      << "Workers stopped cleanly.\n"
      << "Test complete.\n";

  return EXIT_SUCCESS;
}

// -----------------------------------------------------------------------------
// Main.
// -----------------------------------------------------------------------------

}  // namespace

int main(int argc, char** argv) {
  // Flush each insertion so diagnostics appear promptly under systemd.
  // This is safe only because PipeWire callbacks (including capture and state
  // change) never write to stdout/stderr or call Trace; only non-real-time
  // threads log. Keep all synchronous output outside the PipeWire loop.
  std::cout << std::unitbuf;

  std::signal(SIGINT, SignalHandler);
  std::signal(SIGTERM, SignalHandler);

  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];

    // -------------------------------------------------------------------------
    // Trace.
    // -------------------------------------------------------------------------

    if (arg == "--trace") {
      g_config.trace_enabled = true;

      std::cout
          << "[TRACE] Detailed pipeline tracing ENABLED\n";

    // -------------------------------------------------------------------------
    // Data directory.
    // -------------------------------------------------------------------------

    } else if (arg == "--data-dir") {
      if (i + 1 >= argc) {
        std::cerr
            << "--data-dir requires a path\n";

        return EXIT_FAILURE;
      }

      g_config.data_dir = argv[++i];

    } else if (arg.rfind("--data-dir=", 0) == 0) {
      g_config.data_dir =
          arg.substr(
              std::strlen("--data-dir="));

    // -------------------------------------------------------------------------
    // PipeWire source.
    // -------------------------------------------------------------------------

    } else if (arg == "--pipewire-source") {
      if (i + 1 >= argc) {
        std::cerr
            << "--pipewire-source requires a name\n";

        return EXIT_FAILURE;
      }

      g_config.pipewire_source =
          argv[++i];

    } else if (
        arg.rfind("--pipewire-source=", 0) == 0) {

      g_config.pipewire_source =
          arg.substr(
              std::strlen("--pipewire-source="));

    // -------------------------------------------------------------------------
    // HTTP URL.
    // -------------------------------------------------------------------------

    } else if (arg == "--post-url") {
      if (i + 1 >= argc) {
        std::cerr
            << "--post-url requires a URL\n";

        return EXIT_FAILURE;
      }

      g_config.post_url =
          argv[++i];

    } else if (
        arg.rfind("--post-url=", 0) == 0) {

      g_config.post_url =
          arg.substr(
              std::strlen("--post-url="));

    // -------------------------------------------------------------------------
    // VAD threshold.
    // -------------------------------------------------------------------------

    } else if (arg == "--vad-threshold") {
      if (i + 1 >= argc) {
        std::cerr
            << "--vad-threshold requires a value\n";

        return EXIT_FAILURE;
      }

      if (!ParseFloat(
              argv[++i],
              &g_config.vad_threshold) ||
          g_config.vad_threshold < 0.0f ||
          g_config.vad_threshold > 1.0f) {

        std::cerr
            << "--vad-threshold must be between "
               "0.0 and 1.0\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--vad-threshold=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--vad-threshold="));

      if (!ParseFloat(
              value,
              &g_config.vad_threshold) ||
          g_config.vad_threshold < 0.0f ||
          g_config.vad_threshold > 1.0f) {

        std::cerr
            << "--vad-threshold must be between "
               "0.0 and 1.0\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // VAD minimum silence.
    // -------------------------------------------------------------------------

    } else if (arg == "--vad-min-silence") {
      if (i + 1 >= argc) {
        std::cerr
            << "--vad-min-silence requires a value\n";

        return EXIT_FAILURE;
      }

      if (!ParseNonNegativeFloat(
              argv[++i],
              &g_config.vad_min_silence_duration)) {

        std::cerr
            << "--vad-min-silence must be "
               "a non-negative number\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--vad-min-silence=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--vad-min-silence="));

      if (!ParseNonNegativeFloat(
              value,
              &g_config.vad_min_silence_duration)) {

        std::cerr
            << "--vad-min-silence must be "
               "a non-negative number\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // VAD minimum speech.
    // -------------------------------------------------------------------------

    } else if (arg == "--vad-min-speech") {
      if (i + 1 >= argc) {
        std::cerr
            << "--vad-min-speech requires a value\n";

        return EXIT_FAILURE;
      }

      if (!ParsePositiveFloat(
              argv[++i],
              &g_config.vad_min_speech_duration)) {

        std::cerr
            << "--vad-min-speech must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--vad-min-speech=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--vad-min-speech="));

      if (!ParsePositiveFloat(
              value,
              &g_config.vad_min_speech_duration)) {

        std::cerr
            << "--vad-min-speech must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // VAD maximum speech.
    // -------------------------------------------------------------------------

    } else if (arg == "--vad-max-speech") {
      if (i + 1 >= argc) {
        std::cerr
            << "--vad-max-speech requires a value\n";

        return EXIT_FAILURE;
      }

      if (!ParsePositiveFloat(
              argv[++i],
              &g_config.vad_max_speech_duration)) {

        std::cerr
            << "--vad-max-speech must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--vad-max-speech=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--vad-max-speech="));

      if (!ParsePositiveFloat(
              value,
              &g_config.vad_max_speech_duration)) {

        std::cerr
            << "--vad-max-speech must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // VAD pre-roll.
    // -------------------------------------------------------------------------

    } else if (arg == "--vad-pre-roll-ms") {
      if (i + 1 >= argc) {
        std::cerr
            << "--vad-pre-roll-ms requires a value\n";

        return EXIT_FAILURE;
      }

      int value = 0;
      if (!ParseNonNegativeInt(argv[++i], &value)) {
        std::cerr
            << "--vad-pre-roll-ms must be a non-negative integer\n";

        return EXIT_FAILURE;
      }

      g_config.vad_pre_roll_ms = static_cast<uint32_t>(value);

    } else if (
        arg.rfind("--vad-pre-roll-ms=", 0) == 0) {

      int value = 0;
      const std::string text =
          arg.substr(std::strlen("--vad-pre-roll-ms="));

      if (!ParseNonNegativeInt(text, &value)) {
        std::cerr
            << "--vad-pre-roll-ms must be a non-negative integer\n";

        return EXIT_FAILURE;
      }

      g_config.vad_pre_roll_ms = static_cast<uint32_t>(value);

    // -------------------------------------------------------------------------
    // AIRadio paragraph endpoint.
    // -------------------------------------------------------------------------

    } else if (arg == "--end-silence") {
      if (i + 1 >= argc) {
        std::cerr
            << "--end-silence requires a value\n";

        return EXIT_FAILURE;
      }

      if (!ParsePositiveFloat(
              argv[++i],
              &g_config.end_silence_duration)) {

        std::cerr
            << "--end-silence must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--end-silence=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--end-silence="));

      if (!ParsePositiveFloat(
              value,
              &g_config.end_silence_duration)) {

        std::cerr
            << "--end-silence must be "
               "greater than zero\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // Encoder.
    // -------------------------------------------------------------------------

    } else if (arg == "--encoder") {
      if (i + 1 >= argc) {
        std::cerr
            << "--encoder requires a filename\n";

        return EXIT_FAILURE;
      }

      g_config.encoder =
          argv[++i];

    } else if (
        arg.rfind("--encoder=", 0) == 0) {

      g_config.encoder =
          arg.substr(
              std::strlen("--encoder="));

    // -------------------------------------------------------------------------
    // Decoder.
    // -------------------------------------------------------------------------

    } else if (arg == "--decoder") {
      if (i + 1 >= argc) {
        std::cerr
            << "--decoder requires a filename\n";

        return EXIT_FAILURE;
      }

      g_config.decoder =
          argv[++i];

    } else if (
        arg.rfind("--decoder=", 0) == 0) {

      g_config.decoder =
          arg.substr(
              std::strlen("--decoder="));

    // -------------------------------------------------------------------------
    // Joiner.
    // -------------------------------------------------------------------------

    } else if (arg == "--joiner") {
      if (i + 1 >= argc) {
        std::cerr
            << "--joiner requires a filename\n";

        return EXIT_FAILURE;
      }

      g_config.joiner =
          argv[++i];

    } else if (
        arg.rfind("--joiner=", 0) == 0) {

      g_config.joiner =
          arg.substr(
              std::strlen("--joiner="));

    // -------------------------------------------------------------------------
    // Tokens.
    // -------------------------------------------------------------------------

    } else if (arg == "--tokens") {
      if (i + 1 >= argc) {
        std::cerr
            << "--tokens requires a filename\n";

        return EXIT_FAILURE;
      }

      g_config.tokens =
          argv[++i];

    } else if (
        arg.rfind("--tokens=", 0) == 0) {

      g_config.tokens =
          arg.substr(
              std::strlen("--tokens="));

    // -------------------------------------------------------------------------
    // ASR threads.
    // -------------------------------------------------------------------------

    } else if (arg == "--threads") {
      if (i + 1 >= argc) {
        std::cerr
            << "--threads requires a number\n";

        return EXIT_FAILURE;
      }

      if (!ParsePositiveInt(
              argv[++i],
              &g_config.num_threads)) {

        std::cerr
            << "--threads must be a "
               "positive integer\n";

        return EXIT_FAILURE;
      }

    } else if (
        arg.rfind("--threads=", 0) == 0) {

      const std::string value =
          arg.substr(
              std::strlen("--threads="));

      if (!ParsePositiveInt(
              value,
              &g_config.num_threads)) {

        std::cerr
            << "--threads must be a "
               "positive integer\n";

        return EXIT_FAILURE;
      }

    // -------------------------------------------------------------------------
    // Help.
    // -------------------------------------------------------------------------

    } else if (
        arg == "--help" ||
        arg == "-h") {

      PrintUsage(argv[0]);
      return EXIT_SUCCESS;

    // -------------------------------------------------------------------------
    // Unknown option.
    // -------------------------------------------------------------------------

    } else {
      std::cerr
          << "Unknown argument: "
          << arg
          << '\n';

      PrintUsage(argv[0]);

      return EXIT_FAILURE;
    }
  }

  // ---------------------------------------------------------------------------
  // Final configuration validation.
  // ---------------------------------------------------------------------------

  if (g_config.vad_min_speech_duration >
      g_config.vad_max_speech_duration) {

    std::cerr
        << "--vad-min-speech cannot be greater "
           "than --vad-max-speech\n";

    return EXIT_FAILURE;
  }

  // ---------------------------------------------------------------------------
  // Startup configuration.
  // ---------------------------------------------------------------------------

  std::cout
      << "Data directory:     "
      << g_config.data_dir
      << '\n'

      << "PipeWire source:    "
      << g_config.pipewire_source
      << '\n'

      << "HTTP post URL:      "
      << g_config.post_url
      << '\n'

      << "VAD threshold:      "
      << g_config.vad_threshold
      << '\n'

      << "VAD min silence:    "
      << g_config.vad_min_silence_duration
      << " sec\n"

      << "VAD min speech:     "
      << g_config.vad_min_speech_duration
      << " sec\n"

      << "VAD max speech:     "
      << g_config.vad_max_speech_duration
      << " sec\n"

      << "VAD pre-roll:       "
      << g_config.vad_pre_roll_ms
      << " ms\n"

      << "Endpoint silence:   "
      << g_config.end_silence_duration
      << " sec\n"

      << "Encoder:            "
      << ModelPath(g_config.encoder)
      << '\n'

      << "Decoder:            "
      << ModelPath(g_config.decoder)
      << '\n'

      << "Joiner:             "
      << ModelPath(g_config.joiner)
      << '\n'

      << "Tokens:             "
      << ModelPath(g_config.tokens)
      << '\n'

      << "ASR threads:        "
      << g_config.num_threads
      << '\n';

  return Run();
}