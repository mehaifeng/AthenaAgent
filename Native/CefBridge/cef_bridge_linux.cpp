#include "cef_bridge_platform.h"

#include <X11/Xlib.h>
#include <cmath>
#include <mutex>
#include <string>

namespace {

std::mutex g_x11_mu;
Display* g_display = nullptr;
Window g_parking_xid = 0;

static std::once_flag g_xinit_flag;

static Display* EnsureDisplay() {
    std::call_once(g_xinit_flag, []() { XInitThreads(); });
    if (!g_display) {
        g_display = XOpenDisplay(nullptr);
    }
    return g_display;
}

static int ToPixels(int32_t dips, double scale) {
    double s = scale > 0 ? scale : 1.0;
    int px = static_cast<int>(std::lround(dips * s));
    return px > 0 ? px : 1;
}

}  // namespace

namespace athena_cef {

bool PlatformLoadLibrary(const AthenaCefConfig&, std::string&) { return true; }
void PlatformUnloadLibrary() {}

bool PlatformPrepareInitialize(const AthenaCefConfig&, std::string&) {
    std::call_once(g_xinit_flag, []() { XInitThreads(); });
    return true;
}

void PlatformStartMessagePump(bool) {}
void PlatformStopMessagePump() {}
void PlatformSchedulePumpWork(int64_t) {}
void PlatformDoMessageLoopWork() {}

void PlatformEnsureParkingParent() {
    std::lock_guard<std::mutex> lock(g_x11_mu);
    if (g_parking_xid != 0) return;
    Display* dpy = EnsureDisplay();
    if (!dpy) return;
    Window root = DefaultRootWindow(dpy);
    g_parking_xid = XCreateSimpleWindow(dpy, root, -10000, -10000, 800, 720, 0, 0, 0);
    XFlush(dpy);
}

void PlatformDestroyParkingParent() {
    std::lock_guard<std::mutex> lock(g_x11_mu);
    if (g_display && g_parking_xid != 0) {
        XDestroyWindow(g_display, g_parking_xid);
        g_parking_xid = 0;
        XFlush(g_display);
    }
    if (g_display) {
        XCloseDisplay(g_display);
        g_display = nullptr;
    }
}

int64_t PlatformCreateHostView(int64_t parent_handle) {
    std::lock_guard<std::mutex> lock(g_x11_mu);
    Display* dpy = EnsureDisplay();
    if (!dpy || parent_handle == 0) return 0;
    Window child = XCreateSimpleWindow(dpy, static_cast<Window>(parent_handle), 0, 0, 1, 1, 0, 0, 0);
    XMapWindow(dpy, child);
    XFlush(dpy);
    return static_cast<int64_t>(child);
}

void PlatformDestroyHostView(int64_t view_handle) {
    if (view_handle == 0) return;
    std::lock_guard<std::mutex> lock(g_x11_mu);
    if (!g_display) return;
    XDestroyWindow(g_display, static_cast<Window>(view_handle));
    XFlush(g_display);
}

void PlatformConfigureWindowInfo(cef_window_info_t* info, int64_t parent_handle,
                                 int32_t width, int32_t height, double scale, bool, bool windowless) {
    info->size = sizeof(cef_window_info_t);
    info->runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    info->bounds = {0, 0, ToPixels(width > 0 ? width : 800, scale), ToPixels(height > 0 ? height : 720, scale)};
    if (windowless) {
        info->windowless_rendering_enabled = 1;
        info->parent_window = 0;
        return;
    }
    PlatformEnsureParkingParent();
    info->parent_window = parent_handle != 0
        ? static_cast<cef_window_handle_t>(parent_handle)
        : static_cast<cef_window_handle_t>(g_parking_xid);
}

void PlatformAttachBrowserView(cef_browser_t* browser, int64_t parent_handle,
                               int32_t width, int32_t height, double scale, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    cef_window_handle_t child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!child) return;

    PlatformEnsureParkingParent();
    std::lock_guard<std::mutex> lock(g_x11_mu);
    Display* dpy = EnsureDisplay();
    if (!dpy) return;
    Window target = (parent_handle != 0 && visible) ? static_cast<Window>(parent_handle) : g_parking_xid;
    if (target == 0) return;
    XReparentWindow(dpy, static_cast<Window>(child), target, 0, 0);
    XMoveResizeWindow(dpy, static_cast<Window>(child), 0, 0, ToPixels(width, scale), ToPixels(height, scale));
    if (parent_handle != 0 && visible) XMapWindow(dpy, static_cast<Window>(child));
    else XUnmapWindow(dpy, static_cast<Window>(child));
    XFlush(dpy);
}

void PlatformResizeBrowserView(cef_browser_t* browser, int32_t width, int32_t height,
                               double scale, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    cef_window_handle_t child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!child) return;

    std::lock_guard<std::mutex> lock(g_x11_mu);
    Display* dpy = EnsureDisplay();
    if (!dpy) return;
    XMoveResizeWindow(dpy, static_cast<Window>(child), 0, 0, ToPixels(width, scale), ToPixels(height, scale));
    if (visible) XMapWindow(dpy, static_cast<Window>(child));
    else XUnmapWindow(dpy, static_cast<Window>(child));
    XFlush(dpy);
}

int PlatformCloseNativeView(cef_browser_t* browser) {
    if (!browser) return 0;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return 0;
    cef_window_handle_t child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!child) return 0;

    std::lock_guard<std::mutex> lock(g_x11_mu);
    Display* dpy = EnsureDisplay();
    if (!dpy) return 0;
    XDestroyWindow(dpy, static_cast<Window>(child));
    XFlush(dpy);
    return 1;
}

}  // namespace athena_cef
