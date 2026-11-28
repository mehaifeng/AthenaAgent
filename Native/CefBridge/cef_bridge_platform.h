#ifndef ATHENA_CEF_PLATFORM_H
#define ATHENA_CEF_PLATFORM_H

#include "athena_cef_abi.h"
#include "include/capi/cef_browser_capi.h"

#include <stdint.h>
#include <string>

namespace athena_cef {

bool PlatformLoadLibrary(const AthenaCefConfig& config, std::string& error);
void PlatformUnloadLibrary();
bool PlatformPrepareInitialize(const AthenaCefConfig& config, std::string& error);
void PlatformStartMessagePump(bool headless_mode);
void PlatformStopMessagePump();
void PlatformSchedulePumpWork(int64_t delay_ms);
void PlatformDoMessageLoopWork();
void PlatformEnsureParkingParent();
void PlatformDestroyParkingParent();

int64_t PlatformCreateHostView(int64_t parent_handle);
void PlatformDestroyHostView(int64_t view_handle);

void PlatformConfigureWindowInfo(cef_window_info_t* info, int64_t parent_handle,
                                 int32_t width, int32_t height, double scale, bool visible, bool windowless);
void PlatformAttachBrowserView(cef_browser_t* browser, int64_t parent_handle,
                               int32_t width, int32_t height, double scale, bool visible);
void PlatformResizeBrowserView(cef_browser_t* browser, int32_t width, int32_t height,
                               double scale, bool visible);
int PlatformCloseNativeView(cef_browser_t* browser);

}  // namespace athena_cef

#endif  // ATHENA_CEF_PLATFORM_H
