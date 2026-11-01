/* odin_bridge.h - EnneaOdin native bridge: a C ABI over Heimdall's C++.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * This header is the only interface the managed side of EnneaOdin uses to
 * reach the Samsung download-mode (Odin / LOKE) protocol. Behind it,
 * Heimdall's own C++ - BridgeManager, Interface and libpit, vendored under
 * native/vendor/Heimdall - is compiled into one shared library
 * (libenneaodin.dylib on macOS, libenneaodin.so on Linux) and linked into the
 * application's own process. There is no heimdall executable, no child
 * process and no argv parsing anywhere in this path.
 *
 * Conventions that hold for every function below.
 *
 *   Calling convention   cdecl. Every pointer-sized value is a plain pointer.
 *
 *   Return values        every operation returns an odin_status. ODIN_OK is 0.
 *                        A failure is never a thrown exception: no C++
 *                        exception can cross this boundary. Every entry point
 *                        catches std::bad_alloc, every other std::exception
 *                        and any unknown throw, and reports it as
 *                        ODIN_OUT_OF_MEMORY or ODIN_INTERNAL_ERROR. That is
 *                        deliberate - a std::bad_alloc unwinding into the CLR
 *                        is an immediate process kill, not a managed
 *                        exception.
 *
 *   Callbacks            an odin_callbacks pointer may be NULL, and each of
 *                        its members may be NULL. Every call is safe with no
 *                        callbacks at all; the operation still runs and still
 *                        reports its status. Callbacks are invoked only on the
 *                        thread that called the operation, only before that
 *                        call returns, and never after it. A callback must not
 *                        throw and must not call back into this library.
 *
 *   Logging              Heimdall writes its own diagnostics with vfprintf to
 *                        stdout and stderr and flushes after every line
 *                        (Interface.cpp:84-85). For the duration of a call
 *                        this library redirects file descriptors 1 and 2 into
 *                        a pipe, reads it on a helper thread so a long
 *                        operation cannot fill and block on it, and delivers
 *                        whole lines to odin_callbacks::log. Heimdall's C++ is
 *                        not modified to do this. Two consequences: the
 *                        redirect is process-wide, so anything else the
 *                        process prints during a call is captured too, and
 *                        when two operations overlap only the first captures -
 *                        the second says so through its own log callback and
 *                        runs normally.
 *
 *   Strings              every string in and out of this ABI is NUL-terminated
 *                        UTF-8, and in practice ASCII. Returned const char *
 *                        values point at static storage and are valid for the
 *                        life of the library.
 *
 *   Threading            an odin_session is not thread-safe. Drive one session
 *                        from one thread at a time. The single exception is
 *                        odin_cancel, odin_cancel_reset and odin_is_cancelled,
 *                        which are safe to call from any thread at any time,
 *                        including while an operation is running on another
 *                        thread - that is what makes a Stop button work.
 *
 *   No device here       nothing in this header has been exercised against a
 *                        Samsung handset. The device-free paths - version,
 *                        detect-with-no-device, PIT parsing, the stream
 *                        adapter - have been run; the flashing path has not.
 */

#ifndef ODIN_BRIDGE_H
#define ODIN_BRIDGE_H

#include <stddef.h>

#if defined(_WIN32)
#  define ODIN_API __declspec(dllexport)
#else
#  define ODIN_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Bumped whenever anything in this header changes shape. odin_abi_version()
   returns the value the loaded library was built with, so a managed caller can
   refuse a mismatched library instead of corrupting its own stack.

   2: added odin_send_total_bytes, odin_enable_tflash and
      odin_session_geometry. Nothing that existed in 1 changed shape, but the
      managed caller requires all three, so a library reporting 1 is refused
      rather than probed symbol by symbol. */
/* 3: odin_session_geometry gained device_default_part_size. The struct grew,
 *    so a managed caller built against 2 would read one field short of the
 *    end; the version is what refuses that pairing instead of letting it
 *    read rubbish.
 * 2: odin_session_geometry_of.
 * 1: the original surface. */
#define ODIN_ABI_VERSION 5

/* ------------------------------------------------------------------------- */
/* Status                                                                    */
/* ------------------------------------------------------------------------- */

typedef enum odin_status
{
    /* The operation did what it said. */
    ODIN_OK = 0,

    /* No supported download-mode device is present. Returned by odin_detect,
       odin_probe_device and odin_open. It is not an error condition: it is the
       normal answer when no handset is plugged in. */
    ODIN_NO_DEVICE = 1,

    /* The operation failed. Heimdall's own reason has already been delivered
       to the log callback; this ABI deliberately does not paraphrase it. */
    ODIN_FAILED = 2,

    /* The operation stopped because odin_cancel was called. The session is
       left with its USB interface still claimed and, if a session had been
       begun, still begun: end it with odin_end_session and then odin_close. */
    ODIN_CANCELLED = 3,

    /* A caller argument was NULL, out of range, or self-contradictory. No
       device traffic happened. */
    ODIN_INVALID_ARGUMENT = 4,

    /* The call does not make sense in this session's current state, for
       instance sending a file before odin_begin_session. No device traffic
       happened. */
    ODIN_INVALID_STATE = 5,

    /* The caller's buffer is too small. The size needed has been written to
       the operation's out-size parameter. Nothing was copied. */
    ODIN_BUFFER_TOO_SMALL = 6,

    /* The platform's C library cannot do what this call needs. The only case
       today is odin_send_file_stream on a libc that has neither funopen nor
       fopencookie; see that function. */
    ODIN_UNSUPPORTED = 7,

    /* An allocation failed inside the native code. Caught at the boundary; the
       CLR is never asked to unwind a std::bad_alloc. */
    ODIN_OUT_OF_MEMORY = 8,

    /* A C++ exception that is not std::bad_alloc was caught at the boundary,
       or an internal invariant did not hold. Treat it as a bug in this
       library. */
    ODIN_INTERNAL_ERROR = 9
} odin_status;

/* A short ASCII description of a status code. Never NULL, never allocates. */
ODIN_API const char *odin_status_text(int status);

/* ------------------------------------------------------------------------- */
/* Identity                                                                  */
/* ------------------------------------------------------------------------- */

/* ODIN_ABI_VERSION as compiled into the library that is actually loaded. */
ODIN_API int odin_abi_version(void);

/* The version string of the Heimdall C++ linked into this library. It is not
   a copy of a literal: Interface.cpp:45 defines "const char *version" at
   global scope with external linkage, and that object is what this returns. */
ODIN_API const char *odin_heimdall_version(void);

/* The EnneaOdin bridge's own version string, independent of Heimdall's. */
ODIN_API const char *odin_bridge_version(void);

/* ------------------------------------------------------------------------- */
/* Callbacks                                                                 */
/* ------------------------------------------------------------------------- */

/* One whole line of output, with no trailing newline. Heimdall's in-place
   progress writing (it prints "0%" and then backspaces over it) is resolved
   before delivery, so a line is never a mess of control characters. */
typedef void (*odin_log_fn)(const char *line, void *user);

/* transferred and total are byte counts; total is 0 when it is not known.
   Called often - for odin_send_file_* it is called once per protocol file
   part, which is 131072 bytes by default - so it must be cheap. */
typedef void (*odin_progress_fn)(unsigned long long transferred,
                                 unsigned long long total,
                                 void *user);

typedef struct odin_callbacks
{
    odin_log_fn      log;      /* may be NULL */
    odin_progress_fn progress; /* may be NULL; ignored by short operations */
    void            *user;     /* passed through untouched to both */
} odin_callbacks;

/* ------------------------------------------------------------------------- */
/* Session                                                                   */
/* ------------------------------------------------------------------------- */

/* Opaque. One odin_session owns at most one Heimdall BridgeManager and
   therefore at most one libusb context and one claimed USB interface. */
typedef struct odin_session odin_session;

/* USB log level, matching BridgeManager::UsbLogLevel. */
typedef enum odin_usb_log_level
{
    ODIN_USB_LOG_NONE    = 0,
    ODIN_USB_LOG_ERROR   = 1,
    ODIN_USB_LOG_WARNING = 2,
    ODIN_USB_LOG_INFO    = 3,
    ODIN_USB_LOG_DEBUG   = 4
} odin_usb_log_level;

/* Destination of a file transfer, matching
   EndFileTransferPacket::kDestination*. */
typedef enum odin_destination
{
    ODIN_DESTINATION_PHONE = 0,
    ODIN_DESTINATION_MODEM = 1
} odin_destination;

/* Used where BridgeManager::SendFile takes no file identifier. */
#define ODIN_NO_FILE_IDENTIFIER 0xFFFFFFFFu

/* Creates a session. No USB access happens here and no device is touched.
 *
 * verbose          non-zero turns on Heimdall's verbose logging, which is what
 *                  prints the USB descriptor dump.
 * wait_for_device  non-zero makes odin_detect and odin_open wait for a device
 *                  to appear instead of failing at once. The wait is a one
 *                  second poll loop and it honours odin_cancel.
 *
 * Writes the new session to *out_session on success. */
ODIN_API int odin_session_create(int verbose, int wait_for_device,
                                 odin_session **out_session);

/* Ends any session, releases the USB interface, exits libusb and frees the
   handle. Safe with NULL. The handle must not be used afterwards. This does
   not reboot or otherwise signal the device: use odin_end_session first if
   the device should be told. */
ODIN_API void odin_session_release(odin_session *session);

/* Tears down the BridgeManager - releasing the USB interface and exiting
   libusb - but keeps the session handle, which may be opened again. */
ODIN_API int odin_close(odin_session *session, const odin_callbacks *cb);

/* ------------------------------------------------------------------------- */
/* Cancellation                                                              */
/* ------------------------------------------------------------------------- */

/* Asks the running operation to stop. Safe from any thread, including while
 * an operation is in flight. It sets a flag; it does not interrupt a USB
 * transfer in progress.
 *
 * What that means in practice, by operation:
 *
 *   odin_send_file_path, odin_send_file_stream
 *       checked before each protocol file part, so cancellation lands within
 *       one part - 131072 bytes by default, plus however long the part
 *       already in flight takes to time out, at most 3 seconds.
 *   odin_download_pit
 *       checked before each PIT part request.
 *   odin_detect, odin_open with wait_for_device
 *       checked once per second, between polls.
 *   odin_begin_session, odin_end_session, odin_send_pit,
 *   odin_request_device_type, odin_close_pc_screen
 *       short, bounded operations. The flag is read when they start and not
 *       again: they are not interruptible in the middle of a handshake,
 *       because stopping half way through one would leave the device in a
 *       state this code cannot describe.
 *
 * A cancelled operation returns ODIN_CANCELLED and stops between packets, not
 * inside one, so the device is left at a packet boundary. The flag stays set
 * until odin_cancel_reset, so a cancelled flash cannot silently continue with
 * the next file. */
ODIN_API void odin_cancel(odin_session *session);

/* Clears the cancellation flag. Call it before reusing a session that was
   cancelled. */
ODIN_API void odin_cancel_reset(odin_session *session);

/* Non-zero when cancellation has been requested and not yet reset. */
ODIN_API int odin_is_cancelled(const odin_session *session);

/* ------------------------------------------------------------------------- */
/* Device operations                                                         */
/* ------------------------------------------------------------------------- */

/* Is a supported download-mode device present? ODIN_OK yes, ODIN_NO_DEVICE
 * no. This runs BridgeManager::DetectDevice on a BridgeManager of its own and
 * throws it away afterwards, so it neither needs nor disturbs an open session
 * and can be polled while one is open.
 *
 * Why its own BridgeManager: BridgeManager::DetectDevice and
 * BridgeManager::Initialise each call libusb_init on the same member, so
 * calling both on one object overwrites and leaks the first context. Upstream
 * never does both, and neither does this ABI. */
ODIN_API int odin_detect(odin_session *session, const odin_callbacks *cb);

/* The same question without a session, for a caller that only wants to poll.
   Equivalent to creating a non-verbose session that does not wait, calling
   odin_detect and releasing it. */
ODIN_API int odin_probe_device(const odin_callbacks *cb);

/* Reset the attached download-mode device: libusb_reset_device on a handle of
 * its own, opened and closed here.
 *
 * This exists because a reset RE-ENUMERATES the device and so invalidates
 * every handle to it. It therefore cannot be done in the middle of an open
 * session, or even in the middle of opening one: the caller must reset, wait
 * for the device to come back, and then open it again from scratch.
 *
 * Returns ODIN_OK, ODIN_NO_DEVICE if nothing is attached, or ODIN_FAILED.
 * Writes nothing to the handset. */
ODIN_API int odin_reset_device(const odin_callbacks *cb);

/* Ask the attached download-mode handset what it is, with the protocol's
 * "DVIF" command, and copy its reply into buffer.
 *
 * The reply is up to 256 bytes of
 *   @#MODEL=..;UN=..;CAPA=..;VENDOR=..;FWVER=..;PRODUCT=..;PROV=..;
 *     SALES=..;VER=..;DID=..;TMU_TEMP=..;NAD_TEMP=..;@#
 *
 * This does NOT open a session and does NOT consume the one ODIN handshake the
 * handset will answer per entry into download mode: DVIF is a separate branch
 * of the bootloader's first-command chain and does not set its latch. So it is
 * safe to call on connect, before anything else.
 *
 * Returns ODIN_OK with buffer NUL-terminated, ODIN_NO_DEVICE, or ODIN_FAILED.
 * Writes nothing to the handset. */
ODIN_API int odin_read_device_info(char *buffer, int size, const odin_callbacks *cb);

/* BridgeManager::Initialise. Finds the device, claims its interface, sets up
 * the alternate setting and, unless resuming, performs the LOKE handshake.
 *
 * resume   non-zero skips the handshake, for a device that is already in a
 *          session because an earlier run left it there.
 *
 * ODIN_NO_DEVICE when nothing is plugged in, ODIN_FAILED when the device is
 * there but could not be claimed or would not talk. On Linux a claim failure
 * is usually permissions: see the udev rules this project installs. */
ODIN_API int odin_open(odin_session *session, int resume,
                       const odin_callbacks *cb);

/* BridgeManager::BeginSession. Must follow a successful odin_open. This is
   where the device negotiates the file-part size, and where a handset can
   take up to two minutes to answer. */
ODIN_API int odin_begin_session(odin_session *session,
                                const odin_callbacks *cb);

/* BridgeManager::EndSession. reboot non-zero also sends the reboot packet.
   Must follow a successful odin_begin_session. */
ODIN_API int odin_end_session(odin_session *session, int reboot,
                              const odin_callbacks *cb);

/* The last step of heimdall's "close-pc-screen" action: it ends the session,
 * which is what clears the "Downloading... Do not turn off target" screen.
 * reboot non-zero reboots as well.
 *
 * This is odin_end_session with one extra log line, kept as its own entry
 * point because closing the PC screen is a thing a user asks for by name and
 * the GUI should not have to know it is the same packet. */
ODIN_API int odin_close_pc_screen(odin_session *session, int reboot,
                                  const odin_callbacks *cb);

/* BridgeManager::RequestDeviceType. Writes the device's answer to *out_value.
   request is a device-type request id; Heimdall's own flash path uses the
   values in DeviceTypePacket.h. */
ODIN_API int odin_request_device_type(odin_session *session,
                                      unsigned int request, int *out_value,
                                      const odin_callbacks *cb);

/* BridgeManager::SetUsbLogLevel, taking an odin_usb_log_level. Valid before
   or after opening; the level is remembered and reapplied. */
ODIN_API int odin_set_usb_log_level(odin_session *session, int level,
                                    const odin_callbacks *cb);

/* The session-setup packet that declares how many bytes the whole flash will
 * send, which Odin sends before the first image. Port of
 * FlashAction.cpp:155-201 (sendTotalTransferSize), minus the summing: the
 * caller knows the plan, this does not.
 *
 * Heimdall reaches this through BridgeManager::SendPacket and ReceivePacket,
 * which are public (BridgeManager.h:158-159), so no vendored code is
 * duplicated here. A non-zero result field is ODIN_FAILED, exactly as
 * FlashAction.cpp:194-198 treats it. Must follow odin_begin_session. */
ODIN_API int odin_send_total_bytes(odin_session *session,
                                   unsigned long long total_bytes,
                                   const odin_callbacks *cb);

/* Redirect the flash to the SD card instead of the handset's own storage.
 * Port of FlashAction.cpp:434-466 (enableTFlash). Note the 5000 ms response
 * timeout, which is longer than the usual 3000 (FlashAction.cpp:449).
 * Must follow odin_begin_session. */
ODIN_API int odin_enable_tflash(odin_session *session,
                                const odin_callbacks *cb);

/* The transfer geometry the BridgeManager is currently using: before
 * odin_begin_session these are the pre-negotiation defaults
 * (BridgeManager.cpp:74-76), afterwards they are whatever BeginSession
 * settled on (BridgeManager.cpp:530-534).
 *
 * This exists because the numbers are private members of BridgeManager and a
 * caller that shows them on screen must not guess which set is in force.
 *
 * The device's raw negotiation result USED to be unrecoverable - it was a
 * local variable inside BeginSession and the only way to tell negotiation had
 * happened was to notice file_part_size differing from the default, which is
 * an inference and not a reading. EnneaOdin now keeps it on the BridgeManager
 * and reports it as device_default_part_size, so that inference is gone. */
typedef struct odin_session_geometry
{
    unsigned int file_part_size;      /* bytes per file part */
    unsigned int sequence_max_length; /* parts per sequence */
    unsigned int sequence_timeout_ms; /* end-of-sequence timeout */

    /* What the HANDSET said, in the begin-session response, before anything
     * was negotiated. Zero has two meanings and they are distinguishable only
     * by whether a session has been begun:
     *
     *   before odin_begin_session   the device has not been asked yet
     *   after  odin_begin_session   the device refuses a packet-size change,
     *                               which is what a zero response means
     *                               (BridgeManager.cpp:558) and is worth
     *                               showing a user wondering why a transfer
     *                               is running at the 131072-byte default.
     *
     * Added in ABI 3. Upstream reads this value into a local, branches on it
     * and discards it; EnneaOdin keeps it on the BridgeManager so it can be
     * reported. See NOTICE. */
    unsigned int device_default_part_size;
} odin_session_geometry;

ODIN_API int odin_session_geometry_of(const odin_session *session,
                                      odin_session_geometry *out);

/* ------------------------------------------------------------------------- */
/* PIT                                                                       */
/* ------------------------------------------------------------------------- */

/* Downloads the device's PIT into a buffer the CALLER owns. Heimdall's
 * BridgeManager::DownloadPitFile hands back a new[] buffer that the caller
 * would have to delete[] with the matching allocator; that cannot be done
 * safely from managed code, so this ABI never hands out a native allocation.
 *
 * There is no way to ask the size in advance: the device only reports it as
 * part of the transfer. A PIT is a few kilobytes - this handset's is 7412
 * bytes - so pass a buffer of ODIN_PIT_MAX_SIZE and be done. A NULL buffer or
 * a capacity of 0 is ODIN_INVALID_ARGUMENT and talks to no device.
 *
 * On ODIN_OK, *out_size holds the number of bytes written. On
 * ODIN_BUFFER_TOO_SMALL the transfer did happen, *out_size holds the size that
 * would have been needed, and the downloaded bytes have been discarded. */
#define ODIN_PIT_MAX_SIZE 65536u

ODIN_API int odin_download_pit(odin_session *session, unsigned char *buffer,
                               size_t capacity, size_t *out_size,
                               const odin_callbacks *cb);

/* Sends a PIT to the device: a repartition. data/length is a complete PIT
 * file as it would sit on disk.
 *
 * The bytes are parsed with libpit before anything is sent, and a PIT that
 * does not parse is refused with ODIN_INVALID_ARGUMENT without touching the
 * device. That check is not decoration: PitData::Unpack reads its entry count
 * out of the file and then reads that many 132-byte entries with no bounds
 * check of its own, so the length validation lives here, in front of it.
 *
 * This writes the partition table of the handset. There is no undo. */
ODIN_API int odin_send_pit(odin_session *session, const unsigned char *data,
                           size_t length, const odin_callbacks *cb);

/* An opaque parsed PIT. Parsing needs no device and no session. */
typedef struct odin_pit odin_pit;

/* Fixed-size, blittable views of libpit's PitData and PitEntry. The char
   arrays are 33 bytes: libpit's fields are 32 bytes and are not required to be
   NUL-terminated, so one byte is added and always set. */
typedef struct odin_pit_header
{
    unsigned int   entry_count;
    unsigned int   lu_count;
    char           com_tar2[9];
    char           cpu_bl_id[9];
} odin_pit_header;

typedef struct odin_pit_entry
{
    unsigned int binary_type;
    unsigned int device_type;
    unsigned int identifier;
    unsigned int attributes;
    unsigned int update_attributes;
    unsigned int block_size_or_offset;
    unsigned int block_count;
    unsigned int file_offset;         /* obsolete in the format itself */
    unsigned int file_size;           /* obsolete in the format itself */
    int          is_flashable;        /* non-zero when the name is not empty */
    char         partition_name[33];
    char         flash_filename[33];
    char         fota_filename[33];
} odin_pit_entry;

/* Parses a PIT file. Writes the handle to *out_pit on success. Rejects, with
   ODIN_INVALID_ARGUMENT, a buffer that is too short for its own declared
   entry count or that does not carry the PIT magic. */
ODIN_API int odin_pit_parse(const unsigned char *data, size_t length,
                            odin_pit **out_pit, const odin_callbacks *cb);

/* Frees a parsed PIT. Safe with NULL. */
ODIN_API void odin_pit_release(odin_pit *pit);

/* Number of entries, or 0 if pit is NULL. */
ODIN_API unsigned int odin_pit_entry_count(const odin_pit *pit);

/* Copies the header fields into *out. */
ODIN_API int odin_pit_header_of(const odin_pit *pit, odin_pit_header *out);

/* Copies entry [index] into *out. ODIN_INVALID_ARGUMENT if index is past the
   end. */
ODIN_API int odin_pit_entry_at(const odin_pit *pit, unsigned int index,
                               odin_pit_entry *out);

/* ------------------------------------------------------------------------- */
/* Sending a file                                                            */
/* ------------------------------------------------------------------------- */

/* BridgeManager::SendFile against a file on disk. The native side opens it
 * with fopen in "rb" and closes it before returning.
 *
 * destination      an odin_destination.
 * device_type      the device type the PIT entry asks for.
 * file_identifier  the PIT entry's identifier for a phone file, or
 *                  ODIN_NO_FILE_IDENTIFIER. A modem file must use
 *                  ODIN_NO_FILE_IDENTIFIER; Heimdall refuses otherwise.
 *
 * Progress is reported per file part, not per percent: cb->progress receives
 * the running byte count and the file size. */
ODIN_API int odin_send_file_path(odin_session *session, const char *path,
                                 unsigned int destination,
                                 unsigned int device_type,
                                 unsigned int file_identifier,
                                 const odin_callbacks *cb);

/* How a managed stream reaches BridgeManager::SendFile, which takes a FILE *.
 *
 * The problem is real and not hypothetical: every member of this handset's
 * Odin packages is .lz4, AP.tar is 6.7 GB and its super.img.lz4 expands to
 * 5.5 GB. The bytes SendFile has to send therefore do not exist as a file on
 * disk, and spooling them to one would mean writing several gigabytes to the
 * user's disk before flashing a single byte.
 *
 * So the caller supplies read and seek callbacks and this library wraps them
 * in a real FILE * - funopen on macOS and the BSDs, fopencookie on glibc -
 * which is then handed to SendFile unchanged. No temporary file, no extra
 * copy of the data, and Heimdall's C++ is untouched.
 *
 * The stream is set unbuffered, so stdio never reads ahead and every fread
 * SendFile performs becomes exactly one read callback.
 *
 * What the stream must support. SendFilePartPacket (SendFilePartPacket.h:38)
 * asks the FILE * for its position, seeks to the end to learn the size, seeks
 * straight back and then reads. The adapter answers the first three from the
 * length argument and its own byte counter, without calling into managed code
 * at all, so a forward-only stream - an LZ4 decoder, say - works unchanged and
 * seek may be NULL. seek is only ever called for a real backwards jump, which
 * happens when a transfer is retried; a stream that cannot do it should return
 * non-zero and the operation fails cleanly.
 *
 * length must be the exact number of bytes the stream will produce. It is what
 * the device is told to expect.
 *
 * A read that fails stops the transfer. That needs saying because upstream
 * would not: SendFilePartPacket ignores what fread returned and sends a packet
 * it had already zeroed, so a stream that dies half way through a partition
 * would write zeros to the handset and report success. This library watches
 * for the failure and stops at the next part boundary with ODIN_FAILED. The
 * partition is then half written and must be flashed again - but the caller is
 * told, which is the part that matters. For the same reason the number of
 * bytes the stream really produced is compared with length at the end, and a
 * stream that was short of what it declared is ODIN_FAILED too.
 *
 * ODIN_UNSUPPORTED is returned, before anything is sent, on a libc that has
 * neither funopen nor fopencookie - musl is the case to care about. A caller
 * that has to run there must spool to a temporary file and use
 * odin_send_file_path. */
typedef struct odin_stream
{
    /* Reads at most count bytes into buffer. Returns the number read, 0 at
       end of stream, or a negative value on error. Must not throw. */
    long long (*read)(void *user, unsigned char *buffer,
                      unsigned long long count);

    /* Seeks to an absolute byte offset. Returns 0 on success, non-zero if the
       stream cannot. May be NULL for a forward-only stream. */
    int (*seek)(void *user, unsigned long long offset);

    void *user;
} odin_stream;

ODIN_API int odin_send_file_stream(odin_session *session,
                                   const odin_stream *stream,
                                   unsigned long long length,
                                   unsigned int destination,
                                   unsigned int device_type,
                                   unsigned int file_identifier,
                                   const odin_callbacks *cb);

/* Drives a caller's odin_stream through exactly the access pattern
 * SendFilePartPacket uses, in part_size chunks, and reports what came back.
 * It touches no USB and needs no device.
 *
 * It exists because the FILE * adapter above depends on fine details of how
 * stdio treats a custom stream, and those details deserve to be proven
 * without a handset in the loop. Point it at a stream over a file whose
 * contents are known, compare *out_crc32 and *out_total_read with the file,
 * and the adapter is either right or it is not.
 *
 * out_reported_size receives what the FILE * answered when asked for its size
 * - that is what SendFile would tell the device.
 * out_crc32 is CRC-32 (IEEE 802.3, the zlib polynomial) over the bytes read,
 * end padding excluded.
 * Any out parameter may be NULL. */
ODIN_API int odin_stream_probe(const odin_stream *stream,
                               unsigned long long length,
                               unsigned long part_size,
                               unsigned long long *out_total_read,
                               unsigned long long *out_reported_size,
                               unsigned int *out_crc32,
                               const odin_callbacks *cb);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* ODIN_BRIDGE_H */
