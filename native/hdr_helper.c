// Wayland helper — exposes three capabilities:
//
// 1. hdr_helper_apply_pq(display, surface)
//    One-shot attach of a PQ/BT.2020 image description to an arbitrary wl_surface. Used by the GLArea fallback path (Linux X11/Xwayland) where we have no subsurface. Known issue: makes GTK's UI look blown out because the compositor reinterprets sRGB widget output as PQ. Kept for compat.
//
// 2. vom_video_surface_* API
//    Creates a wl_subsurface child of a given parent wl_surface, places it ABOVE the parent, builds a dedicated EGL context + EGL surface on top via wl_egl_window, and (optionally) tags that child surface PQ/BT.2020. Used by the Wayland path: main surface stays sRGB (UI looks normal), subsurface is HDR-only. Caller drives rendering: make_current → (caller's render) → swap.
//
// 3. VRR classifier
//    Per-surface behavioral classifier for whether the compositor is scanning frames out on a fixed vsync grid or at variable intervals. Inputs: per-frame presentation timestamps (wp_presentation_feedback) + the panel's nominal mode rate (wl_output.mode). Output: one of UNKNOWN/VRR/FIXED/CAN'T-TELL. Called from inside the presentation-feedback listener (for the per-N-frames stats log) and exposed to the client via vom_video_surface_get_vrr_classification.
//
// The wp_color_manager_v1 + wp_presentation + wl_output globals are bound once per process (via a retained wl_registry on first use) and cached across all entry points.
//
// Build: gcc -shared -fPIC -o libhdr_helper.so hdr_helper.c color-management-v1-protocol.c presentation-time-protocol.c -lwayland-client -lwayland-egl -lEGL -lm

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include "color-management-v1-client-protocol.h"
#include "presentation-time-client-protocol.h"

//---------------------------------------------------------------
// Process-global Wayland globals, resolved lazily on first use.
//---------------------------------------------------------------

// Per-output record. Populated when the compositor advertises a wl_output in the registry; mode updates via wl_output.mode (MODE_CURRENT flag). Tracked process-wide so the VRR classifier can look up the panel's nominal refresh for whichever output the video subsurface currently lives on.
struct output_info
{
    struct wl_output *output;
    uint32_t registry_name;
    int32_t current_mode_mhz;
    struct output_info *next;
};

static struct wl_display *g_cached_display;
static struct wl_registry *g_registry;
static struct wl_compositor *g_compositor;
static struct wl_subcompositor *g_subcompositor;
static struct wp_color_manager_v1 *g_color_manager;
static struct wp_presentation *g_presentation;
static struct output_info *g_outputs;
// Toggles per-frame wp_presentation_feedback logging. Set VOM_WAYLAND_LOG_PRESENTATION=1 in the environment to enable; off by default so normal runs don't spam stderr. Evaluated once per process on first ensure_globals call.
static int g_presentation_log_enabled = -1;

static void output_handle_geometry(void *data, struct wl_output *o,
    int32_t x, int32_t y, int32_t phys_w, int32_t phys_h,
    int32_t subpixel, const char *make, const char *model, int32_t transform)
{
    (void)data; (void)o; (void)x; (void)y; (void)phys_w; (void)phys_h;
    (void)subpixel; (void)make; (void)model; (void)transform;
}

// Panels advertise their full mode list; only the entry flagged MODE_CURRENT reflects the currently-selected timing (and therefore the panel's actual vsync period). refresh is in millihertz — 60 Hz = 60000 mHz = 16.666 ms period.
static void output_handle_mode(void *data, struct wl_output *o,
    uint32_t flags, int32_t width, int32_t height, int32_t refresh)
{
    (void)o; (void)width; (void)height;
    struct output_info *info = data;
    if (flags & WL_OUTPUT_MODE_CURRENT)
    {
        info->current_mode_mhz = refresh;
    }
}

static void output_handle_done(void *data, struct wl_output *o) { (void)data; (void)o; }
static void output_handle_scale(void *data, struct wl_output *o, int32_t scale) { (void)data; (void)o; (void)scale; }
static void output_handle_name(void *data, struct wl_output *o, const char *name) { (void)data; (void)o; (void)name; }
static void output_handle_description(void *data, struct wl_output *o, const char *d) { (void)data; (void)o; (void)d; }

static const struct wl_output_listener output_listener_impl = {
    .geometry = output_handle_geometry,
    .mode = output_handle_mode,
    .done = output_handle_done,
    .scale = output_handle_scale,
    .name = output_handle_name,
    .description = output_handle_description,
};

// Writes directly to file-scope globals rather than collecting into a local struct. The registry is kept alive for the process lifetime so we also receive global_remove events on hot-plug, which need the list to be accessible from here.
static void globals_reg_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t version)
{
    (void)data;
    if (strcmp(iface, "wl_compositor") == 0 && !g_compositor)
    {
        uint32_t bind_v = version < 4 ? version : 4;
        g_compositor = wl_registry_bind(reg, name, &wl_compositor_interface, bind_v);
    }
    else if (strcmp(iface, "wl_subcompositor") == 0 && !g_subcompositor)
    {
        g_subcompositor = wl_registry_bind(reg, name, &wl_subcompositor_interface, 1);
    }
    else if (strcmp(iface, "wp_color_manager_v1") == 0 && !g_color_manager)
    {
        g_color_manager = wl_registry_bind(reg, name, &wp_color_manager_v1_interface, version < 1 ? version : 1);
    }
    else if (strcmp(iface, "wp_presentation") == 0 && !g_presentation)
    {
        g_presentation = wl_registry_bind(reg, name, &wp_presentation_interface, version < 1 ? version : 1);
    }
    else if (strcmp(iface, "wl_output") == 0)
    {
        // v2 suffices for geometry/mode/done/scale; bump to 4 where available to also receive name/description (diagnostic only — classifier only needs mode).
        uint32_t v = version < 4 ? version : 4;
        struct output_info *info = calloc(1, sizeof(*info));
        if (!info) { return; }
        info->output = wl_registry_bind(reg, name, &wl_output_interface, v);
        info->registry_name = name;
        info->current_mode_mhz = 0;
        info->next = g_outputs;
        g_outputs = info;
        wl_output_add_listener(info->output, &output_listener_impl, info);
    }
}

// Monitor hot-plug removal: the compositor tells us a global has vanished by its registry name. Unlink and free the matching output_info so the classifier's active_output-membership check later surfaces the removal. Note that any vom_video_surface currently holding a pointer to the freed output will have a dangling active_output until the classifier's next pass — the classifier validates membership and nulls it out before dereferencing.
static void globals_reg_global_remove(void *data, struct wl_registry *reg, uint32_t name)
{
    (void)data; (void)reg;
    struct output_info **pp = &g_outputs;
    while (*pp)
    {
        if ((*pp)->registry_name == name)
        {
            struct output_info *dead = *pp;
            *pp = dead->next;
            wl_output_destroy(dead->output);
            free(dead);
            return;
        }
        pp = &(*pp)->next;
    }
}

static const struct wl_registry_listener globals_registry_listener = {
    .global = globals_reg_global,
    .global_remove = globals_reg_global_remove,
};

// Binds wl_compositor, wl_subcompositor, and the optional globals (color-management, presentation, wl_outputs) from the display's registry. The registry is kept alive for the process lifetime so the global_remove handler fires on runtime output hot-plug. Safe to call multiple times; no-op after first success. Returns 0 on success, negative on failure.
static int ensure_globals(struct wl_display *display)
{
    if (g_cached_display == display && g_compositor && g_subcompositor)
    {
        return 0;
    }
    if (g_cached_display && g_cached_display != display)
    {
        fprintf(stderr, "[hdr_helper] second wl_display encountered; not supported\n");
        return -1;
    }

    struct wl_registry *reg = wl_display_get_registry(display);
    if (!reg)
    {
        return -2;
    }
    wl_registry_add_listener(reg, &globals_registry_listener, NULL);
    if (wl_display_roundtrip(display) < 0)
    {
        wl_registry_destroy(reg);
        return -3;
    }

    // Second roundtrip: globals bound above (wl_output in particular) send their initial events in response to binding, which only arrive AFTER the first sync_done. Without this second round we'd see the outputs themselves but no mode events, leaving current_mode_mhz=0.
    if (wl_display_roundtrip(display) < 0)
    {
        wl_registry_destroy(reg);
        return -3;
    }

    if (!g_compositor || !g_subcompositor)
    {
        fprintf(stderr, "[hdr_helper] compositor/subcompositor globals missing\n");
        wl_registry_destroy(reg);
        return -4;
    }

    g_cached_display = display;
    g_registry = reg;
    if (g_presentation_log_enabled < 0)
    {
        const char *e = getenv("VOM_WAYLAND_LOG_PRESENTATION");
        g_presentation_log_enabled = (e && e[0] == '1') ? 1 : 0;
    }
    return 0;
}

//---------------------------------------------------------------
// Parametric PQ/BT.2020 image description builder (shared).
//---------------------------------------------------------------

struct desc_state
{
    int ready_status; // 0 pending, 1 ready, -1 failed
    uint32_t failure_cause;
};

static void desc_ready(void *data, struct wp_image_description_v1 *desc, uint32_t identity)
{
    (void)desc; (void)identity;
    ((struct desc_state *)data)->ready_status = 1;
}

static void desc_failed(void *data, struct wp_image_description_v1 *desc, uint32_t cause, const char *msg)
{
    (void)desc;
    struct desc_state *s = data;
    s->ready_status = -1;
    s->failure_cause = cause;
    fprintf(stderr, "[hdr_helper] image description failed: cause=%u msg=%s\n", cause, msg ? msg : "");
}

static const struct wp_image_description_v1_listener desc_listener = {
    .failed = desc_failed,
    .ready = desc_ready,
};

// Builds a PQ/BT.2020 parametric image description and waits for ready. Returns NULL on failure. Caller owns the returned proxy (but in our current callers we leak it for process lifetime).
static struct wp_image_description_v1 *build_pq_description(struct wl_display *display)
{
    if (!g_color_manager)
    {
        fprintf(stderr, "[hdr_helper] no wp_color_manager_v1 bound\n");
        return NULL;
    }

    struct wp_image_description_creator_params_v1 *creator = wp_color_manager_v1_create_parametric_creator(g_color_manager);
    if (!creator)
    {
        return NULL;
    }
    wp_image_description_creator_params_v1_set_primaries_named(creator, WP_COLOR_MANAGER_V1_PRIMARIES_BT2020);
    wp_image_description_creator_params_v1_set_tf_named(creator, WP_COLOR_MANAGER_V1_TRANSFER_FUNCTION_ST2084_PQ);
    wp_image_description_creator_params_v1_set_mastering_display_primaries(
        creator,
        34000, 16000,
        13250, 34500,
         7500,  3000,
        15635, 16450);

    struct wp_image_description_v1 *desc = wp_image_description_creator_params_v1_create(creator);
    if (!desc)
    {
        return NULL;
    }

    struct desc_state st = {0};
    wp_image_description_v1_add_listener(desc, &desc_listener, &st);
    while (st.ready_status == 0)
    {
        if (wl_display_roundtrip(display) < 0)
        {
            wp_image_description_v1_destroy(desc);
            return NULL;
        }
    }
    if (st.ready_status < 0)
    {
        wp_image_description_v1_destroy(desc);
        return NULL;
    }
    return desc;
}

//---------------------------------------------------------------
// Entry point 1: one-shot PQ attach to an existing wl_surface.
//---------------------------------------------------------------

int hdr_helper_apply_pq(struct wl_display *display, struct wl_surface *surface)
{
    if (!display || !surface)
    {
        return -1;
    }
    if (ensure_globals(display) < 0)
    {
        return -2;
    }
    if (!g_color_manager)
    {
        fprintf(stderr, "[hdr_helper] compositor does not advertise wp_color_manager_v1\n");
        return -4;
    }
    fprintf(stderr, "[hdr_helper] bound wp_color_manager_v1\n");

    struct wp_image_description_v1 *desc = build_pq_description(display);
    if (!desc)
    {
        return -5;
    }

    struct wp_color_management_surface_v1 *cm_surf = wp_color_manager_v1_get_surface(g_color_manager, surface);
    if (!cm_surf)
    {
        wp_image_description_v1_destroy(desc);
        return -6;
    }
    wp_color_management_surface_v1_set_image_description(cm_surf, desc, WP_COLOR_MANAGER_V1_RENDER_INTENT_PERCEPTUAL);
    wl_surface_commit(surface);
    wl_display_flush(display);

    fprintf(stderr, "[hdr_helper] PQ/BT.2020 description attached to surface\n");
    // Intentionally leak cm_surf + desc (process-lifetime attachment).
    return 0;
}

//---------------------------------------------------------------
// Entry point 2: subsurface + EGL surface + HDR (used by the Wayland path).
//---------------------------------------------------------------

// Rolling presentation-timing stats, populated from wp_presentation_feedback.presented events. Logged every STATS_WINDOW frames when VOM_WAYLAND_LOG_PRESENTATION=1. `refresh_ns` is the compositor's expected next-frame interval at time of presentation; on VRR-active outputs it varies frame-to-frame or is reported as 0 (aperiodic).
struct presentation_stats
{
    int frames;
    uint64_t last_ns;
    double delta_sum_ms;
    double delta_min_ms;
    double delta_max_ms;
    uint32_t refresh_min_ns;
    uint32_t refresh_max_ns;
    int discarded;
};

// Rolling window of inter-frame delta samples in microseconds (uint32 fits ~4295 s). Populated in refresh_ring_push on every wp_presentation_feedback.presented event. Consumed by vom_video_surface_get_vrr_classification to decide VRR vs FIXED.
#define VOM_REFRESH_RING_SIZE 60
struct refresh_ring
{
    uint32_t delta_us[VOM_REFRESH_RING_SIZE];
    int count;
    int head;
    int has_prev;
    uint64_t prev_ns;
};

#define VOM_PRESENTATION_STATS_WINDOW 120
// The stats log calls the classifier (and the classifier expects a full ring); require the ring to fill at least once before we emit a log line. Tripping this means someone lowered the log window below the ring size, which would make every log entry say UNKNOWN.
_Static_assert(VOM_PRESENTATION_STATS_WINDOW >= VOM_REFRESH_RING_SIZE, "log window must be large enough for the classifier's ring to fill at least once");

struct vom_video_surface
{
    struct wl_display *display;
    struct wl_surface *parent;
    struct wl_surface *wl_surface;
    struct wl_subsurface *wl_subsurface;
    struct wl_egl_window *egl_window;
    EGLDisplay egl_display;
    EGLContext egl_context;
    EGLSurface egl_surface;
    struct wp_color_management_surface_v1 *cm_surface;
    struct wp_image_description_v1 *image_desc;
    int buffer_scale;
    int buffer_w;
    int buffer_h;
    struct presentation_stats pstats;
    struct refresh_ring refresh;
    // Set by the wl_surface.enter handler the first time the compositor tells us our subsurface has landed on a specific output; cleared in wl_surface.leave when that output is the one we leave. Classifier uses this output's current_mode_mhz as T_nominal; when NULL, classifier returns UNKNOWN rather than guessing from g_outputs — on a multi-monitor host, picking the wrong panel rate would silently mis-classify.
    struct output_info *active_output;
};

static void vs_surface_handle_enter(void *data, struct wl_surface *s, struct wl_output *o)
{
    (void)s;
    struct vom_video_surface *vs = data;
    for (struct output_info *it = g_outputs; it; it = it->next)
    {
        if (it->output == o)
        {
            vs->active_output = it;
            if (g_presentation_log_enabled)
            {
                fprintf(stderr, "[vom_wayland] subsurface entered output (nominal=%d mHz)\n", it->current_mode_mhz);
            }
            return;
        }
    }
}

static void vs_surface_handle_leave(void *data, struct wl_surface *s, struct wl_output *o)
{
    (void)s;
    struct vom_video_surface *vs = data;
    // Walk g_outputs to find the matching output and (if it matches active) clear it. Cannot compare vs->active_output->output directly — if global_remove freed active_output between the enter and this leave, the deref would UAF.
    if (!vs->active_output) { return; }
    for (struct output_info *it = g_outputs; it; it = it->next)
    {
        if (it == vs->active_output && it->output == o)
        {
            vs->active_output = NULL;
            return;
        }
    }
}

static const struct wl_surface_listener vs_surface_listener = {
    .enter = vs_surface_handle_enter,
    .leave = vs_surface_handle_leave,
};


static void pstats_reset(struct presentation_stats *ps)
{
    ps->frames = 0;
    ps->delta_sum_ms = 0;
    ps->delta_min_ms = 0;
    ps->delta_max_ms = 0;
    ps->refresh_min_ns = 0;
    ps->refresh_max_ns = 0;
    ps->discarded = 0;
}

// Behavioral classifier: is the compositor scanning our frames out on a fixed vsync grid, or at variable intervals?
//
// Reference period T_nominal comes from wl_output.mode (current mode's refresh in mHz). This is "system status" — the panel's configured mode, which KWin/wlroots/Mutter all report even while they VRR-engage within the mode. NOT the wp_presentation refresh field: KWin sets refresh=0 on VRR-capable outputs in many cases where VRR isn't actually engaged for this surface (windowed subsurfaces on a VRR-capable output), so refresh=0 is an unreliable engagement signal and the stats log keeps it only as a diagnostic field.
//
// For each observed delta D_i, residual R_i = D_i - round(D_i/T) * T measures how far off the nearest integer-multiple of T the delta sits. On a fixed-refresh panel, scanout can only happen on T-boundaries — every delta MUST be K*T + small jitter. On VRR, deltas take arbitrary values in the panel's VRR range and typically miss the K*T grid.
//
// Thresholds (named below): RMS_OFF_GRID and RMS_ON_GRID are expressed as fractions of T. A warm-system compositor on idle produces per-frame jitter on the order of 100-300 µs — ~1-2% of a 60 Hz period — so ON_GRID at 2% sits right at the jitter ceiling, and OFF_GRID at 5% (~830 µs on 60 Hz) is comfortably past it. MIN_VARIATION is the σ/μ floor: below it, the stream is so steady we can't tell fixed-rate-at-panel-Hz from VRR-locked-to-content-rate apart — return CAN'T-TELL rather than guess. These numbers were chosen by observing traces on KWin/AMDGPU; retune if future traces misclassify.
//
// Order of checks matters: a constant-rate stream at a non-grid rate (e.g. 50 fps on a VRR 60 Hz panel) has σ/μ tiny but RMS/T large — VRR check first catches it correctly.
//
// Thread safety: called from (a) the Wayland main-thread dispatcher (via the presentation-feedback listener) and (b) the C# caller on the GTK main thread. Both are the same thread in production — no cross-thread contention on vs->refresh or vs->active_output.
//
// Returns:
//   0 = UNKNOWN   (ring not full, or no wl_output mode available yet)
//   1 = VRR       (deltas don't fit the nominal T grid)
//   2 = FIXED     (deltas fit the grid with sufficient variance)
//   3 = CAN'T-TELL (ambiguous: either stream too stable, or metrics in middle band)
// out_hz_centi carries the panel's nominal mode rate × 100 whenever it's known (any result except UNKNOWN).
#define VOM_VRR_RMS_OFF_GRID 0.05
#define VOM_VRR_RMS_ON_GRID 0.02
#define VOM_VRR_MIN_VARIATION 0.05
int vom_video_surface_get_vrr_classification(struct vom_video_surface *vs, int *out_hz_centi)
{
    if (out_hz_centi) { *out_hz_centi = 0; }
    if (!vs) { return 0; }
    struct refresh_ring *r = &vs->refresh;
    if (r->count < VOM_REFRESH_RING_SIZE) { return 0; }

    // Refuse to guess when we haven't been told which output we're on — on multi-monitor, silently picking the wrong panel rate would silently mis-classify. The caller's UX will show UNKNOWN briefly at startup until wl_surface.enter fires. Also validate that the output hasn't been hot-unplugged since we were told about it — dereferencing a freed output_info would be UB.
    struct output_info *info = vs->active_output;
    if (info)
    {
        int found = 0;
        for (struct output_info *it = g_outputs; it; it = it->next)
        {
            if (it == info) { found = 1; break; }
        }
        if (!found)
        {
            vs->active_output = NULL;
            info = NULL;
        }
    }
    if (!info || info->current_mode_mhz <= 0) { return 0; }

    // wl_output.mode.refresh is in millihertz. T_us = 1e6 µs/s / (mHz/1000) Hz = 1e9 / mHz. delta_us is uint32 µs, so all math is in µs.
    double T_us = 1e9 / (double)info->current_mode_mhz;

    if (out_hz_centi)
    {
        *out_hz_centi = (int)((info->current_mode_mhz + 5) / 10);
    }

    double sum = 0;
    for (int i = 0; i < r->count; i++) { sum += (double)r->delta_us[i]; }
    double mean = sum / r->count;
    if (mean <= 0) { return 0; }

    double sumsq = 0;
    for (int i = 0; i < r->count; i++)
    {
        double d = (double)r->delta_us[i] - mean;
        sumsq += d * d;
    }
    double stddev = sqrt(sumsq / r->count);
    double sigma_over_mu = stddev / mean;

    double rms_sq = 0;
    for (int i = 0; i < r->count; i++)
    {
        double d = (double)r->delta_us[i];
        double ratio = d / T_us;
        long K = (long)(ratio + 0.5);
        if (K < 1) { K = 1; }
        double residual = d - (double)K * T_us;
        rms_sq += residual * residual;
    }
    double rms_over_t = sqrt(rms_sq / r->count) / T_us;

    if (rms_over_t > VOM_VRR_RMS_OFF_GRID) { return 1; }
    if (sigma_over_mu < VOM_VRR_MIN_VARIATION) { return 3; }
    if (rms_over_t < VOM_VRR_RMS_ON_GRID) { return 2; }
    return 3;
}

static void refresh_ring_push(struct refresh_ring *r, uint64_t now_ns)
{
    uint32_t delta_us = 0;
    if (r->has_prev && now_ns > r->prev_ns)
    {
        uint64_t d = (now_ns - r->prev_ns) / 1000;
        delta_us = d > UINT32_MAX ? UINT32_MAX : (uint32_t)d;
    }
    r->prev_ns = now_ns;
    r->has_prev = 1;
    // Skip the push when no valid delta is available — the first presented event has no predecessor, and (rarely) an out-of-order presented timestamp against prev_ns produces delta=0. prev_ns is still updated so subsequent deltas compute against the newest timestamp rather than staying anchored to a stale one.
    if (delta_us == 0) { return; }
    r->delta_us[r->head] = delta_us;
    r->head = (r->head + 1) % VOM_REFRESH_RING_SIZE;
    if (r->count < VOM_REFRESH_RING_SIZE) { r->count++; }
}

static void feedback_sync_output(void *data, struct wp_presentation_feedback *fb, struct wl_output *out)
{
    (void)data; (void)fb; (void)out;
}

static void feedback_presented(void *data, struct wp_presentation_feedback *fb,
    uint32_t tv_sec_hi, uint32_t tv_sec_lo, uint32_t tv_nsec,
    uint32_t refresh, uint32_t seq_hi, uint32_t seq_lo, uint32_t flags)
{
    (void)seq_hi; (void)seq_lo; (void)flags;
    struct vom_video_surface *vs = data;
    struct presentation_stats *ps = &vs->pstats;
    struct refresh_ring *ring = &vs->refresh;

    uint64_t now_ns = (((uint64_t)tv_sec_hi << 32) | tv_sec_lo) * 1000000000ULL + tv_nsec;
    refresh_ring_push(ring, now_ns);
    if (ps->last_ns != 0)
    {
        double delta_ms = (double)(now_ns - ps->last_ns) / 1e6;
        ps->delta_sum_ms += delta_ms;
        if (ps->frames == 0 || delta_ms < ps->delta_min_ms) { ps->delta_min_ms = delta_ms; }
        if (ps->frames == 0 || delta_ms > ps->delta_max_ms) { ps->delta_max_ms = delta_ms; }
        ps->frames++;
    }
    ps->last_ns = now_ns;
    if (refresh != 0)
    {
        if (ps->refresh_min_ns == 0 || refresh < ps->refresh_min_ns) { ps->refresh_min_ns = refresh; }
        if (refresh > ps->refresh_max_ns) { ps->refresh_max_ns = refresh; }
    }
    if (g_presentation_log_enabled && ps->frames >= VOM_PRESENTATION_STATS_WINDOW)
    {
        double avg_ms = ps->delta_sum_ms / ps->frames;
        double avg_hz = avg_ms > 0 ? 1000.0 / avg_ms : 0;
        double min_hz = ps->delta_max_ms > 0 ? 1000.0 / ps->delta_max_ms : 0;
        double max_hz = ps->delta_min_ms > 0 ? 1000.0 / ps->delta_min_ms : 0;
        double refresh_min_hz = ps->refresh_max_ns > 0 ? 1e9 / ps->refresh_max_ns : 0;
        double refresh_max_hz = ps->refresh_min_ns > 0 ? 1e9 / ps->refresh_min_ns : 0;
        // Get T_nominal via the classifier so we go through its output-membership validation — reading vs->active_output->current_mode_mhz directly would UAF after a global_remove freed the struct.
        int cls_hz_centi = 0;
        int cls = vom_video_surface_get_vrr_classification(vs, &cls_hz_centi);
        const char *cls_name = cls == 1 ? "VRR" : cls == 2 ? "FIXED" : cls == 3 ? "CANT-TELL" : "UNKNOWN";
        double nominal_hz = cls_hz_centi > 0 ? (double)cls_hz_centi / 100.0 : 0;
        fprintf(stderr,
            "[vom_wayland] presentation %d frames: delta avg=%.2fms (%.1fHz) range=[%.2f..%.2f]ms ([%.1f..%.1f]Hz) refresh=[%.1f..%.1f]Hz nominal=%.2fHz classification=%s discarded=%d\n",
            ps->frames, avg_ms, avg_hz,
            ps->delta_min_ms, ps->delta_max_ms, min_hz, max_hz,
            refresh_min_hz, refresh_max_hz, nominal_hz, cls_name, ps->discarded);
        pstats_reset(ps);
        ps->last_ns = now_ns;
    }
    else if (!g_presentation_log_enabled && ps->frames >= VOM_PRESENTATION_STATS_WINDOW)
    {
        pstats_reset(ps);
        ps->last_ns = now_ns;
    }
    wp_presentation_feedback_destroy(fb);
}

static void feedback_discarded(void *data, struct wp_presentation_feedback *fb)
{
    struct vom_video_surface *vs = data;
    vs->pstats.discarded++;
    // Don't let the next presented frame compute its delta against the prev_ns from before the discard — that would produce a spuriously-large delta reflecting the gap the discarded frame(s) would have filled. Clearing has_prev causes refresh_ring_push to skip the first post-discard sample; we pick up clean deltas from the one after.
    vs->refresh.has_prev = 0;
    wp_presentation_feedback_destroy(fb);
}

static const struct wp_presentation_feedback_listener feedback_listener = {
    .sync_output = feedback_sync_output,
    .presented = feedback_presented,
    .discarded = feedback_discarded,
};

// Pick an 8-bit-per-channel RGBA EGL config with an alpha channel. We request alpha because some drivers don't advertise configs without it; the actual framebuffer can be RGB and we ignore alpha downstream.
static int choose_egl_config(EGLDisplay egl_display, EGLConfig *out)
{
    EGLint attribs[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_NONE,
    };
    EGLint count = 0;
    if (!eglChooseConfig(egl_display, attribs, out, 1, &count) || count < 1)
    {
        return -1;
    }
    return 0;
}

struct vom_video_surface *vom_video_surface_create(
    struct wl_display *display, struct wl_surface *parent,
    int initial_w, int initial_h, int initial_buffer_scale, int hdr)
{
    if (!display || !parent)
    {
        fprintf(stderr, "[vom_wayland] create: null display or parent\n");
        return NULL;
    }
    if (initial_w < 1) { initial_w = 1; }
    if (initial_h < 1) { initial_h = 1; }
    if (initial_buffer_scale < 1) { initial_buffer_scale = 1; }

    if (ensure_globals(display) < 0)
    {
        return NULL;
    }

    struct vom_video_surface *vs = calloc(1, sizeof(*vs));
    if (!vs)
    {
        return NULL;
    }
    vs->display = display;
    vs->parent = parent;
    vs->buffer_scale = initial_buffer_scale;
    vs->buffer_w = initial_w * initial_buffer_scale;
    vs->buffer_h = initial_h * initial_buffer_scale;

    vs->wl_surface = wl_compositor_create_surface(g_compositor);
    if (!vs->wl_surface)
    {
        fprintf(stderr, "[vom_wayland] create_surface failed\n");
        free(vs);
        return NULL;
    }
    wl_surface_set_buffer_scale(vs->wl_surface, initial_buffer_scale);
    // Attach before first commit so the compositor's initial enter event is not lost.
    wl_surface_add_listener(vs->wl_surface, &vs_surface_listener, vs);

    // Empty input region: pointer/touch events over the subsurface fall through to the parent wl_surface. Without this, the compositor routes input to the child (since the default input region is infinite), and the main GTK surface's EventControllerMotion never sees motion over the video — which breaks mouse-idle auto-hide and any click/motion UI that lives on the parent.
    struct wl_region *empty_input = wl_compositor_create_region(g_compositor);
    if (empty_input)
    {
        wl_surface_set_input_region(vs->wl_surface, empty_input);
        wl_region_destroy(empty_input);
    }

    vs->wl_subsurface = wl_subcompositor_get_subsurface(g_subcompositor, vs->wl_surface, parent);
    if (!vs->wl_subsurface)
    {
        fprintf(stderr, "[vom_wayland] get_subsurface failed\n");
        wl_surface_destroy(vs->wl_surface);
        free(vs);
        return NULL;
    }
    // Place the video subsurface BELOW the parent wl_surface (not above). The GTK main surface is made transparent in the video region (VideoArea paints nothing; window bg is overridden to transparent), so the subsurface shows through from underneath. Anything GTK renders into the main surface — including control-bar widgets overlaid at the bottom in fullscreen — then composites on top of the video. Place-above would hide GTK-drawn pixels behind the subsurface's opaque video buffer, which makes overlaid controls invisible.
    wl_subsurface_place_below(vs->wl_subsurface, parent);
    wl_subsurface_set_position(vs->wl_subsurface, 0, 0);
    wl_subsurface_set_desync(vs->wl_subsurface);

    // HDR attach happens before first commit so the very first frame the compositor sees is already tagged PQ.
    if (hdr)
    {
        if (!g_color_manager)
        {
            fprintf(stderr, "[vom_wayland] HDR requested but compositor does not advertise wp_color_manager_v1; proceeding SDR\n");
        }
        else
        {
            vs->image_desc = build_pq_description(display);
            if (vs->image_desc)
            {
                vs->cm_surface = wp_color_manager_v1_get_surface(g_color_manager, vs->wl_surface);
                if (vs->cm_surface)
                {
                    wp_color_management_surface_v1_set_image_description(
                        vs->cm_surface, vs->image_desc, WP_COLOR_MANAGER_V1_RENDER_INTENT_PERCEPTUAL);
                    fprintf(stderr, "[vom_wayland] PQ/BT.2020 description attached to subsurface\n");
                }
                else
                {
                    fprintf(stderr, "[vom_wayland] get_surface for cm failed; proceeding SDR\n");
                    wp_image_description_v1_destroy(vs->image_desc);
                    vs->image_desc = NULL;
                }
            }
        }
    }

    // EGL setup.
    vs->egl_display = eglGetDisplay((EGLNativeDisplayType)display);
    if (vs->egl_display == EGL_NO_DISPLAY || !eglInitialize(vs->egl_display, NULL, NULL))
    {
        fprintf(stderr, "[vom_wayland] eglInitialize failed (err=0x%x)\n", eglGetError());
        goto fail;
    }
    if (!eglBindAPI(EGL_OPENGL_ES_API))
    {
        fprintf(stderr, "[vom_wayland] eglBindAPI GLES failed (err=0x%x)\n", eglGetError());
        goto fail;
    }
    EGLConfig config;
    if (choose_egl_config(vs->egl_display, &config) < 0)
    {
        fprintf(stderr, "[vom_wayland] no EGL config (err=0x%x)\n", eglGetError());
        goto fail;
    }

    EGLint ctx_attribs[] = { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
    vs->egl_context = eglCreateContext(vs->egl_display, config, EGL_NO_CONTEXT, ctx_attribs);
    if (vs->egl_context == EGL_NO_CONTEXT)
    {
        // Fall back to GLES 2 if 3 isn't supported.
        EGLint ctx_attribs2[] = { EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE };
        vs->egl_context = eglCreateContext(vs->egl_display, config, EGL_NO_CONTEXT, ctx_attribs2);
        if (vs->egl_context == EGL_NO_CONTEXT)
        {
            fprintf(stderr, "[vom_wayland] eglCreateContext failed (err=0x%x)\n", eglGetError());
            goto fail;
        }
    }

    vs->egl_window = wl_egl_window_create(vs->wl_surface, vs->buffer_w, vs->buffer_h);
    if (!vs->egl_window)
    {
        fprintf(stderr, "[vom_wayland] wl_egl_window_create failed\n");
        goto fail;
    }
    vs->egl_surface = eglCreateWindowSurface(vs->egl_display, config, (EGLNativeWindowType)vs->egl_window, NULL);
    if (vs->egl_surface == EGL_NO_SURFACE)
    {
        fprintf(stderr, "[vom_wayland] eglCreateWindowSurface failed (err=0x%x)\n", eglGetError());
        goto fail;
    }

    // Commit the child with no buffer yet — this flushes the subsurface creation to the compositor. The first render will attach a buffer and swap.
    wl_surface_commit(vs->wl_surface);
    // Parent commit applies the subsurface position.
    wl_surface_commit(parent);
    wl_display_flush(display);

    fprintf(stderr, "[vom_wayland] subsurface created (w=%d h=%d scale=%d hdr=%d)\n",
            vs->buffer_w, vs->buffer_h, vs->buffer_scale, hdr && vs->image_desc != NULL);
    return vs;

fail:
    // Do NOT eglTerminate: eglGetDisplay returns a process-shared handle. Terminating it would invalidate GTK's own EGL users and anything else on the same wl_display. Destroying context + surface is sufficient.
    if (vs->egl_surface != EGL_NO_SURFACE) { eglDestroySurface(vs->egl_display, vs->egl_surface); }
    if (vs->egl_window) { wl_egl_window_destroy(vs->egl_window); }
    if (vs->egl_context != EGL_NO_CONTEXT) { eglDestroyContext(vs->egl_display, vs->egl_context); }
    if (vs->image_desc) { wp_image_description_v1_destroy(vs->image_desc); }
    if (vs->cm_surface) { wp_color_management_surface_v1_destroy(vs->cm_surface); }
    if (vs->wl_subsurface) { wl_subsurface_destroy(vs->wl_subsurface); }
    if (vs->wl_surface) { wl_surface_destroy(vs->wl_surface); }
    free(vs);
    return NULL;
}

void vom_video_surface_set_geometry(struct vom_video_surface *vs, int x, int y, int w, int h, int buffer_scale)
{
    if (!vs) { return; }
    if (w < 1) { w = 1; }
    if (h < 1) { h = 1; }
    if (buffer_scale < 1) { buffer_scale = 1; }

    int new_buffer_w = w * buffer_scale;
    int new_buffer_h = h * buffer_scale;

    wl_subsurface_set_position(vs->wl_subsurface, x, y);

    if (new_buffer_w != vs->buffer_w || new_buffer_h != vs->buffer_h || buffer_scale != vs->buffer_scale)
    {
        wl_egl_window_resize(vs->egl_window, new_buffer_w, new_buffer_h, 0, 0);
        wl_surface_set_buffer_scale(vs->wl_surface, buffer_scale);
        vs->buffer_w = new_buffer_w;
        vs->buffer_h = new_buffer_h;
        vs->buffer_scale = buffer_scale;
    }
    // Parent commit applies the position change. Child commit happens on the next swap.
    wl_surface_commit(vs->parent);
    wl_display_flush(vs->display);
}

int vom_video_surface_make_current(struct vom_video_surface *vs)
{
    if (!vs) { return -1; }
    if (!eglMakeCurrent(vs->egl_display, vs->egl_surface, vs->egl_surface, vs->egl_context))
    {
        fprintf(stderr, "[vom_wayland] eglMakeCurrent failed (err=0x%x)\n", eglGetError());
        return -2;
    }
    return 0;
}

void vom_video_surface_swap(struct vom_video_surface *vs)
{
    if (!vs) { return; }
    // wp_presentation_feedback must be requested BEFORE the commit it pertains to. eglSwapBuffers internally commits, so we request here. The feedback events fire asynchronously once the compositor actually presents the frame — GTK's main-loop dispatch on the shared wl_display delivers them to our listener. Always requested (cheap: one proxy + one event per frame) so vom_video_surface_get_vrr_classification has samples to work with; the per-120-frames stats line is gated by VOM_WAYLAND_LOG_PRESENTATION inside the listener.
    if (g_presentation)
    {
        struct wp_presentation_feedback *fb = wp_presentation_feedback(g_presentation, vs->wl_surface);
        if (fb)
        {
            wp_presentation_feedback_add_listener(fb, &feedback_listener, vs);
        }
    }
    if (!eglSwapBuffers(vs->egl_display, vs->egl_surface))
    {
        fprintf(stderr, "[vom_wayland] eglSwapBuffers failed (err=0x%x)\n", eglGetError());
    }
}

void vom_video_surface_get_buffer_size(struct vom_video_surface *vs, int *out_w, int *out_h)
{
    if (!vs) { return; }
    if (out_w) { *out_w = vs->buffer_w; }
    if (out_h) { *out_h = vs->buffer_h; }
}

// Reflects whether the PQ/BT.2020 image description was actually attached (1) or silently dropped (0). Dropped happens when hdr=1 was requested but the compositor doesn't advertise wp_color_manager_v1 or the parametric creator failed. Callers use this to decide whether to tell mpv to target PQ — if HDR isn't active on the surface, PQ-targeting mpv would overdrive SDR output.
int vom_video_surface_hdr_active(struct vom_video_surface *vs)
{
    if (!vs) { return 0; }
    return vs->image_desc != NULL ? 1 : 0;
}

void vom_video_surface_destroy(struct vom_video_surface *vs)
{
    if (!vs) { return; }
    if (vs->egl_display != EGL_NO_DISPLAY)
    {
        eglMakeCurrent(vs->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (vs->egl_surface != EGL_NO_SURFACE) { eglDestroySurface(vs->egl_display, vs->egl_surface); }
        if (vs->egl_context != EGL_NO_CONTEXT) { eglDestroyContext(vs->egl_display, vs->egl_context); }
        // Do NOT eglTerminate: the EGLDisplay from eglGetDisplay(wl_display) is process-shared with GTK + anyone else on the same wl_display. Terminating would invalidate their resources. Destroying our own context+surface is sufficient; the display reference-counts.
    }
    if (vs->egl_window) { wl_egl_window_destroy(vs->egl_window); }
    if (vs->cm_surface) { wp_color_management_surface_v1_destroy(vs->cm_surface); }
    if (vs->image_desc) { wp_image_description_v1_destroy(vs->image_desc); }
    if (vs->wl_subsurface) { wl_subsurface_destroy(vs->wl_subsurface); }
    if (vs->wl_surface) { wl_surface_destroy(vs->wl_surface); }
    free(vs);
}
