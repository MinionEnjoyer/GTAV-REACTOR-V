#pragma once

#include "SharedGpuFrameTransport.h"

#include <Windows.h>
#include <cstdint>
#include <string>

namespace rwui::transport {

struct SharedGpuFrameChannelEndpoint final {
    std::uint32_t producerProcessId{};
    std::uint64_t producerCreationTime{};
    std::uint32_t targetConsumerProcessId{};
    std::uint64_t targetConsumerCreationTime{};
    std::uint64_t sessionIdHigh{};
    std::uint64_t sessionIdLow{};
};

std::wstring SharedGpuFrameChannelName(
    const SharedGpuFrameChannelEndpoint& endpoint);

enum class SharedGpuFrameChannelError : std::uint8_t {
    None = 0,
    InvalidEndpoint,
    PipeCreationFailed,
    PipeAlreadyOwned,
    ConnectionTimedOut,
    ConnectionFailed,
    PeerProcessMismatch,
    ProducerIdentityChanged,
    HandshakeRejected,
    AcknowledgementInvalid,
    DescriptorRejected,
    IoFailed,
};

enum class SharedGpuFrameAcknowledgement : std::uint32_t {
    Accepted = 1,
    Rejected = 2,
};

enum class SharedGpuFrameChannelMessageKind : std::uint32_t {
    Frame = 1,
    PresentationControl = 2,
};

struct SharedGpuPresentationControl final {
    std::uint64_t epoch{};
    bool visible{};
};

struct SharedGpuFrameChannelMessage final {
    SharedGpuFrameChannelMessageKind kind{};
    SharedGpuFrameDescriptorV1 frame{};
    SharedGpuPresentationControl presentation{};
};

const char* SharedGpuFrameChannelErrorName(
    SharedGpuFrameChannelError error) noexcept;

// Producer endpoint. WaitForConsumer and Send execute only on the producer's
// control worker; neither belongs in CEF's paint callback.
class SharedGpuFrameChannelServer final {
public:
    SharedGpuFrameChannelServer() = default;
    ~SharedGpuFrameChannelServer();
    SharedGpuFrameChannelServer(const SharedGpuFrameChannelServer&) = delete;
    SharedGpuFrameChannelServer& operator=(
        const SharedGpuFrameChannelServer&) = delete;

    SharedGpuFrameChannelError Create(
        const SharedGpuFrameChannelEndpoint& endpoint) noexcept;
    SharedGpuFrameChannelError WaitForConsumer(
        std::uint32_t timeoutMilliseconds) noexcept;
    SharedGpuFrameChannelError Send(
        const SharedGpuFrameDescriptorV1& descriptor) noexcept;
    SharedGpuFrameChannelError SendPresentationControl(
        const SharedGpuPresentationControl& control) noexcept;
    SharedGpuFrameChannelError ReceiveAcknowledgement(
        const SharedGpuFrameDescriptorV1& descriptor,
        SharedGpuFrameAcknowledgement& acknowledgement) noexcept;
    void Close() noexcept;
    bool Connected() const noexcept { return connected_; }

private:
    HANDLE pipe_{INVALID_HANDLE_VALUE};
    SharedGpuFrameChannelEndpoint endpoint_{};
    bool connected_{};
};

// GTA-side endpoint. Connect and Receive execute on a receiver/import worker.
// Present consumes only the already imported latest frame and never touches
// this blocking control channel.
class SharedGpuFrameChannelClient final {
public:
    SharedGpuFrameChannelClient() = default;
    ~SharedGpuFrameChannelClient();
    SharedGpuFrameChannelClient(const SharedGpuFrameChannelClient&) = delete;
    SharedGpuFrameChannelClient& operator=(
        const SharedGpuFrameChannelClient&) = delete;

    SharedGpuFrameChannelError Connect(
        const SharedGpuFrameChannelEndpoint& endpoint,
        std::uint32_t timeoutMilliseconds) noexcept;
    SharedGpuFrameChannelError Receive(
        SharedGpuFrameDescriptorV1& descriptor) noexcept;
    SharedGpuFrameChannelError TryReceive(
        SharedGpuFrameDescriptorV1& descriptor) noexcept;
    SharedGpuFrameChannelError ReceiveMessage(
        SharedGpuFrameChannelMessage& message) noexcept;
    SharedGpuFrameChannelError TryReceiveMessage(
        SharedGpuFrameChannelMessage& message) noexcept;
    SharedGpuFrameChannelError Acknowledge(
        const SharedGpuFrameDescriptorV1& descriptor,
        SharedGpuFrameAcknowledgement acknowledgement) noexcept;
    void Close() noexcept;
    bool Connected() const noexcept { return connected_; }

private:
    HANDLE pipe_{INVALID_HANDLE_VALUE};
    SharedGpuFrameChannelEndpoint endpoint_{};
    SharedGpuFrameValidationContext validation_{};
    bool connected_{};
};

} // namespace rwui::transport
