/* odin_bridge.cpp - the C ABI declared in odin_bridge.h.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Everything here is a thin, explicit layer over Heimdall's BridgeManager and
 * libpit, vendored under native/vendor/Heimdall. Three rules shape the file.
 *
 *   1. No C++ exception may cross the boundary. Every entry point that can
 *      allocate is wrapped in ODIN_TRY / ODIN_CATCH, which turns a throw into
 *      a status code. A std::bad_alloc unwinding into the CLR is not a managed
 *      exception, it is a dead process.
 *
 *   2. No native allocation is handed to the caller. The PIT download copies
 *      into the caller's buffer and deletes Heimdall's new[] buffer here,
 *      where the matching delete[] exists.
 *
 *   3. Every operation runs inside an Operation, which owns the stdout/stderr
 *      capture, installs itself as the cancellation and progress context for
 *      the duration, and flushes captured lines to the log callback as the
 *      operation proceeds rather than only at the end.
 */

#include "odin_bridge.h"

#include "OdinHooks.h"
#include "OdinLogCapture.h"
#include "OdinStreamFile.h"

/* Vendored Heimdall. */
#include "BridgeManager.h"
#include "EnableTFlashPacket.h"
#include "EndFileTransferPacket.h"
#include "Interface.h"
#include "SessionSetupResponse.h"
#include "TotalBytesPacket.h"
#include "libpit.h"

/* Only odin_reset_device touches libusb directly; everything else goes
   through BridgeManager, whose header forward-declares the types. */
#include <libusb.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <new>
#include <stdexcept>
#include <string>
#include <vector>

using Heimdall::BridgeManager;

/* The ABI's own constants are fixed by this header, not by the vendored code,
   so that a managed caller never has to chase an upstream enum. These assert
   that the two have not drifted apart - if upstream renumbers anything, the
   build stops here instead of a handset being told to flash the wrong
   destination. */
static_assert(static_cast<int>(ODIN_DESTINATION_PHONE) ==
              static_cast<int>(Heimdall::EndFileTransferPacket::kDestinationPhone),
              "ODIN_DESTINATION_PHONE must match EndFileTransferPacket::kDestinationPhone");
static_assert(static_cast<int>(ODIN_DESTINATION_MODEM) ==
              static_cast<int>(Heimdall::EndFileTransferPacket::kDestinationModem),
              "ODIN_DESTINATION_MODEM must match EndFileTransferPacket::kDestinationModem");
static_assert(static_cast<int>(ODIN_USB_LOG_NONE) ==
              static_cast<int>(BridgeManager::UsbLogLevel::None) &&
              static_cast<int>(ODIN_USB_LOG_ERROR) ==
              static_cast<int>(BridgeManager::UsbLogLevel::Error) &&
              static_cast<int>(ODIN_USB_LOG_WARNING) ==
              static_cast<int>(BridgeManager::UsbLogLevel::Warning) &&
              static_cast<int>(ODIN_USB_LOG_INFO) ==
              static_cast<int>(BridgeManager::UsbLogLevel::Info) &&
              static_cast<int>(ODIN_USB_LOG_DEBUG) ==
              static_cast<int>(BridgeManager::UsbLogLevel::Debug),
              "odin_usb_log_level must match BridgeManager::UsbLogLevel");
static_assert(ODIN_NO_FILE_IDENTIFIER == 0xFFFFFFFFu,
              "ODIN_NO_FILE_IDENTIFIER is the value BridgeManager::SendFile defaults to");

/* ------------------------------------------------------------------------- */
/* Exception boundary                                                        */
/* ------------------------------------------------------------------------- */

namespace
{
	void ReportLiteral(const odin_callbacks *cb, const char *line)
	{
		if (cb != nullptr && cb->log != nullptr)
			cb->log(line, cb->user);
	}

	/* snprintf into a stack buffer, never a std::string: this runs inside a
	   catch handler, where an allocation that throws would escape the
	   function and cross the ABI, which is the exact thing being prevented. */
	int ReportException(const odin_callbacks *cb, const char *what)
	{
		if (cb != nullptr && cb->log != nullptr)
		{
			char line[320];
			snprintf(line, sizeof line, "ERROR: native: %s",
			         (what != nullptr) ? what : "unspecified C++ exception");
			cb->log(line, cb->user);
		}

		return ODIN_INTERNAL_ERROR;
	}
}

#define ODIN_TRY try {

#define ODIN_CATCH(cb) \
	} \
	catch (const std::bad_alloc &) \
	{ \
		ReportLiteral((cb), "ERROR: native: out of memory"); \
		return ODIN_OUT_OF_MEMORY; \
	} \
	catch (const std::exception &error) \
	{ \
		return ReportException((cb), error.what()); \
	} \
	catch (...) \
	{ \
		ReportLiteral((cb), "ERROR: native: unknown C++ exception at the ABI boundary"); \
		return ODIN_INTERNAL_ERROR; \
	}

/* ------------------------------------------------------------------------- */
/* Session                                                                   */
/* ------------------------------------------------------------------------- */

struct odin_session
{
	BridgeManager              *bridge;
	bool                        verbose;
	bool                        waitForDevice;
	bool                        opened;
	bool                        sessionBegun;
	BridgeManager::UsbLogLevel  usbLogLevel;
	std::atomic<bool>           cancelled;

	odin_session(bool verboseValue, bool waitValue)
		: bridge(nullptr),
		  verbose(verboseValue),
		  waitForDevice(waitValue),
		  opened(false),
		  sessionBegun(false),
		  usbLogLevel(BridgeManager::UsbLogLevel::Default),
		  cancelled(false)
	{
	}
};

struct odin_pit
{
	libpit::PitData data;
};

namespace
{
	/* ------------------------------------------------------------------- */
	/* Operation: one ABI call                                             */
	/* ------------------------------------------------------------------- */

	class Operation : public EnneaOdin::Hooks::Context
	{
		public:

			Operation(odin_session *sessionValue, const odin_callbacks *cbValue)
				: session(sessionValue),
				  cb(cbValue),
				  previous(nullptr),
				  abortFlag(nullptr)
			{
				previous = EnneaOdin::Hooks::Install(this);

				if (!capture.Active())
					ReportLiteral(cb, "NOTE: another native operation holds the output "
					                  "capture; this one's Heimdall output is not captured.");
			}

			~Operation()
			{
				/* A destructor that throws during unwinding ends the process,
				   so nothing in here may escape. */
				try
				{
					capture.Finish();
					FlushLog();
				}
				catch (...)
				{
				}

				EnneaOdin::Hooks::Install(previous);
			}

			/* Hooks::Context. Called from inside Heimdall's loops, between
			   packets. */
			bool OnCancelCheck(void)
			{
				FlushLog();
				return Cancelled() || Aborted();
			}

			/* Watches a flag owned by something else - today the stream
			   adapter's read-error flag - and stops the transfer at the next
			   packet boundary when it is set. */
			void WatchAbortFlag(const bool *flag)
			{
				abortFlag = flag;
			}

			bool Aborted(void) const
			{
				return abortFlag != nullptr && *abortFlag;
			}

			void OnProgress(unsigned long long transferred, unsigned long long total)
			{
				FlushLog();

				if (cb != nullptr && cb->progress != nullptr)
					cb->progress(transferred, total, cb->user);
			}

			bool Cancelled(void) const
			{
				return session != nullptr && session->cancelled.load();
			}

			/* Always drains the capture, even with no log callback: the buffer
			   would otherwise grow for the whole length of a flash. */
			void FlushLog(void)
			{
				lines.clear();
				capture.Take(lines);

				if (cb == nullptr || cb->log == nullptr)
					return;

				for (size_t i = 0; i < lines.size(); i++)
					cb->log(lines[i].c_str(), cb->user);
			}

		private:

			Operation(const Operation &);
			Operation &operator=(const Operation &);

			/* Declared first so the redirect is in place before anything else
			   in this object exists. */
			EnneaOdin::LogCapture     capture;
			odin_session             *session;
			const odin_callbacks     *cb;
			EnneaOdin::Hooks::Context *previous;
			const bool               *abortFlag;
			std::vector<std::string>  lines;
	};

	/* ------------------------------------------------------------------- */
	/* Helpers                                                             */
	/* ------------------------------------------------------------------- */

	bool ValidLogLevel(int level)
	{
		return level >= ODIN_USB_LOG_NONE && level <= ODIN_USB_LOG_DEBUG;
	}

	BridgeManager::UsbLogLevel ToUsbLogLevel(int level)
	{
		switch (level)
		{
			case ODIN_USB_LOG_NONE:    return BridgeManager::UsbLogLevel::None;
			case ODIN_USB_LOG_WARNING: return BridgeManager::UsbLogLevel::Warning;
			case ODIN_USB_LOG_INFO:    return BridgeManager::UsbLogLevel::Info;
			case ODIN_USB_LOG_DEBUG:   return BridgeManager::UsbLogLevel::Debug;
			default:                   return BridgeManager::UsbLogLevel::Error;
		}
	}

	bool ValidDestination(unsigned int destination)
	{
		return destination == ODIN_DESTINATION_PHONE ||
		       destination == ODIN_DESTINATION_MODEM;
	}

	/* The PIT magic as libpit reads it: PitData::UnpackInteger takes the four
	   bytes little-endian on a little-endian host, which is every host this
	   project targets. */
	unsigned int ReadLittleEndian32(const unsigned char *data)
	{
		return static_cast<unsigned int>(data[0]) |
		       (static_cast<unsigned int>(data[1]) << 8) |
		       (static_cast<unsigned int>(data[2]) << 16) |
		       (static_cast<unsigned int>(data[3]) << 24);
	}

	/* No real PIT comes close. The cap exists because PitData::Unpack takes
	   the entry count straight from the file and multiplies. */
	const unsigned int kMaxPitEntries = 4096;

	/* Validates a PIT buffer and parses it into pit->data.
	   Returns an odin_status. */
	int ParsePit(const unsigned char *data, size_t length, libpit::PitData &out)
	{
		if (data == nullptr || length < libpit::PitData::kHeaderDataSize)
			return ODIN_INVALID_ARGUMENT;

		if (ReadLittleEndian32(data) != libpit::PitData::kFileIdentifier)
			return ODIN_INVALID_ARGUMENT;

		const unsigned int entryCount = ReadLittleEndian32(data + 4);

		if (entryCount > kMaxPitEntries)
			return ODIN_INVALID_ARGUMENT;

		const size_t needed = static_cast<size_t>(libpit::PitData::kHeaderDataSize) +
		                      static_cast<size_t>(entryCount) *
		                      static_cast<size_t>(libpit::PitEntry::kDataSize);

		if (length < needed)
			return ODIN_INVALID_ARGUMENT;

		/* Unpack is handed a zero-padded copy rather than the caller's buffer.
		   PitEntry::SetPartitionName and friends take strlen of a 32-byte
		   field that the format does not require to be terminated, so a field
		   of 32 non-zero bytes in the last entry would read past the end of a
		   buffer sized exactly to `needed`. The padding ends that read inside
		   memory this function owns. */
		std::vector<unsigned char> padded(needed + 64, 0);
		memcpy(&padded[0], data, needed);

		if (!out.Unpack(&padded[0]))
			return ODIN_FAILED;

		return ODIN_OK;
	}

	void CopyField(char *destination, size_t destinationSize, const char *source)
	{
		memset(destination, 0, destinationSize);

		if (source == nullptr)
			return;

		size_t i = 0;

		while (i + 1 < destinationSize && source[i] != '\0')
		{
			destination[i] = source[i];
			i++;
		}
	}

	/* CRC-32, IEEE 802.3 / zlib polynomial. Used only by odin_stream_probe,
	   so that a managed test can check the bytes the FILE * adapter produced
	   against the file it was pointed at. */
	unsigned int Crc32Update(unsigned int crc, const unsigned char *data, size_t length)
	{
		static unsigned int table[256];
		static bool ready = false;

		if (!ready)
		{
			for (unsigned int i = 0; i < 256; i++)
			{
				unsigned int value = i;

				for (int bit = 0; bit < 8; bit++)
					value = (value & 1u) ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);

				table[i] = value;
			}

			ready = true;
		}

		for (size_t i = 0; i < length; i++)
			crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);

		return crc;
	}
}

/* ------------------------------------------------------------------------- */
/* Identity                                                                  */
/* ------------------------------------------------------------------------- */

/* Interface.cpp:45 defines this at global scope - not in the Heimdall or
   Interface namespace - and declares it in no header. It has external linkage,
   so it is declared here instead of duplicating the literal, and
   odin_heimdall_version therefore reports what the linked C++ actually is. */
extern const char *version;

extern "C" ODIN_API const char *odin_heimdall_version(void)
{
	return version;
}

extern "C" ODIN_API const char *odin_bridge_version(void)
{
	return "1.0.0";
}

extern "C" ODIN_API int odin_abi_version(void)
{
	return ODIN_ABI_VERSION;
}

extern "C" ODIN_API const char *odin_status_text(int status)
{
	switch (status)
	{
		case ODIN_OK:               return "OK";
		case ODIN_NO_DEVICE:        return "no download-mode device found";
		case ODIN_FAILED:           return "the operation failed; see the log";
		case ODIN_CANCELLED:        return "cancelled at the caller's request";
		case ODIN_INVALID_ARGUMENT: return "invalid argument";
		case ODIN_INVALID_STATE:    return "the session is not in a state for that call";
		case ODIN_BUFFER_TOO_SMALL: return "the supplied buffer is too small";
		case ODIN_UNSUPPORTED:      return "not supported on this platform";
		case ODIN_OUT_OF_MEMORY:    return "out of memory";
		case ODIN_INTERNAL_ERROR:   return "internal error in the native bridge";
		default:                    return "unknown status";
	}
}

/* ------------------------------------------------------------------------- */
/* Session lifetime                                                          */
/* ------------------------------------------------------------------------- */

extern "C" ODIN_API int odin_session_create(int verbose, int wait_for_device,
                                            odin_session **out_session)
{
	if (out_session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	*out_session = nullptr;

	ODIN_TRY

	*out_session = new odin_session(verbose != 0, wait_for_device != 0);
	return ODIN_OK;

	ODIN_CATCH(nullptr)
}

extern "C" ODIN_API void odin_session_release(odin_session *session)
{
	if (session == nullptr)
		return;

	try
	{
		Operation operation(session, nullptr);

		delete session->bridge;
		session->bridge = nullptr;
	}
	catch (...)
	{
	}

	delete session;
}

extern "C" ODIN_API int odin_close(odin_session *session, const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	delete session->bridge;
	session->bridge = nullptr;
	session->opened = false;
	session->sessionBegun = false;

	return ODIN_OK;

	ODIN_CATCH(cb)
}

/* ------------------------------------------------------------------------- */
/* Cancellation                                                              */
/* ------------------------------------------------------------------------- */

extern "C" ODIN_API void odin_cancel(odin_session *session)
{
	if (session != nullptr)
		session->cancelled.store(true);
}

extern "C" ODIN_API void odin_cancel_reset(odin_session *session)
{
	if (session != nullptr)
		session->cancelled.store(false);
}

extern "C" ODIN_API int odin_is_cancelled(const odin_session *session)
{
	return (session != nullptr && session->cancelled.load()) ? 1 : 0;
}

/* ------------------------------------------------------------------------- */
/* Device operations                                                         */
/* ------------------------------------------------------------------------- */

extern "C" ODIN_API int odin_detect(odin_session *session, const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	/* A BridgeManager of its own, thrown away before returning. DetectDevice
	   and Initialise both call libusb_init on the same member, so an object
	   that did both would overwrite and leak the first context. This also
	   leaves an already-open session untouched, so the GUI can keep polling
	   for a device while one is open. */
	BridgeManager detector(session->verbose, session->waitForDevice);
	detector.SetUsbLogLevel(session->usbLogLevel);

	const bool found = detector.DetectDevice();

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	return found ? ODIN_OK : ODIN_NO_DEVICE;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_probe_device(const odin_callbacks *cb)
{
	ODIN_TRY

	Operation operation(nullptr, cb);

	BridgeManager detector(false, false);
	detector.SetUsbLogLevel(BridgeManager::UsbLogLevel::Error);

	return detector.DetectDevice() ? ODIN_OK : ODIN_NO_DEVICE;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_read_device_info(char *buffer, int size,
                                              const odin_callbacks *cb)
{
	if (buffer == nullptr || size < 2)
		return ODIN_INVALID_ARGUMENT;

	buffer[0] = '\0';

	ODIN_TRY

	Operation operation(nullptr, cb);

	/* resume = true, so Initialise chooses a transport and stops short of the
	   handshake. DVIF needs the transport and nothing else. */
	BridgeManager bridge(false, false);
	bridge.SetUsbLogLevel(BridgeManager::UsbLogLevel::Error);

	const int opened = bridge.Initialise(true);

	if (opened != BridgeManager::kInitialiseSucceeded)
	{
		return (opened == BridgeManager::kInitialiseDeviceNotDetected)
			? ODIN_NO_DEVICE
			: ODIN_FAILED;
	}

	return bridge.RequestDeviceInfo(buffer, size) ? ODIN_OK : ODIN_FAILED;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_reset_device(const odin_callbacks *cb)
{
	ODIN_TRY

	Operation operation(nullptr, cb);

	libusb_context *context = nullptr;

	if (libusb_init(&context) != LIBUSB_SUCCESS)
		return ODIN_FAILED;

	int result = ODIN_NO_DEVICE;

	/* The same three the engine matches on. Repeated here rather than read
	   from BridgeManager::supportedDevices, which is private: a vendored
	   header change would have to be declared in NOTICE, and these three
	   numbers are the whole content of it.
	   BridgeManager.h:71-78 - kVidSamsung, kPidGalaxyS, kPidGalaxyS2,
	   kPidDroidCharge. */
	static const struct { unsigned short vendor; unsigned short product; } known[] = {
		{ 0x04E8, 0x6601 },
		{ 0x04E8, 0x685D },
		{ 0x04E8, 0x68C3 }
	};

	for (unsigned int i = 0; i < sizeof known / sizeof known[0]; i++)
	{
		libusb_device_handle *handle = libusb_open_device_with_vid_pid(
			context, known[i].vendor, known[i].product);

		if (handle == nullptr)
			continue;

		const int reset = libusb_reset_device(handle);

		Heimdall::Interface::Print("Resetting the device: %s\n", libusb_error_name(reset));

		/* LIBUSB_ERROR_NOT_FOUND means the reset worked and the device
		   re-enumerated, which is the normal outcome and not a failure. */
		result = (reset == LIBUSB_SUCCESS || reset == LIBUSB_ERROR_NOT_FOUND)
			? ODIN_OK
			: ODIN_FAILED;

		libusb_close(handle);
		break;
	}

	libusb_exit(context);
	return result;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_open(odin_session *session, int resume,
                                  const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	/* Opening again starts from a clean BridgeManager; the old one's
	   destructor releases the interface and exits its libusb context. */
	delete session->bridge;
	session->bridge = nullptr;
	session->opened = false;
	session->sessionBegun = false;

	session->bridge = new BridgeManager(session->verbose, session->waitForDevice);
	session->bridge->SetUsbLogLevel(session->usbLogLevel);

	const int result = session->bridge->Initialise(resume != 0);

	if (result == BridgeManager::kInitialiseSucceeded)
	{
		session->opened = true;

		/* Even when resuming, odin_begin_session is still required: resume
		   only skips the LOKE handshake inside Initialise, and it is
		   BeginSession that negotiates the file-part size every transfer
		   depends on. Upstream's actions call BeginSession in both cases. */
		session->sessionBegun = false;
		return ODIN_OK;
	}

	delete session->bridge;
	session->bridge = nullptr;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	return (result == BridgeManager::kInitialiseDeviceNotDetected)
		? ODIN_NO_DEVICE
		: ODIN_FAILED;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_begin_session(odin_session *session,
                                           const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->opened || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	if (!session->bridge->BeginSession())
		return ODIN_FAILED;

	session->sessionBegun = true;
	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_end_session(odin_session *session, int reboot,
                                         const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	const bool success = session->bridge->EndSession(reboot != 0);

	/* Either the device left the session or this code no longer knows what
	   state it is in. Both mean the same thing here: do not offer to end it
	   again. */
	session->sessionBegun = false;

	return success ? ODIN_OK : ODIN_FAILED;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_close_pc_screen(odin_session *session, int reboot,
                                             const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	/* Printed through Heimdall's own Interface so it arrives in the log in
	   order with everything Heimdall prints next, rather than jumping ahead of
	   lines still sitting in the capture. */
	Heimdall::Interface::Print("Attempting to close the connect-to-PC screen...\n");

	const bool success = session->bridge->EndSession(reboot != 0);
	session->sessionBegun = false;

	if (success)
		Heimdall::Interface::Print("Attempt complete.\n");

	return success ? ODIN_OK : ODIN_FAILED;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_request_device_type(odin_session *session,
                                                 unsigned int request,
                                                 int *out_value,
                                                 const odin_callbacks *cb)
{
	if (session == nullptr || out_value == nullptr)
		return ODIN_INVALID_ARGUMENT;

	*out_value = 0;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	int value = 0;

	if (!session->bridge->RequestDeviceType(request, &value))
		return ODIN_FAILED;

	*out_value = value;
	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_set_usb_log_level(odin_session *session, int level,
                                               const odin_callbacks *cb)
{
	if (session == nullptr || !ValidLogLevel(level))
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	session->usbLogLevel = ToUsbLogLevel(level);

	if (session->bridge != nullptr)
		session->bridge->SetUsbLogLevel(session->usbLogLevel);

	return ODIN_OK;

	ODIN_CATCH(cb)
}

/* FlashAction.cpp:155-201, without the summing: the caller owns the plan and
   therefore the total. Driven through BridgeManager::SendPacket and
   ReceivePacket, both public at BridgeManager.h:158-159. */
extern "C" ODIN_API int odin_send_total_bytes(odin_session *session,
                                              unsigned long long total_bytes,
                                              const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	/* TotalBytesPacket takes a uint64_t and packs it as two 32-bit halves
	   (TotalBytesPacket.h:54-55), so the full 64 bits do reach the wire. */
	Heimdall::TotalBytesPacket totalBytesPacket(total_bytes);

	if (!session->bridge->SendPacket(&totalBytesPacket))
	{
		Heimdall::Interface::PrintError("Failed to send total bytes packet!\n");
		return ODIN_FAILED;
	}

	Heimdall::SessionSetupResponse totalBytesResponse;

	if (!session->bridge->ReceivePacket(&totalBytesResponse))
	{
		Heimdall::Interface::PrintError("Failed to receive session total bytes response!\n");
		return ODIN_FAILED;
	}

	const int result = totalBytesResponse.GetResult();

	if (result != 0)
	{
		Heimdall::Interface::PrintError("Unexpected session total bytes response!\nExpected: 0\nReceived:%d\n",
		                                result);
		return ODIN_FAILED;
	}

	return ODIN_OK;

	ODIN_CATCH(cb)
}

/* FlashAction.cpp:434-466. The 5000 ms receive timeout is upstream's, at
   FlashAction.cpp:449, and is longer than the 3000 ms default. */
extern "C" ODIN_API int odin_enable_tflash(odin_session *session,
                                           const odin_callbacks *cb)
{
	if (session == nullptr)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	Heimdall::EnableTFlashPacket enableTFlashPacket;

	if (!session->bridge->SendPacket(&enableTFlashPacket))
	{
		Heimdall::Interface::PrintError("Failed to send T-Flash packet!\n");
		return ODIN_FAILED;
	}

	Heimdall::SessionSetupResponse enableTFlashResponse;

	if (!session->bridge->ReceivePacket(&enableTFlashResponse, 5000))
	{
		Heimdall::Interface::PrintError("Failed to receive T-Flash response!\n");
		return ODIN_FAILED;
	}

	const unsigned int result = enableTFlashResponse.GetResult();

	if (result != 0)
	{
		Heimdall::Interface::PrintError("Unexpected T-Flash response!\nExpected: 0\nReceived: %d\n",
		                                result);
		return ODIN_FAILED;
	}

	return ODIN_OK;

	ODIN_CATCH(cb)
}

/* Reads the three geometry members through the accessors added to
   BridgeManager.h. Talks to no device, allocates nothing and installs no
   Operation, so it is safe to call while a transfer is in flight on the same
   session - it reads values that only BeginSession writes. */
extern "C" ODIN_API int odin_session_geometry_of(const odin_session *session,
                                                 odin_session_geometry *out)
{
	if (session == nullptr || out == nullptr)
		return ODIN_INVALID_ARGUMENT;

	memset(out, 0, sizeof *out);

	if (session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	out->file_part_size = session->bridge->GetFileTransferPacketSize();
	out->sequence_max_length = session->bridge->GetFileTransferSequenceMaxLength();
	out->sequence_timeout_ms = session->bridge->GetFileTransferSequenceTimeout();
	out->device_default_part_size = session->bridge->GetDeviceDefaultPacketSize();

	return ODIN_OK;
}

/* ------------------------------------------------------------------------- */
/* PIT                                                                       */
/* ------------------------------------------------------------------------- */

extern "C" ODIN_API int odin_download_pit(odin_session *session,
                                          unsigned char *buffer, size_t capacity,
                                          size_t *out_size,
                                          const odin_callbacks *cb)
{
	if (session == nullptr || buffer == nullptr || capacity == 0)
		return ODIN_INVALID_ARGUMENT;

	if (out_size != nullptr)
		*out_size = 0;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	unsigned char *devicePit = nullptr;
	const int size = session->bridge->DownloadPitFile(&devicePit);

	if (devicePit == nullptr || size <= 0)
	{
		delete [] devicePit;
		return operation.Cancelled() ? ODIN_CANCELLED : ODIN_FAILED;
	}

	if (out_size != nullptr)
		*out_size = static_cast<size_t>(size);

	/* Heimdall's buffer is new[]-allocated; the matching delete[] only exists
	   on this side of the boundary, so the bytes are copied into the caller's
	   buffer and the native allocation dies here. */
	if (capacity < static_cast<size_t>(size))
	{
		delete [] devicePit;
		return ODIN_BUFFER_TOO_SMALL;
	}

	memcpy(buffer, devicePit, static_cast<size_t>(size));
	delete [] devicePit;

	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_send_pit(odin_session *session,
                                      const unsigned char *data, size_t length,
                                      const odin_callbacks *cb)
{
	if (session == nullptr || data == nullptr || length == 0)
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	/* Parsed before a byte goes to the device. A PIT that does not parse here
	   is a PIT that must not become the handset's partition table. */
	libpit::PitData pit;
	const int parsed = ParsePit(data, length, pit);

	if (parsed != ODIN_OK)
	{
		Heimdall::Interface::PrintError("The PIT data is not a valid PIT file; nothing was sent.\n");
		return parsed;
	}

	return session->bridge->SendPitData(&pit) ? ODIN_OK : ODIN_FAILED;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_pit_parse(const unsigned char *data, size_t length,
                                       odin_pit **out_pit,
                                       const odin_callbacks *cb)
{
	if (out_pit == nullptr)
		return ODIN_INVALID_ARGUMENT;

	*out_pit = nullptr;

	ODIN_TRY

	odin_pit *pit = new odin_pit();
	const int parsed = ParsePit(data, length, pit->data);

	if (parsed != ODIN_OK)
	{
		delete pit;
		return parsed;
	}

	*out_pit = pit;
	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API void odin_pit_release(odin_pit *pit)
{
	delete pit;
}

extern "C" ODIN_API unsigned int odin_pit_entry_count(const odin_pit *pit)
{
	return (pit == nullptr) ? 0u : pit->data.GetEntryCount();
}

extern "C" ODIN_API int odin_pit_header_of(const odin_pit *pit, odin_pit_header *out)
{
	if (pit == nullptr || out == nullptr)
		return ODIN_INVALID_ARGUMENT;

	memset(out, 0, sizeof *out);

	out->entry_count = pit->data.GetEntryCount();
	out->lu_count = pit->data.GetLUCount();
	CopyField(out->com_tar2, sizeof out->com_tar2, pit->data.GetComTar2());
	CopyField(out->cpu_bl_id, sizeof out->cpu_bl_id, pit->data.GetCpuBlId());

	return ODIN_OK;
}

extern "C" ODIN_API int odin_pit_entry_at(const odin_pit *pit, unsigned int index,
                                          odin_pit_entry *out)
{
	if (pit == nullptr || out == nullptr)
		return ODIN_INVALID_ARGUMENT;

	if (index >= pit->data.GetEntryCount())
		return ODIN_INVALID_ARGUMENT;

	const libpit::PitEntry *entry = pit->data.GetEntry(index);

	if (entry == nullptr)
		return ODIN_INVALID_ARGUMENT;

	memset(out, 0, sizeof *out);

	out->binary_type = entry->GetBinaryType();
	out->device_type = entry->GetDeviceType();
	out->identifier = entry->GetIdentifier();
	out->attributes = entry->GetAttributes();
	out->update_attributes = entry->GetUpdateAttributes();
	out->block_size_or_offset = entry->GetBlockSizeOrOffset();
	out->block_count = entry->GetBlockCount();
	out->file_offset = entry->GetFileOffset();
	out->file_size = entry->GetFileSize();
	out->is_flashable = entry->IsFlashable() ? 1 : 0;

	CopyField(out->partition_name, sizeof out->partition_name, entry->GetPartitionName());
	CopyField(out->flash_filename, sizeof out->flash_filename, entry->GetFlashFilename());
	CopyField(out->fota_filename, sizeof out->fota_filename, entry->GetFotaFilename());

	return ODIN_OK;
}

/* ------------------------------------------------------------------------- */
/* Sending a file                                                            */
/* ------------------------------------------------------------------------- */

extern "C" ODIN_API int odin_send_file_path(odin_session *session, const char *path,
                                            unsigned int destination,
                                            unsigned int device_type,
                                            unsigned int file_identifier,
                                            const odin_callbacks *cb)
{
	if (session == nullptr || path == nullptr || !ValidDestination(destination))
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	FILE *file = fopen(path, "rb");

	if (file == nullptr)
	{
		Heimdall::Interface::PrintError("Failed to open the file to send.\n");
		return ODIN_INVALID_ARGUMENT;
	}

	const bool success = session->bridge->SendFile(file, destination, device_type,
	                                               file_identifier);
	fclose(file);

	if (!success)
		return operation.Cancelled() ? ODIN_CANCELLED : ODIN_FAILED;

	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_send_file_stream(odin_session *session,
                                              const odin_stream *stream,
                                              unsigned long long length,
                                              unsigned int destination,
                                              unsigned int device_type,
                                              unsigned int file_identifier,
                                              const odin_callbacks *cb)
{
	if (session == nullptr || stream == nullptr || stream->read == nullptr ||
	    !ValidDestination(destination))
		return ODIN_INVALID_ARGUMENT;

	ODIN_TRY

	Operation operation(session, cb);

	if (!session->sessionBegun || session->bridge == nullptr)
		return ODIN_INVALID_STATE;

	if (!EnneaOdin::StreamFile::Available())
	{
		Heimdall::Interface::PrintError(
			"This C library has neither funopen nor fopencookie, so a caller-supplied "
			"stream cannot be turned into a FILE *. Write the data to a file and use "
			"the file path instead.\n");
		return ODIN_UNSUPPORTED;
	}

	if (operation.Cancelled())
		return ODIN_CANCELLED;

	EnneaOdin::StreamFile::Observer observer;
	FILE *file = EnneaOdin::StreamFile::Open(stream, length, &observer);

	if (file == nullptr)
	{
		Heimdall::Interface::PrintError("Failed to wrap the caller's stream in a FILE *.\n");
		return ODIN_INTERNAL_ERROR;
	}

	/* A read error has to stop the transfer, not ride along with it:
	   SendFilePartPacket ignores what fread returned and sends a packet that
	   was memset to zero, so a stream that dies half way through a partition
	   would otherwise write zeros to the handset and report success. Watching
	   the flag turns that into a stop at the next part boundary. */
	operation.WatchAbortFlag(observer.failed);

	const bool success = session->bridge->SendFile(file, destination, device_type,
	                                               file_identifier);

	const unsigned long long consumed = (observer.consumed != nullptr) ? *observer.consumed : length;
	const int streamFailed = EnneaOdin::StreamFile::Close(file);
	operation.WatchAbortFlag(nullptr);

	if (streamFailed != 0)
	{
		Heimdall::Interface::PrintError("The caller's stream reported an error.\n");
		return ODIN_FAILED;
	}

	if (!success)
		return operation.Cancelled() ? ODIN_CANCELLED : ODIN_FAILED;

	/* The declared length is what the device was told to expect and what it
	   wrote. If the stream had fewer bytes than that, the difference went to
	   the handset as the zeroes SendFilePartPacket starts each packet with. */
	if (consumed != length)
	{
		Heimdall::Interface::PrintError(
			"The stream produced %llu of the %llu bytes it declared. The device was "
			"padded with zeroes; flash that partition again.\n", consumed, length);
		return ODIN_FAILED;
	}

	return ODIN_OK;

	ODIN_CATCH(cb)
}

extern "C" ODIN_API int odin_stream_probe(const odin_stream *stream,
                                          unsigned long long length,
                                          unsigned long part_size,
                                          unsigned long long *out_total_read,
                                          unsigned long long *out_reported_size,
                                          unsigned int *out_crc32,
                                          const odin_callbacks *cb)
{
	if (stream == nullptr || stream->read == nullptr || part_size == 0)
		return ODIN_INVALID_ARGUMENT;

	if (out_total_read != nullptr)
		*out_total_read = 0;

	if (out_reported_size != nullptr)
		*out_reported_size = 0;

	if (out_crc32 != nullptr)
		*out_crc32 = 0;

	ODIN_TRY

	Operation operation(nullptr, cb);

	if (!EnneaOdin::StreamFile::Available())
		return ODIN_UNSUPPORTED;

	FILE *file = EnneaOdin::StreamFile::Open(stream, length);

	if (file == nullptr)
		return ODIN_INTERNAL_ERROR;

	/* The same sequence BridgeManager::SendFile performs before its first
	   part: size the file, then go back to the beginning. */
	if (FileSeek(file, 0, SEEK_END) != 0)
	{
		EnneaOdin::StreamFile::Close(file);
		return ODIN_FAILED;
	}

	const long long reportedSize = static_cast<long long>(FileTell(file));
	FileRewind(file);

	if (out_reported_size != nullptr && reportedSize >= 0)
		*out_reported_size = static_cast<unsigned long long>(reportedSize);

	std::vector<unsigned char> part(part_size, 0);
	unsigned long long total = 0;
	unsigned int crc = 0xFFFFFFFFu;
	int status = ODIN_OK;

	for (;;)
	{
		/* And the sequence SendFilePartPacket performs for every part
		   (SendFilePartPacket.h:38): where am I, how big are you, back where
		   I was, read. */
		const long long position = static_cast<long long>(FileTell(file));

		if (position < 0)
		{
			status = ODIN_FAILED;
			break;
		}

		if (FileSeek(file, 0, SEEK_END) != 0 ||
		    FileSeek(file, position, SEEK_SET) != 0)
		{
			status = ODIN_FAILED;
			break;
		}

		const size_t got = fread(&part[0], 1, part_size, file);

		if (got > 0)
		{
			crc = Crc32Update(crc, &part[0], got);
			total += got;
		}

		if (got < part_size)
		{
			if (ferror(file) != 0)
				status = ODIN_FAILED;

			break;
		}
	}

	const int streamFailed = EnneaOdin::StreamFile::Close(file);

	if (streamFailed != 0)
		status = ODIN_FAILED;

	if (out_total_read != nullptr)
		*out_total_read = total;

	if (out_crc32 != nullptr)
		*out_crc32 = crc ^ 0xFFFFFFFFu;

	return status;

	ODIN_CATCH(cb)
}
