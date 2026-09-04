#include "SharedGpuFrameChannel.h"

#include "SharedGpuFrameD3D11.h"

#include <Windows.h>
#include <array>
#include <cwchar>

namespace rwui::transport {
namespace {

constexpr std::uint32_t ChannelMagic = 0x43475652u; // "RVGC"
constexpr std::uint16_t ChannelMajor = 1;
constexpr std::uint16_t ChannelMinor = 2;
constexpr std::uint32_t AcknowledgementMagic = 0x41475652u; // "RVGA"
constexpr std::uint32_t MessageMagic = 0x4D475652u; // "RVGM"
constexpr std::uint32_t PresentationVisibleFlag = 1u;
using ChannelName = std::array<wchar_t, 160>;

struct alignas(8) ChannelHello final {
    std::uint32_t magic{ChannelMagic};
    std::uint16_t major{ChannelMajor};
    std::uint16_t minor{ChannelMinor};
    std::uint32_t byteSize{72};
    std::uint32_t descriptorByteSize{sizeof(SharedGpuFrameDescriptorV1)};
    std::uint32_t producerProcessId{};
    std::uint32_t consumerProcessId{};
    std::uint64_t producerCreationTime{};
    std::uint64_t consumerCreationTime{};
    std::uint64_t sessionIdHigh{};
    std::uint64_t sessionIdLow{};
    std::uint64_t reserved[2]{};
};
static_assert(sizeof(ChannelHello) == 72);

struct alignas(8) ChannelAcknowledgement final {
    std::uint32_t magic{AcknowledgementMagic};
    std::uint16_t major{ChannelMajor};
    std::uint16_t minor{ChannelMinor};
    std::uint32_t byteSize{64};
    SharedGpuFrameAcknowledgement acknowledgement{
        SharedGpuFrameAcknowledgement::Rejected};
    std::uint32_t producerProcessId{};
    std::uint32_t consumerProcessId{};
    std::uint64_t generation{};
    std::uint64_t resourceEpoch{};
    std::uint64_t sessionIdHigh{};
    std::uint64_t sessionIdLow{};
    std::uint64_t consumerCreationTime{};
};
static_assert(sizeof(ChannelAcknowledgement) == 64);

struct alignas(8) ChannelMessage final {
    std::uint32_t magic{MessageMagic};
    std::uint16_t major{ChannelMajor};
    std::uint16_t minor{ChannelMinor};
    std::uint32_t byteSize{200};
    SharedGpuFrameChannelMessageKind kind{};
    std::uint32_t presentationFlags{};
    std::uint32_t reserved32{};
    std::uint64_t presentationEpoch{};
    SharedGpuFrameDescriptorV1 descriptor{};
    std::uint64_t reserved[2]{};
};
static_assert(sizeof(ChannelMessage) == 200);

bool ValidEndpoint(const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    return endpoint.producerProcessId != 0 &&
        endpoint.producerCreationTime != 0 &&
        endpoint.targetConsumerProcessId != 0 &&
        endpoint.targetConsumerCreationTime != 0 &&
        (endpoint.sessionIdHigh != 0 || endpoint.sessionIdLow != 0);
}

bool FormatChannelName(
    const SharedGpuFrameChannelEndpoint& endpoint,
    ChannelName& name) noexcept {
    name = {};
    if (!ValidEndpoint(endpoint)) return false;
    const auto count = std::swprintf(
        name.data(),
        name.size(),
        L"\\\\.\\pipe\\ReactorV.Frame.v1.%08X.%016llX%016llX",
        endpoint.targetConsumerProcessId,
        static_cast<unsigned long long>(endpoint.sessionIdHigh),
        static_cast<unsigned long long>(endpoint.sessionIdLow));
    return count > 0 && static_cast<std::size_t>(count) < name.size();
}

bool WriteExactly(
    HANDLE pipe,
    const void* const data,
    const DWORD byteCount) noexcept {
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (event == nullptr) return false;
    OVERLAPPED overlapped{};
    overlapped.hEvent = event;
    DWORD written{};
    bool complete = WriteFile(
        pipe, data, byteCount, nullptr, &overlapped) != FALSE;
    const auto error = complete ? ERROR_SUCCESS : GetLastError();
    if (!complete && error == ERROR_IO_PENDING) {
        const auto wait = WaitForSingleObject(event, 250);
        if (wait == WAIT_OBJECT_0) {
            complete = GetOverlappedResult(
                pipe, &overlapped, &written, FALSE) != FALSE;
        } else {
            CancelIoEx(pipe, &overlapped);
            WaitForSingleObject(event, INFINITE);
        }
    } else if (complete) {
        complete = GetOverlappedResult(
            pipe, &overlapped, &written, TRUE) != FALSE;
    }
    CloseHandle(event);
    return complete && written == byteCount;
}

bool ReadExactly(
    HANDLE pipe,
    void* const data,
    const DWORD byteCount,
    const DWORD timeoutMilliseconds) noexcept {
    auto* destination = static_cast<std::uint8_t*>(data);
    DWORD remaining = byteCount;
    const auto deadline = GetTickCount64() + timeoutMilliseconds;
    while (remaining != 0) {
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr) return false;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        DWORD read{};
        bool complete = ReadFile(
            pipe, destination, remaining, nullptr, &overlapped) != FALSE;
        const auto error = complete ? ERROR_SUCCESS : GetLastError();
        if (!complete && error == ERROR_IO_PENDING) {
            const auto now = GetTickCount64();
            const auto remainingWait = now >= deadline ? 0u :
                static_cast<DWORD>(deadline - now);
            if (WaitForSingleObject(event, remainingWait) == WAIT_OBJECT_0) {
                complete = GetOverlappedResult(
                    pipe, &overlapped, &read, FALSE) != FALSE;
            } else {
                CancelIoEx(pipe, &overlapped);
                WaitForSingleObject(event, INFINITE);
            }
        } else if (complete) {
            complete = GetOverlappedResult(
                pipe, &overlapped, &read, TRUE) != FALSE;
        }
        CloseHandle(event);
        if (!complete || read == 0) return false;
        destination += read;
        remaining -= read;
    }
    return true;
}

SharedGpuFrameValidationContext ValidationContext(
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    return {
        endpoint.producerProcessId,
        endpoint.targetConsumerProcessId,
        endpoint.producerCreationTime,
        endpoint.targetConsumerCreationTime,
        endpoint.sessionIdHigh,
        endpoint.sessionIdLow,
        SharedGpuFrameMaximumDimension,
        SharedGpuFrameMaximumDimension,
    };
}

ChannelHello Hello(
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    ChannelHello hello{};
    hello.producerProcessId = endpoint.producerProcessId;
    hello.consumerProcessId = endpoint.targetConsumerProcessId;
    hello.producerCreationTime = endpoint.producerCreationTime;
    hello.consumerCreationTime = endpoint.targetConsumerCreationTime;
    hello.sessionIdHigh = endpoint.sessionIdHigh;
    hello.sessionIdLow = endpoint.sessionIdLow;
    return hello;
}

bool ValidHello(
    const ChannelHello& hello,
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    return hello.magic == ChannelMagic &&
        hello.major == ChannelMajor && hello.minor <= ChannelMinor &&
        hello.byteSize == sizeof(ChannelHello) &&
        hello.descriptorByteSize == sizeof(SharedGpuFrameDescriptorV1) &&
        hello.producerProcessId == endpoint.producerProcessId &&
        hello.consumerProcessId == endpoint.targetConsumerProcessId &&
        hello.producerCreationTime == endpoint.producerCreationTime &&
        hello.consumerCreationTime == endpoint.targetConsumerCreationTime &&
        hello.sessionIdHigh == endpoint.sessionIdHigh &&
        hello.sessionIdLow == endpoint.sessionIdLow &&
        hello.reserved[0] == 0 && hello.reserved[1] == 0;
}

ChannelAcknowledgement Acknowledgement(
    const SharedGpuFrameChannelEndpoint& endpoint,
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameAcknowledgement acknowledgement) noexcept {
    ChannelAcknowledgement result{};
    result.acknowledgement = acknowledgement;
    result.producerProcessId = endpoint.producerProcessId;
    result.consumerProcessId = endpoint.targetConsumerProcessId;
    result.generation = descriptor.generation;
    result.resourceEpoch = descriptor.resourceEpoch;
    result.sessionIdHigh = endpoint.sessionIdHigh;
    result.sessionIdLow = endpoint.sessionIdLow;
    result.consumerCreationTime = endpoint.targetConsumerCreationTime;
    return result;
}

bool ValidAcknowledgement(
    const ChannelAcknowledgement& value,
    const SharedGpuFrameChannelEndpoint& endpoint,
    const SharedGpuFrameDescriptorV1& descriptor) noexcept {
    return value.magic == AcknowledgementMagic &&
        value.major == ChannelMajor && value.minor <= ChannelMinor &&
        value.byteSize == sizeof(ChannelAcknowledgement) &&
        (value.acknowledgement == SharedGpuFrameAcknowledgement::Accepted ||
         value.acknowledgement == SharedGpuFrameAcknowledgement::Rejected) &&
        value.producerProcessId == endpoint.producerProcessId &&
        value.consumerProcessId == endpoint.targetConsumerProcessId &&
        value.generation == descriptor.generation &&
        value.resourceEpoch == descriptor.resourceEpoch &&
        value.sessionIdHigh == endpoint.sessionIdHigh &&
        value.sessionIdLow == endpoint.sessionIdLow &&
        value.consumerCreationTime == endpoint.targetConsumerCreationTime;
}

ChannelMessage FrameMessage(
    const SharedGpuFrameDescriptorV1& descriptor) noexcept {
    ChannelMessage result{};
    result.kind = SharedGpuFrameChannelMessageKind::Frame;
    result.descriptor = descriptor;
    return result;
}

ChannelMessage PresentationMessage(
    const SharedGpuPresentationControl& control) noexcept {
    ChannelMessage result{};
    result.kind = SharedGpuFrameChannelMessageKind::PresentationControl;
    result.presentationFlags = control.visible ? PresentationVisibleFlag : 0;
    result.presentationEpoch = control.epoch;
    return result;
}

bool ValidMessageHeader(const ChannelMessage& value) noexcept {
    return value.magic == MessageMagic && value.major == ChannelMajor &&
        value.minor == ChannelMinor && value.byteSize == sizeof(ChannelMessage) &&
        value.reserved32 == 0 && value.reserved[0] == 0 &&
        value.reserved[1] == 0;
}

} // namespace

std::wstring SharedGpuFrameChannelName(
    const SharedGpuFrameChannelEndpoint& endpoint) {
    ChannelName name{};
    return FormatChannelName(endpoint, name)
        ? std::wstring(name.data()) : std::wstring{};
}

const char* SharedGpuFrameChannelErrorName(
    const SharedGpuFrameChannelError error) noexcept {
    switch (error) {
    case SharedGpuFrameChannelError::None: return "none";
    case SharedGpuFrameChannelError::InvalidEndpoint:
        return "invalid_endpoint";
    case SharedGpuFrameChannelError::PipeCreationFailed:
        return "pipe_creation_failed";
    case SharedGpuFrameChannelError::PipeAlreadyOwned:
        return "pipe_already_owned";
    case SharedGpuFrameChannelError::ConnectionTimedOut:
        return "connection_timed_out";
    case SharedGpuFrameChannelError::ConnectionFailed:
        return "connection_failed";
    case SharedGpuFrameChannelError::PeerProcessMismatch:
        return "peer_process_mismatch";
    case SharedGpuFrameChannelError::ProducerIdentityChanged:
        return "producer_identity_changed";
    case SharedGpuFrameChannelError::HandshakeRejected:
        return "handshake_rejected";
    case SharedGpuFrameChannelError::AcknowledgementInvalid:
        return "acknowledgement_invalid";
    case SharedGpuFrameChannelError::DescriptorRejected:
        return "descriptor_rejected";
    case SharedGpuFrameChannelError::IoFailed: return "io_failed";
    default: return "unknown";
    }
}

SharedGpuFrameChannelServer::~SharedGpuFrameChannelServer() {
    Close();
}

SharedGpuFrameChannelError SharedGpuFrameChannelServer::Create(
    const SharedGpuFrameChannelEndpoint& endpoint) noexcept {
    Close();
    if (!ValidEndpoint(endpoint) ||
        endpoint.producerProcessId != GetCurrentProcessId()) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    WindowsProcessIdentity identity{};
    if (!QueryWindowsProcessIdentity(GetCurrentProcessId(), identity) ||
        identity.creationTime != endpoint.producerCreationTime) {
        return SharedGpuFrameChannelError::ProducerIdentityChanged;
    }
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(
            endpoint.targetConsumerProcessId, consumerIdentity) ||
        consumerIdentity.creationTime !=
            endpoint.targetConsumerCreationTime) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    ChannelName name{};
    if (!FormatChannelName(endpoint, name)) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    pipe_ = CreateNamedPipeW(
        name.data(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE |
            FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1,
        sizeof(ChannelMessage) * 4,
        sizeof(ChannelMessage) * 4,
        0,
        nullptr);
    if (pipe_ == INVALID_HANDLE_VALUE) {
        return GetLastError() == ERROR_ACCESS_DENIED
            ? SharedGpuFrameChannelError::PipeAlreadyOwned
            : SharedGpuFrameChannelError::PipeCreationFailed;
    }
    endpoint_ = endpoint;
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError SharedGpuFrameChannelServer::WaitForConsumer(
    const std::uint32_t timeoutMilliseconds) noexcept {
    if (pipe_ == INVALID_HANDLE_VALUE || connected_) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (event == nullptr) return SharedGpuFrameChannelError::ConnectionFailed;
    OVERLAPPED overlapped{};
    overlapped.hEvent = event;
    const bool connectedImmediately = ConnectNamedPipe(pipe_, &overlapped) != FALSE;
    auto error = connectedImmediately ? ERROR_SUCCESS : GetLastError();
    bool connected = connectedImmediately || error == ERROR_PIPE_CONNECTED;
    if (!connected && error == ERROR_IO_PENDING) {
        const auto wait = WaitForSingleObject(event, timeoutMilliseconds);
        if (wait == WAIT_OBJECT_0) {
            DWORD transferred{};
            connected = GetOverlappedResult(
                pipe_, &overlapped, &transferred, FALSE) != FALSE;
        } else {
            CancelIoEx(pipe_, &overlapped);
            WaitForSingleObject(event, INFINITE);
            CloseHandle(event);
            return wait == WAIT_TIMEOUT
                ? SharedGpuFrameChannelError::ConnectionTimedOut
                : SharedGpuFrameChannelError::ConnectionFailed;
        }
    }
    CloseHandle(event);
    if (!connected) return SharedGpuFrameChannelError::ConnectionFailed;

    ULONG clientProcessId{};
    if (GetNamedPipeClientProcessId(pipe_, &clientProcessId) == FALSE ||
        clientProcessId != endpoint_.targetConsumerProcessId) {
        DisconnectNamedPipe(pipe_);
        return SharedGpuFrameChannelError::PeerProcessMismatch;
    }
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(clientProcessId, consumerIdentity) ||
        consumerIdentity.creationTime !=
            endpoint_.targetConsumerCreationTime) {
        DisconnectNamedPipe(pipe_);
        return SharedGpuFrameChannelError::PeerProcessMismatch;
    }
    const auto hello = Hello(endpoint_);
    if (!WriteExactly(pipe_, &hello, sizeof(hello))) {
        DisconnectNamedPipe(pipe_);
        return SharedGpuFrameChannelError::IoFailed;
    }
    connected_ = true;
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError SharedGpuFrameChannelServer::Send(
    const SharedGpuFrameDescriptorV1& descriptor) noexcept {
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    if (ValidateSharedGpuFrame(descriptor, ValidationContext(endpoint_)) !=
        SharedGpuFrameValidationError::None) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    const auto message = FrameMessage(descriptor);
    if (!WriteExactly(pipe_, &message, sizeof(message))) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError
SharedGpuFrameChannelServer::SendPresentationControl(
    const SharedGpuPresentationControl& control) noexcept {
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    if (control.epoch == 0) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    const auto message = PresentationMessage(control);
    if (!WriteExactly(pipe_, &message, sizeof(message))) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError
SharedGpuFrameChannelServer::ReceiveAcknowledgement(
    const SharedGpuFrameDescriptorV1& descriptor,
    SharedGpuFrameAcknowledgement& acknowledgement) noexcept {
    acknowledgement = SharedGpuFrameAcknowledgement::Rejected;
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    ChannelAcknowledgement wire{};
    if (!ReadExactly(pipe_, &wire, sizeof(wire), 500)) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    if (!ValidAcknowledgement(wire, endpoint_, descriptor)) {
        return SharedGpuFrameChannelError::AcknowledgementInvalid;
    }
    acknowledgement = wire.acknowledgement;
    return SharedGpuFrameChannelError::None;
}

void SharedGpuFrameChannelServer::Close() noexcept {
    if (pipe_ != INVALID_HANDLE_VALUE) {
        DisconnectNamedPipe(pipe_);
        CloseHandle(pipe_);
    }
    pipe_ = INVALID_HANDLE_VALUE;
    endpoint_ = {};
    connected_ = false;
}

SharedGpuFrameChannelClient::~SharedGpuFrameChannelClient() {
    Close();
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::Connect(
    const SharedGpuFrameChannelEndpoint& endpoint,
    const std::uint32_t timeoutMilliseconds) noexcept {
    Close();
    if (!ValidEndpoint(endpoint) ||
        endpoint.targetConsumerProcessId != GetCurrentProcessId()) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    WindowsProcessIdentity consumerIdentity{};
    if (!QueryWindowsProcessIdentity(GetCurrentProcessId(), consumerIdentity) ||
        consumerIdentity.creationTime != endpoint.targetConsumerCreationTime) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    ChannelName name{};
    if (!FormatChannelName(endpoint, name)) {
        return SharedGpuFrameChannelError::InvalidEndpoint;
    }
    if (WaitNamedPipeW(name.data(), timeoutMilliseconds) == FALSE) {
        return GetLastError() == ERROR_SEM_TIMEOUT ||
            GetLastError() == ERROR_FILE_NOT_FOUND
            ? SharedGpuFrameChannelError::ConnectionTimedOut
            : SharedGpuFrameChannelError::ConnectionFailed;
    }
    pipe_ = CreateFileW(
        name.data(),
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
        nullptr);
    if (pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    ULONG serverProcessId{};
    if (GetNamedPipeServerProcessId(pipe_, &serverProcessId) == FALSE ||
        serverProcessId != endpoint.producerProcessId) {
        Close();
        return SharedGpuFrameChannelError::PeerProcessMismatch;
    }
    WindowsProcessIdentity producer{};
    if (!QueryWindowsProcessIdentity(endpoint.producerProcessId, producer) ||
        producer.creationTime != endpoint.producerCreationTime) {
        Close();
        return SharedGpuFrameChannelError::ProducerIdentityChanged;
    }
    ChannelHello hello{};
    if (!ReadExactly(pipe_, &hello, sizeof(hello), 500) ||
        !ValidHello(hello, endpoint)) {
        Close();
        return SharedGpuFrameChannelError::HandshakeRejected;
    }
    endpoint_ = endpoint;
    validation_ = ValidationContext(endpoint);
    connected_ = true;
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::Receive(
    SharedGpuFrameDescriptorV1& descriptor) noexcept {
    descriptor = {};
    SharedGpuFrameChannelMessage message{};
    const auto result = ReceiveMessage(message);
    if (result != SharedGpuFrameChannelError::None) return result;
    if (message.kind != SharedGpuFrameChannelMessageKind::Frame) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    descriptor = message.frame;
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::ReceiveMessage(
    SharedGpuFrameChannelMessage& message) noexcept {
    message = {};
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    ChannelMessage wire{};
    if (!ReadExactly(pipe_, &wire, sizeof(wire), 5000)) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    if (!ValidMessageHeader(wire)) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    if (wire.kind == SharedGpuFrameChannelMessageKind::Frame) {
        if (wire.presentationFlags != 0 || wire.presentationEpoch != 0 ||
            ValidateSharedGpuFrame(wire.descriptor, validation_) !=
                SharedGpuFrameValidationError::None) {
            return SharedGpuFrameChannelError::DescriptorRejected;
        }
        message.kind = wire.kind;
        message.frame = wire.descriptor;
        return SharedGpuFrameChannelError::None;
    }
    if (wire.kind ==
            SharedGpuFrameChannelMessageKind::PresentationControl &&
        wire.presentationEpoch != 0 &&
        (wire.presentationFlags & ~PresentationVisibleFlag) == 0) {
        message.kind = wire.kind;
        message.presentation = {
            wire.presentationEpoch,
            (wire.presentationFlags & PresentationVisibleFlag) != 0,
        };
        return SharedGpuFrameChannelError::None;
    }
    return SharedGpuFrameChannelError::DescriptorRejected;
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::TryReceiveMessage(
    SharedGpuFrameChannelMessage& message) noexcept {
    message = {};
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE) {
        return SharedGpuFrameChannelError::ConnectionFailed;
    }
    DWORD available{};
    if (PeekNamedPipe(pipe_, nullptr, 0, nullptr, &available, nullptr) == FALSE) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    if (available < sizeof(ChannelMessage)) {
        return SharedGpuFrameChannelError::ConnectionTimedOut;
    }
    return ReceiveMessage(message);
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::TryReceive(
    SharedGpuFrameDescriptorV1& descriptor) noexcept {
    descriptor = {};
    SharedGpuFrameChannelMessage message{};
    const auto result = TryReceiveMessage(message);
    if (result != SharedGpuFrameChannelError::None) return result;
    if (message.kind != SharedGpuFrameChannelMessageKind::Frame) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    descriptor = message.frame;
    return SharedGpuFrameChannelError::None;
}

SharedGpuFrameChannelError SharedGpuFrameChannelClient::Acknowledge(
    const SharedGpuFrameDescriptorV1& descriptor,
    const SharedGpuFrameAcknowledgement acknowledgement) noexcept {
    if (!connected_ || pipe_ == INVALID_HANDLE_VALUE ||
        (acknowledgement != SharedGpuFrameAcknowledgement::Accepted &&
         acknowledgement != SharedGpuFrameAcknowledgement::Rejected) ||
        ValidateSharedGpuFrame(descriptor, validation_) !=
            SharedGpuFrameValidationError::None) {
        return SharedGpuFrameChannelError::DescriptorRejected;
    }
    const auto wire = Acknowledgement(endpoint_, descriptor, acknowledgement);
    if (!WriteExactly(pipe_, &wire, sizeof(wire))) {
        connected_ = false;
        return SharedGpuFrameChannelError::IoFailed;
    }
    return SharedGpuFrameChannelError::None;
}

void SharedGpuFrameChannelClient::Close() noexcept {
    if (pipe_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_);
    pipe_ = INVALID_HANDLE_VALUE;
    endpoint_ = {};
    validation_ = {};
    connected_ = false;
}

} // namespace rwui::transport
