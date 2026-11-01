/* Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
 
 Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:
 
 The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.
 
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.*/

#ifndef BRIDGEMANAGER_H
#define BRIDGEMANAGER_H

// libpit
#include "libpit.h"

// Heimdall
#include "Heimdall.h"

#include "OdinSerialPort.h"

struct libusb_context;
struct libusb_device;
struct libusb_device_handle;

namespace Heimdall
{
	class InboundPacket;
	class OutboundPacket;

	class DeviceIdentifier
	{
		public:

			const int vendorId;
			const int productId;

			DeviceIdentifier(int vid, int pid) :
					vendorId(vid),
					productId(pid)
			{
			}
	};

	class BridgeManager
	{
		public:

			enum
			{
				kSupportedDeviceCount = 3
			};

			enum
			{
				kInitialiseSucceeded = 0,
				kInitialiseFailed,
				kInitialiseDeviceNotDetected
			};

			enum
			{
				kVidSamsung	= 0x04E8
			};

			enum
			{
				kPidGalaxyS = 0x6601,
				kPidGalaxyS2 = 0x685D,
				kPidDroidCharge = 0x68C3
			};

			enum
			{
				kDefaultTimeoutSend = 3000,
				kDefaultTimeoutReceive = 3000,
				kDefaultTimeoutEmptyTransfer = 100
			};

			enum class UsbLogLevel
			{
				None = 0,
				Error,
				Warning,
				Info,
				Debug,

				Default = Error
			};

			enum
			{
				kEmptyTransferNone = 0,
				kEmptyTransferBefore = 1,
				kEmptyTransferAfter = 1 << 1,
				kEmptyTransferBeforeAndAfter = kEmptyTransferBefore | kEmptyTransferAfter
			};

		private:

			static const DeviceIdentifier supportedDevices[kSupportedDeviceCount];

			bool verbose;
			bool waitForDevice;

			libusb_context *libusbContext;
			libusb_device_handle *deviceHandle;
			libusb_device *heimdallDevice;

			int interfaceIndex;
			int altSettingIndex;
			int inEndpoint;
			int outEndpoint;

			bool interfaceClaimed;

			/* EnneaOdin: was OS_LINUX only, along with the detach that sets
			   it and the re-attach that consumes it. A kernel driver on the
			   interface is not a Linux-only situation. */
			bool detachedDriver;

			unsigned int fileTransferSequenceMaxLength;

			// EnneaOdin: what the handset itself said its default file-part
			// size was, in the begin-session response. Upstream reads it into
			// a local, branches on whether it is zero and discards it; zero
			// means the device will not accept a packet-size change, which is
			// the kind of thing a user staring at a slow transfer should be
			// able to see. Reported through odin_session_transfer_params.
			unsigned int deviceDefaultPacketSize;

			// EnneaOdin: the last libusb result from a bulk transfer, so a
			// caller can name the error rather than only that one happened.
			// mutable because the transfer helpers are const.
			mutable int lastTransferResult;

			// EnneaOdin: the CDC serial transport, used instead of libusb's
			// bulk endpoints when a kernel driver holds the interface and will
			// not let go of it. See OdinSerialPort.h for the measurements that
			// made this necessary. mutable for the same reason as above.
			mutable EnneaOdin::SerialPort serialPort;
			bool useSerialTransport;
			unsigned int fileTransferPacketSize;
			unsigned int fileTransferSequenceTimeout;

			UsbLogLevel usbLogLevel;

			int FindDeviceInterface(void);
			bool ClaimDeviceInterface(void);
			bool SetupDeviceInterface(void);
			void ReleaseDeviceInterface(void);

			bool InitialiseProtocol(void);

			// EnneaOdin: libusb's bulk endpoints, or the serial node the
			// kernel driver publishes. See BridgeManager.cpp.
			bool ChooseTransport(void);

			// EnneaOdin: tried FIRST, before libusb opens anything. See
			// BridgeManager.cpp for why the order matters.
			bool TrySerialTransport(void);

			// EnneaOdin: one measured fact, said once, where a user can act
			// on it. See BridgeManager.cpp.
			void PrintHandshakeAdvice(void) const;

			// EnneaOdin: clear the handset's handshake latch, reversed from
			// its own bootloader. See BridgeManager.cpp.
			bool SendRearmPacket(void) const;

			bool SendBulkTransfer(unsigned char *data, int length, int timeout, bool retry = true) const;
			int ReceiveBulkTransfer(unsigned char *data, int length, int timeout, bool retry = true) const;

		public:

			// EnneaOdin: "DVIF", which the handset answers with its model,
			// firmware version, sales code and capacity, and which does NOT
			// consume the one handshake it will answer. Public because the C
			// ABI calls it directly, without a session. See BridgeManager.cpp.
			bool RequestDeviceInfo(char *out, int size);


			BridgeManager(bool verbose, bool waitForDevice);
			~BridgeManager();

			bool DetectDevice(void);
			int Initialise(bool resume);

			bool BeginSession(void);
			bool EndSession(bool reboot) const;

			bool SendPacket(OutboundPacket *packet, int timeout = kDefaultTimeoutSend, int emptyTransferFlags = kEmptyTransferAfter) const;
			bool ReceivePacket(InboundPacket *packet, int timeout = kDefaultTimeoutReceive, int emptyTransferFlags = kEmptyTransferNone) const;

			bool RequestDeviceType(unsigned int request, int *result) const;

			bool SendPitData(const libpit::PitData *pitData) const;
			int ReceivePitFile(unsigned char **pitBuffer) const;
			int DownloadPitFile(unsigned char **pitBuffer) const; // Thin wrapper around ReceivePitFile() with additional logging.

			bool SendFile(FILE *file, unsigned int destination, unsigned int deviceType, unsigned int fileIdentifier = 0xFFFFFFFF) const;

			void SetUsbLogLevel(UsbLogLevel usbLogLevel);
#ifdef OS_LINUX
			bool IsUbuntu(void);
#endif
			UsbLogLevel GetUsbLogLevel(void) const
			{
				return usbLogLevel;
			}

			bool IsVerbose(void) const
			{
				return (verbose);
			}

			// EnneaOdin: three read-only accessors for the transfer geometry.
			// The members are private and BeginSession is what settles them
			// (BridgeManager.cpp:530-534), so a GUI that shows the numbers on
			// screen had no way to read them without guessing which set was in
			// force. Nothing is written and no protocol decision changes.
			unsigned int GetFileTransferPacketSize(void) const
			{
				return (fileTransferPacketSize);
			}

			unsigned int GetFileTransferSequenceMaxLength(void) const
			{
				return (fileTransferSequenceMaxLength);
			}

			unsigned int GetFileTransferSequenceTimeout(void) const
			{
				return (fileTransferSequenceTimeout);
			}

			// EnneaOdin: see the field's own comment. Zero also means "not
			// negotiated yet", which is true until BeginSession has run.
			// EnneaOdin: which transport the protocol is running over. For the
			// log, and for nothing else: no protocol decision depends on it.
			bool IsUsingSerialTransport(void) const
			{
				return (useSerialTransport);
			}

			const char *GetSerialPortPath(void) const
			{
				return (serialPort.Path());
			}

			unsigned int GetDeviceDefaultPacketSize(void) const
			{
				return (deviceDefaultPacketSize);
			}
	};
}

#endif
