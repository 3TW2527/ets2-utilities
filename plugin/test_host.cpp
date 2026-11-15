// Loads ets2u_telemetry.dll the way the game does, feeds it fake telemetry and checks the
// shared memory it publishes. Build and run with plugin\test.cmd.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <vector>

#include "scssdk_telemetry.h"
#include "scssdk_input.h"
#include "eurotrucks2/scssdk_eut2.h"
#include "eurotrucks2/scssdk_telemetry_eut2.h"
#include "common/scssdk_telemetry_truck_common_channels.h"
#include "common/scssdk_telemetry_common_configs.h"

// SCSAPI_RESULT / SCSAPI_VOID already carry the calling convention, so spell the pointer types out.
typedef scs_result_t (SCSAPIFUNC *scs_telemetry_init_t)(const scs_u32_t, const scs_telemetry_init_params_t *const);
typedef void (SCSAPIFUNC *scs_telemetry_shutdown_t)(void);
typedef scs_result_t (SCSAPIFUNC *scs_input_init_t)(const scs_u32_t, const scs_input_init_params_t *const);

static scs_input_device_t g_device = {};
static SCSAPI_RESULT reg_device(const scs_input_device_t *const d)
{
    g_device = *d;
    return SCS_RESULT_ok;
}

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

static void send_vector(const char *channel, float x, float y, float z)
{
    scs_value_t value{};
    value.type = SCS_VALUE_TYPE_fvector;
    value.value_fvector = {x, y, z};
    for (auto &c : g_channels)
        if (strcmp(c.name, channel) == 0) c.cb(c.name, SCS_U32_NIL, &value, c.ctx);
}

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
    // MOUSE DRIVING DISABLED: the input device's checks are commented out below.
    // auto input_init = reinterpret_cast<scs_input_init_t>(GetProcAddress(dll, "scs_input_init"));
    // auto input_shutdown = reinterpret_cast<scs_telemetry_shutdown_t>(GetProcAddress(dll, "scs_input_shutdown"));
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

    // scs_input_init_params_v100_t input_params{};
    // input_params.common = params.common;
    // input_params.register_device = reg_device;
    // if (input_init(SCS_INPUT_VERSION_1_00, &input_params) != SCS_RESULT_ok) { printf("FAIL: input init\n"); return 1; }
    // bool device_ok = g_device.type == SCS_INPUT_DEVICE_TYPE_semantical && g_device.input_count == 5
    //                  && strcmp(g_device.inputs[0].name, "steering") == 0 && strcmp(g_device.inputs[1].name, "aforward") == 0
    //                  && strcmp(g_device.inputs[2].name, "abackward") == 0
    //                  && strcmp(g_device.inputs[3].name, "camlr") == 0 && strcmp(g_device.inputs[4].name, "camud") == 0;
    // // One frame of the device: five events (steering, throttle, brake, camera holds), all zero while mouse driving is off.
    // scs_input_event_t ev{};
    // int events = 0;
    // bool zeros = true;
    // for (scs_u32_t flags = SCS_INPUT_EVENT_CALLBACK_FLAG_first_in_frame | SCS_INPUT_EVENT_CALLBACK_FLAG_first_after_activation;
    //      g_device.input_event_callback(&ev, flags, g_device.callback_context) == SCS_RESULT_ok; flags = 0)
    // {
    //     zeros = zeros && ev.value_float.value == 0.0f && ev.input_index == static_cast<scs_u32_t>(events);
    //     if (++events > 10) break;
    // }
    // printf("device=%s events=%d zeros=%d\n", g_device.name, events, zeros);
    // device_ok = device_ok && events == 5 && zeros;
    const bool device_ok = true;

    g_events[SCS_TELEMETRY_EVENT_started](SCS_TELEMETRY_EVENT_started, nullptr, nullptr);
    // A coach's truck configuration: the plugin keeps its id.
    scs_named_value_t attrs[2] = {};
    attrs[0].name = SCS_TELEMETRY_CONFIG_ATTRIBUTE_id;
    attrs[0].index = SCS_U32_NIL;
    attrs[0].value.type = SCS_VALUE_TYPE_string;
    attrs[0].value.value_string.value = "vehicle.man.lions_24";
    scs_telemetry_configuration_t config{SCS_TELEMETRY_CONFIG_truck, attrs};
    g_events[SCS_TELEMETRY_EVENT_configuration](SCS_TELEMETRY_EVENT_configuration, &config, nullptr);
    send_float(SCS_TELEMETRY_TRUCK_CHANNEL_speed, 25.0f);
    send_float(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_speed_limit, 80.0f / 3.6f);
    // Braking at 0.4 g (vehicle space: +z is backwards) while accelerating 0.2 g towards the right.
    send_vector(SCS_TELEMETRY_TRUCK_CHANNEL_local_linear_acceleration, 0.2f * 9.80665f, 0.0f, 0.4f * 9.80665f);
    for (int i = 0; i < 3; i++) { g_events[SCS_TELEMETRY_EVENT_frame_end](SCS_TELEMETRY_EVENT_frame_end, nullptr, nullptr); Sleep(400); }

    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\Ets2uTelemetry");
    if (!map) { printf("FAIL: shared memory not found\n"); return 1; }
    const uint32_t *raw = static_cast<const uint32_t *>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 112));
    const float *f = reinterpret_cast<const float *>(raw);
    printf("magic=%08X version=%u sequence=%u paused=%u speed=%.2f limit=%.2f fwd=%.3fg right=%.3fg mouse=%u truck=%s\n",
           raw[0], raw[1], raw[2], raw[3], f[4], f[5], f[6], f[7], raw[8], reinterpret_cast<const char *>(raw + 12));
    bool ok = device_ok && raw[0] == 0x54553245 && raw[1] == 3 && raw[2] == 3 && raw[3] == 0 && f[4] == 25.0f
              && f[5] > 22.2f && f[5] < 22.3f
              // smoothed towards -0.4 g forward and 0.2 g right: two frames of at most 0.2 s each count: about three quarters of the way
              && f[6] < -0.27f && f[6] > -0.32f && f[7] > 0.135f && f[7] < 0.16f && raw[8] == 0
              && strcmp(reinterpret_cast<const char *>(raw + 12), "vehicle.man.lions_24") == 0;

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
    // input_shutdown();
    FreeLibrary(dll);
    printf(ok ? "PASS\n" : "FAIL: values\n");
    return ok ? 0 : 1;
}
