#include "BootstrapPolicy.h"

#include <cstdlib>
#include <iostream>
#include <string>

namespace {

bool ParseBoolean(const char* value, bool& parsed) {
    const std::string candidate = value == nullptr ? "" : value;
    if (candidate == "true" || candidate == "1") {
        parsed = true;
        return true;
    }
    if (candidate == "false" || candidate == "0") {
        parsed = false;
        return true;
    }
    return false;
}

bool ParseStage(
    const char* value,
    reactorv::bootstrap::StartupStage& stage) {
    const std::string candidate = value == nullptr ? "" : value;
    if (candidate == "core-data") {
        stage = reactorv::bootstrap::StartupStage::CoreDataPrepared;
        return true;
    }
    if (candidate == "script-threads") {
        stage = reactorv::bootstrap::StartupStage::ScriptThreadsStarting;
        return true;
    }
    if (candidate == "managed") {
        stage = reactorv::bootstrap::StartupStage::ManagedRuntimeReady;
        return true;
    }
    if (candidate == "story") {
        stage = reactorv::bootstrap::StartupStage::StoryModeReady;
        return true;
    }
    return false;
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 8 && argc != 10) {
        std::cerr << "usage: BootstrapRouteProbe <stage> "
                     "<loading-available> <loading> "
                     "<player-playing-available> <player-playing> "
                     "<frontend-ready-available> <frontend-ready> "
                     "[<landing-menu-available> <landing-menu-active>]\n";
        return 2;
    }

    reactorv::bootstrap::StartupStage stage{};
    reactorv::bootstrap::BootstrapGameStateProbe probe{};
    if (!ParseStage(argv[1], stage) ||
        !ParseBoolean(argv[2], probe.loadingAvailable) ||
        !ParseBoolean(argv[3], probe.loading) ||
        !ParseBoolean(argv[4], probe.playerPlayingAvailable) ||
        !ParseBoolean(argv[5], probe.playerPlaying) ||
        !ParseBoolean(argv[6], probe.frontendReadyAvailable) ||
        !ParseBoolean(argv[7], probe.frontendReady) ||
        (argc == 10 &&
         (!ParseBoolean(argv[8], probe.landingMenuAvailable) ||
          !ParseBoolean(argv[9], probe.landingMenuActive)))) {
        std::cerr << "invalid route-probe argument\n";
        return 2;
    }

    const auto route = reactorv::bootstrap::ResolveBootstrapF9Surface(stage, probe);
    const char* routeName = "verifying";
    if (route == reactorv::bootstrap::BootstrapSurfaceRoute::About) {
        routeName = "about";
    } else if (
        route == reactorv::bootstrap::BootstrapSurfaceRoute::Initializing) {
        routeName = "initializing";
    }
    std::cout << routeName << '\n';
    return 0;
}
