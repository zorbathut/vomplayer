// Wayland ABI-interop shim. All policy, algorithms, and stateful decision-making live in C#; this layer exists only because the protocol's inline stubs and listener function-pointer structs aren't directly callable via P/Invoke. See CLAUDE.md: "Native code is for ABI interop only."
//
// Entry points:
//
// 1. vom_video_surface_* API
//    Creates a wl_subsurface child of a given parent wl_surface, places it BELOW the parent, builds a dedicated EGL context + EGL surface on top via wl_egl_window. Subsurface starts untagged (compositor treats as sRGB); vom_video_surface_set_hdr toggles a PQ/BT.2020 image description at runtime, staged for flush on the next eglSwapBuffers. The caller drives rendering: make_current → (caller's render) → swap.
//
// 2. Output + presentation-feedback trampolines
//    Process-global output events (added / mode / removed) forward to callbacks registered via vom_set_output_callbacks. Per-surface wl_surface.enter/leave and wp_presentation_feedback.presented/discarded events forward to callbacks registered via vom_video_surface_set_callbacks. The consumer (C# FrameTimingBridge + WaylandOutputRegistry) owns all state derived from these events.
//
// The wp_color_manager_v1 + wp_presentation + wl_output globals are bound once per process (via a retained wl_registry on first use).
//
// Build: gcc -shared -fPIC -o libhdr_helper.so hdr_helper.c color-management-v1-protocol.c presentation-time-protocol.c -lwayland-client -lwayland-egl -lEGL

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
// Callback function-pointer types forwarded up to the C# layer.
//---------------------------------------------------------------

typedef void (*vom_output_mode_fn)(uint32_t registry_name, int32_t refresh_mhz);
typedef void (*vom_output_removed_fn)(uint32_t registry_name);

typedef void (*vom_surface_enter_fn)(void *data, uint32_t registry_name);
typedef void (*vom_surface_leave_fn)(void *data, uint32_t registry_name);
typedef void (*vom_feedback_presented_fn)(void *data, uint64_t tv_ns, uint32_t refresh_ns);
typedef void (*vom_feedback_discarded_fn)(void *data);

static vom_output_mode_fn g_output_mode_cb;
static vom_output_removed_fn g_output_removed_cb;

//---------------------------------------------------------------
// Process-global Wayland globals, resolved lazily on first use.
//---------------------------------------------------------------

// Per-output record. The current-mode refresh rate is kept here purely for replay: if vom_set_output_callbacks is called after the initial wl_output enumeration has already fired, we re-deliver the cached mode values so the consumer doesn't miss them. Under steady-state, the consumer (C#) is the authoritative store.
struct output_info
{
    struct wl_output *output;
    uint32_t registry_name;
    int32_t cached_mode_mhz;
    struct output_info *next;
};

static struct wl_display *g_cached_display;
static struct wl_registry *g_registry;
static struct wl_compositor *g_compositor;
static struct wl_subcompositor *g_subcompositor;
static struct wp_color_manager_v1 *g_color_manager;
static struct wp_presentation *g_presentation;
static struct output_info *g_outputs;

// Looks up the registry name for a wl_output proxy in the process-global list. Returns 0 if not found (registry names start at 1 per the Wayland spec, so 0 is unambiguous here).
static uint32_t lookup_output_registry_name(struct wl_output *o)
{
    for (struct output_info *it = g_outputs; it; it = it->next)
    {
        if (it->output == o)
        {
            return it->registry_name;
        }
    }
    return 0;
}

// Callbacks may be registered either before ensure_globals fires (no events yet, nothing to replay) or after (initial enumeration complete, replay cached modes so consumer state catches up). The replay decouples ordering between vom_set_output_callbacks and whatever triggers ensure_globals.
void vom_set_output_callbacks(vom_output_mode_fn mode, vom_output_removed_fn removed)
{
    g_output_mode_cb = mode;
    g_output_removed_cb = removed;
    for (struct output_info *it = g_outputs; it; it = it->next)
    {
        if (it->cached_mode_mhz != 0 && g_output_mode_cb)
        {
            g_output_mode_cb(it->registry_name, it->cached_mode_mhz);
        }
    }
}

static void output_handle_geometry(void *data, struct wl_output *o,
    int32_t x, int32_t y, int32_t phys_w, int32_t phys_h,
    int32_t subpixel, const char *make, const char *model, int32_t transform)
{
    (void)data; (void)o; (void)x; (void)y; (void)phys_w; (void)phys_h;
    (void)subpixel; (void)make; (void)model; (void)transform;
}

// Panels advertise their full mode list; only the entry flagged MODE_CURRENT reflects the currently-selected timing. refresh is in millihertz — 60 Hz = 60000 mHz.
static void output_handle_mode(void *data, struct wl_output *o,
    uint32_t flags, int32_t width, int32_t height, int32_t refresh)
{
    (void)o; (void)width; (void)height;
    struct output_info *info = data;
    if (!(flags & WL_OUTPUT_MODE_CURRENT))
    {
        return;
    }
    info->cached_mode_mhz = refresh;
    if (g_output_mode_cb)
    {
        g_output_mode_cb(info->registry_name, refresh);
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
        // v2 suffices for geometry/mode/done/scale; bump to 4 where available to also receive name/description (diagnostic only).
        uint32_t v = version < 4 ? version : 4;
        struct output_info *info = calloc(1, sizeof(*info));
        if (!info) { return; }
        info->output = wl_registry_bind(reg, name, &wl_output_interface, v);
        info->registry_name = name;
        info->cached_mode_mhz = 0;
        info->next = g_outputs;
        g_outputs = info;
        wl_output_add_listener(info->output, &output_listener_impl, info);
    }
}

// Monitor hot-plug removal. Unlink and free the matching output_info, then fire the removed callback so the C# registry can drop its entry. Listener remains attached to the wl_output proxy until wl_output_destroy — safe because the listener handlers are all stateless forwarders.
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
            if (g_output_removed_cb) { g_output_removed_cb(name); }
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

    // Second roundtrip: globals bound above (wl_output in particular) send their initial events in response to binding, which only arrive AFTER the first sync_done. Without this second round we'd see the outputs themselves but no mode events.
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

// Builds a PQ/BT.2020 parametric image description and waits for ready. Returns NULL on failure. The ready/failed roundtrip is a one-shot protocol handshake (not ongoing logic), so it stays here rather than getting split across the ABI.
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
// Entry point 1: subsurface + EGL surface + HDR (used by the Wayland path).
//---------------------------------------------------------------

// In-flight wp_presentation_feedback record. The compositor sends `presented` or `discarded` asynchronously; without tracking, a feedback delivered after vom_video_surface_destroy has freed `vs` would UAF the listener data (and, on the C# side, a freed-then-recycled GCHandle). By keeping a per-vs list of outstanding proxies, destroy() can wp_presentation_feedback_destroy all of them synchronously and nuke their listeners before freeing vs.
struct fb_node
{
    struct wp_presentation_feedback *fb;
    struct fb_node *next;
    struct fb_node *prev;
    struct vom_video_surface *vs;
};

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
    int buffer_w;
    int buffer_h;

    // Callback trampoline targets. Populated by vom_video_surface_set_callbacks.
    void *cb_data;
    vom_surface_enter_fn enter_cb;
    vom_surface_leave_fn leave_cb;
    vom_feedback_presented_fn presented_cb;
    vom_feedback_discarded_fn discarded_cb;

    // Doubly-linked list of in-flight feedback proxies; head only. Listener receives fb_node* as user data, so unlink on terminal event is O(1).
    struct fb_node *fb_head;
};

static void vs_surface_handle_enter(void *data, struct wl_surface *s, struct wl_output *o)
{
    (void)s;
    struct vom_video_surface *vs = data;
    uint32_t name = lookup_output_registry_name(o);
    if (name != 0 && vs->enter_cb)
    {
        vs->enter_cb(vs->cb_data, name);
    }
}

static void vs_surface_handle_leave(void *data, struct wl_surface *s, struct wl_output *o)
{
    (void)s;
    struct vom_video_surface *vs = data;
    uint32_t name = lookup_output_registry_name(o);
    if (name != 0 && vs->leave_cb)
    {
        vs->leave_cb(vs->cb_data, name);
    }
}

static const struct wl_surface_listener vs_surface_listener = {
    .enter = vs_surface_handle_enter,
    .leave = vs_surface_handle_leave,
};

static void fb_unlink(struct fb_node *n)
{
    if (n->prev) { n->prev->next = n->next; }
    else { n->vs->fb_head = n->next; }
    if (n->next) { n->next->prev = n->prev; }
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
    struct fb_node *n = data;
    struct vom_video_surface *vs = n->vs;
    uint64_t now_ns = (((uint64_t)tv_sec_hi << 32) | tv_sec_lo) * 1000000000ULL + tv_nsec;
    if (vs->presented_cb)
    {
        vs->presented_cb(vs->cb_data, now_ns, refresh);
    }
    fb_unlink(n);
    wp_presentation_feedback_destroy(fb);
    free(n);
}

static void feedback_discarded(void *data, struct wp_presentation_feedback *fb)
{
    struct fb_node *n = data;
    struct vom_video_surface *vs = n->vs;
    if (vs->discarded_cb)
    {
        vs->discarded_cb(vs->cb_data);
    }
    fb_unlink(n);
    wp_presentation_feedback_destroy(fb);
    free(n);
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
    int initial_w, int initial_h, int initial_buffer_scale)
{
    if (!display || !parent)
    {
        fprintf(stderr, "[vom_wayland] create: null display or parent\n");
        return NULL;
    }

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

    // The subsurface starts with no wp_color_management_v1 image description attached — the compositor treats it as sRGB by default. vom_video_surface_set_hdr later toggles a PQ/BT.2020 description in response to per-video source-colorspace detection; the toggle's commit is piggy-backed on the next eglSwapBuffers so the CM state and the first new-content buffer land atomically.

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

    fprintf(stderr, "[vom_wayland] subsurface created (w=%d h=%d)\n",
            vs->buffer_w, vs->buffer_h);
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

void vom_video_surface_set_callbacks(struct vom_video_surface *vs, void *data,
    vom_surface_enter_fn enter, vom_surface_leave_fn leave,
    vom_feedback_presented_fn presented, vom_feedback_discarded_fn discarded)
{
    if (!vs) { return; }
    vs->cb_data = data;
    vs->enter_cb = enter;
    vs->leave_cb = leave;
    vs->presented_cb = presented;
    vs->discarded_cb = discarded;
}

// Caller (C# wrapper) is responsible for clamping inputs and deciding when geometry actually changed; this entry point just executes the protocol sequence unconditionally.
void vom_video_surface_set_geometry(struct vom_video_surface *vs, int x, int y, int w, int h, int buffer_scale)
{
    if (!vs) { return; }

    int new_buffer_w = w * buffer_scale;
    int new_buffer_h = h * buffer_scale;

    wl_subsurface_set_position(vs->wl_subsurface, x, y);
    wl_egl_window_resize(vs->egl_window, new_buffer_w, new_buffer_h, 0, 0);
    wl_surface_set_buffer_scale(vs->wl_surface, buffer_scale);
    vs->buffer_w = new_buffer_w;
    vs->buffer_h = new_buffer_h;

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
    // wp_presentation_feedback must be requested BEFORE the commit it pertains to. eglSwapBuffers internally commits, so we request here. The feedback events fire asynchronously once the compositor actually presents the frame — GTK's main-loop dispatch on the shared wl_display delivers them to our listener, which trampolines up to FrameTimingBridge in C#.
    if (g_presentation)
    {
        struct wp_presentation_feedback *fb = wp_presentation_feedback(g_presentation, vs->wl_surface);
        if (fb)
        {
            struct fb_node *n = calloc(1, sizeof(*n));
            if (!n)
            {
                wp_presentation_feedback_destroy(fb);
            }
            else
            {
                n->fb = fb;
                n->vs = vs;
                n->prev = NULL;
                n->next = vs->fb_head;
                if (vs->fb_head) { vs->fb_head->prev = n; }
                vs->fb_head = n;
                wp_presentation_feedback_add_listener(fb, &feedback_listener, n);
            }
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

// Toggles a PQ/BT.2020 image description on the subsurface's wp_color_management_v1 surface. Intentionally does NOT call wl_surface_commit — the next eglSwapBuffers flushes the CM state double-buffered alongside the first new-content buffer, so tag-change and frame-change land atomically on the compositor (no one-frame flash of mis-tagged content). Caller (C#) must only call EnableHdrOutput on mpv if this returns 0; returning -1 means the compositor didn't advertise wp_color_manager_v1 (or description build failed) and mpv must stay on default-auto targets, else PQ-encoded output would hit an untagged surface.
int vom_video_surface_set_hdr(struct vom_video_surface *vs, int enable)
{
    if (!vs)
    {
        return -1;
    }
    if (!g_color_manager)
    {
        return -1;
    }
    if (enable)
    {
        if (!vs->image_desc)
        {
            vs->image_desc = build_pq_description(vs->display);
            if (!vs->image_desc)
            {
                return -1;
            }
        }
        if (!vs->cm_surface)
        {
            vs->cm_surface = wp_color_manager_v1_get_surface(g_color_manager, vs->wl_surface);
            if (!vs->cm_surface)
            {
                fprintf(stderr, "[vom_wayland] set_hdr: get_surface failed\n");
                return -1;
            }
        }
        wp_color_management_surface_v1_set_image_description(
            vs->cm_surface, vs->image_desc, WP_COLOR_MANAGER_V1_RENDER_INTENT_PERCEPTUAL);
        return 0;
    }
    if (vs->cm_surface)
    {
        wp_color_management_surface_v1_unset_image_description(vs->cm_surface);
    }
    return 0;
}

void vom_video_surface_destroy(struct vom_video_surface *vs)
{
    if (!vs) { return; }
    // Null the callbacks first so any listener that fires between now and actual proxy destruction becomes a no-op. Then destroy all in-flight feedback proxies synchronously — post-destroy, no late feedback can deliver against a freed vs or a recycled GCHandle on the C# side.
    vs->cb_data = NULL;
    vs->enter_cb = NULL;
    vs->leave_cb = NULL;
    vs->presented_cb = NULL;
    vs->discarded_cb = NULL;
    while (vs->fb_head)
    {
        struct fb_node *n = vs->fb_head;
        vs->fb_head = n->next;
        wp_presentation_feedback_destroy(n->fb);
        free(n);
    }
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
