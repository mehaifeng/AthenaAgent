#import <AppKit/AppKit.h>
#import <Cocoa/Cocoa.h>
#import <objc/runtime.h>

#include "cef_bridge_platform.h"
#include "include/capi/cef_app_capi.h"
#include "include/cef_application_mac.h"
#include "include/wrapper/cef_library_loader.h"

#include <atomic>
#include <string>

// Dynamically attach CefAppProtocol conformance onto Avalonia's existing NSApplication class.
static const void* kAthenaHandlingSendEventKey = &kAthenaHandlingSendEventKey;
static IMP g_orig_send_event = nullptr;
static bool g_app_swizzled = false;

static BOOL AthenaIsHandlingSendEvent(id self, SEL) {
    NSNumber* val = objc_getAssociatedObject(self, kAthenaHandlingSendEventKey);
    return val ? [val boolValue] : NO;
}

static void AthenaSetHandlingSendEvent(id self, SEL, BOOL flag) {
    objc_setAssociatedObject(self, kAthenaHandlingSendEventKey, @(flag), OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

static void AthenaSendEvent(id self, SEL _cmd, NSEvent* event) {
    BOOL prev = AthenaIsHandlingSendEvent(self, @selector(isHandlingSendEvent));
    AthenaSetHandlingSendEvent(self, @selector(setHandlingSendEvent:), YES);
    using SendEventFn = void (*)(id, SEL, NSEvent*);
    reinterpret_cast<SendEventFn>(g_orig_send_event)(self, _cmd, event);
    AthenaSetHandlingSendEvent(self, @selector(setHandlingSendEvent:), prev);
}

static bool EnsureNsApplicationConformsToCefAppProtocol(std::string& error) {
    if (g_app_swizzled) return true;
    NSApplication* app = [NSApplication sharedApplication];
    if (!app) {
        error = "NSApplication sharedApplication is unavailable.";
        return false;
    }
    Class cls = [app class];
    if (![app respondsToSelector:@selector(isHandlingSendEvent)]) {
        class_addMethod(cls, @selector(isHandlingSendEvent),
                        reinterpret_cast<IMP>(AthenaIsHandlingSendEvent), "B@:");
    }
    if (![app respondsToSelector:@selector(setHandlingSendEvent:)]) {
        class_addMethod(cls, @selector(setHandlingSendEvent:),
                        reinterpret_cast<IMP>(AthenaSetHandlingSendEvent), "v@:B");
    }
    Method sendMethod = class_getInstanceMethod(cls, @selector(sendEvent:));
    if (sendMethod) {
        g_orig_send_event = method_getImplementation(sendMethod);
        method_setImplementation(sendMethod, reinterpret_cast<IMP>(AthenaSendEvent));
    }
    if (!class_conformsToProtocol(cls, @protocol(CefAppProtocol))) {
        class_addProtocol(cls, @protocol(CefAppProtocol));
    }
    if (!class_conformsToProtocol(cls, @protocol(CrAppProtocol))) {
        class_addProtocol(cls, @protocol(CrAppProtocol));
    }
    if (!class_conformsToProtocol(cls, @protocol(CrAppControlProtocol))) {
        class_addProtocol(cls, @protocol(CrAppControlProtocol));
    }
    g_app_swizzled = true;
    return true;
}

@interface AthenaCefHostView : NSView
- (void)syncSubviewsToBounds;
@end

@implementation AthenaCefHostView
- (void)syncSubviewsToBounds {
    NSRect b = self.bounds;
    if (b.size.width <= 0 || b.size.height <= 0) return;
    NSRect childRect = NSMakeRect(0, 0, b.size.width, b.size.height);
    for (NSView* sub in self.subviews) {
        if (!NSEqualRects(sub.frame, childRect)) {
            [sub setFrame:childRect];
        }
    }
}

- (void)layout {
    [super layout];
    [self syncSubviewsToBounds];
}

- (void)setFrame:(NSRect)frame {
    [super setFrame:frame];
    [self syncSubviewsToBounds];
}

- (void)setFrameSize:(NSSize)newSize {
    [super setFrameSize:newSize];
    [self syncSubviewsToBounds];
}

- (void)setBoundsSize:(NSSize)newSize {
    [super setBoundsSize:newSize];
    [self syncSubviewsToBounds];
}

- (void)viewDidMoveToWindow {
    [super viewDidMoveToWindow];
    [self syncSubviewsToBounds];
}

- (void)didAddSubview:(NSView*)subview {
    [super didAddSubview:subview];
    [self syncSubviewsToBounds];
}
@end

namespace {

NSWindow* g_parking_window = nil;
NSView* g_parking_view = nil;
dispatch_source_t g_pump_timer = nullptr;
std::atomic<bool> g_pump_active{false};
std::atomic<bool> g_pump_pending{false};
std::atomic<bool> g_pump_in_progress{false};
bool g_cef_loaded = false;
bool g_headless = false;

static void RunPumpWork() {
    if (!g_pump_active.load(std::memory_order_acquire)) return;
    if (g_pump_in_progress.exchange(true, std::memory_order_acq_rel)) return;
    g_pump_pending.store(false, std::memory_order_release);
    @autoreleasepool {
        cef_do_message_loop_work();
        if (g_headless) {
            CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.0, true);
        }
    }
    g_pump_in_progress.store(false, std::memory_order_release);
}

static void RunOnMainSync(dispatch_block_t block) {
    if ([NSThread isMainThread]) {
        block();
    } else {
        dispatch_sync(dispatch_get_main_queue(), block);
    }
}

}  // namespace

namespace athena_cef {

bool PlatformLoadLibrary(const AthenaCefConfig& config, std::string& error) {
    if (g_cef_loaded) return true;
    std::string framework = config.framework_dir ? config.framework_dir : "";
    if (framework.empty()) {
        error = "CEF framework_dir is required on macOS.";
        return false;
    }
    std::string dylib = framework + "/Chromium Embedded Framework";
    if (!cef_load_library(dylib.c_str())) {
        error = "Failed to load CEF framework at: " + dylib;
        return false;
    }
    g_cef_loaded = true;
    return true;
}

void PlatformUnloadLibrary() {
    // Intentionally keep Chromium Embedded Framework mapped for the remainder of the process.
    // On macOS its static initializer promotes PartitionAlloc to malloc zones[0]; calling dlclose
    // would unmap code backing active malloc_zone_t pointers during late .NET/native teardown.
}

bool PlatformPrepareInitialize(const AthenaCefConfig& config, std::string& error) {
    g_headless = config.headless_mode != 0;
    __block bool ok = false;
    std::string* err_ptr = &error;
    RunOnMainSync(^{
        ok = EnsureNsApplicationConformsToCefAppProtocol(*err_ptr);
    });
    return ok;
}

void PlatformStartMessagePump(bool headless_mode) {
    g_headless = headless_mode;
    if (g_pump_active.exchange(true, std::memory_order_acq_rel)) return;
    if (headless_mode) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!g_pump_active.load(std::memory_order_acquire)) return;
        g_pump_timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
        // CEF recommends calling cef_do_message_loop_work at most every ~33ms even without schedule callbacks.
        dispatch_source_set_timer(g_pump_timer, dispatch_time(DISPATCH_TIME_NOW, 0),
                                  30 * NSEC_PER_MSEC, 5 * NSEC_PER_MSEC);
        dispatch_source_set_event_handler(g_pump_timer, ^{
            RunPumpWork();
        });
        dispatch_resume(g_pump_timer);
    });
}

void PlatformStopMessagePump() {
    g_pump_active.store(false, std::memory_order_release);
    if (g_headless) return;
    RunOnMainSync(^{
        if (g_pump_timer) {
            dispatch_source_cancel(g_pump_timer);
            g_pump_timer = nullptr;
        }
    });
}

void PlatformDoMessageLoopWork() {
    if (g_pump_in_progress.exchange(true, std::memory_order_acq_rel)) return;
    @autoreleasepool {
        cef_do_message_loop_work();
        if (g_headless) {
            CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.0, true);
        }
    }
    g_pump_in_progress.store(false, std::memory_order_release);
}

void PlatformSchedulePumpWork(int64_t delay_ms) {
    if (!g_pump_active.load(std::memory_order_acquire)) return;
    if (delay_ms <= 0) {
        if (g_pump_pending.exchange(true, std::memory_order_acq_rel)) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            RunPumpWork();
        });
    } else {
        int64_t clamped = delay_ms > 33 ? 33 : delay_ms;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, clamped * NSEC_PER_MSEC),
                       dispatch_get_main_queue(), ^{
            RunPumpWork();
        });
    }
}

void PlatformEnsureParkingParent() {
    RunOnMainSync(^{
        if (g_parking_window) return;
        NSRect rect = NSMakeRect(-10000, -10000, 800, 720);
        g_parking_window = [[NSWindow alloc] initWithContentRect:rect
                                                       styleMask:NSWindowStyleMaskBorderless
                                                         backing:NSBackingStoreBuffered
                                                           defer:NO];
        [g_parking_window setReleasedWhenClosed:NO];
        [g_parking_window setIgnoresMouseEvents:YES];
        g_parking_view = [[AthenaCefHostView alloc] initWithFrame:NSMakeRect(0, 0, 800, 720)];
        [g_parking_window setContentView:g_parking_view];
        [g_parking_window orderOut:nil];
    });
}

void PlatformDestroyParkingParent() {
    RunOnMainSync(^{
        if (g_parking_window) {
            [g_parking_window close];
            g_parking_window = nil;
            g_parking_view = nil;
        }
    });
}

int64_t PlatformCreateHostView(int64_t) {
    __block int64_t handle = 0;
    RunOnMainSync(^{
        // Must match AvnNativeControlHost::CreateDefaultChild: NSViewNotSizable (0) and default
        // non-flipped coordinates. Avalonia's ShowInBounds calls [child setFrame:(0,0,w,h)] BEFORE
        // [holder setFrame:(x,y,w,h)]; setting WidthSizable|HeightSizable on child causes AppKit to
        // apply every size delta twice (2x overflow on open/maximize, 2x shrink/blank space on restore).
        AthenaCefHostView* view = [[AthenaCefHostView alloc] initWithFrame:NSZeroRect];
        [view setWantsLayer:YES];
        [view setClipsToBounds:YES];
        [view setAutoresizesSubviews:NO];
        [view setAutoresizingMask:NSViewNotSizable];
        handle = reinterpret_cast<int64_t>((__bridge_retained void*)view);
    });
    return handle;
}

void PlatformDestroyHostView(int64_t view_handle) {
    if (view_handle == 0) return;
    RunOnMainSync(^{
        NSView* view = (__bridge_transfer NSView*)reinterpret_cast<void*>(view_handle);
        [view removeFromSuperview];
    });
}

void PlatformConfigureWindowInfo(cef_window_info_t* info, int64_t parent_handle,
                                 int32_t width, int32_t height, double, bool visible, bool windowless) {
    info->size = sizeof(cef_window_info_t);
    info->runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    info->bounds = {0, 0, width > 0 ? width : 800, height > 0 ? height : 720};
    if (windowless) {
        info->windowless_rendering_enabled = 1;
        info->parent_view = nullptr;
        return;
    }
    PlatformEnsureParkingParent();
    NSView* parent = parent_handle != 0
        ? (__bridge NSView*)reinterpret_cast<void*>(parent_handle)
        : g_parking_view;
    info->parent_view = (__bridge void*)parent;
    info->hidden = visible ? 0 : 1;
}

void PlatformAttachBrowserView(cef_browser_t* browser, int64_t parent_handle,
                               int32_t width, int32_t height, double, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    void* raw_view = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!raw_view) return;

    RunOnMainSync(^{
        PlatformEnsureParkingParent();
        NSView* child = (__bridge NSView*)raw_view;
        NSView* target = (parent_handle != 0 && visible)
            ? (__bridge NSView*)reinterpret_cast<void*>(parent_handle)
            : g_parking_view;
        if (!target) return;
        if ([child superview] != target) {
            [child removeFromSuperview];
            [target addSubview:child];
        }
        NSRect targetBounds = [target bounds];
        NSRect frame = (target != g_parking_view && targetBounds.size.width > 0 && targetBounds.size.height > 0)
            ? NSMakeRect(0, 0, targetBounds.size.width, targetBounds.size.height)
            : NSMakeRect(0, 0, width > 0 ? width : 1, height > 0 ? height : 1);
        [child setFrame:frame];
        [child setAutoresizingMask:NSViewNotSizable];
        [child setHidden:(parent_handle != 0 && visible) ? NO : YES];
        if ([target isKindOfClass:[AthenaCefHostView class]]) {
            [(AthenaCefHostView*)target syncSubviewsToBounds];
        }
        if (target == g_parking_view && g_parking_window) {
            [g_parking_window setContentSize:frame.size];
        }
    });
}

void PlatformResizeBrowserView(cef_browser_t* browser, int32_t width, int32_t height,
                               double, bool visible) {
    if (!browser) return;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return;
    void* raw_view = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!raw_view) return;

    RunOnMainSync(^{
        NSView* child = (__bridge NSView*)raw_view;
        NSView* superview = [child superview];
        NSRect superBounds = superview ? [superview bounds] : NSZeroRect;
        NSRect frame = (superview && superview != g_parking_view && superBounds.size.width > 0 && superBounds.size.height > 0)
            ? NSMakeRect(0, 0, superBounds.size.width, superBounds.size.height)
            : NSMakeRect(0, 0, width > 0 ? width : 1, height > 0 ? height : 1);
        [child setFrame:frame];
        [child setHidden:visible ? NO : YES];
        if (superview == g_parking_view && g_parking_window) {
            [g_parking_window setContentSize:frame.size];
        }
    });
}

int PlatformCloseNativeView(cef_browser_t* browser) {
    if (!browser) return 0;
    cef_browser_host_t* host = browser->get_host(browser);
    if (!host) return 0;
    void* raw_view = host->get_window_handle(host);
    host->base.release(&host->base);
    if (!raw_view) return 0;

    RunOnMainSync(^{
        NSView* child = (__bridge NSView*)raw_view;
        // Move the child NSView into a disposable offscreen window and close ONLY that window
        // so CEF receives NSWindowWillCloseNotification and fires OnBeforeClose without ever
        // sending performClose: to Avalonia's MainWindow.
        NSWindow* tempWindow = [[NSWindow alloc] initWithContentRect:NSMakeRect(-10000, -10000, 1, 1)
                                                           styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskClosable
                                                             backing:NSBackingStoreBuffered
                                                               defer:NO];
        [tempWindow setReleasedWhenClosed:NO];
        [tempWindow setIgnoresMouseEvents:YES];
        NSView* container = [[AthenaCefHostView alloc] initWithFrame:NSMakeRect(0, 0, 1, 1)];
        [tempWindow setContentView:container];
        [child removeFromSuperview];
        [container addSubview:child];
        [tempWindow close];
        // Keep tempWindow alive through the current run-loop turn while CEF tears down the view.
        dispatch_async(dispatch_get_main_queue(), ^{
            (void)tempWindow;
        });
    });
    return 1;
}

}  // namespace athena_cef
