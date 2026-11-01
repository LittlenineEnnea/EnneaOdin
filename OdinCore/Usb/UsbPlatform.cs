// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (IsUbuntu, lines 1271-1298)
//   native/vendor/Heimdall/heimdall/source/BridgeManager.h    (the OS_LINUX guards)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace OdinCore.Usb;

/// <summary>
/// Platform questions the USB layer has to ask: which OS this is, whether the
/// Ubuntu reset quirk applies, how a user installs libusb here, and where the
/// Linux usbfs node for a (bus, address) pair lives.
/// </summary>
public static class UsbPlatform
{
    /// <summary>True on macOS. There is no kernel-driver detach path here, exactly as upstream.</summary>
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>
    /// True on Linux. Gates every call that upstream guards with #ifdef OS_LINUX:
    /// detach (BridgeManager.cpp:248-259), re-attach (:301-309) and the reset
    /// quirk (:325-334).
    /// </summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>True on a platform this program does not support.</summary>
    public static bool IsSupported => IsMacOS || IsLinux;

    /// <summary>A short platform word for log lines and diagnostics.</summary>
    public static string Name => IsMacOS ? "macOS" : IsLinux ? "Linux" : "unsupported platform";

    /// <summary>
    /// Port of BridgeManager::IsUbuntu (BridgeManager.cpp:1271-1298). Reads
    /// /etc/os-release, finds the first line whose text before the first '=' is
    /// exactly "ID", and compares the raw remainder of that line to "ubuntu".
    /// </summary>
    public static bool IsUbuntu()
    {
        if (!IsLinux)
            return false;

        const string osRelease = "/etc/os-release";

        try
        {
            if (!File.Exists(osRelease))
                return false;

            foreach (string line in File.ReadLines(osRelease))
            {
                int pos = line.IndexOf('=');
                if (pos < 0)
                    continue;

                string entry = line.Substring(0, pos);
                if (!string.Equals(entry, "ID", StringComparison.Ordinal))
                    continue;

                string os = line.Substring(pos + 1);
                return string.Equals(os, "ubuntu", StringComparison.Ordinal);
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// The command that installs the libusb-1.0 runtime on this platform. This
    /// is EnneaOdin's own text, not a port: heimdall is built against libusb and
    /// never tells the user how to install it.
    /// </summary>
    public static string LibUsbInstallCommand()
    {
        if (IsMacOS)
            return "brew install libusb";

        if (!IsLinux)
            return "this platform is not supported; EnneaOdin runs on macOS and Linux";

        IReadOnlyList<string> ids = LinuxDistroIds();

        if (ContainsAny(ids, "debian", "ubuntu", "linuxmint", "pop", "raspbian"))
            return "sudo apt install libusb-1.0-0";

        if (ContainsAny(ids, "fedora", "rhel", "centos", "rocky", "almalinux"))
            return "sudo dnf install libusb1";

        if (ContainsAny(ids, "arch", "archlinux", "manjaro", "endeavouros"))
            return "sudo pacman -S libusb";

        if (ContainsAny(ids, "opensuse", "suse", "sles"))
            return "sudo zypper install libusb-1_0-0";

        return "install the libusb-1.0 runtime package for your distribution";
    }

    /// <summary>
    /// ID and ID_LIKE from /etc/os-release, lower-cased with the invariant
    /// culture and with surrounding quotes removed. Used only to choose an
    /// install command; unlike <see cref="IsUbuntu"/> this is not a port and so
    /// it is allowed to be tolerant about quoting.
    /// </summary>
    public static IReadOnlyList<string> LinuxDistroIds()
    {
        var ids = new List<string>();

        if (!IsLinux)
            return ids;

        try
        {
            const string osRelease = "/etc/os-release";
            if (!File.Exists(osRelease))
                return ids;

            foreach (string line in File.ReadLines(osRelease))
            {
                int pos = line.IndexOf('=');
                if (pos < 0)
                    continue;

                string key = line.Substring(0, pos);
                if (!string.Equals(key, "ID", StringComparison.Ordinal) &&
                    !string.Equals(key, "ID_LIKE", StringComparison.Ordinal))
                    continue;

                string value = line.Substring(pos + 1).Trim().Trim('"', '\'');

                foreach (string part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    ids.Add(part.ToLowerInvariant());
            }
        }
        catch (IOException)
        {
            return ids;
        }
        catch (UnauthorizedAccessException)
        {
            return ids;
        }

        return ids;
    }

    private static bool ContainsAny(IReadOnlyList<string> ids, params string[] wanted)
    {
        foreach (string id in ids)
            foreach (string w in wanted)
                if (string.Equals(id, w, StringComparison.Ordinal))
                    return true;

        return false;
    }

    /// <summary>
    /// The Linux usbfs node libusb opens for a device: /dev/bus/usb/BBB/DDD with
    /// both numbers zero-padded to three digits. This is the file whose
    /// permissions decide whether a claim can succeed, and the reason the udev
    /// rule in the repository root exists.
    /// </summary>
    public static string UsbfsNodePath(byte busNumber, byte deviceAddress) =>
        string.Format(CultureInfo.InvariantCulture, "/dev/bus/usb/{0:D3}/{1:D3}", busNumber, deviceAddress);
}
