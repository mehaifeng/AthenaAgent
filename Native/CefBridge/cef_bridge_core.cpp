#include "athena_cef_abi.h"
#include "cef_bridge_platform.h"

#include "include/cef_api_hash.h"
#include "include/capi/cef_app_capi.h"
#include "include/capi/cef_auth_callback_capi.h"
#include "include/capi/cef_browser_capi.h"
#include "include/capi/cef_browser_process_handler_capi.h"
#include "include/capi/cef_callback_capi.h"
#include "include/capi/cef_client_capi.h"
#include "include/capi/cef_command_line_capi.h"
#include "include/capi/cef_cookie_capi.h"
#include "include/capi/cef_devtools_message_observer_capi.h"
#include "include/capi/cef_dialog_handler_capi.h"
#include "include/capi/cef_display_handler_capi.h"
#include "include/capi/cef_download_handler_capi.h"
#include "include/capi/cef_download_item_capi.h"
#include "include/capi/cef_frame_capi.h"
#include "include/capi/cef_input_handler_capi.h"
#include "include/capi/cef_jsdialog_handler_capi.h"
#include "include/capi/cef_life_span_handler_capi.h"
#include "include/capi/cef_load_handler_capi.h"
#include "include/capi/cef_permission_handler_capi.h"
#include "include/capi/cef_registration_capi.h"
#include "include/capi/cef_render_handler_capi.h"
#include "include/capi/cef_request_capi.h"
#include "include/capi/cef_request_context_capi.h"
#include "include/capi/cef_request_handler_capi.h"
#include "include/capi/cef_task_capi.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <chrono>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace {

// RAII smart pointer for CEF C API ref-counted structs.
// Every CEF C API ref-counted struct begins with cef_base_ref_counted_t at offset 0
// (even multi-level subclasses like cef_request_context_t -> cef_preference_manager_t).
template <typename T>
inline cef_base_ref_counted_t* CefBaseOf(T* ptr) {
    return reinterpret_cast<cef_base_ref_counted_t*>(ptr);
}

template <typename T>
class CefHandle {
public:
    CefHandle() = default;
    ~CefHandle() { reset(); }

    CefHandle(const CefHandle& other) : ptr_(other.ptr_) {
        if (ptr_) CefBaseOf(ptr_)->add_ref(CefBaseOf(ptr_));
    }
    CefHandle& operator=(const CefHandle& other) {
        if (this != &other) {
            reset();
            ptr_ = other.ptr_;
            if (ptr_) CefBaseOf(ptr_)->add_ref(CefBaseOf(ptr_));
        }
        return *this;
    }
    CefHandle(CefHandle&& other) noexcept : ptr_(other.ptr_) { other.ptr_ = nullptr; }
    CefHandle& operator=(CefHandle&& other) noexcept {
        if (this != &other) {
            reset();
            ptr_ = other.ptr_;
            other.ptr_ = nullptr;
        }
        return *this;
    }

    static CefHandle Adopt(T* ptr) {
        CefHandle h;
        h.ptr_ = ptr;
        return h;
    }

    static CefHandle Retain(T* ptr) {
        if (ptr) CefBaseOf(ptr)->add_ref(CefBaseOf(ptr));
        return Adopt(ptr);
    }

    void reset(T* ptr = nullptr) {
        if (ptr_) CefBaseOf(ptr_)->release(CefBaseOf(ptr_));
        ptr_ = ptr;
    }

    T* detach() {
        T* p = ptr_;
        ptr_ = nullptr;
        return p;
    }

    T* add_ref_out() const {
        if (ptr_) CefBaseOf(ptr_)->add_ref(CefBaseOf(ptr_));
        return ptr_;
    }

    T* get() const { return ptr_; }
    T* operator->() const { return ptr_; }
    explicit operator bool() const { return ptr_ != nullptr; }

private:
    T* ptr_ = nullptr;
};

template <typename Derived, typename CefStruct>
struct RefBox {
    CefStruct cef{};
    std::atomic<int> ref_count{1};

    RefBox() {
        std::memset(&cef, 0, sizeof(CefStruct));
        cef.base.size = sizeof(CefStruct);
        cef.base.add_ref = &OnAddRef;
        cef.base.release = &OnRelease;
        cef.base.has_one_ref = &OnHasOneRef;
        cef.base.has_at_least_one_ref = &OnHasAtLeastOneRef;
    }

    static Derived* FromBase(cef_base_ref_counted_t* base) {
        return static_cast<Derived*>(reinterpret_cast<RefBox*>(base));
    }
    static void CEF_CALLBACK OnAddRef(cef_base_ref_counted_t* self) {
        FromBase(self)->ref_count.fetch_add(1, std::memory_order_relaxed);
    }
    static int CEF_CALLBACK OnRelease(cef_base_ref_counted_t* self) {
        auto* box = FromBase(self);
        if (box->ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            delete box;
            return 1;
        }
        return 0;
    }
    static int CEF_CALLBACK OnHasOneRef(cef_base_ref_counted_t* self) {
        return FromBase(self)->ref_count.load(std::memory_order_acquire) == 1 ? 1 : 0;
    }
    static int CEF_CALLBACK OnHasAtLeastOneRef(cef_base_ref_counted_t* self) {
        return FromBase(self)->ref_count.load(std::memory_order_acquire) >= 1 ? 1 : 0;
    }
};

struct CefStr {
    cef_string_t s{};
    CefStr() = default;
    explicit CefStr(const char* utf8) {
        if (utf8 && *utf8) {
            cef_string_from_utf8(utf8, std::strlen(utf8), &s);
        }
    }
    explicit CefStr(const std::string& utf8) {
        if (!utf8.empty()) {
            cef_string_from_utf8(utf8.data(), utf8.size(), &s);
        }
    }
    ~CefStr() { cef_string_clear(&s); }
    CefStr(const CefStr&) = delete;
    CefStr& operator=(const CefStr&) = delete;
};

static std::string FromCefStr(const cef_string_t* s) {
    if (!s || !s->str || s->length == 0) return {};
    cef_string_utf8_t u8{};
    if (!cef_string_to_utf8(s->str, s->length, &u8)) return {};
    std::string out(u8.str ? u8.str : "", u8.length);
    cef_string_utf8_clear(&u8);
    return out;
}

static std::string FromCefUserFree(cef_string_userfree_t s) {
    if (!s) return {};
    std::string out = FromCefStr(s);
    cef_string_userfree_free(s);
    return out;
}

static std::string JsonEscape(const std::string& input) {
    std::string out;
    out.reserve(input.size() + 8);
    for (unsigned char c : input) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
        }
    }
    return out;
}

static bool IsAllowedUrl(const std::string& raw, bool allow_blank) {
    if (allow_blank && (raw.empty() || raw == "about:blank")) return true;
    auto scheme_end = raw.find("://");
    if (scheme_end == std::string::npos) return false;
    std::string scheme = raw.substr(0, scheme_end);
    for (char& ch : scheme) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    if (scheme != "http" && scheme != "https") return false;
    std::string rest = raw.substr(scheme_end + 3);
    auto slash = rest.find_first_of("/?#");
    std::string authority = slash == std::string::npos ? rest : rest.substr(0, slash);
    if (authority.empty()) return false;
    if (authority.find('@') != std::string::npos) return false;  // Disallow embedded userinfo.
    return true;
}

struct TaskBox : RefBox<TaskBox, cef_task_t> {
    std::function<void()> fn;
    explicit TaskBox(std::function<void()> f) : fn(std::move(f)) {
        cef.execute = &OnExecute;
    }
    static void CEF_CALLBACK OnExecute(cef_task_t* self) {
        auto* box = static_cast<TaskBox*>(RefBox::FromBase(&self->base));
        if (box->fn) box->fn();
    }
};

static void RunOnUiThread(std::function<void()> fn) {
    if (cef_currently_on(TID_UI)) {
        fn();
        return;
    }
    auto* task = new TaskBox(std::move(fn));
    cef_post_task(TID_UI, &task->cef);
}

enum class InteractionKind {
    File,
    Download,
    JsDialog,
    Auth,
    PermissionPrompt,
    MediaPermission,
};

struct PendingInteraction {
    int32_t tab_id = 0;
    InteractionKind kind = InteractionKind::JsDialog;
    uint32_t requested_media = 0;
    CefHandle<cef_file_dialog_callback_t> file_cb;
    CefHandle<cef_before_download_callback_t> download_cb;
    CefHandle<cef_jsdialog_callback_t> jsdialog_cb;
    CefHandle<cef_auth_callback_t> auth_cb;
    CefHandle<cef_permission_prompt_callback_t> perm_cb;
    CefHandle<cef_media_access_callback_t> media_cb;
};

struct TabState {
    int32_t request_id = 0;
    int32_t tab_id = 0;
    int32_t opener_id = 0;
    std::string group = "browse";
    std::string url = "about:blank";
    std::string title;
    int64_t parent_handle = 0;
    int32_t width = 800;
    int32_t height = 720;
    double scale = 1.0;
    bool visible = false;
    bool windowless = false;
    bool can_go_back = false;
    bool can_go_forward = false;
    std::atomic<bool> agent_input_active{false};
    CefHandle<cef_browser_t> browser;
    CefHandle<cef_registration_t> devtools_reg;
};

struct BridgeGlobals {
    std::mutex mu;
    bool initialized = false;
    bool shutting_down = false;
    bool headless_mode = false;
    AthenaCefCallbacks cb{};
    std::unordered_map<int32_t, std::shared_ptr<TabState>> tabs;
    std::unordered_map<int32_t, PendingInteraction> interactions;
    std::unordered_set<uint32_t> reported_downloads;
    std::atomic<int32_t> next_interaction_id{1};
};

static BridgeGlobals g;

static void CancelInteractionOnUi(PendingInteraction item) {
    switch (item.kind) {
        case InteractionKind::File:
            if (item.file_cb) item.file_cb->cancel(item.file_cb.get());
            break;
        case InteractionKind::Download:
            // CEF cancels the download when callback is released without calling cont().
            break;
        case InteractionKind::JsDialog:
            if (item.jsdialog_cb) item.jsdialog_cb->cont(item.jsdialog_cb.get(), 0, nullptr);
            break;
        case InteractionKind::Auth:
            if (item.auth_cb) item.auth_cb->cancel(item.auth_cb.get());
            break;
        case InteractionKind::PermissionPrompt:
            if (item.perm_cb) item.perm_cb->cont(item.perm_cb.get(), CEF_PERMISSION_RESULT_DENY);
            break;
        case InteractionKind::MediaPermission:
            if (item.media_cb) item.media_cb->cancel(item.media_cb.get());
            break;
    }
}

static int32_t RegisterInteraction(PendingInteraction item) {
    int32_t id = g.next_interaction_id.fetch_add(1, std::memory_order_relaxed);
    std::lock_guard<std::mutex> lock(g.mu);
    if (g.shutting_down) {
        RunOnUiThread([moved = std::move(item)]() mutable { CancelInteractionOnUi(std::move(moved)); });
        return 0;
    }
    g.interactions.emplace(id, std::move(item));
    return id;
}

static std::string PermissionLabel(uint32_t mask, bool media) {
    if (media) {
        bool audio = (mask & (CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE | CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE)) != 0;
        bool video = (mask & (CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE | CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE)) != 0;
        if (audio && video) return "camera, microphone";
        if (video) return "camera";
        if (audio) return "microphone";
        return "media";
    }
    std::vector<const char*> names;
    if (mask & CEF_PERMISSION_TYPE_GEOLOCATION) names.push_back("geolocation");
    if (mask & CEF_PERMISSION_TYPE_NOTIFICATIONS) names.push_back("notifications");
    if (mask & CEF_PERMISSION_TYPE_CLIPBOARD) names.push_back("clipboard");
    if (mask & CEF_PERMISSION_TYPE_MIC_STREAM) names.push_back("microphone");
    if (mask & CEF_PERMISSION_TYPE_CAMERA_STREAM) names.push_back("camera");
    if (names.empty()) return "permission";
    std::string joined;
    for (size_t i = 0; i < names.size(); ++i) {
        if (i > 0) joined += ", ";
        joined += names[i];
    }
    return joined;
}

// Forward declaration for popup child creation.
static CefHandle<cef_client_t> CreateTabClient(std::shared_ptr<TabState> state);

// ---- DevTools observer ----
struct DevToolsObserverBox : RefBox<DevToolsObserverBox, cef_dev_tools_message_observer_t> {
    std::shared_ptr<TabState> state;
    explicit DevToolsObserverBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_dev_tools_message = &OnMessage;
        cef.on_dev_tools_method_result = &OnMethodResult;
        cef.on_dev_tools_event = &OnEvent;
    }
    static int CEF_CALLBACK OnMessage(cef_dev_tools_message_observer_t*, cef_browser_t* browser,
                                      const void*, size_t) {
        CefHandle<cef_browser_t>::Adopt(browser);
        return 0;  // Let CEF route to OnMethodResult / OnEvent.
    }
    static void CEF_CALLBACK OnMethodResult(cef_dev_tools_message_observer_t* self, cef_browser_t* browser,
                                            int message_id, int success, const void* result, size_t result_size) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<DevToolsObserverBox*>(RefBox::FromBase(&self->base));
        if (g.cb.on_devtools_result) {
            g.cb.on_devtools_result(g.cb.user_data, box->state->tab_id, message_id, success ? 1 : 0,
                                    static_cast<const char*>(result), static_cast<int32_t>(result_size));
        }
    }
    static void CEF_CALLBACK OnEvent(cef_dev_tools_message_observer_t* self, cef_browser_t* browser,
                                     const cef_string_t* method, const void* params, size_t params_size) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<DevToolsObserverBox*>(RefBox::FromBase(&self->base));
        if (g.cb.on_devtools_event) {
            std::string m = FromCefStr(method);
            g.cb.on_devtools_event(g.cb.user_data, box->state->tab_id, m.c_str(),
                                   static_cast<const char*>(params), static_cast<int32_t>(params_size));
        }
    }
};

// ---- LifeSpan handler ----
struct LifeSpanHandlerBox : RefBox<LifeSpanHandlerBox, cef_life_span_handler_t> {
    std::shared_ptr<TabState> state;
    explicit LifeSpanHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_before_popup = &OnBeforePopup;
        cef.on_after_created = &OnAfterCreated;
        cef.do_close = &DoClose;
        cef.on_before_close = &OnBeforeClose;
    }

    static int CEF_CALLBACK OnBeforePopup(
        cef_life_span_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, int,
        const cef_string_t* target_url, const cef_string_t*, cef_window_open_disposition_t,
        int, const cef_popup_features_t*, cef_window_info_t* windowInfo, cef_client_t** client,
        cef_browser_settings_t*, cef_dictionary_value_t**, int*) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto f = CefHandle<cef_frame_t>::Adopt(frame);
        auto* box = static_cast<LifeSpanHandlerBox*>(RefBox::FromBase(&self->base));
        std::string url = FromCefStr(target_url);
        if (!IsAllowedUrl(url, true)) {
            return 1;  // Block non-HTTP(S) popups.
        }
        auto child = std::make_shared<TabState>();
        {
            std::lock_guard<std::mutex> lock(g.mu);
            child->request_id = 0;
            child->opener_id = box->state->tab_id;
            child->group = box->state->group;
            child->url = url.empty() ? "about:blank" : url;
            child->parent_handle = box->state->parent_handle;
            child->width = std::max(1, box->state->width);
            child->height = std::max(1, box->state->height);
            child->scale = box->state->scale;
            child->visible = box->state->visible;
            child->windowless = g.headless_mode;
        }
        athena_cef::PlatformConfigureWindowInfo(windowInfo, child->parent_handle, child->width,
                                                child->height, child->scale, child->visible, child->windowless);
        auto child_client = CreateTabClient(child);
        if (client) {
            if (*client) (*client)->base.release(&(*client)->base);
            *client = child_client.detach();
        }
        return 0;  // Allow popup inside our embedded container with opener preserved.
    }

    static void CEF_CALLBACK OnAfterCreated(cef_life_span_handler_t* self, cef_browser_t* browser) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<LifeSpanHandlerBox*>(RefBox::FromBase(&self->base));
        auto st = box->state;
        int32_t tab_id = b->get_identifier(b.get());
        auto host = CefHandle<cef_browser_host_t>::Adopt(b->get_host(b.get()));
        int32_t muted = 0;
        CefHandle<cef_registration_t> reg;
        if (host) {
            muted = host->is_audio_muted(host.get()) ? 1 : 0;
            auto* observer = new DevToolsObserverBox(st);
            reg = CefHandle<cef_registration_t>::Adopt(
                host->add_dev_tools_message_observer(host.get(), &observer->cef));
        }
        {
            std::lock_guard<std::mutex> lock(g.mu);
            st->tab_id = tab_id;
            st->browser = b;
            st->devtools_reg = reg;
            g.tabs[tab_id] = st;
        }
        if (!st->windowless) {
            athena_cef::PlatformAttachBrowserView(b.get(), st->parent_handle, st->width, st->height,
                                                  st->scale, st->visible);
        }
        if (g.cb.on_tab_opened) {
            g.cb.on_tab_opened(g.cb.user_data, st->request_id, tab_id, st->opener_id,
                               st->group.c_str(), st->url.c_str(), st->title.c_str(), muted);
        }
    }

    static int CEF_CALLBACK DoClose(cef_life_span_handler_t* self, cef_browser_t* browser) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<LifeSpanHandlerBox*>(RefBox::FromBase(&self->base));
        if (box->state->windowless) {
            return 0;  // Windowless browsers destroy immediately on return 0.
        }
        // Destroy only the embedded browser child view, never the host Avalonia window.
        return athena_cef::PlatformCloseNativeView(b.get());
    }

    static void CEF_CALLBACK OnBeforeClose(cef_life_span_handler_t* self, cef_browser_t* browser) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<LifeSpanHandlerBox*>(RefBox::FromBase(&self->base));
        auto st = box->state;
        int32_t tab_id = st->tab_id;
        std::vector<PendingInteraction> orphaned;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            g.tabs.erase(tab_id);
            for (auto it = g.interactions.begin(); it != g.interactions.end();) {
                if (it->second.tab_id == tab_id) {
                    orphaned.push_back(std::move(it->second));
                    it = g.interactions.erase(it);
                } else {
                    ++it;
                }
            }
            st->devtools_reg.reset();
            st->browser.reset();
        }
        for (auto& item : orphaned) {
            CancelInteractionOnUi(std::move(item));
        }
        if (g.cb.on_tab_closed && tab_id != 0) {
            g.cb.on_tab_closed(g.cb.user_data, tab_id);
        }
    }
};

// ---- Display handler ----
struct DisplayHandlerBox : RefBox<DisplayHandlerBox, cef_display_handler_t> {
    std::shared_ptr<TabState> state;
    explicit DisplayHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_address_change = &OnAddressChange;
        cef.on_title_change = &OnTitleChange;
    }
    static void CEF_CALLBACK OnAddressChange(cef_display_handler_t* self, cef_browser_t* browser,
                                             cef_frame_t* frame, const cef_string_t* url) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto f = CefHandle<cef_frame_t>::Adopt(frame);
        if (!f || !f->is_main(f.get())) return;
        auto* box = static_cast<DisplayHandlerBox*>(RefBox::FromBase(&self->base));
        std::string next_url = FromCefStr(url);
        if (!IsAllowedUrl(next_url, false)) return;
        std::string title;
        int32_t back = 0, fwd = 0;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            box->state->url = next_url;
            title = box->state->title;
            back = box->state->can_go_back ? 1 : 0;
            fwd = box->state->can_go_forward ? 1 : 0;
        }
        if (g.cb.on_navigation) {
            g.cb.on_navigation(g.cb.user_data, box->state->tab_id, "address",
                               next_url.c_str(), title.c_str(), back, fwd);
        }
    }
    static void CEF_CALLBACK OnTitleChange(cef_display_handler_t* self, cef_browser_t* browser,
                                           const cef_string_t* title) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<DisplayHandlerBox*>(RefBox::FromBase(&self->base));
        std::string next_title = FromCefStr(title);
        std::string url;
        int32_t back = 0, fwd = 0;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            box->state->title = next_title;
            url = box->state->url;
            back = box->state->can_go_back ? 1 : 0;
            fwd = box->state->can_go_forward ? 1 : 0;
        }
        if (IsAllowedUrl(url, false) && g.cb.on_navigation) {
            g.cb.on_navigation(g.cb.user_data, box->state->tab_id, "title",
                               url.c_str(), next_title.c_str(), back, fwd);
        }
    }
};

// ---- Load handler ----
struct LoadHandlerBox : RefBox<LoadHandlerBox, cef_load_handler_t> {
    std::shared_ptr<TabState> state;
    explicit LoadHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_loading_state_change = &OnLoadingStateChange;
        cef.on_load_end = &OnLoadEnd;
    }
    static void CEF_CALLBACK OnLoadingStateChange(cef_load_handler_t* self, cef_browser_t* browser,
                                                  int, int can_go_back, int can_go_forward) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<LoadHandlerBox*>(RefBox::FromBase(&self->base));
        std::lock_guard<std::mutex> lock(g.mu);
        box->state->can_go_back = can_go_back != 0;
        box->state->can_go_forward = can_go_forward != 0;
    }
    static void CEF_CALLBACK OnLoadEnd(cef_load_handler_t* self, cef_browser_t* browser,
                                       cef_frame_t* frame, int http_status_code) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto f = CefHandle<cef_frame_t>::Adopt(frame);
        if (!f || !f->is_main(f.get())) return;
        auto* box = static_cast<LoadHandlerBox*>(RefBox::FromBase(&self->base));
        std::string url = FromCefUserFree(f->get_url(f.get()));
        if (!IsAllowedUrl(url, false)) return;
        std::string title;
        int32_t back = 0, fwd = 0;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            box->state->url = url;
            title = box->state->title;
            back = box->state->can_go_back ? 1 : 0;
            fwd = box->state->can_go_forward ? 1 : 0;
        }
        const char* kind = (http_status_code >= 200 && http_status_code < 400) ? "visit" : "address";
        if (g.cb.on_navigation) {
            g.cb.on_navigation(g.cb.user_data, box->state->tab_id, kind,
                               url.c_str(), title.c_str(), back, fwd);
        }
    }
};

// ---- Request handler ----
struct RequestHandlerBox : RefBox<RequestHandlerBox, cef_request_handler_t> {
    std::shared_ptr<TabState> state;
    explicit RequestHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_before_browse = &OnBeforeBrowse;
        cef.get_auth_credentials = &GetAuthCredentials;
    }
    static int CEF_CALLBACK OnBeforeBrowse(cef_request_handler_t*, cef_browser_t* browser,
                                           cef_frame_t* frame, cef_request_t* request, int, int) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto f = CefHandle<cef_frame_t>::Adopt(frame);
        auto r = CefHandle<cef_request_t>::Adopt(request);
        if (!f || !f->is_main(f.get()) || !r) return 0;
        std::string url = FromCefUserFree(r->get_url(r.get()));
        return IsAllowedUrl(url, true) ? 0 : 1;
    }
    static int CEF_CALLBACK GetAuthCredentials(cef_request_handler_t* self, cef_browser_t* browser,
                                               const cef_string_t* origin_url, int, const cef_string_t* host,
                                               int port, const cef_string_t*, const cef_string_t*,
                                               cef_auth_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto cb = CefHandle<cef_auth_callback_t>::Adopt(callback);
        auto* box = static_cast<RequestHandlerBox*>(RefBox::FromBase(&self->base));
        std::string url = FromCefStr(origin_url);
        if (url.empty()) {
            url = FromCefStr(host) + ":" + std::to_string(port);
        }
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::Auth;
        item.auth_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        if (g.cb.on_interaction) {
            std::string payload = "{\"url\":\"" + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "authentication", payload.c_str());
        }
        return 1;
    }
};

// ---- File dialog handler ----
struct DialogHandlerBox : RefBox<DialogHandlerBox, cef_dialog_handler_t> {
    std::shared_ptr<TabState> state;
    explicit DialogHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_file_dialog = &OnFileDialog;
    }
    static int CEF_CALLBACK OnFileDialog(cef_dialog_handler_t* self, cef_browser_t* browser,
                                         cef_file_dialog_mode_t mode, const cef_string_t*,
                                         const cef_string_t*, cef_string_list_t, cef_string_list_t,
                                         cef_string_list_t, cef_file_dialog_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto cb = CefHandle<cef_file_dialog_callback_t>::Adopt(callback);
        auto* box = static_cast<DialogHandlerBox*>(RefBox::FromBase(&self->base));
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::File;
        item.file_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        std::string url;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            url = box->state->url;
        }
        bool multiple = mode == FILE_DIALOG_OPEN_MULTIPLE;
        if (g.cb.on_interaction) {
            std::string payload = std::string("{\"multiple\":") + (multiple ? "true" : "false")
                + ",\"url\":\"" + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "file", payload.c_str());
        }
        return 1;
    }
};

// ---- Download handler ----
struct DownloadHandlerBox : RefBox<DownloadHandlerBox, cef_download_handler_t> {
    std::shared_ptr<TabState> state;
    explicit DownloadHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.can_download = &CanDownload;
        cef.on_before_download = &OnBeforeDownload;
        cef.on_download_updated = &OnDownloadUpdated;
    }
    static int CEF_CALLBACK CanDownload(cef_download_handler_t*, cef_browser_t* browser,
                                        const cef_string_t*, const cef_string_t*) {
        CefHandle<cef_browser_t>::Adopt(browser);
        return 1;
    }
    static int CEF_CALLBACK OnBeforeDownload(cef_download_handler_t* self, cef_browser_t* browser,
                                             cef_download_item_t* download_item,
                                             const cef_string_t* suggested_name,
                                             cef_before_download_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto d = CefHandle<cef_download_item_t>::Adopt(download_item);
        auto cb = CefHandle<cef_before_download_callback_t>::Adopt(callback);
        auto* box = static_cast<DownloadHandlerBox*>(RefBox::FromBase(&self->base));
        std::string name = FromCefStr(suggested_name);
        if (name.empty() && d) name = FromCefUserFree(d->get_suggested_file_name(d.get()));
        if (name.empty()) name = "download";
        std::string url = d ? FromCefUserFree(d->get_url(d.get())) : "";
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::Download;
        item.download_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        if (g.cb.on_interaction) {
            std::string payload = "{\"name\":\"" + JsonEscape(name) + "\",\"url\":\"" + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "download", payload.c_str());
        }
        return 1;
    }
    static void CEF_CALLBACK OnDownloadUpdated(cef_download_handler_t* self, cef_browser_t* browser,
                                               cef_download_item_t* download_item,
                                               cef_download_item_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto d = CefHandle<cef_download_item_t>::Adopt(download_item);
        auto cb = CefHandle<cef_download_item_callback_t>::Adopt(callback);
        if (!d) return;
        bool complete = d->is_complete(d.get()) != 0;
        bool canceled = d->is_canceled(d.get()) != 0;
        bool interrupted = d->is_interrupted(d.get()) != 0;
        if (!complete && !canceled && !interrupted) return;
        uint32_t dl_id = d->get_id(d.get());
        {
            std::lock_guard<std::mutex> lock(g.mu);
            if (!g.reported_downloads.insert(dl_id).second) return;
        }
        if (canceled && !complete) return;
        auto* box = static_cast<DownloadHandlerBox*>(RefBox::FromBase(&self->base));
        std::string name = FromCefUserFree(d->get_suggested_file_name(d.get()));
        if (name.empty()) {
            std::string path = FromCefUserFree(d->get_full_path(d.get()));
            auto slash = path.find_last_of("/\\");
            name = slash == std::string::npos ? path : path.substr(slash + 1);
        }
        if (g.cb.on_download_completed) {
            g.cb.on_download_completed(g.cb.user_data, box->state->tab_id, name.c_str(), complete ? 1 : 0);
        }
    }
};

// ---- JS Dialog handler ----
struct JsDialogHandlerBox : RefBox<JsDialogHandlerBox, cef_jsdialog_handler_t> {
    std::shared_ptr<TabState> state;
    explicit JsDialogHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_jsdialog = &OnJsDialog;
        cef.on_before_unload_dialog = &OnBeforeUnload;
    }
    static int CEF_CALLBACK OnJsDialog(cef_jsdialog_handler_t* self, cef_browser_t* browser,
                                       const cef_string_t* origin_url, cef_jsdialog_type_t dialog_type,
                                       const cef_string_t* message_text, const cef_string_t* default_prompt_text,
                                       cef_jsdialog_callback_t* callback, int* suppress_message) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto cb = CefHandle<cef_jsdialog_callback_t>::Adopt(callback);
        if (suppress_message) *suppress_message = 0;
        auto* box = static_cast<JsDialogHandlerBox*>(RefBox::FromBase(&self->base));
        const char* kind = dialog_type == JSDIALOGTYPE_PROMPT ? "prompt"
                         : dialog_type == JSDIALOGTYPE_CONFIRM ? "confirm" : "alert";
        std::string msg = FromCefStr(message_text);
        std::string def = FromCefStr(default_prompt_text);
        std::string url = FromCefStr(origin_url);
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::JsDialog;
        item.jsdialog_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        if (g.cb.on_interaction) {
            std::string payload = "{\"dialogType\":\"" + std::string(kind) + "\",\"message\":\""
                + JsonEscape(msg) + "\",\"defaultValue\":\"" + JsonEscape(def) + "\",\"url\":\""
                + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "dialog", payload.c_str());
        }
        return 1;
    }
    static int CEF_CALLBACK OnBeforeUnload(cef_jsdialog_handler_t*, cef_browser_t* browser,
                                           const cef_string_t*, int, cef_jsdialog_callback_t* callback) {
        CefHandle<cef_browser_t>::Adopt(browser);
        auto cb = CefHandle<cef_jsdialog_callback_t>::Adopt(callback);
        if (cb) cb->cont(cb.get(), 1, nullptr);
        return 1;
    }
};

// ---- Permission handler ----
struct PermissionHandlerBox : RefBox<PermissionHandlerBox, cef_permission_handler_t> {
    std::shared_ptr<TabState> state;
    explicit PermissionHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_request_media_access_permission = &OnRequestMedia;
        cef.on_show_permission_prompt = &OnShowPrompt;
    }
    static int CEF_CALLBACK OnRequestMedia(cef_permission_handler_t* self, cef_browser_t* browser,
                                           cef_frame_t* frame, const cef_string_t* requesting_origin,
                                           uint32_t requested_permissions, cef_media_access_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto f = CefHandle<cef_frame_t>::Adopt(frame);
        auto cb = CefHandle<cef_media_access_callback_t>::Adopt(callback);
        auto* box = static_cast<PermissionHandlerBox*>(RefBox::FromBase(&self->base));
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::MediaPermission;
        item.requested_media = requested_permissions;
        item.media_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        std::string url = FromCefStr(requesting_origin);
        std::string perm = PermissionLabel(requested_permissions, true);
        if (g.cb.on_interaction) {
            std::string payload = "{\"permission\":\"" + JsonEscape(perm) + "\",\"url\":\"" + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "permission", payload.c_str());
        }
        return 1;
    }
    static int CEF_CALLBACK OnShowPrompt(cef_permission_handler_t* self, cef_browser_t* browser,
                                         uint64_t, const cef_string_t* requesting_origin,
                                         uint32_t requested_permissions,
                                         cef_permission_prompt_callback_t* callback) {
        auto b = CefHandle<cef_browser_t>::Adopt(browser);
        auto cb = CefHandle<cef_permission_prompt_callback_t>::Adopt(callback);
        auto* box = static_cast<PermissionHandlerBox*>(RefBox::FromBase(&self->base));
        PendingInteraction item;
        item.tab_id = box->state->tab_id;
        item.kind = InteractionKind::PermissionPrompt;
        item.perm_cb = cb;
        int32_t id = RegisterInteraction(std::move(item));
        if (id == 0) return 0;
        std::string url = FromCefStr(requesting_origin);
        std::string perm = PermissionLabel(requested_permissions, false);
        if (g.cb.on_interaction) {
            std::string payload = "{\"permission\":\"" + JsonEscape(perm) + "\",\"url\":\"" + JsonEscape(url) + "\"}";
            g.cb.on_interaction(g.cb.user_data, id, box->state->tab_id, "permission", payload.c_str());
        }
        return 1;
    }
};

// ---- Unified Input handler (CEF 158) for non-blocking user interaction detection ----
struct InputHandlerBox : RefBox<InputHandlerBox, cef_input_handler_t> {
    std::shared_ptr<TabState> state;
    explicit InputHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.on_pre_keyboard_event = &OnPreKey;
        cef.on_pre_mouse_event = &OnPreMouse;
        cef.on_pre_gesture_event = &OnPreGesture;
    }
    static void NotifyUserInput(const std::shared_ptr<TabState>& st) {
        if (st->agent_input_active.load(std::memory_order_acquire)) return;
        if (g.cb.on_user_interaction && st->tab_id != 0) {
            g.cb.on_user_interaction(g.cb.user_data, st->tab_id);
        }
    }
    static int CEF_CALLBACK OnPreKey(cef_input_handler_t* self, cef_browser_t* browser,
                                     const cef_keyboard_input_event_t*, const cef_input_event_context_t*,
                                     int* is_shortcut) {
        CefHandle<cef_browser_t>::Adopt(browser);
        if (is_shortcut) *is_shortcut = 0;
        auto* box = static_cast<InputHandlerBox*>(RefBox::FromBase(&self->base));
        NotifyUserInput(box->state);
        return 0;
    }
    static int CEF_CALLBACK OnPreMouse(cef_input_handler_t* self, cef_browser_t* browser,
                                       const cef_mouse_input_event_t* event) {
        CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<InputHandlerBox*>(RefBox::FromBase(&self->base));
        if (event && event->type == CEF_MOUSE_EVENT_DOWN) {
            NotifyUserInput(box->state);
        }
        return 0;
    }
    static int CEF_CALLBACK OnPreGesture(cef_input_handler_t* self, cef_browser_t* browser,
                                         const cef_gesture_input_event_t* event) {
        CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<InputHandlerBox*>(RefBox::FromBase(&self->base));
        if (event && (event->type == CEF_GESTURE_EVENT_SCROLL_BEGIN || event->type == CEF_GESTURE_EVENT_TAP)) {
            NotifyUserInput(box->state);
        }
        return 0;
    }
};

// ---- Render handler (used ONLY in headless windowless mode) ----
struct RenderHandlerBox : RefBox<RenderHandlerBox, cef_render_handler_t> {
    std::shared_ptr<TabState> state;
    explicit RenderHandlerBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        cef.get_view_rect = &GetViewRect;
        cef.get_screen_info = &GetScreenInfo;
        cef.on_paint = &OnPaint;
    }
    static void CEF_CALLBACK GetViewRect(cef_render_handler_t* self, cef_browser_t* browser, cef_rect_t* rect) {
        CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<RenderHandlerBox*>(RefBox::FromBase(&self->base));
        std::lock_guard<std::mutex> lock(g.mu);
        rect->x = 0;
        rect->y = 0;
        rect->width = std::max(1, box->state->width);
        rect->height = std::max(1, box->state->height);
    }
    static int CEF_CALLBACK GetScreenInfo(cef_render_handler_t* self, cef_browser_t* browser,
                                          cef_screen_info_t* screen_info) {
        CefHandle<cef_browser_t>::Adopt(browser);
        auto* box = static_cast<RenderHandlerBox*>(RefBox::FromBase(&self->base));
        std::lock_guard<std::mutex> lock(g.mu);
        screen_info->size = sizeof(cef_screen_info_t);
        screen_info->device_scale_factor = static_cast<float>(box->state->scale > 0 ? box->state->scale : 1.0);
        screen_info->depth = 24;
        screen_info->depth_per_component = 8;
        screen_info->is_monochrome = 0;
        screen_info->rect = {0, 0, std::max(1, box->state->width), std::max(1, box->state->height)};
        screen_info->available_rect = screen_info->rect;
        return 1;
    }
    static void CEF_CALLBACK OnPaint(cef_render_handler_t*, cef_browser_t* browser,
                                     cef_paint_element_type_t, size_t, cef_rect_t const*,
                                     const void*, int, int) {
        CefHandle<cef_browser_t>::Adopt(browser);
    }
};

// ---- Client ----
struct ClientBox : RefBox<ClientBox, cef_client_t> {
    std::shared_ptr<TabState> state;
    CefHandle<cef_life_span_handler_t> life_span;
    CefHandle<cef_display_handler_t> display;
    CefHandle<cef_load_handler_t> load;
    CefHandle<cef_request_handler_t> request;
    CefHandle<cef_dialog_handler_t> dialog;
    CefHandle<cef_download_handler_t> download;
    CefHandle<cef_jsdialog_handler_t> jsdialog;
    CefHandle<cef_permission_handler_t> permission;
    CefHandle<cef_input_handler_t> input;
    CefHandle<cef_render_handler_t> render;

    explicit ClientBox(std::shared_ptr<TabState> s) : state(std::move(s)) {
        life_span = CefHandle<cef_life_span_handler_t>::Adopt(&(new LifeSpanHandlerBox(state))->cef);
        display = CefHandle<cef_display_handler_t>::Adopt(&(new DisplayHandlerBox(state))->cef);
        load = CefHandle<cef_load_handler_t>::Adopt(&(new LoadHandlerBox(state))->cef);
        request = CefHandle<cef_request_handler_t>::Adopt(&(new RequestHandlerBox(state))->cef);
        dialog = CefHandle<cef_dialog_handler_t>::Adopt(&(new DialogHandlerBox(state))->cef);
        download = CefHandle<cef_download_handler_t>::Adopt(&(new DownloadHandlerBox(state))->cef);
        jsdialog = CefHandle<cef_jsdialog_handler_t>::Adopt(&(new JsDialogHandlerBox(state))->cef);
        permission = CefHandle<cef_permission_handler_t>::Adopt(&(new PermissionHandlerBox(state))->cef);
        input = CefHandle<cef_input_handler_t>::Adopt(&(new InputHandlerBox(state))->cef);
        if (state->windowless) {
            render = CefHandle<cef_render_handler_t>::Adopt(&(new RenderHandlerBox(state))->cef);
        }

        cef.get_life_span_handler = &GetLifeSpan;
        cef.get_display_handler = &GetDisplay;
        cef.get_load_handler = &GetLoad;
        cef.get_request_handler = &GetRequest;
        cef.get_dialog_handler = &GetDialog;
        cef.get_download_handler = &GetDownload;
        cef.get_jsdialog_handler = &GetJsDialog;
        cef.get_permission_handler = &GetPermission;
        cef.get_input_handler = &GetInput;
        cef.get_render_handler = &GetRender;
    }

    static ClientBox* From(cef_client_t* self) { return static_cast<ClientBox*>(RefBox::FromBase(&self->base)); }
    static cef_life_span_handler_t* CEF_CALLBACK GetLifeSpan(cef_client_t* s) { return From(s)->life_span.add_ref_out(); }
    static cef_display_handler_t* CEF_CALLBACK GetDisplay(cef_client_t* s) { return From(s)->display.add_ref_out(); }
    static cef_load_handler_t* CEF_CALLBACK GetLoad(cef_client_t* s) { return From(s)->load.add_ref_out(); }
    static cef_request_handler_t* CEF_CALLBACK GetRequest(cef_client_t* s) { return From(s)->request.add_ref_out(); }
    static cef_dialog_handler_t* CEF_CALLBACK GetDialog(cef_client_t* s) { return From(s)->dialog.add_ref_out(); }
    static cef_download_handler_t* CEF_CALLBACK GetDownload(cef_client_t* s) { return From(s)->download.add_ref_out(); }
    static cef_jsdialog_handler_t* CEF_CALLBACK GetJsDialog(cef_client_t* s) { return From(s)->jsdialog.add_ref_out(); }
    static cef_permission_handler_t* CEF_CALLBACK GetPermission(cef_client_t* s) { return From(s)->permission.add_ref_out(); }
    static cef_input_handler_t* CEF_CALLBACK GetInput(cef_client_t* s) { return From(s)->input.add_ref_out(); }
    static cef_render_handler_t* CEF_CALLBACK GetRender(cef_client_t* s) { return From(s)->render.add_ref_out(); }
};

static CefHandle<cef_client_t> CreateTabClient(std::shared_ptr<TabState> state) {
    return CefHandle<cef_client_t>::Adopt(&(new ClientBox(std::move(state)))->cef);
}

// ---- App & BrowserProcessHandler ----
struct BrowserProcessHandlerBox : RefBox<BrowserProcessHandlerBox, cef_browser_process_handler_t> {
    BrowserProcessHandlerBox() {
        cef.on_context_initialized = &OnContextInitialized;
        cef.on_schedule_message_pump_work = &OnScheduleMessagePumpWork;
    }
    static void CEF_CALLBACK OnContextInitialized(cef_browser_process_handler_t*) {
        if (g.cb.on_context_initialized) {
            g.cb.on_context_initialized(g.cb.user_data);
        }
    }
    static void CEF_CALLBACK OnScheduleMessagePumpWork(cef_browser_process_handler_t*, int64_t delay_ms) {
        if (g.headless_mode) {
            if (g.cb.on_schedule_pump_work) {
                g.cb.on_schedule_pump_work(g.cb.user_data, delay_ms);
            }
            return;
        }
        athena_cef::PlatformSchedulePumpWork(delay_ms);
    }
};

struct AppBox : RefBox<AppBox, cef_app_t> {
    CefHandle<cef_browser_process_handler_t> bph;
    bool headless_mode = false;

    explicit AppBox(bool headless) : headless_mode(headless) {
        bph = CefHandle<cef_browser_process_handler_t>::Adopt(&(new BrowserProcessHandlerBox())->cef);
        cef.on_before_command_line_processing = &OnBeforeCommandLine;
        cef.get_browser_process_handler = &GetBph;
    }

    static void CEF_CALLBACK OnBeforeCommandLine(cef_app_t* self, const cef_string_t* process_type,
                                                 cef_command_line_t* command_line) {
        auto cmd = CefHandle<cef_command_line_t>::Adopt(command_line);
        if (!cmd) return;
        std::string ptype = FromCefStr(process_type);
        if (!ptype.empty()) return;
        auto* box = static_cast<AppBox*>(RefBox::FromBase(&self->base));
        auto add_switch = [&](const char* sw) {
            CefStr cs(sw);
            cmd->append_switch(cmd.get(), &cs.s);
        };
#if defined(__APPLE__)
        add_switch("use-mock-keychain");
#elif defined(__linux__)
        auto add_val = [&](const char* sw, const char* val) {
            CefStr k(sw), v(val);
            cmd->append_switch_with_value(cmd.get(), &k.s, &v.s);
        };
        add_val("ozone-platform", "x11");
        add_val("disable-features", "Vulkan");
#endif
        if (box->headless_mode) {
            add_switch("disable-gpu");
            add_switch("disable-gpu-compositing");
        }
    }

    static cef_browser_process_handler_t* CEF_CALLBACK GetBph(cef_app_t* self) {
        return static_cast<AppBox*>(RefBox::FromBase(&self->base))->bph.add_ref_out();
    }
};

struct DeleteCookiesCallbackBox : RefBox<DeleteCookiesCallbackBox, cef_delete_cookies_callback_t> {
    int32_t request_id;
    explicit DeleteCookiesCallbackBox(int32_t req) : request_id(req) {
        cef.on_complete = &OnComplete;
    }
    static void CEF_CALLBACK OnComplete(cef_delete_cookies_callback_t* self, int) {
        auto* box = static_cast<DeleteCookiesCallbackBox*>(RefBox::FromBase(&self->base));
        int32_t req = box->request_id;
        auto ctx = CefHandle<cef_request_context_t>::Adopt(cef_request_context_get_global_context());
        if (ctx) {
            auto cm = CefHandle<cef_cookie_manager_t>::Adopt(ctx->get_cookie_manager(ctx.get(), nullptr));
            if (cm) cm->flush_store(cm.get(), nullptr);
        }
        if (g.cb.on_site_data_cleared) {
            g.cb.on_site_data_cleared(g.cb.user_data, req, 1);
        }
    }
};

static std::shared_ptr<TabState> FindTab(int32_t tab_id) {
    std::lock_guard<std::mutex> lock(g.mu);
    auto it = g.tabs.find(tab_id);
    return it == g.tabs.end() ? nullptr : it->second;
}

static void CopyError(char* buf, int32_t len, const std::string& msg) {
    if (!buf || len <= 0) return;
    std::snprintf(buf, static_cast<size_t>(len), "%s", msg.c_str());
}

static CefHandle<cef_app_t> g_app;

}  // namespace

extern "C" {

ATHENA_CEF_API int32_t athena_cef_preload_library(const char* framework_dir) {
    AthenaCefConfig cfg{};
    cfg.struct_size = sizeof(cfg);
    cfg.framework_dir = framework_dir;
    std::string err;
    return athena_cef::PlatformLoadLibrary(cfg, err) ? 1 : 0;
}

ATHENA_CEF_API int32_t athena_cef_initialize(const AthenaCefConfig* config,
                                             const AthenaCefCallbacks* callbacks,
                                             char* error_buf, int32_t error_len) {
    if (!config || config->struct_size != static_cast<int32_t>(sizeof(AthenaCefConfig)) ||
        !callbacks || callbacks->struct_size != static_cast<int32_t>(sizeof(AthenaCefCallbacks))) {
        CopyError(error_buf, error_len, "Invalid CEF bridge config or callback structure size.");
        return 0;
    }
    {
        std::lock_guard<std::mutex> lock(g.mu);
        if (g.initialized) {
            g.cb = *callbacks;
            return 1;
        }
        g.cb = *callbacks;
        g.headless_mode = config->headless_mode != 0;
        g.shutting_down = false;
    }

    std::string err;
    if (!athena_cef::PlatformLoadLibrary(*config, err)) {
        CopyError(error_buf, error_len, err.empty() ? "Failed to load CEF library." : err);
        return 0;
    }

    cef_api_hash(CEF_API_VERSION, 0);

    if (!athena_cef::PlatformPrepareInitialize(*config, err)) {
        CopyError(error_buf, error_len, err.empty() ? "Platform CEF preparation failed." : err);
        return 0;
    }

    cef_settings_t settings{};
    settings.size = sizeof(cef_settings_t);
    settings.no_sandbox = 1;
    settings.windowless_rendering_enabled = config->headless_mode ? 1 : 0;
    settings.persist_session_cookies = 0;
    settings.log_severity = LOGSEVERITY_WARNING;

#if defined(__APPLE__)
    settings.multi_threaded_message_loop = 0;
    settings.external_message_pump = 1;
#else
    settings.multi_threaded_message_loop = 1;
    settings.external_message_pump = 0;
#endif

    CefStr subprocess(config->subprocess_path ? config->subprocess_path : "");
    CefStr framework(config->framework_dir ? config->framework_dir : "");
    CefStr main_bundle(config->main_bundle_dir ? config->main_bundle_dir : "");
    CefStr cache(config->cache_path ? config->cache_path : "");
    CefStr root_cache(config->root_cache_path ? config->root_cache_path : "");
    CefStr resources(config->resources_dir ? config->resources_dir : "");
    CefStr locales(config->locales_dir ? config->locales_dir : "");
    CefStr log_file(config->log_file ? config->log_file : "");

    if (subprocess.s.length) cef_string_copy(subprocess.s.str, subprocess.s.length, &settings.browser_subprocess_path);
    if (framework.s.length) cef_string_copy(framework.s.str, framework.s.length, &settings.framework_dir_path);
    if (main_bundle.s.length) cef_string_copy(main_bundle.s.str, main_bundle.s.length, &settings.main_bundle_path);
    if (cache.s.length) cef_string_copy(cache.s.str, cache.s.length, &settings.cache_path);
    if (root_cache.s.length) cef_string_copy(root_cache.s.str, root_cache.s.length, &settings.root_cache_path);
    if (resources.s.length) cef_string_copy(resources.s.str, resources.s.length, &settings.resources_dir_path);
    if (locales.s.length) cef_string_copy(locales.s.str, locales.s.length, &settings.locales_dir_path);
    if (log_file.s.length) cef_string_copy(log_file.s.str, log_file.s.length, &settings.log_file);

    cef_main_args_t main_args{};
#if defined(_WIN32)
    main_args.instance = GetModuleHandleW(nullptr);
#else
    static char arg0[] = "Athena.UI";
    static char* argv_buf[] = {arg0, nullptr};
    main_args.argc = 1;
    main_args.argv = argv_buf;
#endif

    g_app = CefHandle<cef_app_t>::Adopt(&(new AppBox(config->headless_mode != 0))->cef);
    int ok = cef_initialize(&main_args, &settings, g_app.add_ref_out(), nullptr);

    cef_string_clear(&settings.browser_subprocess_path);
    cef_string_clear(&settings.framework_dir_path);
    cef_string_clear(&settings.main_bundle_path);
    cef_string_clear(&settings.cache_path);
    cef_string_clear(&settings.root_cache_path);
    cef_string_clear(&settings.resources_dir_path);
    cef_string_clear(&settings.locales_dir_path);
    cef_string_clear(&settings.log_file);

    if (!ok) {
        int code = cef_get_exit_code();
        g_app.reset();
        CopyError(error_buf, error_len, "cef_initialize failed with exit code " + std::to_string(code) + ".");
        return 0;
    }

    {
        std::lock_guard<std::mutex> lock(g.mu);
        g.initialized = true;
    }

    if (!config->headless_mode) {
        athena_cef::PlatformEnsureParkingParent();
    }
    athena_cef::PlatformStartMessagePump(config->headless_mode != 0);
    return 1;
}

ATHENA_CEF_API void athena_cef_shutdown(void) {
    {
        std::lock_guard<std::mutex> lock(g.mu);
        if (!g.initialized || g.shutting_down) return;
        g.shutting_down = true;
    }

    athena_cef::PlatformStopMessagePump();

    // Close remaining tabs before cef_shutdown.
    std::vector<std::shared_ptr<TabState>> remaining;
    {
        std::lock_guard<std::mutex> lock(g.mu);
        for (auto& kv : g.tabs) remaining.push_back(kv.second);
    }
    for (auto& st : remaining) {
        RunOnUiThread([st]() {
            if (st->browser) {
                auto host = CefHandle<cef_browser_host_t>::Adopt(st->browser->get_host(st->browser.get()));
                if (host) host->close_browser(host.get(), 1);
            }
        });
    }

#if defined(__APPLE__)
    for (int i = 0; i < 150; ++i) {
        athena_cef::PlatformDoMessageLoopWork();
        bool empty = false;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            empty = g.tabs.empty();
        }
        if (empty) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    // Pump a few extra cycles so final renderer/cookie flushes finish cleanly.
    for (int i = 0; i < 10; ++i) {
        athena_cef::PlatformDoMessageLoopWork();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
#else
    for (int i = 0; i < 150; ++i) {
        bool empty = false;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            empty = g.tabs.empty();
        }
        if (empty) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
#endif

    athena_cef::PlatformDestroyParkingParent();
    cef_shutdown();
    g_app.reset();
    athena_cef::PlatformUnloadLibrary();

    std::lock_guard<std::mutex> lock(g.mu);
    g.initialized = false;
    g.tabs.clear();
    g.interactions.clear();
    g.reported_downloads.clear();
}

ATHENA_CEF_API void athena_cef_do_message_loop_work(void) {
    {
        std::lock_guard<std::mutex> lock(g.mu);
        if (!g.initialized || g.shutting_down) return;
    }
    athena_cef::PlatformDoMessageLoopWork();
}

ATHENA_CEF_API int32_t athena_cef_create_tab(int32_t request_id, int64_t parent_handle,
                                             int32_t width, int32_t height, double scale,
                                             const char* group, const char* initial_url, int32_t visible) {
    bool headless = false;
    {
        std::lock_guard<std::mutex> lock(g.mu);
        if (!g.initialized || g.shutting_down) return 0;
        headless = g.headless_mode;
    }
    auto st = std::make_shared<TabState>();
    st->request_id = request_id;
    st->group = (group && *group) ? group : "browse";
    st->url = (initial_url && *initial_url) ? initial_url : "about:blank";
    st->parent_handle = parent_handle;
    st->width = std::max(1, width);
    st->height = std::max(1, height);
    st->scale = scale > 0 ? scale : 1.0;
    st->visible = visible != 0;
    st->windowless = headless;

    RunOnUiThread([st]() {
        cef_window_info_t info{};
        info.size = sizeof(cef_window_info_t);
        athena_cef::PlatformConfigureWindowInfo(&info, st->parent_handle, st->width, st->height,
                                                st->scale, st->visible, st->windowless);

        cef_browser_settings_t bs{};
        bs.size = sizeof(cef_browser_settings_t);
        bs.windowless_frame_rate = 30;

        CefStr url(st->url);
        auto client = CreateTabClient(st);
        cef_browser_host_create_browser(&info, client.detach(), &url.s, &bs, nullptr, nullptr);
    });
    return 1;
}

ATHENA_CEF_API void athena_cef_set_tab_parent(int32_t tab_id, int64_t parent_handle,
                                              int32_t width, int32_t height, double scale, int32_t visible) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st, parent_handle, width, height, scale, visible]() {
        CefHandle<cef_browser_t> browser;
        bool windowless = false;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            st->parent_handle = parent_handle;
            st->width = std::max(1, width);
            st->height = std::max(1, height);
            st->scale = scale > 0 ? scale : 1.0;
            st->visible = visible != 0;
            browser = st->browser;
            windowless = st->windowless;
        }
        if (!browser) return;
        if (windowless) {
            auto host = CefHandle<cef_browser_host_t>::Adopt(browser->get_host(browser.get()));
            if (host) {
                host->notify_screen_info_changed(host.get());
                host->was_resized(host.get());
            }
            return;
        }
        athena_cef::PlatformAttachBrowserView(browser.get(), parent_handle, std::max(1, width),
                                              std::max(1, height), scale > 0 ? scale : 1.0, visible != 0);
    });
}

ATHENA_CEF_API void athena_cef_set_tab_bounds(int32_t tab_id, int32_t width, int32_t height,
                                              double scale, int32_t visible) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st, width, height, scale, visible]() {
        CefHandle<cef_browser_t> browser;
        bool windowless = false;
        {
            std::lock_guard<std::mutex> lock(g.mu);
            st->width = std::max(1, width);
            st->height = std::max(1, height);
            st->scale = scale > 0 ? scale : 1.0;
            st->visible = visible != 0;
            browser = st->browser;
            windowless = st->windowless;
        }
        if (!browser) return;
        if (windowless) {
            auto host = CefHandle<cef_browser_host_t>::Adopt(browser->get_host(browser.get()));
            if (host) {
                host->notify_screen_info_changed(host.get());
                host->was_resized(host.get());
            }
            return;
        }
        athena_cef::PlatformResizeBrowserView(browser.get(), std::max(1, width), std::max(1, height),
                                              scale > 0 ? scale : 1.0, visible != 0);
    });
}

ATHENA_CEF_API void athena_cef_set_tab_focus(int32_t tab_id, int32_t focused) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st, focused]() {
        if (!st->browser) return;
        auto host = CefHandle<cef_browser_host_t>::Adopt(st->browser->get_host(st->browser.get()));
        if (host) host->set_focus(host.get(), focused ? 1 : 0);
    });
}

ATHENA_CEF_API void athena_cef_set_agent_input_active(int32_t tab_id, int32_t active) {
    auto st = FindTab(tab_id);
    if (!st) return;
    st->agent_input_active.store(active != 0, std::memory_order_release);
}

ATHENA_CEF_API void athena_cef_close_tab(int32_t tab_id, int32_t force_close) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st, force_close]() {
        if (!st->browser) return;
        auto host = CefHandle<cef_browser_host_t>::Adopt(st->browser->get_host(st->browser.get()));
        if (host) host->close_browser(host.get(), force_close ? 1 : 0);
    });
}

ATHENA_CEF_API void athena_cef_navigate(int32_t tab_id, const char* url) {
    auto st = FindTab(tab_id);
    if (!st || !url) return;
    std::string target(url);
    RunOnUiThread([st, target]() {
        if (!st->browser) return;
        auto frame = CefHandle<cef_frame_t>::Adopt(st->browser->get_main_frame(st->browser.get()));
        if (!frame) return;
        CefStr cs(target);
        frame->load_url(frame.get(), &cs.s);
    });
}

ATHENA_CEF_API void athena_cef_go_back(int32_t tab_id) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st]() {
        if (st->browser && st->browser->can_go_back(st->browser.get())) {
            st->browser->go_back(st->browser.get());
        }
    });
}

ATHENA_CEF_API void athena_cef_go_forward(int32_t tab_id) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st]() {
        if (st->browser && st->browser->can_go_forward(st->browser.get())) {
            st->browser->go_forward(st->browser.get());
        }
    });
}

ATHENA_CEF_API void athena_cef_reload(int32_t tab_id) {
    auto st = FindTab(tab_id);
    if (!st) return;
    RunOnUiThread([st]() {
        if (st->browser) st->browser->reload(st->browser.get());
    });
}

ATHENA_CEF_API int32_t athena_cef_set_muted(int32_t tab_id, int32_t muted) {
    auto st = FindTab(tab_id);
    if (!st) return 0;
    RunOnUiThread([st, muted]() {
        if (!st->browser) return;
        auto host = CefHandle<cef_browser_host_t>::Adopt(st->browser->get_host(st->browser.get()));
        if (!host) return;
        host->set_audio_muted(host.get(), muted ? 1 : 0);
        int32_t actual = host->is_audio_muted(host.get()) ? 1 : 0;
        if (g.cb.on_muted_changed) {
            g.cb.on_muted_changed(g.cb.user_data, st->tab_id, actual);
        }
    });
    return 1;
}

ATHENA_CEF_API int32_t athena_cef_send_devtools(int32_t tab_id, int32_t message_id,
                                                const char* json_utf8, int32_t json_len) {
    auto st = FindTab(tab_id);
    if (!st || !json_utf8 || json_len <= 0) return 0;
    std::string payload(json_utf8, static_cast<size_t>(json_len));
    RunOnUiThread([st, message_id, payload = std::move(payload)]() {
        int sent = 0;
        if (st->browser) {
            auto host = CefHandle<cef_browser_host_t>::Adopt(st->browser->get_host(st->browser.get()));
            if (host) {
                sent = host->send_dev_tools_message(host.get(), payload.data(), payload.size());
            }
        }
        if (!sent && g.cb.on_devtools_result) {
            static const char kDetached[] = "{\"message\":\"Not attached to an active page.\"}";
            g.cb.on_devtools_result(g.cb.user_data, st->tab_id, message_id, 0,
                                    kDetached, static_cast<int32_t>(sizeof(kDetached) - 1));
        }
    });
    return 1;
}

ATHENA_CEF_API void athena_cef_reply_interaction(int32_t interaction_id, int32_t accept,
                                                 const char* text1, const char* text2) {
    PendingInteraction item;
    {
        std::lock_guard<std::mutex> lock(g.mu);
        auto it = g.interactions.find(interaction_id);
        if (it == g.interactions.end()) return;
        item = std::move(it->second);
        g.interactions.erase(it);
    }
    std::string t1 = text1 ? text1 : "";
    std::string t2 = text2 ? text2 : "";
    RunOnUiThread([item = std::move(item), accept, t1 = std::move(t1), t2 = std::move(t2)]() mutable {
        switch (item.kind) {
            case InteractionKind::File: {
                if (!item.file_cb) break;
                if (accept && !t1.empty()) {
                    cef_string_list_t list = cef_string_list_alloc();
                    size_t start = 0;
                    while (start <= t1.size()) {
                        auto sep = t1.find('\x1f', start);
                        std::string part = sep == std::string::npos ? t1.substr(start) : t1.substr(start, sep - start);
                        if (!part.empty()) {
                            CefStr cs(part);
                            cef_string_list_append(list, &cs.s);
                        }
                        if (sep == std::string::npos) break;
                        start = sep + 1;
                    }
                    item.file_cb->cont(item.file_cb.get(), list);
                    cef_string_list_free(list);
                } else {
                    item.file_cb->cancel(item.file_cb.get());
                }
                break;
            }
            case InteractionKind::Download: {
                if (item.download_cb && accept && !t1.empty()) {
                    CefStr path(t1);
                    item.download_cb->cont(item.download_cb.get(), &path.s, 0);
                }
                break;
            }
            case InteractionKind::JsDialog: {
                if (!item.jsdialog_cb) break;
                if (accept) {
                    CefStr input(t1);
                    item.jsdialog_cb->cont(item.jsdialog_cb.get(), 1, &input.s);
                } else {
                    item.jsdialog_cb->cont(item.jsdialog_cb.get(), 0, nullptr);
                }
                break;
            }
            case InteractionKind::Auth: {
                if (!item.auth_cb) break;
                if (accept) {
                    CefStr u(t1), p(t2);
                    item.auth_cb->cont(item.auth_cb.get(), &u.s, &p.s);
                } else {
                    item.auth_cb->cancel(item.auth_cb.get());
                }
                break;
            }
            case InteractionKind::PermissionPrompt: {
                if (!item.perm_cb) break;
                item.perm_cb->cont(item.perm_cb.get(),
                                   accept ? CEF_PERMISSION_RESULT_ACCEPT : CEF_PERMISSION_RESULT_DENY);
                break;
            }
            case InteractionKind::MediaPermission: {
                if (!item.media_cb) break;
                if (accept) item.media_cb->cont(item.media_cb.get(), item.requested_media);
                else item.media_cb->cancel(item.media_cb.get());
                break;
            }
        }
    });
}

ATHENA_CEF_API void athena_cef_clear_site_data(int32_t request_id) {
    RunOnUiThread([request_id]() {
        auto ctx = CefHandle<cef_request_context_t>::Adopt(cef_request_context_get_global_context());
        if (!ctx) {
            if (g.cb.on_site_data_cleared) g.cb.on_site_data_cleared(g.cb.user_data, request_id, 0);
            return;
        }
        auto cm = CefHandle<cef_cookie_manager_t>::Adopt(ctx->get_cookie_manager(ctx.get(), nullptr));
        if (!cm) {
            if (g.cb.on_site_data_cleared) g.cb.on_site_data_cleared(g.cb.user_data, request_id, 1);
            return;
        }
        auto* cb = new DeleteCookiesCallbackBox(request_id);
        int started = cm->delete_cookies(cm.get(), nullptr, nullptr, &cb->cef);
        if (!started && g.cb.on_site_data_cleared) {
            g.cb.on_site_data_cleared(g.cb.user_data, request_id, 0);
        }
    });
}

ATHENA_CEF_API int64_t athena_cef_create_host_view(int64_t parent_handle) {
    return athena_cef::PlatformCreateHostView(parent_handle);
}

ATHENA_CEF_API void athena_cef_destroy_host_view(int64_t view_handle) {
    athena_cef::PlatformDestroyHostView(view_handle);
}

}  // extern "C"
