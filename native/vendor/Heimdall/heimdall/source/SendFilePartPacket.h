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

#ifndef SENDFILEPARTPACKET_H
#define SENDFILEPARTPACKET_H

// C Standard Library
#include <stdio.h>
#include <string.h>

// Heimdall
#include "Packet.h"

namespace Heimdall
{
	class SendFilePartPacket : public OutboundPacket
	{
		public:

			// EnneaOdin: this constructor used to learn the file's length by
			// seeking to the end and back on EVERY packet, and ignored fread's
			// return value. With a 1 MiB packet size that is thousands of
			// round trips to the end of a multi-gigabyte image, each of which
			// also discards stdio's read buffer; and a short read caused by an
			// I/O error silently shipped the zero-filled buffer to the device.
			// The caller already knows the size (BridgeManager.cpp:1029), so it
			// passes it in, and the bytes actually read are recorded so the
			// caller can tell a legitimately partial last packet from a failure.
			SendFilePartPacket(FILE *file, unsigned long size, unsigned long fileSize)
				: OutboundPacket(size), bytesRead(0), readOffset(0)
			{
				memset(data, 0, size);

				readOffset = (unsigned long)FileTell(file);

				// Correct remaining-bytes arithmetic: min(size, fileSize - position).
				// The original read min(fileSize, size), which is only ever right
				// because the buffer is zero-filled and the protocol pads to the
				// packet size.
				unsigned long remaining = (fileSize > readOffset) ? fileSize - readOffset : 0;
				unsigned long bytesToRead = (remaining < size) ? remaining : size;

				if (bytesToRead > 0)
					bytesRead = (unsigned long)fread(data, 1, bytesToRead, file);

				expectedBytes = bytesToRead;
			}

			// Kept so any caller that still passes two arguments measures the
			// size itself exactly as before, rather than silently sending zeros.
			SendFilePartPacket(FILE *file, unsigned long size) : OutboundPacket(size)
			{
				unsigned long position = (unsigned long)FileTell(file);
				FileSeek(file, 0, SEEK_END);
				unsigned long fileSize = (unsigned long)FileTell(file);
				FileSeek(file, position, SEEK_SET);

				memset(data, 0, size);
				readOffset = position;
				unsigned long remaining = (fileSize > position) ? fileSize - position : 0;
				unsigned long bytesToRead = (remaining < size) ? remaining : size;
				bytesRead = (bytesToRead > 0)
					? (unsigned long)fread(data, 1, bytesToRead, file) : 0;
				expectedBytes = bytesToRead;
			}

			// True when fread returned less than the file had left to give,
			// which means an I/O error rather than the end of the image.
			bool ShortRead(void) const
			{
				return bytesRead != expectedBytes;
			}

			unsigned long GetBytesRead(void) const
			{
				return bytesRead;
			}

			// The file offset this part was read from. SendFile seeks back here
			// before a retry so the retry resends THIS part and not the next one.
			unsigned long GetReadOffset(void) const
			{
				return readOffset;
			}

			// EnneaOdin: the three members below are initialised here too. This
			// constructor takes an already-filled buffer rather than a file, so
			// there is no read to report on - but leaving them indeterminate
			// would make ShortRead() and GetReadOffset() read uninitialised
			// memory on such an object. No caller does that today
			// (BridgeManager.cpp:875 constructs one and never queries it), which
			// is exactly why it would be a trap for the next edit rather than a
			// visible bug. size, not 0, because every byte asked for is present.
			SendFilePartPacket(unsigned char *buffer, unsigned int size)
				: OutboundPacket(size), bytesRead(size), expectedBytes(size), readOffset(0)
			{
				memcpy(data, buffer, size);
			}

			void Pack(void)
			{
			}

		private:

			unsigned long bytesRead;
			unsigned long expectedBytes;
			unsigned long readOffset;
	};
}

#endif
