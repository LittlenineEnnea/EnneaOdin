// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall. Heimdall links libusb at build time;
// this port resolves it at run time, which is a different problem and has no
// upstream counterpart. The two citations of BridgeManager.cpp below name the
// upstream calls whose behaviour is reproduced.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using OdinCore.Diagnostics;
using OdinCore.Usb;

namespace OdinCore.Interop;

/// <summary>
/// Resolves the libusb-1.0 shared library at run time. Every import in
/// <see cref="LibUsb"/> is declared against the logical name
/// <see cref="LibraryName"/>; this class registers the one resolver that turns
/// that name into a real file on macOS and Linux.
/// </summary>
internal static class LibUsbLoader
{
    internal const string LibraryName = "libusb-1.0";

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string[]? _attempted;

    /// <summary>The candidate that loaded, or null if nothing has loaded yet.</summary>
    internal static string? LoadedPath { get; private set; }

    /// <summary>
    /// Registers the assembly's one resolver. Any type that is about to call
    /// into <see cref="LibUsb"/> calls this first, because the resolver must be
    /// registered before the first P/Invoke on this assembly is bound.
    /// </summary>
    internal static void EnsureRegistered() => NativeLibraryResolver.EnsureRegistered();

    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        lock (Gate)
        {
            // The cache is not cosmetic: the resolver is invoked once per
            // imported function, so without it every import would re-walk the
            // whole candidate list.
            if (_handle != IntPtr.Zero)
                return _handle;

            string[] candidates = Candidates();
            _attempted = candidates;

            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr loaded))
                {
                    _handle = loaded;
                    LoadedPath = candidate;
                    return loaded;
                }
            }

            // Returning zero makes the call site throw DllNotFoundException.
            // LibUsbAvailability turns that into a status line with an install
            // command instead of letting it reach the user as a crash.
            return IntPtr.Zero;
        }
    }

    /// <summary>The candidate list for this platform, in the order it is tried.</summary>
    internal static string[] Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            var list = new List<string>
            {
                // Bare name first: picks up DYLD_LIBRARY_PATH and a dylib shipped
                // beside the application.
                "libusb-1.0.0.dylib"
            };

            // THE APPLICATION'S OWN COPY, before any package manager's.
            //
            // package-macos.sh bundles libusb into Contents/Frameworks, and
            // the native engine finds it there through its @loader_path/../Frameworks
            // rpath. This managed loader does NOT go through that rpath - it
            // is a separate resolution with its own rules - so without the two
            // paths below, an .app on a machine with no Homebrew would carry a
            // libusb the native half could load and the managed half could not.
            // That is precisely the shape of bug where a bundle "opens" and
            // then cannot see a handset.
            //
            // Both layouts are covered: Contents/MacOS/../Frameworks for the
            // bundle, and the base directory itself for a plain publish
            // directory somebody dropped a dylib into.
            string? baseDirectory = AppContext.BaseDirectory;

            if (!string.IsNullOrEmpty(baseDirectory))
            {
                list.Add(Path.Combine(baseDirectory, "libusb-1.0.0.dylib"));
                list.Add(Path.Combine(baseDirectory, "..", "Frameworks", "libusb-1.0.0.dylib"));
            }

            string? brewPrefix = Environment.GetEnvironmentVariable("HOMEBREW_PREFIX");
            if (!string.IsNullOrWhiteSpace(brewPrefix))
                list.Add(Path.Combine(brewPrefix, "lib", "libusb-1.0.0.dylib"));

            list.Add("/opt/homebrew/lib/libusb-1.0.0.dylib");             // arm64 Homebrew
            list.Add("/opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib");  // the dylib's own install name
            list.Add("/usr/local/lib/libusb-1.0.0.dylib");                // x64 Homebrew
            list.Add("/usr/local/opt/libusb/lib/libusb-1.0.0.dylib");
            list.Add("/opt/local/lib/libusb-1.0.0.dylib");                // MacPorts
            list.Add("libusb-1.0.dylib");                                 // unversioned, last resort
            return list.ToArray();

            // Deliberately no "brew --prefix" subprocess: an application started
            // from Finder has no Homebrew PATH, and spawning a process in the
            // detection path is exactly what this rewrite removes.
        }

        if (OperatingSystem.IsLinux())
        {
            return new[]
            {
                "libusb-1.0.so.0",                              // the SONAME; correct on every distro
                "libusb-1.0.so",                                // only with the -dev package
                "/usr/lib/x86_64-linux-gnu/libusb-1.0.so.0",
                "/usr/lib/aarch64-linux-gnu/libusb-1.0.so.0",
                "/usr/lib64/libusb-1.0.so.0",                   // Fedora, openSUSE
                "/usr/lib/libusb-1.0.so.0",                     // Arch
                "/lib/x86_64-linux-gnu/libusb-1.0.so.0"
            };
        }

        // Any other platform is unsupported. A Windows name is never attempted:
        // this program targets macOS and Linux only.
        return Array.Empty<string>();
    }

    /// <summary>The candidates the resolver actually tried, for the status line.</summary>
    internal static string[] Attempted()
    {
        lock (Gate)
        {
            return _attempted ?? Candidates();
        }
    }

    /// <summary>
    /// libusb_set_log_cb, bound softly: present since libusb 1.0.23, and absent
    /// on older runtimes. Returns null when the export does not exist, which is
    /// not an error.
    /// </summary>
    internal static LibUsb.libusb_set_log_cb_fn? TryGetSetLogCallback()
    {
        lock (Gate)
        {
            if (_handle == IntPtr.Zero)
                return null;

            if (!NativeLibrary.TryGetExport(_handle, "libusb_set_log_cb", out IntPtr address) || address == IntPtr.Zero)
                return null;

            return Marshal.GetDelegateForFunctionPointer<LibUsb.libusb_set_log_cb_fn>(address);
        }
    }
}

/// <summary>
/// One libusb context, with its log level set and (when the runtime supports it)
/// its log output routed into the application log rather than the process
/// stderr that a GUI swallows. Heimdall creates a context per BridgeManager, in
/// DetectDevice (BridgeManager.cpp:421) and in Initialise (:474); this port does
/// the same, one context per enumeration and one per open device.
/// </summary>
internal sealed class LibUsbContext : IDisposable
{
    // libusb holds the function pointer, so the delegate must stay reachable for
    // the process lifetime or it would be collected and the callback would jump
    // into freed memory.
    private static readonly LibUsb.libusb_log_cb LogCallback = OnLibUsbLog;
    private static readonly ConcurrentDictionary<IntPtr, IProgress<OdinEvent>> Sinks = new();

    private readonly IProgress<OdinEvent>? _log;
    private IntPtr _context;
    private bool _disposed;

    private LibUsbContext(IntPtr context, IProgress<OdinEvent>? log)
    {
        _context = context;
        _log = log;
    }

    internal IntPtr Handle => _context;

    /// <summary>
    /// libusb_init plus the log-level setup of BridgeManager.cpp:433 and :487.
    /// Throws <see cref="LibUsbException"/> when libusb_init fails and
    /// <see cref="LibUsbUnavailableException"/> when the library is not present.
    /// </summary>
    internal static LibUsbContext Create(int logLevel, IProgress<OdinEvent>? log)
    {
        LibUsbLoader.EnsureRegistered();

        IntPtr handle;
        int result;
        try
        {
            result = LibUsb.libusb_init(out handle);
        }
        catch (DllNotFoundException ex)
        {
            throw new LibUsbUnavailableException(LibUsbAvailability.Probe(), ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new LibUsbUnavailableException(LibUsbAvailability.Probe(), ex);
        }

        if (result != LibUsb.LIBUSB_SUCCESS)
            throw new LibUsbException("libusb_init", result);

        var context = new LibUsbContext(handle, log);
        context.SetLogLevel(logLevel);
        context.InstallLogCallback();
        return context;
    }

    /// <summary>
    /// The non-variadic equivalent of upstream's
    /// libusb_set_option(ctx, LIBUSB_OPTION_LOG_LEVEL, level)
    /// at BridgeManager.cpp:1249-1266. Values are the LIBUSB_LOG_LEVEL_* range
    /// 0 (none) to 4 (debug); anything outside it is clamped.
    /// </summary>
    internal void SetLogLevel(int level)
    {
        if (_context == IntPtr.Zero)
            return;

        int clamped = level < LibUsb.LIBUSB_LOG_LEVEL_NONE ? LibUsb.LIBUSB_LOG_LEVEL_NONE
            : level > LibUsb.LIBUSB_LOG_LEVEL_DEBUG ? LibUsb.LIBUSB_LOG_LEVEL_DEBUG
            : level;

        LibUsb.libusb_set_debug(_context, clamped);
    }

    private void InstallLogCallback()
    {
        if (_log is null || _context == IntPtr.Zero)
            return;

        LibUsb.libusb_set_log_cb_fn? setLogCb = LibUsbLoader.TryGetSetLogCallback();
        if (setLogCb is null)
            return;

        Sinks[_context] = _log;
        try
        {
            setLogCb(_context, LogCallback, LibUsb.LIBUSB_LOG_CB_CONTEXT);
        }
        catch (Exception ex) when (ex is MarshalDirectiveException or EntryPointNotFoundException)
        {
            Sinks.TryRemove(_context, out _);
        }
    }

    private static void OnLibUsbLog(IntPtr ctx, int level, IntPtr text)
    {
        if (!Sinks.TryGetValue(ctx, out IProgress<OdinEvent>? sink))
            return;

        string? message = Marshal.PtrToStringAnsi(text);
        if (string.IsNullOrWhiteSpace(message))
            return;

        OdinLevel mapped = level switch
        {
            LibUsb.LIBUSB_LOG_LEVEL_ERROR => OdinLevel.Error,
            LibUsb.LIBUSB_LOG_LEVEL_WARNING => OdinLevel.Warning,
            _ => OdinLevel.Debug
        };

        sink.Report(new OdinEvent(DateTimeOffset.Now, mapped, message.TrimEnd('\n', '\r'), "libusb"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        IntPtr context = _context;
        _context = IntPtr.Zero;

        if (context == IntPtr.Zero)
            return;

        Sinks.TryRemove(context, out _);

        try
        {
            LibUsb.libusb_set_log_cb_fn? setLogCb = LibUsbLoader.TryGetSetLogCallback();
            setLogCb?.Invoke(context, null, LibUsb.LIBUSB_LOG_CB_CONTEXT);
        }
        catch (Exception ex) when (ex is MarshalDirectiveException or EntryPointNotFoundException)
        {
            // An older libusb without libusb_set_log_cb never had a callback to
            // clear. Nothing to report and nothing to leak.
        }

        LibUsb.libusb_exit(context);   // BridgeManager.cpp:415
    }
}
