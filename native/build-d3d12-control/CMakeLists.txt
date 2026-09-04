cmake_minimum_required(VERSION 3.20)
project(ReactorVD3D12ResizeControl LANGUAGES CXX)
add_executable(ReactorV.D3D12.ResizeControl D3D12ResizeControl.cpp)
target_compile_features(ReactorV.D3D12.ResizeControl PRIVATE cxx_std_17)
target_compile_definitions(ReactorV.D3D12.ResizeControl PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(ReactorV.D3D12.ResizeControl PRIVATE
    d3d12 dxgi dxguid user32)
