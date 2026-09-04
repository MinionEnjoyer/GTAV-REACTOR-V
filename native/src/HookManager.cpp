#include "RuntimeState.h"
#include "DxgiHookPolicy.h"
#include "RageWebUI.Native.h"

#include <MinHook.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cwchar>
#include <mutex>
#include <vector>
#include <wrl/client.h>

namespace rwui {

FrameMailbox g_frameMailbox;
DirectXCompositor g_compositor(g_frameMailbox);
InputQueue g_inputQueue;
std::atomic_bool g_visible{false};
std::atomic_bool g_localPresentationOwner{false};

namespace {

using CreateSwapChainFunction = HRESULT(STDMETHODCALLTYPE*)(
    IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
using CreateSwapChainForHwndFunction = HRESULT(STDMETHODCALLTYPE*)(
    IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
using PresentFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Function = HRESULT(STDMETHODCALLTYPE*)(
    IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeBuffersFunction = HRESULT(STDMETHODCALLTYPE*)(
    IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using ResizeBuffers1Function = HRESULT(STDMETHODCALLTYPE*)(
    IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT,
    const UINT*, IUnknown* const*);

CreateSwapChainFunction originalCreateSwapChain{};
CreateSwapChainForHwndFunction originalCreateSwapChainForHwnd{};
PresentFunction originalPresent{};
Present1Function originalPresent1{};
ResizeBuffersFunction originalResizeBuffers{};
ResizeBuffers1Function originalResizeBuffers1{};

void* createSwapChainAddress{};
void* createSwapChainForHwndAddress{};
void* presentAddress{};
void* present1Address{};
void* resizeAddress{};
void* resize1Address{};

std::mutex lifecycleMutex;
std::atomic<HWND> targetWindow{};
std::atomic_bool strictEnhancedTarget{};
std::atomic_bool strictLegacyTarget{};
HWND inputWindow{};
bool minHookOwned{};
bool hooksArmed{};
bool hookCleanupPending{};
std::vector<void*> createdHookAddresses;
std::atomic_uint32_t activeHookCallbacks{};
std::atomic_bool teardownRequested{};
std::mutex callbackDrainMutex;
std::condition_variable callbackDrainCondition;
std::vector<std::uintptr_t> createdHookIdentities;

std::mutex queueMutex;
QueueBindingSelection queueSelection{};
std::uintptr_t boundSwapChainIdentity{};
Microsoft::WRL::ComPtr<IDXGISwapChain> boundSwapChain;
std::atomic<IDXGISwapChain*> targetSwapChainPointer{};
std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> boundPresentQueues;

struct CapturedSwapChain final {
    HWND window{};
    std::uintptr_t identity{};
    Microsoft::WRL::ComPtr<IDXGISwapChain> swapChain;
    std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> queues;
    QueueBindingSource source{QueueBindingSource::None};
};

constexpr std::size_t MaximumCapturedSwapChains = 8;
constexpr UINT MaximumPresentQueues = 16;
std::vector<CapturedSwapChain> capturedSwapChains;

thread_local unsigned presentHookDepth = 0;
thread_local unsigned resizeHookDepth = 0;

class HookCallbackScope final {
public:
    HookCallbackScope() noexcept {
        activeHookCallbacks.fetch_add(1, std::memory_order_acq_rel);
    }

    ~HookCallbackScope() noexcept {
        const auto prior = activeHookCallbacks.fetch_sub(
            1, std::memory_order_acq_rel);
        if (prior == 1 &&
            teardownRequested.load(std::memory_order_acquire)) {
            callbackDrainCondition.notify_all();
        }
    }

    HookCallbackScope(const HookCallbackScope&) = delete;
    HookCallbackScope& operator=(const HookCallbackScope&) = delete;

    bool IsTearingDown() const noexcept {
        return teardownRequested.load(std::memory_order_acquire);
    }
};

HWND CreateDummyWindow() {
    // Use a process-lifetime system class. Registering a class whose WndProc
    // lives in this DLL leaves a stale callback after a valid Shutdown +
    // FreeLibrary/reload cycle unless every exit path unregisters it first.
    // DXGI only needs a valid HWND for vtable discovery, so a built-in STATIC
    // window removes that DLL-unload hazard entirely.
    return CreateWindowExW(
        0,
        L"STATIC",
        L"",
        WS_OVERLAPPED,
        0,
        0,
        16,
        16,
        nullptr,
        nullptr,
        GetModuleHandleW(nullptr),
        nullptr);
}

bool ResolveFactoryMethods() {
    Microsoft::WRL::ComPtr<IDXGIFactory2> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    auto** virtualTable = *reinterpret_cast<void***>(factory.Get());
    createSwapChainAddress = virtualTable[10];
    createSwapChainForHwndAddress = virtualTable[15];
    return createSwapChainAddress != nullptr &&
        createSwapChainForHwndAddress != nullptr;
}

bool ResolveDxgiMethods() {
    const HWND window = CreateDummyWindow();
    if (window == nullptr) return false;

    DXGI_SWAP_CHAIN_DESC swapChainDescription{};
    swapChainDescription.BufferCount = 2;
    swapChainDescription.BufferDesc.Width = 16;
    swapChainDescription.BufferDesc.Height = 16;
    swapChainDescription.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapChainDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDescription.OutputWindow = window;
    swapChainDescription.SampleDesc.Count = 1;
    swapChainDescription.Windowed = TRUE;
    swapChainDescription.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    Microsoft::WRL::ComPtr<IDXGISwapChain> swapChain;
    D3D_FEATURE_LEVEL featureLevel{};
    const auto result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &swapChainDescription,
        &swapChain,
        &device,
        &featureLevel,
        &context);
    if (SUCCEEDED(result)) {
        auto** virtualTable = *reinterpret_cast<void***>(swapChain.Get());
        presentAddress = virtualTable[8];
        resizeAddress = virtualTable[13];

        Microsoft::WRL::ComPtr<IDXGISwapChain3> swapChain3;
        if (SUCCEEDED(swapChain.As(&swapChain3))) {
            auto** extendedTable = *reinterpret_cast<void***>(swapChain3.Get());
            present1Address = extendedTable[22];
            resize1Address = extendedTable[39];
        }
    }

    DestroyWindow(window);
    return presentAddress != nullptr && resizeAddress != nullptr;
}

Microsoft::WRL::ComPtr<IUnknown> CanonicalIdentity(IUnknown* object) {
    Microsoft::WRL::ComPtr<IUnknown> identity;
    if (object != nullptr) object->QueryInterface(IID_PPV_ARGS(&identity));
    return identity;
}

Microsoft::WRL::ComPtr<ID3D12CommandQueue> DirectQueueFrom(IUnknown* object) {
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
    if (object == nullptr || FAILED(object->QueryInterface(IID_PPV_ARGS(&queue))) ||
        queue->GetDesc().Type != D3D12_COMMAND_LIST_TYPE_DIRECT) {
        queue.Reset();
    }
    return queue;
}

HWND WindowForSwapChain(IDXGISwapChain* swapChain) {
    if (swapChain == nullptr) return nullptr;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain1;
    HWND window{};
    if (SUCCEEDED(swapChain->QueryInterface(IID_PPV_ARGS(&swapChain1))) &&
        SUCCEEDED(swapChain1->GetHwnd(&window)) && window != nullptr) {
        return window;
    }
    DXGI_SWAP_CHAIN_DESC description{};
    return SUCCEEDED(swapChain->GetDesc(&description))
        ? description.OutputWindow
        : nullptr;
}

RwuiEnhancedTargetWindowClass EnhancedWindowClass(
    const HWND window) noexcept {
    wchar_t name[64]{};
    if (window == nullptr || GetClassNameW(window, name, std::size(name)) == 0) {
        return RwuiEnhancedTargetWindowClass::Unknown;
    }
    if (_wcsicmp(name, L"sgaWindow") == 0) {
        return RwuiEnhancedTargetWindowClass::SgaWindow;
    }
    if (_wcsicmp(name, L"grcWindow") == 0) {
        return RwuiEnhancedTargetWindowClass::GrcWindow;
    }
    return RwuiEnhancedTargetWindowClass::Unknown;
}

bool IsEligibleEnhancedWindow(const HWND window) noexcept {
    if (window == nullptr || !IsWindow(window) ||
        GetAncestor(window, GA_ROOT) != window ||
        GetWindow(window, GW_OWNER) != nullptr ||
        (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 ||
        EnhancedWindowClass(window) ==
            RwuiEnhancedTargetWindowClass::Unknown) {
        return false;
    }
    DWORD processId{};
    GetWindowThreadProcessId(window, &processId);
    RECT client{};
    return processId == GetCurrentProcessId() &&
        GetClientRect(window, &client) != FALSE &&
        client.right > client.left && client.bottom > client.top;
}

RwuiLegacyTargetWindowClass LegacyWindowClass(const HWND window) noexcept {
    wchar_t name[64]{};
    if (window == nullptr || GetClassNameW(window, name, std::size(name)) == 0) {
        return RwuiLegacyTargetWindowClass::Unknown;
    }
    return _wcsicmp(name, L"grcWindow") == 0
        ? RwuiLegacyTargetWindowClass::GrcWindow
        : RwuiLegacyTargetWindowClass::Unknown;
}

bool IsEligibleLegacyWindow(const HWND window) noexcept {
    if (window == nullptr || !IsWindow(window) ||
        GetAncestor(window, GA_ROOT) != window ||
        GetWindow(window, GW_OWNER) != nullptr ||
        (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 ||
        LegacyWindowClass(window) == RwuiLegacyTargetWindowClass::Unknown) {
        return false;
    }
    DWORD processId{};
    GetWindowThreadProcessId(window, &processId);
    RECT client{};
    return processId == GetCurrentProcessId() &&
        GetClientRect(window, &client) != FALSE &&
        client.right > client.left && client.bottom > client.top;
}

std::uintptr_t PointerIdentity(const void* pointer) {
    return reinterpret_cast<std::uintptr_t>(pointer);
}

bool ApplyCapturedBindingLocked(const CapturedSwapChain& captured) {
    if (captured.identity == 0 || captured.swapChain == nullptr) return false;
    const auto activeWindow = targetWindow.load(std::memory_order_acquire);
    if (captured.queues.empty()) {
        if (strictEnhancedTarget.load(std::memory_order_acquire)) return false;
        if (activeWindow == nullptr || captured.window != activeWindow) {
            return false;
        }
        queueSelection = {};
        boundSwapChainIdentity = captured.identity;
        boundSwapChain = captured.swapChain;
        targetSwapChainPointer.store(
            captured.swapChain.Get(), std::memory_order_release);
        boundPresentQueues.clear();
        return true;
    }
    // A Legacy binding is deliberately D3D11-only. Never let a D3D12 queue
    // capture replace it just because another same-process swap chain uses the
    // selected HWND.
    if (strictLegacyTarget.load(std::memory_order_acquire)) return false;
    const QueueBindingCandidate candidate{
        captured.identity,
        PointerIdentity(captured.window),
        PointerIdentity(captured.queues.front().Get()),
        true,
        captured.source,
    };
    if (!ShouldReplaceQueueBinding(
            queueSelection, candidate, PointerIdentity(activeWindow))) {
        return false;
    }
    queueSelection = SelectQueueBinding(
        queueSelection, candidate, PointerIdentity(activeWindow));
    boundSwapChainIdentity = captured.identity;
    boundSwapChain = captured.swapChain;
    if (activeWindow != nullptr && captured.window == activeWindow) {
        targetSwapChainPointer.store(
            captured.swapChain.Get(), std::memory_order_release);
    }
    boundPresentQueues = captured.queues;
    return true;
}

void RecordCapturedSwapChain(
    IDXGISwapChain* swapChain,
    const HWND window,
    std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> queues,
    const QueueBindingSource source) {
    if (swapChain == nullptr || window == nullptr) return;
    auto identity = CanonicalIdentity(swapChain);
    if (identity == nullptr) return;

    CapturedSwapChain captured{
        window,
        PointerIdentity(identity.Get()),
        swapChain,
        std::move(queues),
        source,
    };
    Microsoft::WRL::ComPtr<IDXGISwapChain> prepareSwapChain;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> prepareQueue;
    {
        std::scoped_lock lock(queueMutex);
        std::erase_if(capturedSwapChains, [&](const CapturedSwapChain& existing) {
            return existing.identity == captured.identity;
        });
        capturedSwapChains.push_back(std::move(captured));
        if (capturedSwapChains.size() > MaximumCapturedSwapChains) {
            capturedSwapChains.erase(capturedSwapChains.begin());
        }
        const bool selected = ApplyCapturedBindingLocked(
            capturedSwapChains.back());
        const auto activeWindow = targetWindow.load(std::memory_order_acquire);
        if (selected && activeWindow != nullptr &&
            capturedSwapChains.back().window == activeWindow) {
            prepareSwapChain = boundSwapChain;
            if (!boundPresentQueues.empty()) {
                prepareQueue = boundPresentQueues.front();
            }
        }
    }
    // Device/pipeline/resource preparation occurs in the factory/resize hook,
    // never on the hot Present detour. Failure is fail-open and leaves the
    // overlay hidden until a later target bind or resize can prepare it.
    if (prepareSwapChain != nullptr) {
        g_compositor.Prepare(prepareSwapChain.Get(), prepareQueue.Get());
    }
}

void RecordFactorySwapChain(
    IDXGISwapChain* swapChain,
    const HWND window,
    IUnknown* deviceOrQueue) {
    auto queue = DirectQueueFrom(deviceOrQueue);
    std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> queues;
    if (queue != nullptr) queues.push_back(std::move(queue));
    RecordCapturedSwapChain(
        swapChain, window, std::move(queues), QueueBindingSource::FactoryCreation);
}

void ClearActiveBindingLocked() {
    queueSelection = {};
    boundSwapChainIdentity = 0;
    boundSwapChain.Reset();
    targetSwapChainPointer.store(nullptr, std::memory_order_release);
    boundPresentQueues.clear();
}

void RetireCapturedSwapChainLocked(IDXGISwapChain* const swapChain) {
    const auto identity = CanonicalIdentity(swapChain);
    if (identity == nullptr) return;
    const auto value = PointerIdentity(identity.Get());
    std::erase_if(capturedSwapChains, [&](const CapturedSwapChain& captured) {
        return captured.identity == value;
    });
    if (boundSwapChainIdentity == value) ClearActiveBindingLocked();
}

void BindRecordedTargetLocked(const HWND window) {
    ClearActiveBindingLocked();
    for (auto candidate = capturedSwapChains.rbegin();
         candidate != capturedSwapChains.rend(); ++candidate) {
        if (candidate->window == window && ApplyCapturedBindingLocked(*candidate)) {
            return;
        }
    }
}

bool IsTargetSwapChain(IDXGISwapChain* swapChain) {
    const auto window = targetWindow.load(std::memory_order_acquire);
    if (window == nullptr) return false;
    const auto cached = targetSwapChainPointer.load(std::memory_order_acquire);
    if (cached != nullptr) return cached == swapChain;
    if (strictEnhancedTarget.load(std::memory_order_acquire)) return false;
    if (WindowForSwapChain(swapChain) != window) {
        return false;
    }
    IDXGISwapChain* expected{};
    targetSwapChainPointer.compare_exchange_strong(
        expected, swapChain, std::memory_order_release,
        std::memory_order_relaxed);
    return true;
}

Microsoft::WRL::ComPtr<ID3D12CommandQueue> QueueForPresent(
    IDXGISwapChain* swapChain) {
    const auto identity = CanonicalIdentity(swapChain);
    if (identity == nullptr) return {};
    const auto identityValue = PointerIdentity(identity.Get());

    std::unique_lock lock(queueMutex, std::try_to_lock);
    if (!lock.owns_lock()) return {};
    if (boundSwapChainIdentity != identityValue ||
        boundPresentQueues.empty()) {
        return {};
    }
    if (boundPresentQueues.size() == 1) return boundPresentQueues.front();

    Microsoft::WRL::ComPtr<IDXGISwapChain3> swapChain3;
    if (FAILED(swapChain->QueryInterface(IID_PPV_ARGS(&swapChain3)))) return {};
    const auto index = swapChain3->GetCurrentBackBufferIndex();
    return index < boundPresentQueues.size()
        ? boundPresentQueues[index]
        : Microsoft::WRL::ComPtr<ID3D12CommandQueue>{};
}

std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> ReadPresentQueues(
    const UINT bufferCount,
    IUnknown* const* presentQueues,
    bool& supported) {
    std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> queues;
    supported = false;
    if (bufferCount == 0 || bufferCount > MaximumPresentQueues ||
        presentQueues == nullptr) {
        return queues;
    }
    queues.reserve(bufferCount);
    std::vector<std::uintptr_t> identities;
    identities.reserve(bufferCount);
    for (UINT index = 0; index < bufferCount; ++index) {
        auto queue = DirectQueueFrom(presentQueues[index]);
        if (queue == nullptr) {
            queues.clear();
            return queues;
        }
        const auto identity = CanonicalIdentity(queue.Get());
        if (identity == nullptr) {
            queues.clear();
            return queues;
        }
        identities.push_back(PointerIdentity(identity.Get()));
        queues.push_back(std::move(queue));
    }
    supported = IsUniformPresentQueueSet(identities);
    if (!supported) queues.clear();
    return queues;
}

void RenderTargetSwapChain(IDXGISwapChain* swapChain) noexcept {
    try {
        if (!IsTargetSwapChain(swapChain)) return;
        auto queue = QueueForPresent(swapChain);
        const bool localOwner = g_localPresentationOwner.load(
            std::memory_order_acquire);
        const bool visible = localOwner
            ? g_visible.load(std::memory_order_acquire)
            : g_compositor.ExternalPresentationVisible();
        if (visible) {
            // Capture mirrors this Present's committed surface, not a prior
            // successful frame. A device/preparation/contention failure must
            // immediately release input so an invisible overlay cannot trap
            // the player.
            const bool rendered = g_compositor.Render(
                swapChain, queue.Get(), false, !localOwner);
            g_inputQueue.SetCapture(localOwner && rendered);
        } else {
            g_inputQueue.SetCapture(false);
            // Hidden Presents only publish a bounded latest preparation request;
            // they never initialize a device or compile/upload in the detour.
            g_compositor.RequestPrepare(swapChain, queue.Get());
            // Passive startup text is not a menu and never acquires input.
            g_compositor.RenderStartupStatus(swapChain);
        }
    } catch (...) {
        // No renderer/COM exception may escape into DXGI's Present caller.
        g_inputQueue.SetCapture(false);
    }
}

HRESULT STDMETHODCALLTYPE CreateSwapChainHook(
    IDXGIFactory* factory,
    IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* description,
    IDXGISwapChain** swapChain) {
    HookCallbackScope callback;
    const auto original = originalCreateSwapChain;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    const auto result = original(
        factory, device, description, swapChain);
    if (!callback.IsTearingDown() && SUCCEEDED(result) &&
        swapChain != nullptr && *swapChain != nullptr &&
        description != nullptr) {
        try {
            RecordFactorySwapChain(
                *swapChain, description->OutputWindow, device);
        } catch (...) {
        }
    }
    return result;
}

HRESULT STDMETHODCALLTYPE CreateSwapChainForHwndHook(
    IDXGIFactory2* factory,
    IUnknown* device,
    const HWND window,
    const DXGI_SWAP_CHAIN_DESC1* description,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDescription,
    IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** swapChain) {
    HookCallbackScope callback;
    const auto original = originalCreateSwapChainForHwnd;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    const auto result = original(
        factory,
        device,
        window,
        description,
        fullscreenDescription,
        restrictToOutput,
        swapChain);
    if (!callback.IsTearingDown() && SUCCEEDED(result) &&
        swapChain != nullptr && *swapChain != nullptr) {
        try {
            RecordFactorySwapChain(*swapChain, window, device);
        } catch (...) {
        }
    }
    return result;
}

HRESULT STDMETHODCALLTYPE PresentHook(
    IDXGISwapChain* swapChain,
    const UINT syncInterval,
    const UINT flags) {
    HookCallbackScope callback;
    const auto original = originalPresent;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    const bool outermost = presentHookDepth++ == 0;
    bool visibilityProbeDrawn{};
    if (outermost && !callback.IsTearingDown()) {
        if (IsTestPresent(flags)) {
            g_inputQueue.SetCapture(false);
        } else {
            RenderTargetSwapChain(swapChain);
            // Last Reactor writes before forwarding this exact Present. A normal
            // menu draw cannot overwrite the diagnostic, and TEST never paints.
            g_compositor.RenderLegacyTextureProbe(swapChain);
            visibilityProbeDrawn = g_compositor.RenderLegacyVisibilityProbe(swapChain);
        }
    }
    const auto result = original(swapChain, syncInterval, flags);
    if (visibilityProbeDrawn) g_compositor.RecordLegacyProbePresent(result);
    if (outermost && !DidPresentCommit(result)) {
        g_inputQueue.SetCapture(false);
        if (IsDxgiDeviceFailure(result)) {
            const auto queue = QueueForPresent(swapChain);
            g_compositor.NotifyDeviceFailure(swapChain, queue.Get());
        }
    }
    --presentHookDepth;
    return result;
}

HRESULT STDMETHODCALLTYPE Present1Hook(
    IDXGISwapChain1* swapChain,
    const UINT syncInterval,
    const UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters) {
    HookCallbackScope callback;
    const auto original = originalPresent1;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    const bool outermost = presentHookDepth++ == 0;
    bool visibilityProbeDrawn{};
    if (outermost && !callback.IsTearingDown()) {
        if (IsTestPresent(flags)) {
            g_inputQueue.SetCapture(false);
        } else {
            RenderTargetSwapChain(swapChain);
            g_compositor.RenderLegacyTextureProbe(swapChain);
            visibilityProbeDrawn = g_compositor.RenderLegacyVisibilityProbe(swapChain);
        }
    }
    const auto result = original(
        swapChain, syncInterval, flags, parameters);
    if (visibilityProbeDrawn) g_compositor.RecordLegacyProbePresent(result);
    if (outermost && !DidPresentCommit(result)) {
        g_inputQueue.SetCapture(false);
        if (IsDxgiDeviceFailure(result)) {
            const auto queue = QueueForPresent(swapChain);
            g_compositor.NotifyDeviceFailure(swapChain, queue.Get());
        }
    }
    --presentHookDepth;
    return result;
}

HRESULT STDMETHODCALLTYPE ResizeBuffersHook(
    IDXGISwapChain* swapChain,
    const UINT bufferCount,
    const UINT width,
    const UINT height,
    const DXGI_FORMAT format,
    const UINT flags) {
    HookCallbackScope callback;
    const auto original = originalResizeBuffers;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    const bool target = !callback.IsTearingDown() &&
        IsTargetSwapChain(swapChain);
    const bool outermost = resizeHookDepth++ == 0;
    bool compositorRetired = true;
    if (outermost && target) {
        g_inputQueue.SetCapture(false);
        compositorRetired = g_compositor.BeforeResize(swapChain);
    }
    const auto result = original(
        swapChain, bufferCount, width, height, format, flags);
    if (outermost && target) {
        g_compositor.AfterResize(swapChain);
        if (!compositorRetired) {
            // A bounded D3D12 retirement failure must not let a later Present
            // resurrect wrappers for buffers DXGI is about to discard. Keep
            // the game's resize fail-open, but retire Reactor's stale route.
            try {
                std::scoped_lock lock(queueMutex);
                RetireCapturedSwapChainLocked(swapChain);
            } catch (...) {
            }
        } else if (SUCCEEDED(result)) {
            auto queue = QueueForPresent(swapChain);
            g_compositor.Prepare(swapChain, queue.Get());
        }
    }
    --resizeHookDepth;
    return result;
}

HRESULT STDMETHODCALLTYPE ResizeBuffers1Hook(
    IDXGISwapChain3* swapChain,
    const UINT bufferCount,
    const UINT width,
    const UINT height,
    const DXGI_FORMAT format,
    const UINT flags,
    const UINT* creationNodeMask,
    IUnknown* const* presentQueues) {
    HookCallbackScope callback;
    const auto original = originalResizeBuffers1;
    if (original == nullptr) return DXGI_ERROR_INVALID_CALL;
    std::vector<Microsoft::WRL::ComPtr<ID3D12CommandQueue>> queues;
    bool queueSetSupported{};
    if (!callback.IsTearingDown()) {
        try {
            queues = ReadPresentQueues(
                bufferCount, presentQueues, queueSetSupported);
        } catch (...) {
            queues.clear();
            queueSetSupported = false;
        }
    }
    const bool target = !callback.IsTearingDown() &&
        IsTargetSwapChain(swapChain);
    const bool outermost = resizeHookDepth++ == 0;
    bool compositorRetired = true;
    if (outermost && target) {
        g_inputQueue.SetCapture(false);
        compositorRetired = g_compositor.BeforeResize(swapChain);
    }
    const auto result = original(
        swapChain,
        bufferCount,
        width,
        height,
        format,
        flags,
        creationNodeMask,
        presentQueues);
    // The real resize has completed. Lift the worker fence before recording
    // the successful queue refresh because RecordCapturedSwapChain performs
    // the one synchronous preparation of the new buffers.
    if (outermost && target) {
        g_compositor.AfterResize(swapChain);
        if (!compositorRetired) {
            try {
                std::scoped_lock lock(queueMutex);
                RetireCapturedSwapChainLocked(swapChain);
            } catch (...) {
            }
        }
    }
    if (!callback.IsTearingDown() && SUCCEEDED(result) &&
        bufferCount != 0 && presentQueues != nullptr &&
        !queueSetSupported) {
        // Mixed per-buffer queues require separate compositor state. Explicitly
        // fail the overlay open instead of rebuilding against a different queue
        // on each Present; the game Present itself remains untouched.
        if (target) g_inputQueue.SetCapture(false);
        try {
            std::scoped_lock lock(queueMutex);
            // Retire the factory/earlier uniform capture too. Merely clearing
            // the active binding lets a later HWND rebind resurrect a queue
            // that no longer describes this now-mixed swap chain.
            RetireCapturedSwapChainLocked(swapChain);
        } catch (...) {
        }
    } else if (!callback.IsTearingDown() && SUCCEEDED(result) &&
        !queues.empty() && (!outermost || !target || compositorRetired)) {
        try {
            RecordCapturedSwapChain(
                swapChain,
                WindowForSwapChain(swapChain),
                std::move(queues),
                QueueBindingSource::ResizeBuffers1);
        } catch (...) {
        }
    }
    --resizeHookDepth;
    return result;
}

bool CreateHookTracked(
    void* address,
    void* detour,
    void** original,
    const bool required) {
    const auto disposition = ClassifyHookAddress(
        PointerIdentity(address), createdHookIdentities, required);
    if (disposition == HookAddressDisposition::SkipOptional) {
        if (original != nullptr) *original = nullptr;
        return true;
    }
    if (disposition == HookAddressDisposition::RejectRequired) return false;
    if (MH_CreateHook(address, detour, original) != MH_OK) {
        if (!required && original != nullptr) *original = nullptr;
        return !required;
    }
    createdHookAddresses.push_back(address);
    createdHookIdentities.push_back(PointerIdentity(address));
    return true;
}

void ClearResolvedMethods() {
    originalCreateSwapChain = nullptr;
    originalCreateSwapChainForHwnd = nullptr;
    originalPresent = nullptr;
    originalPresent1 = nullptr;
    originalResizeBuffers = nullptr;
    originalResizeBuffers1 = nullptr;
    createSwapChainAddress = nullptr;
    createSwapChainForHwndAddress = nullptr;
    presentAddress = nullptr;
    present1Address = nullptr;
    resizeAddress = nullptr;
    resize1Address = nullptr;
}

bool WaitForHookCallbacksToDrain() {
    using Clock = std::chrono::steady_clock;
    constexpr auto MaximumDrainTime = std::chrono::seconds(2);
    constexpr auto QuietPeriod = std::chrono::milliseconds(25);
    constexpr auto PollInterval = std::chrono::milliseconds(2);
    const auto deadline = Clock::now() + MaximumDrainTime;
    auto quietSince = Clock::time_point{};
    bool quiet = false;
    std::unique_lock lock(callbackDrainMutex);
    while (Clock::now() < deadline) {
        if (activeHookCallbacks.load(std::memory_order_acquire) == 0) {
            if (!quiet) {
                quietSince = Clock::now();
                quiet = true;
            } else if (Clock::now() - quietSince >= QuietPeriod) {
                return true;
            }
        } else {
            quiet = false;
        }
        callbackDrainCondition.wait_for(lock, PollInterval);
    }
    return false;
}

void FinalizeHookRemovalUnlocked() {
    for (auto address = createdHookAddresses.rbegin();
         address != createdHookAddresses.rend(); ++address) {
        MH_RemoveHook(*address);
    }
    createdHookAddresses.clear();
    createdHookIdentities.clear();
    if (minHookOwned) MH_Uninitialize();

    {
        std::scoped_lock lock(queueMutex);
        ClearActiveBindingLocked();
        capturedSwapChains.clear();
    }
    g_compositor.ShutdownSharedFrameConsumer();
    // Stop/join preparation before the final resource reset. Otherwise a
    // queued request can recreate a stale swap-chain renderer after Reset and
    // leave it retained beyond hook removal.
    g_compositor.Reset();
    hooksArmed = false;
    hookCleanupPending = false;
    minHookOwned = false;
    ClearResolvedMethods();
}

void RemoveHooksUnlocked() {
    g_visible.store(false, std::memory_order_relaxed);
    g_localPresentationOwner.store(false, std::memory_order_release);
    strictEnhancedTarget.store(false, std::memory_order_release);
    strictLegacyTarget.store(false, std::memory_order_release);
    g_inputQueue.SetCapture(false);
    g_inputQueue.Detach();
    inputWindow = nullptr;
    targetWindow.store(nullptr, std::memory_order_release);
    teardownRequested.store(true, std::memory_order_release);

    for (const auto address : createdHookAddresses) {
        MH_DisableHook(address);
    }
    hooksArmed = false;
    if (!WaitForHookCallbacksToDrain()) {
        // Keep MinHook's trampolines and the original function pointers alive.
        // A later Shutdown/Arm call can finalize once the stuck callback exits.
        hookCleanupPending = true;
        return;
    }
    FinalizeHookRemovalUnlocked();
}

} // namespace

bool ArmDxgiHooks() {
    std::scoped_lock lock(lifecycleMutex);
    if (hooksArmed) return true;
    if (hookCleanupPending) {
        if (!WaitForHookCallbacksToDrain()) return false;
        FinalizeHookRemovalUnlocked();
    }
    if (!ResolveFactoryMethods() || !ResolveDxgiMethods()) return false;

    const auto initializeResult = MH_Initialize();
    if (initializeResult != MH_OK &&
        initializeResult != MH_ERROR_ALREADY_INITIALIZED) {
        ClearResolvedMethods();
        return false;
    }
    minHookOwned = initializeResult == MH_OK;

    const bool created =
        CreateHookTracked(
            createSwapChainAddress,
            reinterpret_cast<void*>(&CreateSwapChainHook),
            reinterpret_cast<void**>(&originalCreateSwapChain),
            true) &&
        CreateHookTracked(
            createSwapChainForHwndAddress,
            reinterpret_cast<void*>(&CreateSwapChainForHwndHook),
            reinterpret_cast<void**>(&originalCreateSwapChainForHwnd),
            true) &&
        CreateHookTracked(
            presentAddress,
            reinterpret_cast<void*>(&PresentHook),
            reinterpret_cast<void**>(&originalPresent),
            true) &&
        CreateHookTracked(
            resizeAddress,
            reinterpret_cast<void*>(&ResizeBuffersHook),
            reinterpret_cast<void**>(&originalResizeBuffers),
            true) &&
        CreateHookTracked(
            present1Address,
            reinterpret_cast<void*>(&Present1Hook),
            reinterpret_cast<void**>(&originalPresent1),
            true) &&
        CreateHookTracked(
            resize1Address,
            reinterpret_cast<void*>(&ResizeBuffers1Hook),
            reinterpret_cast<void**>(&originalResizeBuffers1),
            true);

    teardownRequested.store(false, std::memory_order_release);
    bool enabled = created;
    if (enabled) {
        for (const auto address : createdHookAddresses) {
            if (MH_EnableHook(address) != MH_OK) {
                enabled = false;
                break;
            }
        }
    }
    if (!enabled) {
        RemoveHooksUnlocked();
        return false;
    }

    // Arm discovery/control I/O before the first Present. Device binding is a
    // lightweight seam published later by the compositor initialization path.
    // Failure is fail-open: the existing CPU mailbox remains available.
    g_compositor.ArmSharedFrameConsumer();
    hooksArmed = true;
    return true;
}

RwuiEnhancedTargetBindStatus BindEnhancedCompositor(const HWND window) {
    if (!IsEligibleEnhancedWindow(window) || !ArmDxgiHooks()) {
        return RwuiEnhancedTargetBindStatus::Invalid;
    }

    Microsoft::WRL::ComPtr<IDXGISwapChain> prepareSwapChain;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> prepareQueue;
    {
        std::scoped_lock lock(lifecycleMutex);
        if (!hooksArmed) return RwuiEnhancedTargetBindStatus::Invalid;
        if (g_localPresentationOwner.load(std::memory_order_acquire)) {
            return inputWindow == window
                ? RwuiEnhancedTargetBindStatus::Bound
                : RwuiEnhancedTargetBindStatus::Invalid;
        }
        strictEnhancedTarget.store(true, std::memory_order_release);
        strictLegacyTarget.store(false, std::memory_order_release);
        targetWindow.store(window, std::memory_order_release);
        std::scoped_lock queueLock(queueMutex);
        BindRecordedTargetLocked(window);
        if (boundSwapChain == nullptr || boundPresentQueues.empty()) {
            return RwuiEnhancedTargetBindStatus::PendingCapture;
        }
        prepareSwapChain = boundSwapChain;
        prepareQueue = boundPresentQueues.front();
    }
    if (prepareQueue == nullptr ||
        !g_compositor.Prepare(prepareSwapChain.Get(), prepareQueue.Get())) {
        return RwuiEnhancedTargetBindStatus::PendingCapture;
    }
    const auto stats = g_compositor.Stats();
    return stats.api == RwuiRenderApi::Direct3D12
        ? RwuiEnhancedTargetBindStatus::Bound
        : RwuiEnhancedTargetBindStatus::PendingCapture;
}

RwuiLegacyTargetBindStatus BindLegacyCompositor(const HWND window) {
    if (!IsEligibleLegacyWindow(window) || !ArmDxgiHooks()) {
        return RwuiLegacyTargetBindStatus::Invalid;
    }

    Microsoft::WRL::ComPtr<IDXGISwapChain> prepareSwapChain;
    {
        std::scoped_lock lock(lifecycleMutex);
        if (!hooksArmed) return RwuiLegacyTargetBindStatus::Invalid;
        // The Legacy root ASI is a compositor bridge only. A prior local
        // RWUI_Initialize owner is incompatible because it subclasses/input-
        // attaches the HWND; fail closed instead of silently taking it over.
        if (g_localPresentationOwner.load(std::memory_order_acquire)) {
            return RwuiLegacyTargetBindStatus::Invalid;
        }
        // ScriptHook can load the root ASI after GTA created its D3D11 swap
        // chain. Present then discovers the exact HWND/chain without a factory
        // capture. Preserve that late-bound identity across the worker's
        // repeated bind polls while asynchronous preparation completes.
        if (strictLegacyTarget.load(std::memory_order_acquire) &&
            targetWindow.load(std::memory_order_acquire) == window &&
            targetSwapChainPointer.load(std::memory_order_acquire) != nullptr) {
            return g_compositor.Stats().api == RwuiRenderApi::Direct3D11
                ? RwuiLegacyTargetBindStatus::Bound
                : RwuiLegacyTargetBindStatus::PendingCapture;
        }
        strictEnhancedTarget.store(false, std::memory_order_release);
        strictLegacyTarget.store(true, std::memory_order_release);
        targetWindow.store(window, std::memory_order_release);
        std::scoped_lock queueLock(queueMutex);
        BindRecordedTargetLocked(window);
        if (boundSwapChain == nullptr || !boundPresentQueues.empty()) {
            return RwuiLegacyTargetBindStatus::PendingCapture;
        }
        prepareSwapChain = boundSwapChain;
    }
    if (!g_compositor.Prepare(prepareSwapChain.Get(), nullptr)) {
        return RwuiLegacyTargetBindStatus::PendingCapture;
    }
    return g_compositor.Stats().api == RwuiRenderApi::Direct3D11
        ? RwuiLegacyTargetBindStatus::Bound
        : RwuiLegacyTargetBindStatus::PendingCapture;
}

bool InstallHooks(const HWND window) {
    if (window == nullptr || !IsWindow(window) || !ArmDxgiHooks()) {
        return false;
    }

    std::scoped_lock lock(lifecycleMutex);
    if (!hooksArmed) return false;
    strictEnhancedTarget.store(false, std::memory_order_release);
    strictLegacyTarget.store(false, std::memory_order_release);
    if (inputWindow == window) {
        g_localPresentationOwner.store(true, std::memory_order_release);
        return true;
    }

    g_localPresentationOwner.store(false, std::memory_order_release);
    g_inputQueue.SetCapture(false);
    g_inputQueue.Detach();
    inputWindow = nullptr;
    targetWindow.store(window, std::memory_order_release);
    Microsoft::WRL::ComPtr<IDXGISwapChain> prepareSwapChain;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> prepareQueue;
    {
        std::scoped_lock queueLock(queueMutex);
        BindRecordedTargetLocked(window);
        prepareSwapChain = boundSwapChain;
        if (!boundPresentQueues.empty()) {
            prepareQueue = boundPresentQueues.front();
        }
    }
    if (prepareSwapChain != nullptr) {
        g_compositor.Prepare(prepareSwapChain.Get(), prepareQueue.Get());
    }
    if (!g_inputQueue.Attach(window)) {
        targetWindow.store(nullptr, std::memory_order_release);
        std::scoped_lock queueLock(queueMutex);
        ClearActiveBindingLocked();
        return false;
    }
    inputWindow = window;
    g_localPresentationOwner.store(true, std::memory_order_release);
    // Capture commits only after the first successfully drawn frame.
    g_inputQueue.SetCapture(false);
    return true;
}

RwuiEnhancedHookDiagnostics EnhancedHookDiagnostics() {
    RwuiEnhancedHookDiagnostics diagnostics{};
    diagnostics.byteSize = sizeof(diagnostics);
    diagnostics.majorVersion = 1;
    diagnostics.minorVersion = 0;
    diagnostics.processId = GetCurrentProcessId();

    std::scoped_lock lock(lifecycleMutex);
    if (hooksArmed) diagnostics.flags |=
        RWUI_ENHANCED_DIAGNOSTIC_HOOKS_ARMED;
    if (g_localPresentationOwner.load(std::memory_order_acquire)) {
        diagnostics.flags |= RWUI_ENHANCED_DIAGNOSTIC_LOCAL_OWNER;
    }
    if (inputWindow != nullptr) diagnostics.flags |=
        RWUI_ENHANCED_DIAGNOSTIC_INPUT_ATTACHED;

    const auto window = targetWindow.load(std::memory_order_acquire);
    diagnostics.targetWindowClass = EnhancedWindowClass(window);
    if (window != nullptr) {
        DWORD targetProcessId{};
        GetWindowThreadProcessId(window, &targetProcessId);
        diagnostics.targetWindowProcessId = targetProcessId;
    }
    {
        std::scoped_lock queueLock(queueMutex);
        diagnostics.queueBindingSource = static_cast<std::uint32_t>(
            queueSelection.source);
        if (!boundPresentQueues.empty()) diagnostics.flags |=
            RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE;
    }
    const auto stats = g_compositor.Stats();
    diagnostics.renderedFrames = stats.renderedFrames;
    diagnostics.lastFrameGeneration = stats.lastFrameGeneration;
    // Reserved diagnostics remain ABI-compatible while giving the integration
    // gate stable identities without exposing raw COM addresses.
    diagnostics.reserved[0] = g_compositor.D3D12InteropGeneration();
    diagnostics.reserved[1] = g_compositor.D3D12BackBufferGeneration();
    if (stats.api == RwuiRenderApi::Direct3D12) diagnostics.flags |=
        RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY;
    if ((diagnostics.flags &
            (RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE |
             RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY)) ==
        (RWUI_ENHANCED_DIAGNOSTIC_DIRECT_QUEUE |
         RWUI_ENHANCED_DIAGNOSTIC_D3D12_READY) &&
        targetSwapChainPointer.load(std::memory_order_acquire) != nullptr) {
        diagnostics.flags |= RWUI_ENHANCED_DIAGNOSTIC_TARGET_BOUND;
    }
    const auto shared = g_compositor.SharedFrameDiagnostics();
    diagnostics.consumerStage = static_cast<std::uint32_t>(shared.stage);
    diagnostics.presentationEpoch = shared.presentationEpoch;
    if (g_compositor.ExternalPresentationVisible()) diagnostics.flags |=
        RWUI_ENHANCED_DIAGNOSTIC_EXTERNAL_VISIBLE;
    if (g_compositor.ExternalProducerConnected()) {
        diagnostics.flags |= RWUI_ENHANCED_DIAGNOSTIC_PRODUCER_CONNECTED;
    }
    return diagnostics;
}

RwuiLegacyHookDiagnostics LegacyHookDiagnostics() {
    RwuiLegacyHookDiagnostics diagnostics{};
    diagnostics.byteSize = sizeof(diagnostics);
    diagnostics.majorVersion = 1;
    diagnostics.minorVersion = 0;
    diagnostics.processId = GetCurrentProcessId();

    std::scoped_lock lock(lifecycleMutex);
    if (hooksArmed) diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_HOOKS_ARMED;
    if (g_localPresentationOwner.load(std::memory_order_acquire)) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_LOCAL_OWNER;
    }
    if (inputWindow != nullptr) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_INPUT_ATTACHED;
    }

    const auto window = targetWindow.load(std::memory_order_acquire);
    diagnostics.targetWindowClass = LegacyWindowClass(window);
    if (window != nullptr) {
        DWORD targetProcessId{};
        GetWindowThreadProcessId(window, &targetProcessId);
        diagnostics.targetWindowProcessId = targetProcessId;
    }
    const auto stats = g_compositor.Stats();
    diagnostics.renderApi = stats.api;
    diagnostics.renderedFrames = stats.renderedFrames;
    diagnostics.lastFrameGeneration = stats.lastFrameGeneration;
    // Preserve the diagnostics ABI while exposing stable lifecycle identities:
    // full D3D11 compositor initialization versus backbuffer-only preparation.
    diagnostics.reserved[0] = g_compositor.D3D11CompositorGeneration();
    diagnostics.reserved[1] = g_compositor.D3D11BackBufferGeneration();
    if (stats.api == RwuiRenderApi::Direct3D11) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_D3D11_READY;
    }
    if ((diagnostics.flags & RWUI_LEGACY_DIAGNOSTIC_D3D11_READY) != 0 &&
        targetSwapChainPointer.load(std::memory_order_acquire) != nullptr) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_TARGET_BOUND;
    }
    const auto shared = g_compositor.SharedFrameDiagnostics();
    diagnostics.consumerStage = static_cast<std::uint32_t>(shared.stage);
    diagnostics.presentationEpoch = shared.presentationEpoch;
    if (g_compositor.ExternalPresentationVisible()) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_EXTERNAL_VISIBLE;
    }
    if (g_compositor.ExternalProducerConnected()) {
        diagnostics.flags |= RWUI_LEGACY_DIAGNOSTIC_PRODUCER_CONNECTED;
    }
    return diagnostics;
}

void RemoveHooks() {
    std::scoped_lock lock(lifecycleMutex);
    RemoveHooksUnlocked();
}

} // namespace rwui

RWUI_API std::int32_t RWUI_CALL RWUI_ArmEnhancedHook() {
    try {
        return rwui::ArmDxgiHooks() ? 1 : 0;
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_ArmLegacyHook() {
    try {
        return rwui::ArmDxgiHooks() ? 1 : 0;
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetD3D11DeviceDiagnostics(
    RwuiD3D11DeviceDiagnostics* diagnostics) {
    if (!diagnostics || diagnostics->byteSize != sizeof(*diagnostics)) return 0;
    try {
        auto snapshot = rwui::g_compositor.D3D11DeviceDiagnostics();
        if (snapshot.byteSize != sizeof(snapshot)) return 0;
        *diagnostics = snapshot;
        return 1;
    } catch (...) { return 0; }
}

RWUI_API void RWUI_CALL RWUI_EnableD3D11DiagnosticProbes(std::int32_t enabled) {
    rwui::g_compositor.EnableD3D11DiagnosticProbes(enabled != 0);
}

RWUI_API void RWUI_CALL RWUI_ConfigureLegacyTextureProbe(const wchar_t* helper, const wchar_t* log) {
    try { rwui::g_compositor.ConfigureLegacyTextureProbe(helper, log); } catch (...) {}
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetD3D11CompatibilityDiagnostics(
    RwuiD3D11CompatibilityDiagnostics* diagnostics) {
    if (!diagnostics || diagnostics->byteSize != sizeof(*diagnostics)) return 0;
    *diagnostics = rwui::g_compositor.D3D11CompatibilityDiagnostics();
    return 1;
}

RWUI_API std::int32_t RWUI_CALL RWUI_BindLegacyTarget(void* targetWindow) {
    try {
        return static_cast<std::int32_t>(rwui::BindLegacyCompositor(
            static_cast<HWND>(targetWindow)));
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
        return static_cast<std::int32_t>(
            RwuiLegacyTargetBindStatus::Invalid);
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetLegacyHookDiagnostics(
    RwuiLegacyHookDiagnostics* const diagnostics) {
    if (diagnostics == nullptr ||
        diagnostics->byteSize != sizeof(RwuiLegacyHookDiagnostics)) {
        return 0;
    }
    try {
        *diagnostics = rwui::LegacyHookDiagnostics();
        return 1;
    } catch (...) {
        *diagnostics = {};
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_BindEnhancedTarget(void* targetWindow) {
    try {
        return static_cast<std::int32_t>(rwui::BindEnhancedCompositor(
            static_cast<HWND>(targetWindow)));
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
        return static_cast<std::int32_t>(
            RwuiEnhancedTargetBindStatus::Invalid);
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetEnhancedHookDiagnostics(
    RwuiEnhancedHookDiagnostics* const diagnostics) {
    if (diagnostics == nullptr ||
        diagnostics->byteSize != sizeof(RwuiEnhancedHookDiagnostics)) {
        return 0;
    }
    try {
        *diagnostics = rwui::EnhancedHookDiagnostics();
        return 1;
    } catch (...) {
        *diagnostics = {};
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetSharedTextureConsumerDiagnostics(
    RwuiSharedTextureConsumerDiagnostics* const diagnostics) {
    if (diagnostics == nullptr ||
        diagnostics->byteSize !=
            sizeof(RwuiSharedTextureConsumerDiagnostics)) {
        return 0;
    }
    try {
        const auto value = rwui::g_compositor.SharedFrameDiagnostics();
        *diagnostics = {
            sizeof(RwuiSharedTextureConsumerDiagnostics), 1, 1,
            static_cast<std::uint32_t>(value.stage),
            value.lastReceiveError,
            value.lastImportError,
            value.lastImportHresult,
            value.discoveryMisses,
            value.producerImageRejects,
            value.connectFailures,
            value.receivedFrames,
            value.receiveFailures,
            value.importedResources,
            value.publishedFrames,
            value.copyFailures,
            value.acknowledgementsAccepted,
            value.acknowledgementsRejected,
            value.acknowledgementFailures,
            value.lastReceivedGeneration,
            value.lastPublishedGeneration,
        };
        return 1;
    } catch (...) {
        *diagnostics = {};
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_Initialize(void* targetWindow) {
    try {
        return rwui::InstallHooks(static_cast<HWND>(targetWindow)) ? 1 : 0;
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
        return 0;
    }
}

RWUI_API void RWUI_CALL RWUI_Shutdown() {
    // Shutdown is best-effort by contract: attempt every independent cleanup
    // stage even if one reports an allocation/system exception.
    try {
        RWUI_TestStop();
    } catch (...) {
    }
    // A producer owns a control worker, pipe handles, keyed shared textures,
    // and a discovery mapping independently of the compositor hooks.  Stop it
    // before unhooking so Shutdown + FreeLibrary cannot leave a DLL-backed
    // worker or shared-GPU object alive across the next load.
    try {
        RWUI_StopSharedTextureProducer();
    } catch (...) {
    }
    try {
        rwui::RemoveHooks();
    } catch (...) {
        rwui::g_inputQueue.SetCapture(false);
    }
    try {
        rwui::g_frameMailbox.Clear();
    } catch (...) {
    }
}

RWUI_API void RWUI_CALL RWUI_SetVisible(const std::int32_t visible) {
    try {
        const bool active = visible != 0;
        rwui::g_visible.store(active, std::memory_order_release);
        // Opening is two-phase: RenderTargetSwapChain enables capture only
        // after a frame commits. Closing releases it immediately.
        if (!active) rwui::g_inputQueue.SetCapture(false);
    } catch (...) {
        rwui::g_visible.store(false, std::memory_order_release);
        rwui::g_inputQueue.SetCapture(false);
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_SubmitFrame(
    const void* bgraPixels,
    const std::int32_t width,
    const std::int32_t height,
    const std::int32_t stride,
    const std::uint64_t generation) {
    try {
        if (!rwui::g_frameMailbox.Submit(
                bgraPixels, width, height, stride, generation)) return 0;
        // Upload/create work belongs to the submitting host thread. If the
        // compositor is not prepared yet, Prepare stages the retained latest
        // mailbox frame later; submission itself remains successful.
        rwui::g_compositor.StageLatestCpuFrame();
        return 1;
    } catch (...) {
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_SubmitStartupStatusFrame(
    const void* pixels, std::int32_t width, std::int32_t height, std::int32_t stride, std::uint64_t generation) {
    return rwui::g_compositor.SubmitStartupStatus(pixels, width, height, stride, generation) ? 1 : 0;
}

RWUI_API std::int32_t RWUI_CALL RWUI_PollInput(RwuiInputEvent* inputEvent) {
    try {
        return inputEvent != nullptr &&
            rwui::g_inputQueue.Poll(*inputEvent) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

RWUI_API std::int32_t RWUI_CALL RWUI_GetStats(RwuiRenderStats* stats) {
    if (stats == nullptr) return 0;
    *stats = {};
    try {
        *stats = rwui::g_compositor.Stats();
        return 1;
    } catch (...) {
        return 0;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
