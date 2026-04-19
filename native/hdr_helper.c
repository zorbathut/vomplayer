// Minimal Wayland helper that attaches a PQ+BT.2020 image description to an arbitrary wl_surface. Used by the spike to bypass GTK's color-management sanity check (which fails on KWin because KWin doesn't advertise TRANSFER_FUNCTION_SRGB).
//
// Build: gcc -shared -fPIC -o libhdr_helper.so hdr_helper.c color-management-v1-protocol.c -lwayland-client

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <wayland-client.h>
#include "color-management-v1-client-protocol.h"

struct hdr_ctx {
    struct wp_color_manager_v1 *color_manager;
    uint32_t color_manager_name;
    uint32_t color_manager_version;
    int ready_status; // 0 = pending, 1 = ready, -1 = failed
    int failure_cause;
};

static void reg_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t version)
{
    struct hdr_ctx *ctx = data;
    if (strcmp(iface, "wp_color_manager_v1") == 0)
    {
        ctx->color_manager_name = name;
        ctx->color_manager_version = version;
        ctx->color_manager = wl_registry_bind(reg, name, &wp_color_manager_v1_interface, version < 1 ? version : 1);
    }
}

static void reg_global_remove(void *data, struct wl_registry *reg, uint32_t name) { (void)data; (void)reg; (void)name; }

static const struct wl_registry_listener registry_listener = {
    .global = reg_global,
    .global_remove = reg_global_remove,
};

static void desc_ready(void *data, struct wp_image_description_v1 *desc, uint32_t identity)
{
    (void)desc; (void)identity;
    ((struct hdr_ctx *)data)->ready_status = 1;
}

static void desc_failed(void *data, struct wp_image_description_v1 *desc, uint32_t cause, const char *msg)
{
    (void)desc; (void)msg;
    struct hdr_ctx *ctx = data;
    ctx->ready_status = -1;
    ctx->failure_cause = (int)cause;
    fprintf(stderr, "[hdr_helper] image description failed: cause=%u msg=%s\n", cause, msg ? msg : "");
}

static const struct wp_image_description_v1_listener desc_listener = {
    .failed = desc_failed,
    .ready = desc_ready,
};

// Returns 0 on success, negative on error. Caller keeps owning wl_display / wl_surface.
int hdr_helper_apply_pq(struct wl_display *display, struct wl_surface *surface)
{
    if (!display || !surface)
    {
        fprintf(stderr, "[hdr_helper] null display or surface\n");
        return -1;
    }

    struct hdr_ctx ctx = {0};

    struct wl_registry *reg = wl_display_get_registry(display);
    if (!reg)
    {
        fprintf(stderr, "[hdr_helper] wl_display_get_registry failed\n");
        return -2;
    }
    wl_registry_add_listener(reg, &registry_listener, &ctx);

    // Roundtrip to populate globals.
    if (wl_display_roundtrip(display) < 0)
    {
        fprintf(stderr, "[hdr_helper] registry roundtrip failed\n");
        wl_registry_destroy(reg);
        return -3;
    }

    if (!ctx.color_manager)
    {
        fprintf(stderr, "[hdr_helper] compositor does not advertise wp_color_manager_v1\n");
        wl_registry_destroy(reg);
        return -4;
    }

    fprintf(stderr, "[hdr_helper] bound wp_color_manager_v1 (name=%u version=%u)\n", ctx.color_manager_name, ctx.color_manager_version);

    struct wp_image_description_creator_params_v1 *creator = wp_color_manager_v1_create_parametric_creator(ctx.color_manager);
    if (!creator)
    {
        fprintf(stderr, "[hdr_helper] create_parametric_creator failed\n");
        wp_color_manager_v1_destroy(ctx.color_manager);
        wl_registry_destroy(reg);
        return -5;
    }

    wp_image_description_creator_params_v1_set_primaries_named(creator, WP_COLOR_MANAGER_V1_PRIMARIES_BT2020);
    wp_image_description_creator_params_v1_set_tf_named(creator, WP_COLOR_MANAGER_V1_TRANSFER_FUNCTION_ST2084_PQ);

    // Tag mastering display metadata so libplacebo / compositor have peak info. Values in 1/10000ths (Rec.2020 primaries), as per wp_color_management spec.
    wp_image_description_creator_params_v1_set_mastering_display_primaries(
        creator,
        34000, 16000,   // R
        13250, 34500,   // G
         7500,  3000,   // B
        15635, 16450);  // W (D65)

    struct wp_image_description_v1 *desc = wp_image_description_creator_params_v1_create(creator);
    if (!desc)
    {
        fprintf(stderr, "[hdr_helper] params_v1_create failed\n");
        wp_color_manager_v1_destroy(ctx.color_manager);
        wl_registry_destroy(reg);
        return -6;
    }
    wp_image_description_v1_add_listener(desc, &desc_listener, &ctx);

    // Wait for ready or failed.
    while (ctx.ready_status == 0)
    {
        if (wl_display_roundtrip(display) < 0)
        {
            fprintf(stderr, "[hdr_helper] roundtrip while waiting for image_description failed\n");
            wp_image_description_v1_destroy(desc);
            wp_color_manager_v1_destroy(ctx.color_manager);
            wl_registry_destroy(reg);
            return -7;
        }
    }

    if (ctx.ready_status < 0)
    {
        fprintf(stderr, "[hdr_helper] image description not ready (cause=%d)\n", ctx.failure_cause);
        wp_image_description_v1_destroy(desc);
        wp_color_manager_v1_destroy(ctx.color_manager);
        wl_registry_destroy(reg);
        return -8;
    }

    struct wp_color_management_surface_v1 *cm_surf = wp_color_manager_v1_get_surface(ctx.color_manager, surface);
    if (!cm_surf)
    {
        fprintf(stderr, "[hdr_helper] get_surface failed\n");
        wp_image_description_v1_destroy(desc);
        wp_color_manager_v1_destroy(ctx.color_manager);
        wl_registry_destroy(reg);
        return -9;
    }

    wp_color_management_surface_v1_set_image_description(cm_surf, desc, WP_COLOR_MANAGER_V1_RENDER_INTENT_PERCEPTUAL);

    // Commit the surface so the compositor picks up the new color description. wl_surface_commit is valid any time; GTK will re-commit its content afterwards. Must flush to the compositor.
    wl_surface_commit(surface);
    wl_display_flush(display);

    fprintf(stderr, "[hdr_helper] PQ/BT.2020 description attached to surface\n");

    // Leak the proxies on purpose — the compositor keeps the attached description alive via the surface. Destroying the cm_surf here would revert the attachment, and we want it to persist for the process lifetime. wp_image_description_v1 is also left alive. wl_registry too (we'd lose the color_manager if we destroyed it prematurely). This leaks per-call; only called once.

    return 0;
}
