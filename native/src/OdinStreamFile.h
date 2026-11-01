/* OdinStreamFile.h - a real FILE * over a caller-supplied stream.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * BridgeManager::SendFile takes a FILE *. The data EnneaOdin has to send is
 * usually an LZ4 member inside a tar - this handset's super.img.lz4 is 5.5 GB
 * compressed inside a 6.7 GB AP.tar - so it is not a file on disk and must not
 * be spooled to one. This turns the caller's read/seek callbacks into a FILE *
 * that stdio treats like any other stream, using funopen on the BSDs and
 * macOS, or fopencookie on glibc.
 *
 * The stream is opened unbuffered on purpose. With a buffer, stdio reads ahead
 * and then discards the buffer on a seek, so its idea of the file position and
 * the number of bytes actually pulled from the caller's stream drift apart.
 * Unbuffered, one fread is one read callback and the two never disagree.
 *
 * Neither funopen nor fopencookie is universal: musl has neither. The build
 * detects this, Available() reports it, and the ABI turns it into
 * ODIN_UNSUPPORTED rather than a crash.
 */

#ifndef ODIN_STREAM_FILE_H
#define ODIN_STREAM_FILE_H

#include <cstdio>

#include "odin_bridge.h"

namespace EnneaOdin
{
	namespace StreamFile
	{
		/* True when this platform can make a FILE * out of callbacks. */
		bool Available(void);

		/* A window onto a live adapter. Both pointers stay valid until Close.

		   It exists because SendFilePartPacket (SendFilePartPacket.h:43)
		   ignores what fread returned and sends a packet it had already
		   zeroed. With nothing watching, a stream that failed part way through
		   a partition, or one whose declared length was simply wrong, would
		   write zeros to the handset and report success. */
		struct Observer
		{
			/* Set as soon as the caller's stream reports an error. */
			const bool *failed;

			/* Bytes actually pulled out of the caller's stream. After a
			   complete transfer this equals the declared length; less than
			   that means the device was padded with zeros. */
			const unsigned long long *consumed;

			Observer() : failed(nullptr), consumed(nullptr)
			{
			}
		};

		/* Returns a FILE * positioned at 0, or null. The caller keeps owning
		   *stream and its user pointer, which must outlive the FILE *. Close
		   it with Close, never with fclose: the cookie has to be freed too. */
		FILE *Open(const odin_stream *stream, unsigned long long length,
		           Observer *out_observer = nullptr);

		/* Closes a FILE * from Open and frees its cookie. The caller's stream
		   is not touched. Returns non-zero if the stream reported an error at
		   any point during its life. */
		int Close(FILE *file);
	}
}

#endif
