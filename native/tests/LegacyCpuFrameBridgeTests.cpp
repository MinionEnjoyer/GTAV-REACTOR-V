#include "LegacyCpuFrameBridge.h"
#include "SharedGpuFrameChannel.h"
#include <iostream>

using namespace rwui::transport;
int main() {
    int failures{};
    const auto check = [&](bool pass, const char* label) {
        if (!pass) { ++failures; std::cerr << "FAIL: " << label << '\n'; }
    };
    CpuFrameRecoveryBudget budget;
    const auto timeout = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    check(!budget.TryRecover(E_ACCESSDENIED, 0) && !budget.TryRecover(DXGI_ERROR_DEVICE_REMOVED, 0) &&
        !budget.TryRecover(E_ABORT, 0), "identity errors, device loss and cancellation are not retried");
    check(budget.TryRecover(timeout, 0) && budget.TryRecover(timeout, 100) &&
        budget.TryRecover(DXGI_ERROR_WAS_STILL_DRAWING, 200), "three isolated stalls are recoverable");
    check(!budget.TryRecover(S_OK, 201) && !budget.TryRecover(timeout, 9999),
        "success does not replenish bounded recovery budget");
    check(budget.TryRecover(timeout, 10000), "budget replenishes only after the ten-second window");
    WindowsProcessIdentity self{};
    if (!QueryWindowsProcessIdentity(GetCurrentProcessId(), self)) return 1;
    SharedGpuFrameValidationContext v{self.processId, self.processId, self.creationTime, self.creationTime, 123, 456};
    SharedGpuFrameDescriptorV1 f{};
    f.producerProcessId = f.consumerProcessId = self.processId;
    f.producerCreationTime = f.consumerCreationTime = self.creationTime;
    f.sessionIdHigh = 123; f.sessionIdLow = 456;
    f.generation = f.resourceEpoch = 1; f.slotCount = 1;
    f.width = 2; f.height = 2; f.pixelFormat = SharedGpuPixelFormat::Bgra8Unorm;
    f.versionMinor = CpuFrameVersionMinor; f.synchronization = SharedGpuSynchronization::CpuBgraMapping;
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 4096, nullptr);
    if (!mapping) return 1;
    auto* write = static_cast<BYTE*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, 4096));
    if (!write) { CloseHandle(mapping); return 1; }
    for (UINT i = 0; i < 16; ++i) write[i] = static_cast<BYTE>(i * 7);
    f.sharedTextureHandle = reinterpret_cast<UINT_PTR>(mapping);
    {
        CpuFrameMapping read;
        check(read.Open(f, v) == S_OK, "authenticated CPU mapping opens");
        if (read.Pixels()) {
            check(std::memcmp(read.Pixels(), write, 16) == 0, "all BGRA bytes including alpha preserved");
            MEMORY_BASIC_INFORMATION info{};
            check(VirtualQuery(read.Pixels(), &info, sizeof(info)) && info.Protect == PAGE_READONLY,
                "consumer view has no write authority");
        }
        check(read.Open(f, v) == E_INVALIDARG, "mapping object cannot be reopened/leaked");
    }
    {
        auto bad = f; bad.width = 100; bad.height = 100;
        CpuFrameMapping read;
        check(FAILED(read.Open(bad, v)), "small mapping cannot satisfy larger descriptor");
    }
    {
        auto bad = f; auto context = v;
        ++bad.producerCreationTime; ++context.expectedProducerCreationTime;
        CpuFrameMapping read;
        check(read.Open(bad, context) == E_ACCESSDENIED, "actual producer process identity is checked");
    }
    {
        auto bad = f; ++bad.sessionIdLow;
        CpuFrameMapping read;
        check(read.Open(bad, v) == E_INVALIDARG, "foreign session fails before duplicate handle");
    }
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    {
        auto bad = f; bad.sharedTextureHandle = reinterpret_cast<UINT_PTR>(event);
        CpuFrameMapping read;
        check(FAILED(read.Open(bad, v)), "non-mapping NT handle rejected");
    }
    if (event) CloseHandle(event);
    UnmapViewOfFile(write); CloseHandle(mapping);
    check(!LegacyCpuFramesEnabled(GetCurrentProcessId()), "ordinary executable cannot opt into Legacy game route");
    check(CpuFrameLogPath(GetCurrentProcessId(), L"Consumer").empty(), "test executable cannot write game diagnostics");
    LegacyCpuFrameBridge bridge; std::atomic_bool stop{true}; SharedGpuFrameDescriptorV1 output{};
    check(bridge.Convert(f, {}, stop, output) == E_ABORT, "cancelled worker cannot start GPU readback");
    if (!failures) std::cout << "PASS: CPU mapping identity, read-only access, bounds, handle type, opt-in and cancellation\n";
    return failures ? 1 : 0;
}
