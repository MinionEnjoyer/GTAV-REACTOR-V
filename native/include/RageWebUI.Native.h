#pragma once

#include <cstdint>

#ifdef _WIN32
#define RWUI_CALL __cdecl
#ifdef RWUI_NATIVE_EXPORTS
#define RWUI_API extern "C" __declspec(dllexport)
#else
#define RWUI_API extern "C" __declspec(dllimport)
#endif
#else
#define RWUI_CALL
#define RWUI_API extern "C"
#endif

enum class RwuiRenderApi : std::int32_t {
    None = 0,
    Direct3D11 = 11,
    Direct3D12 = 12,
};

enum class RwuiInputType : std::int32_t {
    None = 0,
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    KeyDown = 5,
    KeyUp = 6,
    Character = 7,
    Resize = 8,
};

struct RwuiInputEvent {
    RwuiInputType type;
    std::int32_t x;
    std::int32_t y;
    std::int32_t delta;
    std::int32_t key;
    std::uint32_t modifiers;
    std::uint64_t timestamp;
};

struct RwuiRenderStats {
    RwuiRenderApi api;
    std::int32_t width;
    std::int32_t height;
    std::uint64_t submittedFrames;
    std::uint64_t renderedFrames;
    std::uint64_t droppedFrames;
    std::uint64_t lastFrameGeneration;
};

inline constexpr std::uint16_t RWUI_SHARED_TEXTURE_CAPABILITY_MAJOR = 1;
inline constexpr std::uint16_t RWUI_SHARED_TEXTURE_CAPABILITY_MINOR = 0;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_FORMAT_BGRA8_UNORM =
    1u << 0;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_FORMAT_BGRA8_UNORM_SRGB =
    1u << 1;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_SYNCHRONOUS_TRANSIENT_COPY =
    1u << 0;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_CROSS_PROCESS_POOL =
    1u << 1;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_D3D11_KEYED_MUTEX =
    1u << 2;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_D3D12_SHARED_FENCE =
    1u << 3;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_BOOTSTRAP_PROBE =
    1u << 4;

struct RwuiSharedTextureCapabilities {
    std::uint32_t byteSize;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    std::uint32_t maximumWidth;
    std::uint32_t maximumHeight;
    std::uint32_t supportedFormatMask;
    std::uint32_t flags;
};

static_assert(sizeof(RwuiSharedTextureCapabilities) == 24);

enum class RwuiSharedTextureSubmitStatus : std::uint32_t {
    UnknownFailure = 0,
    Submitted = 1,
    Backpressure = 2,
    SessionInvalid = 3,
    AdapterOrResourceInvalid = 4,
    DeviceOrCopyFailure = 5,
    ProducerStopped = 6,
    InvalidFrame = 7,
};

inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_PRODUCER_BOUND = 1u << 0;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_PRODUCER_CONNECTED = 1u << 1;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_PRODUCER_CONSUMER_VALIDATED =
    1u << 2;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_PRODUCER_ADAPTER_READY =
    1u << 3;
inline constexpr std::uint32_t RWUI_SHARED_TEXTURE_PRODUCER_ACCELERATED_READY =
    1u << 4;

// Per-producer-session diagnostics. Counters are monotonic until Stop/Start;
// they contain no handles, adapter addresses, or paths. The adapter LUID and
// display name are runtime diagnostics only and must not be persisted into a
// distributable manifest. Older callers remain compatible through the
// existing Boolean probe/submit exports.
struct RwuiSharedTextureProducerDiagnostics {
    std::uint32_t byteSize;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    RwuiSharedTextureSubmitStatus lastStatus;
    std::uint32_t flags;
    std::uint64_t probeAttempts;
    std::uint64_t submitAttempts;
    std::uint64_t submitted;
    std::uint64_t backpressure;
    std::uint64_t sessionInvalid;
    std::uint64_t adapterOrResourceInvalid;
    std::uint64_t deviceOrCopyFailure;
    std::uint64_t producerStopped;
    std::uint64_t invalidFrame;
    std::uint64_t unknownFailure;
    std::uint64_t acknowledgementsAccepted;
    std::uint64_t acknowledgementsRejected;
    std::uint64_t acknowledgementFailures;
    std::uint64_t lastAttemptedGeneration;
    std::uint64_t lastSubmittedGeneration;
    std::uint64_t lastAcknowledgedGeneration;
    std::int32_t adapterLuidHigh;
    std::uint32_t adapterLuidLow;
    std::uint32_t adapterVendorId;
    std::uint32_t adapterDeviceId;
    wchar_t adapterDescription[128];
};
static_assert(sizeof(RwuiSharedTextureProducerDiagnostics) == 416);

struct RwuiSharedTextureConsumerDiagnostics {
    std::uint32_t byteSize;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    std::uint32_t stage;
    std::uint32_t lastReceiveError;
    std::uint32_t lastImportError;
    // ABI 1.1 uses the formerly reserved DWORD without changing layout/size.
    std::uint32_t lastImportHresult;
    std::uint64_t discoveryMisses;
    std::uint64_t producerImageRejects;
    std::uint64_t connectFailures;
    std::uint64_t receivedFrames;
    std::uint64_t receiveFailures;
    std::uint64_t importedResources;
    std::uint64_t publishedFrames;
    std::uint64_t copyFailures;
    std::uint64_t acknowledgementsAccepted;
    std::uint64_t acknowledgementsRejected;
    std::uint64_t acknowledgementFailures;
    std::uint64_t lastReceivedGeneration;
    std::uint64_t lastPublishedGeneration;
};
static_assert(sizeof(RwuiSharedTextureConsumerDiagnostics) == 128);

enum class RwuiEnhancedTargetBindStatus : std::int32_t {
    Invalid = -1,
    PendingCapture = 0,
    Bound = 1,
};

enum class RwuiEnhancedTargetWindowClass : std::uint32_t {
    Unknown = 0,
    SgaWindow = 1,
    GrcWindow = 2,
};

inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_HOOKS_ARMED = 1u << 0;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_TARGET_BOUND = 1u << 1;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY = 1u << 2;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE = 1u << 3;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_PRODUCER_CONNECTED =
    1u << 4;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_EXTERNAL_VISIBLE =
    1u << 5;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_LOCAL_OWNER = 1u << 6;
inline constexpr std::uint32_t RWUI_ENHANCED_DIAGNOSTIC_INPUT_ATTACHED =
    1u << 7;

struct RwuiEnhancedHookDiagnostics {
    std::uint32_t byteSize;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    std::uint32_t flags;
    std::uint32_t processId;
    std::uint32_t targetWindowProcessId;
    RwuiEnhancedTargetWindowClass targetWindowClass;
    std::uint32_t queueBindingSource;
    std::uint32_t consumerStage;
    std::uint32_t reserved32;
    std::uint64_t presentationEpoch;
    std::uint64_t renderedFrames;
    std::uint64_t lastFrameGeneration;
    std::uint64_t reserved[2];
};
static_assert(sizeof(RwuiEnhancedHookDiagnostics) == 80);

// Legacy uses the same DXGI interception layer, but its game swap chain is
// Direct3D 11 and therefore has no D3D12 command-queue binding.  Keep a
// separate ABI so an injected loader cannot accidentally treat a queue-less
// Legacy capture as a partially initialized Enhanced capture.
enum class RwuiLegacyTargetBindStatus : std::int32_t {
    Invalid = -1,
    PendingCapture = 0,
    Bound = 1,
};

enum class RwuiLegacyTargetWindowClass : std::uint32_t {
    Unknown = 0,
    GrcWindow = 1,
};

inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_HOOKS_ARMED = 1u << 0;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND = 1u << 1;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_D3D11_READY = 1u << 2;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_PRODUCER_CONNECTED =
    1u << 3;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_EXTERNAL_VISIBLE =
    1u << 4;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_LOCAL_OWNER = 1u << 5;
inline constexpr std::uint32_t RWUI_LEGACY_DIAGNOSTIC_INPUT_ATTACHED = 1u << 6;

struct RwuiLegacyHookDiagnostics {
    std::uint32_t byteSize;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    std::uint32_t flags;
    std::uint32_t processId;
    std::uint32_t targetWindowProcessId;
    RwuiLegacyTargetWindowClass targetWindowClass;
    RwuiRenderApi renderApi;
    std::uint32_t consumerStage;
    std::uint32_t reserved32;
    std::uint64_t presentationEpoch;
    std::uint64_t renderedFrames;
    std::uint64_t lastFrameGeneration;
    std::uint64_t reserved[2];
};
static_assert(sizeof(RwuiLegacyHookDiagnostics) == 80);

// Additive ABI: old consumers keep their existing 80/128-byte layouts. HRESULT
// E_PENDING means not attempted, never success. Fullscreen is DXGI's reported
// state (Windows FSO may still optimize the physical presentation path).
struct RwuiD3D11DeviceDiagnostics {
    std::uint32_t byteSize, majorVersion, probeComplete, featureLevel;
    std::uint32_t creationFlags, adapterHigh, adapterLow, vendorId, deviceId;
    std::uint32_t bgraSupport, rgbaSupport, device1Hresult, peerDeviceHresult;
    std::uint32_t peerFeatureLevel, localBgraHresult, sharedBgraHresult;
    std::uint32_t sharedRgbaHresult, sharedBgraRenderTargetHresult;
    std::uint32_t fullscreenHresult, fullscreen, swapEffect, swapFlags;
    std::uint32_t backBufferFormat, width, height, sampleCount;
};
static_assert(sizeof(RwuiD3D11DeviceDiagnostics) == 104);
RWUI_API std::int32_t RWUI_CALL RWUI_GetD3D11DeviceDiagnostics(
    RwuiD3D11DeviceDiagnostics* diagnostics);
// Call before arming. Extra GPU allocation probes are opt-in live-test work,
// never a requirement for normal browser rendering or production startup.
RWUI_API void RWUI_CALL RWUI_EnableD3D11DiagnosticProbes(std::int32_t enabled);
RWUI_API void RWUI_CALL RWUI_ConfigureLegacyTextureProbe(const wchar_t* helper, const wchar_t* log);

struct RwuiD3D11CompatibilityDiagnostics {
    std::uint32_t byteSize, majorVersion, enabled, active;
    std::uint32_t stage, hresult, directImportHresult, reserved;
    std::uint64_t bridgedFrames;
};
static_assert(sizeof(RwuiD3D11CompatibilityDiagnostics) == 40);
RWUI_API std::int32_t RWUI_CALL RWUI_GetD3D11CompatibilityDiagnostics(
    RwuiD3D11CompatibilityDiagnostics* diagnostics);

// Installs the DXGI/D3D12 interception layer without selecting a window or
// attaching input. Call this early in Enhanced startup; RWUI_Initialize later
// binds the actual GTA HWND and remains safe to call without a prior arm.
RWUI_API std::int32_t RWUI_CALL RWUI_ArmEnhancedHook();
// Strict compositor-only Enhanced binding. It never subclasses the GTA HWND
// and succeeds only after the exact window has a captured D3D12 DIRECT queue.
RWUI_API std::int32_t RWUI_CALL RWUI_BindEnhancedTarget(void* targetWindow);
RWUI_API std::int32_t RWUI_CALL RWUI_GetEnhancedHookDiagnostics(
    RwuiEnhancedHookDiagnostics* diagnostics);
// Compositor-only Legacy route. These exports never subclass the GTA HWND or
// attach Reactor input; the external producer remains the sole UI/input owner.
RWUI_API std::int32_t RWUI_CALL RWUI_ArmLegacyHook();
RWUI_API std::int32_t RWUI_CALL RWUI_BindLegacyTarget(void* targetWindow);
RWUI_API std::int32_t RWUI_CALL RWUI_GetLegacyHookDiagnostics(
    RwuiLegacyHookDiagnostics* diagnostics);
RWUI_API std::int32_t RWUI_CALL RWUI_Initialize(void* targetWindow);
RWUI_API void RWUI_CALL RWUI_Shutdown();
RWUI_API void RWUI_CALL RWUI_SetVisible(std::int32_t visible);
// Passive 560x68 premultiplied BGRA startup text. Null hides it; no input lease.
RWUI_API std::int32_t RWUI_CALL RWUI_SubmitStartupStatusFrame(
    const void* pixels, std::int32_t width, std::int32_t height, std::int32_t stride, std::uint64_t generation);
RWUI_API std::int32_t RWUI_CALL RWUI_SubmitFrame(
    const void* bgraPixels,
    std::int32_t width,
    std::int32_t height,
    std::int32_t stride,
    std::uint64_t generation);
// Capability negotiation for accelerated browser paint. The first data-plane
// ABI is synchronous because the CEF shared handle is valid only for the
// duration of OnAcceleratedPaint. Callers must fall back to CPU OnPaint when
// this export is absent, returns zero, or omits
// RWUI_SHARED_TEXTURE_SYNCHRONOUS_TRANSIENT_COPY.
RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureCapabilities(
    RwuiSharedTextureCapabilities* capabilities);
RWUI_API std::int32_t RWUI_CALL RWUI_StartSharedTextureProducer(
    std::uint32_t targetGtaProcessId);
RWUI_API void RWUI_CALL RWUI_StopSharedTextureProducer();
// Publishes presentation ownership state over the authenticated producer /
// GTA consumer control channel. This never calls managed code inside GTA.
RWUI_API std::int32_t RWUI_CALL RWUI_SetSharedTextureProducerVisible(
    std::int32_t visible);
RWUI_API std::int32_t RWUI_CALL RWUI_ProbeSharedTexture(
    void* sharedTextureHandle,
    std::int32_t width,
    std::int32_t height,
    std::uint32_t dxgiFormat,
    std::uint64_t generation);
// Typed companion to RWUI_ProbeSharedTexture. This preserves the legacy
// Boolean ABI while making bootstrap rejection actionable in runtime traces.
RWUI_API std::uint32_t RWUI_CALL RWUI_ProbeSharedTextureStatus(
    void* sharedTextureHandle,
    std::int32_t width,
    std::int32_t height,
    std::uint32_t dxgiFormat,
    std::uint64_t generation);
RWUI_API std::int32_t RWUI_CALL RWUI_SubmitSharedTexture(
    void* sharedTextureHandle,
    std::int32_t width,
    std::int32_t height,
    std::uint32_t dxgiFormat,
    std::uint64_t generation);
// Typed companion to RWUI_SubmitSharedTexture. Unlike the compatibility bool
// export, this distinguishes a normal latest-frame drop from a transport or
// device failure that requires managed fallback/re-probe.
RWUI_API std::uint32_t RWUI_CALL RWUI_SubmitSharedTextureStatus(
    void* sharedTextureHandle,
    std::int32_t width,
    std::int32_t height,
    std::uint32_t dxgiFormat,
    std::uint64_t generation);
RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureProducerDiagnostics(
    RwuiSharedTextureProducerDiagnostics* diagnostics);
// Consumer diagnostics are meaningful in the injected GTA instance. The
// export is still fail-open when called from a producer-only process.
RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureConsumerDiagnostics(
    RwuiSharedTextureConsumerDiagnostics* diagnostics);
RWUI_API std::int32_t RWUI_CALL RWUI_PollInput(RwuiInputEvent* inputEvent);
RWUI_API std::int32_t RWUI_CALL RWUI_GetStats(RwuiRenderStats* stats);

// Standalone harness entry points. These create a real swap chain and use the
// same compositor classes as the injected hook, without loading GTA.
RWUI_API std::int32_t RWUI_CALL RWUI_TestStart(
    RwuiRenderApi api,
    std::int32_t width,
    std::int32_t height,
    const wchar_t* title);
RWUI_API void RWUI_CALL RWUI_TestStop();
RWUI_API std::int32_t RWUI_CALL RWUI_TestIsRunning();
