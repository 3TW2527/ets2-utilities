// ETS2 Utilities plugin.
//
// Telemetry: publishes the truck's speed, the navigation speed limit and its g-forces to shared
// memory (Local\Ets2uTelemetry) for the app's overlays (both speed units, driving alerts).
//
// Input: an optional OMSI-style mouse driving device. While it's on, the mouse's place across the
// game window is the steering and its place above or below the middle is the throttle or the
// brake, as OMSI 2 has it (the maths follows openOMSI's port of Omsi.exe, see mouse_update).
// It feeds the game's own "semantical" steering / aforward / abackward inputs, which every
// controls.sii mixes in. The game reads the mouse through DirectInput and keeps it from other
// programs while it has the focus, so the plugin reads it the same way, from inside the game, as a
// second (non-exclusive) DirectInput mouse. The app changes the camera's mixes in controls.sii so
// the plugin can hold the camera still while the mouse steers (see kInputs).
//
// Built against SCS's telemetry and input SDK (MIT licence), tools/scs_sdk/include.

#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>

#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cwchar>

#include "scssdk_telemetry.h"
#include "scssdk_input.h"
#include "eurotrucks2/scssdk_eut2.h"
#include "eurotrucks2/scssdk_telemetry_eut2.h"
#include "eurotrucks2/scssdk_input_eut2.h"
#include "amtrucks/scssdk_ats.h"
#include "amtrucks/scssdk_telemetry_ats.h"
#include "amtrucks/scssdk_input_ats.h"
#include "common/scssdk_telemetry_common_channels.h"
#include "common/scssdk_telemetry_common_configs.h"
#include "common/scssdk_telemetry_truck_common_channels.h"

// Must match Ets2u.Core/Telemetry/Telemetry.cs.
#pragma pack(push, 4)
struct Ets2uTelemetry
{
    uint32_t magic;           // 'E2UT'
    uint32_t version;         // layout version, 3
    uint32_t sequence;        // bumped every frame the game simulates
    uint32_t paused;          // 1 while the game is paused or in menus
    float speed_ms;           // truck.speed, m/s (negative when reversing)
    float speed_limit_ms;     // truck.navigation.speed.limit, m/s (0 = none)
    float accel_forward_g;    // truck.local.acceleration.linear, forward, in g, smoothed over ~0.3 s
    float accel_right_g;      // the same, towards the truck's right
    uint32_t mouse_state;     // 0 off, 1 steering, 2 held (looking round, a menu, alt-tabbed), 3 the mouse can't be read
    float mouse_steer;        // -1 full left .. 1 full right
    float mouse_throttle;     // 0..1
    float mouse_brake;        // 0..1
    char truck_id[64];        // the truck config's id, e.g. "vehicle.man.lions_24" (a coach), "" when none
};
#pragma pack(pop)

static const wchar_t *const kMapName = L"Local\\Ets2uTelemetry";
static const uint32_t kMagic = 0x54553245; // "E2UT" little-endian
static const uint32_t kVersion = 3;
static const float kGravity = 9.80665f;
static const float kAccelSmoothing = 0.3f; // seconds

static HANDLE g_mapping = nullptr;
static Ets2uTelemetry *g_shared = nullptr;
static scs_log_t g_log = nullptr;

static scs_value_fvector_t g_accel = {};
static float g_effective_steering = 0.0f; // counterclockwise: + is left
static float g_input_steering = 0.0f;     // what the game took from the controls, counterclockwise
static LARGE_INTEGER g_frequency = {};
static LARGE_INTEGER g_last_frame = {};

static void log_message(const char *text)
{
    if (g_log) g_log(SCS_LOG_TYPE_message, text);
}

static float seconds_since(LARGE_INTEGER &last)
{
    if (!g_frequency.QuadPart) QueryPerformanceFrequency(&g_frequency);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const float dt = last.QuadPart ? float(now.QuadPart - last.QuadPart) / float(g_frequency.QuadPart) : 0.0f;
    last = now;
    return dt < 0.0f ? 0.0f : dt > 0.2f ? 0.2f : dt;
}

// Shared memory exists once per process: whichever API (telemetry or input) starts first makes it.
static bool open_memory()
{
    if (g_shared) return true;
    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Ets2uTelemetry), kMapName);
    if (!g_mapping) return false;
    g_shared = static_cast<Ets2uTelemetry *>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Ets2uTelemetry)));
    if (!g_shared)
    {
        CloseHandle(g_mapping);
        g_mapping = nullptr;
        return false;
    }
    memset(g_shared, 0, sizeof(Ets2uTelemetry));
    g_shared->magic = kMagic;
    g_shared->version = kVersion;
    g_shared->paused = 1;
    return true;
}

static void release_memory()
{
    if (g_shared) UnmapViewOfFile(g_shared);
    if (g_mapping) CloseHandle(g_mapping);
    g_shared = nullptr;
    g_mapping = nullptr;
}

// ---------------------------------------------------------------- telemetry

static SCSAPI_VOID store_float(const scs_string_t, const scs_u32_t, const scs_value_t *const value, const scs_context_t context)
{
    // With SCS_TELEMETRY_CHANNEL_FLAG_no_value a missing value arrives as nullptr.
    *static_cast<float *>(context) = value ? value->value_float.value : 0.0f;
}

static SCSAPI_VOID store_fvector(const scs_string_t, const scs_u32_t, const scs_value_t *const value, const scs_context_t context)
{
    *static_cast<scs_value_fvector_t *>(context) = value ? value->value_fvector : scs_value_fvector_t{};
}

static SCSAPI_VOID on_frame_end(const scs_event_t, const void *const, const scs_context_t)
{
    if (!g_shared) return;
    // Vehicle space: x right, y up, z backwards. The raw value shakes with every bump in the road,
    // so it is smoothed the way a telematics box filters its accelerometer.
    const float dt = seconds_since(g_last_frame);
    const float k = 1.0f - std::exp(-dt / kAccelSmoothing);
    g_shared->accel_forward_g += (-g_accel.z / kGravity - g_shared->accel_forward_g) * k;
    g_shared->accel_right_g += (g_accel.x / kGravity - g_shared->accel_right_g) * k;
    InterlockedIncrement(reinterpret_cast<volatile LONG *>(&g_shared->sequence));
}

// The truck's id (vehicle.<brand>.<model>), so the app can tell a coach from a truck.
static SCSAPI_VOID on_configuration(const scs_event_t, const void *const event_info, const scs_context_t)
{
    const auto *config = static_cast<const scs_telemetry_configuration_t *>(event_info);
    if (!g_shared || !config || strcmp(config->id, SCS_TELEMETRY_CONFIG_truck) != 0) return;
    char id[sizeof(g_shared->truck_id)] = {};
    for (const scs_named_value_t *a = config->attributes; a->name; a++)
        if (strcmp(a->name, SCS_TELEMETRY_CONFIG_ATTRIBUTE_id) == 0 && a->value.type == SCS_VALUE_TYPE_string && a->value.value_string.value)
            strncpy_s(id, a->value.value_string.value, _TRUNCATE);
    memcpy(g_shared->truck_id, id, sizeof(id));
}

static SCSAPI_VOID on_pause(const scs_event_t event, const void *const, const scs_context_t)
{
    if (g_shared) g_shared->paused = event == SCS_TELEMETRY_EVENT_paused ? 1 : 0;
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

    if (!open_memory()) return SCS_RESULT_generic_error;
    g_shared->paused = 1;

    const bool events_ok =
        p->register_for_event(SCS_TELEMETRY_EVENT_frame_end, on_frame_end, nullptr) == SCS_RESULT_ok &&
        p->register_for_event(SCS_TELEMETRY_EVENT_paused, on_pause, nullptr) == SCS_RESULT_ok &&
        p->register_for_event(SCS_TELEMETRY_EVENT_started, on_pause, nullptr) == SCS_RESULT_ok &&
        p->register_for_event(SCS_TELEMETRY_EVENT_configuration, on_configuration, nullptr) == SCS_RESULT_ok;
    if (!events_ok) return SCS_RESULT_generic_error;

    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_speed, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_shared->speed_ms);
    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_navigation_speed_limit, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_shared->speed_limit_ms);
    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_local_linear_acceleration, SCS_U32_NIL, SCS_VALUE_TYPE_fvector,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_fvector, &g_accel);
    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_effective_steering, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_effective_steering);
    p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_input_steering, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                            SCS_TELEMETRY_CHANNEL_FLAG_no_value, store_float, &g_input_steering);

    log_message("ets2u: telemetry plugin ready");
    return SCS_RESULT_ok;
}

SCSAPI_VOID scs_telemetry_shutdown(void)
{
    // The game unregisters every callback itself on shutdown. The input side may still use the memory.
    g_log = nullptr;
}

// ---------------------------------------------------------------- mouse driving

// MOUSE DRIVING DISABLED: everything down to the matching #endif (the input device, its exports
// scs_input_init / scs_input_shutdown) is compiled out. To bring it back, define ETS2U_MOUSE_DRIVING
// (e.g. /DETS2U_MOUSE_DRIVING in build.cmd) and restore the two scs_input_* lines in ets2u_telemetry.def.
#ifdef ETS2U_MOUSE_DRIVING

// Settings, written by the app to %APPDATA%\ets2u\mouse_drive.ini and re-read when it changes.
struct MouseConfig
{
    bool enabled = false;
    bool toggle_middle = false; // the middle mouse button switches it on and off ...
    int toggle_vk = VK_F8;      // ... or this key
    float sensitivity = 1.0f;   // OMSI's mouse_sens: the full lock in 1/sens of the window's width
    float pedal_strength = 1.0f;
    float pedal_deadzone = 0.05f;
    bool smooth = true;
    bool trace = true;          // %LOCALAPPDATA%\ets2u\mouse_trace.csv while it steers
};

static MouseConfig g_config;
static wchar_t g_config_path[MAX_PATH] = {};
static wchar_t g_trace_path[MAX_PATH] = {};
static FILETIME g_config_time = {};
static float g_config_check = 0.0f;

static IDirectInput8W *g_dinput = nullptr;
static IDirectInputDevice8W *g_mouse = nullptr;
static float g_mouse_retry = 0.0f;

struct MouseDrive
{
    bool on = false;
    float x = 0.0f, y = 0.0f;  // the steering point, counts from the middle (may lie past the window's edges)
    float kmh = 0.0f;          // speed smoothed over 0.4 s: the speed divisor would make the wheel tremble
    float fade = 0.0f;         // seconds of easing in left after switching on
    float steer = 0.0f, throttle = 0.0f, brake = 0.0f;
    bool key_down = false;
    LARGE_INTEGER last = {};
    float pending = 0.0f;      // seconds since the last update (the game asks far more often than it needs)
    float sent[5] = {-2.0f, -2.0f, -2.0f, -2.0f, -2.0f}; // the values last reported to the game
    int emit = 0;              // next input to look at this round
    FILE *trace = nullptr;
    float trace_due = 0.0f;
    int trace_lines = 0;
    float time = 0.0f;
    int moved = 0;             // movement read since the last trace line, for the trace
};
static MouseDrive g_drive;

static bool game_in_foreground(HWND *window = nullptr)
{
    const HWND fg = GetForegroundWindow();
    DWORD pid = 0;
    if (fg) GetWindowThreadProcessId(fg, &pid);
    if (window) *window = fg;
    return fg && pid == GetCurrentProcessId();
}

static bool key_held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

static void read_config()
{
    WIN32_FILE_ATTRIBUTE_DATA info;
    if (!g_config_path[0] || !GetFileAttributesExW(g_config_path, GetFileExInfoStandard, &info))
    {
        g_config = MouseConfig{};
        g_config_time = {};
        return;
    }
    if (CompareFileTime(&info.ftLastWriteTime, &g_config_time) == 0) return;
    g_config_time = info.ftLastWriteTime;
    auto number = [](const wchar_t *key, float fallback) {
        wchar_t text[32];
        GetPrivateProfileStringW(L"mouse_drive", key, L"", text, 32, g_config_path);
        return text[0] ? static_cast<float>(_wtof(text)) : fallback;
    };
    MouseConfig c;
    c.enabled = number(L"enabled", 0) != 0;
    wchar_t toggle[32];
    GetPrivateProfileStringW(L"mouse_drive", L"toggle", L"key", toggle, 32, g_config_path);
    c.toggle_middle = _wcsicmp(toggle, L"middle") == 0;
    c.toggle_vk = static_cast<int>(number(L"toggle_vk", VK_F8));
    c.sensitivity = fminf(fmaxf(number(L"sensitivity", 1.0f), 0.2f), 5.0f);
    c.pedal_strength = fminf(fmaxf(number(L"pedal_strength", 1.0f), 0.25f), 4.0f);
    c.pedal_deadzone = fminf(fmaxf(number(L"pedal_deadzone", 0.05f), 0.0f), 0.5f);
    c.smooth = number(L"smooth", 1) != 0;
    c.trace = number(L"trace", 1) != 0;
    g_config = c;
}

static void mouse_release()
{
    if (g_mouse)
    {
        g_mouse->Unacquire();
        g_mouse->Release();
    }
    if (g_dinput) g_dinput->Release();
    g_mouse = nullptr;
    g_dinput = nullptr;
}

/// The mouse's movement since the last call, through a DirectInput mouse of the plugin's own
/// (non-exclusive, background: it shares the mouse with the game's). False when it can't be read.
static bool mouse_read(HWND window, float dt, LONG *dx, LONG *dy)
{
    *dx = *dy = 0;
    if (!g_mouse)
    {
        g_mouse_retry -= dt;
        if (g_mouse_retry > 0.0f || !window) return false;
        g_mouse_retry = 2.0f;
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&mouse_release), &self);
        const bool ok =
            SUCCEEDED(DirectInput8Create(self, DIRECTINPUT_VERSION, IID_IDirectInput8W, reinterpret_cast<void **>(&g_dinput), nullptr)) &&
            SUCCEEDED(g_dinput->CreateDevice(GUID_SysMouse, &g_mouse, nullptr)) &&
            SUCCEEDED(g_mouse->SetDataFormat(&c_dfDIMouse2)) &&
            SUCCEEDED(g_mouse->SetCooperativeLevel(window, DISCL_NONEXCLUSIVE | DISCL_BACKGROUND));
        if (!ok)
        {
            log_message("ets2u: couldn't open the mouse through DirectInput");
            mouse_release();
            return false;
        }
        log_message("ets2u: reading the mouse through DirectInput");
    }
    DIMOUSESTATE2 state;
    HRESULT hr = g_mouse->GetDeviceState(sizeof(state), &state);
    if (hr == DIERR_INPUTLOST || hr == DIERR_NOTACQUIRED)
    {
        g_mouse->Acquire();
        hr = g_mouse->GetDeviceState(sizeof(state), &state);
    }
    if (FAILED(hr)) return false;
    // Relative axes: the movement since the last read, in the mouse's own counts.
    *dx = state.lX;
    *dy = state.lY;
    return true;
}

static float clampf(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

// openOMSI's mouse_pedal_target, with a small dead zone round the middle so the truck can coast.
static float pedal_target(float input)
{
    const float dz = g_config.pedal_deadzone;
    const float v = clampf((input - dz) / (1.0f - dz), 0.0f, 1.0f);
    return fminf(v * g_config.pedal_strength, 1.0f);
}

// openOMSI's mouse_follow: how much of the way to the target is left behind this frame.
static float follow(float fade, float dt)
{
    if (fade > 0.0f) return std::exp(-0.6931472f / fade * dt);
    return g_config.smooth ? std::exp(-dt / 0.06f) : 0.0f;
}

static void trace_close()
{
    if (g_drive.trace) fclose(g_drive.trace);
    g_drive.trace = nullptr;
}

static void trace_open()
{
    trace_close();
    g_drive.trace_lines = 0;
    g_drive.time = 0.0f;
    if (!g_config.trace || !g_trace_path[0]) return;
    if (_wfopen_s(&g_drive.trace, g_trace_path, L"w") == 0 && g_drive.trace)
        fprintf(g_drive.trace, "t,dt,state,moved,x,y,kmh,target,steer_out,input_steering,effective_steering,throttle,brake\n");
}

static void mouse_update(float dt)
{
    g_drive.time += dt;
    g_config_check -= dt;
    if (g_config_check <= 0.0f)
    {
        read_config();
        g_config_check = 1.0f;
    }

    HWND window = nullptr;
    const bool foreground = game_in_foreground(&window);

    // Movement since the last update (read even while off, so switching on starts from now).
    LONG dx = 0, dy = 0;
    const bool feed = g_config.enabled && mouse_read(foreground ? window : nullptr, dt, &dx, &dy);
    g_drive.moved += abs(dx) + abs(dy);

    // Switching on and off: the key or the middle button, pressed with the game in front.
    const int vk = g_config.toggle_middle ? VK_MBUTTON : g_config.toggle_vk;
    const bool key = g_config.enabled && foreground && vk > 0 && key_held(vk);
    bool toggle = key && !g_drive.key_down;
    g_drive.key_down = key;
    if (!g_config.enabled && g_drive.on) toggle = true;

    if (toggle)
    {
        g_drive.on = !g_drive.on;
        if (g_drive.on)
        {
            // Start from where the wheel stands, so switching on doesn't yank it.
            RECT r = {0, 0, 1920, 1080};
            if (window) GetClientRect(window, &r);
            const float half = fmaxf(float(r.right - r.left) * 0.5f, 1.0f);
            const float kmh = g_shared ? fmaxf(g_shared->speed_ms * 3.6f, 0.0f) : 0.0f;
            g_drive.kmh = kmh;
            g_drive.steer = clampf(-g_effective_steering, -1.0f, 1.0f);
            g_drive.x = g_drive.steer * fmaxf(kmh / 10.0f, 1.0f) / g_config.sensitivity * half;
            g_drive.y = 0.0f;
            g_drive.throttle = g_drive.brake = 0.0f;
            g_drive.fade = 1.0f;
            trace_open();
        }
        else
        {
            trace_close();
        }
        log_message(g_drive.on ? "ets2u: mouse driving on" : "ets2u: mouse driving off");
    }

    CURSORINFO cursor = {sizeof(cursor)};
    const bool cursor_showing = GetCursorInfo(&cursor) && (cursor.flags & CURSOR_SHOWING);
    const bool paused = !g_shared || g_shared->paused;
    const bool steering = g_drive.on && feed && foreground && !paused && !key_held(VK_RBUTTON) && !cursor_showing;
    float target = 0.0f;

    if (!g_drive.on)
    {
        g_drive.steer = g_drive.throttle = g_drive.brake = 0.0f;
    }
    else if (steering)
    {
        RECT r = {0, 0, 1920, 1080};
        if (window) GetClientRect(window, &r);
        const float w = fmaxf(float(r.right - r.left), 1.0f), h = fmaxf(float(r.bottom - r.top), 1.0f);

        // Going backwards counts as standing (OMSI).
        const float raw_kmh = g_shared ? fmaxf(g_shared->speed_ms * 3.6f, 0.0f) : 0.0f;
        g_drive.kmh += (raw_kmh - g_drive.kmh) * (1.0f - std::exp(-dt / 0.4f));
        const float divisor = fmaxf(g_drive.kmh / 10.0f, 1.0f);

        // The point goes on past the window's edges as far as the full lock at this speed.
        g_drive.x += static_cast<float>(dx);
        g_drive.y += static_cast<float>(dy);
        const float reach = fmaxf(divisor / g_config.sensitivity, 1.0f) * w * 0.5f;
        g_drive.x = clampf(g_drive.x, -reach, reach);
        g_drive.y = clampf(g_drive.y, -h * 0.5f, h * 0.5f);

        // Omsi.exe: the window's whole width is full left to full right, divided by the speed in
        // tens of km/h above 10 km/h, so the wheel gets "heavier" the faster the truck goes.
        target = clampf(g_drive.x / (w * 0.5f) / divisor * g_config.sensitivity, -1.0f, 1.0f);
        // Above the middle the throttle, below it the brake.
        const float y = clampf(g_drive.y / (h * 0.5f), -1.0f, 1.0f);
        const float throttle = pedal_target(-y), brake = pedal_target(y);

        const float k = follow(g_drive.fade, dt);
        g_drive.steer = target + (g_drive.steer - target) * k;
        g_drive.throttle = throttle + (g_drive.throttle - throttle) * k;
        g_drive.brake = brake + (g_drive.brake - brake) * k;
        if (fabsf(g_drive.throttle - throttle) < 1e-4f) g_drive.throttle = throttle;
        if (fabsf(g_drive.brake - brake) < 1e-4f) g_drive.brake = brake;
        g_drive.fade = fmaxf(g_drive.fade - dt, 0.0f);
    }
    else if (!foreground || !feed)
    {
        // Another window in front, or no mouse feed: wheel stays, but never keep driving.
        g_drive.throttle = 0.0f;
    }
    // (otherwise looking round with the right button, or a menu: wheel and pedals stay where the
    // mouse left them, as in OMSI, and the movement meanwhile doesn't count)

    const uint32_t state = !g_drive.on ? 0 : !feed ? 3 : steering ? 1 : 2;
    if (g_shared)
    {
        g_shared->mouse_state = state;
        g_shared->mouse_steer = g_drive.steer;
        g_shared->mouse_throttle = g_drive.throttle;
        g_shared->mouse_brake = g_drive.brake;
    }

    // 50 lines a second, for up to an hour.
    g_drive.trace_due -= dt;
    if (g_drive.trace && g_drive.trace_due <= 0.0f && g_drive.trace_lines < 180000)
    {
        g_drive.trace_due = 0.02f;
        fprintf(g_drive.trace, "%.3f,%.4f,%u,%d,%.1f,%.1f,%.1f,%.4f,%.4f,%.4f,%.4f,%.3f,%.3f\n", g_drive.time, dt, state, g_drive.moved,
                g_drive.x, g_drive.y, g_drive.kmh, target, g_drive.steer, -g_input_steering, -g_effective_steering,
                g_drive.throttle, g_drive.brake);
        g_drive.moved = 0;
        if (++g_drive.trace_lines % 50 == 0) fflush(g_drive.trace);
    }
}

// camlr / camud aren't camera movement here: the app changes those two mixes in controls.sii to
// "mouse movement * (1 - semantical.camlr?0)", so 1 holds the camera still while the mouse steers
// and 0 lets the mouse look round as usual.
static const int kInputCount = 5;
static const scs_input_device_input_t kInputs[kInputCount] = {
    {"steering", "Steering", SCS_VALUE_TYPE_float},
    {"aforward", "Throttle", SCS_VALUE_TYPE_float},
    {"abackward", "Brake", SCS_VALUE_TYPE_float},
    {"camlr", "Camera hold LR", SCS_VALUE_TYPE_float},
    {"camud", "Camera hold UD", SCS_VALUE_TYPE_float},
};

// The game asks for input tens of thousands of times a second. The mouse is read and the wheel
// worked out at most 250 times a second, and only values that changed are reported.
static SCSAPI_RESULT input_event(scs_input_event_t *const event, const scs_u32_t flags, const scs_context_t)
{
    if (flags & SCS_INPUT_EVENT_CALLBACK_FLAG_first_in_frame)
    {
        g_drive.pending += seconds_since(g_drive.last);
        if (flags & SCS_INPUT_EVENT_CALLBACK_FLAG_first_after_activation)
            for (float &v : g_drive.sent) v = -2.0f; // tell the game everything again
        if (g_drive.pending >= 0.004f)
        {
            mouse_update(fminf(g_drive.pending, 0.2f));
            g_drive.pending = 0.0f;
        }
        g_drive.emit = 0;
    }
    const float hold = g_shared && g_shared->mouse_state == 1 ? 1.0f : 0.0f;
    const float values[kInputCount] = {g_drive.steer, g_drive.throttle, g_drive.brake, hold, hold};
    while (g_drive.emit < kInputCount)
    {
        const int i = g_drive.emit++;
        if (values[i] == g_drive.sent[i]) continue;
        g_drive.sent[i] = values[i];
        event->input_index = static_cast<scs_u32_t>(i);
        event->value_float.value = values[i];
        return SCS_RESULT_ok;
    }
    return SCS_RESULT_not_found;
}

SCSAPI_RESULT scs_input_init(const scs_u32_t version, const scs_input_init_params_t *const params)
{
    if (version != SCS_INPUT_VERSION_1_00) return SCS_RESULT_unsupported;
    const auto *const p = static_cast<const scs_input_init_params_v100_t *>(params);
    if (strcmp(p->common.game_id, SCS_GAME_ID_EUT2) != 0 && strcmp(p->common.game_id, SCS_GAME_ID_ATS) != 0)
        return SCS_RESULT_unsupported;
    if (!g_log) g_log = p->common.log;
    if (!open_memory()) return SCS_RESULT_generic_error;

    wchar_t dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"APPDATA", dir, MAX_PATH);
    if (n > 0 && n < MAX_PATH - 40) swprintf_s(g_config_path, L"%s\\ets2u\\mouse_drive.ini", dir);
    n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
    if (n > 0 && n < MAX_PATH - 40)
    {
        swprintf_s(g_trace_path, L"%s\\ets2u", dir);
        CreateDirectoryW(g_trace_path, nullptr);
        swprintf_s(g_trace_path, L"%s\\ets2u\\mouse_trace.csv", dir);
    }
    read_config();

    scs_input_device_t device;
    memset(&device, 0, sizeof(device));
    device.name = "ets2u_mouse";
    device.display_name = "ETS2 Utilities mouse driving";
    device.type = SCS_INPUT_DEVICE_TYPE_semantical;
    device.input_count = kInputCount;
    device.inputs = kInputs;
    device.input_event_callback = input_event;
    if (p->register_device(&device) != SCS_RESULT_ok)
    {
        p->common.log(SCS_LOG_TYPE_error, "ets2u: couldn't register the mouse driving device");
        return SCS_RESULT_generic_error;
    }
    p->common.log(SCS_LOG_TYPE_message, "ets2u: mouse driving ready");
    return SCS_RESULT_ok;
}

SCSAPI_VOID scs_input_shutdown(void)
{
    trace_close();
    mouse_release();
}

#endif // ETS2U_MOUSE_DRIVING

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH) release_memory();
    return TRUE;
}
