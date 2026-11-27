#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

enum { MaxPlanes = 4 };

typedef struct {
    uint32_t sequence, count;
    uint64_t modifier;
    int32_t stride[MaxPlanes], offset[MaxPlanes];
} FrameInfo;

// An abstract Unix socket has no filesystem entry and disappears on close.
static socklen_t address(const char *name, struct sockaddr_un *addr)
{
    size_t len = strlen(name);
    if (!len || len >= sizeof(addr->sun_path)) return 0;
    addr->sun_family = AF_UNIX;
    memcpy(addr->sun_path + 1, name, len);
    return (socklen_t)(offsetof(struct sockaddr_un, sun_path) + 1 + len);
}

#ifdef ATHENA_ELECTRON
#include <node_api.h>

static int sender = -1;

static napi_value boolean(napi_env env, bool value)
{
    napi_value result;
    napi_get_boolean(env, value, &result);
    return result;
}

static napi_value connect_receiver(napi_env env, napi_callback_info info)
{
    napi_value arg;
    size_t argc = 1, length;
    struct sockaddr_un addr = {0};
    char name[sizeof(addr.sun_path)];
    if (sender >= 0 || napi_get_cb_info(env, info, &argc, &arg, NULL, NULL) != napi_ok || argc != 1
        || napi_get_value_string_utf8(env, arg, name, sizeof(name), &length) != napi_ok || length >= sizeof(name) - 1)
        return boolean(env, false);
    socklen_t size = address(name, &addr);
    int fd = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (!size || fd < 0 || connect(fd, (struct sockaddr *)&addr, size) != 0) {
        if (fd >= 0) close(fd);
        return boolean(env, false);
    }
    sender = fd;
    return boolean(env, true);
}

static bool field(napi_env env, napi_value obj, const char *name, int32_t *out)
{
    napi_value value;
    double number;
    if (napi_get_named_property(env, obj, name, &value) != napi_ok || napi_get_value_double(env, value, &number) != napi_ok
        || !(number >= 0 && number <= INT_MAX) || number != (int32_t)number) return false;
    *out = (int32_t)number;
    return true;
}

static napi_value send_surface(napi_env env, napi_callback_info info)
{
    napi_value args[2], planes, modifier;
    size_t argc = 2, length;
    FrameInfo frame = {0};
    char value[32], *end;
    if (sender < 0 || napi_get_cb_info(env, info, &argc, args, NULL, NULL) != napi_ok || argc != 2
        || napi_get_value_uint32(env, args[1], &frame.sequence) != napi_ok
        || napi_get_named_property(env, args[0], "planes", &planes) != napi_ok
        || napi_get_array_length(env, planes, &frame.count) != napi_ok || !frame.count || frame.count > MaxPlanes
        || napi_get_named_property(env, args[0], "modifier", &modifier) != napi_ok
        || napi_get_value_string_utf8(env, modifier, value, sizeof(value), &length) != napi_ok
        || !length || length >= sizeof(value) - 1 || value[0] < '0' || value[0] > '9') return boolean(env, false);
    errno = 0;
    frame.modifier = strtoull(value, &end, 10);
    if (errno || *end) return boolean(env, false);
    int fds[MaxPlanes];
    for (unsigned i = 0; i < frame.count; i++) {
        napi_value plane;
        if (napi_get_element(env, planes, i, &plane) != napi_ok || !field(env, plane, "fd", &fds[i])
            || !field(env, plane, "stride", &frame.stride[i]) || !frame.stride[i]
            || !field(env, plane, "offset", &frame.offset[i])) return boolean(env, false);
    }
    union { struct cmsghdr align; char data[CMSG_SPACE(sizeof(fds))]; } control = {0};
    struct iovec iov = { &frame, sizeof(frame) };
    size_t bytes = frame.count * sizeof(int);
    struct msghdr msg = { .msg_iov = &iov, .msg_iovlen = 1, .msg_control = control.data, .msg_controllen = CMSG_SPACE(bytes) };
    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    cmsg->cmsg_level = SOL_SOCKET;
    cmsg->cmsg_type = SCM_RIGHTS;
    cmsg->cmsg_len = CMSG_LEN(bytes);
    memcpy(CMSG_DATA(cmsg), fds, bytes);
    return boolean(env, sendmsg(sender, &msg, MSG_DONTWAIT | MSG_NOSIGNAL) == sizeof(frame));
}

static napi_value init(napi_env env, napi_value exports)
{
    const napi_property_descriptor methods[] = {
        { .utf8name = "connect", .method = connect_receiver },
        { .utf8name = "send", .method = send_surface },
    };
    napi_define_properties(env, exports, 2, methods);
    return exports;
}
NAPI_MODULE(NODE_GYP_MODULE_NAME, init)

#else
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#include <drm_fourcc.h>

typedef struct {
    FrameInfo info;
    int fd[MaxPlanes];
    int width, height;
} Surface;

uint32_t athena_browser_surface_open(const char *name)
{
    struct sockaddr_un addr = {0};
    socklen_t size = address(name, &addr);
    if (!size) return 0;
    int fd = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (fd < 0) return 0;
    int enabled = 1;
    if (setsockopt(fd, SOL_SOCKET, SO_PASSCRED, &enabled, sizeof(enabled)) || bind(fd, (struct sockaddr *)&addr, size)) {
        close(fd);
        return 0;
    }
    return (uint32_t)fd + 1;
}

void athena_browser_surface_close(uint32_t channel) { close((int)(channel - 1)); }

void athena_browser_surface_release(Surface *surface)
{
    for (unsigned i = 0; i < surface->info.count; i++) close(surface->fd[i]);
    free(surface);
}

Surface *athena_browser_surface_read(uint32_t channel, int pid, uint32_t sequence, int width, int height)
{
    // The producer queues the rights before announcing their sequence on the private stdout pipe.
    for (int attempt = 0; attempt < 8; attempt++) {
        Surface surface = { .width = width, .height = height };
        union { struct cmsghdr align; char data[CMSG_SPACE(sizeof(struct ucred)) + CMSG_SPACE(sizeof(surface.fd))]; } control;
        struct iovec iov = { &surface.info, sizeof(surface.info) };
        struct msghdr msg = { .msg_iov = &iov, .msg_iovlen = 1, .msg_control = control.data, .msg_controllen = sizeof(control.data) };
        ssize_t bytes = recvmsg((int)(channel - 1), &msg, MSG_CMSG_CLOEXEC | MSG_DONTWAIT);
        if (bytes < 0) return NULL;
        bool authenticated = false;
        unsigned count = 0;
        for (struct cmsghdr *c = CMSG_FIRSTHDR(&msg); c; c = CMSG_NXTHDR(&msg, c)) {
            if (c->cmsg_level != SOL_SOCKET) continue;
            if (c->cmsg_type == SCM_CREDENTIALS && c->cmsg_len == CMSG_LEN(sizeof(struct ucred))) {
                struct ucred cred;
                memcpy(&cred, CMSG_DATA(c), sizeof(cred));
                authenticated = cred.pid == pid && cred.uid == geteuid();
            }
            if (c->cmsg_type == SCM_RIGHTS) {
                size_t n = (c->cmsg_len - CMSG_LEN(0)) / sizeof(int);
                const int *fds = (const int *)CMSG_DATA(c);
                for (size_t i = 0; i < n; i++) {
                    if (count < MaxPlanes) surface.fd[count++] = fds[i];
                    else close(fds[i]);
                }
            }
        }
        if (authenticated && bytes == sizeof(FrameInfo) && !(msg.msg_flags & (MSG_TRUNC | MSG_CTRUNC))
            && surface.info.sequence == sequence && count && count == surface.info.count && width > 0 && height > 0) {
            Surface *owned = malloc(sizeof(surface));
            if (owned) { *owned = surface; return owned; }
        }
        for (unsigned i = 0; i < count; i++) close(surface.fd[i]);
    }
    return NULL;
}

static bool extension(const char *list, const char *name)
{
    size_t len = strlen(name);
    const char *p = list;
    while (p && (p = strstr(p, name))) {
        if ((p == list || p[-1] == ' ') && (!p[len] || p[len] == ' ')) return true;
        p += len;
    }
    return false;
}

static EGLImageKHR import_image(EGLDisplay display, Surface *surface, PFNEGLCREATEIMAGEKHRPROC create, bool modifiers)
{
    const EGLint fd_attrs[] = { EGL_DMA_BUF_PLANE0_FD_EXT, EGL_DMA_BUF_PLANE1_FD_EXT, EGL_DMA_BUF_PLANE2_FD_EXT, EGL_DMA_BUF_PLANE3_FD_EXT };
    const EGLint modifier_attrs[] = { EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT, EGL_DMA_BUF_PLANE1_MODIFIER_LO_EXT,
        EGL_DMA_BUF_PLANE2_MODIFIER_LO_EXT, EGL_DMA_BUF_PLANE3_MODIFIER_LO_EXT };
    EGLint attrs[7 + 10 * MaxPlanes] = { EGL_WIDTH, surface->width, EGL_HEIGHT, surface->height, EGL_LINUX_DRM_FOURCC_EXT, DRM_FORMAT_ARGB8888 };
    unsigned n = 6;
    const FrameInfo *info = &surface->info;
    // An explicit layout cannot safely be guessed by a driver that only supports implicit modifiers.
    if (!modifiers && (info->count == 4 || info->modifier != DRM_FORMAT_MOD_INVALID))
        return EGL_NO_IMAGE_KHR;
    for (unsigned i = 0; i < info->count; i++) {
        attrs[n++] = fd_attrs[i];     attrs[n++] = surface->fd[i];
        attrs[n++] = fd_attrs[i] + 1; attrs[n++] = info->offset[i];
        attrs[n++] = fd_attrs[i] + 2; attrs[n++] = info->stride[i];
        if (modifiers && info->modifier != DRM_FORMAT_MOD_INVALID) {
            attrs[n++] = modifier_attrs[i];     attrs[n++] = (EGLint)info->modifier;
            attrs[n++] = modifier_attrs[i] + 1; attrs[n++] = (EGLint)(info->modifier >> 32);
        }
    }
    attrs[n] = EGL_NONE;
    return create(display, EGL_NO_CONTEXT, EGL_LINUX_DMA_BUF_EXT, NULL, attrs);
}

// Called only with Avalonia's Skia EGL context current. Return a private RGBA8 texture for Skia to adopt.
// Restore both changed bindings before returning control to Skia's state cache.
GLuint athena_browser_surface_snapshot_gl(Surface *surface, int *keep_lease)
{
    *keep_lease = 0;
    EGLDisplay display = eglGetCurrentDisplay();
    if (display == EGL_NO_DISPLAY || eglGetCurrentContext() == EGL_NO_CONTEXT) return 0;
    const char *ext = eglQueryString(display, EGL_EXTENSIONS);
    if (!extension(ext, "EGL_EXT_image_dma_buf_import") || !extension(ext, "EGL_KHR_fence_sync")) return 0;
    PFNEGLCREATEIMAGEKHRPROC create = (PFNEGLCREATEIMAGEKHRPROC)eglGetProcAddress("eglCreateImageKHR");
    PFNEGLDESTROYIMAGEKHRPROC destroy = (PFNEGLDESTROYIMAGEKHRPROC)eglGetProcAddress("eglDestroyImageKHR");
    PFNGLEGLIMAGETARGETTEXTURE2DOESPROC bind_image = (PFNGLEGLIMAGETARGETTEXTURE2DOESPROC)eglGetProcAddress("glEGLImageTargetTexture2DOES");
    PFNEGLCREATESYNCKHRPROC fence = (PFNEGLCREATESYNCKHRPROC)eglGetProcAddress("eglCreateSyncKHR");
    PFNEGLCLIENTWAITSYNCKHRPROC wait = (PFNEGLCLIENTWAITSYNCKHRPROC)eglGetProcAddress("eglClientWaitSyncKHR");
    PFNEGLDESTROYSYNCKHRPROC destroy_sync = (PFNEGLDESTROYSYNCKHRPROC)eglGetProcAddress("eglDestroySyncKHR");
    if (!create || !destroy || !bind_image || !fence || !wait || !destroy_sync) return 0;
    GLint major = 0;
    glGetIntegerv(GL_MAJOR_VERSION, &major);
    if (major < 3) return 0;
    EGLImageKHR image = import_image(display, surface, create, extension(ext, "EGL_EXT_image_dma_buf_import_modifiers"));
    if (image == EGL_NO_IMAGE_KHR) return 0;

    GLint old_texture, old_read;
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &old_texture);
    glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &old_read);
    GLuint from = 0, to = 0, fb = 0;
    glGenTextures(1, &from);
    glBindTexture(GL_TEXTURE_2D, from);
    bind_image(GL_TEXTURE_2D, image);
    glGenFramebuffers(1, &fb);
    glBindFramebuffer(GL_READ_FRAMEBUFFER, fb);
    glFramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, from, 0);
    if (glCheckFramebufferStatus(GL_READ_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE) {
        glGenTextures(1, &to);
        glBindTexture(GL_TEXTURE_2D, to);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glCopyTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, 0, 0, surface->width, surface->height, 0);
        GLenum error = glGetError();
        EGLSyncKHR sync = fence(display, EGL_SYNC_FENCE_KHR, NULL);
        *keep_lease = !sync || wait(display, sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, 250000000) != EGL_CONDITION_SATISFIED_KHR;
        if (sync) destroy_sync(display, sync);
        if (error != GL_NO_ERROR || *keep_lease) {
            glDeleteTextures(1, &to);
            to = 0;
        }
    }
    glBindFramebuffer(GL_READ_FRAMEBUFFER, old_read);
    glBindTexture(GL_TEXTURE_2D, old_texture);
    glDeleteFramebuffers(1, &fb);
    glDeleteTextures(1, &from);
    destroy(display, image);
    return to;
}
#endif
