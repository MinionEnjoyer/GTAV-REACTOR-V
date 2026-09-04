cmake_minimum_required(VERSION 3.24)
project(RageWebUINative VERSION 0.2.0 LANGUAGES C CXX)

if(WIN32 AND NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR
        "ReactorV native targets support only the x64 GTA V process.")
endif()

option(
    REACTORV_ENABLE_LEGACY_STARTUP_SHADOW
    "Enable the fail-closed Legacy startup lifecycle observer"
    ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(MSVC)
    # Player binaries must not acquire a new VC runtime installation
    # prerequisite merely because the ScriptHook companion requires MSVC's
    # C++ ABI. Keep Release and Debug self-contained.
    set(
        CMAKE_MSVC_RUNTIME_LIBRARY
        "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

include(FetchContent)
FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.4
    GIT_SHALLOW TRUE
)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(minhook)

add_library(RageWebUI.Native SHARED
    src/AdapterLuidDiscovery.cpp
    src/AdapterLuidExports.cpp
    src/D3D11OverlayRenderer.cpp
    src/D3D11DeviceProbe.cpp
    src/LegacyTextureProbe.cpp
    src/LegacyVisibilityProbe.cpp
    src/DirectXCompositor.cpp
    src/DxgiHookPolicy.cpp
    src/FrameMailbox.cpp
    src/HookManager.cpp
    src/InputQueue.cpp
    src/SharedGpuFrameChannel.cpp
    src/SharedGpuFrameConsumer.cpp
    src/LegacyD3D11FrameBridge.cpp
    src/LegacyCpuFrameBridge.cpp
    src/SharedGpuFrameD3D11.cpp
    src/SharedGpuFrameDiscovery.cpp
    src/SharedGpuFrameExports.cpp
    src/SharedGpuFrameProducer.cpp
    src/SharedGpuFrameRuntime.cpp
    src/SharedGpuFrameTransport.cpp
    src/TestSurface.cpp
)

add_library(ReactorV.Bootstrap SHARED
    bootstrap/BootstrapMain.cpp
    bootstrap/IncrementalLogSignals.cpp
    bootstrap/LegacyStartupDiscovery.cpp
    bootstrap/LegacyStartupHookCore.cpp
    bootstrap/LegacyStartupShadowProbe.cpp
    bootstrap/LegacyStartupShadowRunner.cpp
    bootstrap/BootstrapPolicy.cpp
)

target_include_directories(ReactorV.Bootstrap PRIVATE bootstrap probe)
target_compile_definitions(ReactorV.Bootstrap PRIVATE
    NOMINMAX
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    REACTORV_ENABLE_LEGACY_STARTUP_SHADOW=$<BOOL:${REACTORV_ENABLE_LEGACY_STARTUP_SHADOW}>
)
target_link_libraries(ReactorV.Bootstrap PRIVATE
    user32
    comctl32
    gdi32
    version
)

set_target_properties(ReactorV.Bootstrap PROPERTIES
    OUTPUT_NAME "ReactorV.Bootstrap"
    PREFIX ""
    SUFFIX ".asi"
)

add_library(ReactorV.RenderHook SHARED
    renderhook/RenderHookMain.cpp
    renderhook/RenderHookPolicy.cpp
)
target_include_directories(ReactorV.RenderHook PRIVATE renderhook)
target_compile_definitions(ReactorV.RenderHook PRIVATE
    NOMINMAX
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
)
set_target_properties(ReactorV.RenderHook PROPERTIES
    OUTPUT_NAME "ReactorV.RenderHook"
    PREFIX ""
    SUFFIX ".asi"
)

set(
    REACTORV_SCRIPTHOOK_SDK_ROOT
    ""
    CACHE PATH
    "Absolute path to an official, externally supplied ScriptHookV SDK root")

if(REACTORV_SCRIPTHOOK_SDK_ROOT)
    if(NOT IS_ABSOLUTE "${REACTORV_SCRIPTHOOK_SDK_ROOT}")
        message(FATAL_ERROR
            "REACTORV_SCRIPTHOOK_SDK_ROOT must be an absolute path.")
    endif()
    set(
        REACTORV_SCRIPTHOOK_INCLUDE_DIRECTORY
        "${REACTORV_SCRIPTHOOK_SDK_ROOT}/inc")
    set(
        REACTORV_SCRIPTHOOK_IMPORT_LIBRARY
        "${REACTORV_SCRIPTHOOK_SDK_ROOT}/lib/ScriptHookV.lib")
    if(NOT EXISTS "${REACTORV_SCRIPTHOOK_IMPORT_LIBRARY}" AND
       EXISTS "${REACTORV_SCRIPTHOOK_SDK_ROOT}/ScriptHookV.lib")
        set(
            REACTORV_SCRIPTHOOK_IMPORT_LIBRARY
            "${REACTORV_SCRIPTHOOK_SDK_ROOT}/ScriptHookV.lib")
    endif()
    if(NOT EXISTS "${REACTORV_SCRIPTHOOK_INCLUDE_DIRECTORY}/main.h")
        message(FATAL_ERROR
            "The configured official ScriptHookV SDK is missing inc/main.h: "
            "${REACTORV_SCRIPTHOOK_SDK_ROOT}")
    endif()
    if(NOT EXISTS "${REACTORV_SCRIPTHOOK_IMPORT_LIBRARY}")
        message(FATAL_ERROR
            "The configured official ScriptHookV SDK is missing "
            "ScriptHookV.lib under its root or lib directory: "
            "${REACTORV_SCRIPTHOOK_SDK_ROOT}")
    endif()
    if(NOT MSVC)
        message(FATAL_ERROR
            "ReactorV.ScriptProbe uses ScriptHookV's official Microsoft C++ ABI. "
            "Configure this tree with the x64 MSVC toolchain when supplying "
            "REACTORV_SCRIPTHOOK_IMPORT_LIBRARY.")
    endif()

    add_library(ReactorV.ScriptProbe SHARED
        probe/ScriptProbeMain.cpp
        probe/ScriptProbeState.cpp
    )
    target_include_directories(ReactorV.ScriptProbe PRIVATE
        probe
        "${REACTORV_SCRIPTHOOK_INCLUDE_DIRECTORY}")
    target_compile_definitions(ReactorV.ScriptProbe PRIVATE
        NOMINMAX
        UNICODE
        _UNICODE
        WIN32_LEAN_AND_MEAN
    )
    target_compile_options(ReactorV.ScriptProbe PRIVATE /W4 /permissive-)
    target_link_libraries(ReactorV.ScriptProbe PRIVATE
        "${REACTORV_SCRIPTHOOK_IMPORT_LIBRARY}")
    set_target_properties(ReactorV.ScriptProbe PROPERTIES
        OUTPUT_NAME "ReactorV.ScriptProbe"
        PREFIX ""
        SUFFIX ".asi"
    )
    message(STATUS
        "ReactorV.ScriptProbe enabled with the configured official ScriptHookV import library.")
else()
    message(STATUS
        "ReactorV.ScriptProbe disabled: set REACTORV_SCRIPTHOOK_SDK_ROOT to "
        "an official, externally supplied ScriptHookV SDK for a release build.")
endif()

target_include_directories(RageWebUI.Native
    PUBLIC include
    PRIVATE src
)
target_compile_definitions(RageWebUI.Native PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    RWUI_NATIVE_EXPORTS
)
target_link_libraries(RageWebUI.Native PRIVATE
    minhook
    d3d11
    d3d12
    dxgi
    d3dcompiler
    user32
    gdi32
    bcrypt
)

if(MSVC)
    target_compile_options(RageWebUI.Native PRIVATE /W4 /permissive-)
    target_compile_options(ReactorV.Bootstrap PRIVATE /W4 /permissive-)
    target_compile_options(ReactorV.RenderHook PRIVATE /W4 /permissive-)
else()
    target_compile_options(RageWebUI.Native PRIVATE -Wall -Wextra -Wpedantic)
    target_link_options(RageWebUI.Native PRIVATE -static-libgcc -static-libstdc++)
    target_compile_options(ReactorV.Bootstrap PRIVATE -Wall -Wextra -Wpedantic)
    if(MINGW AND CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        target_compile_options(ReactorV.Bootstrap PRIVATE
            -Wno-language-extension-token)
    endif()
    target_link_options(ReactorV.Bootstrap PRIVATE -static-libgcc -static-libstdc++)
    target_compile_options(ReactorV.RenderHook PRIVATE -Wall -Wextra -Wpedantic)
    target_link_options(ReactorV.RenderHook PRIVATE -static-libgcc -static-libstdc++)
endif()

set_target_properties(RageWebUI.Native PROPERTIES
    OUTPUT_NAME "RageWebUI.Native"
    PREFIX ""
)

include(CTest)
add_executable(ReactorV.TextureProbe.Partner
    tests/TextureProbePartnerMain.cpp src/LegacyTextureProbe.cpp
    src/D3D11OverlayRenderer.cpp src/FrameMailbox.cpp)
target_include_directories(ReactorV.TextureProbe.Partner PRIVATE src include)
target_compile_definitions(ReactorV.TextureProbe.Partner PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
target_link_libraries(ReactorV.TextureProbe.Partner PRIVATE d3d11 dxgi d3dcompiler)
if(MSVC)
    target_compile_options(ReactorV.TextureProbe.Partner PRIVATE /W4 /permissive-)
elseif(MINGW)
    target_link_options(ReactorV.TextureProbe.Partner PRIVATE -municode -static-libgcc -static-libstdc++)
endif()
if(BUILD_TESTING)
    add_executable(ReactorV.LegacyVisibilityProbe.Tests
        tests/LegacyVisibilityProbeTests.cpp src/LegacyVisibilityProbe.cpp
        src/D3D11OverlayRenderer.cpp src/FrameMailbox.cpp)
    target_include_directories(ReactorV.LegacyVisibilityProbe.Tests PRIVATE src)
    target_compile_definitions(ReactorV.LegacyVisibilityProbe.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(ReactorV.LegacyVisibilityProbe.Tests PRIVATE d3d11 dxgi d3dcompiler)
    add_test(NAME ReactorV.LegacyVisibilityProbe COMMAND ReactorV.LegacyVisibilityProbe.Tests)
    set_tests_properties(ReactorV.LegacyVisibilityProbe PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 20)
    add_executable(ReactorV.LegacyTextureProbe.Tests
        tests/LegacyTextureProbeTests.cpp src/LegacyTextureProbe.cpp
        src/D3D11OverlayRenderer.cpp src/FrameMailbox.cpp)
    target_include_directories(ReactorV.LegacyTextureProbe.Tests PRIVATE src include)
    target_compile_definitions(ReactorV.LegacyTextureProbe.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(ReactorV.LegacyTextureProbe.Tests PRIVATE d3d11 dxgi d3dcompiler)
    add_dependencies(ReactorV.LegacyTextureProbe.Tests ReactorV.TextureProbe.Partner)
    add_test(NAME ReactorV.LegacyTextureProbe COMMAND ReactorV.LegacyTextureProbe.Tests)
    set_tests_properties(ReactorV.LegacyTextureProbe PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 35)
    add_executable(RageWebUI.Native.Tests
        tests/FrameMailboxTests.cpp
        src/FrameMailbox.cpp
    )
    target_include_directories(RageWebUI.Native.Tests PRIVATE include src)
    target_compile_definitions(RageWebUI.Native.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(NOT MSVC)
        target_link_options(RageWebUI.Native.Tests PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(NAME RageWebUI.Native.FrameMailbox COMMAND RageWebUI.Native.Tests)

    add_executable(ReactorV.AdapterLuidDiscovery.Tests
        tests/AdapterLuidDiscoveryTests.cpp
        src/AdapterLuidDiscovery.cpp
    )
    target_include_directories(
        ReactorV.AdapterLuidDiscovery.Tests PRIVATE src)
    target_compile_definitions(
        ReactorV.AdapterLuidDiscovery.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.AdapterLuidDiscovery.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.AdapterLuidDiscovery.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.AdapterLuidDiscovery.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.AdapterLuidDiscovery
        COMMAND ReactorV.AdapterLuidDiscovery.Tests)

    add_executable(ReactorV.D3D11OverlayRenderer.Tests
        tests/D3D11OverlayRendererTests.cpp
        src/D3D11OverlayRenderer.cpp
        src/FrameMailbox.cpp
    )
    target_include_directories(
        ReactorV.D3D11OverlayRenderer.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.D3D11OverlayRenderer.Tests PRIVATE
        NOMINMAX
        WIN32_LEAN_AND_MEAN
        REACTORV_RENDERER_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/D3D11OverlayRenderer.cpp")
    target_link_libraries(
        ReactorV.D3D11OverlayRenderer.Tests PRIVATE d3d11 dxgi d3dcompiler)
    if(MSVC)
        target_compile_options(
            ReactorV.D3D11OverlayRenderer.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.D3D11OverlayRenderer.Tests PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.D3D11OverlayRenderer.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.D3D11OverlayRenderer.HotPath
        COMMAND ReactorV.D3D11OverlayRenderer.Tests)
    set_tests_properties(
        ReactorV.D3D11OverlayRenderer.HotPath
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 15)

    add_executable(ReactorV.NativeAbi.SourceContract.Tests
        tests/NativeAbiSourceContractTests.cpp
    )
    target_compile_definitions(
        ReactorV.NativeAbi.SourceContract.Tests PRIVATE
        REACTORV_HOOK_MANAGER_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/HookManager.cpp"
        REACTORV_DIRECTX_COMPOSITOR_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/DirectXCompositor.cpp"
        REACTORV_INPUT_QUEUE_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/InputQueue.cpp"
        REACTORV_SHARED_GPU_CHANNEL_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/SharedGpuFrameChannel.cpp"
        REACTORV_SHARED_GPU_CONSUMER_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/SharedGpuFrameConsumer.cpp"
        REACTORV_SHARED_GPU_DISCOVERY_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/SharedGpuFrameDiscovery.cpp"
        REACTORV_SHARED_GPU_EXPORTS_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/SharedGpuFrameExports.cpp"
        REACTORV_TEST_SURFACE_SOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/src/TestSurface.cpp")
    if(MSVC)
        target_compile_options(
            ReactorV.NativeAbi.SourceContract.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.NativeAbi.SourceContract.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.NativeAbi.SourceContract.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.NativeAbi.SourceContract
        COMMAND ReactorV.NativeAbi.SourceContract.Tests)

    add_executable(ReactorV.SharedGpuFrame.Transport.Tests
        tests/SharedGpuFrameTransportTests.cpp
        src/SharedGpuFrameTransport.cpp
    )
    target_include_directories(
        ReactorV.SharedGpuFrame.Transport.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.SharedGpuFrame.Transport.Tests
        PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.SharedGpuFrame.Transport.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.SharedGpuFrame.Transport.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.SharedGpuFrame.Transport.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.SharedGpuFrame.Transport
        COMMAND ReactorV.SharedGpuFrame.Transport.Tests)

    add_executable(ReactorV.SharedGpuFrame.D3D11.Tests
        tests/SharedGpuFrameD3D11Tests.cpp
        src/SharedGpuFrameD3D11.cpp
        src/SharedGpuFrameTransport.cpp
    )

    add_executable(ReactorV.D3D11DeviceProbe.Tests
        tests/D3D11DeviceProbeTests.cpp src/D3D11DeviceProbe.cpp)
    target_include_directories(ReactorV.D3D11DeviceProbe.Tests PRIVATE src include)
    target_compile_definitions(ReactorV.D3D11DeviceProbe.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(ReactorV.D3D11DeviceProbe.Tests PRIVATE d3d11 dxgi)
    add_test(NAME ReactorV.D3D11DeviceProbe COMMAND ReactorV.D3D11DeviceProbe.Tests)
    set_tests_properties(ReactorV.D3D11DeviceProbe PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)
    add_executable(ReactorV.LegacyD3D11FrameBridge.Tests
        tests/LegacyD3D11FrameBridgeTests.cpp src/LegacyD3D11FrameBridge.cpp
        src/D3D11OverlayRenderer.cpp src/FrameMailbox.cpp)
    target_include_directories(ReactorV.LegacyD3D11FrameBridge.Tests PRIVATE src include)
    target_compile_definitions(ReactorV.LegacyD3D11FrameBridge.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(ReactorV.LegacyD3D11FrameBridge.Tests PRIVATE d3d11 dxgi d3dcompiler)
    add_test(NAME ReactorV.LegacyD3D11FrameBridge COMMAND ReactorV.LegacyD3D11FrameBridge.Tests)
    set_tests_properties(ReactorV.LegacyD3D11FrameBridge PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)
    target_include_directories(
        ReactorV.SharedGpuFrame.D3D11.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.SharedGpuFrame.D3D11.Tests
        PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.SharedGpuFrame.D3D11.Tests PRIVATE d3d11 dxgi)
    if(MSVC)
        target_compile_options(
            ReactorV.SharedGpuFrame.D3D11.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.SharedGpuFrame.D3D11.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.SharedGpuFrame.D3D11.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.SharedGpuFrame.D3D11
        COMMAND ReactorV.SharedGpuFrame.D3D11.Tests)
    set_tests_properties(
        ReactorV.SharedGpuFrame.D3D11
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 15)

    add_executable(ReactorV.SharedGpuFrame.Producer.Tests
        tests/SharedGpuFrameProducerTests.cpp
        src/SharedGpuFrameD3D11.cpp
        src/SharedGpuFrameProducer.cpp
        src/SharedGpuFrameTransport.cpp
    )
    target_include_directories(
        ReactorV.SharedGpuFrame.Producer.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.SharedGpuFrame.Producer.Tests
        PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.SharedGpuFrame.Producer.Tests PRIVATE d3d11 dxgi)
    if(MSVC)
        target_compile_options(
            ReactorV.SharedGpuFrame.Producer.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.SharedGpuFrame.Producer.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.SharedGpuFrame.Producer.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.SharedGpuFrame.Producer
        COMMAND ReactorV.SharedGpuFrame.Producer.Tests)
    set_tests_properties(
        ReactorV.SharedGpuFrame.Producer
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 15)

    add_executable(ReactorV.SharedGpuFrame.Channel.Tests
        tests/SharedGpuFrameChannelTests.cpp
        src/SharedGpuFrameChannel.cpp
        src/SharedGpuFrameD3D11.cpp
        src/SharedGpuFrameProducer.cpp
        src/SharedGpuFrameTransport.cpp
    )
    target_include_directories(
        ReactorV.SharedGpuFrame.Channel.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.SharedGpuFrame.Channel.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.SharedGpuFrame.Channel.Tests PRIVATE d3d11 dxgi)
    if(MSVC)
        target_compile_options(
            ReactorV.SharedGpuFrame.Channel.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.SharedGpuFrame.Channel.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.SharedGpuFrame.Channel.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.SharedGpuFrame.Channel
        COMMAND ReactorV.SharedGpuFrame.Channel.Tests)
    set_tests_properties(
        ReactorV.SharedGpuFrame.Channel
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 20)

    add_executable(ReactorV.SharedGpuFrame.Runtime.Tests
        tests/SharedGpuFrameRuntimeTests.cpp
        src/AdapterLuidDiscovery.cpp
        src/SharedGpuFrameChannel.cpp
        src/SharedGpuFrameConsumer.cpp
        src/LegacyD3D11FrameBridge.cpp
        src/LegacyCpuFrameBridge.cpp
        src/SharedGpuFrameD3D11.cpp
        src/SharedGpuFrameDiscovery.cpp
        src/SharedGpuFrameProducer.cpp
        src/SharedGpuFrameRuntime.cpp
        src/SharedGpuFrameTransport.cpp
    )
    target_include_directories(
        ReactorV.SharedGpuFrame.Runtime.Tests PRIVATE include src)
    target_compile_definitions(
        ReactorV.SharedGpuFrame.Runtime.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.SharedGpuFrame.Runtime.Tests
        PRIVATE bcrypt d3d11 dxgi)
    if(MSVC)
        target_compile_options(
            ReactorV.SharedGpuFrame.Runtime.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.SharedGpuFrame.Runtime.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.SharedGpuFrame.Runtime.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_custom_command(
        TARGET ReactorV.SharedGpuFrame.Runtime.Tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:ReactorV.SharedGpuFrame.Runtime.Tests>
            $<TARGET_FILE_DIR:ReactorV.SharedGpuFrame.Runtime.Tests>/ReactorV.Preloader.exe
        COMMENT "Copying the test producer with the trusted Preloader image name")
    add_test(
        NAME ReactorV.SharedGpuFrame.Runtime
        COMMAND ReactorV.SharedGpuFrame.Runtime.Tests)
    set_tests_properties(
        ReactorV.SharedGpuFrame.Runtime
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 25)
    add_test(NAME ReactorV.SharedGpuFrame.LegacyCompatibility
        COMMAND ReactorV.SharedGpuFrame.Runtime.Tests --legacy-nt-rejection)
    set_tests_properties(ReactorV.SharedGpuFrame.LegacyCompatibility
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 25)
    add_test(NAME ReactorV.SharedGpuFrame.LegacyCpuBridge
        COMMAND ReactorV.SharedGpuFrame.Runtime.Tests --legacy-cpu)
    set_tests_properties(ReactorV.SharedGpuFrame.LegacyCpuBridge
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 25)
    add_test(NAME ReactorV.SharedGpuFrame.LegacyCpuBlocked
        COMMAND ReactorV.SharedGpuFrame.Runtime.Tests --legacy-cpu-blocked)
    set_tests_properties(ReactorV.SharedGpuFrame.LegacyCpuBlocked
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 25)
    add_test(NAME ReactorV.SharedGpuFrame.LegacyCpuRecovery
        COMMAND ReactorV.SharedGpuFrame.Runtime.Tests --legacy-cpu-recovery)
    set_tests_properties(ReactorV.SharedGpuFrame.LegacyCpuRecovery
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 25)
    add_executable(ReactorV.LegacyCpuFrameBridge.Tests
        tests/LegacyCpuFrameBridgeTests.cpp src/LegacyCpuFrameBridge.cpp
        src/SharedGpuFrameD3D11.cpp src/SharedGpuFrameTransport.cpp src/SharedGpuFrameChannel.cpp)
    target_include_directories(ReactorV.LegacyCpuFrameBridge.Tests PRIVATE src include)
    target_compile_definitions(ReactorV.LegacyCpuFrameBridge.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(ReactorV.LegacyCpuFrameBridge.Tests PRIVATE d3d11 dxgi bcrypt)
    add_test(NAME ReactorV.LegacyCpuFrameBridge COMMAND ReactorV.LegacyCpuFrameBridge.Tests)

    # Unlike the standalone surface, this executable never calls the
    # compositor directly. It dynamically loads the production DLL, arms the
    # MinHook layer before creating a real D3D12 swap chain, late-binds the
    # HWND, and reaches rendering only through DXGI Present/resize calls.
    add_executable(ReactorV.EnhancedHook.Integration.Tests
        tests/EnhancedHookIntegrationTests.cpp
    )
    target_include_directories(
        ReactorV.EnhancedHook.Integration.Tests PRIVATE include)
    target_compile_definitions(
        ReactorV.EnhancedHook.Integration.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.EnhancedHook.Integration.Tests
        PRIVATE d3d11 d3d12 dxgi user32 ole32)
    add_dependencies(
        ReactorV.EnhancedHook.Integration.Tests
        RageWebUI.Native
        ReactorV.SharedGpuFrame.Runtime.Tests)
    if(MSVC)
        target_compile_options(
            ReactorV.EnhancedHook.Integration.Tests
            PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.EnhancedHook.Integration.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.EnhancedHook.Integration.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.EnhancedHook.Integration
        COMMAND ReactorV.EnhancedHook.Integration.Tests
            $<TARGET_FILE:RageWebUI.Native>
            $<TARGET_FILE_DIR:ReactorV.SharedGpuFrame.Runtime.Tests>/ReactorV.Preloader.exe)
    set_tests_properties(
        ReactorV.EnhancedHook.Integration
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)

    # A distinct Legacy hardware gate exercises the production D3D11 hook
    # through repeated ResizeBuffers calls while an authenticated external GPU
    # frame remains live. The normal Enhanced integration test stays focused on
    # D3D12; release qualification forbids this hardware gate from being skipped.
    add_test(
        NAME ReactorV.LegacyHook.ResizeExternalLifecycle
        COMMAND ReactorV.EnhancedHook.Integration.Tests
            $<TARGET_FILE:RageWebUI.Native>
            $<TARGET_FILE_DIR:ReactorV.SharedGpuFrame.Runtime.Tests>/ReactorV.Preloader.exe
            --legacy-resize-external)
    set_tests_properties(
        ReactorV.LegacyHook.ResizeExternalLifecycle
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)
    add_test(
        NAME ReactorV.LegacyHook.FlipExternalLifecycle
        COMMAND ReactorV.EnhancedHook.Integration.Tests
            $<TARGET_FILE:RageWebUI.Native>
            $<TARGET_FILE_DIR:ReactorV.SharedGpuFrame.Runtime.Tests>/ReactorV.Preloader.exe
            --legacy-flip-external)
    set_tests_properties(
        ReactorV.LegacyHook.FlipExternalLifecycle
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)

    # Explicit opt-in: this takes the desktop display and is unsuitable for
    # headless CI. It must fail (not qualify windowed) if fullscreen is refused.
    option(REACTORV_TEST_EXCLUSIVE_FULLSCREEN "Run interactive DXGI fullscreen qualification" OFF)
    if(REACTORV_TEST_EXCLUSIVE_FULLSCREEN)
        add_test(NAME ReactorV.LegacyHook.FullscreenExternalLifecycle
            COMMAND ReactorV.EnhancedHook.Integration.Tests
                $<TARGET_FILE:RageWebUI.Native>
                $<TARGET_FILE_DIR:ReactorV.SharedGpuFrame.Runtime.Tests>/ReactorV.Preloader.exe
                --legacy-fullscreen-external)
        set_tests_properties(ReactorV.LegacyHook.FullscreenExternalLifecycle
            PROPERTIES TIMEOUT 45 RUN_SERIAL TRUE)
    endif()

    # Legacy has a distinct compositor-only ABI: exact grcWindow binding,
    # D3D11 preparation/resize, and no RWUI_Initialize/input attachment.
    add_executable(ReactorV.LegacyHook.Integration.Tests
        tests/LegacyHookIntegrationTests.cpp
    )
    add_dependencies(ReactorV.LegacyHook.Integration.Tests ReactorV.TextureProbe.Partner)
    add_test(NAME ReactorV.LegacyTextureProbe.Hook
        COMMAND ReactorV.LegacyHook.Integration.Tests $<TARGET_FILE:RageWebUI.Native> --texture-probe)
    set_tests_properties(ReactorV.LegacyTextureProbe.Hook PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 30)
    target_include_directories(
        ReactorV.LegacyHook.Integration.Tests PRIVATE include)
    target_compile_definitions(
        ReactorV.LegacyHook.Integration.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    target_link_libraries(
        ReactorV.LegacyHook.Integration.Tests
        PRIVATE d3d11 dxgi user32)
    add_dependencies(
        ReactorV.LegacyHook.Integration.Tests RageWebUI.Native)
    if(MSVC)
        target_compile_options(
            ReactorV.LegacyHook.Integration.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.LegacyHook.Integration.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.LegacyHook.Integration.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.LegacyHook.Integration
        COMMAND ReactorV.LegacyHook.Integration.Tests
            $<TARGET_FILE:RageWebUI.Native>)
    set_tests_properties(
        ReactorV.LegacyHook.Integration
        PROPERTIES SKIP_RETURN_CODE 125 TIMEOUT 20)

    add_executable(ReactorV.DxgiHook.Policy.Tests
        tests/DxgiHookPolicyTests.cpp
        src/DxgiHookPolicy.cpp
    )
    target_include_directories(ReactorV.DxgiHook.Policy.Tests PRIVATE src)
    target_compile_definitions(
        ReactorV.DxgiHook.Policy.Tests
        PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.DxgiHook.Policy.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.DxgiHook.Policy.Tests PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.DxgiHook.Policy.Tests PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.DxgiHook.Policy
        COMMAND ReactorV.DxgiHook.Policy.Tests)

    add_executable(ReactorV.RenderHook.Policy.Tests
        tests/RenderHookPolicyTests.cpp
        renderhook/RenderHookPolicy.cpp
    )
    target_include_directories(
        ReactorV.RenderHook.Policy.Tests
        PRIVATE renderhook)
    target_compile_definitions(
        ReactorV.RenderHook.Policy.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.RenderHook.Policy.Tests
            PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.RenderHook.Policy.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.RenderHook.Policy.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.RenderHook.Policy
        COMMAND ReactorV.RenderHook.Policy.Tests)

    add_executable(ReactorV.Bootstrap.Tests
        tests/BootstrapPolicyTests.cpp
        bootstrap/BootstrapPolicy.cpp
    )
    target_include_directories(ReactorV.Bootstrap.Tests PRIVATE bootstrap)
    target_compile_definitions(ReactorV.Bootstrap.Tests PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(ReactorV.Bootstrap.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(ReactorV.Bootstrap.Tests PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(ReactorV.Bootstrap.Tests PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(NAME ReactorV.Bootstrap.Policy COMMAND ReactorV.Bootstrap.Tests)

    add_executable(ReactorV.LegacyStartupHook.Core.Tests
        tests/LegacyStartupHookCoreTests.cpp
        bootstrap/LegacyStartupHookCore.cpp
    )
    target_include_directories(
        ReactorV.LegacyStartupHook.Core.Tests
        PRIVATE bootstrap)
    target_compile_definitions(
        ReactorV.LegacyStartupHook.Core.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.LegacyStartupHook.Core.Tests
            PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.LegacyStartupHook.Core.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.LegacyStartupHook.Core.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.LegacyStartupHook.Core
        COMMAND ReactorV.LegacyStartupHook.Core.Tests)

    add_executable(ReactorV.LegacyStartupHook.Discovery.Tests
        tests/LegacyStartupDiscoveryTests.cpp
        bootstrap/LegacyStartupDiscovery.cpp
        bootstrap/LegacyStartupHookCore.cpp
    )
    target_include_directories(
        ReactorV.LegacyStartupHook.Discovery.Tests
        PRIVATE bootstrap)
    target_compile_definitions(
        ReactorV.LegacyStartupHook.Discovery.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.LegacyStartupHook.Discovery.Tests
            PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.LegacyStartupHook.Discovery.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.LegacyStartupHook.Discovery.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.LegacyStartupHook.Discovery
        COMMAND ReactorV.LegacyStartupHook.Discovery.Tests)

    add_executable(ReactorV.LegacyStartupHook.Runner.Tests
        tests/LegacyStartupShadowRunnerTests.cpp
        bootstrap/LegacyStartupShadowRunner.cpp
    )
    target_include_directories(
        ReactorV.LegacyStartupHook.Runner.Tests
        PRIVATE bootstrap)
    target_compile_definitions(
        ReactorV.LegacyStartupHook.Runner.Tests
        PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(
            ReactorV.LegacyStartupHook.Runner.Tests
            PRIVATE /W4 /permissive-)
    else()
        target_compile_options(
            ReactorV.LegacyStartupHook.Runner.Tests
            PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(
            ReactorV.LegacyStartupHook.Runner.Tests
            PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.LegacyStartupHook.Runner
        COMMAND ReactorV.LegacyStartupHook.Runner.Tests)

    # The managed bootstrap-host harness invokes this tiny executable so its
    # named-event test follows the exact C++ routing policy used by the ASI.
    # It is a test artifact only and is removed from player staging.
    add_executable(ReactorV.Bootstrap.RouteProbe
        tests/BootstrapRouteProbe.cpp
        bootstrap/BootstrapPolicy.cpp
    )
    target_include_directories(ReactorV.Bootstrap.RouteProbe PRIVATE bootstrap)
    target_compile_definitions(ReactorV.Bootstrap.RouteProbe PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(ReactorV.Bootstrap.RouteProbe PRIVATE /W4 /permissive-)
    else()
        target_compile_options(ReactorV.Bootstrap.RouteProbe PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(ReactorV.Bootstrap.RouteProbe PRIVATE -static-libgcc -static-libstdc++)
    endif()

    add_executable(ReactorV.Bootstrap.IncrementalLogSignals.Tests
        tests/IncrementalLogSignalsTests.cpp
        bootstrap/IncrementalLogSignals.cpp
    )
    target_include_directories(ReactorV.Bootstrap.IncrementalLogSignals.Tests PRIVATE bootstrap)
    target_compile_definitions(ReactorV.Bootstrap.IncrementalLogSignals.Tests PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(ReactorV.Bootstrap.IncrementalLogSignals.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(ReactorV.Bootstrap.IncrementalLogSignals.Tests PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(ReactorV.Bootstrap.IncrementalLogSignals.Tests PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.Bootstrap.IncrementalLogSignals
        COMMAND ReactorV.Bootstrap.IncrementalLogSignals.Tests)

    add_executable(ReactorV.ScriptProbe.State.Tests
        tests/ScriptProbeStateTests.cpp
        probe/ScriptProbeState.cpp
    )
    target_include_directories(ReactorV.ScriptProbe.State.Tests PRIVATE probe)
    target_compile_definitions(ReactorV.ScriptProbe.State.Tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(MSVC)
        target_compile_options(ReactorV.ScriptProbe.State.Tests PRIVATE /W4 /permissive-)
    else()
        target_compile_options(ReactorV.ScriptProbe.State.Tests PRIVATE -Wall -Wextra -Wpedantic)
        target_link_options(ReactorV.ScriptProbe.State.Tests PRIVATE -static-libgcc -static-libstdc++)
    endif()
    add_test(
        NAME ReactorV.ScriptProbe.State
        COMMAND ReactorV.ScriptProbe.State.Tests)
endif()
