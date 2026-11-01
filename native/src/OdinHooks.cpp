/* OdinHooks.cpp - per-thread cancellation and progress context.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "OdinHooks.h"

/* C++ Standard Library */
#include <chrono>
#include <thread>

namespace EnneaOdin
{
	namespace Hooks
	{
		namespace
		{
			/* thread_local, not a global: an operation belongs to the thread
			   that started it, and nothing here is shared between threads. That
			   also means no lock is needed on the hot path - Cancelled() is
			   called once per protocol file part. */
			thread_local Context *currentContext = nullptr;
		}

		Context::~Context()
		{
		}

		Context *Install(Context *context)
		{
			Context *previous = currentContext;
			currentContext = context;
			return previous;
		}

		Context *Current(void)
		{
			return currentContext;
		}

		bool Cancelled(void)
		{
			return currentContext != nullptr && currentContext->OnCancelCheck();
		}

		void Progress(unsigned long long transferred, unsigned long long total)
		{
			if (currentContext != nullptr)
				currentContext->OnProgress(transferred, total);
		}

		bool Attached(void)
		{
			return currentContext != nullptr;
		}

		bool Nap(unsigned long milliseconds)
		{
			if (currentContext == nullptr)
			{
				/* A terminal caller. Sleep the whole interval, exactly as the
				   vendored code did before this existed. */
				if (milliseconds > 0)
					std::this_thread::sleep_for(std::chrono::milliseconds(milliseconds));

				return true;
			}

			/* 50 ms. Small enough that Stop feels immediate against a 250 to
			   1250 ms backoff, large enough that the slicing itself costs
			   nothing: the longest backoff is 25 checks. */
			const unsigned long slice = 50;
			unsigned long remaining = milliseconds;

			while (remaining > 0)
			{
				if (currentContext->OnCancelCheck())
					return false;

				const unsigned long step = (remaining < slice) ? remaining : slice;
				std::this_thread::sleep_for(std::chrono::milliseconds(step));
				remaining -= step;
			}

			return !currentContext->OnCancelCheck();
		}
	}
}
