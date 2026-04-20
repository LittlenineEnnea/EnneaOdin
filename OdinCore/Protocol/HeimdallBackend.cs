using System.Diagnostics;
using System.Text.RegularExpressions;
using OdinCore.Models;
using OdinCore.Tar;

namespace OdinCore.Protocol;

/// <summary>
/// Thin wrapper around the <c>heimdall</c> CLI.
///
/// heimdall is the only mature open-source tool that correctly implements
/// the Samsung download-mode USB protocol (raw USB bulk transfers via libusb)
/// on macOS and Linux.  It must be installed separately:
///
///   macOS  → brew install heimdall
///   Linux  → sudo apt install heimdall-flash  (or build from source)
///
/// This class builds the correct heimdall command line from the GUI state,
/// runs it as a child process, and streams its stdout/stderr back to the
/// GUI as LogEntry / FlashProgress events.
/// </summary>
public class HeimdallBackend
{
    // ── Events ─────────────────────────────────────────────────────────────
    public event Action<string, MsgType>? Log;
    public event Action<FlashProgress>?  Progress;

    private void L(string msg, MsgType t = MsgType.Normal) =>
        Log?.Invoke(msg, t);

    // ── Cancellation ────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private Process?                 _proc;

    // ── Heimdall detection ──────────────────────────────────────────────────

    /// <summary>Locate the heimdall binary; returns null if not found.</summary>
    public static string? FindHeimdall()
    {
        // Common installation paths
        var candidates = new[]
        {
            "/opt/homebrew/bin/heimdall",     // macOS arm64 Homebrew
            "/usr/local/bin/heimdall",        // macOS x86 Homebrew / Linux manual
            "/usr/bin/heimdall",              // Debian/Ubuntu package
            "/usr/bin/heimdall-flash",        // some distros rename it
            "heimdall",                        // $PATH fallback
        };

        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
                if (p?.ExitCode == 0 || p?.ExitCode == 1) return c;
            }
            catch { }
        }
        return null;
    }

    public bool IsAvailable => FindHeimdall() != null;

    // ── Detect / Device Info ────────────────────────────────────────────────

    public async Task<bool> DetectDeviceAsync()
    {
        L("Detecting Samsung device...", MsgType.Info);
        var (ok, _) = await RunHeimdallAsync("detect");
        if (ok) L("Device found in download mode ✓", MsgType.Success);
        else    L("No device found. Connect your phone in download mode.", MsgType.Warning);
        return ok;
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync()
    {
        L("Reading device information...", MsgType.Info);
        var (ok, stdout) = await RunHeimdallAsync("detect --verbose");
        if (!ok) return null;

        var info = new DeviceInfo();
        foreach (var line in stdout.Split('\n'))
        {
            var l = line.Trim();
            if      (l.StartsWith("Model:"))    info.Model      = After(l, ":");
            else if (l.StartsWith("Product:"))  info.Product    = After(l, ":");
            else if (l.StartsWith("Manufacturer:")) info.Vendor = After(l, ":");
            else if (l.StartsWith("Protocol:")) info.Protocol   = After(l, ":");
        }
        return info;
    }

    // ── PIT ─────────────────────────────────────────────────────────────────

    public async Task<(bool ok, string pitPath)> DownloadPitAsync(string saveDir)
    {
        Directory.CreateDirectory(saveDir);
        var outFile = Path.Combine(saveDir, "device.pit");
        L($"Downloading PIT → {outFile}", MsgType.Info);

        var (ok, _) = await RunHeimdallAsync($"download-pit --output \"{outFile}\"");
        if (ok) L("PIT downloaded ✓", MsgType.Success);
        else    L("Failed to download PIT", MsgType.Error);

        return (ok, outFile);
    }

    public async Task<List<PitEntry>> ReadPitAsync(string pitFilePath)
    {
        // heimdall print-pit --file <path>
        var (ok, stdout) = await RunHeimdallAsync($"print-pit --file \"{pitFilePath}\"");
        return ok ? ParsePitOutput(stdout) : new();
    }

    // ── Flash ────────────────────────────────────────────────────────────────

    public async Task<bool> FlashAsync(FlashSlots slots, FlashOptions opts,
                                        CancellationToken ct = default)
    {
        var heimdall = FindHeimdall();
        if (heimdall == null)
        {
            L("heimdall not found! Install it first (brew install heimdall).", MsgType.Error);
            return false;
        }

        // ── Build argument list ──────────────────────────────────────────────
        // heimdall flash [options] --PARTITION file [--PARTITION file ...]
        // Partition names are read from the device's PIT table.
        // We need to map each slot to the correct partition name.

        var args = new System.Text.StringBuilder("flash");

        if (!opts.AutoReboot)    args.Append(" --no-reboot");
        if (opts.Repartition)    args.Append(" --repartition");
        if (opts.BlankFlash)     args.Append(" --Blank-Flash");
        if (opts.ResetFlashCount)args.Append(" --reset-flash-count");

        // Always request verbose so we can parse progress
        args.Append(" --verbose");

        // Map slots to heimdall partition flags
        // heimdall uses the partition NAME from the device's own PIT table.
        // These names are standardised across Samsung devices.
        bool hasFile = false;

        if (!string.IsNullOrEmpty(slots.BL))
        {
            hasFile = true;
            AppendSlotArgs(args, slots.BL, "BL");   // BOOTLOADER / BL
        }
        if (!string.IsNullOrEmpty(slots.AP))
        {
            hasFile = true;
            AppendSlotArgs(args, slots.AP, "AP");   // system / USERDATA / more
        }
        if (!string.IsNullOrEmpty(slots.CP))
        {
            hasFile = true;
            AppendSlotArgs(args, slots.CP, "CP");   // modem
        }
        if (!string.IsNullOrEmpty(slots.CSC))
        {
            hasFile = true;
            AppendSlotArgs(args, slots.CSC, opts.EfsClear ? "HOME_CSC" : "CSC");
        }
        if (!string.IsNullOrEmpty(slots.UserData))
        {
            hasFile = true;
            AppendSlotArgs(args, slots.UserData, "USERDATA");
        }

        if (!hasFile)
        {
            L("No firmware files selected.", MsgType.Warning);
            return false;
        }

        L($"Running: heimdall {args}", MsgType.Debug);
        L("─────────────────────────────────────", MsgType.Info);

        return await RunFlashProcessAsync(heimdall, args.ToString(), ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// For each file inside the tar/tar.md5 package that is NOT a .pit,
    /// append "--PARTITION filename_no_ext" to the heimdall command.
    /// heimdall reads directly from the tar.
    /// </summary>
    private static void AppendSlotArgs(System.Text.StringBuilder sb,
                                        string tarPath, string slotHint)
    {
        // If it's a single image file (not a tar) flash directly
        var ext = Path.GetExtension(tarPath).ToLower();
        if (ext is ".img" or ".bin" or ".lz4" or ".md5" or "" &&
            !tarPath.EndsWith(".tar") && !tarPath.EndsWith(".tar.md5"))
        {
            var partName = Path.GetFileNameWithoutExtension(tarPath).ToUpper();
            sb.Append($" --{partName} \"{tarPath}\"");
            return;
        }

        // It's a tar – list contents and add each file
        var entries = TarInspector.List(tarPath)
            .Where(e => !e.Filename.EndsWith(".pit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
        {
            // heimdall can flash tar files directly since 1.4.1
            sb.Append($" --{slotHint.ToUpper()} \"{tarPath}\"");
            return;
        }

        // heimdall flash --<PARTITION> <tarfile>  (heimdall ≥ 1.4 handles tar)
        // Safer: pass the tar directly with the slot name
        sb.Append($" --{slotHint.ToUpper()} \"{tarPath}\"");
    }

    private async Task<bool> RunFlashProcessAsync(string exe, string args,
                                                    CancellationToken ct)
    {
        _cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var combined = _cts.Token;

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Start();

            // Stream stdout and stderr concurrently
            var t1 = StreamOutputAsync(_proc.StandardOutput, combined);
            var t2 = StreamOutputAsync(_proc.StandardError,  combined, isErr: true);
            await Task.WhenAll(t1, t2);
            await _proc.WaitForExitAsync(combined);

            bool ok = _proc.ExitCode == 0;
            if (ok) L("Flash complete ✓", MsgType.Success);
            else    L($"heimdall exited with code {_proc.ExitCode}", MsgType.Error);
            return ok;
        }
        catch (OperationCanceledException)
        {
            L("Flash cancelled by user.", MsgType.Warning);
            try { _proc?.Kill(); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            L($"Process error: {ex.Message}", MsgType.Error);
            return false;
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }
    }

    // Regex patterns for parsing heimdall output
    private static readonly Regex _rxPercent =
        new(@"(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly Regex _rxFlashing =
        new(@"Uploading\s+(.+?)\s*\.\.\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _rxSize =
        new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private string _currentFile = "";

    private async Task StreamOutputAsync(System.IO.StreamReader reader,
                                          CancellationToken ct, bool isErr = false)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var mt = isErr ? MsgType.Debug : DetectMsgType(line);
            L(line.Trim(), mt);

            // Parse progress from heimdall output
            var mFile = _rxFlashing.Match(line);
            if (mFile.Success) _currentFile = mFile.Groups[1].Value.Trim();

            var mPct = _rxPercent.Match(line);
            if (mPct.Success && double.TryParse(mPct.Groups[1].Value, out var pct))
            {
                Progress?.Invoke(new FlashProgress
                {
                    FileName   = _currentFile,
                    Percent    = pct,
                    StatusText = line.Trim()
                });
            }

            // "ERROR" keyword
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed",StringComparison.OrdinalIgnoreCase))
            {
                Progress?.Invoke(new FlashProgress
                { IsError = true, StatusText = line.Trim() });
            }
        }
    }

    private static MsgType DetectMsgType(string line)
    {
        var l = line.ToLower();
        if (l.Contains("error") || l.Contains("failed")) return MsgType.Error;
        if (l.Contains("warning"))                        return MsgType.Warning;
        if (l.Contains("success") || l.Contains("complete") || l.Contains("done"))
                                                          return MsgType.Success;
        if (l.StartsWith("uploading") || l.StartsWith("sending") || l.StartsWith("flashing"))
                                                          return MsgType.Info;
        return MsgType.Normal;
    }

    // ── Generic heimdall runner (for detect / pit commands) ──────────────────

    private async Task<(bool ok, string stdout)> RunHeimdallAsync(string args)
    {
        var heimdall = FindHeimdall();
        if (heimdall == null)
        {
            L("heimdall not found.", MsgType.Error);
            return (false, "");
        }

        try
        {
            var psi = new ProcessStartInfo(heimdall, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            var stdout  = await p.StandardOutput.ReadToEndAsync();
            var stderr  = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(stdout))
                foreach (var ln in stdout.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(ln)) L(ln.Trim(), DetectMsgType(ln));

            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var ln in stderr.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(ln)) L(ln.Trim(), MsgType.Debug);

            return (p.ExitCode == 0, stdout);
        }
        catch (Exception ex)
        {
            L($"heimdall error: {ex.Message}", MsgType.Error);
            return (false, "");
        }
    }

    // ── PIT parser ────────────────────────────────────────────────────────────

    private static List<PitEntry> ParsePitOutput(string text)
    {
        var entries = new List<PitEntry>();
        PitEntry? cur = null;

        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("--- Entry #"))
            {
                if (cur != null) entries.Add(cur);
                cur = new PitEntry();
                continue;
            }
            if (cur == null) continue;

            if (l.StartsWith("Partition Name:"))    cur.PartitionName = After(l, ":");
            else if (l.StartsWith("Flash Filename:"))cur.FlashFileName = After(l, ":");
            else if (l.StartsWith("FOTA Filename:")) cur.FotaFileName  = After(l, ":");
            else if (l.StartsWith("Binary Type:"))   cur.BinaryType    = After(l, ":");
            else if (l.StartsWith("Device Type:"))   cur.DeviceType    = After(l, ":");
            else if (l.StartsWith("Identifier:") &&
                     int.TryParse(After(l,":"), out var id)) cur.Identifier = id;
            else if (l.StartsWith("File Size (hex):") &&
                     long.TryParse(After(l,":").Replace("0x",""),
                         System.Globalization.NumberStyles.HexNumber, null, out var fs))
                cur.FileSize = fs;
        }
        if (cur != null) entries.Add(cur);
        return entries;
    }

    private static string After(string line, string sep)
    {
        var idx = line.IndexOf(sep, StringComparison.Ordinal);
        return idx < 0 ? "" : line[(idx + sep.Length)..].Trim();
    }

    // ── Cancel ────────────────────────────────────────────────────────────────
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        try { _proc?.Kill();  } catch { }
    }
}
