// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/BridgeManager.cpp  (the claim path at lines
//                                                242-270, which this file
//                                                diagnoses before it is run)
//   native/vendor/Heimdall/heimdall/60-heimdall.rules         (the udev rule form)
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
/// Answers "can this device be claimed, and if not, why" without opening
/// anything. Upstream prints "Claiming interface failed!"
/// (BridgeManager.cpp:263) and stops; this is the diagnostic that was missing.
/// </summary>
public static class UsbAccess
{
    /// <summary>
    /// The access state of a candidate device, plus a detail string the user can
    /// act on and the name of the bound kernel driver when there is one.
    /// </summary>
    public static (UsbAccessState State, string? Detail, string? Driver) Inspect(UsbDeviceRecord candidate)
    {
        if (UsbPlatform.IsMacOS)
            return InspectMac();

        if (UsbPlatform.IsLinux)
            return InspectLinux(candidate);

        return (UsbAccessState.Unknown,
            "EnneaOdin supports macOS and Linux only; this platform is not supported.", null);
    }

    /// <summary>
    /// True when a claim is worth attempting. A bound kernel driver is not a
    /// refusal on Linux: the claim path detaches it and retries, exactly as
    /// upstream does at BridgeManager.cpp:248-259. Permission denied is a
    /// refusal, because nothing the program can do at run time fixes it.
    /// </summary>
    public static bool CanAttemptClaim(UsbAccessState state) =>
        state is UsbAccessState.Claimable or UsbAccessState.DriverBound or UsbAccessState.Unknown;

    private static (UsbAccessState, string?, string?) InspectMac()
    {
        // On macOS there is no usbfs node to stat and no kernel driver to detach,
        // which is why upstream's detach code is inside #ifdef OS_LINUX. A
        // throwaway probe on this machine confirmed that libusb_open and
        // libusb_get_string_descriptor_ascii both succeed without root, so a
        // device that enumerated at all is treated as claimable and the real
        // answer comes from the claim itself.
        return (UsbAccessState.Claimable, null, null);
    }

    private static (UsbAccessState, string?, string?) InspectLinux(UsbDeviceRecord candidate)
    {
        string node = UsbPlatform.UsbfsNodePath(candidate.BusNumber, candidate.DeviceAddress);

        if (!File.Exists(node))
        {
            return (UsbAccessState.NotPresent,
                "The usbfs node " + node + " does not exist any more; the device was unplugged or re-enumerated.",
                null);
        }

        string? driver = FindBoundDriver(candidate);

        if (!CanOpenForReadWrite(node, out string? openError))
        {
            string detail =
                "The process may not open " + node + ": " + (openError ?? "permission denied") + ". " +
                LinuxRulesInstallHint();

            return (UsbAccessState.PermissionDenied, detail, driver);
        }

        if (driver is not null)
        {
            string detail =
                "The kernel driver \"" + driver + "\" currently holds the download interface on " + node +
                ". EnneaOdin detaches it when it claims the interface and re-attaches it on release, " +
                "which is what heimdall does on Linux.";

            return (UsbAccessState.DriverBound, detail, driver);
        }

        return (UsbAccessState.Claimable, null, null);
    }

    /// <summary>
    /// Whether the usbfs node can be opened read-write, which is what libusb
    /// needs. The file is opened and closed immediately; opening a usbfs node
    /// does not configure or reset the device.
    /// </summary>
    private static bool CanOpenForReadWrite(string node, out string? error)
    {
        try
        {
            using FileStream stream = new(node, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            error = null;
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The kernel driver bound to the device's download interface, or to any of
    /// its interfaces when the download interface is not known yet. Read from
    /// the sysfs "driver" symlink of the interface directory. Returns null when
    /// no driver is bound, and also when sysfs cannot be read, because "no
    /// driver" and "cannot tell" must not be reported as a bound driver.
    /// </summary>
    public static string? FindBoundDriver(UsbDeviceRecord candidate)
    {
        if (!UsbPlatform.IsLinux)
            return null;

        string? deviceDirectory = FindSysfsDeviceDirectory(candidate.BusNumber, candidate.DeviceAddress);
        if (deviceDirectory is null)
            return null;

        try
        {
            string prefix = Path.GetFileName(deviceDirectory) + ":";
            byte? wanted = candidate.Download?.InterfaceNumber;
            string? firstFound = null;

            foreach (string interfaceDirectory in Directory.EnumerateDirectories(deviceDirectory))
            {
                string name = Path.GetFileName(interfaceDirectory);
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string? driver = ReadDriverLink(interfaceDirectory);
                if (driver is null)
                    continue;

                firstFound ??= driver;

                if (wanted is null)
                    continue;

                // Interface directory names are "<device>:<config>.<interface>".
                int dot = name.LastIndexOf('.');
                if (dot < 0 || dot + 1 >= name.Length)
                    continue;

                if (byte.TryParse(name.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                        out byte interfaceNumber) && interfaceNumber == wanted.Value)
                {
                    return driver;
                }
            }

            return firstFound;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadDriverLink(string interfaceDirectory)
    {
        string link = Path.Combine(interfaceDirectory, "driver");

        try
        {
            var info = new DirectoryInfo(link);
            if (!info.Exists)
                return null;

            string? target = info.LinkTarget;
            string name = target is null ? info.Name : Path.GetFileName(target.TrimEnd('/'));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The /sys/bus/usb/devices entry whose busnum and devnum files match this
    /// device, or null when none does.
    /// </summary>
    private static string? FindSysfsDeviceDirectory(byte busNumber, byte deviceAddress)
    {
        const string root = "/sys/bus/usb/devices";

        try
        {
            if (!Directory.Exists(root))
                return null;

            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (ReadSysfsInt(directory, "busnum") is not int bus || bus != busNumber)
                    continue;

                if (ReadSysfsInt(directory, "devnum") is not int address || address != deviceAddress)
                    continue;

                return directory;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static int? ReadSysfsInt(string directory, string file)
    {
        try
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
                return null;

            string text = File.ReadAllText(path).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What to do about a permission failure on Linux: install the rules file
    /// this repository ships, reload udev, and re-plug the cable. It names the
    /// file and the mechanism, and deliberately does not tell anyone to run the
    /// program as root or to chmod the node.
    /// </summary>
    public static string LinuxRulesInstallHint() =>
        "Install the rules file shipped with EnneaOdin: " +
        "sudo cp 51-samsung-download.rules /etc/udev/rules.d/ && " +
        "sudo udevadm control --reload-rules && sudo udevadm trigger, then unplug and re-plug the cable. " +
        "The rule grants access through TAG+=\"uaccess\" to the user logged in at this seat, so no group " +
        "membership and no world-writable device node is needed.";

    /// <summary>
    /// What to say on macOS when the user reports a connected device and nothing
    /// matching is in the USB registry. An accessory that has not been approved
    /// is not enumerated at all, so there is nothing to probe and no permission
    /// to check.
    /// </summary>
    public static string MacAccessoryApprovalHint() =>
        "No matching device is in the USB registry. On macOS an accessory that has not been approved is " +
        "not enumerated at all, so check System Settings > Privacy and Security > \"Allow accessories to " +
        "connect\", and confirm the handset is in download mode and the cable carries data.";
}
