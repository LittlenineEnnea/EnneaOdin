/* OdinSerialPort.h - the Odin protocol over a CDC serial node.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * WHY THIS EXISTS
 *
 * On macOS, Samsung's download mode enumerates as a CDC-ACM gadget and the
 * system binds AppleUSBACMData to its data interface. libusb cannot take that
 * interface away: libusb_detach_kernel_driver returns LIBUSB_ERROR_ACCESS and
 * the backend says why - "USB device capture requires either an entitlement
 * (com.apple.vm.device-access) or root privilege". libusb_claim_interface then
 * SUCCEEDS anyway while the kernel driver still owns the pipes
 * (libusb_kernel_driver_active still reports 1 after the claim), so every bulk
 * transfer times out. Measured on an SM-A166U under macOS 26.
 *
 * The kernel driver publishes the same two bulk endpoints as a serial node.
 * This reaches the device through that node instead, which needs no
 * entitlement and no root.
 *
 * The Odin protocol is a sequence of fixed-size messages, so a byte stream
 * carries it provided each read collects exactly as many bytes as the caller
 * asked for. That is what Read does.
 */

#ifndef ODIN_SERIAL_PORT_H
#define ODIN_SERIAL_PORT_H

#include <cstddef>

namespace EnneaOdin
{
	class SerialPort
	{
		public:

			/* The longest /dev path this will report. */
			static const size_t kMaxPathLength = 256;

			SerialPort(void);
			~SerialPort(void);

			/* Find the callout node that belongs to the USB device with this
			   vendor and product id, by walking the IOKit registry from each
			   serial node up to its USB device. NOT a glob over /dev: a
			   machine with two serial adapters attached has several nodes and
			   only one of them is the handset.

			   Returns true and fills path on success. */
			static bool Find(unsigned short vendorId, unsigned short productId,
			                 char *path, size_t pathLength);

			/* Open path in raw mode. False if it cannot be opened, with errno
			   left for the caller to report. */
			bool Open(const char *path);

			void Close(void);

			bool IsOpen(void) const { return descriptor >= 0; }

			/* Write every byte or fail. Returns the number written, or -1. */
			int Write(const unsigned char *data, int length, int timeoutMs);

			/* How long to wait after configuring the port before the first
			   byte is sent.
			   
			   Odin 3.14.1 does exactly this: its port setup ends in
			   Sleep(500) after SetCommState, before the caller writes
			   anything (sub_437FB0). Reversed from the shipped binary, not
			   guessed. Writing immediately after opening is what the earlier
			   version here did, and it is the difference between a handshake
			   that lands and one that is never answered. */
			static const int kSettleMs = 500;

			/* How long to keep waiting for more bytes once some have
			   arrived. A byte stream has no packet boundaries, so this is
			   what stands in for a short USB packet ending a transfer.
			   
			   Without it, asking for more bytes than the device is going to
			   send means waiting out the whole timeout every time: the
			   handshake reads 7 bytes into a buffer the device answers with
			   4, which cost three seconds a go.
			   
			   300 ms is far longer than any gap inside a stream the device is
			   actively sending - a megabyte file part arrives as a continuous
			   run - and far shorter than the timeouts the protocol uses. */
			static const int kInterByteTimeoutMs = 300;

			/* Collect exactly length bytes, or as many as arrive before the
			   timeout. Returns the number read, or -1 on a real error.
			   A return of less than length is a timeout, not an error: the
			   protocol has variable-size responses and the caller decides
			   whether a short read matters. */
			int Read(unsigned char *data, int length, int timeoutMs);

			/* Wait, BOUNDED, for the kernel's output queue to empty.
			   Returns true when it drained, false on a timeout or if the
			   queue cannot be queried.
			   
			   tcdrain would be the obvious call and is the wrong one: it
			   blocks until the other end consumes, with no deadline at all,
			   inside a Write that promises to honour one. Odin 3.14.1 does
			   not do an unbounded drain either - it uses overlapped I/O and
			   waits on the event with its own timeout. */
			bool DrainFor(int timeoutMs);

			/* Throw away anything the kernel has buffered in either
			   direction. Called before a handshake so a previous attempt's
			   leftovers cannot be mistaken for this attempt's reply. */
			void Flush(void);

			const char *Path(void) const { return openPath; }

		private:

			SerialPort(const SerialPort&);
			SerialPort& operator=(const SerialPort&);

			int descriptor;
			char openPath[kMaxPathLength];
	};
}

#endif
