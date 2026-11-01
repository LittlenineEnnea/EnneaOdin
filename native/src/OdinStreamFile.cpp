/* OdinStreamFile.cpp
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * _GNU_SOURCE has to be defined before the first system header, which is why
 * it is here and not in the project header.
 */

#if defined(__linux__) && !defined(_GNU_SOURCE)
#  define _GNU_SOURCE 1
#endif

#include <cstdio>
#include <sys/types.h>

#include "OdinStreamFile.h"

#if defined(ODIN_HAVE_FUNOPEN) || defined(ODIN_HAVE_FOPENCOOKIE)
#  define ODIN_STREAM_FILE_SUPPORTED 1
#endif

namespace EnneaOdin
{
	namespace StreamFile
	{
		namespace
		{
			struct Cookie
			{
				odin_stream        stream;
				unsigned long long length;

				/* Bytes actually pulled out of the caller's stream: the true
				   read offset. */
				unsigned long long physical;

				/* Where stdio believes the file pointer is. The two differ
				   only between a seek to the end and the seek back, which is
				   exactly what SendFilePartPacket does on every part. */
				unsigned long long position;

				bool failed;
			};

			/* Set by the close callback so Close can report it after the
			   cookie has been freed. Per thread because one operation owns one
			   stream on one thread. */
			thread_local bool lastStreamFailed = false;

			long long ReadCore(Cookie *cookie, char *buffer, unsigned long long count)
			{
				if (cookie->failed)
					return -1;

				if (cookie->position != cookie->physical)
				{
					/* A real backwards jump: only a retry asks for this, and
					   only a seekable stream can answer it. */
					if (cookie->stream.seek == nullptr ||
					    cookie->stream.seek(cookie->stream.user, cookie->position) != 0)
					{
						cookie->failed = true;
						return -1;
					}

					cookie->physical = cookie->position;
				}

				if (cookie->physical >= cookie->length)
					return 0;

				const unsigned long long remaining = cookie->length - cookie->physical;
				const unsigned long long want = (count < remaining) ? count : remaining;

				if (want == 0)
					return 0;

				const long long got = cookie->stream.read(
					cookie->stream.user, reinterpret_cast<unsigned char *>(buffer), want);

				if (got < 0)
				{
					cookie->failed = true;
					return -1;
				}

				cookie->physical += static_cast<unsigned long long>(got);
				cookie->position = cookie->physical;
				return got;
			}

			/* Seeking is lazy and never touches the caller's stream. It only
			   moves this cookie's idea of where the file pointer is; ReadCore
			   reconciles that with where the stream really is, and only then,
			   and only for a genuine jump, does the caller's seek callback get
			   called.

			   That is what makes a forward-only stream work. SendFilePartPacket
			   asks the position, seeks to the end to learn the size, seeks
			   straight back and reads - three seeks per file part that move
			   nothing at all. An LZ4 decoder cannot answer any of them, and
			   with this it never has to. */
			long long SeekCore(Cookie *cookie, long long offset, int whence)
			{
				if (cookie->failed)
					return -1;

				long long target;

				switch (whence)
				{
					case SEEK_SET:
						target = offset;
						break;

					case SEEK_CUR:
						target = static_cast<long long>(cookie->position) + offset;
						break;

					case SEEK_END:
						target = static_cast<long long>(cookie->length) + offset;
						break;

					default:
						return -1;
				}

				if (target < 0)
					return -1;

				cookie->position = static_cast<unsigned long long>(target);
				return target;
			}

			int CloseCore(void *raw)
			{
				Cookie *cookie = static_cast<Cookie *>(raw);
				lastStreamFailed = cookie->failed;
				delete cookie;
				return 0;
			}

#if defined(ODIN_HAVE_FUNOPEN)

			int FunopenRead(void *raw, char *buffer, int count)
			{
				if (count < 0)
					return -1;

				const long long got = ReadCore(static_cast<Cookie *>(raw), buffer,
				                               static_cast<unsigned long long>(count));
				return (got < 0) ? -1 : static_cast<int>(got);
			}

			fpos_t FunopenSeek(void *raw, fpos_t offset, int whence)
			{
				const long long result = SeekCore(static_cast<Cookie *>(raw),
				                                  static_cast<long long>(offset), whence);
				return static_cast<fpos_t>(result);
			}

#elif defined(ODIN_HAVE_FOPENCOOKIE)

			ssize_t FopencookieRead(void *raw, char *buffer, size_t count)
			{
				const long long got = ReadCore(static_cast<Cookie *>(raw), buffer,
				                               static_cast<unsigned long long>(count));
				return (got < 0) ? -1 : static_cast<ssize_t>(got);
			}

			int FopencookieSeek(void *raw, off64_t *offset, int whence)
			{
				const long long result = SeekCore(static_cast<Cookie *>(raw),
				                                  static_cast<long long>(*offset), whence);

				if (result < 0)
					return -1;

				*offset = static_cast<off64_t>(result);
				return 0;
			}

#endif
		}

		bool Available(void)
		{
#if defined(ODIN_STREAM_FILE_SUPPORTED)
			return true;
#else
			return false;
#endif
		}

		FILE *Open(const odin_stream *stream, unsigned long long length,
		           Observer *out_observer)
		{
			if (out_observer != nullptr)
				*out_observer = Observer();

			if (stream == nullptr || stream->read == nullptr)
				return nullptr;

#if !defined(ODIN_STREAM_FILE_SUPPORTED)
			(void)length;
			return nullptr;
#else
			Cookie *cookie = new Cookie();
			cookie->stream = *stream;
			cookie->length = length;
			cookie->physical = 0;
			cookie->position = 0;
			cookie->failed = false;

			FILE *file = nullptr;

#  if defined(ODIN_HAVE_FUNOPEN)
			file = funopen(cookie, &FunopenRead, nullptr, &FunopenSeek, &CloseCore);
#  else
			cookie_io_functions_t functions;
			functions.read = &FopencookieRead;
			functions.write = nullptr;
			functions.seek = &FopencookieSeek;
			functions.close = &CloseCore;

			file = fopencookie(cookie, "rb", functions);
#  endif

			if (file == nullptr)
			{
				delete cookie;
				return nullptr;
			}

			/* See the header: buffering would let stdio read ahead and then
			   throw the read-ahead away on the next seek, which would both
			   pull bytes the device never receives and desynchronise the
			   position this cookie tracks. */
			setvbuf(file, nullptr, _IONBF, 0);

			if (out_observer != nullptr)
			{
				out_observer->failed = &cookie->failed;
				out_observer->consumed = &cookie->physical;
			}

			return file;
#endif
		}

		int Close(FILE *file)
		{
			if (file == nullptr)
				return 0;

			lastStreamFailed = false;
			fclose(file);
			return lastStreamFailed ? 1 : 0;
		}
	}
}
