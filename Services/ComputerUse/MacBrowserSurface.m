#include <CoreFoundation/CoreFoundation.h>
#include <IOSurface/IOSurface.h>
#include <bsm/libbsm.h>
#include <mach/mach.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// Apple exports these launchd rendezvous functions from libSystem.
extern kern_return_t bootstrap_check_in(mach_port_t, const char *, mach_port_t *);
extern kern_return_t bootstrap_look_up(mach_port_t, const char *, mach_port_t *);

enum { FrameMessage = 0x41544846 };

typedef struct {
    mach_msg_header_t header;
    mach_msg_body_t body;
    mach_msg_port_descriptor_t surface;
    uint32_t sequence;
} SurfaceMessage;

static mach_port_t bootstrap(void)
{
    mach_port_t port = MACH_PORT_NULL;
    task_get_special_port(mach_task_self(), TASK_BOOTSTRAP_PORT, &port);
    return port;
}

#ifdef ATHENA_ELECTRON
#include <node_api.h>

static mach_port_t receiver;

static napi_value connect_receiver(napi_env env, napi_callback_info info)
{
    napi_value argument, result;
    size_t argc = 1;
    char service[96];
    size_t length;
    if (!MACH_PORT_VALID(receiver) && napi_get_cb_info(env, info, &argc, &argument, NULL, NULL) == napi_ok
        && argc == 1 && napi_get_value_string_utf8(env, argument, service, sizeof(service), &length) == napi_ok
        && length < sizeof(service) - 1) {
        const mach_port_t port = bootstrap();
        if (MACH_PORT_VALID(port)) {
            bootstrap_look_up(port, service, &receiver);
            mach_port_deallocate(mach_task_self(), port);
        }
    }
    napi_get_boolean(env, MACH_PORT_VALID(receiver), &result);
    return result;
}

static napi_value send_surface(napi_env env, napi_callback_info info)
{
    napi_value args[2], result;
    size_t argc = 2, length;
    void *bytes;
    uint32_t sequence;
    bool sent = false;
    if (MACH_PORT_VALID(receiver) && napi_get_cb_info(env, info, &argc, args, NULL, NULL) == napi_ok
        && argc == 2 && napi_get_buffer_info(env, args[0], &bytes, &length) == napi_ok
        && length == sizeof(IOSurfaceRef) && napi_get_value_uint32(env, args[1], &sequence) == napi_ok) {
        IOSurfaceRef surface;
        memcpy(&surface, bytes, sizeof(surface));
        const mach_port_t right = surface ? IOSurfaceCreateMachPort(surface) : MACH_PORT_NULL;
        if (MACH_PORT_VALID(right)) {
            SurfaceMessage message = {
                .header = {
                    .msgh_bits = MACH_MSGH_BITS(MACH_MSG_TYPE_COPY_SEND, 0) | MACH_MSGH_BITS_COMPLEX,
                    .msgh_size = sizeof(message), .msgh_remote_port = receiver, .msgh_id = FrameMessage,
                },
                .body = { .msgh_descriptor_count = 1 },
                .surface = { .name = right, .disposition = MACH_MSG_TYPE_COPY_SEND, .type = MACH_MSG_PORT_DESCRIPTOR },
                .sequence = sequence,
            };
            sent = mach_msg(&message.header, MACH_SEND_MSG | MACH_SEND_TIMEOUT, sizeof(message), 0,
                MACH_PORT_NULL, 0, MACH_PORT_NULL) == MACH_MSG_SUCCESS;
            mach_port_deallocate(mach_task_self(), right);
        }
    }
    napi_get_boolean(env, sent, &result);
    return result;
}

static napi_value init(napi_env env, napi_value exports)
{
    const napi_property_descriptor methods[] = {
        { .utf8name = "connect", .method = connect_receiver },
        { .utf8name = "send", .method = send_surface },
    };
    napi_define_properties(env, exports, sizeof(methods) / sizeof(methods[0]), methods);
    return exports;
}

NAPI_MODULE(NODE_GYP_MODULE_NAME, init)

#else

#import <Metal/Metal.h>

uint32_t athena_browser_surface_open(const char *service)
{
    const mach_port_t port = bootstrap();
    if (!MACH_PORT_VALID(port)) return MACH_PORT_NULL;
    mach_port_t receive = MACH_PORT_NULL;
    const kern_return_t status = bootstrap_check_in(port, service, &receive);
    mach_port_deallocate(mach_task_self(), port);
    return status == KERN_SUCCESS ? receive : MACH_PORT_NULL;
}

void athena_browser_surface_close(uint32_t receive)
{
    mach_port_mod_refs(mach_task_self(), receive, MACH_PORT_RIGHT_RECEIVE, -1);
}

IOSurfaceRef athena_browser_surface_read(uint32_t receive, int pid, uint32_t sequence, int width, int height)
{
    struct {
        SurfaceMessage frame;
        mach_msg_audit_trailer_t trailer;
    } incoming = {0};
    const mach_msg_option_t options = MACH_RCV_MSG | MACH_RCV_TIMEOUT
        | MACH_RCV_TRAILER_TYPE(MACH_MSG_TRAILER_FORMAT_0) | MACH_RCV_TRAILER_ELEMENTS(MACH_RCV_TRAILER_AUDIT);
    // The host writes the pipe metadata only after the corresponding right is queued.
    for (int attempt = 0; attempt < 5; attempt++) {
        const mach_msg_return_t status = mach_msg(&incoming.frame.header, options, 0, sizeof(incoming),
            receive, 0, MACH_PORT_NULL);
        if (status != MACH_MSG_SUCCESS) return NULL;
        const SurfaceMessage *frame = &incoming.frame;
        if (frame->header.msgh_id == FrameMessage && frame->header.msgh_size == sizeof(*frame)
            && frame->header.msgh_bits & MACH_MSGH_BITS_COMPLEX && frame->body.msgh_descriptor_count == 1
            && frame->surface.type == MACH_MSG_PORT_DESCRIPTOR && frame->surface.disposition == MACH_MSG_TYPE_PORT_SEND
            && incoming.trailer.msgh_trailer_size >= sizeof(incoming.trailer)
            && audit_token_to_pid(incoming.trailer.msgh_audit) == pid && frame->sequence == sequence) {
            IOSurfaceRef surface = IOSurfaceLookupFromMachPort(frame->surface.name);
            mach_msg_destroy(&incoming.frame.header);
            if (surface && IOSurfaceGetWidth(surface) == (size_t)width && IOSurfaceGetHeight(surface) == (size_t)height)
                return surface;
            if (surface) CFRelease(surface);
            return NULL;
        }
        mach_msg_destroy(&incoming.frame.header);
    }
    return NULL;
}

// Give Avalonia an immutable Metal texture and let Chromium reuse the original after the blit.
void *athena_browser_surface_snapshot(IOSurfaceRef source, int *timed_out)
{
    *timed_out = 0;
    @autoreleasepool {
        static id<MTLCommandQueue> queue;
        static dispatch_once_t once;
        dispatch_once(&once, ^{ queue = [MTLCreateSystemDefaultDevice() newCommandQueue]; });
        if (!queue) return NULL;
        const size_t width = IOSurfaceGetWidth(source), height = IOSurfaceGetHeight(source);
        MTLTextureDescriptor *desc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
            width:width height:height mipmapped:NO];
        desc.storageMode = MTLStorageModeShared;
        desc.usage = MTLTextureUsageShaderRead;
        id<MTLTexture> from = [queue.device newTextureWithDescriptor:desc iosurface:source plane:0];
        desc.storageMode = MTLStorageModePrivate;
        id<MTLTexture> to = [queue.device newTextureWithDescriptor:desc];
        id<MTLCommandBuffer> command = [queue commandBuffer];
        id<MTLBlitCommandEncoder> blit = [command blitCommandEncoder];
        if (!from || !to || !blit) return NULL;
        [blit copyFromTexture:from sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
            sourceSize:MTLSizeMake(width, height, 1) toTexture:to destinationSlice:0 destinationLevel:0
            destinationOrigin:MTLOriginMake(0, 0, 0)];
        [blit endEncoding];
        dispatch_semaphore_t done = dispatch_semaphore_create(0);
        [command addCompletedHandler:^(id<MTLCommandBuffer> completed) { (void)completed; dispatch_semaphore_signal(done); }];
        [command commit];
        *timed_out = dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) != 0;
        return !*timed_out && command.status == MTLCommandBufferStatusCompleted ? (__bridge_retained void *)to : NULL;
    }
}

#endif
