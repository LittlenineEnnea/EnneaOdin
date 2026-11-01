// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using OdinCore.Interop;

namespace OdinCore.Native;

/// <summary>
/// Thrown when the protocol engine cannot be used. It is a typed failure with
/// an actionable message, never a crash: the application must keep running and
/// keep showing the device list, the firmware panel and the log.
/// </summary>
public sealed class OdinEngineUnavailableException : Exception
{
    public OdinEngineUnavailableException(OdinEngineStatus status)
        : base(status.Summary)
    {
        Status = status;
    }

    public OdinEngineStatus Status { get; }
}

/// <summary>
/// Whether the native protocol engine is present and usable, and if not, what
/// to do about it. Shaped like <see cref="Usb.LibUsbStatus"/> on purpose: the
/// two are the application's two native prerequisites and they are reported
/// the same way.
/// </summary>
/// <param name="Available">True only if the library loaded AND its ABI matches.</param>
/// <param name="Path">The candidate that loaded, or null.</param>
/// <param name="AbiVersion">What the loaded library reports, or 0.</param>
/// <param name="HeimdallVersion">The version string of the linked C++, or null.</param>
/// <param name="BridgeVersion">The bridge's own version, or null.</param>
/// <param name="Error">One sentence naming what went wrong, or null.</param>
/// <param name="Attempted">Every candidate path the resolver tried.</param>
public sealed record OdinEngineStatus(bool Available, string? Path, int AbiVersion,
    string? HeimdallVersion, string? BridgeVersion, string? Error,
    IReadOnlyList<string> Attempted)
{
    /// <summary>One line for the log and the status area.</summary>
    public string Summary =>
        Available
            ? string.Format(CultureInfo.InvariantCulture,
                "Protocol engine: {0} (Heimdall {1}, bridge {2}, ABI {3})",
                Path, HeimdallVersion, BridgeVersion, AbiVersion)
            : "Protocol engine unavailable: " + (Error ?? "unknown reason") + " " + InstallHint;

    /// <summary>
    /// What the user has to do. The engine is built from this repository, so
    /// unlike libusb the answer is a build step, not a package manager.
    /// </summary>
    public static string InstallHint =>
        "Build it with ./native/build.sh (CMake 3.20 or newer, a C++ compiler and " +
        "libusb-1.0 development headers), then run ./build.sh, which copies the " +
        "library next to the application. Set ENNEAODIN_NATIVE_LIBRARY to an " +
        "absolute path to override the search.";
}

/// <summary>
/// Finds libenneaodin and reports, in one typed value, whether the protocol
/// engine can be used.
/// </summary>
internal static class OdinNativeLoader
{
    /// <summary>The override, documented in README.md.</summary>
    internal const string PathVariable = "ENNEAODIN_NATIVE_LIBRARY";

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string[]? _attempted;
    private static OdinEngineStatus? _status;

    internal static string? LoadedPath { get; private set; }

    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, OdinNative.Library, StringComparison.Ordinal))
            return IntPtr.Zero;

        lock (Gate)
        {
            // The resolver is invoked once per imported function, so without
            // this cache every import would re-walk the candidate list.
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

            // Zero makes the call site throw DllNotFoundException, which
            // Probe() turns into a status line rather than a crash.
            return IntPtr.Zero;
        }
    }

    /// <summary>The candidates the resolver tried, or would try.</summary>
    internal static IReadOnlyList<string> Attempted()
    {
        lock (Gate)
        {
            return _attempted ?? Candidates();
        }
    }

    /// <summary>
    /// Loads the library if it is not loaded, reads its identity and checks
    /// its ABI. Cached: the answer cannot change inside one process, because a
    /// native library that has been loaded is never unloaded here.
    /// </summary>
    internal static OdinEngineStatus Probe()
    {
        lock (Gate)
        {
            if (_status is not null)
                return _status;
        }

        OdinEngineStatus status;

        try
        {
            NativeLibraryResolver.EnsureRegistered();

            int abi = OdinNative.odin_abi_version();
            string? heimdall = Marshal.PtrToStringUTF8(OdinNative.odin_heimdall_version());
            string? bridge = Marshal.PtrToStringUTF8(OdinNative.odin_bridge_version());

            status = abi == OdinNative.ExpectedAbiVersion
                ? new OdinEngineStatus(true, LoadedPath, abi, heimdall, bridge, null, Attempted())
                : new OdinEngineStatus(false, LoadedPath, abi, heimdall, bridge,
                    string.Format(CultureInfo.InvariantCulture,
                        "the library at {0} reports ABI version {1}, but this build requires {2}; it is a stale copy.",
                        LoadedPath, abi, OdinNative.ExpectedAbiVersion),
                    Attempted());
        }
        catch (DllNotFoundException)
        {
            // Names the one candidate the user can act on - the copy that
            // belongs beside the application - and counts the rest. The whole
            // list is on Attempted for a diagnostic that wants it; nineteen
            // absolute paths in a log line would be unreadable.
            IReadOnlyList<string> tried = Attempted();

            status = new OdinEngineStatus(false, null, 0, null, null,
                string.Format(CultureInfo.InvariantCulture,
                    "lib{0} was not found beside the application ({1}), nor in {2} other location(s).",
                    OdinNative.Library,
                    tried.Count > 0 ? tried[0] : "no candidate",
                    tried.Count > 0 ? tried.Count - 1 : 0),
                tried);
        }
        catch (EntryPointNotFoundException missing)
        {
            status = new OdinEngineStatus(false, LoadedPath, 0, null, null,
                string.Format(CultureInfo.InvariantCulture,
                    "the library at {0} is missing an entry point ({1}); it is a stale copy.",
                    LoadedPath, missing.Message),
                Attempted());
        }
        catch (BadImageFormatException wrongArch)
        {
            status = new OdinEngineStatus(false, LoadedPath, 0, null, null,
                string.Format(CultureInfo.InvariantCulture,
                    "the library at {0} could not be loaded: {1}. It was probably built for a different CPU architecture.",
                    LoadedPath, wrongArch.Message),
                Attempted());
        }

        lock (Gate)
        {
            _status ??= status;
            return _status;
        }
    }

    private static string[] Candidates()
    {
        string file = OperatingSystem.IsMacOS() ? "libenneaodin.dylib" : "libenneaodin.so";

        string? explicitPath = Environment.GetEnvironmentVariable(PathVariable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // An explicit override is used alone. Falling back after it would
            // hide a typo in it and load something the user did not ask for.
            return new[] { explicitPath };
        }

        var list = new List<string>();

        string? assemblyDirectory = AssemblyDirectory();
        string baseDirectory = AppContext.BaseDirectory;

        foreach (string? directory in new[] { assemblyDirectory, baseDirectory })
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            Add(list, Path.Combine(directory, file));
            Add(list, Path.Combine(directory, "..", "Frameworks", file));
        }

        // The build tree, for "dotnet run" in a checkout. Six levels covers
        // bin/<config>/<tfx>/<rid>/ and the publish directory under dist/.
        string? walk = assemblyDirectory ?? baseDirectory;

        for (int i = 0; i < 6 && !string.IsNullOrEmpty(walk); i++)
        {
            Add(list, Path.Combine(walk, "native", "build", file));
            Add(list, Path.Combine(walk, "native", "build-debug", file));
            walk = Path.GetDirectoryName(walk);
        }

        // The platform's own search, then the usual install prefixes.
        Add(list, file);
        Add(list, Path.Combine("/usr/local/lib", file));

        if (OperatingSystem.IsMacOS())
        {
            Add(list, Path.Combine("/opt/homebrew/lib", file));
            Add(list, Path.Combine("/opt/local/lib", file));
        }

        Add(list, Path.Combine("/usr/lib", file));

        return list.ToArray();
    }

    private static void Add(List<string> list, string candidate)
    {
        string normalised = candidate.Contains("..", StringComparison.Ordinal)
            ? Path.GetFullPath(candidate)
            : candidate;

        if (!list.Contains(normalised, StringComparer.Ordinal))
            list.Add(normalised);
    }

    private static string? AssemblyDirectory()
    {
        try
        {
            string location = typeof(OdinNativeLoader).Assembly.Location;

            // Location is empty for a single-file publish; BaseDirectory then
            // carries the answer instead.
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
