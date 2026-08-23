#include <signal.h>
#include <stdio.h>
#include <stdlib.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <utility>
#include <deque>
#include <fstream>
#include <cmath>
#include <filesystem>

#include "portaudio.h"
#include "sherpa-onnx/c-api/cxx-api.h"
#include "sherpa-onnx/csrc/microphone.h"

namespace {

    // =============================================================================
    // AUDIO CONFIGURATION
    // =============================================================================

    constexpr int32_t kCaptureSampleRate = 48000;
    constexpr int32_t kRecognizerSampleRate = 16000;

    constexpr size_t kCaptureFrameSamples = 4800;   // 100 ms @ 48 kHz
    constexpr size_t kFrameSamples = 1600;          // 100 ms @ 16 kHz

    constexpr size_t kVadWindowSize = 512;          // 32 ms @ 16 kHz

    constexpr size_t kRingFrameCount = 128;

    constexpr size_t kPreRollFrames = 3;             // 300 ms

    // IMPORTANT:
    //
    // sherpa-onnx's streaming examples use approximately 300 ms of tail padding
    // before InputFinished().
    //
    // 4800 samples @ 16 kHz = 300 ms.
    constexpr size_t kTailPaddingSamples = 4800;

    constexpr float kVadThreshold = 0.5f;
    constexpr float kMinSpeechDuration = 0.25f;
    constexpr float kMinSilenceDuration = 0.4f;
    constexpr float kMaxSpeechDuration = 30.0f;

    constexpr size_t kCaptureQueueLimit = 8;

    constexpr size_t kAsrQueueLimit = kRingFrameCount;
    constexpr size_t kRecognitionQueueLimit = kRingFrameCount;

    constexpr int kShutdownTimeoutSeconds = 5;

    constexpr bool kEnableAudioDiagnostics = true;
    constexpr uint64_t kDiagnosticFrames = 100;

    std::string g_data_dir;

    static std::string ModelPath(const char* filename) {
      return (std::filesystem::path(g_data_dir) / filename).string();
    }

    static bool ParseDataDir(int argc, char** argv) {
      for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "--data-dir") {
          if (i + 1 >= argc || argv[i + 1][0] == '\0') {
            std::cerr << "ERROR: --data-dir requires a directory path\n";
            return false;
          }
          g_data_dir = argv[++i];
          return true;
        }
      }

      std::cerr << "ERROR: --data-dir <model-directory> is required\n";
      return false;
    }

    std::atomic<bool> g_trace_enabled{ false };
    std::mutex g_trace_mutex;

    template <typename... Args>
    static void Trace(Args&&... args) {
        if (!g_trace_enabled.load(std::memory_order_relaxed)) {
            return;
        }

        std::lock_guard<std::mutex> lock(g_trace_mutex);

        std::cout << "[TRACE] ";
        (std::cout << ... << std::forward<Args>(args));
        std::cout << "\n";
    }

    // =============================================================================
    // SHUTDOWN
    // =============================================================================

    std::atomic<bool> g_shutdown_requested{ false };

    std::mutex g_shutdown_mutex;
    std::condition_variable g_shutdown_condition;

    std::atomic<bool> g_asr_worker_done{ false };
    std::atomic<bool> g_recognition_worker_done{ false };

    static void Handler(int /*sig*/) {
      g_shutdown_requested.store(true);
    }

    // =============================================================================
    // AUDIO DIAGNOSTICS
    // =============================================================================

    struct AudioDiagnostics {
      uint64_t capture_input_samples = 0;
      uint64_t resampled_output_samples = 0;
      uint64_t recognition_frames = 0;

      void Print() const {
        if (!kEnableAudioDiagnostics) {
          return;
        }

        const uint64_t consumed_samples =
            recognition_frames * kFrameSamples;

        const uint64_t remainder =
            resampled_output_samples >= consumed_samples
                ? resampled_output_samples - consumed_samples
                : 0;

        std::cout
            << "[AUDIO] input="
            << capture_input_samples
            << " samples, output="
            << resampled_output_samples
            << " samples, frames="
            << recognition_frames
            << ", remainder="
            << remainder
            << "\n";
      }
    };

    AudioDiagnostics g_audio_diagnostics;

    // =============================================================================
    // PCM16 WAV WRITER
    //
    // All diagnostic WAV files are standard:
    //
    //   mono
    //   signed 16-bit little endian PCM
    //
    // This is deliberately NOT IEEE Float WAV because aplay and ordinary audio
    // utilities handle PCM16 reliably.
    // =============================================================================

    class Pcm16WavWriter {
     public:
      Pcm16WavWriter() = default;

      ~Pcm16WavWriter() {
        Close();
      }

      Pcm16WavWriter(const Pcm16WavWriter&) = delete;
      Pcm16WavWriter& operator=(const Pcm16WavWriter&) = delete;

      bool Open(
          const std::string& path,
          uint32_t sample_rate) {
        Close();

        if (sample_rate == 0) {
          return false;
        }

        file_.open(
            path,
            std::ios::binary |
            std::ios::out |
            std::ios::trunc);

        if (!file_) {
          return false;
        }

        path_ = path;
        sample_rate_ = sample_rate;
        sample_count_ = 0;

        WriteFourCC("RIFF");
        WriteLE32(0);

        WriteFourCC("WAVE");

        WriteFourCC("fmt ");
        WriteLE32(16);
        WriteLE16(1);  // PCM
        WriteLE16(1);  // mono
        WriteLE32(sample_rate_);
        WriteLE32(sample_rate_ * 2);
        WriteLE16(2);
        WriteLE16(16);

        WriteFourCC("data");
        WriteLE32(0);

        if (!file_) {
          Close();
          return false;
        }

        return true;
      }

      bool Write(
          const float* samples,
          size_t count) {
        if (!file_.is_open()) {
          return false;
        }

        if (samples == nullptr || count == 0) {
          return true;
        }

        if (sample_count_ >
            (std::numeric_limits<uint32_t>::max() / 2) - count) {
          std::cerr
              << "WAV file exceeds RIFF limit: "
              << path_
              << "\n";

          return false;
        }

        for (size_t i = 0; i < count; ++i) {
          float x = samples[i];

          if (!std::isfinite(x)) {
            x = 0.0f;
          }

          x = std::max(-1.0f, std::min(1.0f, x));

          int32_t value;

          if (x >= 0.0f) {
            value = static_cast<int32_t>(
                std::lround(x * 32767.0f));
          }
     else {
    value = static_cast<int32_t>(
        std::lround(x * 32768.0f));
  }

  const int16_t pcm =
      static_cast<int16_t>(value);

  WriteLE16(
      static_cast<uint16_t>(
          static_cast<uint16_t>(pcm)));
}

if (!file_) {
  return false;
}

sample_count_ += count;

return true;
}

uint64_t SampleCount() const {
  return sample_count_;
}

bool IsOpen() const {
  return file_.is_open();
}

void Close() {
  if (!file_.is_open()) {
    return;
  }

  const uint32_t data_bytes =
      static_cast<uint32_t>(
          sample_count_ * 2);

  const uint32_t riff_size =
      36 + data_bytes;

  file_.flush();

  file_.seekp(4, std::ios::beg);
  WriteLE32(riff_size);

  file_.seekp(40, std::ios::beg);
  WriteLE32(data_bytes);

  file_.flush();
  file_.close();
}

private:
 void WriteFourCC(const char* value) {
   file_.write(value, 4);
 }

 void WriteLE16(uint16_t value) {
   const char bytes[2] = {
       static_cast<char>(value & 0xff),
       static_cast<char>((value >> 8) & 0xff)
   };

   file_.write(bytes, 2);
 }

 void WriteLE32(uint32_t value) {
   const char bytes[4] = {
       static_cast<char>(value & 0xff),
       static_cast<char>((value >> 8) & 0xff),
       static_cast<char>((value >> 16) & 0xff),
       static_cast<char>((value >> 24) & 0xff)
   };

   file_.write(bytes, 4);
 }

 std::ofstream file_;

 std::string path_;

 uint32_t sample_rate_ = 0;

 uint64_t sample_count_ = 0;
};

    // =============================================================================
    // CAPTURE QUEUE
    // =============================================================================

    struct CaptureBlock {
      uint64_t block_index = 0;

      std::array<float, kCaptureFrameSamples>
          samples{};
    };

    std::array<CaptureBlock, kCaptureQueueLimit>
        g_capture_queue{};

    size_t g_capture_queue_head = 0;
    size_t g_capture_queue_tail = 0;
    size_t g_capture_queue_count = 0;

    std::mutex g_capture_mutex;
    std::condition_variable g_capture_condition;

    // =============================================================================
    // ASR / VAD QUEUE
    // =============================================================================

    struct AsrQueueEntry {
      uint64_t frame_index = 0;
    };

    std::deque<AsrQueueEntry>
        g_asr_queue;

    std::mutex g_asr_mutex;
    std::condition_variable g_asr_condition;

    // =============================================================================
    // RECOGNITION QUEUE
    //
    // IMPORTANT:
    //
    // The recognizer receives frames asynchronously from the VAD worker.
    //
    // end_of_utterance is attached to the LAST REAL AUDIO FRAME.
    //
    // The recognition worker then:
    //   1. accepts that real frame
    //   2. decodes
    //   3. appends 300 ms of zero padding
    //   4. calls InputFinished()
    //   5. drains Decode()
    //   6. calls GetResult()
    //   7. destroys the stream
    // =============================================================================

    struct RecognitionQueueEntry {
      uint64_t frame_index = 0;

      bool end_of_utterance = false;
    };

    std::deque<RecognitionQueueEntry>
        g_recognition_queue;

    std::mutex g_recognition_mutex;
    std::condition_variable g_recognition_condition;

    // =============================================================================
    // WAKE WORKERS
    // =============================================================================

    static void WakeAllWorkers() {
      g_capture_condition.notify_all();
      g_asr_condition.notify_all();
      g_recognition_condition.notify_all();
      g_shutdown_condition.notify_all();
    }

    // =============================================================================
    // AUDIO RING
    // =============================================================================

    class AudioRing {
     public:
      using Frame =
          std::array<float, kFrameSamples>;

      uint64_t Write(
          const float* samples) {
        const uint64_t frame_index =
            next_frame_index_++;

        std::lock_guard<std::mutex> lock(
            mutex_);

        std::memcpy(
            frames_[frame_index %
                    kRingFrameCount].data(),
            samples,
            sizeof(float) * kFrameSamples);

        return frame_index;
      }

      Frame Read(
          uint64_t frame_index) const {
        std::lock_guard<std::mutex> lock(
            mutex_);

        Frame frame{};

        std::memcpy(
            frame.data(),
            frames_[frame_index %
                     kRingFrameCount].data(),
            sizeof(float) * kFrameSamples);

        return frame;
      }

     private:
      std::array<Frame, kRingFrameCount>
          frames_{};

      mutable std::mutex mutex_;

      uint64_t next_frame_index_ = 0;
    };

    AudioRing g_audio_ring;

    // =============================================================================
    // CAPTURE ASSEMBLER
    // =============================================================================

    class CaptureAssembler {
     public:
      explicit CaptureAssembler(
          int32_t sample_rate)
          : sample_rate_(sample_rate) {
        if (sample_rate_ != kCaptureSampleRate) {
          std::cerr
              << "Unsupported capture sample rate: "
              << sample_rate_
              << " Hz\n";

          valid_ = false;
        }
      }

      bool IsValid() const {
        return valid_;
      }

      bool Add(
          const float* samples,
          size_t count) {
        if (!valid_) {
          return false;
        }

        while (count != 0) {
          const size_t remaining =
              kCaptureFrameSamples - offset_;

          const size_t copy_count =
              std::min(remaining, count);

          std::memcpy(
              capture_buffer_.data() + offset_,
              samples,
              copy_count * sizeof(float));

          offset_ += copy_count;
          samples += copy_count;
          count -= copy_count;

          if (offset_ == kCaptureFrameSamples) {
            if (!EnqueueCompletedBlock()) {
              return false;
            }

            offset_ = 0;
          }
        }

        return true;
      }

     private:
      bool EnqueueCompletedBlock() {
        if (!g_capture_mutex.try_lock()) {
          std::cerr
              << "\nERROR: capture queue mutex busy "
                 "in PortAudio callback\n";

          g_shutdown_requested.store(true);

          return false;
        }

        if (g_capture_queue_count >=
            kCaptureQueueLimit) {
          g_capture_mutex.unlock();

          std::cerr
              << "\nERROR: capture queue overflow\n";

          g_shutdown_requested.store(true);

          return false;
        }

        CaptureBlock& block =
            g_capture_queue[
                g_capture_queue_tail];

        block.block_index =
            next_block_index_++;

        std::memcpy(
            block.samples.data(),
            capture_buffer_.data(),
            sizeof(float) *
                kCaptureFrameSamples);

        g_capture_queue_tail =
            (g_capture_queue_tail + 1) %
            kCaptureQueueLimit;

        ++g_capture_queue_count;

        g_capture_mutex.unlock();

        g_capture_condition.notify_one();

        return true;
      }

      int32_t sample_rate_;

      std::array<float, kCaptureFrameSamples>
          capture_buffer_{};

      size_t offset_ = 0;

      uint64_t next_block_index_ = 0;

      bool valid_ = true;
    };

    CaptureAssembler* g_capture_assembler = nullptr;

    // =============================================================================
    // PORTAUDIO CALLBACK
    // =============================================================================

    static int RecordCallback(
        const void* input_buffer,
        void* /*output_buffer*/,
        unsigned long frames_per_buffer,
        const PaStreamCallbackTimeInfo* /*time_info*/,
        PaStreamCallbackFlags /*status_flags*/,
        void* /*user_data*/) {
      if (g_shutdown_requested.load()) {
        return paComplete;
      }

      if (input_buffer == nullptr ||
          frames_per_buffer == 0 ||
          g_capture_assembler == nullptr) {
        return paContinue;
      }

      const float* input =
          reinterpret_cast<const float*>(
              input_buffer);

      if (!g_capture_assembler->Add(
              input,
              static_cast<size_t>(
                  frames_per_buffer))) {
        return paComplete;
      }

      return g_shutdown_requested.load()
                 ? paComplete
                 : paContinue;
    }

    // =============================================================================
    // VAD
    // =============================================================================

    static sherpa_onnx::cxx::VoiceActivityDetector
    CreateVad() {
      using namespace sherpa_onnx::cxx;

      VadModelConfig config;

      config.silero_vad.model =
          "./silero_vad.onnx";

      config.silero_vad.threshold =
          kVadThreshold;

      config.silero_vad.min_silence_duration =
          kMinSilenceDuration;

      config.silero_vad.min_speech_duration =
          kMinSpeechDuration;

      config.silero_vad.max_speech_duration =
          kMaxSpeechDuration;

      config.silero_vad.window_size =
          kVadWindowSize;

      config.sample_rate =
          kRecognizerSampleRate;

      config.debug = false;

      auto vad =
          VoiceActivityDetector::Create(
              config,
              20);

      if (!vad.Get()) {
        std::cerr
            << "Failed to create VAD\n";

        std::exit(EXIT_FAILURE);
      }

      return vad;
    }

    // =============================================================================
    // RECOGNIZER
    // =============================================================================

    static sherpa_onnx::cxx::OnlineRecognizer
    CreateRecognizer() {
      using namespace sherpa_onnx::cxx;

      OnlineRecognizerConfig config;

      config.model_config.transducer.encoder =
          "./encoder-epoch-99-avg-1.int8.onnx";

      config.model_config.transducer.decoder =
          "./decoder-epoch-99-avg-1.onnx";

      config.model_config.transducer.joiner =
          "./joiner-epoch-99-avg-1.int8.onnx";

      config.model_config.tokens =
          "./tokens.txt";

      config.model_config.num_threads = 2;

      config.feat_config.sample_rate =
          kRecognizerSampleRate;

      // VAD controls utterance boundaries.
      config.enable_endpoint = false;

      std::cout
          << "Loading EN20M streaming Zipformer...\n";

      auto recognizer =
          OnlineRecognizer::Create(
              config);

      if (!recognizer.Get()) {
        std::cerr
            << "Failed to create Zipformer recognizer\n";

        std::exit(EXIT_FAILURE);
      }

      std::cout
          << "Loading model done\n";

      return recognizer;
    }

    // =============================================================================
    // SAMPLE RATE CONVERTER
    // =============================================================================

    class SampleConverter {
     public:
      explicit SampleConverter(
          double input_rate)
          : input_rate_(input_rate) {
        if (input_rate_ <= 0.0) {
          std::cerr
              << "Invalid input sample rate\n";

          std::exit(EXIT_FAILURE);
        }

        if (static_cast<int32_t>(input_rate_) ==
            kRecognizerSampleRate) {
          return;
        }

        const float min_freq =
            std::min(
                static_cast<float>(input_rate_),
                static_cast<float>(
                    kRecognizerSampleRate));

        const float lowpass_cutoff =
            0.99f * 0.5f * min_freq;

        constexpr int32_t kFilterWidth = 6;

        resampler_ =
            sherpa_onnx::cxx::LinearResampler::Create(
                input_rate_,
                kRecognizerSampleRate,
                lowpass_cutoff,
                kFilterWidth);

        if (!resampler_.Get()) {
          std::cerr
              << "Failed to create resampler\n";

          std::exit(EXIT_FAILURE);
        }
      }

      std::vector<float> Convert(
          const float* input,
          size_t count) {
        if (count == 0) {
          return {};
        }

        if (!resampler_.Get()) {
          return std::vector<float>(
              input,
              input + count);
        }

        return resampler_.Resample(
            input,
            count,
            false);
      }

     private:
      double input_rate_;

      sherpa_onnx::cxx::LinearResampler
          resampler_;
    };

    // =============================================================================
    // RESAMPLED ACCUMULATOR
    // =============================================================================

    class ResampledAccumulator {
     public:
      void Append(
          const std::vector<float>& samples) {
        if (samples.empty()) {
          return;
        }

        samples_.insert(
            samples_.end(),
            samples.begin(),
            samples.end());
      }

      size_t Available() const {
        return samples_.size() -
               read_offset_;
      }

      bool HasFrame() const {
        return Available() >=
               kFrameSamples;
      }

      const float* Data() const {
        return samples_.data() +
               read_offset_;
      }

      void ConsumeFrame() {
        read_offset_ += kFrameSamples;

        if (read_offset_ ==
            samples_.size()) {
          samples_.clear();
          read_offset_ = 0;
          return;
        }

        if (read_offset_ >=
            kFrameSamples * 4) {
          const size_t remaining =
              samples_.size() -
              read_offset_;

          std::memmove(
              samples_.data(),
              samples_.data() +
                  read_offset_,
              remaining * sizeof(float));

          samples_.resize(remaining);

          read_offset_ = 0;
        }
      }

     private:
      std::vector<float> samples_;

      size_t read_offset_ = 0;
    };

    // =============================================================================
    // QUEUE HELPERS
    // =============================================================================

    static void EnqueueAsrFrame(
        uint64_t frame_index) {
      std::lock_guard<std::mutex> lock(
          g_asr_mutex);

      if (g_asr_queue.size() >=
          kAsrQueueLimit) {
        std::cerr
            << "\nWARNING: ASR queue backlog exceeded "
               "limit; resynchronizing.\n";

        g_asr_queue.clear();
      }

      g_asr_queue.push_back(
          AsrQueueEntry{
              frame_index});

      Trace(
          "ASR enqueue frame=",
          frame_index,
          " depth=",
          g_asr_queue.size());

      g_asr_condition.notify_one();
    }

    static void EnqueueRecognitionFrame(
        uint64_t frame_index,
        bool end_of_utterance) {
      std::lock_guard<std::mutex> lock(
          g_recognition_mutex);

      if (g_recognition_queue.size() >=
          kRecognitionQueueLimit) {
        std::cerr
            << "\nWARNING: recognition queue backlog "
               "exceeded limit; resynchronizing.\n";

        g_recognition_queue.clear();
      }

      g_recognition_queue.push_back(
          RecognitionQueueEntry{
              frame_index,
              end_of_utterance});

      Trace(
          "RECOGNITION enqueue frame=",
          frame_index,
          " end=",
          end_of_utterance ? "yes" : "no",
          " depth=",
          g_recognition_queue.size());

      g_recognition_condition.notify_one();
    }

    // =============================================================================
    // VAD WORKER
    // =============================================================================

    static void AsrWorker() {
      auto vad = CreateVad();

      std::vector<float> vad_accumulator;

      vad_accumulator.reserve(
          kVadWindowSize * 4);

      bool speech_active = false;

      uint64_t previous_frame_index = 0;

      bool have_previous_frame = false;

      while (!g_shutdown_requested.load()) {
        AsrQueueEntry entry;

        {
          std::unique_lock<std::mutex> lock(
              g_asr_mutex);

          g_asr_condition.wait_for(
              lock,
              std::chrono::milliseconds(100),
              [] {
                return
                    g_shutdown_requested.load() ||
                    !g_asr_queue.empty();
              });

          if (g_shutdown_requested.load()) {
            break;
          }

          if (g_asr_queue.empty()) {
            continue;
          }

          entry =
              g_asr_queue.front();

          g_asr_queue.pop_front();
        }

        const uint64_t frame_index =
            entry.frame_index;

        if (have_previous_frame &&
            frame_index !=
                previous_frame_index + 1) {
          std::cerr
              << "\n[VAD] frame discontinuity:\n"
              << "previous="
              << previous_frame_index
              << " current="
              << frame_index
              << "\n"
              << "[VAD] resetting VAD state\n";

          vad_accumulator.clear();

          speech_active = false;

          vad = CreateVad();

          have_previous_frame = false;
        }

        previous_frame_index =
            frame_index;

        have_previous_frame = true;

        const auto frame =
            g_audio_ring.Read(
                frame_index);

        vad_accumulator.insert(
            vad_accumulator.end(),
            frame.begin(),
            frame.end());

        bool speech_detected_in_frame = false;

        bool speech_ended_in_frame = false;

        while (vad_accumulator.size() >=
               kVadWindowSize) {
          std::array<float, kVadWindowSize>
              vad_window{};

          std::copy_n(
              vad_accumulator.begin(),
              kVadWindowSize,
              vad_window.begin());

          vad_accumulator.erase(
              vad_accumulator.begin(),
              vad_accumulator.begin() +
                  kVadWindowSize);

          vad.AcceptWaveform(
              vad_window.data(),
              kVadWindowSize);

          if (vad.IsDetected()) {
            speech_detected_in_frame = true;
          }

          while (!vad.IsEmpty()) {
            vad.Pop();

            if (speech_active) {
              speech_ended_in_frame = true;
            }
          }
        }

        // -------------------------------------------------------------------------
        // SPEECH START
        // -------------------------------------------------------------------------

        if (!speech_active &&
            speech_detected_in_frame) {
          speech_active = true;

          const uint64_t start_frame =
              frame_index >= kPreRollFrames
                  ? frame_index - kPreRollFrames
                  : 0;

          std::cout
              << "\n[VAD] SPEECH START"
              << " frame="
              << frame_index
              << " pre-roll-start="
              << start_frame
              << "\n";

          for (uint64_t f = start_frame;
               f <= frame_index;
               ++f) {
            EnqueueRecognitionFrame(
                f,
                speech_ended_in_frame &&
                    f == frame_index);
          }

          if (speech_ended_in_frame) {
            speech_active = false;

            std::cout
                << "[VAD] SPEECH END frame="
                << frame_index
                << "\n";
          }

          continue;
        }

        // -------------------------------------------------------------------------
        // EXISTING SPEECH
        // -------------------------------------------------------------------------

        if (speech_active) {
          EnqueueRecognitionFrame(
              frame_index,
              speech_ended_in_frame);

          if (speech_ended_in_frame) {
            speech_active = false;

            std::cout
                << "[VAD] SPEECH END frame="
                << frame_index
                << "\n";
          }
        }
      }

      g_asr_worker_done.store(true);

      g_asr_condition.notify_all();
      g_recognition_condition.notify_all();
      g_shutdown_condition.notify_all();
    }

    // =============================================================================
    // HTTP
    // =============================================================================

    static void SendResultToHttp(
        const std::string& text) {
      if (text.empty()) {
        return;
      }

      // Insert existing Radio HTTP POST implementation here.

      std::cout
          << "[HTTP] RESULT: "
          << text
          << "\n";
    }

    // =============================================================================
    // RECOGNITION WORKER
    //
    // THIS IS THE IMPORTANT FIX.
    //
    // Recognition remains asynchronous.
    //
    // The worker does NOT call GetResult() after every frame.
    //
    // It obtains the final result only after:
    //
    //   last real frame
    //       |
    //       v
    //   decode
    //       |
    //       v
    //   300 ms zero tail padding
    //       |
    //       v
    //   InputFinished()
    //       |
    //       v
    //   decode until !IsReady()
    //       |
    //       v
    //   GetResult()
    // =============================================================================

    static void RecognitionWorker(
        sherpa_onnx::cxx::OnlineRecognizer*
            recognizer) {
      using namespace sherpa_onnx::cxx;

      std::unique_ptr<OnlineStream>
          stream;

      uint64_t last_frame_index = 0;

      bool have_frame = false;

      uint64_t utterance_number = 0;

      Pcm16WavWriter utterance_wav;

      std::vector<float>
          utterance_audio;

      utterance_audio.reserve(
          kFrameSamples * 32);

      auto FinalizeUtterance =
          [&]() {
            if (!stream) {
              return;
            }

            ++utterance_number;

            // ---------------------------------------------------------------------
            // Write the exact real audio supplied to the recognizer.
            //
            // The tail padding is deliberately NOT included in this WAV.
            // ---------------------------------------------------------------------

            const std::string wav_path =
                "/tmp/zipformer-vad-utterance-" +
                (utterance_number < 10
                     ? std::string("00")
                     : utterance_number < 100
                           ? std::string("0")
                           : std::string("")) +
                std::to_string(utterance_number) +
                ".wav";

            std::cout
                << "[WAV] VAD utterance "
                << utterance_number
                << ": "
                << wav_path
                << "\n";

            if (!utterance_wav.Open(
                    wav_path,
                    kRecognizerSampleRate)) {
              std::cerr
                  << "[WAV] ERROR: could not open "
                  << wav_path
                  << "\n";
            }
     else {
    utterance_wav.Write(
        utterance_audio.data(),
        utterance_audio.size());

    utterance_wav.Close();

    const double rms =
        [&]() {
          if (utterance_audio.empty()) {
            return 0.0;
          }

          long double sum = 0.0;

          for (float x :
               utterance_audio) {
            sum +=
                static_cast<long double>(x) *
                static_cast<long double>(x);
          }

          return std::sqrt(
              static_cast<double>(
                  sum /
                  utterance_audio.size()));
        }();

    float peak = 0.0f;

    for (float x :
         utterance_audio) {
      peak = std::max(
          peak,
          std::fabs(x));
    }

    const double duration =
        static_cast<double>(
            utterance_audio.size()) /
        kRecognizerSampleRate;

    std::cout
        << "[WAV] utterance samples="
        << utterance_audio.size()
        << " rate="
        << kRecognizerSampleRate
        << " Hz frames="
        << utterance_audio.size() /
               kFrameSamples
        << " file="
        << wav_path
        << "\n";

    std::cout
        << "[ASR AUDIO] utterance="
        << utterance_number
        << " frames="
        << utterance_audio.size() /
               kFrameSamples
        << " samples="
        << utterance_audio.size()
        << " duration="
        << duration
        << " sec RMS="
        << rms
        << " peak="
        << peak
        << "\n";
  }

            // ---------------------------------------------------------------------
            // IMPORTANT:
            //
            // Add 300 ms of silence before InputFinished().
            //
            // This is the behavior used by sherpa-onnx's streaming examples.
            // ---------------------------------------------------------------------

            std::array<float,
                       kTailPaddingSamples>
                tail_padding{};

            Trace(
                "ASR tail padding samples=",
                tail_padding.size());

            stream->AcceptWaveform(
                kRecognizerSampleRate,
                tail_padding.data(),
                static_cast<int32_t>(
                    tail_padding.size()));

            // ---------------------------------------------------------------------
            // Signal that no more input will arrive.
            // ---------------------------------------------------------------------

            Trace(
                "ASR InputFinished utterance=",
                utterance_number);

            stream->InputFinished();

            // ---------------------------------------------------------------------
            // Drain EVERYTHING that became ready because of the tail padding and
            // InputFinished().
            // ---------------------------------------------------------------------

            size_t final_decode_count = 0;

            while (recognizer->IsReady(
                stream.get())) {
              recognizer->Decode(
                  stream.get());

              ++final_decode_count;
            }

            Trace(
                "ASR final drain decode_count=",
                final_decode_count);

            // ---------------------------------------------------------------------
            // ONLY NOW obtain the final result.
            // ---------------------------------------------------------------------

            const auto result =
                recognizer->GetResult(
                    stream.get());

            std::cout
                << "[ASR] FINAL: ";

            if (result.text.empty()) {
              std::cout
                  << "(empty)";
            }
     else {
    std::cout
        << result.text;
  }

  std::cout
      << "\n";

  if (!result.text.empty()) {
    SendResultToHttp(
        result.text);
  }

  // ---------------------------------------------------------------------
  // Destroy the finished stream.
  // ---------------------------------------------------------------------

  stream.reset();

  have_frame = false;

  utterance_audio.clear();

  Trace(
      "ASR stream FINISHED utterance=",
      utterance_number);
};

while (!g_shutdown_requested.load()) {
  RecognitionQueueEntry entry;

  {
    std::unique_lock<std::mutex> lock(
        g_recognition_mutex);

    g_recognition_condition.wait_for(
        lock,
        std::chrono::milliseconds(100),
        [] {
          return
              g_shutdown_requested.load() ||
              !g_recognition_queue.empty();
        });

    if (g_shutdown_requested.load()) {
      break;
    }

    if (g_recognition_queue.empty()) {
      continue;
    }

    entry =
        g_recognition_queue.front();

    g_recognition_queue.pop_front();

    Trace(
        "RECOGNITION dequeue frame=",
        entry.frame_index,
        " end=",
        entry.end_of_utterance
            ? "yes"
            : "no",
        " depth=",
        g_recognition_queue.size());
  }

  // -------------------------------------------------------------------------
  // Detect missing frame(s).
  // -------------------------------------------------------------------------

  if (have_frame &&
      entry.frame_index !=
          last_frame_index + 1) {
    std::cerr
        << "\n[ASR] frame discontinuity:\n"
        << "previous="
        << last_frame_index
        << " current="
        << entry.frame_index
        << "\n"
        << "[ASR] discarding current stream\n";

    stream.reset();

    have_frame = false;

    utterance_audio.clear();
  }

  // -------------------------------------------------------------------------
  // Create stream if this is the first frame of an utterance.
  // -------------------------------------------------------------------------

  if (!stream) {
    stream =
        std::make_unique<OnlineStream>(
            recognizer->CreateStream());

    utterance_audio.clear();

    Trace(
        "ASR stream CREATED frame=",
        entry.frame_index);
  }

  last_frame_index =
      entry.frame_index;

  have_frame = true;

  // -------------------------------------------------------------------------
  // Read the exact 100-ms frame.
  // -------------------------------------------------------------------------

  const auto frame =
      g_audio_ring.Read(
          entry.frame_index);

  // Keep an exact copy for the VAD utterance WAV.
  utterance_audio.insert(
      utterance_audio.end(),
      frame.begin(),
      frame.end());

  // -------------------------------------------------------------------------
  // Feed the real audio.
  // -------------------------------------------------------------------------

  stream->AcceptWaveform(
      kRecognizerSampleRate,
      frame.data(),
      static_cast<int32_t>(
          frame.size()));

  Trace(
      "ASR accepted frame=",
      entry.frame_index,
      " samples=",
      frame.size());

  // -------------------------------------------------------------------------
  // Decode whenever the stream says enough data is available.
  //
  // This can happen zero or more times for one 100-ms audio frame.
  //
  // Therefore decoding is intentionally NOT tied one-to-one to frames.
  // -------------------------------------------------------------------------

  size_t decode_count = 0;

  while (recognizer->IsReady(
      stream.get())) {
    recognizer->Decode(
        stream.get());

    ++decode_count;
  }

  Trace(
      "ASR decoded frame=",
      entry.frame_index,
      " decode_count=",
      decode_count);

  // -------------------------------------------------------------------------
  // Utterance finished by VAD.
  // -------------------------------------------------------------------------

  if (entry.end_of_utterance) {
    FinalizeUtterance();
  }
}

// ---------------------------------------------------------------------------
// Shutdown is NOT an utterance boundary.
//
// Do not add padding.
// Do not call InputFinished().
// Do not submit a partial result.
// ---------------------------------------------------------------------------

stream.reset();

utterance_audio.clear();

g_recognition_worker_done.store(true);

g_recognition_condition.notify_all();
g_shutdown_condition.notify_all();
}

    // =============================================================================
    // PROCESS CAPTURE BLOCKS
    // =============================================================================

    static bool ProcessCaptureBlocks(
        SampleConverter* converter,
        ResampledAccumulator* accumulator,
        Pcm16WavWriter* capture_wav,
        Pcm16WavWriter* resample_wav) {
      while (!g_shutdown_requested.load()) {
        CaptureBlock block;

        size_t capture_queue_depth = 0;

        {
          std::lock_guard<std::mutex> lock(
              g_capture_mutex);

          if (g_capture_queue_count == 0) {
            return true;
          }

          block.block_index =
              g_capture_queue[
                  g_capture_queue_head]
                  .block_index;

          std::memcpy(
              block.samples.data(),
              g_capture_queue[
                  g_capture_queue_head]
                  .samples.data(),
              sizeof(float) *
                  kCaptureFrameSamples);

          g_capture_queue_head =
              (g_capture_queue_head + 1) %
              kCaptureQueueLimit;

          --g_capture_queue_count;

          capture_queue_depth =
              g_capture_queue_count;
        }

        Trace(
            "CAPTURE dequeue block=",
            block.block_index,
            " depth=",
            capture_queue_depth);

        g_audio_diagnostics.capture_input_samples +=
            kCaptureFrameSamples;

        // -------------------------------------------------------------------------
        // 48 kHz PCM16 diagnostic WAV
        // -------------------------------------------------------------------------

        if (capture_wav != nullptr) {
          if (!capture_wav->Write(
                  block.samples.data(),
                  kCaptureFrameSamples)) {
            std::cerr
                << "\nERROR: failed writing capture WAV\n";

            return false;
          }
        }

        // -------------------------------------------------------------------------
        // Resample 48 kHz -> 16 kHz.
        // -------------------------------------------------------------------------

        const std::vector<float> converted =
            converter->Convert(
                block.samples.data(),
                kCaptureFrameSamples);

        g_audio_diagnostics
            .resampled_output_samples +=
            converted.size();

        // -------------------------------------------------------------------------
        // 16 kHz PCM16 diagnostic WAV
        // -------------------------------------------------------------------------

        if (resample_wav != nullptr) {
          if (!resample_wav->Write(
                  converted.data(),
                  converted.size())) {
            std::cerr
                << "\nERROR: failed writing resampled WAV\n";

            return false;
          }
        }

        accumulator->Append(
            converted);

        // -------------------------------------------------------------------------
        // Extract exact 1600-sample recognition frames.
        // -------------------------------------------------------------------------

        while (accumulator->HasFrame()) {
          const uint64_t frame_index =
              g_audio_ring.Write(
                  accumulator->Data());

          accumulator->ConsumeFrame();

          ++g_audio_diagnostics
              .recognition_frames;

          Trace(
              "FRAME create index=",
              frame_index,
              " ring_slot=",
              frame_index % kRingFrameCount);

          EnqueueAsrFrame(
              frame_index);

          if (kEnableAudioDiagnostics &&
              g_audio_diagnostics
                      .recognition_frames %
                  kDiagnosticFrames ==
                  0) {
            g_audio_diagnostics.Print();
          }
        }
      }

      return false;
    }

    // =============================================================================
    // MAIN
    // =============================================================================

    }  // namespace

        int main(
            int argc,
            char** argv) {

      if (!ParseDataDir(argc, argv)) {
        return 1;
      }

      const std::filesystem::path data_dir(g_data_dir);
      const char* required_models[] = {
          "encoder-epoch-99-avg-1.int8.onnx",
          "decoder-epoch-99-avg-1.onnx",
          "joiner-epoch-99-avg-1.int8.onnx",
          "tokens.txt",
          "silero_vad.onnx",
      };

      if (!std::filesystem::is_directory(data_dir)) {
        std::cerr << "ERROR: data directory does not exist or is not a directory: "
                  << g_data_dir << "\n";
        return 1;
      }

      for (const char* filename : required_models) {
        const auto model_path = data_dir / filename;
        if (!std::filesystem::is_regular_file(model_path)) {
          std::cerr << "ERROR: required model file not found: "
                    << model_path << "\n";
          return 1;
        }
      }


        signal(SIGINT, Handler);
        signal(SIGTERM, Handler);

        bool dump_capture = false;
        bool dump_resample = false;

        std::string capture_wav_path =
            "/tmp/zipformer-capture-48k.wav";

        std::string resample_wav_path =
            "/tmp/zipformer-resampled-16k.wav";

        for (int i = 1; i < argc; ++i) {
            const std::string arg =
                argv[i];

            if (arg == "--trace") {
                g_trace_enabled.store(
                    true,
                    std::memory_order_relaxed);

                continue;
            }

            if (arg == "--dump-capture") {
                dump_capture = true;
                continue;
            }

            if (arg == "--dump-resample") {
                dump_resample = true;
                continue;
            }

            if (arg.rfind(
                "--dump-capture=",
                0) == 0) {
                capture_wav_path =
                    arg.substr(15);

                if (capture_wav_path.empty()) {
                    std::cerr
                        << "--dump-capture requires a path\n";

                    return -1;
                }

                dump_capture = true;

                continue;
            }

            if (arg.rfind(
                "--dump-resample=",
                0) == 0) {
                resample_wav_path =
                    arg.substr(16);

                if (resample_wav_path.empty()) {
                    std::cerr
                        << "--dump-resample requires a path\n";

                    return -1;
                }

                dump_resample = true;

                continue;
            }

            if (arg == "--help" ||
                arg == "-h") {
                std::cout
                    << "Usage: "
                    << argv[0]
                    << " [options]\n\n"

                    << "  --trace\n"
                    << "      Enable detailed pipeline tracing.\n\n"

                    << "  --dump-capture\n"
                    << "      Save 48 kHz PCM16 capture WAV.\n\n"

                    << "  --dump-capture=PATH\n"
                    << "      Save capture WAV to PATH.\n\n"

                    << "  --dump-resample\n"
                    << "      Save 16 kHz PCM16 resampled WAV.\n\n"

                    << "  --dump-resample=PATH\n"
                    << "      Save resampled WAV to PATH.\n";

                return 0;
            }

            std::cerr
                << "Unknown argument: "
                << arg
                << "\n";

            return -1;
        }

        if (g_trace_enabled.load(
            std::memory_order_relaxed)) {
            std::cout
                << "[TRACE] Detailed pipeline tracing ENABLED\n";
        }

        using namespace sherpa_onnx::cxx;

        // ===========================================================================
        // RECOGNIZER
        // ===========================================================================

        auto recognizer =
            CreateRecognizer();

        // ===========================================================================
        // MICROPHONE
        // ===========================================================================

        sherpa_onnx::Microphone mic;

        const int32_t num_devices =
            mic.GetDeviceCount();

        if (num_devices <= 0) {
            std::cerr
                << "No PortAudio input devices found\n";

            return -1;
        }

        int32_t device_index =
            mic.GetDefaultInputDevice();

        const char* device_env =
            std::getenv(
                "SHERPA_ONNX_MIC_DEVICE");

        if (device_env != nullptr) {
            device_index =
                std::atoi(device_env);
        }

        std::cout
            << "\n===== PORTAUDIO DEVICES =====\n";

        mic.PrintDevices(
            device_index);

        // ===========================================================================
        // SAMPLE RATE
        // ===========================================================================

        double mic_sample_rate =
            static_cast<double>(
                kCaptureSampleRate);

        const char* rate_env =
            std::getenv(
                "SHERPA_ONNX_MIC_SAMPLE_RATE");

        if (rate_env != nullptr) {
            mic_sample_rate =
                std::atof(rate_env);
        }

        const int32_t mic_rate =
            static_cast<int32_t>(
                mic_sample_rate);

        if (mic_rate !=
            kCaptureSampleRate) {
            std::cerr
                << "This build expects "
                << kCaptureSampleRate
                << " Hz capture.\n"
                << "Requested rate: "
                << mic_rate
                << " Hz\n";

            return -1;
        }

        // ===========================================================================
        // CAPTURE ASSEMBLER
        // ===========================================================================

        CaptureAssembler
            capture_assembler(mic_rate);

        if (!capture_assembler.IsValid()) {
            return -1;
        }

        g_capture_assembler =
            &capture_assembler;

        // ===========================================================================
        // RESAMPLER
        // ===========================================================================

        SampleConverter converter(
            mic_sample_rate);

        ResampledAccumulator
            resampled_accumulator;

        // ===========================================================================
        // WAV DIAGNOSTICS
        // ===========================================================================

        Pcm16WavWriter capture_wav;
        Pcm16WavWriter resample_wav;

        if (dump_capture) {
            if (!capture_wav.Open(
                capture_wav_path,
                kCaptureSampleRate)) {
                std::cerr
                    << "Failed to open capture WAV: "
                    << capture_wav_path
                    << "\n";

                g_capture_assembler =
                    nullptr;

                return -1;
            }

            std::cout
                << "Capture WAV:       "
                << capture_wav_path
                << " (48 kHz PCM16 mono)\n";
        }

        if (dump_resample) {
            if (!resample_wav.Open(
                resample_wav_path,
                kRecognizerSampleRate)) {
                std::cerr
                    << "Failed to open resampled WAV: "
                    << resample_wav_path
                    << "\n";

                capture_wav.Close();

                g_capture_assembler =
                    nullptr;

                return -1;
            }

            std::cout
                << "Resample WAV:      "
                << resample_wav_path
                << " (16 kHz PCM16 mono)\n";
        }

        // ===========================================================================
        // DISPLAY CONFIGURATION
        // ===========================================================================

        std::cout
            << "\n===== VAD + ZIPFORMER MICROPHONE TEST =====\n"

            << "Capture rate:       "
            << mic_rate
            << " Hz\n"

            << "Recognizer rate:    "
            << kRecognizerSampleRate
            << " Hz\n"

            << "Channels:           1\n"

            << "Capture frame:      "
            << kCaptureFrameSamples
            << " samples / 100 ms\n"

            << "Recognition frame:  "
            << kFrameSamples
            << " samples / 100 ms\n"

            << "Ring frames:        "
            << kRingFrameCount
            << "\n"

            << "Ring capacity:      "
            << kRingFrameCount *
            kFrameSamples
            << " samples / 12.8 sec\n"

            << "Pre-roll:           "
            << kPreRollFrames
            << " frames / 300 ms\n"

            << "VAD window:         "
            << kVadWindowSize
            << " samples / 32 ms\n"

            << "VAD threshold:      "
            << kVadThreshold
            << "\n"

            << "Min speech:         "
            << kMinSpeechDuration
            << " sec\n"

            << "Min silence:        "
            << kMinSilenceDuration
            << " sec\n"

            << "Max speech:         "
            << kMaxSpeechDuration
            << " sec\n"

            << "ASR tail padding:   "
            << kTailPaddingSamples
            << " samples / 300 ms\n"

            << "Capture queue:      "
            << kCaptureQueueLimit
            << " blocks / "
            << kCaptureQueueLimit * 100
            << " ms\n"

            << "=============================================\n\n";

        // ===========================================================================
        // START WORKERS
        // ===========================================================================

        std::thread asr_thread(
            AsrWorker);

        std::thread recognition_thread(
            RecognitionWorker,
            &recognizer);

        // ===========================================================================
        // OPEN MICROPHONE
        // ===========================================================================

        if (!mic.OpenDevice(
            device_index,
            mic_rate,
            1,
            RecordCallback,
            nullptr)) {
            std::cerr
                << "Failed to open microphone device\n";

            g_shutdown_requested.store(true);

            WakeAllWorkers();

            asr_thread.join();
            recognition_thread.join();

            g_capture_assembler =
                nullptr;

            return -1;
        }

        Trace(
            "CAPTURE started device=",
            device_index,
            " rate=",
            mic_rate,
            " channels=1");

        std::cout
            << "Speak a phrase, then pause.\n"
            << "Press Ctrl+C to stop.\n\n";

        // ===========================================================================
        // MAIN PROCESSING LOOP
        // ===========================================================================

        while (!g_shutdown_requested.load()) {
            {
                std::unique_lock<std::mutex> lock(
                    g_capture_mutex);

                g_capture_condition.wait_for(
                    lock,
                    std::chrono::milliseconds(100),
                    [] {
                        return
                            g_shutdown_requested.load() ||
                            g_capture_queue_count != 0;
                    });
            }

            if (g_shutdown_requested.load()) {
                break;
            }

            if (!ProcessCaptureBlocks(
                &converter,
                &resampled_accumulator,
                dump_capture
                ? &capture_wav
                : nullptr,
                dump_resample
                ? &resample_wav
                : nullptr)) {
                break;
            }
        }

        // ===========================================================================
        // SHUTDOWN
        //
        // Do NOT:
        //
        //   - flush a partial capture block
        //   - pad a partial resampled frame
        //   - finalize an unfinished VAD utterance
        //   - call InputFinished() on an unfinished utterance
        // ===========================================================================

        g_shutdown_requested.store(true);

        WakeAllWorkers();

        mic.CloseDevice();

        g_capture_assembler =
            nullptr;

        std::cout
            << "\nShutdown requested. "
            "Waiting for workers...\n";

        // ===========================================================================
        // WAIT FOR WORKERS
        // ===========================================================================

        const auto shutdown_deadline =
            std::chrono::steady_clock::now() +
            std::chrono::seconds(
                kShutdownTimeoutSeconds);

        {
            std::unique_lock<std::mutex> lock(
                g_shutdown_mutex);

            while (!(
                g_asr_worker_done.load() &&
                g_recognition_worker_done.load())) {
                if (g_shutdown_condition.wait_until(
                    lock,
                    shutdown_deadline) ==
                    std::cv_status::timeout) {
                    break;
                }
            }
        }

        // ===========================================================================
        // CLEAN SHUTDOWN
        // ===========================================================================

        if (g_asr_worker_done.load() &&
            g_recognition_worker_done.load()) {
            asr_thread.join();
            recognition_thread.join();

            if (dump_capture) {
                capture_wav.Close();

                std::cout
                    << "[WAV] capture samples="
                    << capture_wav.SampleCount()
                    << " rate="
                    << kCaptureSampleRate
                    << " Hz file="
                    << capture_wav_path
                    << "\n";
            }

            if (dump_resample) {
                resample_wav.Close();

                std::cout
                    << "[WAV] resampled samples="
                    << resample_wav.SampleCount()
                    << " rate="
                    << kRecognizerSampleRate
                    << " Hz file="
                    << resample_wav_path
                    << "\n";
            }

            if (kEnableAudioDiagnostics) {
                std::cout
                    << "\n===== FINAL AUDIO DIAGNOSTICS =====\n";

                g_audio_diagnostics.Print();

                std::cout
                    << "====================================\n";
            }

            std::cout
                << "Workers stopped cleanly.\n"
                << "Test complete.\n";

            return 0;
        }

        // ===========================================================================
        // WORKER FAILURE
        // ===========================================================================

        std::cerr
            << "\nERROR: worker shutdown timeout after "
            << kShutdownTimeoutSeconds
            << " seconds.\n"

            << "ASR worker done: "
            << (g_asr_worker_done.load()
                ? "yes"
                : "NO")
            << "\n"

            << "Recognition worker done: "
            << (g_recognition_worker_done.load()
                ? "yes"
                : "NO")
            << "\n"

            << "Forcing process termination.\n";

        std::_Exit(EXIT_FAILURE);
    }