// Loads ets2u_telemetry.dll the way the game does, feeds it fake telemetry and checks the
// shared memory it publishes. Build and run with plugin\test.cmd.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <vector>

#include "scssdk_telemetry.h"
#include "eurotrucks2/scssdk_eut2.h"
#include "eurotrucks2/scssdk_telemetry_eut2.h"
#include "common/scssdk_telemetry_truck_common_channels.h"

// SCSAPI_RESULT / SCSAPI_VOID already carry the calling convention, so spell the pointer types out.
typedef scs_result_t (SCSAPIFUNC *scs_telemetry_init_t)(const scs_u32_t, const scs_telemetry_init_params_t *const);
typedef void (SCSAPIFUNC *scs_telemetry_shutdown_t)(void);

struct Channel { const char *name; scs_telemetry_channel_callback_t cb; scs_context_t ctx; };
static std::vector<Channel> g_channels;
static scs_telemetry_event_callback_t g_events[16] = {};

static SCSAPI_RESULT reg_event(const scs_event_t e, const scs_telemetry_event_callback_t cb, const scs_context_t)
{
    g_events[e] = cb;
    return SCS_RESULT_ok;
}
static SCSAPI_RESULT unreg_event(const scs_event_t) { return SCS_RESULT_ok; }
static SCSAPI_RESULT reg_channel(const scs_string_t name, const scs_u32_t, const scs_value_type_t, const scs_u32_t,
                                 const scs_telemetry_channel_callback_t cb, const scs_context_t ctx)
{
    g_channels.push_back({name, cb, ctx});
    return SCS_RESULT_ok;
}
static SCSAPI_RESULT unreg_channel(const scs_string_t, const scs_u32_t, const scs_value_type_t) { return SCS_RESULT_ok; }
static SCSAPI_VOID log(const scs_log_type_t, const scs_string_t text) { printf("[game log] %s\n", text); }

static void send_float(const char *channel, float v)
{
    scs_value_t value{};
    value.type = SCS_VALUE_TYPE_float;
    value.value_float.value = v;
    for (auto &c : g_channels)
        if (strcmp(c.name, channel) == 0) c.cb(c.name, SCS_U32_NIL, &value, c.ctx);
}

int main(int argc, char **argv)
{
    HMODULE dll = LoadLibraryA(argc > 1 ? argv[1] : "ets2u_telemetry.dll");
    if (!dll) { printf("FAIL: LoadLibrary\n"); return 1; }
    auto init = reinterpret_cast<scs_telemetry_init_t>(GetProcAddress(dll, "scs_telemetry_init"));
    auto shutdown = reinterpret_cast<scs_telemetry_shutdown_t>(GetProcAddress(dll, "scs_telemetry_shutdown"));
    if (!init || !shutdown) { printf("FAIL: exports missing\n"); return 1; }

    scs_telemetry_init_params_v101_t params{};
    params.common.log = log;
    params.common.game_name = "Euro Truck Simulator 2";
    params.common.game_id = SCS_GAME_ID_EUT2;
    params.common.game_version = SCS_TELEMETRY_EUT2_GAME_VERSION_CURRENT;
    params.register_for_event = reg_event;
    params.unregister_from_event = unreg_event;
    params.register_for_channel = reg_channel;
    params.unregister_from_channel = unreg_channel;
    if (init(SCS_TELEMETRY_VERSION_1_01, &params) != SCS_RESULT_ok) { printf("FAIL: init\n"); return 1; }

    g_events[SCS_TELEMETRY_EVENT_started](SCS_TELEMETRY_EVENT_started, nullptr, nullptr);
    send_float(SCS_TELEMETRY_TRUCK_CHANNEL_speed, 25.0f);
    send_float(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_speed_limit, 80.0f / 3.6f);
    for (int i = 0; i < 3; i++) g_events[SCS_TELEMETRY_EVENT_frame_end](SCS_TELEMETRY_EVENT_frame_end, nullptr, nullptr);

    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\Ets2uTelemetry");
    if (!map) { printf("FAIL: shared memory not found\n"); return 1; }
    const uint32_t *raw = static_cast<const uint32_t *>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 24));
    const float *f = reinterpret_cast<const float *>(raw);
    printf("magic=%08X version=%u sequence=%u paused=%u speed=%.2f limit=%.2f\n", raw[0], raw[1], raw[2], raw[3], f[4], f[5]);
    bool ok = raw[0] == 0x54553245 && raw[1] == 1 && raw[2] == 3 && raw[3] == 0 && f[4] == 25.0f && f[5] > 22.2f && f[5] < 22.3f;

    g_events[SCS_TELEMETRY_EVENT_paused](SCS_TELEMETRY_EVENT_paused, nullptr, nullptr);
    ok = ok && raw[3] == 1;

    if (argc > 2) { printf("holding shared memory for %s ms\n", argv[2]); fflush(stdout); Sleep(atoi(argv[2])); }

    // "drive <ms>": keep simulating frames with a changing speed, e.g. to look at the overlay.
    if (argc > 4 && strcmp(argv[3], "drive") == 0)
    {
        g_events[SCS_TELEMETRY_EVENT_started](SCS_TELEMETRY_EVENT_started, nullptr, nullptr);
        for (int t = 0; t < atoi(argv[4]); t += 50)
        {
            send_float(SCS_TELEMETRY_TRUCK_CHANNEL_speed, 20.0f + 5.0f * (t % 4000) / 4000.0f);
            g_events[SCS_TELEMETRY_EVENT_frame_end](SCS_TELEMETRY_EVENT_frame_end, nullptr, nullptr);
            Sleep(50);
        }
    }

    UnmapViewOfFile(raw);
    CloseHandle(map);
    shutdown();
    FreeLibrary(dll);
    printf(ok ? "PASS\n" : "FAIL: values\n");
    return ok ? 0 : 1;
}
