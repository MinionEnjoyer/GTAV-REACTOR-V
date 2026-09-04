#include "LegacyStartupShadowProbe.h"

#include <windows.h>
#include <winver.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <cwchar>
#include <limits>
#include <memory>
#include <new>
#include <span>
#include <vector>

namespace reactorv::bootstrap {
namespace {

constexpr std::uint16_t Amd64Machine = 0x8664;
constexpr std::uint32_t Legacy3889ProfileRevision = 2;
constexpr std::uint32_t Legacy3889PeTimestamp = 0x6A4F9ED3;
constexpr std::uint32_t Legacy3889SizeOfImage = 0x03E5BC00;
constexpr LegacySignatureMask InitStatePatternBit = 1ULL << 0U;
constexpr LegacySignatureMask InitStateTargetBit = 1ULL << 1U;
constexpr LegacySignatureMask RequiredSignatures =
    InitStatePatternBit | InitStateTargetBit;

// Read-only research profile derived from CitizenFX's modern GTA V
// initialization-state discovery at commit
// 5c8481b36c2dc65ee7c8e9f5d9bf283f03e2f36e, BlockLoadSetters.cpp
// lines 579-593. Reactor does not copy CitizenFX's mutation behavior: this
// profile only locates and reads the state integer. On the exact, corroborated
// 3889 identity, the worker may reduce that read-only evidence into Reactor's
// typed initializer event; it never copies CitizenFX's mutation behavior.
constexpr std::array<LegacyPatternByte, 22> InitStateSignature{{
    {0x48, false}, {0x83, false}, {0xEC, false}, {0x00, true},
    {0xE8, false}, {0x00, true}, {0x00, true}, {0x00, true},
    {0x00, true}, {0xE8, false}, {0x00, true}, {0x00, true},
    {0x00, true}, {0x00, true}, {0x48, false}, {0x8B, false},
    {0x0D, false}, {0x00, true}, {0x00, true}, {0x00, true},
    {0x00, true}, {0xE8, false},
}};

constexpr std::size_t InitStateInstructionOffsetFromMatch = 55;
constexpr std::size_t InitStateDisplacementOffset = 2;
constexpr std::size_t InitStateInstructionLength =
    LegacyShadowInstructionByteCount;

bool IsExecutableProtection(const DWORD protection) noexcept {
    const DWORD base = protection & 0xFFU;
    // PAGE_EXECUTE alone is not readable and must never be handed to the
    // byte scanner.
    return base == PAGE_EXECUTE_READ || base == PAGE_EXECUTE_READWRITE ||
        base == PAGE_EXECUTE_WRITECOPY;
}

bool IsWritableNonExecutableProtection(const DWORD protection) noexcept {
    const DWORD base = protection & 0xFFU;
    return base == PAGE_READWRITE || base == PAGE_WRITECOPY;
}

bool IsExecuteReadWriteProtection(const DWORD protection) noexcept {
    return (protection & 0xFFU) == PAGE_EXECUTE_READWRITE;
}

bool IsUsableRegion(const MEMORY_BASIC_INFORMATION& region) noexcept {
    return region.State == MEM_COMMIT &&
        (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
}

bool TryReadCurrentProcessMemory(
    const void* source,
    void* destination,
    const std::size_t bytes,
    unsigned long* readError = nullptr,
    std::size_t* actualBytesRead = nullptr) noexcept {
    if (source == nullptr || destination == nullptr || bytes == 0) return false;
    SIZE_T bytesRead{};
    SetLastError(ERROR_SUCCESS);
    const bool success = ReadProcessMemory(
               GetCurrentProcess(),
               source,
               destination,
               bytes,
               &bytesRead) != FALSE &&
        bytesRead == bytes;
    if (readError != nullptr) *readError = success ? ERROR_SUCCESS : GetLastError();
    if (actualBytesRead != nullptr) *actualBytesRead = bytesRead;
    return success;
}

bool MatchesInitStateSignature(
    const std::span<const std::uint8_t> candidate) noexcept {
    if (candidate.size() != InitStateSignature.size()) return false;
    for (std::size_t index = 0; index < candidate.size(); ++index) {
        if (!InitStateSignature[index].wildcard &&
            candidate[index] != InitStateSignature[index].value) {
            return false;
        }
    }
    return true;
}

LegacyExecutableKind CurrentExecutableKind(
    const wchar_t* executablePath) noexcept {
    if (executablePath == nullptr || *executablePath == L'\0') {
        return LegacyExecutableKind::Unknown;
    }
    const wchar_t* filename = std::wcsrchr(executablePath, L'\\');
    filename = filename == nullptr ? executablePath : filename + 1;
    if (_wcsicmp(filename, L"GTA5.exe") == 0) {
        return LegacyExecutableKind::LegacyGta5;
    }
    if (_wcsicmp(filename, L"GTA5_Enhanced.exe") == 0) {
        return LegacyExecutableKind::EnhancedGta5;
    }
    return LegacyExecutableKind::Other;
}

bool TryReadFileVersion(
    const wchar_t* executablePath,
    LegacyFileVersion& destination) noexcept {
    try {
        DWORD ignored{};
        const DWORD bytes = GetFileVersionInfoSizeW(executablePath, &ignored);
        if (bytes == 0) return false;
        std::vector<std::uint8_t> buffer(bytes);
        if (!GetFileVersionInfoW(
                executablePath,
                0,
                bytes,
                buffer.data())) {
            return false;
        }
        VS_FIXEDFILEINFO* fixed{};
        UINT fixedBytes{};
        if (!VerQueryValueW(
                buffer.data(),
                L"\\",
                reinterpret_cast<void**>(&fixed),
                &fixedBytes) ||
            fixed == nullptr || fixedBytes < sizeof(VS_FIXEDFILEINFO) ||
            fixed->dwSignature != 0xFEEF04BD) {
            return false;
        }
        destination = {
            HIWORD(fixed->dwFileVersionMS),
            LOWORD(fixed->dwFileVersionMS),
            HIWORD(fixed->dwFileVersionLS),
            LOWORD(fixed->dwFileVersionLS),
        };
        return true;
    } catch (...) {
        return false;
    }
}

struct ExecutableScanResult {
    LegacyPatternScanResult pattern{};
    bool readFault{};
    unsigned long readError{};
    bool cancelled{};
};

ExecutableScanResult ScanExecutableRegions(
    const std::uint8_t* moduleBase,
    const std::size_t imageSize,
    const std::stop_token* stopToken) noexcept {
    constexpr std::size_t SnapshotBytes = 256U * 1024U;
    constexpr std::size_t PatternOverlap = InitStateSignature.size() - 1U;
    LegacyPatternScanResult aggregate{
        LegacyPatternScanStatus::Missing,
        0,
        0,
    };
    const auto imageStart = reinterpret_cast<std::uintptr_t>(moduleBase);
    if (imageStart > std::numeric_limits<std::uintptr_t>::max() - imageSize) {
        return {{LegacyPatternScanStatus::Invalid, 0, 0}, false, 0, false};
    }
    std::unique_ptr<std::uint8_t[]> snapshot(
        new (std::nothrow) std::uint8_t[SnapshotBytes + PatternOverlap]);
    if (!snapshot) {
        return {{LegacyPatternScanStatus::Invalid, 0, 0}, false, 0, false};
    }
    const auto imageEnd = imageStart + imageSize;
    auto cursor = imageStart;
    std::size_t overlapBytes{};
    std::uintptr_t previousReadableEnd{};
    while (cursor < imageEnd) {
        if (stopToken != nullptr && stopToken->stop_requested()) {
            return {aggregate, false, 0, true};
        }
        MEMORY_BASIC_INFORMATION region{};
        if (VirtualQuery(
                reinterpret_cast<const void*>(cursor),
                &region,
                sizeof(region)) != sizeof(region) ||
            region.RegionSize == 0) {
            return {{LegacyPatternScanStatus::Invalid, 0, 0}, false, 0, false};
        }
        const auto regionStart = std::max(
            cursor,
            reinterpret_cast<std::uintptr_t>(region.BaseAddress));
        const auto regionBase =
            reinterpret_cast<std::uintptr_t>(region.BaseAddress);
        if (regionBase >
            std::numeric_limits<std::uintptr_t>::max() - region.RegionSize) {
            return {{LegacyPatternScanStatus::Invalid, 0, 0}, false, 0, false};
        }
        const auto rawRegionEnd = regionBase + region.RegionSize;
        const auto regionEnd = std::min(imageEnd, rawRegionEnd);
        if (regionEnd <= regionStart) {
            return {{LegacyPatternScanStatus::Invalid, 0, 0}, false, 0, false};
        }

        if (IsUsableRegion(region) &&
            IsExecutableProtection(region.Protect) &&
            region.Type == MEM_IMAGE && region.AllocationBase == moduleBase) {
            if (previousReadableEnd != regionStart) overlapBytes = 0;
            auto chunkStart = regionStart;
            while (chunkStart < regionEnd) {
                if (stopToken != nullptr && stopToken->stop_requested()) {
                    return {aggregate, false, 0, true};
                }
                const auto chunkBytes = std::min<std::size_t>(
                    SnapshotBytes,
                    static_cast<std::size_t>(regionEnd - chunkStart));
                SIZE_T bytesRead{};
                SetLastError(ERROR_SUCCESS);
                if (!ReadProcessMemory(
                        GetCurrentProcess(),
                        reinterpret_cast<const void*>(chunkStart),
                        snapshot.get() + overlapBytes,
                        chunkBytes,
                        &bytesRead) ||
                    bytesRead != chunkBytes) {
                    return {
                        aggregate,
                        true,
                        GetLastError(),
                        false,
                    };
                }

                const auto snapshotBytes = overlapBytes + chunkBytes;
                const auto result = ScanLegacyPattern(
                    std::span<const std::uint8_t>(
                        snapshot.get(),
                        snapshotBytes),
                    InitStateSignature);
                if (result.status == LegacyPatternScanStatus::Invalid) {
                    return {result, false, 0, false};
                }
                if (result.matchCount != 0) {
                    if (aggregate.matchCount == 0) {
                        aggregate.firstMatchOffset =
                            static_cast<std::size_t>(chunkStart - imageStart) -
                            overlapBytes + result.firstMatchOffset;
                    }
                    const auto room =
                        std::numeric_limits<std::size_t>::max() -
                        aggregate.matchCount;
                    aggregate.matchCount +=
                        std::min(room, result.matchCount);
                }

                overlapBytes = std::min(PatternOverlap, snapshotBytes);
                std::memmove(
                    snapshot.get(),
                    snapshot.get() + snapshotBytes - overlapBytes,
                    overlapBytes);
                chunkStart += chunkBytes;
                previousReadableEnd = chunkStart;
            }
        } else {
            overlapBytes = 0;
            previousReadableEnd = 0;
        }
        cursor = regionEnd;
    }

    aggregate.status = aggregate.matchCount == 0
        ? LegacyPatternScanStatus::Missing
        : aggregate.matchCount == 1
            ? LegacyPatternScanStatus::Unique
            : LegacyPatternScanStatus::Ambiguous;
    return {aggregate, false, 0, false};
}

LegacyDataSectionInspection InspectMappedTargetDataSection(
    const std::uint8_t* moduleBase,
    const std::size_t imageSize,
    const std::size_t targetRva,
    const std::size_t ntOffset,
    const IMAGE_NT_HEADERS64& nt,
    LegacyShadowTargetDiagnostics& diagnostics) noexcept {
    LegacyDataSectionInspection rejected{};
    const auto sectionCount =
        static_cast<std::size_t>(nt.FileHeader.NumberOfSections);
    constexpr std::size_t MaximumSectionCount = 96;
    constexpr std::size_t PeSignatureSize = sizeof(std::uint32_t);
    const auto optionalHeaderSize =
        static_cast<std::size_t>(nt.FileHeader.SizeOfOptionalHeader);
    if (sectionCount == 0 || sectionCount > MaximumSectionCount ||
        optionalHeaderSize < sizeof(IMAGE_OPTIONAL_HEADER64) ||
        ntOffset > std::numeric_limits<std::size_t>::max() -
            PeSignatureSize - sizeof(IMAGE_FILE_HEADER) ||
        ntOffset + PeSignatureSize + sizeof(IMAGE_FILE_HEADER) >
            std::numeric_limits<std::size_t>::max() - optionalHeaderSize) {
        rejected.status = LegacyDataSectionStatus::HeaderTableRejected;
        return rejected;
    }

    const auto sectionTableRva = ntOffset + PeSignatureSize +
        sizeof(IMAGE_FILE_HEADER) + optionalHeaderSize;
    if (sectionCount >
        std::numeric_limits<std::size_t>::max() /
            sizeof(IMAGE_SECTION_HEADER)) {
        rejected.status = LegacyDataSectionStatus::HeaderTableRejected;
        return rejected;
    }
    const auto tableBytes = sectionCount * sizeof(IMAGE_SECTION_HEADER);
    if (sectionTableRva > imageSize || tableBytes > imageSize - sectionTableRva ||
        nt.OptionalHeader.SizeOfHeaders > imageSize ||
        sectionTableRva > nt.OptionalHeader.SizeOfHeaders ||
        tableBytes > nt.OptionalHeader.SizeOfHeaders - sectionTableRva) {
        rejected.status = LegacyDataSectionStatus::HeaderTableRejected;
        return rejected;
    }

    try {
        std::vector<IMAGE_SECTION_HEADER> nativeSections(sectionCount);
        std::size_t sectionBytesRead{};
        if (!TryReadCurrentProcessMemory(
                moduleBase + sectionTableRva,
                nativeSections.data(),
                tableBytes,
                &diagnostics.dataSectionReadError,
                &sectionBytesRead)) {
            rejected.status = LegacyDataSectionStatus::HeaderReadFault;
            return rejected;
        }

        std::vector<LegacyPeSectionDescriptor> sections;
        sections.reserve(sectionCount);
        for (const auto& native : nativeSections) {
            LegacyPeSectionDescriptor section{};
            std::copy_n(
                native.Name,
                section.name.size(),
                section.name.begin());
            section.virtualAddress = native.VirtualAddress;
            section.virtualSize = native.Misc.VirtualSize;
            section.rawSize = native.SizeOfRawData;
            section.characteristics = native.Characteristics;
            sections.push_back(section);
        }
        return InspectLegacyTargetDataSection(
            sections,
            imageSize,
            targetRva,
            sizeof(std::int32_t));
    } catch (...) {
        rejected.status = LegacyDataSectionStatus::HeaderReadFault;
        diagnostics.dataSectionReadError = ERROR_NOT_ENOUGH_MEMORY;
        return rejected;
    }
}

LegacyTargetValidationStatus ValidateTarget(
    const std::uint8_t* moduleBase,
    const std::size_t imageSize,
    const std::size_t targetRva,
    const std::size_t ntOffset,
    const IMAGE_NT_HEADERS64& nt,
    LegacyShadowTargetDiagnostics& diagnostics) noexcept {
    auto& evidence = diagnostics.validationEvidence;
    const auto classify = [&diagnostics, &evidence]() noexcept {
        diagnostics.validationStatus =
            ClassifyLegacyTargetValidation(evidence);
        return diagnostics.validationStatus;
    };

    evidence.imageBoundsPass = targetRva <= imageSize &&
        sizeof(std::int32_t) <= imageSize - targetRva;
    if (!evidence.imageBoundsPass) return classify();

    const auto dataSection = InspectMappedTargetDataSection(
        moduleBase,
        imageSize,
        targetRva,
        ntOffset,
        nt,
        diagnostics);
    diagnostics.dataSectionStatus = dataSection.status;
    diagnostics.dataSectionMatchCount = dataSection.containingSectionCount;
    diagnostics.dataSectionName = dataSection.section.name;
    diagnostics.dataSectionRva = dataSection.section.virtualAddress;
    diagnostics.dataSectionVirtualSize = dataSection.section.virtualSize;
    diagnostics.dataSectionRawSize = dataSection.section.rawSize;
    diagnostics.dataSectionCharacteristics =
        dataSection.section.characteristics;

    const auto* target = moduleBase + targetRva;
    evidence.alignmentPass =
        (reinterpret_cast<std::uintptr_t>(target) %
         alignof(std::int32_t)) == 0;
    if (!evidence.alignmentPass) return classify();

    MEMORY_BASIC_INFORMATION region{};
    SetLastError(ERROR_SUCCESS);
    evidence.regionQueryPass =
        VirtualQuery(target, &region, sizeof(region)) == sizeof(region);
    if (!evidence.regionQueryPass) {
        diagnostics.regionQueryError = GetLastError();
        return classify();
    }

    diagnostics.regionState = region.State;
    diagnostics.regionProtect = region.Protect;
    diagnostics.regionType = region.Type;
    diagnostics.regionSize = region.RegionSize;
    const auto regionBase =
        reinterpret_cast<std::uintptr_t>(region.BaseAddress);
    const auto moduleStart = reinterpret_cast<std::uintptr_t>(moduleBase);
    if (regionBase >= moduleStart) {
        diagnostics.regionBaseRva =
            static_cast<std::size_t>(regionBase - moduleStart);
    }

    // Gather the complete region receipt before classifying. This keeps a
    // protection rejection from hiding a wrong allocation, type, or boundary
    // in diagnostics.
    evidence.regionUsablePass = IsUsableRegion(region);
    evidence.ordinaryProtectionPass =
        IsWritableNonExecutableProtection(region.Protect);
    evidence.executeReadWriteObserved =
        IsExecuteReadWriteProtection(region.Protect);
    evidence.dataSectionBackedProtectionPass =
        evidence.executeReadWriteObserved && dataSection.IsAccepted();
    evidence.protectionPass = IsLegacyTargetProtectionAccepted(
        evidence.ordinaryProtectionPass,
        evidence.executeReadWriteObserved,
        dataSection.IsAccepted());
    evidence.typePass = region.Type == MEM_IMAGE;
    evidence.allocationBasePass = region.AllocationBase == moduleBase;

    const auto targetStart = reinterpret_cast<std::uintptr_t>(target);
    evidence.regionAddressPass =
        regionBase <=
            std::numeric_limits<std::uintptr_t>::max() - region.RegionSize &&
        targetStart <=
            std::numeric_limits<std::uintptr_t>::max() -
                sizeof(std::int32_t);
    if (evidence.regionAddressPass) {
        const auto regionEnd = regionBase + region.RegionSize;
        const auto targetEnd = targetStart + sizeof(std::int32_t);
        evidence.targetContainedPass =
            targetStart >= regionBase && targetEnd <= regionEnd;
    }
    return classify();
}

LegacyShadowPollStatus PollStatus(
    const Legacy3889ClassificationStatus status) noexcept {
    switch (status) {
        case Legacy3889ClassificationStatus::UnsupportedRawValue:
            return LegacyShadowPollStatus::UnsupportedRawValue;
        case Legacy3889ClassificationStatus::Debouncing:
            return LegacyShadowPollStatus::Debouncing;
        case Legacy3889ClassificationStatus::StableButUnarmed:
            return LegacyShadowPollStatus::StableButUnarmed;
        case Legacy3889ClassificationStatus::Grounded:
            return LegacyShadowPollStatus::Grounded;
        case Legacy3889ClassificationStatus::InvalidConfiguration:
        default:
            return LegacyShadowPollStatus::NotReady;
    }
}

} // namespace

LegacyShadowDiscoveryReceipt LegacyStartupShadowProbe::Discover(
    const std::stop_token* stopToken) noexcept {
    Reset();

    wchar_t executablePath[32768]{};
    const DWORD pathLength = GetModuleFileNameW(
        nullptr,
        executablePath,
        static_cast<DWORD>(_countof(executablePath)));
    if (pathLength == 0 || pathLength >= _countof(executablePath)) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }

    const HMODULE module = GetModuleHandleW(nullptr);
    if (module == nullptr) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    const auto* moduleBytes = reinterpret_cast<const std::uint8_t*>(module);
    MEMORY_BASIC_INFORMATION moduleRegion{};
    IMAGE_DOS_HEADER dos{};
    if (VirtualQuery(moduleBytes, &moduleRegion, sizeof(moduleRegion)) !=
            sizeof(moduleRegion) ||
        !IsUsableRegion(moduleRegion) || moduleRegion.Type != MEM_IMAGE ||
        moduleRegion.AllocationBase != module) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    if (!TryReadCurrentProcessMemory(
            moduleBytes,
            &dos,
            sizeof(dos),
            &_receipt.readError,
            &_receipt.bytesRead)) {
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureReadFault;
        return _receipt;
    }
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew <= 0) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    const auto ntOffset = static_cast<std::size_t>(dos.e_lfanew);
    constexpr std::size_t MaximumPeHeaderOffset = 1024U * 1024U;
    if (ntOffset > MaximumPeHeaderOffset ||
        ntOffset > std::numeric_limits<std::size_t>::max() -
            sizeof(IMAGE_NT_HEADERS64)) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    MEMORY_BASIC_INFORMATION ntRegion{};
    const auto* ntAddress = moduleBytes + ntOffset;
    if (VirtualQuery(ntAddress, &ntRegion, sizeof(ntRegion)) !=
            sizeof(ntRegion) ||
        !IsUsableRegion(ntRegion) || ntRegion.Type != MEM_IMAGE ||
        ntRegion.AllocationBase != module) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    IMAGE_NT_HEADERS64 nt{};
    if (!TryReadCurrentProcessMemory(
            ntAddress,
            &nt,
            sizeof(nt),
            &_receipt.readError,
            &_receipt.bytesRead)) {
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureReadFault;
        return _receipt;
    }
    if (nt.Signature != IMAGE_NT_SIGNATURE ||
        nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt.OptionalHeader.SizeOfImage <
            ntOffset + sizeof(IMAGE_NT_HEADERS64)) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }

    _identity.executableKind = CurrentExecutableKind(executablePath);
    _identity.peMachine = nt.FileHeader.Machine;
    _identity.peTimestamp = nt.FileHeader.TimeDateStamp;
    _identity.sizeOfImage = nt.OptionalHeader.SizeOfImage;
    _receipt.peTimestamp = _identity.peTimestamp;
    _receipt.sizeOfImage = _identity.sizeOfImage;
    if (_identity.executableKind != LegacyExecutableKind::LegacyGta5) {
        _receipt.status = LegacyShadowDiscoveryStatus::UnsupportedExecutable;
        return _receipt;
    }
    if (!TryReadFileVersion(executablePath, _identity.fileVersion)) {
        _receipt.status = LegacyShadowDiscoveryStatus::VersionUnavailable;
        return _receipt;
    }

    _profile = {
        Legacy3889ProfileRevision,
        Amd64Machine,
        {1, 0, 3889, 0},
        Legacy3889PeTimestamp,
        Legacy3889SizeOfImage,
        RequiredSignatures,
    };
    if (_identity.peMachine != _profile.expectedPeMachine ||
        _identity.fileVersion.major != _profile.expectedFileVersion.major ||
        _identity.fileVersion.minor != _profile.expectedFileVersion.minor ||
        _identity.fileVersion.build != _profile.expectedFileVersion.build ||
        _identity.fileVersion.revision != _profile.expectedFileVersion.revision ||
        _identity.peTimestamp != _profile.expectedPeTimestamp ||
        _identity.sizeOfImage != _profile.expectedSizeOfImage) {
        _receipt.status = LegacyShadowDiscoveryStatus::UnsupportedBuild;
        return _receipt;
    }

    _moduleBase = module;
    _imageSize = _identity.sizeOfImage;
    const auto scanResult = ScanExecutableRegions(
        moduleBytes,
        _imageSize,
        stopToken);
    if (scanResult.cancelled) {
        _receipt.status = LegacyShadowDiscoveryStatus::Uninitialized;
        return _receipt;
    }
    if (scanResult.readFault) {
        _signatureEvidence.checkedMask = InitStatePatternBit;
        _signatureEvidence.readFaultMask = InitStatePatternBit;
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureReadFault;
        _receipt.readError = scanResult.readError;
        return _receipt;
    }
    const auto& scan = scanResult.pattern;
    _receipt.patternStatus = scan.status;
    _receipt.matchCount = static_cast<std::uint32_t>(std::min<std::size_t>(
        scan.matchCount,
        std::numeric_limits<std::uint32_t>::max()));
    _receipt.matchRva = scan.firstMatchOffset;
    _signatureEvidence.checkedMask = InitStatePatternBit;
    if (scan.status == LegacyPatternScanStatus::Missing) {
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureMissing;
        return _receipt;
    }
    if (scan.status == LegacyPatternScanStatus::Ambiguous) {
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureAmbiguous;
        return _receipt;
    }
    if (!scan.IsUnique()) {
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidImage;
        return _receipt;
    }
    std::array<std::uint8_t, InitStateSignature.size()>
        revalidatedSignature{};
    if (!TryReadCurrentProcessMemory(
            moduleBytes + scan.firstMatchOffset,
            revalidatedSignature.data(),
            revalidatedSignature.size(),
            &_receipt.readError,
            &_receipt.bytesRead)) {
        _signatureEvidence.readFaultMask |= InitStatePatternBit;
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureReadFault;
        return _receipt;
    }
    if (!MatchesInitStateSignature(revalidatedSignature)) {
        _receipt.targetDiagnostics.instructionStatus =
            LegacyShadowInstructionStatus::SignatureRevalidationMismatch;
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidTarget;
        return _receipt;
    }
    _signatureEvidence.matchedMask |= InitStatePatternBit;

    _receipt.targetDiagnostics.instructionRva =
        scan.firstMatchOffset + InitStateInstructionOffsetFromMatch;
    if (_imageSize < InitStateInstructionOffsetFromMatch +
            InitStateInstructionLength ||
        scan.firstMatchOffset >
            _imageSize - InitStateInstructionOffsetFromMatch -
                InitStateInstructionLength) {
        _receipt.targetDiagnostics.instructionStatus =
            LegacyShadowInstructionStatus::RangeInvalid;
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidTarget;
        return _receipt;
    }
    const auto instructionRva = _receipt.targetDiagnostics.instructionRva;
    auto& instruction = _receipt.targetDiagnostics.instructionBytes;
    if (!TryReadCurrentProcessMemory(
            moduleBytes + instructionRva,
            instruction.data(),
            instruction.size(),
            &_receipt.readError,
            &_receipt.bytesRead)) {
        _receipt.targetDiagnostics.instructionBytesRead =
            _receipt.bytesRead;
        _receipt.targetDiagnostics.instructionStatus =
            LegacyShadowInstructionStatus::ReadFault;
        _signatureEvidence.checkedMask |= InitStateTargetBit;
        _signatureEvidence.readFaultMask |= InitStateTargetBit;
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::SignatureReadFault;
        return _receipt;
    }
    _receipt.targetDiagnostics.instructionBytesRead = _receipt.bytesRead;
    if (!IsLegacy3889InitStateStoreInstruction(instruction)) {
        _receipt.targetDiagnostics.instructionStatus =
            LegacyShadowInstructionStatus::OpcodeMismatch;
        _signatureEvidence.checkedMask |= InitStateTargetBit;
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidTarget;
        return _receipt;
    }
    _receipt.targetDiagnostics.instructionStatus =
        LegacyShadowInstructionStatus::OpcodeMatched;
    _receipt.targetDiagnostics.decodeAttempted = true;
    const auto decode = DecodeLegacyRipRelativeInstruction(
        instruction,
        instructionRva,
        InitStateDisplacementOffset,
        InitStateInstructionLength,
        _imageSize,
        sizeof(std::int32_t));
    _receipt.targetDiagnostics.decodeStatus = decode.status;
    _receipt.targetDiagnostics.displacement = decode.displacement;
    _receipt.targetDiagnostics.candidateTargetRva = decode.targetOffset;
    _signatureEvidence.checkedMask |= InitStateTargetBit;
    if (!decode.IsSuccess()) {
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidTarget;
        return _receipt;
    }
    if (ValidateTarget(
            moduleBytes,
            _imageSize,
            decode.targetOffset,
            ntOffset,
            nt,
            _receipt.targetDiagnostics) !=
        LegacyTargetValidationStatus::Accepted) {
        _receipt.gateStatus = EvaluateLegacyHookGate(
            _identity,
            _profile,
            _signatureEvidence).status;
        _receipt.status = LegacyShadowDiscoveryStatus::InvalidTarget;
        return _receipt;
    }
    _signatureEvidence.matchedMask |= InitStateTargetBit;
    _receipt.targetRva = decode.targetOffset;
    _initState = reinterpret_cast<const std::int32_t*>(
        moduleBytes + decode.targetOffset);

    ++_adapterGeneration;
    if (_adapterGeneration == 0) ++_adapterGeneration;
    _sessionGeneration = 1;
    _observationSequence = 0;
    const auto activation = _hookCore.Activate(
        _identity,
        _profile,
        _signatureEvidence,
        _adapterGeneration,
        _sessionGeneration);
    _receipt.gateStatus = activation.gate.status;
    if (!activation.IsActive()) {
        _receipt.status = LegacyShadowDiscoveryStatus::GateClosed;
        _initState = nullptr;
        return _receipt;
    }

    _receipt.status = LegacyShadowDiscoveryStatus::Ready;
    return _receipt;
}

LegacyShadowPollReceipt LegacyStartupShadowProbe::Poll(
    const std::uint64_t observedAtTickMilliseconds) noexcept {
    LegacyShadowPollReceipt receipt{};
    receipt.adapterGeneration = _adapterGeneration;
    receipt.sessionGeneration = _sessionGeneration;
    receipt.observationSequence = _observationSequence;
    if (!IsReady() || _initState == nullptr) return receipt;

    std::int32_t rawValue{};
    SIZE_T bytesRead{};
    SetLastError(ERROR_SUCCESS);
    if (!ReadProcessMemory(
            GetCurrentProcess(),
            _initState,
            &rawValue,
            sizeof(rawValue),
            &bytesRead) ||
        bytesRead != sizeof(rawValue)) {
        // A gap in the observation stream invalidates the prior frontend arm.
        // The next edge now requires a fresh stable frontend sequence.
        _classifier.Reset();
        receipt.readError = GetLastError();
        receipt.bytesRead = bytesRead;
        if (_consecutiveReadFaults <
            std::numeric_limits<std::uint32_t>::max()) {
            ++_consecutiveReadFaults;
        }
        receipt.consecutiveReadFaults = _consecutiveReadFaults;
        if (_consecutiveReadFaults >= 3) {
            _hookCore.Deactivate();
            _receipt.status =
                LegacyShadowDiscoveryStatus::SignatureReadFault;
            _initState = nullptr;
        }
        receipt.status = LegacyShadowPollStatus::ReadFault;
        return receipt;
    }
    _consecutiveReadFaults = 0;

    receipt.rawValue = rawValue;
    receipt.rawValueChanged =
        !_hasLastRawValue || rawValue != _lastRawValue;
    _lastRawValue = rawValue;
    _hasLastRawValue = true;
    receipt.classification = _classifier.Classify(rawValue);
    receipt.status = PollStatus(receipt.classification.status);
    if (!receipt.classification.IsGrounded()) return receipt;

    if (_sessionBoundaryTracker.ObserveGrounded(
            receipt.classification.state)) {
        ++_sessionGeneration;
        if (_sessionGeneration == 0) ++_sessionGeneration;
        _observationSequence = 0;
        const auto activation = _hookCore.Activate(
            _identity,
            _profile,
            _signatureEvidence,
            _adapterGeneration,
            _sessionGeneration);
        if (!activation.IsActive()) {
            _receipt.status = LegacyShadowDiscoveryStatus::GateClosed;
            receipt.status = LegacyShadowPollStatus::NotReady;
            return receipt;
        }
    }

    const LegacyStartupObservation observation{
        _adapterGeneration,
        _sessionGeneration,
        ++_observationSequence,
        observedAtTickMilliseconds,
        receipt.classification.state,
        receipt.classification.observationStatus,
    };
    _hookCore.PublishFromHook(observation);
    const auto reduction = _hookCore.Poll();
    receipt.reductionDecision = reduction.decision;
    receipt.wouldEnterStory = reduction.HasEnteringStoryEdge();
    if (receipt.wouldEnterStory) {
        receipt.diagnosticEdgeSequence = reduction.edge.edgeSequence;
        receipt.diagnosticSourceObservationSequence =
            reduction.edge.sourceObservationSequence;
    }
    receipt.adapterGeneration = _adapterGeneration;
    receipt.sessionGeneration = _sessionGeneration;
    receipt.observationSequence = _observationSequence;
    return receipt;
}

void LegacyStartupShadowProbe::Reset() noexcept {
    _hookCore.Deactivate();
    _moduleBase = nullptr;
    _initState = nullptr;
    _imageSize = 0;
    _receipt = {};
    _identity = {};
    _profile = {};
    _signatureEvidence = {};
    _classifier.Reset();
    _sessionBoundaryTracker.Reset();
    _sessionGeneration = 1;
    _observationSequence = 0;
    _lastRawValue = 0;
    _hasLastRawValue = false;
    _consecutiveReadFaults = 0;
}

bool LegacyStartupShadowProbe::IsReady() const noexcept {
    return _receipt.IsReady() && _initState != nullptr &&
        _hookCore.IsActive();
}

const LegacyShadowDiscoveryReceipt& LegacyStartupShadowProbe::Receipt() const noexcept {
    return _receipt;
}

} // namespace reactorv::bootstrap
