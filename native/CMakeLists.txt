cmake_minimum_required(VERSION 3.24)
project(RageWebUINative VERSION 0.2.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

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
    src/D3D11OverlayRenderer.cpp
    src/DirectXCompositor.cpp
    src/FrameMailbox.cpp
    src/HookManager.cpp
    src/InputQueue.cpp
    src/TestSurface.cpp
)

add_library(ReactorV.Bootstrap SHARED
    bootstrap/BootstrapMain.cpp
    bootstrap/BootstrapPolicy.cpp
)

target_include_directories(ReactorV.Bootstrap PRIVATE bootstrap)
target_compile_definitions(ReactorV.Bootstrap PRIVATE
    NOMINMAX
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
)
target_link_libraries(ReactorV.Bootstrap PRIVATE
    user32
    gdi32
)

set_target_properties(ReactorV.Bootstrap PROPERTIES
    OUTPUT_NAME "ReactorV.Bootstrap"
    PREFIX ""
    SUFFIX ".asi"
)

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
)

if(MSVC)
    target_compile_options(RageWebUI.Native PRIVATE /W4 /permissive-)
    target_compile_options(ReactorV.Bootstrap PRIVATE /W4 /permissive-)
else()
    target_compile_options(RageWebUI.Native PRIVATE -Wall -Wextra -Wpedantic)
    target_link_options(RageWebUI.Native PRIVATE -static-libgcc -static-libstdc++)
    target_compile_options(ReactorV.Bootstrap PRIVATE -Wall -Wextra -Wpedantic)
    target_link_options(ReactorV.Bootstrap PRIVATE -static-libgcc -static-libstdc++)
endif()

set_target_properties(RageWebUI.Native PROPERTIES
    OUTPUT_NAME "RageWebUI.Native"
    PREFIX ""
)

include(CTest)
if(BUILD_TESTING)
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
endif()
