# EnneaOdin native bridge.
#
# Copyright (c) 2025 EnneaOdin contributors.
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds one shared library, libenneaodin.dylib on macOS and libenneaodin.so on
# Linux, containing:
#
#   src/            EnneaOdin's own C ABI wrapper, GPL-3.0-or-later
#   vendor/Heimdall Heimdall v2.2.2, MIT; see vendor/Heimdall/LICENSE
#
# Out of source only. From this directory:
#
#   cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build
#
# Debug is -DCMAKE_BUILD_TYPE=Debug. On macOS the architecture follows the
# toolchain unless -DCMAKE_OSX_ARCHITECTURES=arm64 or x86_64 says otherwise; a
# universal build needs a universal libusb, which Homebrew does not ship, so it
# is not attempted here.

cmake_minimum_required(VERSION 3.20)

project(enneaodin_native
        VERSION 1.0.0
        DESCRIPTION "EnneaOdin native bridge over Heimdall"
        LANGUAGES CXX)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_BINARY_DIR)
    message(FATAL_ERROR
            "Configure out of source: cmake -S . -B build -DCMAKE_BUILD_TYPE=Release")
endif()

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Release or Debug" FORCE)
endif()

if(WIN32)
    message(FATAL_ERROR "EnneaOdin targets macOS and Linux only.")
endif()

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Warnings ON. This build used to compile with none at all, which made
# "it builds clean" a statement about nothing: the bridge, the hooks and the
# three SendFile fixes had never been shown a warning flag. They are clean
# under -Wall -Wextra, and now the build is what proves it rather than a
# one-off recompile somebody did by hand.
#
# NOT -Werror. The vendored Heimdall under vendor/ is upstream code built with
# these same flags, and turning a warning in somebody else's 2017 C++ into a
# build failure would mean either patching code this project deliberately does
# not own or turning the flags back off. A warning that is printed and read is
# worth more than one that is configured away.
if(NOT MSVC)
    add_compile_options(-Wall -Wextra)
endif()

set(VENDOR "${CMAKE_CURRENT_SOURCE_DIR}/vendor/Heimdall")

# --------------------------------------------------------------------------
# Dependencies
# --------------------------------------------------------------------------

find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBUSB REQUIRED IMPORTED_TARGET libusb-1.0)

# The log capture reads the redirected pipe on its own thread.
set(THREADS_PREFER_PTHREAD_FLAG ON)
find_package(Threads REQUIRED)

# Which trick is available for making a FILE * out of callbacks. Neither is
# universal: glibc has fopencookie, the BSDs and macOS have funopen, musl has
# neither, and odin_send_file_stream reports ODIN_UNSUPPORTED where neither is.
include(CheckSymbolExists)
check_symbol_exists(funopen "stdio.h" ODIN_HAVE_FUNOPEN)
set(CMAKE_REQUIRED_DEFINITIONS -D_GNU_SOURCE)
check_symbol_exists(fopencookie "stdio.h" ODIN_HAVE_FOPENCOOKIE)
unset(CMAKE_REQUIRED_DEFINITIONS)

if(NOT ODIN_HAVE_FUNOPEN AND NOT ODIN_HAVE_FOPENCOOKIE)
    message(WARNING
            "Neither funopen nor fopencookie was found. odin_send_file_stream will "
            "return ODIN_UNSUPPORTED on this platform and callers must spool to a "
            "file and use odin_send_file_path.")
endif()

# --------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------

set(ODIN_SOURCES
    src/odin_bridge.cpp
    src/OdinHooks.cpp
    src/OdinLogCapture.cpp
    src/OdinSerialPort.cpp
    src/OdinStreamFile.cpp)

# Every Heimdall translation unit except main.cpp.
#
# Not a glob: a glob is evaluated when CMake runs, so a file added to the
# vendored tree would be silently left out of an incremental build. The list
# is also the honest statement of what is in this library.
#
# Why all of them rather than just BridgeManager.cpp: Interface.cpp's
# populateActionMap() names all eight Action classes, so linking Interface.cpp
# - which every other file needs for its logging - pulls in the whole command
# surface whether or not this library calls into it. main.cpp is the one
# exclusion, and the reason is structural rather than practical: it owns the
# process entry point, and the process belongs to the host application.
set(HEIMDALL_SOURCES
    ${VENDOR}/heimdall/source/Arguments.cpp
    ${VENDOR}/heimdall/source/BridgeManager.cpp
    ${VENDOR}/heimdall/source/ClosePcScreenAction.cpp
    ${VENDOR}/heimdall/source/DetectAction.cpp
    ${VENDOR}/heimdall/source/DownloadPitAction.cpp
    ${VENDOR}/heimdall/source/FlashAction.cpp
    ${VENDOR}/heimdall/source/HelpAction.cpp
    ${VENDOR}/heimdall/source/InfoAction.cpp
    ${VENDOR}/heimdall/source/Interface.cpp
    ${VENDOR}/heimdall/source/PrintPitAction.cpp
    ${VENDOR}/heimdall/source/Utility.cpp
    ${VENDOR}/heimdall/source/VersionAction.cpp)

set(LIBPIT_SOURCES
    ${VENDOR}/libpit/source/libpit.cpp)

add_library(enneaodin SHARED
    ${ODIN_SOURCES}
    ${HEIMDALL_SOURCES}
    ${LIBPIT_SOURCES})

set_target_properties(enneaodin PROPERTIES
    OUTPUT_NAME enneaodin
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

target_include_directories(enneaodin
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src
            ${VENDOR}/heimdall/source
            ${VENDOR}/libpit/source)

target_link_libraries(enneaodin PRIVATE PkgConfig::LIBUSB Threads::Threads)

if(ODIN_HAVE_FUNOPEN)
    target_compile_definitions(enneaodin PRIVATE ODIN_HAVE_FUNOPEN=1)
endif()

if(ODIN_HAVE_FOPENCOOKIE)
    target_compile_definitions(enneaodin PRIVATE ODIN_HAVE_FOPENCOOKIE=1)
endif()

if(CMAKE_SYSTEM_NAME MATCHES "Linux")
    # BridgeManager.h and BridgeManager.cpp guard the kernel-driver detach and
    # the Ubuntu reset behind OS_LINUX, exactly as upstream's own build does.
    target_compile_definitions(enneaodin PRIVATE OS_LINUX)
endif()

if(NOT APPLE)
    # Large file support. AP.tar is 6.7 GB and super.img.lz4 inside it is
    # 5.5 GB, and Heimdall seeks and tells with fseeko and ftello
    # (Heimdall.h:50-51), so off_t has to be 64 bits wide. It already is on
    # every 64-bit target; this makes it true on a 32-bit one as well.
    target_compile_definitions(enneaodin PRIVATE _FILE_OFFSET_BITS=64 _LARGEFILE_SOURCE)
endif()

if(APPLE)
    target_link_libraries(enneaodin PRIVATE
        "-framework IOKit" "-framework CoreFoundation")
endif()

# --------------------------------------------------------------------------
# libusb linking policy
# --------------------------------------------------------------------------
#
# libusb-1.0 stays a documented system dependency. It is not bundled here and
# not linked statically, which is also what keeps its LGPL-2.1 obligations as
# light as NOTICE says they are.
#
# The hazard this handles is macOS-specific. Linking against Homebrew's libusb
# records its install name, an absolute path such as
# /opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib, into this library. A machine
# without Homebrew libusb at that exact path - an Intel Mac, where the prefix is
# /usr/local, or any machine where the user installed libusb some other way -
# cannot load it at all.
#
# So the recorded dependency is rewritten to @rpath/libusb-1.0.0.dylib after
# linking and a search list is baked in. The dynamic loader then tries, in
# order: beside this library, the app bundle's Frameworks directory, the
# Homebrew arm64 prefix, and the Homebrew x86_64 and MacPorts prefixes. Two
# consequences worth stating plainly:
#
#   - nothing in the shipped library names a path that only exists on the
#     machine that built it;
#   - a later packaging step that wants to bundle libusb only has to copy the
#     dylib into Contents/Frameworks, with no relinking, because @loader_path
#     and @executable_path/../Frameworks are already in the list.
#
# cmake/FixLibusbInstallName.cmake does the rewrite and then fails the build if
# an absolute package-manager path is still recorded, so this cannot rot
# silently.
#
# On Linux there is nothing to fix: the ELF records the SONAME
# libusb-1.0.so.0 and the loader searches for it. $ORIGIN is added so a
# bundled copy beside the library is found first if anyone ever ships one.

if(APPLE)
    set_target_properties(enneaodin PROPERTIES
        BUILD_WITH_INSTALL_RPATH TRUE
        INSTALL_RPATH "@loader_path;@loader_path/../Frameworks;@executable_path/../Frameworks;/opt/homebrew/lib;/usr/local/lib;/opt/local/lib")

    add_custom_command(TARGET enneaodin POST_BUILD
        COMMAND ${CMAKE_COMMAND}
                -DODIN_LIBRARY=$<TARGET_FILE:enneaodin>
                -P ${CMAKE_CURRENT_SOURCE_DIR}/cmake/FixLibusbInstallName.cmake
        VERBATIM
        COMMENT "Rewriting the recorded libusb path to @rpath")
else()
    set_target_properties(enneaodin PROPERTIES
        BUILD_WITH_INSTALL_RPATH TRUE
        INSTALL_RPATH "$ORIGIN")
endif()

# --------------------------------------------------------------------------
# Install
# --------------------------------------------------------------------------

include(GNUInstallDirs)

install(TARGETS enneaodin LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR})
install(FILES include/odin_bridge.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})

message(STATUS "EnneaOdin native bridge")
message(STATUS "  build type      : ${CMAKE_BUILD_TYPE}")
message(STATUS "  libusb-1.0      : ${LIBUSB_VERSION}")
message(STATUS "  funopen         : ${ODIN_HAVE_FUNOPEN}")
message(STATUS "  fopencookie     : ${ODIN_HAVE_FOPENCOOKIE}")
