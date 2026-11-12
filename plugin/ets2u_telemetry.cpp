// ETS2 Utilities telemetry plugin.
//
// Publishes the truck's speed and the navigation speed limit to shared memory
// (Local\Ets2uTelemetry) so the app's overlay can show km/h and mph together.
// Read-only: it never changes anything in the game.
//
// Built against SCS's telemetry SDK (MIT licence), tools/scs_sdk/include.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdint>
#include <cstring>

#include "scssdk_telemetry.h"
#include "eurotrucks2/scssdk_eut2.h"
#include "eurotrucks2/scssdk_telemetry_eut2.h"
#include "amtrucks/scssdk_ats.h"
#include "amtrucks/scssdk_telemetry_ats.h"
#include "common/scssdk_telemetry_common_channels.h"
#include "common/scssdk_telemetry_truck_common_channels.h"

// Must match Ets2u.Core/Telemetry/TelemetryReader.cs.
#pragma pack(push, 4)
struct Ets2uTelemetry
{
    uint32_t magic;           // 'E2UT'
    uint32_t version;         // layout version, 1
    uint32_t sequence;        // bumped every frame the game simulates
    uint32_t paused;          // 1 while the game is paused or in menus
    float speed_ms;           // truck.speed, m/s (negative when reversing)
    float speed_limit_ms;     // truck.navigation.speed.limit, m/s (0 = none)
};
#pragma pack(pop)

static const wchar_t *const kMapName = L"Local\\Ets2uTelemetry";
static const uint32_t kMagic = 0x54553245; // "E2UT" little-endian

static HANDLE g_mapping = nullptr;
static Ets2uTelemetry *g_shared = nullptr;
static scs_log_t g_log = nullptr;

static void log_message(const char *text)
{
    if (g_log) g_log(SCS_LOG_TYPE_message, text);
}

static SCSAPI_VOID store_float(const scs_string_t, const scs_u32_t, const scs_value_t *const value, const scs_context_t context)
{
    // With SCS_TELEMETRY_CHANNEL_FLAG_no_value a missing value arrives as nullptr.
    *static_cast<float *>(context) = value ? value->value_float.value : 0.0f;
}

static SCSAPI_VOID on_frame_end(const scs_event_t, const void *const, const scs_context_t)
{
    if (g_shared) InterlockedIncrement(reinterpret_cast<volatile LONG *>(&g_shared->sequence));
}

static SCSAPI_VOID on_pause(const scs_event_t event, const void *const, const scs_context_t)
{
    if (g_shared) g_shared->paused = event == SCS_TELEMETRY_EVENT_paused ? 1 : 0;
}

static void release_memory()
{
    if (g_shared) UnmapViewOfFile(g_shared);
    if (g_mapping) CloseHandle(g_mapping);
    g_shared = nullptr;
    g_mapping = nullptr;
}

SCSAPI_RESULT scs_telemetry_init(const scs_u32_t version, const scs_telemetry_init_params_t *const params)
{
    if (version != SCS_TELEMETRY_VERSION_1_01) return SCS_RESULT_unsupported;

    const auto *const p = static_cast<const scs_telemetry_init_params_v101_t *>(params);
    g_log = p->common.log;

    if (strcmp(p->common.game_id, SCS_GAME_ID_EUT2) != 0 && strcmp(p->common.game_id, SCS_GAME_ID_ATS) != 0)
    {
        log_message("ets2u: unsupported game, plugin disabled");
        return SCS_RESULT_unsupported;
    }

    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Ets2uTelemetry), kMapName);
    if (!g_mapping) return SCS_RESULT_generic_error;
    g_shared = static_cast<Ets2uTelemetry *>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Ets2uTelemetry)));
    if (!g_shared)
    {
        release_memory();
        return SCS_RESULT_generic_error;
    }

    memset(g_shared, 0, sizeof(Ets2uTelemetry));
    g_shared->magic = kMagic;
    g_shared->version = 1;
    g_shared->paused = 1;

    const bool events_ok =
        p->register_for_event(SCS_TELEMETRY_EVENT_frame_end, on_frame_end, nullptr) == SCS_RESULT_ok &&
        p->register_for_event(SCS_TELEMETRY_EVENT_paused, on_pause, nullptr) == SCS_RESULT_ok &&
        p->register_for_event(SCS_TELEMETRY_EVENT_started, on_pause, nullptr) == SCS_RESULT_ok;
    if (!events_ok)
    {
        release_memory();
        return SCS_RESULT_generic_error;
    }

    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_speed, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_shared->speed_ms);
    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_speed_limit, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_shared->speed_limit_ms);

    log_message("ets2u: telemetry plugin ready");
    return SCS_RESULT_ok;
}

SCSAPI_VOID scs_telemetry_shutdown(void)
{
    // The game unregisters every callback itself on shutdown.
    g_log = nullptr;
    release_memory();
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH) release_memory();
    return TRUE;
}
