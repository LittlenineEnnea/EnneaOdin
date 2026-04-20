namespace OdinCore.Models;

// ── Device ─────────────────────────────────────────────────────────────────
public class OdinDevice
{
    public string Name    { get; set; } = "";
    public string Port    { get; set; } = "";   // /dev/cu.usbmodemXXX  or  /dev/ttyACM0
    public string VID     { get; set; } = "";
    public string PID     { get; set; } = "";
    public bool   IsConnected { get; set; }

    public override string ToString() =>
        string.IsNullOrEmpty(Name) ? Port : $"{Name}  [{Port}]";
}

// ── Device info returned by heimdall detect / print-pit ───────────────────
public class DeviceInfo
{
    public string? Model       { get; set; }
    public string? Product     { get; set; }
    public string? FwVer       { get; set; }
    public string? Vendor      { get; set; }
    public string? SalesCode   { get; set; }
    public string? BuildNumber { get; set; }
    public string? Protocol    { get; set; }
    public Dictionary<string, string> Raw { get; set; } = new();
}

// ── Flash slots (BL / AP / CP / CSC / UserData) ──────────────────────────
public class FlashSlots
{
    public string? BL       { get; set; }
    public string? AP       { get; set; }
    public string? CP       { get; set; }
    public string? CSC      { get; set; }
    public string? UserData { get; set; }

    public bool AnySelected =>
        !string.IsNullOrEmpty(BL)  || !string.IsNullOrEmpty(AP) ||
        !string.IsNullOrEmpty(CP)  || !string.IsNullOrEmpty(CSC)||
        !string.IsNullOrEmpty(UserData);
}

// ── Flash options ─────────────────────────────────────────────────────────
public class FlashOptions
{
    public bool AutoReboot        { get; set; } = true;
    public bool BootUpdate        { get; set; } = true;
    public bool EfsClear          { get; set; } = false;
    public bool BlankFlash        { get; set; } = false;
    public bool Repartition       { get; set; } = false;
    public bool ResetFlashCount   { get; set; } = false;
    public bool NandEraseAll      { get; set; } = false;
    public bool VerifyFlash       { get; set; } = false;
}

// ── PIT partition entry ───────────────────────────────────────────────────
public class PitEntry
{
    public string PartitionName  { get; set; } = "";
    public string FlashFileName  { get; set; } = "";
    public string FotaFileName   { get; set; } = "";
    public string BinaryType     { get; set; } = "";
    public string DeviceType     { get; set; } = "";
    public int    Identifier     { get; set; }
    public long   BlockSize      { get; set; }
    public long   BlockCount     { get; set; }
    public long   FileSize       { get; set; }
    public int    Attributes     { get; set; }
}

// ── Log entry ─────────────────────────────────────────────────────────────
public enum MsgType { Normal, Info, Success, Warning, Error, Debug }

public class LogEntry
{
    public DateTime Time    { get; init; } = DateTime.Now;
    public MsgType  Type    { get; init; }
    public string   Message { get; init; } = "";
    public string   TimeStr => Time.ToString("HH:mm:ss.fff");
    public string   Prefix  => Type switch
    {
        MsgType.Success => "[OK]   ",
        MsgType.Warning => "[WARN] ",
        MsgType.Error   => "[ERR]  ",
        MsgType.Debug   => "[DBG]  ",
        MsgType.Info    => "[INFO] ",
        _               => "       "
    };
    public override string ToString() => $"{TimeStr}  {Prefix}{Message}";
}

// ── Flash progress ────────────────────────────────────────────────────────
public class FlashProgress
{
    public string FileName    { get; set; } = "";
    public double Percent     { get; set; }   // 0-100
    public long   Written     { get; set; }
    public long   Total       { get; set; }
    public long   SpeedBps    { get; set; }
    public bool   IsComplete  { get; set; }
    public bool   IsError     { get; set; }
    public string StatusText  { get; set; } = "";
}
