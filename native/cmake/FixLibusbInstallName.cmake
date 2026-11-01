# Rewrites the libusb dependency recorded in a freshly linked macOS dylib from
# whatever absolute path the linker found to @rpath/<name>, then refuses to let
# the build pass if any absolute package-manager path is still recorded.
#
# Copyright (c) 2025 EnneaOdin contributors.
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Run as a script, not included:
#   cmake -DODIN_LIBRARY=<path to the dylib> -P FixLibusbInstallName.cmake

if(NOT ODIN_LIBRARY)
    message(FATAL_ERROR "ODIN_LIBRARY was not set.")
endif()

if(NOT EXISTS "${ODIN_LIBRARY}")
    message(FATAL_ERROR "No such library: ${ODIN_LIBRARY}")
endif()

find_program(OTOOL_EXECUTABLE otool)
find_program(INSTALL_NAME_TOOL_EXECUTABLE install_name_tool)

if(NOT OTOOL_EXECUTABLE OR NOT INSTALL_NAME_TOOL_EXECUTABLE)
    message(FATAL_ERROR
            "otool and install_name_tool are required to fix the recorded libusb "
            "path. Install the Xcode command line tools.")
endif()

function(odin_recorded_libraries out_var)
    execute_process(
        COMMAND "${OTOOL_EXECUTABLE}" -L "${ODIN_LIBRARY}"
        OUTPUT_VARIABLE output
        ERROR_VARIABLE error
        RESULT_VARIABLE status
        OUTPUT_STRIP_TRAILING_WHITESPACE)

    if(NOT status EQUAL 0)
        message(FATAL_ERROR "otool -L failed: ${error}")
    endif()

    set(${out_var} "${output}" PARENT_SCOPE)
endfunction()

odin_recorded_libraries(BEFORE_TEXT)

string(REPLACE "\n" ";" BEFORE_LINES "${BEFORE_TEXT}")
set(CHANGED FALSE)

foreach(line IN LISTS BEFORE_LINES)
    string(STRIP "${line}" line)

    # An absolute path to a libusb-1.0 dylib, as otool prints it followed by
    # its compatibility and current versions.
    if(line MATCHES "^(/[^ ]*libusb-1\\.0[^ ]*\\.dylib)")
        set(dependency "${CMAKE_MATCH_1}")
        get_filename_component(dependency_name "${dependency}" NAME)

        execute_process(
            COMMAND "${INSTALL_NAME_TOOL_EXECUTABLE}"
                    -change "${dependency}" "@rpath/${dependency_name}"
                    "${ODIN_LIBRARY}"
            ERROR_VARIABLE error
            RESULT_VARIABLE status)

        if(NOT status EQUAL 0)
            message(FATAL_ERROR "install_name_tool failed: ${error}")
        endif()

        message(STATUS "libusb: ${dependency} -> @rpath/${dependency_name}")
        set(CHANGED TRUE)
    endif()
endforeach()

if(CHANGED)
    # install_name_tool invalidates the ad-hoc signature the linker puts on an
    # arm64 Mach-O, and an arm64 image with a broken signature will not load at
    # all. Re-signing ad-hoc costs nothing and keeps the dylib loadable.
    find_program(CODESIGN_EXECUTABLE codesign)

    if(CODESIGN_EXECUTABLE)
        execute_process(
            COMMAND "${CODESIGN_EXECUTABLE}" --force --sign - "${ODIN_LIBRARY}"
            ERROR_VARIABLE error
            RESULT_VARIABLE status)

        if(NOT status EQUAL 0)
            message(WARNING "codesign --sign - failed: ${error}")
        endif()
    else()
        message(WARNING "codesign was not found; the dylib was left unsigned.")
    endif()
endif()

# The gate, in two halves, because the first half alone was reading as a
# stronger claim than it was.
#
# HALF ONE: dependencies, which is what otool -L prints. No DEPENDENCY may
# record an absolute package-manager path, because that is a path that exists
# on the build machine and need not exist anywhere else. /usr/lib is the OS
# itself and is fine.
odin_recorded_libraries(AFTER_TEXT)

if(AFTER_TEXT MATCHES "(/opt/homebrew|/usr/local/Cellar|/usr/local/opt|/opt/local)")
    message(FATAL_ERROR
            "${ODIN_LIBRARY} records an absolute package-manager path as a DEPENDENCY:\n"
            "${AFTER_TEXT}\n"
            "A machine without that exact path cannot load this library.")
endif()

# HALF TWO: the search path, which otool -L does NOT print and which this gate
# could not previously see at all. The header above used to say "no
# package-manager path is recorded in the shipped library", and the LC_RPATH
# load commands have always recorded three of them. The statement was wrong
# about the file, not about the policy, so the policy is written out here and
# checked instead of being asserted.
#
# Package-manager prefixes in LC_RPATH are DELIBERATE and are what let a
# plainly built library find a Homebrew or MacPorts libusb. They are safe in a
# way a dependency path is not: an rpath entry that does not exist is skipped,
# a dependency that does not exist is a load failure. They are last, after
# @loader_path and @loader_path/../Frameworks, so a bundled copy always wins.
#
# What this half refuses is an UNEXPECTED absolute rpath: a build directory, a
# home directory, a Cellar version that pins one release of libusb.
execute_process(
    COMMAND "${OTOOL_EXECUTABLE}" -l "${ODIN_LIBRARY}"
    OUTPUT_VARIABLE LOAD_COMMANDS
    ERROR_VARIABLE error
    RESULT_VARIABLE status
    OUTPUT_STRIP_TRAILING_WHITESPACE)

if(NOT status EQUAL 0)
    message(FATAL_ERROR "otool -l failed: ${error}")
endif()

set(ODIN_ALLOWED_RPATHS
    "@loader_path"
    "@loader_path/../Frameworks"
    "@executable_path"
    "@executable_path/../Frameworks"
    "/opt/homebrew/lib"
    "/usr/local/lib"
    "/opt/local/lib")

string(REPLACE "\n" ";" LOAD_LINES "${LOAD_COMMANDS}")
set(SEEN_RPATH FALSE)
set(FOUND_RPATHS "")

foreach(line IN LISTS LOAD_LINES)
    string(STRIP "${line}" line)

    if(line MATCHES "^cmd LC_RPATH$")
        set(SEEN_RPATH TRUE)
    elseif(SEEN_RPATH AND line MATCHES "^path (.+) \\(offset [0-9]+\\)$")
        set(entry "${CMAKE_MATCH_1}")
        list(APPEND FOUND_RPATHS "${entry}")
        set(SEEN_RPATH FALSE)

        list(FIND ODIN_ALLOWED_RPATHS "${entry}" allowed)

        if(allowed EQUAL -1)
            message(FATAL_ERROR
                    "${ODIN_LIBRARY} records an unexpected LC_RPATH entry: ${entry}\n"
                    "Only these are allowed, and the package-manager ones are last on "
                    "purpose so a bundled libusb wins:\n  ${ODIN_ALLOWED_RPATHS}")
        endif()
    endif()
endforeach()

message(STATUS "rpath: ${FOUND_RPATHS}")
