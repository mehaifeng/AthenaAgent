#ifndef ATHENA_CEF_ABI_H
#define ATHENA_CEF_ABI_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(ATHENA_CEF_BRIDGE_BUILD)
#    define ATHENA_CEF_API __declspec(dllexport)
#  else
#    define ATHENA_CEF_API __declspec(dllimport)
#  endif
#else
#  define ATHENA_CEF_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AthenaCefConfig {
    int32_t struct_size;
    const char* runtime_dir;
    const char* framework_dir;
    const char* main_bundle_dir;
    const char* subprocess_path;
    const char* cache_path;
    const char* root_cache_path;
    const char* resources_dir;
    const char* locales_dir;
    const char* log_file;
    int32_t headless_mode;
} AthenaCefConfig;

typedef struct AthenaCefCallbacks {
    int32_t struct_size;
    void* user_data;
    void (*on_context_initialized)(void* user_data);
    void (*on_tab_opened)(void* user_data, int32_t request_id, int32_t tab_id, int32_t opener_id,
                          const char* group, const char* url, const char* title, int32_t muted);
    void (*on_tab_closed)(void* user_data, int32_t tab_id);
    void (*on_navigation)(void* user_data, int32_t tab_id, const char* kind,
                          const char* url, const char* title, int32_t can_go_back, int32_t can_go_forward);
    void (*on_muted_changed)(void* user_data, int32_t tab_id, int32_t muted);
    void (*on_devtools_result)(void* user_data, int32_t tab_id, int32_t message_id,
                               int32_t success, const char* json, int32_t json_len);
    void (*on_devtools_event)(void* user_data, int32_t tab_id, const char* method,
                              const char* params_json, int32_t params_len);
    void (*on_interaction)(void* user_data, int32_t interaction_id, int32_t tab_id,
                           const char* kind, const char* payload_json);
    void (*on_download_completed)(void* user_data, int32_t tab_id, const char* file_name, int32_t ok);
    void (*on_user_interaction)(void* user_data, int32_t tab_id);
    void (*on_site_data_cleared)(void* user_data, int32_t request_id, int32_t ok);
    void (*on_schedule_pump_work)(void* user_data, int64_t delay_ms);
} AthenaCefCallbacks;

ATHENA_CEF_API int32_t athena_cef_preload_library(const char* framework_dir);
ATHENA_CEF_API int32_t athena_cef_initialize(const AthenaCefConfig* config,
                                             const AthenaCefCallbacks* callbacks,
                                             char* error_buf, int32_t error_len);
ATHENA_CEF_API void athena_cef_shutdown(void);
ATHENA_CEF_API void athena_cef_do_message_loop_work(void);

ATHENA_CEF_API int32_t athena_cef_create_tab(int32_t request_id, int64_t parent_handle,
                                             int32_t width, int32_t height, double scale,
                                             const char* group, const char* initial_url, int32_t visible);
ATHENA_CEF_API void athena_cef_set_tab_parent(int32_t tab_id, int64_t parent_handle,
                                              int32_t width, int32_t height, double scale, int32_t visible);
ATHENA_CEF_API void athena_cef_set_tab_bounds(int32_t tab_id, int32_t width, int32_t height,
                                              double scale, int32_t visible);
ATHENA_CEF_API void athena_cef_set_tab_focus(int32_t tab_id, int32_t focused);
ATHENA_CEF_API void athena_cef_set_agent_input_active(int32_t tab_id, int32_t active);
ATHENA_CEF_API void athena_cef_close_tab(int32_t tab_id, int32_t force_close);

ATHENA_CEF_API void athena_cef_navigate(int32_t tab_id, const char* url);
ATHENA_CEF_API void athena_cef_go_back(int32_t tab_id);
ATHENA_CEF_API void athena_cef_go_forward(int32_t tab_id);
ATHENA_CEF_API void athena_cef_reload(int32_t tab_id);
ATHENA_CEF_API int32_t athena_cef_set_muted(int32_t tab_id, int32_t muted);

ATHENA_CEF_API int32_t athena_cef_send_devtools(int32_t tab_id, int32_t message_id,
                                                const char* json_utf8, int32_t json_len);
ATHENA_CEF_API void athena_cef_reply_interaction(int32_t interaction_id, int32_t accept,
                                                 const char* text1, const char* text2);
ATHENA_CEF_API void athena_cef_clear_site_data(int32_t request_id);

ATHENA_CEF_API int64_t athena_cef_create_host_view(int64_t parent_handle);
ATHENA_CEF_API void athena_cef_destroy_host_view(int64_t view_handle);

#ifdef __cplusplus
}
#endif

#endif  // ATHENA_CEF_ABI_H
