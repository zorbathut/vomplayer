// Wayland helper — exposes two capabilities:
//
// 1. hdr_helper_apply_pq(display, surface)
//    One-shot attach of a PQ/BT.2020 image description to an arbitrary wl_surface. Used by the GLArea fallback path (Linux X11/Xwayland) where we have no subsurface. Known issue: makes GTK's UI look blown out because the compositor reinterprets sRGB widget output as PQ. Kept for compat.
//
// 2. vom_video_surface_* API
//    Creates a wl_subsurface child of a given parent wl_surface, places it ABOVE the parent, builds a dedicated EGL context + EGL surface on top via wl_egl_window, and (optionally) tags that child surface PQ/BT.2020. Used by the Wayland path: main surface stays sRGB (UI looks normal), subsurface is HDR-only. Caller drives rendering: make_current → (caller's render) → swap.
//
// The wp_color_manager_v1 global is bound once per process (via a hidden wl_registry on first use) and cached across both entry points.
//
// Build: gcc -shared -fPIC -o libhdr_helper.so hdr_helper.c color-management-v1-protocol.c -lwayland-client -lwayland-egl -lEGL

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include "color-management-v1-client-protocol.h"

//---------------------------------------------------------------
// Process-global Wayland globals, resolved lazily on first use.
//---------------------------------------------------------------

struct globals_ctx
{
    struct wl_compositor *compositor;
    struct wl_subcompositor *subcompositor;
    struct wp_color_manager_v1 *color_manager;
    int color_manager_ready;
    int done;
};

static struct wl_display *g_cached_display;
static struct wl_compositor *g_compositor;
static struct wl_subcompositor *g_subcompositor;
static struct wp_color_manager_v1 *g_color_manager;

static void globals_reg_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t version)
{
    struct globals_ctx *ctx = data;
    if (strcmp(iface, "wl_compositor") == 0 && !ctx->compositor)
    {
        uint32_t bind_v = version < 4 ? version : 4;
        ctx->compositor = wl_registry_bind(reg, name, &wl_compositor_interface, bind_v);
    }
    else if (strcmp(iface, "wl_subcompositor") == 0 && !ctx->subcompositor)
    {
        ctx->subcompositor = wl_registry_bind(reg, name, &wl_subcompositor_interface, 1);
    }
    else if (strcmp(iface, "wp_color_manager_v1") == 0 && !ctx->color_manager)
    {
        ctx->color_manager = wl_registry_bind(reg, name, &wp_color_manager_v1_interface, version < 1 ? version : 1);
    }
}

static void globals_reg_global_remove(void *data, struct wl_registry *reg, uint32_t name)
{
    (void)data; (void)reg; (void)name;
}

static const struct wl_registry_listener globals_registry_listener = {
    .global = globals_reg_global,
    .global_remove = globals_reg_global_remove,
};

// Binds wl_compositor, wl_subcompositor, and wp_color_manager_v1 (if available) from the display's registry. Safe to call multiple times; no-op after first success. Returns 0 on success, negative on failure.
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

    struct globals_ctx ctx = {0};
    struct wl_registry *reg = wl_display_get_registry(display);
    if (!reg)
    {
        return -2;
    }
    wl_registry_add_listener(reg, &globals_registry_listener, &ctx);
    if (wl_display_roundtrip(display) < 0)
    {
        wl_registry_destroy(reg);
        return -3;
    }
    wl_registry_destroy(reg);

    if (!ctx.compositor || !ctx.subcompositor)
    {
        fprintf(stderr, "[hdr_helper] compositor/subcompositor globals missing\n");
        return -4;
    }

    g_cached_display = display;
    g_compositor = ctx.compositor;
    g_subcompositor = ctx.subcompositor;
    g_color_manager = ctx.color_manager; // may be NULL; HDR will be skipped then.
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

    vs->wl_subsurface = wl_subcompositor_get_subsurface(g_subcompositor, vs->wl_surface, parent);
    if (!vs->wl_subsurface)
    {
        fprintf(stderr, "[vom_wayland] get_subsurface failed\n");
        wl_surface_destroy(vs->wl_surface);
        free(vs);
        return NULL;
    }
    wl_subsurface_place_above(vs->wl_subsurface, parent);
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
