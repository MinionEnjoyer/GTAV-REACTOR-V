#include "SharedGpuFrameChannel.h"
#include "SharedGpuFrameD3D11.h"
#include "SharedGpuFrameProducer.h"

#include <Windows.h>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <iostream>
#include <string>
#include <wrl/client.h>

namespace {

using Microsoft::WRL::ComPtr;
using namespace rwui::transport;

class UniqueHandle final {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(
        HANDLE handle,
        const bool closeOnDestroy = true) noexcept
        : value(handle), closeOnDestroy_(closeOnDestroy) {}
    ~UniqueHandle() {
        if (closeOnDestroy_ &&
            value != nullptr && value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }
    HANDLE value{};

    void SetNonOwning(const HANDLE handle) noexcept {
        value = handle;
        closeOnDestroy_ = false;
    }

private:
    bool closeOnDestroy_{true};
};

bool CreateWarpDevice(
    ComPtr<ID3D11Device>& device,
    ComPtr<ID3D11DeviceContext>& context) {
    D3D_FEATURE_LEVEL level{};
    return SUCCEEDED(D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_WARP,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &device,
        &level,
        &context));
}

bool CreateTransient(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ComPtr<ID3D11Texture2D>& texture,
    UniqueHandle& handle) {
    D3D11_TEXTURE2D_DESC description{};
    description.Width = 2;
    description.Height = 2;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    // CEF's accelerated-paint source is an externally owned shared texture
    // without a keyed mutex. Use the legacy shared-handle form here so this
    // integration test exercises the same import contract and fallback path.
    description.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    if (FAILED(device->CreateTexture2D(
            &description, nullptr, &texture))) return false;
    constexpr std::array<std::uint8_t, 16> pixels{
        73, 2, 3, 255, 73, 5, 6, 255,
        73, 8, 9, 255, 73, 11, 12, 255,
    };
    context->UpdateSubresource(texture.Get(), 0, nullptr, pixels.data(), 8, 0);
    context->Flush();
    ComPtr<IDXGIResource> resource;
    HANDLE sharedHandle{};
    if (FAILED(texture.As(&resource)) ||
        FAILED(resource->GetSharedHandle(&sharedHandle)) ||
        sharedHandle == nullptr || sharedHandle == INVALID_HANDLE_VALUE) {
        return false;
    }
    // A legacy shared-resource handle is not an NT handle and must not be
    // passed to CloseHandle.
    handle.SetNonOwning(sharedHandle);
    return true;
}

bool ReadBlue(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11Texture2D* source,
    std::uint8_t& blue) {
    D3D11_TEXTURE2D_DESC description{};
    source->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(
            &description, nullptr, &staging))) return false;
    context->CopyResource(staging.Get(), source);
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    blue = static_cast<const std::uint8_t*>(mapped.pData)[0];
    context->Unmap(staging.Get(), 0);
    return true;
}

std::uint64_t Parse(const wchar_t* value) {
    return std::wcstoull(value, nullptr, 0);
}

int ChildMain(const int argumentCount, wchar_t** arguments) {
    if (argumentCount != 6) return 40;
    const auto producerPid = static_cast<std::uint32_t>(Parse(arguments[2]));
    const auto producerCreation = Parse(arguments[3]);
    const auto sessionHigh = Parse(arguments[4]);
    const auto sessionLow = Parse(arguments[5]);
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(
            GetCurrentProcessId(), consumerIdentity)) return 46;
    const SharedGpuFrameChannelEndpoint endpoint{
        producerPid,
        producerCreation,
        GetCurrentProcessId(),
        consumerIdentity.creationTime,
        sessionHigh,
        sessionLow,
    };

    SharedGpuFrameChannelClient client;
    if (client.Connect(endpoint, 5000) !=
        SharedGpuFrameChannelError::None) return 41;
    SharedGpuFrameChannelMessage control{};
    if (client.ReceiveMessage(control) !=
            SharedGpuFrameChannelError::None ||
        control.kind !=
            SharedGpuFrameChannelMessageKind::PresentationControl ||
        control.presentation.epoch != 7 ||
        !control.presentation.visible) return 47;
    control = {};
    if (client.ReceiveMessage(control) !=
            SharedGpuFrameChannelError::None ||
        control.kind !=
            SharedGpuFrameChannelMessageKind::PresentationControl ||
        control.presentation.epoch != 8 ||
        control.presentation.visible) return 48;
    SharedGpuFrameDescriptorV1 descriptor{};
    if (client.Receive(descriptor) !=
        SharedGpuFrameChannelError::None) return 42;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    if (!CreateWarpDevice(device, context)) return 125;
    const SharedGpuFrameValidationContext validation{
        producerPid,
        GetCurrentProcessId(),
        producerCreation,
        consumerIdentity.creationTime,
        sessionHigh,
        sessionLow,
        4096,
        4096,
    };
    ImportedD3D11SharedFrame imported;
    if (ImportD3D11SharedFrame(
            device.Get(), descriptor, validation, imported) !=
        SharedGpuD3D11ImportError::None) return 43;
    if (!imported.TryAcquireForPresent()) return 44;
    std::uint8_t blue{};
    const bool pixels = ReadBlue(
        device.Get(), context.Get(), imported.Texture(), blue);
    const bool released = imported.ReleaseAfterPresent();
    const bool acknowledged = client.Acknowledge(
        descriptor, SharedGpuFrameAcknowledgement::Accepted) ==
        SharedGpuFrameChannelError::None;
    SharedGpuFrameDescriptorV1 rejectedDescriptor{};
    const bool rejected = client.Receive(rejectedDescriptor) ==
            SharedGpuFrameChannelError::None &&
        client.Acknowledge(
            rejectedDescriptor, SharedGpuFrameAcknowledgement::Rejected) ==
            SharedGpuFrameChannelError::None;
    return pixels && blue == 73 && released && acknowledged && rejected
        ? 0 : 45;
}

} // namespace

int wmain(const int argumentCount, wchar_t** arguments) {
    if (argumentCount > 1 && std::wstring(arguments[1]) == L"--child") {
        return ChildMain(argumentCount, arguments);
    }

    WindowsProcessIdentity producerIdentity{};
    if (!QueryWindowsProcessIdentity(
            GetCurrentProcessId(), producerIdentity)) return 1;
    constexpr std::uint64_t sessionHigh = 0x13579bdf2468ace0ull;
    constexpr std::uint64_t sessionLow = 0x0eca8642fdb97531ull;

    std::array<wchar_t, 32768> executable{};
    if (GetModuleFileNameW(
            nullptr, executable.data(),
            static_cast<DWORD>(executable.size())) == 0) return 2;
    std::wstring command = L"\"" + std::wstring(executable.data()) +
        L"\" --child " + std::to_wstring(GetCurrentProcessId()) +
        L" " + std::to_wstring(producerIdentity.creationTime) +
        L" " + std::to_wstring(sessionHigh) +
        L" " + std::to_wstring(sessionLow);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (CreateProcessW(
            nullptr,
            command.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_SUSPENDED | CREATE_NO_WINDOW,
            nullptr,
            nullptr,
            &startup,
            &child) == FALSE) return 3;
    UniqueHandle childProcess(child.hProcess);
    UniqueHandle childThread(child.hThread);
    WindowsProcessIdentity childIdentity{};
    if (!QueryWindowsProcessIdentity(child.dwProcessId, childIdentity)) {
        TerminateProcess(childProcess.value, 55);
        return 9;
    }

    const SharedGpuFrameChannelEndpoint endpoint{
        producerIdentity.processId,
        producerIdentity.creationTime,
        child.dwProcessId,
        childIdentity.creationTime,
        sessionHigh,
        sessionLow,
    };
    SharedGpuFrameChannelServer server;
    if (server.Create(endpoint) != SharedGpuFrameChannelError::None) {
        TerminateProcess(childProcess.value, 50);
        return 4;
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> transient;
    UniqueHandle transientHandle;
    D3D11SharedFrameProducer producer;
    if (!CreateWarpDevice(device, context) ||
        !CreateTransient(device.Get(), context.Get(), transient, transientHandle) ||
        !producer.Initialize(
            device.Get(), context.Get(), child.dwProcessId,
            sessionHigh, sessionLow)) {
        TerminateProcess(childProcess.value, 51);
        return 125;
    }

    ResumeThread(childThread.value);
    if (server.WaitForConsumer(5000) !=
        SharedGpuFrameChannelError::None) {
        TerminateProcess(childProcess.value, 52);
        return 5;
    }
    if (server.SendPresentationControl({7, true}) !=
        SharedGpuFrameChannelError::None) {
        TerminateProcess(childProcess.value, 59);
        return 13;
    }
    if (server.SendPresentationControl({8, false}) !=
        SharedGpuFrameChannelError::None) {
        TerminateProcess(childProcess.value, 60);
        return 14;
    }
    SharedGpuFrameDescriptorV1 descriptor{};
    if (producer.SubmitTransientTexture(
            transientHandle.value,
            2,
            2,
            SharedGpuPixelFormat::Bgra8Unorm,
            1,
            descriptor) != SharedGpuProducerSubmitResult::Submitted ||
        server.Send(descriptor) != SharedGpuFrameChannelError::None) {
        TerminateProcess(childProcess.value, 53);
        return 6;
    }
    SharedGpuFrameAcknowledgement acknowledgement{};
    if (server.ReceiveAcknowledgement(descriptor, acknowledgement) !=
            SharedGpuFrameChannelError::None ||
        acknowledgement != SharedGpuFrameAcknowledgement::Accepted) {
        TerminateProcess(childProcess.value, 56);
        return 10;
    }
    SharedGpuFrameDescriptorV1 rejectedDescriptor{};
    if (producer.SubmitTransientTexture(
            transientHandle.value, 2, 2,
            SharedGpuPixelFormat::Bgra8Unorm, 2,
            rejectedDescriptor) != SharedGpuProducerSubmitResult::Submitted ||
        server.Send(rejectedDescriptor) !=
            SharedGpuFrameChannelError::None ||
        server.ReceiveAcknowledgement(
            rejectedDescriptor, acknowledgement) !=
            SharedGpuFrameChannelError::None ||
        acknowledgement != SharedGpuFrameAcknowledgement::Rejected ||
        !producer.TryRecycleUnsent(rejectedDescriptor)) {
        TerminateProcess(childProcess.value, 57);
        return 11;
    }
    SharedGpuFrameDescriptorV1 afterRejection{};
    if (producer.SubmitTransientTexture(
            transientHandle.value, 2, 2,
            SharedGpuPixelFormat::Bgra8Unorm, 3,
            afterRejection) != SharedGpuProducerSubmitResult::Submitted ||
        !producer.TryRecycleUnsent(afterRejection)) {
        TerminateProcess(childProcess.value, 58);
        return 12;
    }
    if (WaitForSingleObject(childProcess.value, 10000) != WAIT_OBJECT_0) {
        TerminateProcess(childProcess.value, 54);
        return 7;
    }
    DWORD childExit{};
    if (GetExitCodeProcess(childProcess.value, &childExit) == FALSE ||
        childExit != 0) {
        std::cerr << "FAIL: cross-process consumer exit=" << childExit << '\n';
        return childExit == 125 ? 125 : 8;
    }

    std::cout << "PASS: target-PID shared GPU channel roundtrip tests\n";
    return 0;
}
