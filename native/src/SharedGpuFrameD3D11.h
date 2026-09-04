#pragma once

#include "SharedGpuFrameTransport.h"

#include <cstdint>
#include <d3d11_1.h>
#include <dxgi.h>
#include <wrl/client.h>

namespace rwui::transport {

struct WindowsProcessIdentity final {
    std::uint32_t processId{};
    std::uint64_t creationTime{};
};

bool QueryWindowsProcessIdentity(
    std::uint32_t processId,
    WindowsProcessIdentity& identity) noexcept;

enum class SharedGpuD3D11ImportError : std::uint8_t {
    None = 0,
    InvalidArguments,
    DescriptorRejected,
    WrongConsumerProcess,
    ProducerUnavailable,
    ProducerIdentityChanged,
    ConsumerIdentityChanged,
    TextureHandleDuplicationFailed,
    DeviceInterfaceUnavailable,
    SharedTextureOpenFailed,
    TextureDescriptionMismatch,
    KeyedMutexUnavailable,
    ShaderResourceViewCreationFailed,
};

const char* SharedGpuD3D11ImportErrorName(
    SharedGpuD3D11ImportError error) noexcept;

// An already imported, persistent COM reference to a producer-owned texture.
// Importing and SRV creation happen on the receiver worker, never in Present.
// Present may call TryAcquireForPresent; it uses a zero-millisecond keyed-mutex
// timeout and therefore fails open instead of blocking the game.
class ImportedD3D11SharedFrame final {
public:
    ImportedD3D11SharedFrame() = default;
    ~ImportedD3D11SharedFrame();

    ImportedD3D11SharedFrame(const ImportedD3D11SharedFrame&) = delete;
    ImportedD3D11SharedFrame& operator=(
        const ImportedD3D11SharedFrame&) = delete;
    ImportedD3D11SharedFrame(ImportedD3D11SharedFrame&& other) noexcept;
    ImportedD3D11SharedFrame& operator=(
        ImportedD3D11SharedFrame&& other) noexcept;

    bool TryAcquireForPresent() noexcept;
    bool ReleaseAfterPresent() noexcept;
    bool TryDiscardWithoutPresent() noexcept;
    // Reuses an already imported slot resource for a newer frame descriptor.
    // Only per-frame generation and keyed-mutex values may change.
    bool RebindDescriptor(
        const SharedGpuFrameDescriptorV1& descriptor,
        const SharedGpuFrameValidationContext& context) noexcept;

    const SharedGpuFrameDescriptorV1& Descriptor() const noexcept {
        return descriptor_;
    }
    ID3D11Texture2D* Texture() const noexcept { return texture_.Get(); }
    ID3D11ShaderResourceView* View() const noexcept { return view_.Get(); }
    bool Acquired() const noexcept { return acquired_; }

private:
    friend SharedGpuD3D11ImportError ImportD3D11SharedFrame(
        ID3D11Device*,
        const SharedGpuFrameDescriptorV1&,
        const SharedGpuFrameValidationContext&,
        ImportedD3D11SharedFrame&, HRESULT*) noexcept;

    void Reset() noexcept;

    SharedGpuFrameDescriptorV1 descriptor_{};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view_;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> keyedMutex_;
    bool acquired_{};
};

SharedGpuD3D11ImportError ImportD3D11SharedFrame(
    ID3D11Device* consumerDevice,
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameValidationContext& context,
    ImportedD3D11SharedFrame& destination,
    HRESULT* failureResult = nullptr) noexcept;

} // namespace rwui::transport
