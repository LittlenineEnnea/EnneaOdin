/* OdinLogCapture.h - captures what Heimdall prints, without editing Heimdall.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Heimdall reports everything through Interface::Print and friends, which
 * vfprintf to stdout or stderr and fflush after every line
 * (Interface.cpp:84-85). A GUI has no terminal to read, so for the duration of
 * one ABI call this class puts a pipe on file descriptors 1 and 2 and reads it.
 *
 * Why a pipe and not a callback inside Interface.cpp: the pipe leaves the
 * vendored C++ unmodified, and every Action, every warning path and every
 * libusb-level message upstream might add in future is captured without
 * anyone having to remember to route it. The cost is that the redirect is
 * process-wide, which is handled as follows.
 *
 *   - Only one capture is active at a time. The second one to start does not
 *     block: it simply does not capture, says so, and the operation runs
 *     normally with its output going to whatever is on fd 1 at the time. A
 *     blocking design would have stalled a device-detect poll behind a
 *     twenty-minute flash.
 *   - Anything else in the process that writes to stdout or stderr during a
 *     call is captured as if Heimdall had written it.
 *
 * A reader thread drains the pipe continuously. That is not decoration: a pipe
 * holds about 64 KiB, and a writer that fills it blocks forever if the only
 * code that could drain it is the thread doing the writing. Draining only at
 * the end of a call - which is what a first cut of this did - is a deadlock
 * waiting for a verbose multi-gigabyte flash.
 *
 * Lines are handed over whole and clean. Heimdall draws progress by printing
 * "0%" and then backspacing over it, so the raw byte stream is full of \b and
 * \r; those are applied to the line being built rather than passed on, and an
 * empty line is dropped.
 */

#ifndef ODIN_LOG_CAPTURE_H
#define ODIN_LOG_CAPTURE_H

#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace EnneaOdin
{
	class LogCapture
	{
		public:

			LogCapture();
			~LogCapture();

			/* False when this instance is not capturing, either because
			   another one already is or because the pipe could not be set up.
			   The operation should still run. */
			bool Active(void) const
			{
				return active;
			}

			/* Appends every complete line captured so far and clears them from
			   the buffer. Safe to call repeatedly during an operation, and
			   that is the intent: it is how a long transfer logs as it goes. */
			void Take(std::vector<std::string> &lines);

			/* Restores the file descriptors, stops the reader thread and makes
			   the last partial line, if any, available to Take. Idempotent.
			   Called by the destructor; call it explicitly when the remaining
			   lines are still wanted. */
			void Finish(void);

		private:

			LogCapture(const LogCapture &);
			LogCapture &operator=(const LogCapture &);

			void ReaderLoop(void);
			void Drain(std::vector<std::string> &lines);

			bool        active;
			bool        finished;
			bool        ownsCapture;
			int         savedOut;
			int         savedErr;
			int         readFd;
			int         writeFd;
			std::thread reader;
			std::mutex  bufferMutex;
			std::string raw;      /* bytes read but not yet turned into lines */
			std::string current;  /* the line being built */
	};
}

#endif
