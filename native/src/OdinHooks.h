/* OdinHooks.h - what Heimdall's loops need from EnneaOdin.
 *
 * Copyright (c) 2025 EnneaOdin contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Heimdall's loops take no cancellation token and report progress by printing
 * over themselves with backspaces. Free functions rather than a token threaded
 * through every BridgeManager signature.
 *
 * The context is per thread, so two sessions on two threads do not see each
 * other's flag. With none installed every hook is a no-op, which is what keeps
 * the vendored code correct outside this library.
 */

#ifndef ODIN_HOOKS_H
#define ODIN_HOOKS_H

namespace EnneaOdin
{
	namespace Hooks
	{
		/* Implemented by the ABI's per-call Operation object. */
		class Context
		{
			public:

				virtual ~Context();

				/* True when the caller has asked for the operation to stop.
				   Called between packets, never inside one. */
				virtual bool OnCancelCheck() = 0;

				/* total is 0 when the total is not known. */
				virtual void OnProgress(unsigned long long transferred,
				                        unsigned long long total) = 0;
		};

		/* Installs (or, with a null argument, clears) the context for the
		   calling thread. Returns the context that was installed before, so a
		   caller can restore it. */
		Context *Install(Context *context);

		/* The calling thread's context, or null. */
		Context *Current(void);

		/* What the vendored loops call. All four are safe with no context
		   installed and none of them allocates. */
		bool Cancelled(void);
		void Progress(unsigned long long transferred, unsigned long long total);

		/* True when a host with its own interface is driving this library.
		   Used to skip BeginSession's three-second "be patient" pause, which
		   is right for a terminal and is three seconds per session for a GUI
		   that already printed the line. No context means a terminal, so the
		   pause stays. */
		bool Attached(void);

		/* A sleep that can be given up on. The bulk-transfer retry loops back
		   off 250 to 1250 ms with no cancellation check; with five 3000 ms
		   libusb timeouts that is 21,750 ms per packet in which Stop does
		   nothing. This slices at 50 ms and returns false on cancellation.
		   With no context it sleeps the lot and returns true. */
		bool Nap(unsigned long milliseconds);
	}
}

#endif
