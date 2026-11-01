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

// C Standard Library
#include <cstdio>
#include <fstream>

// libusb
#include <libusb.h>

// Heimdall
#include "BeginDumpPacket.h"
#include "BeginSessionPacket.h"
#include "BridgeManager.h"
#include "DeviceTypePacket.h"
#include "DumpPartFileTransferPacket.h"
#include "DumpPartPitFilePacket.h"
#include "DumpResponse.h"
#include "EndModemFileTransferPacket.h"
#include "EndPhoneFileTransferPacket.h"
#include "EndPitFileTransferPacket.h"
#include "EndSessionPacket.h"
#include "FilePartSizePacket.h"
#include "FileTransferPacket.h"
#include "FlashPartFileTransferPacket.h"
#include "FlashPartPitFilePacket.h"
#include "InboundPacket.h"
#include "Interface.h"
#include "OutboundPacket.h"
#include "PitFilePacket.h"
#include "PitFileResponse.h"
#include "ReceiveFilePartPacket.h"
#include "ResponsePacket.h"
#include "SendFilePartPacket.h"
#include "SendFilePartResponse.h"
#include "SessionSetupPacket.h"
#include "SessionSetupResponse.h"

// EnneaOdin: cancellation and progress hooks. See native/src/OdinHooks.h.
// This and libpit/source/libpit.h are the only two vendored files EnneaOdin
// modifies; NOTICE in the repository root lists every edit. Each one here is
// marked with an "EnneaOdin:" comment, adds a check between packets, and
// changes no protocol behaviour: with no hook context installed - which is the
// case for any caller other than the EnneaOdin bridge - Hooks::Cancelled() is
// false and Hooks::Progress() does nothing, so this file behaves exactly as
// upstream does.
#include "OdinHooks.h"

// Future versions of libusb will use usb_interface instead of interface.
#ifndef usb_interface
#define usb_interface interface
#endif

#define USB_CLASS_CDC_DATA 0x0A

using namespace libpit;
using namespace Heimdall;

const DeviceIdentifier BridgeManager::supportedDevices[BridgeManager::kSupportedDeviceCount] = {
	DeviceIdentifier(BridgeManager::kVidSamsung, BridgeManager::kPidGalaxyS),
	DeviceIdentifier(BridgeManager::kVidSamsung, BridgeManager::kPidGalaxyS2),
	DeviceIdentifier(BridgeManager::kVidSamsung, BridgeManager::kPidDroidCharge)
};

enum
{
	kFileTransferSequenceMaxLengthDefault = 800,
	kFileTransferPacketSizeDefault = 131072,
	kFileTransferSequenceTimeoutDefault = 30000 // 30 seconds
};

int BridgeManager::FindDeviceInterface(void)
{
	if (waitForDevice)
		Interface::Print("Waiting for device...\n");
	else
		Interface::Print("Detecting device...\n");

	struct libusb_device **devices;
	unsigned int deviceCount, deviceIndex, i;
	libusb_device_descriptor descriptor;
	while (true)
	{
		deviceCount = libusb_get_device_list(libusbContext, &devices);

		for (deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
		{
			libusb_get_device_descriptor(devices[deviceIndex], &descriptor);

			for (i = 0; i < BridgeManager::kSupportedDeviceCount; i++)
			{
				if (descriptor.idVendor == supportedDevices[i].vendorId &&
				    descriptor.idProduct == supportedDevices[i].productId)
				{
					heimdallDevice = devices[deviceIndex];
					libusb_ref_device(heimdallDevice);
					break;
				}
			}
		}
		if (heimdallDevice)
			break;

		libusb_free_device_list(devices, deviceCount);

		// EnneaOdin: a wait-for-device loop that cannot be abandoned is a GUI
		// that hangs on its own Stop button. Checked once per poll, so
		// cancellation here lands within one second.
		if (EnneaOdin::Hooks::Cancelled())
		{
			Interface::PrintError("Cancelled while waiting for a device.\n");
			return (BridgeManager::kInitialiseDeviceNotDetected);
		}

		if (waitForDevice)
			Sleep(1000);
		else
			break;
	}

	if (!heimdallDevice)
	{
		Interface::PrintDeviceDetectionFailed();
		return (BridgeManager::kInitialiseDeviceNotDetected);
	}

	int result = libusb_open(heimdallDevice, &deviceHandle);
	if (result != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Failed to access device. libusb error: %d\n", result);
		return (BridgeManager::kInitialiseFailed);
	}

	/* EnneaOdin: ask libusb to take the interface away from whatever kernel
	   driver holds it, BEFORE the claim, because the claim is what performs
	   the detach. Upstream only ever detaches under OS_LINUX, and on macOS
	   Samsung's download mode is a CDC-ACM gadget that AppleUSBACMData binds
	   to on sight, creating a /dev/cu.usbmodem node. The claim then succeeds,
	   the alternate setting succeeds, the endpoints are not halted, and every
	   bulk transfer still times out, because the pipes belong to the kernel
	   driver. Reported either way: a platform without the capability says so,
	   which is far better than the silent timeout it used to produce. */
	const int autoDetach = libusb_set_auto_detach_kernel_driver(deviceHandle, 1);

	if (autoDetach == LIBUSB_SUCCESS)
	{
		Interface::Print("Kernel driver detach: enabled.\n");
	}
	else
	{
		Interface::Print("Kernel driver detach: %s (claiming anyway).\n",
			libusb_error_name(autoDetach));
	}

	libusb_device_descriptor deviceDescriptor;
	result = libusb_get_device_descriptor(heimdallDevice, &deviceDescriptor);
	if (result != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Failed to retrieve device description\n");
		return (BridgeManager::kInitialiseFailed);
	}

	if (verbose)
	{
		unsigned char stringBuffer[128];

		if (libusb_get_string_descriptor_ascii(deviceHandle, deviceDescriptor.iManufacturer,
			stringBuffer, 128) >= 0)
		{
			Interface::Print("      Manufacturer: \"%s\"\n", stringBuffer);
		}

		if (libusb_get_string_descriptor_ascii(deviceHandle, deviceDescriptor.iProduct,
			stringBuffer, 128) >= 0)
		{
			Interface::Print("           Product: \"%s\"\n", stringBuffer);
		}

		if (libusb_get_string_descriptor_ascii(deviceHandle, deviceDescriptor.iSerialNumber,
			stringBuffer, 128) >= 0)
		{
			Interface::Print("         Serial No: \"%s\"\n", stringBuffer);
		}

		Interface::Print("\n            length: %d\n", deviceDescriptor.bLength);
		Interface::Print("      device class: %d\n", deviceDescriptor.bDeviceClass);
		Interface::Print("               S/N: %d\n", deviceDescriptor.iSerialNumber);
		Interface::Print("           VID:PID: %04X:%04X\n", deviceDescriptor.idVendor, deviceDescriptor.idProduct);
		Interface::Print("         bcdDevice: %04X\n", deviceDescriptor.bcdDevice);
		Interface::Print("   iMan:iProd:iSer: %d:%d:%d\n", deviceDescriptor.iManufacturer, deviceDescriptor.iProduct,
			deviceDescriptor.iSerialNumber);
		Interface::Print("          nb confs: %d\n", deviceDescriptor.bNumConfigurations);
	}

	libusb_config_descriptor *configDescriptor;
	result = libusb_get_config_descriptor(heimdallDevice, 0, &configDescriptor);

	if (result != LIBUSB_SUCCESS || !configDescriptor)
	{
		Interface::PrintError("Failed to retrieve config descriptor\n");
		return (BridgeManager::kInitialiseFailed);
	}

	interfaceIndex = -1;
	altSettingIndex = -1;

	for (int i = 0; i < configDescriptor->bNumInterfaces; i++)
	{
		for (int j = 0 ; j < configDescriptor->usb_interface[i].num_altsetting; j++)
		{
			if (verbose)
			{
				Interface::Print("\ninterface[%d].altsetting[%d]: num endpoints = %d\n",
					i, j, configDescriptor->usb_interface[i].altsetting[j].bNumEndpoints);
				Interface::Print("   Class.SubClass.Protocol: %02X.%02X.%02X\n",
					configDescriptor->usb_interface[i].altsetting[j].bInterfaceClass,
					configDescriptor->usb_interface[i].altsetting[j].bInterfaceSubClass,
					configDescriptor->usb_interface[i].altsetting[j].bInterfaceProtocol);
			}

			int inEndpointAddress = -1;
			int outEndpointAddress = -1;

			for (int k = 0; k < configDescriptor->usb_interface[i].altsetting[j].bNumEndpoints; k++)
			{
				const libusb_endpoint_descriptor *endpoint = &configDescriptor->usb_interface[i].altsetting[j].endpoint[k];

				if (verbose)
				{
					Interface::Print("       endpoint[%d].address: %02X\n", k, endpoint->bEndpointAddress);
					Interface::Print("           max packet size: %04X\n", endpoint->wMaxPacketSize);
					Interface::Print("          polling interval: %02X\n", endpoint->bInterval);
				}

				if (endpoint->bEndpointAddress & LIBUSB_ENDPOINT_IN)
					inEndpointAddress = endpoint->bEndpointAddress;
				else
					outEndpointAddress = endpoint->bEndpointAddress;
			}

			if (interfaceIndex < 0
				&& configDescriptor->usb_interface[i].altsetting[j].bNumEndpoints == 2
				&& configDescriptor->usb_interface[i].altsetting[j].bInterfaceClass == USB_CLASS_CDC_DATA
				&& inEndpointAddress != -1
				&& outEndpointAddress != -1)
			{
				interfaceIndex = i;
				altSettingIndex = j;
				inEndpoint = inEndpointAddress;
				outEndpoint = outEndpointAddress;
			}
		}
	}

	libusb_free_config_descriptor(configDescriptor);

	if (interfaceIndex < 0)
	{
		Interface::PrintError("Failed to find correct interface configuration\n");
		return (BridgeManager::kInitialiseFailed);
	}

	return (BridgeManager::kInitialiseSucceeded);
}

bool BridgeManager::ClaimDeviceInterface(void)
{
	Interface::Print("Claiming interface...\n");

	int result = libusb_claim_interface(deviceHandle, interfaceIndex);

	/* EnneaOdin: this fallback was wrapped in OS_LINUX. The situation it
	   handles - a kernel driver sitting on the interface - is not specific to
	   Linux, and on macOS it is the normal state of a CDC-ACM device. A
	   platform that cannot detach returns an error from the detach and the
	   second claim fails the same way the first did, so nothing is lost by
	   trying. */
	if (result != LIBUSB_SUCCESS)
	{
		detachedDriver = true;
		Interface::Print("Attempt failed (%s). Detaching driver...\n",
			libusb_error_name(result));

		const int detach = libusb_detach_kernel_driver(deviceHandle, interfaceIndex);

		if (detach != LIBUSB_SUCCESS)
			Interface::Print("Detaching returned %s.\n", libusb_error_name(detach));

		Interface::Print("Claiming interface again...\n");
		result = libusb_claim_interface(deviceHandle, interfaceIndex);
	}

	if (result != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Claiming interface failed! libusb: %s (%d)\n",
			libusb_error_name(result), result);
		return (false);
	}

	interfaceClaimed = true;

	return (true);
}

bool BridgeManager::SetupDeviceInterface(void)
{
	Interface::Print("Setting up interface...\n");

	int result = libusb_set_interface_alt_setting(deviceHandle,
						      interfaceIndex,
						      altSettingIndex);

	// EnneaOdin: upstream returned early when altSettingIndex was 0, with the
	// comment "there should be no need to set alt_setting(?)" - a question
	// mark, not a finding. Some backends only bind the pipes once an alt
	// setting is selected, so it is now always attempted; at 0 a failure is
	// reported and ignored, because upstream's guess may well be right and
	// refusing to continue would be a regression for every device it is right
	// about.
	if (result != LIBUSB_SUCCESS)
	{
		if (altSettingIndex == 0)
		{
			Interface::Print("Selecting alt setting 0 returned %s; continuing.\n",
				libusb_error_name(result));
			return (true);
		}

		Interface::PrintError("Setting up interface failed! libusb: %s (%d)\n",
			libusb_error_name(result), result);
		return (false);
	}

	Interface::Print("\n");
	return (true);
}

void BridgeManager::ReleaseDeviceInterface(void)
{
	/* EnneaOdin: the serial node, if that is what the protocol ran over.
	   Harmless when it was not: Close on an unopened port does nothing. */
	serialPort.Close();

	Interface::Print("Releasing device interface...\n");

	libusb_release_interface(deviceHandle, interfaceIndex);

	/* EnneaOdin: unguarded with the detach. Giving the interface back matters
	   most on macOS, where the kernel driver owns the /dev/cu.usbmodem node
	   that everything else on the system expects to find. */
	if (detachedDriver)
	{
		Interface::Print("Re-attaching kernel driver...\n");
		libusb_attach_kernel_driver(deviceHandle, interfaceIndex);
	}

	interfaceClaimed = false;
	Interface::Print("\n");
}

/* EnneaOdin. Is there a serial node for a supported device? If so, open it and
   run the protocol over it, without libusb ever opening the device.
   
   The supported ids are repeated here rather than read from
   supportedDevices, which is private to this class but not reachable from a
   static context in the order this has to run. They are BridgeManager.h:71-78:
   kVidSamsung with kPidGalaxyS, kPidGalaxyS2 and kPidDroidCharge. */
bool BridgeManager::TrySerialTransport(void)
{
	static const struct { unsigned short vendor; unsigned short product; } known[] = {
		{ 0x04E8, 0x6601 },
		{ 0x04E8, 0x685D },
		{ 0x04E8, 0x68C3 }
	};

	char path[EnneaOdin::SerialPort::kMaxPathLength];

	for (unsigned int i = 0; i < sizeof known / sizeof known[0]; i++)
	{
		if (!EnneaOdin::SerialPort::Find(known[i].vendor, known[i].product,
		                                 path, sizeof path))
		{
			continue;
		}

		Interface::Print("Detecting device...\n");

		if (!serialPort.Open(path))
		{
			Interface::PrintError("Found %s for %04X:%04X but could not open it.\n",
				path, known[i].vendor, known[i].product);
			return (false);
		}

		useSerialTransport = true;
		Interface::Print("Transport: %s (%04X:%04X, the kernel driver's own node).\n",
			path, known[i].vendor, known[i].product);
		Interface::Print("libusb is not opening this device: see BridgeManager.cpp.\n");
		return (true);
	}

	return (false);
}

/* EnneaOdin. Decide whether the protocol runs over libusb's bulk endpoints or
   over the serial node the kernel driver publishes. Reached only when no
   serial node was available, so this is the libusb side of the decision. */
bool BridgeManager::ChooseTransport(void)
{
	useSerialTransport = false;

	const int held = libusb_kernel_driver_active(deviceHandle, interfaceIndex);

	if (held != 1)
	{
		/* Nothing holds it, or the platform cannot say. libusb it is. */
		Interface::Print("Transport: libusb bulk endpoints.\n");
		return (true);
	}

	Interface::Print("A kernel driver still holds interface %d after the claim, so its "
		"bulk endpoints will not carry traffic.\n", interfaceIndex);

	libusb_device_descriptor descriptor;

	if (libusb_get_device_descriptor(heimdallDevice, &descriptor) != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Could not re-read the device descriptor.\n");
		return (false);
	}

	char path[EnneaOdin::SerialPort::kMaxPathLength];

	if (!EnneaOdin::SerialPort::Find(descriptor.idVendor, descriptor.idProduct,
	                                 path, sizeof path))
	{
		Interface::PrintError("No serial node belongs to %04X:%04X either, so there is no "
			"way to reach this device.\n", descriptor.idVendor, descriptor.idProduct);
		return (false);
	}

	/* LET GO OF THE DEVICE COMPLETELY FIRST, handle and all.
	   
	   Releasing the interface is not enough: measured on an SM-A166U under
	   macOS 26, with libusb's device handle still open the serial node accepts
	   the write and the reply never arrives, while the identical exchange
	   through the same node works on the first attempt when libusb has never
	   touched the device. libusb_close is what makes the difference, so the
	   handle goes here and not in the destructor.
	   
	   The destructor already checks both for null, so closing early is safe
	   and nothing double-frees. */
	ReleaseDeviceInterface();

	if (deviceHandle != nullptr)
	{
		libusb_close(deviceHandle);
		deviceHandle = nullptr;
	}

	if (heimdallDevice != nullptr)
	{
		libusb_unref_device(heimdallDevice);
		heimdallDevice = nullptr;
	}

	if (libusbContext != nullptr)
	{
		libusb_exit(libusbContext);
		libusbContext = nullptr;
	}

	if (!serialPort.Open(path))
	{
		Interface::PrintError("Found %s but could not open it.\n", path);
		return (false);
	}

	useSerialTransport = true;
	Interface::Print("Transport: %s (the kernel driver's own node).\n", path);
	return (true);
}

/* EnneaOdin. Ask the handset what it is, with "DVIF".
   
   REVERSED FROM THE DEVICE'S OWN BOOTLOADER, s-lk.img for an SM-A166U. The
   first command of a download-mode session is matched against a flat chain,
   not just "ODIN" (BridgeManager-equivalent code at 0xF4810530, table at
   0xF49D0E70): ODIN, FPGM, PROMPT, DVIF, AT+ENGMODES, AT+ENGMODES=1,3,1,
   @#AuToTEst@#, @#AuToTEstRst@#.
   
   DVIF answers with up to 256 bytes:
   
     @#MODEL=%s;UN=%s;CAPA=%s;VENDOR=%s;FWVER=%s;PRODUCT=%s;PROV=%s;
       SALES=%s;VER=%s;DID=%s;TMU_TEMP=%d;NAD_TEMP=%d;@#
   
   and - this is what makes it worth having - the DVIF branch does NOT set the
   handshake latch that "ODIN" sets. Asking costs nothing: the handset will
   still answer one ODIN afterwards.
   
   So the model, firmware version, sales code and capacity ARE available over
   the download-mode protocol. EnneaOdin used to state the opposite. */
bool BridgeManager::RequestDeviceInfo(char *out, int size)
{
	if (out == nullptr || size < 2)
		return (false);

	out[0] = '\0';

	unsigned char request[4];
	memcpy(request, "DVIF", 4);

	if (!SendBulkTransfer(request, 4, 1000, false))
	{
		Interface::PrintError("Could not ask for device information: %s (%d)\n",
			libusb_error_name(lastTransferResult), lastTransferResult);
		return (false);
	}

	/* 256 is what the bootloader formats into; one byte is kept for the
	   terminator. */
	const int wanted = (size - 1 < 256) ? size - 1 : 256;
	const int received = ReceiveBulkTransfer((unsigned char *)out, wanted, 3000, true);

	if (received <= 0)
	{
		Interface::PrintError("No device information came back: %s (%d)\n",
			libusb_error_name(received), received);
		return (false);
	}

	out[received] = '\0';
	return (true);
}

bool BridgeManager::InitialiseProtocol(void)
{
	Interface::Print("Initialising protocol...\n");

	unsigned char dataBuffer[7];

#ifdef OS_LINUX
	if (IsUbuntu())
	{
		Interface::Print("Resetting device...\n");
		if (libusb_reset_device(deviceHandle))
		{
			Interface::PrintError("Failed to reset device!\n");
		}
	}
#endif

	/* EnneaOdin: ONE attempt here, deliberately.
	   
	   An earlier version looped, resetting the device between attempts. That
	   does not work: libusb_reset_device re-enumerates, which INVALIDATES the
	   handle, so the second and third attempts were operating on a dead one -
	   "no connection to an IOService", then LIBUSB_ERROR_NO_DEVICE. The retry
	   therefore lives one level up, in OdinSession.OpenAndBeginAsync, where
	   it can reset the device and then open it again from scratch: new
	   libusb context, fresh enumeration, fresh claim.
	   
	   What is kept here is clearing the endpoints, naming the libusb error,
	   and giving the reply a sane deadline. */
	/* Only meaningful on the libusb transport: on the serial one the
	   interface has already been released and there is no pipe to clear. */
	if (!useSerialTransport)
	{
		libusb_clear_halt(deviceHandle, outEndpoint);
		libusb_clear_halt(deviceHandle, inEndpoint);
	}

	memcpy(dataBuffer, "ODIN", 4);
	memset(dataBuffer + 4, 0, 1);

	if (!SendBulkTransfer(dataBuffer, 4, 1000, false))
	{
		/* No retry: six timeouts here cost ten seconds and never once
		   helped. A fresh open is what helps. */
		Interface::PrintError("Failed to send handshake! libusb: %s (%d)\n",
			libusb_error_name(lastTransferResult), lastTransferResult);
		Interface::PrintError("Protocol initialisation failed!\n\n");
		return (false);
	}

	/* Here, and only here, the bytes are made to leave before anything is
	   read: this is the one exchange that is strictly request-then-response.
	   The data path does not drain, because draining between parts empties
	   the pipe and stalls it. See SerialPort::Write. */
	if (useSerialTransport)
		serialPort.DrainFor(1000);

	memset(dataBuffer, 0, 7);

	/* FOUR bytes on a byte stream, seven on USB.
	   
	   Odin 3.14.1 writes 4 and reads exactly 4 before comparing with "LOKE"
	   (sub_434D50, reversed from the shipped binary). Upstream Heimdall reads
	   7 into this buffer and then checks that 4 arrived, which works on a
	   bulk endpoint because a short packet ends the transfer. A stream has no
	   packet boundaries, so asking for 7 means waiting out a timeout for
	   three bytes that are never coming.
	   
	   3000 ms and retried either way, where upstream read once for 1000. */
	const int wanted = useSerialTransport ? 4 : 7;
	const int received = ReceiveBulkTransfer(dataBuffer, wanted, 3000, true);

	if (received < 0)
	{
		Interface::Print("No handshake response. Re-arming the handset and trying once more.\n");

		/* EnneaOdin: clear the handset's handshake latch and try again.
		   
		   REVERSED FROM THE DEVICE'S OWN BOOTLOADER, s-lk.img for an
		   SM-A166U, in usbd3_rdx_process. Its handshake is latched:
		   
		       if (latch == 0) {
		           if (!strncmp(rx, "ODIN", 4)) { latch = 1; reply = "LOKE"; }
		           ...
		       } else {
		           ... accumulate 1024 bytes ...
		           if (packet[0] == 103 && packet[1] == 4) {
		               offset = 0;
		               latch = 0;        // the only way back
		           }
		       }
		   
		   So the handset answers ODIN exactly ONCE per entry into download
		   mode. Every later handshake is received and ignored, which is why
		   this looked like a dead transport: the bytes went out, the device
		   took them, and nothing came back. The one escape the bootloader
		   offers is a 1024-byte control packet whose first two little-endian
		   words are 103 and 4.
		   
		   103 is ControlPacket::kControlTypeEndSession. 4 is a request
		   Heimdall does not define: EndSessionPacket.h has only
		   kRequestEndSession (0) and kRequestRebootDevice (1).
		   
		   Sent only AFTER a handshake went unanswered. On a handset whose
		   latch is still clear this packet is not what that branch examines,
		   so sending it unprompted would achieve nothing. */
		if (SendRearmPacket() && SendBulkTransfer(dataBuffer, 4, 1000, false))
		{
			memset(dataBuffer, 0, 7);
			const int again = ReceiveBulkTransfer(dataBuffer, wanted, 3000, true);

			if (again == 4 && memcmp(dataBuffer, "LOKE", 4) == 0)
			{
				Interface::Print("Protocol initialisation successful (after re-arming).\n\n");
				return (true);
			}

			Interface::PrintError("Still no handshake after re-arming: %s (%d)\n",
				libusb_error_name(again), again);
		}

		Interface::PrintError("Protocol initialisation failed!\n");
		PrintHandshakeAdvice();
		return (false);
	}

	if (received == 4 && memcmp(dataBuffer, "LOKE", 4) == 0)
	{
		Interface::Print("Protocol initialisation successful.\n\n");
		return (true);
	}

	Interface::PrintError("Unexpected handshake response: %d byte(s), \"%s\"\n",
		received, dataBuffer);
	Interface::PrintError("Protocol initialisation failed!\n\n");
	return (false);
}

/* EnneaOdin. Clear the handset's handshake latch.
   
   A 1024-byte control packet, little-endian, first word 103
   (ControlPacket::kControlTypeEndSession) and second word 4. The call site in
   InitialiseProtocol carries the bootloader listing those two numbers come
   from. */
bool BridgeManager::SendRearmPacket(void) const
{
	unsigned char packet[1024];
	memset(packet, 0, sizeof packet);

	packet[0] = 103;    /* kControlTypeEndSession, little-endian */
	packet[4] = 4;      /* the request the bootloader latches on */

	if (!SendBulkTransfer(packet, sizeof packet, 3000, false))
	{
		Interface::PrintError("Could not send the re-arm packet: %s (%d)\n",
			libusb_error_name(lastTransferResult), lastTransferResult);
		return (false);
	}

	/* The bootloader clears the latch and re-arms its own receive without
	   answering, so there is nothing to read back. */
	return (true);
}

/* EnneaOdin. What to tell the user when the handshake does not land even after
   re-arming.
   
   The latch and its one escape are reversed from the handset's own bootloader
   (see InitialiseProtocol). If clearing it did not help either, the handset
   really does have to be taken out of download mode and put back. */
void BridgeManager::PrintHandshakeAdvice(void) const
{
	Interface::PrintError(
		"The handset accepted the handshake bytes and did not answer, and clearing its "
		"handshake latch did not help either. Take it out of download mode and back in, "
		"then try once.\n\n");
}

BridgeManager::BridgeManager(bool verbose, bool waitForDevice)
{
	this->verbose = verbose;
	this->waitForDevice = waitForDevice;

	libusbContext = nullptr;
	deviceHandle = nullptr;
	heimdallDevice = nullptr;

	inEndpoint = -1;
	outEndpoint = -1;
	interfaceIndex = -1;
	altSettingIndex = -1;

	interfaceClaimed = false;
	detachedDriver = false;

	fileTransferSequenceMaxLength = kFileTransferSequenceMaxLengthDefault;
	fileTransferPacketSize = kFileTransferPacketSizeDefault;
	fileTransferSequenceTimeout = kFileTransferSequenceTimeoutDefault;

	// EnneaOdin: initialised with the rest, so the accessor never reads
	// indeterminate memory before BeginSession has run. Zero is also its
	// meaningful "the device has not said" value.
	deviceDefaultPacketSize = 0;
	lastTransferResult = LIBUSB_SUCCESS;
	useSerialTransport = false;

	usbLogLevel = UsbLogLevel::Default;
}

BridgeManager::~BridgeManager()
{
	if (interfaceClaimed)
		ReleaseDeviceInterface();

	if (deviceHandle)
		libusb_close(deviceHandle);

	if (heimdallDevice)
		libusb_unref_device(heimdallDevice);

	if (libusbContext)
		libusb_exit(libusbContext);
}

bool BridgeManager::DetectDevice(void)
{
	// Initialise libusb
	int result = libusb_init(&libusbContext);

	if (result != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Failed to initialise libusb. libusb error: %d\n", result);
		return (false);
	}

	if (waitForDevice)
		Interface::Print("Waiting for device...\n");

	// Set libusb log level.
	SetUsbLogLevel(usbLogLevel);

	struct libusb_device **devices;
	unsigned int deviceCount, deviceIndex, i;
	libusb_device_descriptor descriptor;
	while (true)
	{
		deviceCount = libusb_get_device_list(libusbContext, &devices);

		for (deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
		{
			libusb_get_device_descriptor(devices[deviceIndex], &descriptor);

			for (i = 0; i < BridgeManager::kSupportedDeviceCount; i++)
			{
				if (descriptor.idVendor == supportedDevices[i].vendorId &&
				    descriptor.idProduct == supportedDevices[i].productId)
				{
					libusb_free_device_list(devices, deviceCount);

					Interface::Print("Device detected\n");
					return (true);
				}
			}
		}

		libusb_free_device_list(devices, deviceCount);

		// EnneaOdin: the same per-poll check as in FindDeviceInterface.
		if (EnneaOdin::Hooks::Cancelled())
		{
			Interface::PrintError("Cancelled while waiting for a device.\n");
			return (false);
		}

		if (waitForDevice)
			Sleep(1000);
		else
			break;
	}
	Interface::PrintDeviceDetectionFailed();
	return (false);
}

int BridgeManager::Initialise(bool resume)
{
	Interface::Print("Initialising connection...\n");

	// Initialise libusb
	int result = libusb_init(&libusbContext);

	if (result != LIBUSB_SUCCESS)
	{
		Interface::PrintError("Failed to initialise libusb. libusb error: %d\n", result);
		Interface::Print("Failed to connect to device!\n");
		return (BridgeManager::kInitialiseFailed);
	}

	// Fixes LIBUSB_ERROR_NOT_FOUND when using dg_ssudbus driver instead of WinUSB.
	libusb_set_option(libusbContext, LIBUSB_OPTION_USE_USBDK);

	// Setup libusb log level.
	SetUsbLogLevel(usbLogLevel);

	/* EnneaOdin: THE SERIAL TRANSPORT IS CHOSEN BEFORE libusb TOUCHES THE
	   DEVICE, and that order is the whole point.
	   
	   Measured on an SM-A166U under macOS 26. libusb cannot take this
	   interface from AppleUSBACMData at all: as root, with
	   libusb_set_auto_detach_kernel_driver set, on one handle with nothing
	   closed in between, libusb_claim_interface returns success while
	   libusb_kernel_driver_active still reports 1 and every bulk transfer
	   times out. Capture needs the com.apple.vm.device-access entitlement,
	   which Apple grants through a provisioning profile and ad-hoc signing
	   cannot have. Root is not enough; that was tested.
	   
	   Worse, the attempt is not free. A claim leaves the kernel driver's own
	   pipes broken and releasing the interface does not repair them, so the
	   serial node stops carrying traffic too, until the device re-enumerates.
	   An earlier version chose the transport AFTER claiming, which meant it
	   had already broken the only path that works before it tried it.
	   
	   So: if a serial node belongs to a supported device, take it and leave
	   libusb alone entirely. Everywhere else - Linux, Windows, and a macOS
	   device no driver matched - there is no node, this returns false, and
	   the libusb path below runs exactly as upstream wrote it. */
	if (TrySerialTransport())
	{
		if (!resume)
		{
			if (!InitialiseProtocol())
				return (BridgeManager::kInitialiseFailed);
		}

		return (BridgeManager::kInitialiseSucceeded);
	}

	result = FindDeviceInterface();

	if (result != BridgeManager::kInitialiseSucceeded)
		return (result);

	if (!ClaimDeviceInterface())
		return (BridgeManager::kInitialiseFailed);

	if (!SetupDeviceInterface())
		return (BridgeManager::kInitialiseFailed);

	/* EnneaOdin: pick the transport, and say which one in the log.
	   
	   A kernel driver that still owns the interface AFTER a successful claim
	   is the macOS CDC-ACM situation described in OdinSerialPort.h: the claim
	   succeeds, libusb_kernel_driver_active still reports 1, and every bulk
	   transfer times out because the pipes were never handed over. Measured
	   on an SM-A166U under macOS 26, where the same handshake that times out
	   on the bulk endpoints returns LOKE on the first attempt through the
	   kernel driver's own serial node.
	   
	   Where the claim really did take the interface - Linux, Windows, and
	   macOS for a device no driver matched - nothing changes and libusb is
	   used exactly as before. */
	if (!ChooseTransport())
		return (BridgeManager::kInitialiseFailed);

	if (!resume)
	{
		if (!InitialiseProtocol())
			return (BridgeManager::kInitialiseFailed);
	}

	return (BridgeManager::kInitialiseSucceeded);
}

bool BridgeManager::BeginSession(void)
{
	Interface::Print("Beginning session...\n");

	BeginSessionPacket beginSessionPacket;

	if (!SendPacket(&beginSessionPacket))
	{
		Interface::PrintError("Failed to begin session!\n");
		return (false);
	}

	SessionSetupResponse beginSessionResponse;
	if (!ReceivePacket(&beginSessionResponse))
		return (false);

	unsigned int deviceDefaultPacketSize = beginSessionResponse.GetResult();

	// EnneaOdin: upstream branches on this and discards it. Zero means the
	// device will not accept a packet-size change, which is worth reporting.
	this->deviceDefaultPacketSize = deviceDefaultPacketSize;

	Interface::Print("\nSome devices may take up to 2 minutes to respond.\nPlease be patient!\n\n");

	// EnneaOdin: upstream sleeps here unconditionally to give a TERMINAL user
	// time to read the line above. A GUI already has it in its log, and this
	// program opens a session for six different operations, so it is three
	// seconds each time for nothing. Attached() is false for a terminal, so
	// that build keeps the pause.
	if (!EnneaOdin::Hooks::Attached())
		Sleep(3000); // Give the user time to read the message.

	if (deviceDefaultPacketSize != 0) // 0 means changing the packet size is not supported.
	{
		fileTransferSequenceTimeout = 120000; // 2 minutes!

		/* EnneaOdin: the file part size is EIGHT times upstream's.
		   
		   Upstream hard-codes 1 MiB. The handset's own limits, from its
		   bootloader (s-lk.img, process_packet):
		   
		     - the part size arrives in control packet {100, 5} and is
		       rejected only if a byte above bit 23 is set, so anything up to
		       just under 16 MiB is accepted:
		           if (HIBYTE(v52) != 0) { "USB packet size is too big!"; fail }
		     - the per-SEQUENCE byte count arrives in {102, 2} and is rejected
		       above 152 MiB:
		           if (v52 >= 0x9800001) { "Binary Size is too bigger than
		                                    buffer"; fail }
		     - under 32 MiB the device takes an ordinary buffer; at or above
		       it waits for a larger one, printing "image is waiting to get
		       BIG usb buffer".
		   
		   Both limits are CHECKED by the handset and answered with a failure
		   code, which Heimdall propagates: too large fails loudly rather than
		   writing something wrong. That is why this is a straight change and
		   not a gamble.
		   
		   8 MiB x 4 parts = 32 MiB a sequence, which is upstream's 30 MiB
		   sequence with an eighth of the round trips. A 6.7 GiB super.img goes
		   from about 6700 part exchanges to about 840. */
		fileTransferPacketSize = 8 * 1048576;
		fileTransferSequenceMaxLength = 4;

		FilePartSizePacket filePartSizePacket(fileTransferPacketSize);

		if (!SendPacket(&filePartSizePacket))
		{
			Interface::PrintError("Failed to send file part size packet!\n");
			return (false);
		}

		SessionSetupResponse filePartSizeResponse;

		if (!ReceivePacket(&filePartSizeResponse))
			return (false);

		if (filePartSizeResponse.GetResult() != 0)
		{
			Interface::PrintError("Unexpected file part size response!\nExpected: 0\nReceived: %d\n", filePartSizeResponse.GetResult());
			return (false);
		}
	}

	Interface::Print("Session begun.\n\n");
	return (true);
}

bool BridgeManager::EndSession(bool reboot) const
{
	Interface::Print("Ending session...\n");

	EndSessionPacket *endSessionPacket = new EndSessionPacket(EndSessionPacket::kRequestEndSession);
	bool success = SendPacket(endSessionPacket);
	delete endSessionPacket;

	if (!success)
	{
		Interface::PrintError("Failed to send end session packet!\n");

		return (false);
	}

	ResponsePacket *endSessionResponse = new ResponsePacket(ResponsePacket::kResponseTypeEndSession);
	success = ReceivePacket(endSessionResponse);
	delete endSessionResponse;

	if (!success)
	{
		Interface::PrintError("Failed to receive session end confirmation!\n");

		return (false);
	}

	if (reboot)
	{
		Interface::Print("Rebooting device...\n");

		EndSessionPacket *rebootDevicePacket = new EndSessionPacket(EndSessionPacket::kRequestRebootDevice);
		bool success = SendPacket(rebootDevicePacket);
		delete rebootDevicePacket;

		if (!success)
		{
			Interface::PrintError("Failed to send reboot device packet!\n");

			return (false);
		}

		ResponsePacket *rebootDeviceResponse = new ResponsePacket(ResponsePacket::kResponseTypeEndSession);
		success = ReceivePacket(rebootDeviceResponse);
		delete rebootDeviceResponse;

		if (!success)
		{
			Interface::PrintError("Failed to receive reboot confirmation!\n");

			return (false);
		}
	}

	return (true);
}

bool BridgeManager::SendBulkTransfer(unsigned char *data, int length, int timeout, bool retry) const
{
	/* EnneaOdin: the serial transport. An empty transfer is a zero-length USB
	   packet, which has no equivalent on a byte stream and no meaning there -
	   upstream already treats a failed one as "continue anyway" - so it is
	   reported as done without touching the port. */
	if (useSerialTransport)
	{
		if (data == nullptr || length == 0)
			return (true);

		const int attempts = retry ? 6 : 1;

		for (int attempt = 0; attempt < attempts; attempt++)
		{
			if (attempt > 0)
			{
				if (verbose)
					Interface::PrintError("Serial write failed. Retrying...\n");

				/* The same backoff as the libusb path, and cancellable. */
				if (!EnneaOdin::Hooks::Nap(250 * attempt))
					break;
			}

			const int written = serialPort.Write(data, length, timeout);

			if (written == length)
			{
				lastTransferResult = LIBUSB_SUCCESS;
				return (true);
			}

			lastTransferResult = (written < 0) ? LIBUSB_ERROR_IO : LIBUSB_ERROR_TIMEOUT;
		}

		return (false);
	}

	int dataTransferred;
	int result = libusb_bulk_transfer(deviceHandle, outEndpoint, data, length, &dataTransferred, timeout);

	// EnneaOdin: kept so a caller can report WHICH libusb error it was. The
	// handshake used to fail with "Failed to send handshake!" and no code,
	// which is not enough to tell a stalled pipe from a vanished device.
	lastTransferResult = result;

	if (result != LIBUSB_SUCCESS && retry)
	{
		static const int retryDelay = 250;

		if (verbose)
			Interface::PrintError("libusb error %d whilst sending bulk transfer.", result);

		// Retry
		for (int i = 0; i < 5; i++)
		{
			// EnneaOdin: upstream has no cancellation check in this loop.
			// Five retries are five 3000 ms libusb timeouts plus 3750 ms of
			// backoff: 21,750 ms per packet in which Stop does nothing.
			// Checking here, and sleeping through Nap, bounds it to one
			// timeout plus a 50 ms slice - the in-flight transfer cannot be
			// interrupted, so that is the floor. Both are inert with no hook
			// context.
			if (EnneaOdin::Hooks::Cancelled())
				break;

			if (verbose)
				Interface::PrintErrorSameLine(" Retrying...\n");

			// Wait longer each retry
			if (!EnneaOdin::Hooks::Nap(retryDelay * (i + 1)))
				break;

			result = libusb_bulk_transfer(deviceHandle, outEndpoint, data, length, &dataTransferred, timeout);
			lastTransferResult = result;

			if (result == LIBUSB_SUCCESS)
				break;

			if (verbose)
				Interface::PrintError("libusb error %d whilst sending bulk transfer.", result);
		}

		if (verbose)
			Interface::PrintErrorSameLine("\n");
	}

	return (result == LIBUSB_SUCCESS && dataTransferred == length);
}

int BridgeManager::ReceiveBulkTransfer(unsigned char *data, int length, int timeout, bool retry) const
{
	if (useSerialTransport)
	{
		/* As with the send: no zero-length packets on a stream. */
		if (data == nullptr || length == 0)
			return (0);

		const int attempts = retry ? 6 : 1;

		for (int attempt = 0; attempt < attempts; attempt++)
		{
			if (attempt > 0)
			{
				if (verbose)
					Interface::PrintError("Serial read came up short. Retrying...\n");

				if (!EnneaOdin::Hooks::Nap(250 * attempt))
					break;
			}

			const int got = serialPort.Read(data, length, timeout);

			if (got > 0)
			{
				/* Short reads are returned as they are, exactly as a short
				   bulk transfer would be: ReceivePacket decides whether the
				   packet it wanted allows a variable size. */
				lastTransferResult = LIBUSB_SUCCESS;
				return (got);
			}

			lastTransferResult = (got < 0) ? LIBUSB_ERROR_IO : LIBUSB_ERROR_TIMEOUT;
		}

		return (lastTransferResult);
	}

	if (data == nullptr)
	{
		// HACK: It seems WinUSB ignores us when we try to read with length zero.
		static unsigned char dummyData;
		data = &dummyData;
		length = 1;
	}

	int dataTransferred;
	int result = libusb_bulk_transfer(deviceHandle, inEndpoint, data, length, &dataTransferred, timeout);
	lastTransferResult = result;

	if (result != LIBUSB_SUCCESS && retry)
	{
		static const int retryDelay = 250;

		if (verbose)
			Interface::PrintError("libusb error %d whilst receiving bulk transfer.", result);

		// Retry
		for (int i = 0; i < 5; i++)
		{
			// EnneaOdin: the same uncancellable 21,750 ms as the send path.
			if (EnneaOdin::Hooks::Cancelled())
				break;

			if (verbose)
				Interface::PrintErrorSameLine(" Retrying...\n");

			// Wait longer each retry
			if (!EnneaOdin::Hooks::Nap(retryDelay * (i + 1)))
				break;

			result = libusb_bulk_transfer(deviceHandle, inEndpoint, data, length, &dataTransferred, timeout);
			lastTransferResult = result;

			if (result == LIBUSB_SUCCESS)
				break;

			if (verbose)
				Interface::PrintError("libusb error %d whilst receiving bulk transfer.", result);
		}

		if (verbose)
			Interface::PrintErrorSameLine("\n");
	}

	if (result != LIBUSB_SUCCESS)
		return (result);

	return (dataTransferred);
}

bool BridgeManager::SendPacket(OutboundPacket *packet, int timeout, int emptyTransferFlags) const
{
	packet->Pack();

	if (emptyTransferFlags & kEmptyTransferBefore)
	{
		if (!SendBulkTransfer(nullptr, 0, kDefaultTimeoutEmptyTransfer, false) && verbose)
		{
			Interface::PrintWarning("Empty bulk transfer before sending packet failed. Continuing anyway...\n");
		}
	}

	if (!SendBulkTransfer(packet->GetData(), packet->GetSize(), timeout))
		return (false);

	if (emptyTransferFlags & kEmptyTransferAfter)
	{
		if (!SendBulkTransfer(nullptr, 0, kDefaultTimeoutEmptyTransfer, false) && verbose)
		{
			Interface::PrintWarning("Empty bulk transfer after sending packet failed. Continuing anyway...\n");
		}
	}

	return (true);
}

bool BridgeManager::ReceivePacket(InboundPacket *packet, int timeout, int emptyTransferFlags) const
{
	if (emptyTransferFlags & kEmptyTransferBefore)
	{
		if (ReceiveBulkTransfer(nullptr, 0, kDefaultTimeoutEmptyTransfer, false) < 0 && verbose)
		{
			Interface::PrintWarning("Empty bulk transfer before receiving packet failed. Continuing anyway...\n");
		}
	}

	int receivedSize = ReceiveBulkTransfer(packet->GetData(), packet->GetSize(), timeout);

	if (receivedSize < 0)
		return (false);

	if (static_cast<unsigned int>(receivedSize) != packet->GetSize() && !packet->IsSizeVariable())
	{
		if (verbose)
			Interface::PrintError("Incorrect packet size received - expected size = %d, received size = %d.\n", packet->GetSize(), receivedSize);

		return (false);
	}

	packet->SetReceivedSize(receivedSize);

	bool unpacked = packet->Unpack();

	if (!unpacked && verbose)
		Interface::PrintError("Failed to unpack received packet.\n");

	if (emptyTransferFlags & kEmptyTransferAfter)
	{
		if (ReceiveBulkTransfer(nullptr, 0, kDefaultTimeoutEmptyTransfer, false) < 0 && verbose)
		{
			Interface::PrintWarning("Empty bulk transfer after receiving packet failed. Continuing anyway...\n");
		}
	}

	return (unpacked);
}

bool BridgeManager::RequestDeviceType(unsigned int request, int *result) const
{
	DeviceTypePacket deviceTypePacket;
	bool success = SendPacket(&deviceTypePacket);

	if (!success)
	{
		Interface::PrintError("Failed to request device info packet!\n");

		if (verbose)
			Interface::PrintError("Failed request: %u\n", request);

		return (false);
	}

	SessionSetupResponse deviceTypeResponse;

	if (!ReceivePacket(&deviceTypeResponse))
		return (false);

	*result = deviceTypeResponse.GetResult();

	return (true);
}

bool BridgeManager::SendPitData(const PitData *pitData) const
{
	unsigned int pitBufferSize = pitData->GetPaddedSize();

	// Start file transfer
	PitFilePacket *pitFilePacket = new PitFilePacket(PitFilePacket::kRequestFlash);
	bool success = SendPacket(pitFilePacket);
	delete pitFilePacket;

	if (!success)
	{
		Interface::PrintError("Failed to initialise PIT file transfer!\n");
		return (false);
	}

	PitFileResponse *pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to confirm transfer initialisation!\n");
		return (false);
	}

	// Transfer file size
	FlashPartPitFilePacket *flashPartPitFilePacket = new FlashPartPitFilePacket(pitBufferSize);
	success = SendPacket(flashPartPitFilePacket);
	delete flashPartPitFilePacket;

	if (!success)
	{
		Interface::PrintError("Failed to send PIT file part information!\n");
		return (false);
	}

	pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to confirm sending of PIT file part information!\n");
		return (false);
	}

	// Create packed in-memory PIT file

	unsigned char *pitBuffer = new unsigned char[pitBufferSize];
	memset(pitBuffer, 0, pitBufferSize);

	pitData->Pack(pitBuffer);

	// Flash pit file
	SendFilePartPacket *sendFilePartPacket = new SendFilePartPacket(pitBuffer, pitBufferSize);
	success = SendPacket(sendFilePartPacket);
	delete sendFilePartPacket;

	delete [] pitBuffer;

	if (!success)
	{
		Interface::PrintError("Failed to send file part packet!\n");
		return (false);
	}

	pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to receive PIT file part response!\n");
		return (false);
	}

	// End pit file transfer
	EndPitFileTransferPacket *endPitFileTransferPacket = new EndPitFileTransferPacket(pitBufferSize);
	success = SendPacket(endPitFileTransferPacket);
	delete endPitFileTransferPacket;

	if (!success)
	{
		Interface::PrintError("Failed to send end PIT file transfer packet!\n");
		return (false);
	}

	pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to confirm end of PIT file transfer!\n");
		return (false);
	}

	return (true);
}

int BridgeManager::ReceivePitFile(unsigned char **pitBuffer) const
{
	*pitBuffer = nullptr;

	bool success;

	// Start file transfer
	PitFilePacket *pitFilePacket = new PitFilePacket(PitFilePacket::kRequestDump);
	success = SendPacket(pitFilePacket);
	delete pitFilePacket;

	if (!success)
	{
		Interface::PrintError("Failed to request receival of PIT file!\n");
		return (0);
	}

	PitFileResponse *pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	unsigned int fileSize = pitFileResponse->GetFileSize();
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to receive PIT file size!\n");
		return (0);
	}

	unsigned int transferCount = fileSize / ReceiveFilePartPacket::kDataSize;
	if (fileSize % ReceiveFilePartPacket::kDataSize != 0)
		transferCount++;

	unsigned char *buffer = new unsigned char[fileSize];
	int offset = 0;

	for (unsigned int i = 0; i < transferCount; i++)
	{
		// EnneaOdin: checked between PIT parts. ReceivePitFile only publishes
		// *pitBuffer on success, so returning here leaves the caller's pointer
		// null and nothing to free.
		if (EnneaOdin::Hooks::Cancelled())
		{
			Interface::PrintError("Cancelled during the PIT file transfer.\n");
			delete [] buffer;
			return (0);
		}

		DumpPartPitFilePacket *requestPacket = new DumpPartPitFilePacket(i);
		success = SendPacket(requestPacket);
		delete requestPacket;

		if (!success)
		{
			Interface::PrintError("Failed to request PIT file part #%d!\n", i);
			delete [] buffer;
			return (0);
		}

		int receiveEmptyTransferFlags = (i == transferCount - 1) ? kEmptyTransferAfter : kEmptyTransferNone;

		ReceiveFilePartPacket *receiveFilePartPacket = new ReceiveFilePartPacket();
		success = ReceivePacket(receiveFilePartPacket, kDefaultTimeoutReceive, receiveEmptyTransferFlags);

		if (!success)
		{
			Interface::PrintError("Failed to receive PIT file part #%d!\n", i);
			delete receiveFilePartPacket;
			delete [] buffer;
			return (0);
		}

		// Copy the whole packet data into the buffer.
		memcpy(buffer + offset, receiveFilePartPacket->GetData(), receiveFilePartPacket->GetReceivedSize());
		offset += receiveFilePartPacket->GetReceivedSize();

		delete receiveFilePartPacket;
	}

	// End file transfer
	pitFilePacket = new PitFilePacket(PitFilePacket::kRequestEndTransfer);
	success = SendPacket(pitFilePacket);
	delete pitFilePacket;

	if (!success)
	{
		Interface::PrintError("Failed to send request to end PIT file transfer!\n");
		delete [] buffer;
		return (0);
	}

	pitFileResponse = new PitFileResponse();
	success = ReceivePacket(pitFileResponse);
	delete pitFileResponse;

	if (!success)
	{
		Interface::PrintError("Failed to receive end PIT file transfer verification!\n");
		delete [] buffer;
		return (0);
	}

	*pitBuffer = buffer;
	return (fileSize);
}

int BridgeManager::DownloadPitFile(unsigned char **pitBuffer) const
{
	Interface::Print("Downloading device's PIT file...\n");

	int devicePitFileSize = ReceivePitFile(pitBuffer);

	if (!*pitBuffer)
	{
		Interface::PrintError("Failed to download PIT file!\n");
		return (0);
	}

	Interface::Print("PIT file download successful.\n\n");
	return (devicePitFileSize);
}

bool BridgeManager::SendFile(FILE *file, unsigned int destination, unsigned int deviceType, unsigned int fileIdentifier) const
{
	if (destination != EndFileTransferPacket::kDestinationModem && destination != EndFileTransferPacket::kDestinationPhone)
	{
		Interface::PrintError("Attempted to send file to unknown destination!\n");
		return (false);
	}

	if (destination == EndFileTransferPacket::kDestinationModem && fileIdentifier != 0xFFFFFFFF)
	{
		Interface::PrintError("The modem file does not have an identifier!\n");
		return (false);
	}

	FileTransferPacket *flashFileTransferPacket = new FileTransferPacket(FileTransferPacket::kRequestFlash);
	bool success = SendPacket(flashFileTransferPacket);
	delete flashFileTransferPacket;

	if (!success)
	{
		Interface::PrintError("Failed to initialise file transfer!\n");
		return (false);
	}

	FileSeek(file, 0, SEEK_END);
	unsigned long fileSize = (unsigned long)FileTell(file);
	FileRewind(file);

	ResponsePacket *fileTransferResponse = new ResponsePacket(ResponsePacket::kResponseTypeFileTransfer);
	success = ReceivePacket(fileTransferResponse);
	delete fileTransferResponse;

	if (!success)
	{
		Interface::PrintError("Failed to confirm transfer initialisation!\n");
		return (false);
	}

	unsigned int sequenceCount = fileSize / (fileTransferSequenceMaxLength * fileTransferPacketSize);
	unsigned int lastSequenceSize = fileTransferSequenceMaxLength;
	unsigned int partialPacketByteCount = fileSize % fileTransferPacketSize;

	if (fileSize % (fileTransferSequenceMaxLength * fileTransferPacketSize) != 0)
	{
		sequenceCount++;

		unsigned int lastSequenceBytes = fileSize % (fileTransferSequenceMaxLength * fileTransferPacketSize);
		lastSequenceSize = lastSequenceBytes / fileTransferPacketSize;

		if (partialPacketByteCount != 0)
			lastSequenceSize++;
	}

	unsigned long bytesTransferred = 0;
	unsigned int currentPercent;
	unsigned int previousPercent = 0;

	/* EnneaOdin: the percentage is for a TERMINAL. Upstream draws it in place
	   with backspaces, which a log pane cannot render - it arrives as a line
	   of its own per step, so a single partition contributes up to a hundred
	   lines of noise between the two that matter. A caller with a progress
	   callback installed already has the byte counts below. */
	const bool printPercent = !EnneaOdin::Hooks::Attached();

	if (printPercent)
		Interface::Print("0%%");

	for (unsigned int sequenceIndex = 0; sequenceIndex < sequenceCount; sequenceIndex++)
	{
		bool isLastSequence = (sequenceIndex == sequenceCount - 1);
		unsigned int sequenceSize = (isLastSequence) ? lastSequenceSize : fileTransferSequenceMaxLength;
		unsigned int sequenceTotalByteCount = sequenceSize * fileTransferPacketSize;

		FlashPartFileTransferPacket *beginFileTransferPacket = new FlashPartFileTransferPacket(sequenceTotalByteCount);
		success = SendPacket(beginFileTransferPacket);
		delete beginFileTransferPacket;

		if (!success)
		{
			Interface::PrintErrorSameLine("\n");
			Interface::PrintError("Failed to begin file transfer sequence!\n");
			return (false);
		}

		fileTransferResponse = new ResponsePacket(ResponsePacket::kResponseTypeFileTransfer);
		bool success = ReceivePacket(fileTransferResponse);
		delete fileTransferResponse;

		if (!success)
		{
			Interface::PrintErrorSameLine("\n");
			Interface::PrintError("Failed to confirm beginning of file transfer sequence!\n");
			return (false);
		}

		for (unsigned int filePartIndex = 0; filePartIndex < sequenceSize; filePartIndex++)
		{
			// EnneaOdin: the finest grain cancellation can have without
			// abandoning a packet half sent - one file part, which is 131072
			// bytes by default. Stopping here leaves the device at a packet
			// boundary, so the caller can still end the session cleanly.
			if (EnneaOdin::Hooks::Cancelled())
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Cancelled during the file transfer.\n");
				return (false);
			}

			// NOTE: This empty transfer thing is entirely ridiculous, but sadly it seems to be required.
			int sendEmptyTransferFlags = (filePartIndex == 0) ? kEmptyTransferNone : kEmptyTransferBefore;

			// Send
			// EnneaOdin: fileSize is passed in rather than re-measured per
			// packet, the offset this part came from is remembered so a retry
			// can seek back to it, and a short read is a failure instead of a
			// packet of zeros.
			SendFilePartPacket *sendFilePartPacket = new SendFilePartPacket(file, fileTransferPacketSize, fileSize);
			unsigned long partReadOffset = sendFilePartPacket->GetReadOffset();
			bool partShortRead = sendFilePartPacket->ShortRead();
			success = SendPacket(sendFilePartPacket, kDefaultTimeoutSend, sendEmptyTransferFlags);
			delete sendFilePartPacket;

			if (partShortRead)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Failed to read file part %d from disk!\n", filePartIndex);
				return (false);
			}

			if (!success)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Failed to send file part packet!\n");
				return (false);
			}

			// Response
			SendFilePartResponse *sendFilePartResponse = new SendFilePartResponse();
			success = ReceivePacket(sendFilePartResponse);
			unsigned int receivedPartIndex = sendFilePartResponse->GetPartIndex();

			delete sendFilePartResponse;

			if (!success)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Failed to receive file part response!\n");

				for (int retry = 0; retry < 4; retry++)
				{
					Interface::PrintErrorSameLine("\n");
					Interface::PrintError("Retrying...");

					// Send
					// EnneaOdin: THE FIX FOR THE CORRUPTING DEFECT. Without this
					// seek the constructor reads from wherever the file position
					// now is, which is the START OF THE NEXT PART, so the retry
					// sent the following packet's bytes in place of the failed
					// one. When the device had genuinely missed the part it
					// accepted that as the part, answered with the index that was
					// expected, the index check passed, and the partition was
					// written short by one packet with no error reported.
					FileSeek(file, partReadOffset, SEEK_SET);

					sendFilePartPacket = new SendFilePartPacket(file, fileTransferPacketSize, fileSize);
					if (sendFilePartPacket->ShortRead())
					{
						delete sendFilePartPacket;
						Interface::PrintErrorSameLine("\n");
						Interface::PrintError("Failed to re-read file part %d from disk!\n", filePartIndex);
						return (false);
					}
					success = SendPacket(sendFilePartPacket, kDefaultTimeoutSend, sendEmptyTransferFlags);
					delete sendFilePartPacket;

					if (!success)
					{
						Interface::PrintErrorSameLine("\n");
						Interface::PrintError("Failed to send file part packet!\n");
						return (false);
					}

					// Response
					sendFilePartResponse = new SendFilePartResponse();
					success = ReceivePacket(sendFilePartResponse);
					unsigned int receivedPartIndex = sendFilePartResponse->GetPartIndex();

					delete sendFilePartResponse;

					if (receivedPartIndex != filePartIndex)
					{
						Interface::PrintErrorSameLine("\n");
						Interface::PrintError("Expected file part index: %d Received: %d\n", filePartIndex, receivedPartIndex);
						return (false);
					}

					if (success)
						break;
				}

				if (!success)
					return (false);
			}

			if (receivedPartIndex != filePartIndex)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Expected file part index: %d Received: %d\n", filePartIndex, receivedPartIndex);
				return (false);
			}

			bytesTransferred += fileTransferPacketSize;

			if (bytesTransferred > fileSize)
				bytesTransferred = fileSize;

			currentPercent = (unsigned int)(100.0 * ((double)bytesTransferred / (double)fileSize));

			if (currentPercent != previousPercent && printPercent)
			{
				if (!verbose)
				{
					if (previousPercent < 10)
						Interface::Print("\b\b%d%%", currentPercent);
					else
						Interface::Print("\b\b\b%d%%", currentPercent);
				}
				else
				{
					Interface::Print("\n%d%%\n", currentPercent);
				}
			}

			previousPercent = currentPercent;

			// EnneaOdin: progress in bytes, so a caller does not have to
			// scrape the percentage printed above out of the log.
			EnneaOdin::Hooks::Progress(bytesTransferred, fileSize);
		}

		unsigned int sequenceEffectiveByteCount = (isLastSequence && partialPacketByteCount != 0) ?
			fileTransferPacketSize * (lastSequenceSize - 1) + partialPacketByteCount : sequenceTotalByteCount;

		if (destination == EndFileTransferPacket::kDestinationPhone)
		{
			EndPhoneFileTransferPacket *endPhoneFileTransferPacket = new EndPhoneFileTransferPacket(sequenceEffectiveByteCount, 0, deviceType, fileIdentifier, isLastSequence);

			success = SendPacket(endPhoneFileTransferPacket, kDefaultTimeoutSend, kEmptyTransferBeforeAndAfter);
			delete endPhoneFileTransferPacket;

			if (!success)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Failed to end phone file transfer sequence!\n");
				return (false);
			}
		}
		else // destination == EndFileTransferPacket::kDestinationModem
		{
			EndModemFileTransferPacket *endModemFileTransferPacket = new EndModemFileTransferPacket(sequenceEffectiveByteCount, 0, deviceType, isLastSequence);

			success = SendPacket(endModemFileTransferPacket, kDefaultTimeoutSend, kEmptyTransferBeforeAndAfter);
			delete endModemFileTransferPacket;

			if (!success)
			{
				Interface::PrintErrorSameLine("\n");
				Interface::PrintError("Failed to end modem file transfer sequence!\n");
				return (false);
			}
		}

		fileTransferResponse = new ResponsePacket(ResponsePacket::kResponseTypeFileTransfer);
		success = ReceivePacket(fileTransferResponse, fileTransferSequenceTimeout);
		delete fileTransferResponse;

		if (!success)
		{
			Interface::PrintErrorSameLine("\n");
			Interface::PrintError("Failed to confirm end of file transfer sequence!\n");
			return (false);
		}
	}

	if (!verbose)
		Interface::Print("\n");

	return (true);
}

void BridgeManager::SetUsbLogLevel(UsbLogLevel usbLogLevel)
{
	this->usbLogLevel = usbLogLevel;

	if (libusbContext)
	{
		switch (usbLogLevel)
		{
			case UsbLogLevel::None:
				libusb_set_option(libusbContext, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_NONE);
				break;

			case UsbLogLevel::Error:
				libusb_set_option(libusbContext, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_ERROR);
				break;

			case UsbLogLevel::Warning:
				libusb_set_option(libusbContext, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_WARNING);
				break;

			case UsbLogLevel::Info:
				libusb_set_option(libusbContext, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_INFO);
				break;

			case UsbLogLevel::Debug:
				libusb_set_option(libusbContext, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_DEBUG);
				break;
		}
	}
}

#ifdef OS_LINUX
bool BridgeManager::IsUbuntu()
{
	std::ifstream os_release("/etc/os-release");
	std::string line, entry, os;
	int pos;
	while (std::getline(os_release, line))
	{
		pos = line.find("=");
		entry = line.substr(0, pos);
		if (entry == "ID")
		{
			os = line.substr(pos+1);
			if (verbose)
			{
				Interface::Print("Linux distro ID: %s\n",
						 os.c_str());
			}
			if (os == "ubuntu")
			{
				return true;
			}
			break;
		}
	}
	return false;
}
#endif
