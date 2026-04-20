using System.Runtime.InteropServices;
using OdinCore.Models;

namespace OdinCore.Usb;

/// <summary>
/// Finds Samsung devices in download mode on macOS and Linux.
///
/// macOS  → system_profiler SPUSBDataType  (VID 04E8, download-mode PIDs)
/// Linux  → /sys/bus/usb/devices + /sys/class/tty
///
/// The device appears as a CDC-ACM serial port in both OSes when the
/// official driver (or cdc-acm kernel module) is loaded.
/// On macOS without the Samsung driver the device may also appear as a
/// generic usbmodem device.
/// </summary>
public class DeviceScanner
{
    // ── Known Samsung download-mode USB IDs ──────────────────────────────
    private static readonly HashSet<string> SamsungVIDs =
        new(StringComparer.OrdinalIgnoreCase) { "04e8" };

    // PIDs confirmed for download mode (Odin protocol)
    private static readonly HashSet<string> DownloadPIDs =
        new(StringComparer.OrdinalIgnoreCase)
        { "6601", "685d", "6860", "6877", "d001" };

    public event Action<string, MsgType>? Log;
    private void L(string m, MsgType t = MsgType.Info) => Log?.Invoke(m, t);

    // ── Public API ────────────────────────────────────────────────────────

    public async Task<List<OdinDevice>> ScanAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await ScanMacOSAsync();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return await ScanLinuxAsync();

        L("Device scanning not supported on this OS.", MsgType.Warning);
        return new();
    }

    /// <summary>
    /// Return all serial port names the OS currently exposes
    /// (used to populate the manual port combo-box).
    /// </summary>
    public static List<string> AllPorts()
    {
        try
        {
            var ports = new List<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // cu.* are caller-oriented (no echo), preferred for writing
                ports.AddRange(Directory.GetFiles("/dev", "cu.*")
                    .Where(p => p.Contains("usbmodem") || p.Contains("usbserial")));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                ports.AddRange(Directory.GetFiles("/dev", "ttyACM*"));
                ports.AddRange(Directory.GetFiles("/dev", "ttyUSB*"));
            }

            ports.Sort();
            return ports;
        }
        catch { return new(); }
    }

    // ── macOS implementation ──────────────────────────────────────────────

    private async Task<List<OdinDevice>> ScanMacOSAsync()
    {
        var devices = new List<OdinDevice>();
        try
        {
            var output = await RunAsync("system_profiler", "SPUSBDataType -xml");
            // Fall back to plain text if xml parse fails
            if (string.IsNullOrWhiteSpace(output))
                output = await RunAsync("system_profiler", "SPUSBDataType");

            devices.AddRange(ParseSysProfilerText(
                await RunAsync("system_profiler", "SPUSBDataType")));
        }
        catch (Exception ex) { L($"macOS scan error: {ex.Message}", MsgType.Error); }

        // If profiler found nothing, list all usbmodem ports as candidates
        if (devices.Count == 0)
        {
            foreach (var p in AllPorts())
                devices.Add(new OdinDevice
                {
                    Name = $"Possible Samsung ({Path.GetFileName(p)})",
                    Port = p, VID = "04E8", PID = "????", IsConnected = true
                });
        }

        return devices;
    }

    private static List<OdinDevice> ParseSysProfilerText(string text)
    {
        var devices = new List<OdinDevice>();
        if (string.IsNullOrEmpty(text)) return devices;

        var lines   = text.Split('\n');
        string vid  = "", pid = "", name = "";
        bool inDev  = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();

            if (l.StartsWith("Product ID:"))
                pid = l.Replace("Product ID:", "").Trim().TrimStart('0', 'x').ToLower();
            else if (l.StartsWith("Vendor ID:"))
            {
                // "Vendor ID: 0x04e8 (Samsung Electronics Co., Ltd.)"
                var raw = l.Replace("Vendor ID:", "").Trim().Split(' ')[0];
                vid = raw.TrimStart('0', 'x').ToLower();
                inDev = SamsungVIDs.Contains(vid) && DownloadPIDs.Contains(pid);
            }
            else if (l.StartsWith("Product Name:") || l.StartsWith("USB Modem:"))
                name = l.Split(':')[1].Trim();
            else if (inDev && l.StartsWith("BSD Name:"))
            {
                var bsd  = l.Replace("BSD Name:", "").Trim();
                var port = bsd.StartsWith("/dev/") ? bsd
                         : File.Exists($"/dev/cu.{bsd}") ? $"/dev/cu.{bsd}"
                         : $"/dev/{bsd}";
                devices.Add(new OdinDevice
                {
                    Name        = string.IsNullOrEmpty(name) ? "Samsung Download Mode" : name,
                    Port        = port,
                    VID         = vid.ToUpper(),
                    PID         = pid.ToUpper(),
                    IsConnected = true
                });
                inDev = false;
            }
        }
        return devices;
    }

    // ── Linux implementation ──────────────────────────────────────────────

    private async Task<List<OdinDevice>> ScanLinuxAsync()
    {
        var devices = new List<OdinDevice>();
        const string sysBus = "/sys/bus/usb/devices";
        if (!Directory.Exists(sysBus)) { devices.AddRange(FallbackLinuxPorts()); return devices; }

        foreach (var dir in Directory.GetDirectories(sysBus))
        {
            try
            {
                var vidFile = Path.Combine(dir, "idVendor");
                var pidFile = Path.Combine(dir, "idProduct");
                if (!File.Exists(vidFile) || !File.Exists(pidFile)) continue;

                var vid = (await File.ReadAllTextAsync(vidFile)).Trim().ToLower();
                var pid = (await File.ReadAllTextAsync(pidFile)).Trim().ToLower();
                if (!SamsungVIDs.Contains(vid) || !DownloadPIDs.Contains(pid)) continue;

                string product = "Samsung Download Mode";
                var pf = Path.Combine(dir, "product");
                if (File.Exists(pf)) product = (await File.ReadAllTextAsync(pf)).Trim();

                var tty = FindLinuxTty(dir);
                if (tty != null)
                    devices.Add(new OdinDevice
                    {
                        Name = product, Port = $"/dev/{tty}",
                        VID = vid.ToUpper(), PID = pid.ToUpper(), IsConnected = true
                    });
            }
            catch { }
        }

        if (devices.Count == 0) devices.AddRange(FallbackLinuxPorts());
        return devices;
    }

    private static string? FindLinuxTty(string deviceDir)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(deviceDir, "*", SearchOption.AllDirectories))
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith("ttyACM") || n.StartsWith("ttyUSB")) return n;
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<OdinDevice> FallbackLinuxPorts()
    {
        foreach (var p in AllPorts())
            yield return new OdinDevice
            {
                Name = $"Possible Samsung ({Path.GetFileName(p)})",
                Port = p, VID = "04E8", PID = "????", IsConnected = true
            };
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static async Task<string> RunAsync(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout  = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return stdout;
        }
        catch { return ""; }
    }
}
