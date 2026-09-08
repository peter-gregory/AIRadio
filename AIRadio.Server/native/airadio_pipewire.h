#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define AIRADIO_API __declspec(dllexport)
#else
#define AIRADIO_API __attribute__((visibility("default")))
#endif

typedef struct AIRadioPipeWire AIRadioPipeWire;

typedef struct AIRadioPcmSegment
{
    const uint8_t *data;
    size_t size;
} AIRadioPcmSegment;

typedef void (*AIRadioPlaybackCallback)(void *user_data);
typedef void (*AIRadioErrorCallback)(void *user_data, int error_code, const char *message);

AIRADIO_API AIRadioPipeWire *airadio_pw_create(uint32_t sample_rate, uint32_t channels, uint32_t bits_per_sample);
AIRADIO_API int airadio_pw_start(AIRadioPipeWire *client);
AIRADIO_API int airadio_pw_enqueue(AIRadioPipeWire *client, const AIRadioPcmSegment *segments, size_t segment_count);
AIRADIO_API int airadio_pw_end_utterance(AIRadioPipeWire *client, int cancel);
AIRADIO_API int airadio_pw_clear(AIRadioPipeWire *client);
AIRADIO_API int airadio_pw_set_volume(AIRadioPipeWire *client, float volume);
AIRADIO_API float airadio_pw_get_volume(AIRadioPipeWire *client);
AIRADIO_API uint64_t airadio_pw_queued_frames(AIRadioPipeWire *client);
AIRADIO_API uint64_t airadio_pw_outstanding_frames(AIRadioPipeWire *client);
AIRADIO_API void airadio_pw_set_playback_complete_callback(AIRadioPipeWire *client, AIRadioPlaybackCallback callback, void *user_data);
AIRADIO_API void airadio_pw_set_error_callback(AIRadioPipeWire *client, AIRadioErrorCallback callback, void *user_data);
AIRADIO_API const char *airadio_pw_last_error(AIRadioPipeWire *client);
AIRADIO_API void airadio_pw_destroy(AIRadioPipeWire *client);

#ifdef __cplusplus
}
#endif
