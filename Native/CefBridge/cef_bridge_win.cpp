#include "cef_bridge_platform.h"

#include <windows.h>
#include <cmath>
#include <string>

namespace {

HWND g_parking_hwnd = nullptr;
const wchar_t* kHostClassName = L"AthenaCefHostContainer";
bool g_class_registered = false;

static LRESULT CALLBACK HostContainerWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (msg == WM_SIZE) {
        int w = LOWORD(lParam);
        int h = HIWORD(lParam);
        if (w > 0 && h > 0) {
            for (HWND child = GetWindow(hwnd, GW_CHILD); child != nullptr; child = GetWindow(child, GW_HWNDNEXT)) {
                SetWindowPos(child, nullptr, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static void EnsureHostClass() {
    if (g_class_registered) return;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(WNDCLASSEXW);
    wc.lpfnWndProc = HostContainerWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kHostClassName;
    wc.style = CS_HREDRAW | CS_VREDRAW;
    RegisterClassExW(&wc);
    g_class_registered = true;
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
bool PlatformPrepareInitialize(const AthenaCefConfig&, std::string&) { return true; }
void PlatformStartMessagePump(bool) {}
void PlatformStopMessagePump() {}
void PlatformSchedulePumpWork(int64_t) {}
void PlatformDoMessageLoopWork() {}

void PlatformEnsureParkingParent() {
    if (g_parking_hwnd) return;
    EnsureHostClass();
    g_parking_hwnd = CreateWindowExW(
        0, kHostClassName, L"", WS_POPUP,
        -10000, -10000, 800, 720, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
}

void PlatformDestroyParkingParent() {
    if (g_parking_hwnd) {
        DestroyWindow(g_parking_hwnd);
        g_parking_hwnd = nullptr;
    }
}

int64_t PlatformCreateHostView(int64_t parent_handle) {
    EnsureHostClass();
    HWND parent = reinterpret_cast<HWND>(static_cast<intptr_t>(parent_handle));
    HWND hwnd = CreateWindowExW(
        0, kHostClassName, L"",
        WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        0, 0, 1, 1, parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    return static_cast<int64_t>(reinterpret_cast<intptr_t>(hwnd));
}

void PlatformDestroyHostView(int64_t view_handle) {
    if (view_handle == 0) return;
    HWND hwnd = reinterpret_cast<HWND>(static_cast<intptr_t>(view_handle));
    if (IsWindow(hwnd)) DestroyWindow(hwnd);
}

void PlatformConfigureWindowInfo(cef_window_info_t* info, int64_t parent_handle,
                                 int32_t width, int32_t height, double scale, bool visible, bool windowless) {
    info->size = sizeof(cef_window_info_t);
    info->runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    info->bounds = {0, 0, ToPixels(width > 0 ? width : 800, scale), ToPixels(height > 0 ? height : 720, scale)};
    if (windowless) {
        info->windowless_rendering_enabled = 1;
        info->parent_window = nullptr;
        return;
    }
    PlatformEnsureParkingParent();
    HWND parent = parent_handle != 0
        ? reinterpret_cast<HWND>(static_cast<intptr_t>(parent_handle))
        : g_parking_hwnd;
    info->parent_window = parent;
    info->style = WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | (visible ? WS_VISIBLE : 0);
}

void PlatformAttachBrowserView(cef_browser_t* browser, int64_t parent_handle,
                               int32_t width, int32_t height, double scale, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    HWND child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!child || !IsWindow(child)) return;

    PlatformEnsureParkingParent();
    HWND target = (parent_handle != 0 && visible)
        ? reinterpret_cast<HWND>(static_cast<intptr_t>(parent_handle))
        : g_parking_hwnd;
    if (!target) return;
    if (GetParent(child) != target) {
        SetParent(child, target);
    }
    int px_w = ToPixels(width, scale);
    int px_h = ToPixels(height, scale);
    RECT rc{};
    if (target != g_parking_hwnd && GetClientRect(target, &rc) && (rc.right - rc.left) > 0 && (rc.bottom - rc.top) > 0) {
        px_w = rc.right - rc.left;
        px_h = rc.bottom - rc.top;
    }
    SetWindowPos(child, nullptr, 0, 0, px_w, px_h, SWP_NOZORDER | SWP_NOACTIVATE);
    ShowWindow(child, (parent_handle != 0 && visible) ? SW_SHOW : SW_HIDE);
}

void PlatformResizeBrowserView(cef_browser_t* browser, int32_t width, int32_t height,
                               double scale, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    HWND child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!child || !IsWindow(child)) return;

    int px_w = ToPixels(width, scale);
    int px_h = ToPixels(height, scale);
    HWND parent = GetParent(child);
    RECT rc{};
    if (parent && parent != g_parking_hwnd && GetClientRect(parent, &rc) && (rc.right - rc.left) > 0 && (rc.bottom - rc.top) > 0) {
        px_w = rc.right - rc.left;
        px_h = rc.bottom - rc.top;
    }
    SetWindowPos(child, nullptr, 0, 0, px_w, px_h, SWP_NOZORDER | SWP_NOACTIVATE);
    ShowWindow(child, visible ? SW_SHOW : SW_HIDE);
}

int PlatformCloseNativeView(cef_browser_t* browser) {
    if (!browser) return 0;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return 0;
    HWND child = host->get_window_handle(host);
    host->base.release(&host->base);
    if (child && IsWindow(child)) {
        DestroyWindow(child);
    }
    return 1;
}

}  // namespace athena_cef
