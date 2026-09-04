#include "SharedGpuFrameConsumer.h"
#include "SharedGpuFrameRuntime.h"

#include <Windows.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <iostream>
#include <string_view>
#include <string>
#include <thread>
#include <vector>
#include <wrl/client.h>

namespace {

using Microsoft::WRL::ComPtr;
using namespace rwui::transport;

int Fail(const int code, const std::string_view stage) {
    std::cerr << "FAIL: SharedGpuFrame.Runtime exit=" << code
              << " stage=" << stage
              << " win32=" << GetLastError() << '\n';
    return code;
}

void PrintDiagnostics(const SharedGpuFrameConsumer& consumer) {
    const auto diagnostics = consumer.Diagnostics();
    std::cerr << "DIAGNOSTIC: consumer_stage="
              << static_cast<std::uint32_t>(diagnostics.stage)
              << " discovery_misses=" << diagnostics.discoveryMisses
              << " image_rejects=" << diagnostics.producerImageRejects
              << " connect_failures=" << diagnostics.connectFailures
              << " receive_failures=" << diagnostics.receiveFailures
              << " copy_failures=" << diagnostics.copyFailures
              << " ack_failures=" << diagnostics.acknowledgementFailures
              << " connected=" << consumer.Connected()
              << " imported=" << consumer.ImportedResourceCount() << '\n';
}

class UniqueHandle final {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(
        HANDLE value,
        const bool closeOnDestroy = true) noexcept
        : value_(value), closeOnDestroy_(closeOnDestroy) {}
    ~UniqueHandle() {
        if (closeOnDestroy_ && value_ != nullptr &&
            value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
    }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    HANDLE Get() const noexcept { return value_; }
    HANDLE* Receive(const bool closeOnDestroy = true) noexcept {
        closeOnDestroy_ = closeOnDestroy;
        return &value_;
    }

private:
    HANDLE value_{};
    bool closeOnDestroy_{true};
};

std::wstring EventName(
    const wchar_t* suffix,
    const std::uint32_t targetProcessId) {
    return L"Local\\ReactorV.SharedGpuFrame.RuntimeTest." +
        std::to_wstring(targetProcessId) + L"." + suffix;
}

bool CreateHardwareDevice(
    ComPtr<ID3D11Device>& device,
    ComPtr<ID3D11DeviceContext>& context,
    const LUID* const requestedAdapter = nullptr) {
    D3D_FEATURE_LEVEL level{};
    if (requestedAdapter != nullptr) {
        ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
        for (UINT index = 0;; ++index) {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(index, &adapter) ==
                DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 description{};
            if (FAILED(adapter->GetDesc1(&description)) ||
                description.AdapterLuid.HighPart !=
                    requestedAdapter->HighPart ||
                description.AdapterLuid.LowPart !=
                    requestedAdapter->LowPart) continue;
            return SUCCEEDED(D3D11CreateDevice(
                adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                D3D11_SDK_VERSION, &device, &level, &context));
        }
        return false;
    }
    return SUCCEEDED(D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &device,
        &level,
        &context));
}

bool CreateTransientTexture(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ComPtr<ID3D11Texture2D>& texture,
    UniqueHandle& sharedHandle, UINT width = 2, UINT height = 2) {
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width;
    description.Height = height;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    description.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    if (FAILED(device->CreateTexture2D(
            &description, nullptr, &texture))) return false;

    constexpr std::array<std::uint8_t, 16> sample{
        73, 2, 3, 255, 73, 5, 6, 255,
        73, 8, 9, 255, 73, 11, 12, 255,
    };
    std::vector<std::uint8_t> pixels(size_t(width) * height * 4);
    for (size_t i = 0; i < pixels.size(); ++i) pixels[i] =
        width > 2 ? static_cast<std::uint8_t>(i % 251) : sample[i % sample.size()];
    context->UpdateSubresource(texture.Get(), 0, nullptr, pixels.data(), width * 4, 0);
    context->Flush();

    ComPtr<IDXGIResource> resource;
    return SUCCEEDED(texture.As(&resource)) &&
        SUCCEEDED(resource->GetSharedHandle(
            sharedHandle.Receive(false)));
}

bool ReadBlue(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11ShaderResourceView* view,
    std::uint8_t& blue, bool verifyResized = false) {
    ComPtr<ID3D11Resource> resource;
    view->GetResource(&resource);
    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(resource.As(&texture))) return false;
    D3D11_TEXTURE2D_DESC description{};
    texture->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(
            &description, nullptr, &staging))) return false;
    context->CopyResource(staging.Get(), texture.Get());
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    blue = static_cast<const std::uint8_t*>(mapped.pData)[0];
    bool valid = true;
    if (verifyResized) {
        valid = description.Width == 33 && description.Height == 7;
        for (UINT y = 0; y < description.Height; ++y)
            for (UINT x = 0; x < description.Width * 4; ++x)
                valid = valid && static_cast<const std::uint8_t*>(mapped.pData)[size_t(y) * mapped.RowPitch + x] ==
                    static_cast<std::uint8_t>((size_t(y) * description.Width * 4 + x) % 251);
    }
    context->Unmap(staging.Get(), 0);
    return valid;
}

bool VerifyLifecycleBindingCannotBeLost(
    ID3D11Device* const device,
    ID3D11DeviceContext* const context) {
    SharedGpuFrameConsumer consumer;
    if (!consumer.BindDevice(device, context) ||
        consumer.BindDevice(nullptr, context) ||
        consumer.BindDevice(device, nullptr)) return false;

    auto blocksBehindLease = [&](auto&& operation) {
        std::atomic_bool started{};
        std::atomic_bool completed{};
        std::thread lifecycle;
        bool waited{};
        {
            auto lease = consumer.TryAcquireLatestForPresent();
            if (!lease.OwnsContext()) return false;
            lifecycle = std::thread([&] {
                started.store(true, std::memory_order_release);
                operation();
                completed.store(true, std::memory_order_release);
            });
            while (!started.load(std::memory_order_acquire)) {
                SwitchToThread();
            }
            Sleep(20);
            waited = !completed.load(std::memory_order_acquire);
        }
        lifecycle.join();
        return waited && completed.load(std::memory_order_acquire);
    };

    const bool unbindCommitted = blocksBehindLease(
        [&] { consumer.UnbindDevice(); });
    std::atomic_bool bindSucceeded{};
    const bool bindCommitted = blocksBehindLease(
        [&] { bindSucceeded.store(
            consumer.BindDevice(device, context), std::memory_order_release); });
    consumer.Stop();
    return unbindCommitted && bindCommitted &&
        bindSucceeded.load(std::memory_order_acquire);
}

int ProducerMain(
    const std::uint32_t targetProcessId,
    const std::uint64_t generation,
    const LUID* const requestedAdapter, bool cpuBridge = false, bool expectRejected = false, bool cpuRecovery = false) {
    const auto readyName = EventName(L"Ready", targetProcessId);
    const auto acknowledgedName = EventName(L"Acknowledged", targetProcessId);
    const auto advanceName = EventName(L"Advance", targetProcessId);
    const auto sequenceCompleteName = EventName(
        L"SequenceComplete", targetProcessId);
    const auto recoveredName = EventName(L"Recovered", targetProcessId);
    const auto demotedName = EventName(L"Demoted", targetProcessId);
    const auto doneName = EventName(L"Done", targetProcessId);
    UniqueHandle ready(OpenEventW(EVENT_MODIFY_STATE, FALSE, readyName.c_str()));
    UniqueHandle acknowledged(OpenEventW(
        EVENT_MODIFY_STATE, FALSE, acknowledgedName.c_str()));
    UniqueHandle advance(OpenEventW(SYNCHRONIZE, FALSE, advanceName.c_str()));
    UniqueHandle sequenceComplete(OpenEventW(
        EVENT_MODIFY_STATE, FALSE, sequenceCompleteName.c_str()));
    UniqueHandle recovered(OpenEventW(
        EVENT_MODIFY_STATE, FALSE, recoveredName.c_str()));
    UniqueHandle demoted(OpenEventW(
        EVENT_MODIFY_STATE, FALSE, demotedName.c_str()));
    UniqueHandle done(OpenEventW(SYNCHRONIZE, FALSE, doneName.c_str()));
    const bool sequenceFixture = requestedAdapter == nullptr;
    if (ready.Get() == nullptr || acknowledged.Get() == nullptr ||
        done.Get() == nullptr ||
        (sequenceFixture && (advance.Get() == nullptr ||
            sequenceComplete.Get() == nullptr || recovered.Get() == nullptr ||
            demoted.Get() == nullptr))) {
        return Fail(40, "producer_open_events");
    }

    auto& runtime = GlobalSharedGpuFrameProducerRuntime();
    if (cpuBridge) runtime.EnableCpuBridgeForTesting();
    if (!runtime.Start(targetProcessId)) return Fail(41, "producer_start");
    if (!runtime.SetPresentationVisible(true)) {
        runtime.Stop();
        return Fail(44, "producer_publish_visible_control");
    }
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> transient;
    UniqueHandle sharedHandle;
    if (!CreateHardwareDevice(device, context, requestedAdapter) ||
        !CreateTransientTexture(
            device.Get(), context.Get(), transient, sharedHandle)) {
        runtime.Stop();
        return 125;
    }

    // Deliberately probe before the consumer attaches and provide a stale
    // browser-size expectation. Adapter discovery and the persistent frame
    // must use the opened 2x2 texture description, independently of channel
    // connection.
    if (!runtime.SubmitTransient(
            sharedHandle.Get(), 640, 360,
            SharedGpuPixelFormat::Bgra8Unorm, generation)) {
        runtime.Stop();
        return Fail(42, "producer_first_probe");
    }
    const auto firstDiagnostics = runtime.Diagnostics();
    if (firstDiagnostics.lastStatus !=
            RwuiSharedTextureSubmitStatus::Submitted ||
        firstDiagnostics.submitted != 1 ||
        firstDiagnostics.submitAttempts != 1 ||
        firstDiagnostics.lastSubmittedGeneration != generation ||
        firstDiagnostics.adapterDescription[0] == L'\0') {
        runtime.Stop();
        return Fail(45, "producer_first_diagnostics");
    }
    SetEvent(ready.Get());
    const auto deadline = GetTickCount64() + 15000;
    DWORD wait{WAIT_TIMEOUT};
    bool secondSubmitted{};
    bool thirdSubmitted{};
    bool recoverableFailureProbed{};
    bool recoverySubmitted{};
    bool hardFailureProbed{};
    while (GetTickCount64() < deadline) {
        const auto acknowledgedGeneration =
            runtime.LastAcknowledgedGeneration();
        if (acknowledgedGeneration >= generation) {
            SetEvent(acknowledged.Get());
        }
        if (advance.Get() != nullptr &&
            WaitForSingleObject(advance.Get(), 0) == WAIT_OBJECT_0) {
            if (!secondSubmitted && acknowledgedGeneration >= generation) {
                if (cpuRecovery) runtime.FailNextCpuReadbackForTesting();
                if (cpuBridge && !CreateTransientTexture(device.Get(), context.Get(), transient, sharedHandle, 33, 7)) {
                    runtime.Stop(); return Fail(47, "cpu_resize_source");
                }
                secondSubmitted = runtime.SubmitTransient(
                    sharedHandle.Get(), 2, 2,
                    SharedGpuPixelFormat::Bgra8Unorm, generation + 1);
            } else if (secondSubmitted && !thirdSubmitted &&
                (acknowledgedGeneration >= generation + 1 ||
                    (cpuRecovery && runtime.CpuRecoveryCountForTesting() == 1))) {
                thirdSubmitted = runtime.SubmitTransient(
                    sharedHandle.Get(), 2, 2,
                    SharedGpuPixelFormat::Bgra8Unorm, generation + 2);
            } else if (thirdSubmitted &&
                acknowledgedGeneration >= generation + 2 &&
                !recoverableFailureProbed) {
                recoverableFailureProbed = true;
                runtime.FailNextPoolReleaseForTesting();
                const auto failure = runtime.SubmitTransientStatus(
                    sharedHandle.Get(), 2, 2,
                    SharedGpuPixelFormat::Bgra8Unorm,
                    generation + 3);
                if (failure == RwuiSharedTextureSubmitStatus::
                        DeviceOrCopyFailure &&
                    runtime.Bound() && runtime.AcceleratedReady()) {
                    recoverySubmitted = runtime.SubmitTransientStatus(
                        sharedHandle.Get(), 2, 2,
                        SharedGpuPixelFormat::Bgra8Unorm,
                        generation + 4) ==
                        RwuiSharedTextureSubmitStatus::Submitted;
                }
            } else if (recoverySubmitted &&
                acknowledgedGeneration >= generation + 4 &&
                !hardFailureProbed) {
                hardFailureProbed = true;
                if (recovered.Get() != nullptr) SetEvent(recovered.Get());
                if (sequenceComplete.Get() != nullptr) {
                    SetEvent(sequenceComplete.Get());
                }
                const auto status = runtime.SubmitTransientStatus(
                    ready.Get(), 2, 2,
                    SharedGpuPixelFormat::Bgra8Unorm,
                    generation + 5);
                if (status == RwuiSharedTextureSubmitStatus::
                        AdapterOrResourceInvalid &&
                    !runtime.Bound() && !runtime.AcceleratedReady() &&
                    demoted.Get() != nullptr) {
                    SetEvent(demoted.Get());
                }
            }
        }
        wait = WaitForSingleObject(done.Get(), 10);
        if (wait == WAIT_OBJECT_0) break;
    }
    const auto finalDiagnostics = runtime.Diagnostics();
    const bool diagnosticsComplete = wait != WAIT_OBJECT_0 || (expectRejected
        ? finalDiagnostics.acknowledgementsRejected >= 1 && finalDiagnostics.lastAcknowledgedGeneration == 0
        :
        (finalDiagnostics.acknowledgementsAccepted >= 1 &&
         finalDiagnostics.lastAcknowledgedGeneration >= generation &&
         finalDiagnostics.adapterVendorId != 0));
    const bool recoveryValid = !cpuRecovery || (runtime.CpuRecoveryCountForTesting() == 1 &&
        finalDiagnostics.acknowledgementsAccepted == 3 && finalDiagnostics.acknowledgementsRejected == 0);
    runtime.Stop();
    if (!recoveryValid) return Fail(48, "cpu_timeout_retired_no_false_ack_then_recovered");
    if (!diagnosticsComplete) {
        return Fail(46, "producer_acknowledgement_diagnostics");
    }
    return wait == WAIT_OBJECT_0 ? 0 :
        Fail(43, "producer_wait_for_parent_done");
}

std::wstring SiblingPreloaderPath() {
    std::array<wchar_t, 32768> executable{};
    const auto length = GetModuleFileNameW(
        nullptr, executable.data(), static_cast<DWORD>(executable.size()));
    if (length == 0 || length >= executable.size()) return {};
    std::wstring path(executable.data(), length);
    const auto separator = path.find_last_of(L"\\/");
    if (separator == std::wstring::npos) return {};
    path.resize(separator + 1);
    path += L"ReactorV.Preloader.exe";
    return path;
}

ID3D11Device* rejectedGameDevice{};
std::atomic<unsigned> rejectedGameImports{};
SharedGpuD3D11ImportError RejectGameNtImport(ID3D11Device* device,
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& validation,
    ImportedD3D11SharedFrame& destination, HRESULT* hr) noexcept {
    if (device == rejectedGameDevice) {
        rejectedGameImports.fetch_add(1);
        *hr = E_INVALIDARG;
        return SharedGpuD3D11ImportError::SharedTextureOpenFailed;
    }
    return ImportD3D11SharedFrame(device, descriptor, validation, destination, hr);
}

int ParentMain(bool rejectGameNt = false, bool cpuBridge = false, bool denyCpu = false, bool cpuRecovery = false) {
    if (!SharedGpuFrameConsumer::ShouldBridge(true,
            SharedGpuD3D11ImportError::SharedTextureOpenFailed, E_INVALIDARG) ||
        SharedGpuFrameConsumer::ShouldBridge(false,
            SharedGpuD3D11ImportError::SharedTextureOpenFailed, E_INVALIDARG) ||
        SharedGpuFrameConsumer::ShouldBridge(true,
            SharedGpuD3D11ImportError::DescriptorRejected, E_INVALIDARG) ||
        SharedGpuFrameConsumer::ShouldBridge(true,
            SharedGpuD3D11ImportError::SharedTextureOpenFailed, E_ACCESSDENIED))
        return Fail(22, "legacy_bridge_fail_closed_policy");
    if (SharedGpuDiscoveryPollDelayMs(0) != 50 ||
        SharedGpuDiscoveryPollDelayMs(39) != 50 ||
        SharedGpuDiscoveryPollDelayMs(40) != 250 ||
        SharedGpuDiscoveryPollDelayMs(79) != 250 ||
        SharedGpuDiscoveryPollDelayMs(80) != 1000 ||
        SharedGpuDiscoveryPollDelayMs(10000) != 1000) {
        return Fail(19, "parent_discovery_backoff_policy");
    }
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    if (!CreateHardwareDevice(device, context)) return 125;
    if (!VerifyLifecycleBindingCannotBeLost(device.Get(), context.Get())) {
        return Fail(16, "parent_lifecycle_binding_contention");
    }

    const auto targetProcessId = GetCurrentProcessId();
    const auto readyName = EventName(L"Ready", targetProcessId);
    const auto acknowledgedName = EventName(L"Acknowledged", targetProcessId);
    const auto advanceName = EventName(L"Advance", targetProcessId);
    const auto sequenceCompleteName = EventName(
        L"SequenceComplete", targetProcessId);
    const auto recoveredName = EventName(L"Recovered", targetProcessId);
    const auto demotedName = EventName(L"Demoted", targetProcessId);
    const auto doneName = EventName(L"Done", targetProcessId);
    UniqueHandle ready(CreateEventW(nullptr, TRUE, FALSE, readyName.c_str()));
    UniqueHandle acknowledged(CreateEventW(
        nullptr, TRUE, FALSE, acknowledgedName.c_str()));
    UniqueHandle advance(CreateEventW(
        nullptr, TRUE, FALSE, advanceName.c_str()));
    UniqueHandle sequenceComplete(CreateEventW(
        nullptr, TRUE, FALSE, sequenceCompleteName.c_str()));
    UniqueHandle recovered(CreateEventW(
        nullptr, TRUE, FALSE, recoveredName.c_str()));
    UniqueHandle demoted(CreateEventW(
        nullptr, TRUE, FALSE, demotedName.c_str()));
    UniqueHandle done(CreateEventW(nullptr, TRUE, FALSE, doneName.c_str()));
    if (ready.Get() == nullptr || acknowledged.Get() == nullptr ||
        advance.Get() == nullptr || sequenceComplete.Get() == nullptr ||
        recovered.Get() == nullptr || demoted.Get() == nullptr ||
        done.Get() == nullptr) {
        return Fail(1, "parent_create_events");
    }

    const auto helper = SiblingPreloaderPath();
    if (helper.empty() || GetFileAttributesW(helper.c_str()) ==
        INVALID_FILE_ATTRIBUTES) return Fail(2, "parent_find_helper");
    std::wstring command = L"\"" + helper + (cpuRecovery ? L"\" --producer-cpu-recovery " : denyCpu ? L"\" --producer-cpu-rejected " :
        cpuBridge ? L"\" --producer-cpu " : L"\" --producer ") +
        std::to_wstring(targetProcessId);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (CreateProcessW(
            helper.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child) == FALSE) {
        return Fail(3, "parent_create_helper");
    }
    UniqueHandle childProcess(child.hProcess);
    UniqueHandle childThread(child.hThread);
    if (WaitForSingleObject(ready.Get(), 5000) != WAIT_OBJECT_0) {
        TerminateProcess(childProcess.Get(), 50);
        return Fail(4, "parent_wait_first_probe");
    }

    rejectedGameDevice = rejectGameNt ? device.Get() : nullptr;
    SharedGpuFrameConsumer consumer(rejectGameNt ? RejectGameNtImport : ImportD3D11SharedFrame);
    if (cpuBridge && !denyCpu) consumer.EnableCpuBridgeForTesting();
    if (!consumer.Arm()) {
        TerminateProcess(childProcess.Get(), 51);
        return Fail(5, "parent_arm_consumer");
    }
    if (!consumer.BindDevice(device.Get(), context.Get(), rejectGameNt)) {
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        return Fail(12, "parent_enable_multithread");
    }
    ComPtr<ID3D11Multithread> multithread;
    if (FAILED(context.As(&multithread)) ||
        multithread->GetMultithreadProtected() == FALSE) {
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        return Fail(12, "parent_enable_multithread");
    }

    if (denyCpu) {
        const auto end = GetTickCount64() + 3000;
        while (consumer.Diagnostics().acknowledgementsRejected == 0 && GetTickCount64() < end) Sleep(5);
        const auto d = consumer.Diagnostics();
        bool empty{};
        { auto lease = consumer.TryAcquireLatestForPresent(); empty = lease.OwnsContext() && !lease; }
        SetEvent(done.Get());
        const auto exited = WaitForSingleObject(childProcess.Get(), 3000);
        DWORD exitCode{}; GetExitCodeProcess(childProcess.Get(), &exitCode);
        consumer.Stop();
        if (exited != WAIT_OBJECT_0) TerminateProcess(childProcess.Get(), 53);
        return empty && d.acknowledgementsRejected == 1 && d.publishedFrames == 0 &&
            d.lastImportHresult == static_cast<UINT>(E_ACCESSDENIED) && exitCode == 0
            ? 0 : Fail(24, "cpu_requires_explicit_legacy_opt_in");
    }
    ID3D11ShaderResourceView* firstView{};
    bool firstObserved{};
    bool presentationObserved{};
    const auto deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline) {
        presentationObserved = presentationObserved ||
            (consumer.ExternalPresentationVisible() &&
             consumer.ExternalPresentationEpoch() >= 2);
        {
            auto lease = consumer.TryAcquireLatestForPresent();
            if (lease.OwnsContext() && lease && lease.Generation() == 77) {
                std::uint8_t blue{};
                firstView = lease.View();
                firstObserved = ReadBlue(
                    device.Get(), context.Get(), lease.View(), blue) &&
                    blue == 73;
                break;
            }
        }
        // Never sleep while retaining the immediate-context gate. Real
        // Present releases this lease as soon as its bounded draw finishes.
        Sleep(5);
    }
    if (!firstObserved) {
        PrintDiagnostics(consumer);
        std::cerr << "DIAGNOSTIC: acknowledged_event="
                  << (WaitForSingleObject(acknowledged.Get(), 0) ==
                      WAIT_OBJECT_0)
                  << " child_state="
                  << WaitForSingleObject(childProcess.Get(), 0) << '\n';
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        return Fail(6, "parent_observe_first_frame");
    }
    if (!presentationObserved) {
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        return Fail(20, "parent_observe_authenticated_presentation_control");
    }
    if (WaitForSingleObject(acknowledged.Get(), 2000) != WAIT_OBJECT_0) {
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        return Fail(11, "parent_wait_first_ack");
    }

    // No second producer frame exists. Every later Present must still see the
    // same consumer-owned SRV/generation instead of flickering to no overlay.
    bool repeatedPresent{};
    {
        auto lease = consumer.TryAcquireLatestForPresent();
        repeatedPresent = lease.OwnsContext() && lease &&
            lease.Generation() == 77 && lease.View() == firstView;
    }

    SetEvent(advance.Get());
    if (WaitForSingleObject(sequenceComplete.Get(), 5000) != WAIT_OBJECT_0) {
        PrintDiagnostics(consumer);
        consumer.Stop();
        SetEvent(done.Get());
        WaitForSingleObject(childProcess.Get(), 2000);
        std::cerr << "DIAGNOSTIC: first_view=" << firstView << '\n';
        return Fail(13, "parent_wait_three_frame_sequence");
    }
    const bool hardFailureDemoted =
        WaitForSingleObject(demoted.Get(), 2000) == WAIT_OBJECT_0;
    const bool oneOffCopyFailureRecovered =
        WaitForSingleObject(recovered.Get(), 0) == WAIT_OBJECT_0;
    bool thirdObserved{};
    const auto thirdDeadline = GetTickCount64() + 2000;
    while (GetTickCount64() < thirdDeadline) {
        {
            auto lease = consumer.TryAcquireLatestForPresent();
            if (lease.OwnsContext() && lease && lease.Generation() >= 79) {
                std::uint8_t blue{};
                thirdObserved = !cpuBridge || ReadBlue(device.Get(), context.Get(), lease.View(), blue, true);
                break;
            }
        }
        Sleep(2);
    }
    // Frames 77-79 reuse the two stable slots. The fault-injected keyed
    // ReleaseSync failure then deliberately retires one slot, so recovery may
    // import exactly one replacement resource/epoch and no more.
    const bool slotCacheReused = thirdObserved &&
        consumer.ImportedResourceCount() == (cpuBridge ? 0 : 3);

    SetEvent(done.Get());
    if (WaitForSingleObject(childProcess.Get(), 5000) != WAIT_OBJECT_0) {
        TerminateProcess(childProcess.Get(), 52);
        return Fail(7, "parent_wait_helper_exit");
    }
    DWORD childExit{};
    GetExitCodeProcess(childProcess.Get(), &childExit);
    if (childExit == 125) return 125;
    if (childExit != 0) {
        std::cerr << "DIAGNOSTIC: child_exit=" << childExit << '\n';
        return Fail(8, "parent_helper_failed");
    }
    bool clearedAfterDisconnect{};
    const auto clearDeadline = GetTickCount64() + 2000;
    while (GetTickCount64() < clearDeadline) {
        {
            auto lease = consumer.TryAcquireLatestForPresent();
            if (lease.OwnsContext() && !lease &&
                !consumer.ExternalPresentationVisible() &&
                consumer.ExternalPresentationEpoch() == 0) {
                clearedAfterDisconnect = true;
                break;
            }
        }
        Sleep(2);
    }
    const auto stopStarted = std::chrono::steady_clock::now();
    const auto finalConsumerDiagnostics = consumer.Diagnostics();
    const bool compatibilityValid = rejectGameNt
        ? consumer.LegacyBridgeActive() && consumer.LegacyBridgedFrames() >= 3 &&
          consumer.LegacyDirectFailure() == static_cast<UINT>(E_INVALIDARG) &&
          rejectedGameImports.load() == 1
        : !consumer.LegacyBridgeActive() && consumer.LegacyBridgedFrames() == 0;
    consumer.Stop();
    if (!compatibilityValid) return Fail(23, "legacy_bridge_route_and_reuse");
    const auto stopElapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopStarted);
    if (!repeatedPresent) return Fail(9, "parent_repeated_present");
    if (stopElapsed.count() >= 500) {
        std::cerr << "DIAGNOSTIC: stop_ms=" << stopElapsed.count() << '\n';
        return Fail(10, "parent_bounded_stop");
    }
    if (!slotCacheReused) {
        std::cerr << "DIAGNOSTIC: third_observed=" << thirdObserved
                  << " imported=" << consumer.ImportedResourceCount() << '\n';
        return Fail(14, "parent_slot_cache_reuse");
    }
    if (!hardFailureDemoted) {
        return Fail(17, "parent_hard_failure_demotion");
    }
    if (!oneOffCopyFailureRecovered) {
        return Fail(18, "parent_recoverable_copy_failure");
    }
    if (!clearedAfterDisconnect) {
        return Fail(15, "parent_disconnect_clear");
    }
    if (finalConsumerDiagnostics.receivedFrames < 3 ||
        finalConsumerDiagnostics.publishedFrames < 3 ||
        finalConsumerDiagnostics.acknowledgementsAccepted < 3 ||
        (!cpuBridge && finalConsumerDiagnostics.importedResources < 2) ||
        finalConsumerDiagnostics.lastReceivedGeneration < 79 ||
        finalConsumerDiagnostics.lastPublishedGeneration < 79) {
        PrintDiagnostics(consumer);
        return Fail(21, "parent_consumer_diagnostics");
    }

    std::cout <<
        "PASS: external probe, cached slots, persistent latest frame, "
        "disconnect clearing, and bounded shutdown\n";
    return 0;
}

} // namespace

int wmain(const int argumentCount, wchar_t** arguments) {
    if (argumentCount == 3 && std::wstring(arguments[1]) == L"--producer-cpu-recovery")
        return ProducerMain(static_cast<std::uint32_t>(std::wcstoul(arguments[2], nullptr, 10)), 77, nullptr, true, false, true);
    if (argumentCount == 2 && std::wstring(arguments[1]) == L"--legacy-cpu-recovery")
        return ParentMain(false, true, false, true);
    if (argumentCount == 3 && std::wstring(arguments[1]) == L"--producer-cpu-rejected") {
        return ProducerMain(static_cast<std::uint32_t>(std::wcstoul(arguments[2], nullptr, 10)), 77, nullptr, true, true);
    }
    if (argumentCount == 2 && std::wstring(arguments[1]) == L"--legacy-cpu-blocked") {
        return ParentMain(false, true, true);
    }
    if (argumentCount == 3 && std::wstring(arguments[1]) == L"--producer-cpu") {
        return ProducerMain(static_cast<std::uint32_t>(std::wcstoul(arguments[2], nullptr, 10)), 77, nullptr, true);
    }
    if (argumentCount == 2 && std::wstring(arguments[1]) == L"--legacy-cpu") {
        return ParentMain(false, true);
    }
    if ((argumentCount == 3 || argumentCount == 6) &&
        std::wstring(arguments[1]) == L"--producer") {
        const auto targetProcessId = static_cast<std::uint32_t>(
            std::wcstoul(arguments[2], nullptr, 10));
        if (argumentCount == 3) {
            return ProducerMain(targetProcessId, 77, nullptr);
        }
        const auto generation = std::wcstoull(arguments[3], nullptr, 10);
        LUID adapter{};
        adapter.HighPart = static_cast<LONG>(
            std::wcstol(arguments[4], nullptr, 10));
        adapter.LowPart = static_cast<DWORD>(
            std::wcstoul(arguments[5], nullptr, 10));
        return ProducerMain(targetProcessId, generation, &adapter);
    }
    return ParentMain(argumentCount == 2 && std::wstring(arguments[1]) == L"--legacy-nt-rejection");
}
