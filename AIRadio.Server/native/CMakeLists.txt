cmake_minimum_required(VERSION 3.16)

project(airadio_pipewire LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

find_package(PkgConfig REQUIRED)
pkg_check_modules(PIPEWIRE REQUIRED IMPORTED_TARGET libpipewire-0.3)

add_library(airadio-pipewire SHARED
    airadio_pipewire.cpp)

target_include_directories(airadio-pipewire
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR})

target_link_libraries(airadio-pipewire
    PRIVATE
        PkgConfig::PIPEWIRE)

target_compile_options(airadio-pipewire PRIVATE
    -Wall
    -Wextra
    -Wpedantic
    -O2)

set_target_properties(airadio-pipewire PROPERTIES
    OUTPUT_NAME "airadio-pipewire"
    VERSION 1.0.0
    SOVERSION 1)
