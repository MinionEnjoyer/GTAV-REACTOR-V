#include "RageWebUI.Native.h"
#include "ReactorV.SharedGpuFrame.h"
#include "SharedGpuFrameRuntime.h"

RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureCapabilities(
    RwuiSharedTextureCapabilities* const capabilities) {
    if (capabilities == nullptr ||
        capabilities->byteSize != sizeof(RwuiSharedTextureCapabilities)) {
        return 0;
    }
    try {
        const auto& runtime =
            rwui::transport::GlobalSharedGpuFrameProducerRuntime();
        std::uint32_t flags{};
        if (runtime.Bound()) flags |= RWUI_SHARED_TEXTURE_BOOTSTRAP_PROBE;
        if (runtime.AcceleratedReady()) {
            flags |= RWUI_SHARED_TEXTURE_SYNCHRONOUS_TRANSIENT_COPY |
                RWUI_SHARED_TEXTURE_CROSS_PROCESS_POOL |
                RWUI_SHARED_TEXTURE_D3D11_KEYED_MUTEX;
        }
        *capabilities = RwuiSharedTextureCapabilities{
            sizeof(RwuiSharedTextureCapabilities),
            RWUI_SHARED_TEXTURE_CAPABILITY_MAJOR,
            RWUI_SHARED_TEXTURE_CAPABILITY_MINOR,
            rwui::transport::SharedGpuFrameMaximumDimension,
            rwui::transport::SharedGpuFrameMaximumDimension,
            RWUI_SHARED_TEXTURE_FORMAT_BGRA8_UNORM |
                RWUI_SHARED_TEXTURE_FORMAT_BGRA8_UNORM_SRGB,
            flags,
        };
        return 1;
    } catch (...) {
        *capabilities = {};
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_StartSharedTextureProducer(
    const std::uint32_t targetGtaProcessId) {
    try {
        return rwui::transport::GlobalSharedGpuFrameProducerRuntime().Start(
            targetGtaProcessId) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

RWUI_API void RWUI_CALL RWUI_StopSharedTextureProducer() {
    try {
        rwui::transport::GlobalSharedGpuFrameProducerRuntime().Stop();
    } catch (...) {
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_SetSharedTextureProducerVisible(
    const std::int32_t visible) {
    try {
        return rwui::transport::GlobalSharedGpuFrameProducerRuntime()
            .SetPresentationVisible(visible != 0) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

namespace {

rwui::transport::SharedGpuPixelFormat PixelFormat(
    const std::uint32_t dxgiFormat) noexcept {
    if (dxgiFormat == static_cast<std::uint32_t>(
            rwui::transport::SharedGpuPixelFormat::Bgra8Unorm)) {
        return rwui::transport::SharedGpuPixelFormat::Bgra8Unorm;
    }
    if (dxgiFormat == static_cast<std::uint32_t>(
            rwui::transport::SharedGpuPixelFormat::Bgra8UnormSrgb)) {
        return rwui::transport::SharedGpuPixelFormat::Bgra8UnormSrgb;
    }
    return rwui::transport::SharedGpuPixelFormat::Unknown;
}

RwuiSharedTextureSubmitStatus SubmitStatus(
    void* const handle,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint32_t dxgiFormat,
    const std::uint64_t generation,
    const bool bootstrapProbe) {
    const auto format = PixelFormat(dxgiFormat);
    auto& runtime =
        rwui::transport::GlobalSharedGpuFrameProducerRuntime();
    if (width <= 0 || height <= 0 ||
        format == rwui::transport::SharedGpuPixelFormat::Unknown) {
        return runtime.RecordRejectedAttempt(
            RwuiSharedTextureSubmitStatus::InvalidFrame,
            generation,
            bootstrapProbe);
    }
    return runtime.SubmitTransientStatus(
        static_cast<HANDLE>(handle),
        static_cast<std::uint32_t>(width),
        static_cast<std::uint32_t>(height),
        format, generation, bootstrapProbe);
}

} // namespace

RWUI_API std::int32_t RWUI_CALL RWUI_ProbeSharedTexture(
    void* const sharedTextureHandle,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint32_t dxgiFormat,
    const std::uint64_t generation) {
    try {
        return RWUI_ProbeSharedTextureStatus(
            sharedTextureHandle, width, height, dxgiFormat, generation) ==
            static_cast<std::uint32_t>(
                RwuiSharedTextureSubmitStatus::Submitted) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

RWUI_API std::uint32_t RWUI_CALL RWUI_ProbeSharedTextureStatus(
    void* const sharedTextureHandle,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint32_t dxgiFormat,
    const std::uint64_t generation) {
    try {
        return static_cast<std::uint32_t>(SubmitStatus(
            sharedTextureHandle, width, height, dxgiFormat, generation,
            true));
    } catch (...) {
        return static_cast<std::uint32_t>(
            RwuiSharedTextureSubmitStatus::UnknownFailure);
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureProducerDiagnostics(
    RwuiSharedTextureProducerDiagnostics* const diagnostics) {
    if (diagnostics == nullptr ||
        diagnostics->byteSize !=
            sizeof(RwuiSharedTextureProducerDiagnostics)) {
        return 0;
    }
    try {
        *diagnostics = rwui::transport::
            GlobalSharedGpuFrameProducerRuntime().Diagnostics();
        return 1;
    } catch (...) {
        *diagnostics = {};
        return 0;
    }
}

/* RWUI_GetSharedTextureConsumerDiagnostics is implemented beside the
   compositor-owned consumer in HookManager.cpp. */

RWUI_API std::int32_t RWUI_CALL RWUI_SubmitSharedTexture(
    void* const sharedTextureHandle,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint32_t dxgiFormat,
    const std::uint64_t generation) {
    try {
        return RWUI_SubmitSharedTextureStatus(
            sharedTextureHandle, width, height, dxgiFormat, generation) ==
            static_cast<std::uint32_t>(
                RwuiSharedTextureSubmitStatus::Submitted) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

RWUI_API std::uint32_t RWUI_CALL RWUI_SubmitSharedTextureStatus(
    void* const sharedTextureHandle,
    const std::int32_t width,
    const std::int32_t height,
    const std::uint32_t dxgiFormat,
    const std::uint64_t generation) {
    try {
        auto& runtime =
            rwui::transport::GlobalSharedGpuFrameProducerRuntime();
        if (!runtime.Bound()) {
            return static_cast<std::uint32_t>(runtime.RecordRejectedAttempt(
                RwuiSharedTextureSubmitStatus::ProducerStopped,
                generation,
                false));
        }
        if (!runtime.AcceleratedReady()) {
            return static_cast<std::uint32_t>(runtime.RecordRejectedAttempt(
                RwuiSharedTextureSubmitStatus::SessionInvalid,
                generation,
                false));
        }
        return static_cast<std::uint32_t>(SubmitStatus(
            sharedTextureHandle, width, height, dxgiFormat, generation,
            false));
    } catch (...) {
        return static_cast<std::uint32_t>(
            RwuiSharedTextureSubmitStatus::UnknownFailure);
    }
}
