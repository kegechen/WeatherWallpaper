// SPDX-License-Identifier: MIT
//
// wwp-set：在 treeland 下用命令行查看/切换壁纸，走的是控制中心同一个协议 treeland_wallpaper_manager_v1。
//
//   wwp-set                         打印每个屏幕当前的桌面/锁屏壁纸
//   wwp-set [选项] <路径>            把壁纸设成 <路径>（默认桌面+锁屏）
//     --desktop | --lockscreen       只设一种
//     --image                        按图片设置（默认按视频设置；.wwp 壁纸包必须按视频）
//
// 需要在用户会话里运行（WAYLAND_DISPLAY / XDG_RUNTIME_DIR 指向 treeland 的用户 socket）。
// 同一个路径同时设桌面和锁屏时，treeland 会对第二次回 already_used，这是正常的。

#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <wayland-client.h>

#include "treeland-wallpaper-manager-unstable-v1-client-protocol.h"

struct output {
    struct wl_output *wl;
    struct treeland_wallpaper_v1 *wallpaper;
    struct output *next;
};

static struct treeland_wallpaper_manager_v1 *manager;
static struct output *outputs;
static int fatal_error;

static const char *role_name(uint32_t role)
{
    switch (role) {
    case TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_DESKTOP: return "桌面";
    case TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_LOCKSCREEN: return "锁屏";
    default: return "?";
    }
}

static void registry_global(void *data, struct wl_registry *registry, uint32_t name,
                            const char *interface, uint32_t version)
{
    (void)data;
    (void)version;
    if (strcmp(interface, treeland_wallpaper_manager_v1_interface.name) == 0) {
        manager = wl_registry_bind(registry, name, &treeland_wallpaper_manager_v1_interface, 1);
    } else if (strcmp(interface, wl_output_interface.name) == 0) {
        struct output *o = calloc(1, sizeof(*o));
        if (!o)
            abort();
        o->wl = wl_registry_bind(registry, name, &wl_output_interface, 1);
        o->next = outputs;
        outputs = o;
    }
}

static void registry_global_remove(void *data, struct wl_registry *registry, uint32_t name)
{
    (void)data;
    (void)registry;
    (void)name;
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

static void wallpaper_failed(void *data, struct treeland_wallpaper_v1 *wallpaper,
                             const char *file_source, uint32_t error)
{
    (void)data;
    (void)wallpaper;
    switch (error) {
    case TREELAND_WALLPAPER_V1_ERROR_ALREADY_USED:
        printf("  already_used（这个路径已经在用，正常）：%s\n", file_source);
        break;
    case TREELAND_WALLPAPER_V1_ERROR_INVALID_SOURCE:
        printf("  失败 invalid_source：%s\n", file_source);
        fatal_error = 1;
        break;
    case TREELAND_WALLPAPER_V1_ERROR_PERMISSION_DENIED:
        printf("  失败 permission_denied：%s\n", file_source);
        fatal_error = 1;
        break;
    default:
        printf("  失败（错误码 %u）：%s\n", error, file_source);
        fatal_error = 1;
    }
}

static void wallpaper_changed(void *data, struct treeland_wallpaper_v1 *wallpaper, uint32_t role,
                              uint32_t source_type, const char *file_source)
{
    (void)data;
    (void)wallpaper;
    printf("  %s\t%s\t%s\n", role_name(role),
           source_type == TREELAND_WALLPAPER_V1_WALLPAPER_SOURCE_TYPE_VIDEO ? "video" : "image",
           file_source);
}

static const struct treeland_wallpaper_v1_listener wallpaper_listener = {
    .failed = wallpaper_failed,
    .changed = wallpaper_changed,
};

static void usage(const char *argv0)
{
    fprintf(stderr, "用法：%s [--desktop|--lockscreen] [--image] [路径]\n", argv0);
}

int main(int argc, char *argv[])
{
    uint32_t roles = TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_DESKTOP
        | TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_LOCKSCREEN;
    int image = 0;
    const char *path = NULL;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--desktop") == 0) {
            roles = TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_DESKTOP;
        } else if (strcmp(argv[i], "--lockscreen") == 0) {
            roles = TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_LOCKSCREEN;
        } else if (strcmp(argv[i], "--image") == 0) {
            image = 1;
        } else if (strcmp(argv[i], "-h") == 0 || strcmp(argv[i], "--help") == 0) {
            usage(argv[0]);
            return 0;
        } else if (argv[i][0] == '-' || path) {
            usage(argv[0]);
            return 2;
        } else {
            path = argv[i];
        }
    }

    char abs[PATH_MAX];
    if (path) {
        if (!realpath(path, abs)) {
            fprintf(stderr, "路径不存在：%s（%s）\n", path, strerror(errno));
            return 1;
        }
        path = abs;
    }

    struct wl_display *display = wl_display_connect(NULL);
    if (!display) {
        fprintf(stderr, "连不上 Wayland（检查 WAYLAND_DISPLAY / XDG_RUNTIME_DIR）\n");
        return 1;
    }
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    wl_display_roundtrip(display);
    if (!manager) {
        fprintf(stderr, "合成器没有 treeland_wallpaper_manager_v1（不是 treeland？）\n");
        return 1;
    }
    if (!outputs) {
        fprintf(stderr, "没有屏幕\n");
        return 1;
    }

    int n = 0;
    for (struct output *o = outputs; o; o = o->next) {
        o->wallpaper = treeland_wallpaper_manager_v1_get_treeland_wallpaper(manager, o->wl, NULL);
        treeland_wallpaper_v1_add_listener(o->wallpaper, &wallpaper_listener, NULL);
        printf("屏幕 %d 当前：\n", n++);
        // 新建对象时 treeland 会先发 changed 告知当前壁纸
        wl_display_roundtrip(display);
    }

    if (path) {
        printf("设置%s%s为 %s（%s）\n",
               (roles & TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_DESKTOP) ? "桌面" : "",
               (roles & TREELAND_WALLPAPER_V1_WALLPAPER_ROLE_LOCKSCREEN) ? "锁屏" : "",
               path, image ? "图片" : "视频");
        for (struct output *o = outputs; o; o = o->next) {
            if (image)
                treeland_wallpaper_v1_set_image_source(o->wallpaper, path, roles);
            else
                treeland_wallpaper_v1_set_video_source(o->wallpaper, path, roles);
        }
        wl_display_roundtrip(display);
        wl_display_roundtrip(display);
    }

    for (struct output *o = outputs; o; o = o->next)
        treeland_wallpaper_v1_destroy(o->wallpaper);
    treeland_wallpaper_manager_v1_destroy(manager);
    wl_display_roundtrip(display);
    wl_display_disconnect(display);
    return fatal_error ? 1 : 0;
}
