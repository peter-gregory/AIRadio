#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>

#include "httplib.h"
#include <nlohmann/json.hpp>
#include "sherpa-onnx/c-api/cxx-api.h"

using json = nlohmann::json;
using namespace sherpa_onnx::cxx;  // NOLINT

namespace {

struct Options {
  std::string model;
  std::string tokens;
  std::string data_dir;

  std::string bind = "127.0.0.1";
  int port = 5200;
  int num_threads = 1;
  bool debug = false;

  // TTS generation settings. These are configured once from the
  // command line and apply to the active daemon session.
  int sid = 0;
  float speed = 1.0f;
  float silence_scale = 0.2f;
};

struct TtsEngine {
  OfflineTts tts;
  GenerationConfig gen_config;
  std::mutex mutex;

  explicit TtsEngine(OfflineTts tts_in)
      : tts(std::move(tts_in)) {}
};

static void PrintUsage(const char *prog) {
  std::cerr
      << "Usage:\n"
      << "  " << prog
      << " --model <model.onnx>"
      << " --tokens <tokens.txt>"
      << " --data-dir <espeak-ng-data-dir>"
      << " [--bind <ip>]"
      << " [--port <port>]"
      << " [--num-threads <n>]"
      << " [--debug 0|1]"
      << " [--sid <speaker-id>]"
      << " [--speed <float>]"
      << " [--silence-scale <float>]\n\n"

      << "TTS generation options:\n"
      << "  --sid <speaker-id>\n"
      << "      Speaker ID for multi-speaker models. Default: 0\n\n"

      << "  --speed <float>\n"
      << "      Speech speed. Larger values are faster. Default: 1.0\n\n"

      << "  --silence-scale <float>\n"
      << "      Scale the duration of pauses. Values below 1 shorten\n"
      << "      pauses; values above 1 lengthen them. Default: 0.2\n\n"

      << "Note:\n"
      << "  These settings are configured when the daemon starts and\n"
      << "  apply to all speech generated during that session.\n"
      << "  They are not changed by individual HTTP requests.\n\n"

      << "Example:\n"
      << "  " << prog
      << " --model /opt/tts/en_GB-northern_english_male-medium.onnx"
      << " --tokens /opt/tts/tokens.txt"
      << " --data-dir /opt/tts/espeak-ng-data"
      << " --bind 127.0.0.1"
      << " --port 5200"
      << " --speed 1.05"
      << " --silence-scale 0.25\n";
}

static bool ParseInt(const char *s, int *out) {
  if (!s || *s == '\0') {
    return false;
  }

  char *end = nullptr;
  long v = std::strtol(s, &end, 10);

  if (end == nullptr || *end != '\0') {
    return false;
  }

  *out = static_cast<int>(v);
  return true;
}

static bool ParseFloat(const char *s, float *out) {
  if (!s || *s == '\0') {
    return false;
  }

  char *end = nullptr;
  float v = std::strtof(s, &end);

  if (end == nullptr || *end != '\0') {
    return false;
  }

  *out = v;
  return true;
}

static bool ParseArgs(int argc, char *argv[], Options *opts) {
  for (int i = 1; i < argc; ++i) {
    std::string arg = argv[i];

    auto need_value = [&](const std::string &name) -> const char * {
      if (i + 1 >= argc) {
        std::cerr << "Missing value for " << name << "\n";
        return nullptr;
      }

      return argv[++i];
    };

    if (arg == "--model") {
      const char *v = need_value(arg);
      if (!v) return false;

      opts->model = v;

    } else if (arg == "--tokens") {
      const char *v = need_value(arg);
      if (!v) return false;

      opts->tokens = v;

    } else if (arg == "--data-dir") {
      const char *v = need_value(arg);
      if (!v) return false;

      opts->data_dir = v;

    } else if (arg == "--bind") {
      const char *v = need_value(arg);
      if (!v) return false;

      opts->bind = v;

    } else if (arg == "--port") {
      const char *v = need_value(arg);
      if (!v) return false;

      if (!ParseInt(v, &opts->port)) {
        std::cerr << "Invalid port: " << v << "\n";
        return false;
      }

    } else if (arg == "--num-threads") {
      const char *v = need_value(arg);
      if (!v) return false;

      if (!ParseInt(v, &opts->num_threads)) {
        std::cerr << "Invalid num-threads: " << v << "\n";
        return false;
      }

    } else if (arg == "--debug") {
      const char *v = need_value(arg);
      if (!v) return false;

      int debug_int = 0;

      if (!ParseInt(v, &debug_int)) {
        std::cerr << "Invalid debug value: " << v << "\n";
        return false;
      }

      opts->debug = (debug_int != 0);

    } else if (arg == "--sid") {
      const char *v = need_value(arg);
      if (!v) return false;

      if (!ParseInt(v, &opts->sid)) {
        std::cerr << "Invalid sid: " << v << "\n";
        return false;
      }

    } else if (arg == "--speed") {
      const char *v = need_value(arg);
      if (!v) return false;

      if (!ParseFloat(v, &opts->speed)) {
        std::cerr << "Invalid speed: " << v << "\n";
        return false;
      }

    } else if (arg == "--silence-scale") {
      const char *v = need_value(arg);
      if (!v) return false;

      if (!ParseFloat(v, &opts->silence_scale)) {
        std::cerr << "Invalid silence-scale: " << v << "\n";
        return false;
      }

    } else if (arg == "--help" || arg == "-h") {
      PrintUsage(argv[0]);
      std::exit(0);

    } else {
      std::cerr << "Unknown argument: " << arg << "\n";
      return false;
    }
  }

  if (opts->model.empty()) {
    std::cerr << "--model is required\n";
    return false;
  }

  if (opts->tokens.empty()) {
    std::cerr << "--tokens is required\n";
    return false;
  }

  if (opts->data_dir.empty()) {
    std::cerr << "--data-dir is required\n";
    return false;
  }

  if (opts->port <= 0 || opts->port > 65535) {
    std::cerr << "Port out of range: " << opts->port << "\n";
    return false;
  }

  if (opts->num_threads <= 0) {
    std::cerr << "num-threads must be > 0\n";
    return false;
  }

  if (opts->speed <= 0.0f) {
    std::cerr << "speed must be > 0\n";
    return false;
  }

  // Sherpa-ONNX supports silence_scale in the range [0.01, 10].
  if (opts->silence_scale < 0.01f ||
      opts->silence_scale > 10.0f) {
    std::cerr << "silence-scale must be in the range 0.01 to 10.0\n";
    return false;
  }

  return true;
}

static void AppendLE16(std::vector<uint8_t> &out, uint16_t v) {
  out.push_back(static_cast<uint8_t>(v & 0xff));
  out.push_back(static_cast<uint8_t>((v >> 8) & 0xff));
}

static void AppendLE32(std::vector<uint8_t> &out, uint32_t v) {
  out.push_back(static_cast<uint8_t>(v & 0xff));
  out.push_back(static_cast<uint8_t>((v >> 8) & 0xff));
  out.push_back(static_cast<uint8_t>((v >> 16) & 0xff));
  out.push_back(static_cast<uint8_t>((v >> 24) & 0xff));
}

static std::vector<float> ResampleAudio(
    const std::vector<float> &input,
    int32_t input_rate,
    int32_t output_rate) {
  if (input.empty() || input_rate <= 0 || output_rate <= 0 ||
      input_rate == output_rate) {
    return input;
  }

  // Use a short windowed-sinc filter so Piper's native output is
  // converted directly to AIRadio's 48 kHz playback format without
  // introducing another sample-rate conversion in PipeWire.
  constexpr int kHalfTaps = 16;
  constexpr double kPi = 3.14159265358979323846;

  const double ratio =
      static_cast<double>(output_rate) /
      static_cast<double>(input_rate);
  const double cutoff =
      0.5 * std::min(1.0, ratio);

  const size_t output_size = static_cast<size_t>(std::ceil(
      static_cast<double>(input.size()) * ratio));

  std::vector<float> output(output_size);

  for (size_t out_index = 0; out_index < output_size; ++out_index) {
    const double source_position =
        static_cast<double>(out_index) / ratio;
    const int source_center =
        static_cast<int>(std::floor(source_position));

    double sum = 0.0;
    double weight_sum = 0.0;

    for (int tap = -kHalfTaps + 1; tap <= kHalfTaps; ++tap) {
      const int source_index = source_center + tap;

      if (source_index < 0 ||
          source_index >= static_cast<int>(input.size())) {
        continue;
      }

      const double distance =
          source_position - static_cast<double>(source_index);
      const double x = 2.0 * cutoff * distance;

      double sinc = 1.0;
      if (std::abs(x) > 1e-12) {
        sinc = std::sin(kPi * x) / (kPi * x);
      }

      const double window_position =
          static_cast<double>(tap + kHalfTaps - 1) /
          static_cast<double>(2 * kHalfTaps - 1);
      const double window =
          0.5 - 0.5 * std::cos(2.0 * kPi * window_position);

      const double weight = 2.0 * cutoff * sinc * window;
      sum += static_cast<double>(input[source_index]) * weight;
      weight_sum += weight;
    }

    if (std::abs(weight_sum) > 1e-12) {
      sum /= weight_sum;
    }

    if (sum > 1.0) {
      sum = 1.0;
    } else if (sum < -1.0) {
      sum = -1.0;
    }

    output[out_index] = static_cast<float>(sum);
  }

  return output;
}

static std::vector<uint8_t> FloatSamplesToWav(
    const std::vector<float> &samples,
    int32_t sample_rate) {
  constexpr uint16_t kNumChannels = 1;
  constexpr uint16_t kBitsPerSample = 16;
  constexpr uint16_t kBlockAlign =
      kNumChannels * (kBitsPerSample / 8);

  const uint32_t byte_rate =
      static_cast<uint32_t>(sample_rate) * kBlockAlign;

  const uint32_t data_bytes =
      static_cast<uint32_t>(samples.size() * sizeof(int16_t));

  const uint32_t riff_chunk_size =
      36 + data_bytes;

  std::vector<uint8_t> wav;
  wav.reserve(44 + data_bytes);

  wav.insert(wav.end(), {'R', 'I', 'F', 'F'});
  AppendLE32(wav, riff_chunk_size);
  wav.insert(wav.end(), {'W', 'A', 'V', 'E'});

  wav.insert(wav.end(), {'f', 'm', 't', ' '});
  AppendLE32(wav, 16);            // PCM fmt chunk size
  AppendLE16(wav, 1);             // PCM
  AppendLE16(wav, kNumChannels);  // mono
  AppendLE32(wav, static_cast<uint32_t>(sample_rate));
  AppendLE32(wav, byte_rate);
  AppendLE16(wav, kBlockAlign);
  AppendLE16(wav, kBitsPerSample);

  wav.insert(wav.end(), {'d', 'a', 't', 'a'});
  AppendLE32(wav, data_bytes);

  for (float s : samples) {
    if (s > 1.0f) {
      s = 1.0f;
    }

    if (s < -1.0f) {
      s = -1.0f;
    }

    int16_t pcm =
        static_cast<int16_t>(s * 32767.0f);

    AppendLE16(wav, static_cast<uint16_t>(pcm));
  }

  return wav;
}

static int32_t ProgressCallback(
    const float *samples,
    int32_t num_samples,
    float progress,
    void *arg) {
  (void)samples;
  (void)num_samples;
  (void)progress;
  (void)arg;

  return 1;
}

}  // namespace

int main(int argc, char *argv[]) {
  Options opts;

  if (!ParseArgs(argc, argv, &opts)) {
    PrintUsage(argv[0]);
    return 1;
  }

  //
  // Create exactly one TTS engine for the lifetime of this process.
  //
  // The model itself is not recreated when changing generation
  // parameters. GenerationConfig is simply populated from the
  // command-line options for this active session.
  //
  OfflineTtsConfig config;

  config.model.vits.model = opts.model;
  config.model.vits.tokens = opts.tokens;
  config.model.vits.data_dir = opts.data_dir;

  config.model.num_threads = opts.num_threads;
  config.model.debug = opts.debug;

  TtsEngine engine(OfflineTts::Create(config));

  //
  // Session-wide generation settings.
  //
  // These are intentionally NOT exposed through the HTTP request.
  //
  engine.gen_config.sid = opts.sid;
  engine.gen_config.speed = opts.speed;
  engine.gen_config.silence_scale = opts.silence_scale;

  httplib::Server server;

  server.Get(
      "/health",
      [](const httplib::Request &, httplib::Response &res) {
        res.set_content("ok\n", "text/plain");
      });

  server.Post(
      "/generate",
      [&engine](const httplib::Request &req,
                httplib::Response &res) {
        try {
          auto body = json::parse(req.body);

          if (!body.contains("Text") ||
              !body["Text"].is_string()) {
            res.status = 400;
            res.set_content(
                R"({"error":"missing or invalid Text"})",
                "application/json");
            return;
          }

          std::string text =
              body["Text"].get<std::string>();

          if (text.empty()) {
            res.status = 400;
            res.set_content(
                R"({"error":"Text must not be empty"})",
                "application/json");
            return;
          }

          if (text.size() > 5000) {
            res.status = 400;
            res.set_content(
                R"({"error":"Text too long"})",
                "application/json");
            return;
          }

          GeneratedAudio audio;

          {
            std::lock_guard<std::mutex> lock(engine.mutex);

            //
            // The same OfflineTts instance and the same
            // session-wide GenerationConfig are used for every
            // request.
            //
            audio = engine.tts.Generate(
                text,
                engine.gen_config,
                ProgressCallback);
          }

          if (audio.samples.empty() ||
              audio.sample_rate <= 0) {
            res.status = 500;
            res.set_content(
                R"({"error":"TTS generation failed"})",
                "application/json");
            return;
          }

          constexpr int32_t kOutputSampleRate = 48000;

          std::vector<float> samples = ResampleAudio(
              audio.samples,
              audio.sample_rate,
              kOutputSampleRate);

          std::vector<uint8_t> wav =
              FloatSamplesToWav(
                  samples,
                  kOutputSampleRate);

          res.status = 200;
          res.set_header(
              "Cache-Control",
              "no-store");

          res.set_content(
              reinterpret_cast<const char *>(wav.data()),
              wav.size(),
              "audio/wav");

        } catch (const std::exception &e) {
          res.status = 400;

          json err = {
              {"error",
               std::string("invalid request: ") + e.what()}};

          res.set_content(
              err.dump(),
              "application/json");
        }
      });

  std::cout
      << "TTS daemon listening on http://"
      << opts.bind << ":"
      << opts.port
      << "/generate\n";

  std::cout
      << "Model          : "
      << opts.model << "\n";

  std::cout
      << "Tokens         : "
      << opts.tokens << "\n";

  std::cout
      << "Data dir       : "
      << opts.data_dir << "\n";

  std::cout
      << "Speaker ID     : "
      << opts.sid << "\n";

  std::cout
      << "Speed          : "
      << opts.speed << "\n";

  std::cout
      << "Silence scale  : "
      << opts.silence_scale << "\n";

  std::cout << std::flush;

  if (!server.listen(
          opts.bind.c_str(),
          opts.port)) {
    std::cerr
        << "Failed to listen on "
        << opts.bind << ":"
        << opts.port << "\n";

    return 1;
  }

  return 0;
}