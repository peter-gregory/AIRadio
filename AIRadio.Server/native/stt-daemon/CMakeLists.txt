cmake_minimum_required(VERSION 3.18)

project(stt-daemon LANGUAGES CXX)

# ============================================================================
# C++ configuration
# ============================================================================

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ============================================================================
# Sherpa-ONNX paths
# ============================================================================

set(SHERPA_ONNX_ROOT
    "/home/radio/sherpa-onnx"
    CACHE PATH "Sherpa-ONNX source directory"
)

set(SHERPA_ONNX_BUILD
    "${SHERPA_ONNX_ROOT}/build"
    CACHE PATH "Sherpa-ONNX build directory"
)

set(SHERPA_ONNX_LIB_DIR
    "${SHERPA_ONNX_BUILD}/lib"
)

set(SHERPA_ONNX_INSTALL_LIB_DIR
    "${SHERPA_ONNX_BUILD}/install/lib"
)

# ============================================================================
# Confirmed Sherpa-ONNX dependency include paths
# ============================================================================

set(ONNXRUNTIME_INCLUDE_DIR
    "${SHERPA_ONNX_BUILD}/_deps/onnxruntime-src/include"
)

set(KALDI_DECODER_INCLUDE_DIR
    "${SHERPA_ONNX_BUILD}/_deps/kaldi_decoder-src"
)

set(OPENFST_INCLUDE_DIR
    "${SHERPA_ONNX_BUILD}/_deps/openfst-src/src/include"
)

set(KALDIFST_INCLUDE_DIR
    "${SHERPA_ONNX_BUILD}/_deps/kaldifst-src"
)

set(EIGEN_INCLUDE_DIR
    "${SHERPA_ONNX_BUILD}/_deps/eigen-src"
)

# ============================================================================
# ONNX Runtime library
# ============================================================================

set(ONNX_RUNTIME_LIB
    "${SHERPA_ONNX_INSTALL_LIB_DIR}/libonnxruntime.a"
)

# ============================================================================
# Dependencies
# ============================================================================

find_package(PkgConfig REQUIRED)
find_package(Threads REQUIRED)
find_package(CURL REQUIRED)

set(ENV{PKG_CONFIG_PATH}
    "/usr/lib/aarch64-linux-gnu/pkgconfig:$ENV{PKG_CONFIG_PATH}"
)

# ============================================================================
# PipeWire
# ============================================================================

pkg_check_modules(
    PIPEWIRE
    REQUIRED
    IMPORTED_TARGET
    libpipewire-0.3
)

# ============================================================================
# Verify Sherpa-ONNX directories
# ============================================================================

if(NOT EXISTS "${SHERPA_ONNX_ROOT}")
    message(FATAL_ERROR
        "Sherpa-ONNX source directory not found:\n"
        "  ${SHERPA_ONNX_ROOT}"
    )
endif()

if(NOT EXISTS "${SHERPA_ONNX_BUILD}")
    message(FATAL_ERROR
        "Sherpa-ONNX build directory not found:\n"
        "  ${SHERPA_ONNX_BUILD}"
    )
endif()

# ============================================================================
# Verify ONNX Runtime headers
# ============================================================================

if(NOT EXISTS "${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_cxx_api.h")
    message(FATAL_ERROR
        "ONNX Runtime C++ header not found:\n"
        "  ${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_cxx_api.h"
    )
endif()

if(NOT EXISTS "${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_c_api.h")
    message(FATAL_ERROR
        "ONNX Runtime C header not found:\n"
        "  ${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_c_api.h"
    )
endif()

# ============================================================================
# Verify Kaldi decoder
# ============================================================================

if(NOT EXISTS
    "${KALDI_DECODER_INCLUDE_DIR}/kaldi-decoder/csrc/faster-decoder.h"
)
    message(FATAL_ERROR
        "Kaldi decoder header not found:\n"
        "  ${KALDI_DECODER_INCLUDE_DIR}/kaldi-decoder/csrc/faster-decoder.h"
    )
endif()

# ============================================================================
# Verify OpenFST
# ============================================================================

if(NOT EXISTS "${OPENFST_INCLUDE_DIR}/fst/fst.h")
    message(FATAL_ERROR
        "OpenFST header not found:\n"
        "  ${OPENFST_INCLUDE_DIR}/fst/fst.h"
    )
endif()

# ============================================================================
# Verify kaldifst
# ============================================================================

if(NOT EXISTS
    "${KALDIFST_INCLUDE_DIR}/kaldifst/csrc/lattice-weight.h"
)
    message(FATAL_ERROR
        "kaldifst header not found:\n"
        "  ${KALDIFST_INCLUDE_DIR}/kaldifst/csrc/lattice-weight.h"
    )
endif()

# ============================================================================
# Verify Eigen
# ============================================================================

if(NOT EXISTS "${EIGEN_INCLUDE_DIR}/Eigen/Dense")
    message(FATAL_ERROR
        "Eigen header not found:\n"
        "  ${EIGEN_INCLUDE_DIR}/Eigen/Dense"
    )
endif()

# ============================================================================
# Verify Sherpa-ONNX libraries
# ============================================================================

set(SHERPA_REQUIRED_LIBRARIES
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-cxx-api.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-c-api.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-kaldifst-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-fstfar.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-fst.a"
    "${SHERPA_ONNX_LIB_DIR}/libkaldi-decoder-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libkaldi-native-fbank-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libssentencepiece_core.a"
    "${SHERPA_ONNX_LIB_DIR}/libkissfft-float.a"
    "${SHERPA_ONNX_LIB_DIR}/libcargs.a"
    "${SHERPA_ONNX_LIB_DIR}/libucd.a"
    "${SHERPA_ONNX_LIB_DIR}/libpiper_phonemize.a"
    "${SHERPA_ONNX_LIB_DIR}/libespeak-ng.a"
    "${ONNX_RUNTIME_LIB}"
)

foreach(LIBRARY ${SHERPA_REQUIRED_LIBRARIES})
    if(NOT EXISTS "${LIBRARY}")
        message(FATAL_ERROR
            "Required Sherpa-ONNX library not found:\n"
            "  ${LIBRARY}"
        )
    endif()
endforeach()

# ============================================================================
# Executable
# ============================================================================

add_executable(stt-daemon
    stt-daemon.cc
)

# ============================================================================
# Compiler configuration
# ============================================================================

target_compile_features(stt-daemon PRIVATE
    cxx_std_17
)

target_compile_options(stt-daemon PRIVATE
    -Wall
    -Wextra
    -Wpedantic
)

# ============================================================================
# Include directories
# ============================================================================

target_include_directories(stt-daemon PRIVATE
    "${SHERPA_ONNX_ROOT}"
    "${SHERPA_ONNX_ROOT}/sherpa-onnx/cxx-api"
    "${SHERPA_ONNX_ROOT}/sherpa-onnx/c-api"
    "${SHERPA_ONNX_BUILD}"
    "${ONNXRUNTIME_INCLUDE_DIR}"
    "${KALDI_DECODER_INCLUDE_DIR}"
    "${OPENFST_INCLUDE_DIR}"
    "${KALDIFST_INCLUDE_DIR}"
    "${EIGEN_INCLUDE_DIR}"
)

# ============================================================================
# Library directories
# ============================================================================

target_link_directories(stt-daemon PRIVATE
    "${SHERPA_ONNX_LIB_DIR}"
    "${SHERPA_ONNX_INSTALL_LIB_DIR}"
)

# ============================================================================
# Link libraries
# ============================================================================

target_link_libraries(stt-daemon PRIVATE
    PkgConfig::PIPEWIRE
    CURL::libcurl

    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-cxx-api.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-c-api.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-core.a"

    "${ONNX_RUNTIME_LIB}"

    "${SHERPA_ONNX_LIB_DIR}/libpiper_phonemize.a"
    "${SHERPA_ONNX_LIB_DIR}/libespeak-ng.a"

    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-kaldifst-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-fstfar.a"
    "${SHERPA_ONNX_LIB_DIR}/libsherpa-onnx-fst.a"

    "${SHERPA_ONNX_LIB_DIR}/libkaldi-decoder-core.a"
    "${SHERPA_ONNX_LIB_DIR}/libkaldi-native-fbank-core.a"

    "${SHERPA_ONNX_LIB_DIR}/libssentencepiece_core.a"
    "${SHERPA_ONNX_LIB_DIR}/libkissfft-float.a"

    "${SHERPA_ONNX_LIB_DIR}/libcargs.a"
    "${SHERPA_ONNX_LIB_DIR}/libucd.a"

    Threads::Threads
    dl
    m
)

# ============================================================================
# Output
# ============================================================================

set_target_properties(stt-daemon PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ============================================================================
# Build RPATH
# ============================================================================

set_target_properties(stt-daemon PROPERTIES
    BUILD_RPATH "${SHERPA_ONNX_LIB_DIR};${SHERPA_ONNX_INSTALL_LIB_DIR}"
)

# ============================================================================
# Diagnostics
# ============================================================================

message(STATUS "")
message(STATUS "==========================================")
message(STATUS "        stt-daemon configuration")
message(STATUS "==========================================")
message(STATUS "")
message(STATUS "C++ standard       : C++17")
message(STATUS "Build type         : ${CMAKE_BUILD_TYPE}")
message(STATUS "")
message(STATUS "Sherpa-ONNX root   : ${SHERPA_ONNX_ROOT}")
message(STATUS "Sherpa-ONNX build  : ${SHERPA_ONNX_BUILD}")
message(STATUS "")
message(STATUS "Sherpa libraries   : ${SHERPA_ONNX_LIB_DIR}")
message(STATUS "Sherpa install lib : ${SHERPA_ONNX_INSTALL_LIB_DIR}")
message(STATUS "")
message(STATUS "Dependency include paths:")
message(STATUS "  ONNX Runtime     : ${ONNXRUNTIME_INCLUDE_DIR}")
message(STATUS "  Kaldi decoder    : ${KALDI_DECODER_INCLUDE_DIR}")
message(STATUS "  OpenFST          : ${OPENFST_INCLUDE_DIR}")
message(STATUS "  kaldifst         : ${KALDIFST_INCLUDE_DIR}")
message(STATUS "  Eigen            : ${EIGEN_INCLUDE_DIR}")
message(STATUS "")
message(STATUS "PipeWire version   : ${PIPEWIRE_VERSION}")
message(STATUS "PipeWire includes  : ${PIPEWIRE_INCLUDE_DIRS}")
message(STATUS "CURL includes      : ${CURL_INCLUDE_DIRS}")
message(STATUS "CURL libraries     : ${CURL_LIBRARIES}")
message(STATUS "")
message(STATUS "Sherpa _deps directories:")

file(GLOB SHERPA_DEPS_ENTRIES
    LIST_DIRECTORIES true
    "${SHERPA_ONNX_BUILD}/_deps/*"
)

foreach(DEP_ENTRY ${SHERPA_DEPS_ENTRIES})
    if(IS_DIRECTORY "${DEP_ENTRY}")
        get_filename_component(DEP_NAME "${DEP_ENTRY}" NAME)
        message(STATUS "  ${DEP_NAME}")
    endif()
endforeach()

message(STATUS "")
message(STATUS "Confirmed dependency headers:")
message(STATUS "")
message(STATUS "  ONNX Runtime:")
message(STATUS "    ${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_cxx_api.h")
message(STATUS "    ${ONNXRUNTIME_INCLUDE_DIR}/onnxruntime_c_api.h")

message(STATUS "")
message(STATUS "  Kaldi decoder:")
message(STATUS "    ${KALDI_DECODER_INCLUDE_DIR}/kaldi-decoder/csrc/faster-decoder.h")

message(STATUS "")
message(STATUS "  OpenFST:")
message(STATUS "    ${OPENFST_INCLUDE_DIR}/fst/fst.h")

message(STATUS "")
message(STATUS "  kaldifst:")
message(STATUS "    ${KALDIFST_INCLUDE_DIR}/kaldifst/csrc/lattice-weight.h")

message(STATUS "")
message(STATUS "  Eigen:")
message(STATUS "    ${EIGEN_INCLUDE_DIR}/Eigen/Dense")

message(STATUS "")
message(STATUS "==========================================")
message(STATUS "")
