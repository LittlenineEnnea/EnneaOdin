// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using OdinCore.Native;

namespace OdinCore.Interop;

/// <summary>
/// The one DllImport resolver this assembly is allowed to have.
/// </summary>
internal static class NativeLibraryResolver
{
    private static int _registered;

    /// <summary>
    /// Registers the resolver if it is not registered yet. Every type that is
    /// about to P/Invoke calls this first: a resolver registered after the
    /// first bind of an imported function would not be consulted for it.
    /// </summary>
    internal static void EnsureRegistered()
    {
        // Interlocked rather than a static constructor, so that a failure to
        // register surfaces at the call site that needed it.
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(name, LibUsbLoader.LibraryName, StringComparison.Ordinal))
            return LibUsbLoader.Resolve(name, assembly, searchPath);

        if (string.Equals(name, OdinNative.Library, StringComparison.Ordinal))
            return OdinNativeLoader.Resolve(name, assembly, searchPath);

        // Zero means "I do not handle this name", and the runtime falls back to
        // its own search. It is not an error.
        return IntPtr.Zero;
    }
}
