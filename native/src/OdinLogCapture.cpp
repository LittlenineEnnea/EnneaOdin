/* OdinLogCapture.cpp
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "OdinLogCapture.h"

#include <cerrno>
#include <cstdio>
#include <unistd.h>

namespace EnneaOdin
{
	namespace
	{
		/* One capture at a time, process-wide, because file descriptors 1 and
		   2 are process-wide. Tried, never waited on. */
		std::mutex &CaptureLock(void)
		{
			static std::mutex lock;
			return lock;
		}

		bool TryTakeCapture(void)
		{
			return CaptureLock().try_lock();
		}

		void ReleaseCapture(void)
		{
			CaptureLock().unlock();
		}

		/* A single line this long is already pathological; flush it rather
		   than let a runaway printf grow the buffer without bound. */
		const size_t kMaxLineLength = 4096;
	}

	LogCapture::LogCapture()
		: active(false),
		  finished(false),
		  ownsCapture(false),
		  savedOut(-1),
		  savedErr(-1),
		  readFd(-1),
		  writeFd(-1)
	{
		if (!TryTakeCapture())
			return;

		ownsCapture = true;

		int fds[2];

		if (pipe(fds) != 0)
			return;

		readFd = fds[0];
		writeFd = fds[1];

		fflush(stdout);
		fflush(stderr);

		savedOut = dup(STDOUT_FILENO);
		savedErr = dup(STDERR_FILENO);

		if (savedOut < 0 || savedErr < 0)
			return;

		if (dup2(writeFd, STDOUT_FILENO) < 0 || dup2(writeFd, STDERR_FILENO) < 0)
			return;

		active = true;
		reader = std::thread(&LogCapture::ReaderLoop, this);
	}

	LogCapture::~LogCapture()
	{
		Finish();

		if (readFd >= 0)
		{
			close(readFd);
			readFd = -1;
		}

		if (ownsCapture)
		{
			ownsCapture = false;
			ReleaseCapture();
		}
	}

	void LogCapture::ReaderLoop(void)
	{
		char buffer[4096];

		for (;;)
		{
			ssize_t count = read(readFd, buffer, sizeof buffer);

			if (count > 0)
			{
				std::lock_guard<std::mutex> guard(bufferMutex);
				raw.append(buffer, static_cast<size_t>(count));
				continue;
			}

			if (count == 0)
				return; /* every write end is closed */

			if (errno == EINTR)
				continue;

			return;
		}
	}

	void LogCapture::Finish(void)
	{
		if (finished)
			return;

		finished = true;

		if (!active)
		{
			/* Nothing was redirected. Still undo whatever did get set up. */
			if (savedOut >= 0) { close(savedOut); savedOut = -1; }
			if (savedErr >= 0) { close(savedErr); savedErr = -1; }
			if (writeFd >= 0) { close(writeFd); writeFd = -1; }
			return;
		}

		fflush(stdout);
		fflush(stderr);

		/* Put the real descriptors back first. Each dup2 closes this process's
		   copy of the pipe's write end, and closing writeFd below drops the
		   last one, which is what gives the reader thread its EOF. Doing it in
		   any other order hangs the join. */
		dup2(savedOut, STDOUT_FILENO);
		dup2(savedErr, STDERR_FILENO);
		close(savedOut);
		close(savedErr);
		savedOut = -1;
		savedErr = -1;

		close(writeFd);
		writeFd = -1;

		if (reader.joinable())
			reader.join();

		active = false;
	}

	void LogCapture::Drain(std::vector<std::string> &lines)
	{
		for (size_t i = 0; i < raw.size(); i++)
		{
			const char c = raw[i];

			if (c == '\n')
			{
				while (!current.empty() && (current[current.size() - 1] == ' ' ||
				                            current[current.size() - 1] == '\t'))
					current.erase(current.size() - 1);

				if (!current.empty())
					lines.push_back(current);

				current.clear();
			}
			else if (c == '\r')
			{
				/* A carriage return without a newline rewrites the line in
				   place; the final text is what matters. */
				current.clear();
			}
			else if (c == '\b')
			{
				if (!current.empty())
					current.erase(current.size() - 1);
			}
			else if (c == '\t' || (c >= 0x20 && c != 0x7F) ||
			         static_cast<unsigned char>(c) >= 0x80)
			{
				current.push_back(c);

				if (current.size() >= kMaxLineLength)
				{
					lines.push_back(current);
					current.clear();
				}
			}
			/* Any other control byte is dropped rather than passed to a UI. */
		}

		raw.clear();
	}

	void LogCapture::Take(std::vector<std::string> &lines)
	{
		std::lock_guard<std::mutex> guard(bufferMutex);

		Drain(lines);

		/* After Finish() there is no more input, so a partial last line will
		   never be completed by a newline. Deliver it. */
		if (finished && !current.empty())
		{
			lines.push_back(current);
			current.clear();
		}
	}
}
