/* OdinSerialPort.cpp - see OdinSerialPort.h for why this transport exists.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "OdinSerialPort.h"

#include <cerrno>
#include <cstring>

#include <ctime>

#include <fcntl.h>
#include <poll.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <unistd.h>

#ifdef __APPLE__
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/IOBSD.h>
#include <IOKit/IOKitLib.h>
#include <IOKit/serial/IOSerialKeys.h>
#include <IOKit/usb/IOUSBLib.h>
#endif

namespace EnneaOdin
{
	namespace
	{
#ifdef __APPLE__
		/* Read a CFNumber property from service or any of its ancestors.
		   The idVendor and idProduct live on the USB DEVICE, while the serial
		   node hangs several levels below it (device -> interface -> ACM
		   control -> ACM data -> IOSerialBSDClient), so this has to climb. */
		bool AncestorNumber(io_service_t service, CFStringRef key, long *out)
		{
			io_service_t current = service;
			IOObjectRetain(current);

			for (int depth = 0; depth < 12 && current != IO_OBJECT_NULL; depth++)
			{
				CFTypeRef value = IORegistryEntryCreateCFProperty(
					current, key, kCFAllocatorDefault, 0);

				if (value != NULL)
				{
					bool got = false;

					if (CFGetTypeID(value) == CFNumberGetTypeID())
						got = CFNumberGetValue((CFNumberRef)value, kCFNumberLongType, out);

					CFRelease(value);

					if (got)
					{
						IOObjectRelease(current);
						return true;
					}
				}

				io_service_t parent = IO_OBJECT_NULL;

				if (IORegistryEntryGetParentEntry(current, kIOServicePlane, &parent)
				        != KERN_SUCCESS)
				{
					break;
				}

				IOObjectRelease(current);
				current = parent;
			}

			if (current != IO_OBJECT_NULL)
				IOObjectRelease(current);

			return false;
		}

		bool CalloutPath(io_service_t service, char *path, size_t pathLength)
		{
			CFTypeRef value = IORegistryEntryCreateCFProperty(
				service, CFSTR(kIOCalloutDeviceKey), kCFAllocatorDefault, 0);

			if (value == NULL)
				return false;

			bool got = false;

			if (CFGetTypeID(value) == CFStringGetTypeID())
			{
				got = CFStringGetCString((CFStringRef)value, path, (CFIndex)pathLength,
				                         kCFStringEncodingUTF8);
			}

			CFRelease(value);
			return got;
		}
#endif
	}

	SerialPort::SerialPort(void)
		: descriptor(-1)
	{
		openPath[0] = '\0';

		writeStats.calls = 0;
		writeStats.polls = 0;
		writeStats.bytes = 0;
		writeStats.microsWriting = 0;
		writeStats.microsPolling = 0;
		writeStats.smallestAccepted = -1;
		writeStats.largestAccepted = 0;
	}

	SerialPort::~SerialPort(void)
	{
		Close();
	}

	bool SerialPort::Find(unsigned short vendorId, unsigned short productId,
	                      char *path, size_t pathLength)
	{
		if (path == NULL || pathLength == 0)
			return false;

		path[0] = '\0';

#ifdef __APPLE__
		CFMutableDictionaryRef matching = IOServiceMatching(kIOSerialBSDServiceValue);

		if (matching == NULL)
			return false;

		/* Callout nodes only (/dev/cu.*), not the dial-in nodes (/dev/tty.*):
		   opening a dial-in node blocks waiting for carrier detect. */
		CFDictionarySetValue(matching, CFSTR(kIOSerialBSDTypeKey),
		                     CFSTR(kIOSerialBSDAllTypes));

		io_iterator_t iterator = IO_OBJECT_NULL;

		if (IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator)
		        != KERN_SUCCESS)
		{
			return false;
		}

		bool found = false;
		io_service_t service;

		while (!found && (service = IOIteratorNext(iterator)) != IO_OBJECT_NULL)
		{
			long vendor = 0;
			long product = 0;

			if (AncestorNumber(service, CFSTR(kUSBVendorID), &vendor)
			        && AncestorNumber(service, CFSTR(kUSBProductID), &product)
			        && (unsigned short)vendor == vendorId
			        && (unsigned short)product == productId)
			{
				found = CalloutPath(service, path, pathLength);
			}

			IOObjectRelease(service);
		}

		IOObjectRelease(iterator);
		return found;
#else
		(void)vendorId;
		(void)productId;
		return false;
#endif
	}

	bool SerialPort::Open(const char *path)
	{
		Close();

		if (path == NULL || path[0] == '\0')
			return false;

		/* O_NONBLOCK so the open itself cannot hang, and no controlling
		   terminal so a hangup on this node cannot signal the process. */
		int fd = open(path, O_RDWR | O_NOCTTY | O_NONBLOCK);

		if (fd < 0)
			return false;

		/* Raw: no line discipline at all. Every byte of the Odin protocol is
		   binary, so any translation, echo, flow control or signal generation
		   would corrupt it. */
		struct termios settings;

		if (tcgetattr(fd, &settings) != 0)
		{
			int saved = errno;
			close(fd);
			errno = saved;
			return false;
		}

		cfmakeraw(&settings);

		settings.c_cflag |= (CS8 | CREAD | CLOCAL);   /* 8 bits, read, ignore modem lines */
		settings.c_cflag &= (tcflag_t)~(PARENB | CSTOPB | CRTSCTS);
		settings.c_iflag &= (tcflag_t)~(IXON | IXOFF | IXANY);

		/* Return from read immediately with whatever is there; the timeout is
		   handled by poll, which can be interrupted and can wait on a
		   deadline that termios' deciseconds cannot express. */
		settings.c_cc[VMIN] = 0;
		settings.c_cc[VTIME] = 0;

		/* A CDC device ignores the line speed, but termios insists on one. */
		cfsetispeed(&settings, B115200);
		cfsetospeed(&settings, B115200);

		if (tcsetattr(fd, TCSANOW, &settings) != 0)
		{
			int saved = errno;
			close(fd);
			errno = saved;
			return false;
		}

		descriptor = fd;
		strncpy(openPath, path, kMaxPathLength - 1);
		openPath[kMaxPathLength - 1] = '\0';

		/* Both halves of what Odin 3.14.1 does, in its order: PurgeComm with
		   all four flags, then Sleep(500). See kSettleMs. */
		Flush();

		struct timespec settle;
		settle.tv_sec = kSettleMs / 1000;
		settle.tv_nsec = (long)(kSettleMs % 1000) * 1000000L;
		nanosleep(&settle, NULL);

		/* The open needed O_NONBLOCK so it could not hang waiting for carrier.
		   Writing wants the opposite.
		   
		   Odin hands a whole part to the driver in one WriteFile and waits for
		   it; the Windows driver streams it without ever coming back to user
		   space. A non-blocking descriptor cannot do that: write() takes only
		   what fits in the tty's output queue and returns, so every few
		   kilobytes costs a poll wakeup, and an 8 MiB part pays that hundreds
		   of times. Blocking lets one write() copy, sleep inside the kernel
		   while the driver drains, and carry on - the waiting happens where
		   the data is. */
		int flags = fcntl(descriptor, F_GETFL, 0);

		if (flags >= 0)
			fcntl(descriptor, F_SETFL, flags & ~O_NONBLOCK);

		return true;
	}

	void SerialPort::Close(void)
	{
		if (descriptor >= 0)
		{
			close(descriptor);
			descriptor = -1;
		}

		openPath[0] = '\0';
	}

	static long long Micros(void)
	{
		struct timespec now;
		clock_gettime(CLOCK_MONOTONIC, &now);
		return (long long)now.tv_sec * 1000000LL + now.tv_nsec / 1000;
	}

	SerialPort::WriteStats SerialPort::TakeWriteStats(void)
	{
		WriteStats taken = writeStats;

		writeStats.calls = 0;
		writeStats.polls = 0;
		writeStats.bytes = 0;
		writeStats.microsWriting = 0;
		writeStats.microsPolling = 0;
		writeStats.smallestAccepted = -1;
		writeStats.largestAccepted = 0;

		return (taken);
	}

	bool SerialPort::DrainFor(int timeoutMs)
	{
		if (descriptor < 0)
			return (false);

		/* 2 ms a turn: a 4-byte handshake leaves the queue almost at once, and
		   even a megabyte at 12 Mbit/s is tens of milliseconds. */
		const int slice = 2;
		int waited = 0;

		while (waited <= timeoutMs)
		{
			int pending = 0;

			if (ioctl(descriptor, TIOCOUTQ, &pending) != 0)
				return (false);        /* cannot ask; do not pretend to wait */

			if (pending == 0)
				return (true);

			struct timespec nap;
			nap.tv_sec = 0;
			nap.tv_nsec = (long)slice * 1000000L;
			nanosleep(&nap, NULL);
			waited += slice;
		}

		return (false);
	}

	void SerialPort::Flush(void)
	{
		if (descriptor >= 0)
			tcflush(descriptor, TCIOFLUSH);
	}

	int SerialPort::Write(const unsigned char *data, int length, int timeoutMs)
	{
		if (descriptor < 0 || data == NULL || length < 0)
			return -1;

		if (length == 0)
			return 0;

		int done = 0;

		while (done < length)
		{
			int chunk = length - done;

			if (chunk > kWriteChunkBytes)
				chunk = kWriteChunkBytes;

			/* One poll per chunk, not per write: it is what keeps the timeout
			   meaningful on a descriptor that now blocks. A full queue is
			   reported here rather than discovered by a short write. */
			struct pollfd waiting;
			waiting.fd = descriptor;
			waiting.events = POLLOUT;
			waiting.revents = 0;

			long long pollBegan = Micros();
			int ready = poll(&waiting, 1, timeoutMs);
			writeStats.microsPolling += Micros() - pollBegan;
			writeStats.polls++;

			if (ready < 0)
			{
				if (errno == EINTR)
					continue;

				return -1;
			}

			if (ready == 0)
				return done;                        /* timed out part way */

			if ((waiting.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0)
				return -1;

			int inChunk = 0;

			while (inChunk < chunk)
			{
				long long writeBegan = Micros();
				ssize_t written = write(descriptor,
				                        data + done + inChunk,
				                        (size_t)(chunk - inChunk));
				writeStats.microsWriting += Micros() - writeBegan;
				writeStats.calls++;

				if (written > 0)
				{
					writeStats.bytes += written;

					if (writeStats.smallestAccepted < 0
					    || written < writeStats.smallestAccepted)
						writeStats.smallestAccepted = (int)written;

					if (written > writeStats.largestAccepted)
						writeStats.largestAccepted = (int)written;
				}

				if (written < 0)
				{
					if (errno == EINTR)
						continue;

					/* The descriptor is meant to block, but a driver can still
					   hand back EAGAIN. Fall back to waiting for room. */
					if (errno == EAGAIN)
						break;

					return -1;
				}

				if (written == 0)
					break;

				inChunk += (int)written;
			}

			done += inChunk;
		}

		/* NO DRAIN HERE, and that is the point.
		   
		   Draining after every write empties the kernel's output queue before
		   the next one is allowed to start, so the USB pipe goes idle in the
		   gap between "queue empty" and "next write queued" - once per 8 MiB
		   part, for every part of a nine gigabyte flash. The loop above has
		   already got every byte into the kernel; leaving them there is what
		   keeps the pipe full.
		   
		   A caller that is about to wait for a reply, rather than send more,
		   calls DrainFor itself. InitialiseProtocol is the one that does. */
		return done;
	}

	int SerialPort::Read(unsigned char *data, int length, int timeoutMs)
	{
		if (descriptor < 0 || data == NULL || length < 0)
			return -1;

		if (length == 0)
			return 0;

		int done = 0;

		while (done < length)
		{
			struct pollfd waiting;
			waiting.fd = descriptor;
			waiting.events = POLLIN;
			waiting.revents = 0;

			/* The full timeout while nothing has arrived yet; a short gap
			   afterwards, which is what ends a response shorter than the
			   buffer it was read into. See kInterByteTimeoutMs. */
			const int wait = (done == 0)
				? timeoutMs
				: (timeoutMs < kInterByteTimeoutMs ? timeoutMs : kInterByteTimeoutMs);

			int ready = poll(&waiting, 1, wait);

			if (ready < 0)
			{
				if (errno == EINTR)
					continue;

				return -1;
			}

			if (ready == 0)
				return done;        /* short, which the caller may accept */

			if ((waiting.revents & (POLLERR | POLLNVAL)) != 0)
				return -1;

			ssize_t got = read(descriptor, data + done, (size_t)(length - done));

			if (got < 0)
			{
				if (errno == EINTR || errno == EAGAIN)
					continue;

				return -1;
			}

			if (got == 0)
			{
				/* POLLHUP with nothing to read: the device went away. */
				if ((waiting.revents & POLLHUP) != 0)
					return done > 0 ? done : -1;

				continue;
			}

			done += (int)got;
		}

		return done;
	}
}
