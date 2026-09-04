#include "SharedGpuFrameD3D11.h"

#include <Windows.h>
#include <dxgi1_2.h>
#include <limits>
#include <utility>

namespace rwui::transport {
namespace {

std::uint64_t FileTimeValue(const FILETIME& value) noexcept {
    return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32u) |
        static_cast<std::uint64_t>(value.dwLowDateTime);
}

class UniqueHandle final {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(HANDLE value) noexcept : value_(value) {}
    ~UniqueHandle() {
        if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
    }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    HANDLE Get() const noexcept { return value_; }
    HANDLE* Receive() noexcept { return &value_; }

private:
    HANDLE value_{};
};

bool QueryIdentityFromHandle(
    HANDLE process,
    const std::uint32_t processId,
    WindowsProcessIdentity& identity) noexcept {
    FILETIME creation{};
    FILETIME exit{};
    FILETIME kernel{};
    FILETIME user{};
    if (process == nullptr ||
        GetProcessTimes(process, &creation, &exit, &kernel, &user) == FALSE) {
        return false;
    }
    identity = {processId, FileTimeValue(creation)};
    return identity.creationTime != 0;
}

DXGI_FORMAT DxgiFormat(const SharedGpuPixelFormat format) noexcept {
    return static_cast<DXGI_FORMAT>(static_cast<std::uint32_t>(format));
}

} // namespace

bool QueryWindowsProcessIdentity(
    const std::uint32_t processId,
    WindowsProcessIdentity& identity) noexcept {
    identity = {};
    if (processId == 0) return false;
    const UniqueHandle process(OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
        FALSE,
        processId));
    if (process.Get() == nullptr ||
        WaitForSingleObject(process.Get(), 0) != WAIT_TIMEOUT) {
        return false;
    }
    return QueryIdentityFromHandle(process.Get(), processId, identity);
}

const char* SharedGpuD3D11ImportErrorName(
    const SharedGpuD3D11ImportError error) noexcept {
    switch (error) {
    case SharedGpuD3D11ImportError::None: return "none";
    case SharedGpuD3D11ImportError::InvalidArguments:
        return "invalid_arguments";
    case SharedGpuD3D11ImportError::DescriptorRejected:
        return "descriptor_rejected";
    case SharedGpuD3D11ImportError::WrongConsumerProcess:
        return "wrong_consumer_process";
    case SharedGpuD3D11ImportError::ProducerUnavailable:
        return "producer_unavailable";
    case SharedGpuD3D11ImportError::ProducerIdentityChanged:
        return "producer_identity_changed";
    case SharedGpuD3D11ImportError::ConsumerIdentityChanged:
        return "consumer_identity_changed";
    case SharedGpuD3D11ImportError::TextureHandleDuplicationFailed:
        return "texture_handle_duplication_failed";
    case SharedGpuD3D11ImportError::DeviceInterfaceUnavailable:
        return "device_interface_unavailable";
    case SharedGpuD3D11ImportError::SharedTextureOpenFailed:
        return "shared_texture_open_failed";
    case SharedGpuD3D11ImportError::TextureDescriptionMismatch:
        return "texture_description_mismatch";
    case SharedGpuD3D11ImportError::KeyedMutexUnavailable:
        return "keyed_mutex_unavailable";
    case SharedGpuD3D11ImportError::ShaderResourceViewCreationFailed:
        return "shader_resource_view_creation_failed";
    default: return "unknown";
    }
}

ImportedD3D11SharedFrame::~ImportedD3D11SharedFrame() {
    Reset();
}

ImportedD3D11SharedFrame::ImportedD3D11SharedFrame(
    ImportedD3D11SharedFrame&& other) noexcept {
    *this = std::move(other);
}

ImportedD3D11SharedFrame& ImportedD3D11SharedFrame::operator=(
    ImportedD3D11SharedFrame&& other) noexcept {
    if (this == &other) return *this;
    Reset();
    descriptor_ = other.descriptor_;
    texture_ = std::move(other.texture_);
    view_ = std::move(other.view_);
    keyedMutex_ = std::move(other.keyedMutex_);
    acquired_ = other.acquired_;
    other.descriptor_ = {};
    other.acquired_ = false;
    return *this;
}

bool ImportedD3D11SharedFrame::TryAcquireForPresent() noexcept {
    if (keyedMutex_ == nullptr || acquired_) return false;
    const auto result = keyedMutex_->AcquireSync(descriptor_.acquireValue, 0);
    acquired_ = result == S_OK;
    return acquired_;
}

bool ImportedD3D11SharedFrame::ReleaseAfterPresent() noexcept {
    if (keyedMutex_ == nullptr || !acquired_) return false;
    const auto result = keyedMutex_->ReleaseSync(descriptor_.releaseValue);
    acquired_ = false;
    return result == S_OK;
}

bool ImportedD3D11SharedFrame::TryDiscardWithoutPresent() noexcept {
    if (!TryAcquireForPresent()) return false;
    return ReleaseAfterPresent();
}

bool ImportedD3D11SharedFrame::RebindDescriptor(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context) noexcept {
    if (acquired_ || texture_ == nullptr || keyedMutex_ == nullptr ||
        ValidateSharedGpuFrame(descriptor, context) !=
            SharedGpuFrameValidationError::None ||
        descriptor.producerProcessId != descriptor_.producerProcessId ||
        descriptor.consumerProcessId != descriptor_.consumerProcessId ||
        descriptor.producerCreationTime != descriptor_.producerCreationTime ||
        descriptor.consumerCreationTime != descriptor_.consumerCreationTime ||
        descriptor.sessionIdHigh != descriptor_.sessionIdHigh ||
        descriptor.sessionIdLow != descriptor_.sessionIdLow ||
        descriptor.resourceEpoch != descriptor_.resourceEpoch ||
        descriptor.slotIndex != descriptor_.slotIndex ||
        descriptor.slotCount != descriptor_.slotCount ||
        descriptor.width != descriptor_.width ||
        descriptor.height != descriptor_.height ||
        descriptor.pixelFormat != descriptor_.pixelFormat ||
        descriptor.synchronization != descriptor_.synchronization ||
        descriptor.sharedTextureHandle != descriptor_.sharedTextureHandle ||
        descriptor.sharedFenceHandle != descriptor_.sharedFenceHandle) {
        return false;
    }
    descriptor_ = descriptor;
    return true;
}

void ImportedD3D11SharedFrame::Reset() noexcept {
    if (acquired_ && keyedMutex_ != nullptr) {
        keyedMutex_->ReleaseSync(descriptor_.releaseValue);
    }
    acquired_ = false;
    keyedMutex_.Reset();
    view_.Reset();
    texture_.Reset();
    descriptor_ = {};
}

SharedGpuD3D11ImportError ImportD3D11SharedFrame(
    ID3D11Device* const consumerDevice,
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context,
    ImportedD3D11SharedFrame& destination,
    HRESULT* const failureResult) noexcept {
    // Preserve the driver's exact error for field diagnostics. Zero means no
    // failing graphics call (for example, a policy rejection before import).
    if (failureResult != nullptr) *failureResult = S_OK;
    destination.Reset();
    if (consumerDevice == nullptr) {
        return SharedGpuD3D11ImportError::InvalidArguments;
    }
    if (ValidateSharedGpuFrame(descriptor, context) !=
        SharedGpuFrameValidationError::None ||
        descriptor.synchronization !=
            SharedGpuSynchronization::D3d11KeyedMutex) {
        return SharedGpuD3D11ImportError::DescriptorRejected;
    }
    if (context.expectedConsumerProcessId != GetCurrentProcessId()) {
        return SharedGpuD3D11ImportError::WrongConsumerProcess;
    }
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(GetCurrentProcessId(), consumerIdentity) ||
        consumerIdentity.creationTime !=
            context.expectedConsumerCreationTime) {
        return SharedGpuD3D11ImportError::ConsumerIdentityChanged;
    }

    const UniqueHandle producer(OpenProcess(
        PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
        FALSE,
        context.expectedProducerProcessId));
    if (producer.Get() == nullptr ||
        WaitForSingleObject(producer.Get(), 0) != WAIT_TIMEOUT) {
        return SharedGpuD3D11ImportError::ProducerUnavailable;
    }
    WindowsProcessIdentity producerIdentity{};
    if (!QueryIdentityFromHandle(
            producer.Get(),
            context.expectedProducerProcessId,
            producerIdentity) ||
        producerIdentity.creationTime !=
            context.expectedProducerCreationTime) {
        return SharedGpuD3D11ImportError::ProducerIdentityChanged;
    }

    UniqueHandle duplicatedTexture;
    if (DuplicateHandle(
            producer.Get(),
            reinterpret_cast<HANDLE>(
                static_cast<std::uintptr_t>(descriptor.sharedTextureHandle)),
            GetCurrentProcess(),
            duplicatedTexture.Receive(),
            0,
            FALSE,
            DUPLICATE_SAME_ACCESS) == FALSE) {
        if (failureResult != nullptr) *failureResult = HRESULT_FROM_WIN32(GetLastError());
        return SharedGpuD3D11ImportError::TextureHandleDuplicationFailed;
    }

    Microsoft::WRL::ComPtr<ID3D11Device1> device1;
    auto result = consumerDevice->QueryInterface(IID_PPV_ARGS(&device1));
    if (FAILED(result)) {
        if (failureResult != nullptr) *failureResult = result;
        return SharedGpuD3D11ImportError::DeviceInterfaceUnavailable;
    }
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    result = device1->OpenSharedResource1(
        duplicatedTexture.Get(), IID_PPV_ARGS(&texture));
    if (FAILED(result)) {
        if (failureResult != nullptr) *failureResult = result;
        return SharedGpuD3D11ImportError::SharedTextureOpenFailed;
    }

    D3D11_TEXTURE2D_DESC textureDescription{};
    texture->GetDesc(&textureDescription);
    const auto requiredMiscFlags =
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    if (textureDescription.Width != descriptor.width ||
        textureDescription.Height != descriptor.height ||
        textureDescription.Format != DxgiFormat(descriptor.pixelFormat) ||
        textureDescription.MipLevels != 1 ||
        textureDescription.ArraySize != 1 ||
        textureDescription.SampleDesc.Count != 1 ||
        (textureDescription.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0 ||
        (textureDescription.MiscFlags & requiredMiscFlags) !=
            requiredMiscFlags) {
        return SharedGpuD3D11ImportError::TextureDescriptionMismatch;
    }

    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> keyedMutex;
    result = texture.As(&keyedMutex);
    if (FAILED(result)) {
        if (failureResult != nullptr) *failureResult = result;
        return SharedGpuD3D11ImportError::KeyedMutexUnavailable;
    }
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
    result = consumerDevice->CreateShaderResourceView(texture.Get(), nullptr, &view);
    if (FAILED(result)) {
        if (failureResult != nullptr) *failureResult = result;
        return SharedGpuD3D11ImportError::ShaderResourceViewCreationFailed;
    }

    destination.descriptor_ = descriptor;
    destination.texture_ = std::move(texture);
    destination.view_ = std::move(view);
    destination.keyedMutex_ = std::move(keyedMutex);
    return SharedGpuD3D11ImportError::None;
}

} // namespace rwui::transport
