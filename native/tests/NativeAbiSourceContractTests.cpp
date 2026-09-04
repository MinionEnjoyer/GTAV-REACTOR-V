#include <array>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <string_view>

namespace {

int failures{};

void Check(const bool condition, const std::string_view message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

std::string Read(const char* const path) {
    std::ifstream stream(path, std::ios::binary);
    return std::string(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}

std::string FunctionBody(
    const std::string& source,
    const std::string_view functionName) {
    const auto declaration = source.find(functionName);
    if (declaration == std::string::npos) return {};
    const auto open = source.find('{', declaration + functionName.size());
    if (open == std::string::npos) return {};
    std::size_t depth{};
    for (auto index = open; index < source.size(); ++index) {
        if (source[index] == '{') {
            ++depth;
        } else if (source[index] == '}' && --depth == 0) {
            return source.substr(open, index - open + 1);
        }
    }
    return {};
}

bool HasCatchAllBoundary(
    const std::string& source,
    const std::string_view exportName) {
    std::size_t declaration{};
    std::size_t open{std::string::npos};
    for (;;) {
        declaration = source.find("RWUI_API", declaration);
        if (declaration == std::string::npos) return false;
        const auto next = source.find("RWUI_API", declaration + 8);
        const auto candidate = source.find(exportName, declaration);
        const auto candidateOpen = source.find('{', declaration);
        if (candidate != std::string::npos &&
            candidateOpen != std::string::npos && candidate < candidateOpen &&
            (next == std::string::npos || candidate < next)) {
            open = candidateOpen;
            break;
        }
        if (next == std::string::npos) return false;
        declaration = next;
    }
    if (open == std::string::npos) return false;
    std::size_t depth{};
    for (auto index = open; index < source.size(); ++index) {
        if (source[index] == '{') {
            ++depth;
        } else if (source[index] == '}' && --depth == 0) {
            const auto body = source.substr(open, index - open + 1);
            return body.find("try") != std::string::npos &&
                body.find("catch (...)") != std::string::npos;
        }
    }
    return false;
}

template<std::size_t Count>
void CheckExports(
    const std::string& source,
    const std::array<std::string_view, Count>& exports) {
    Check(!source.empty(), "public ABI source is readable");
    for (const auto name : exports) {
        if (!HasCatchAllBoundary(source, name)) {
            std::cerr << "FAIL: public ABI export lacks catch-all boundary: "
                      << name << '\n';
            ++failures;
        }
    }
}

} // namespace

int main() {
    const auto hooks = Read(REACTORV_HOOK_MANAGER_SOURCE_PATH);
    CheckExports(hooks, std::array{
        std::string_view{"RWUI_ArmEnhancedHook"},
        std::string_view{"RWUI_BindEnhancedTarget"},
        std::string_view{"RWUI_GetEnhancedHookDiagnostics"},
        std::string_view{"RWUI_ArmLegacyHook"},
        std::string_view{"RWUI_BindLegacyTarget"},
        std::string_view{"RWUI_GetLegacyHookDiagnostics"},
        std::string_view{"RWUI_Initialize"},
        std::string_view{"RWUI_Shutdown"},
        std::string_view{"RWUI_SetVisible"},
        std::string_view{"RWUI_SubmitFrame"},
        std::string_view{"RWUI_PollInput"},
        std::string_view{"RWUI_GetStats"},
    });
    const auto hookScope = hooks.substr(
        hooks.find("class HookCallbackScope"),
        hooks.find("LRESULT CALLBACK DummyWindowProcedure") -
            hooks.find("class HookCallbackScope"));
    const auto firstNotify = hookScope.find(
        "callbackDrainCondition.notify_all()");
    Check(firstNotify != std::string::npos &&
        hookScope.find("callbackDrainCondition.notify_all()", firstNotify + 1) ==
            std::string::npos &&
        hookScope.find("prior == 1") < firstNotify &&
        hookScope.find("teardownRequested.load") < firstNotify,
        "hot hook callbacks notify only the final teardown waiter");
    const auto finalizeStart = hooks.find("void FinalizeHookRemovalUnlocked");
    const auto finalizeEnd = hooks.find(
        "void RemoveHooksUnlocked", finalizeStart);
    const auto finalize = hooks.substr(
        finalizeStart, finalizeEnd - finalizeStart);
    Check(finalize.find("g_compositor.ShutdownSharedFrameConsumer()") <
            finalize.find("g_compositor.Reset()"),
        "teardown joins preparation before its final compositor reset");
    const auto shutdown = FunctionBody(hooks, "RWUI_API void RWUI_CALL RWUI_Shutdown");
    Check(!shutdown.empty() &&
        shutdown.find("RWUI_TestStop()") <
            shutdown.find("RWUI_StopSharedTextureProducer()") &&
        shutdown.find("RWUI_StopSharedTextureProducer()") <
            shutdown.find("rwui::RemoveHooks()"),
        "shutdown retires the test surface and shared producer before hooks");

    const auto compositor = Read(REACTORV_DIRECTX_COMPOSITOR_SOURCE_PATH);
    const auto requestStart = compositor.find(
        "void DirectXCompositor::RequestPrepare");
    const auto requestEnd = compositor.find(
        "void DirectXCompositor::DrainPendingPreparationRequest", requestStart);
    const auto request = compositor.substr(
        requestStart, requestEnd - requestStart);
    const auto gate = request.find("preparationRequestGate_.test_and_set");
    const auto armedRecheck = request.find(
        "!preparationArmed_.load", gate + 1);
    const auto addRef = request.find("swapChain->AddRef()", armedRecheck + 1);
    const auto signal = request.find("SetEvent(preparationEvent_)", addRef + 1);
    const auto releaseGate = request.find(
        "preparationRequestGate_.clear", signal + 1);
    Check(requestStart != std::string::npos &&
        requestEnd != std::string::npos && gate != std::string::npos &&
        armedRecheck != std::string::npos && addRef != std::string::npos &&
        signal != std::string::npos && releaseGate != std::string::npos &&
        gate < armedRecheck && armedRecheck < addRef && addRef < signal &&
        signal < releaseGate,
        "latest-only preparation rechecks lifecycle under its nonblocking gate");
    const auto initializeD3D11BackBuffers = FunctionBody(
        compositor, "bool DirectXCompositor::InitializeD3D11BackBuffers");
    const auto renderD3D11 = FunctionBody(
        compositor, "bool DirectXCompositor::RenderD3D11");
    Check(!initializeD3D11BackBuffers.empty() && !renderD3D11.empty() &&
        initializeD3D11BackBuffers.find("GetBuffer(0") != std::string::npos &&
        initializeD3D11BackBuffers.find(
            "swapChain3_->GetCurrentBackBufferIndex()") ==
            std::string::npos &&
        renderD3D11.find("swapChain3_->GetCurrentBackBufferIndex()") ==
            std::string::npos,
        "D3D11 uses the runtime-rotated buffer-zero identity for flip chains");

    const auto sharedGpu = Read(REACTORV_SHARED_GPU_EXPORTS_SOURCE_PATH);
    CheckExports(sharedGpu, std::array{
        std::string_view{"RWUI_GetSharedTextureCapabilities"},
        std::string_view{"RWUI_StartSharedTextureProducer"},
        std::string_view{"RWUI_StopSharedTextureProducer"},
        std::string_view{"RWUI_SetSharedTextureProducerVisible"},
        std::string_view{"RWUI_ProbeSharedTexture"},
        std::string_view{"RWUI_SubmitSharedTexture"},
        std::string_view{"RWUI_SubmitSharedTextureStatus"},
    });

    const auto testSurface = Read(REACTORV_TEST_SURFACE_SOURCE_PATH);
    CheckExports(testSurface, std::array{
        std::string_view{"RWUI_TestStart"},
        std::string_view{"RWUI_TestStop"},
        std::string_view{"RWUI_TestIsRunning"},
    });
    Check(testSurface.find("GetModuleHandleExW") != std::string::npos &&
        testSurface.find("GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS") !=
            std::string::npos &&
        testSurface.find("GetClassInfoW") != std::string::npos &&
        testSurface.find("existing.lpfnWndProc != TestWindowProcedure") !=
            std::string::npos &&
        testSurface.find("UnregisterClassW(TestWindowClassName, instance_)") !=
            std::string::npos &&
        testSurface.find("GetModuleHandleW(nullptr)") == std::string::npos,
        "test surface owns, validates, and unregisters its DLL-local class");

    const auto discovery = Read(REACTORV_SHARED_GPU_DISCOVERY_SOURCE_PATH);
    Check(discovery.find("using DiscoveryName = std::array") !=
            std::string::npos &&
        discovery.find("std::wstring") == std::string::npos,
        "noexcept discovery uses fixed-capacity, allocation-free names");
    Check(discovery.find("SetPresentationVisible") == std::string::npos,
        "writable presentation state is not exposed through discovery memory");

    const auto channel = Read(REACTORV_SHARED_GPU_CHANNEL_SOURCE_PATH);
    const auto serverCreate = FunctionBody(
        channel, "SharedGpuFrameChannelServer::Create");
    const auto clientConnect = FunctionBody(
        channel, "SharedGpuFrameChannelClient::Connect");
    Check(!serverCreate.empty() && !clientConnect.empty() &&
        serverCreate.find("ChannelName name") != std::string::npos &&
        serverCreate.find("SharedGpuFrameChannelName") == std::string::npos &&
        clientConnect.find("ChannelName name") != std::string::npos &&
        clientConnect.find("SharedGpuFrameChannelName") == std::string::npos,
        "noexcept channel endpoints format names without heap allocation");
    Check(channel.find("SendPresentationControl") != std::string::npos &&
        channel.find("GetNamedPipeClientProcessId") != std::string::npos &&
        channel.find("GetNamedPipeServerProcessId") != std::string::npos,
        "presentation control uses the peer-PID-authenticated pipe");

    const auto consumer = Read(REACTORV_SHARED_GPU_CONSUMER_SOURCE_PATH);
    const auto imageCheck = FunctionBody(consumer, "ExpectedPreloaderImage");
    const auto worker = FunctionBody(
        consumer, "void SharedGpuFrameConsumer::Worker() noexcept");
    Check(!imageCheck.empty() &&
        imageCheck.find("std::array") != std::string::npos &&
        imageCheck.find("std::wstring") == std::string::npos &&
        imageCheck.find("std::filesystem") == std::string::npos,
        "producer image validation is allocation-free inside noexcept worker");
    Check(!worker.empty() && worker.find("try") != std::string::npos &&
        worker.find("catch (...)") != std::string::npos,
        "shared GPU worker contains all C++ failures before thread exit");

    const auto input = Read(REACTORV_INPUT_QUEUE_SOURCE_PATH);
    const auto callback = FunctionBody(input, "InputQueue::WindowProcedure");
    const auto push = FunctionBody(input, "InputQueue::Push");
    Check(!callback.empty() && callback.find("catch (...)") !=
            std::string::npos &&
        !push.empty() && push.find("catch (...)") != std::string::npos,
        "HWND callback drops queue failures and always contains exceptions");

    if (failures == 0) {
        std::cout << "PASS: every public native ABI export contains exceptions\n";
    }
    return failures == 0 ? 0 : 1;
}
