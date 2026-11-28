#include "include/cef_api_hash.h"
#include "include/capi/cef_app_capi.h"

#if defined(__APPLE__)
#include "include/wrapper/cef_library_loader.h"
#include <mach-o/dyld.h>
#include <libgen.h>
#include <limits.h>
#include <stdio.h>
#include <string>
#include <unistd.h>

static bool LoadCefFrameworkFromHelper() {
    uint32_t size = PATH_MAX;
    char raw_path[PATH_MAX];
    if (_NSGetExecutablePath(raw_path, &size) != 0) {
        return false;
    }
    char resolved[PATH_MAX];
    if (!realpath(raw_path, resolved)) {
        return false;
    }
    // Expected layout:
    // .../Frameworks/<App> Helper[ (GPU|Renderer|Plugin)].app/Contents/MacOS/<exe>
    // Framework is at:
    // .../Frameworks/Chromium Embedded Framework.framework/Chromium Embedded Framework
    std::string dir(resolved);
    for (int i = 0; i < 4; ++i) {
        auto pos = dir.find_last_of('/');
        if (pos == std::string::npos) return false;
        dir.erase(pos);
    }
    std::string framework = dir + "/Chromium Embedded Framework.framework/Chromium Embedded Framework";
    if (access(framework.c_str(), R_OK) == 0 && cef_load_library(framework.c_str())) {
        return true;
    }
    // Fallback to CEF's built-in bundle relative resolver.
    void* loader = cef_scoped_library_loader_create(1);
    return loader != nullptr;
}

int main(int argc, char* argv[]) {
    if (!LoadCefFrameworkFromHelper()) {
        fprintf(stderr, "Athena CEF Helper: failed to load Chromium Embedded Framework.\n");
        return 1;
    }
    cef_api_hash(CEF_API_VERSION, 0);
    cef_main_args_t main_args = {};
    main_args.argc = argc;
    main_args.argv = argv;
    return cef_execute_process(&main_args, nullptr, nullptr);
}

#elif defined(_WIN32)
#include <windows.h>

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE, LPWSTR, int) {
    cef_api_hash(CEF_API_VERSION, 0);
    cef_main_args_t main_args = {};
    main_args.instance = hInstance;
    return cef_execute_process(&main_args, nullptr, nullptr);
}

#else

int main(int argc, char* argv[]) {
    cef_api_hash(CEF_API_VERSION, 0);
    cef_main_args_t main_args = {};
    main_args.argc = argc;
    main_args.argv = argv;
    return cef_execute_process(&main_args, nullptr, nullptr);
}
#endif
