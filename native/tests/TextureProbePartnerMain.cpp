#include "LegacyTextureProbe.h"
#include <string>
int wmain(int argc, wchar_t** argv) {
    if (argc != 4 || std::wstring_view(argv[1]) != L"--texture-probe-child") return 2;
    try {
        size_t first{}, second{};
        const auto mapping = std::stoull(argv[2], &first);
        const auto owner = std::stoull(argv[3], &second);
        if (first != wcslen(argv[2]) || second != wcslen(argv[3]) || !mapping || !owner) return 2;
        return rwui::probe::TextureProbeChild(reinterpret_cast<HANDLE>(mapping), reinterpret_cast<HANDLE>(owner));
    } catch (...) { return 2; }
}
