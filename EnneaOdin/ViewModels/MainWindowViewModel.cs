using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using OdinCore.Models;
using OdinCore.Protocol;
using OdinCore.Tar;
using OdinCore.Usb;

namespace EnneaOdin.ViewModels;

/// <summary>
/// Main ViewModel: owns all UI state, wires HeimdallBackend events,
/// and exposes ICommand properties that the View binds to.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    // ── Backend ──────────────────────────────────────────────────────────────
    private readonly HeimdallBackend _heimdall = new();
    private readonly DeviceScanner   _scanner  = new();
    private CancellationTokenSource? _flashCts;

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainWindowViewModel()
    {
        // Wire backend events
        _heimdall.Log      += OnBackendLog;
        _heimdall.Progress += OnBackendProgress;
        _scanner .Log      += OnBackendLog;

        // Build commands
        ScanPortsCommand  = new RelayCommand(_ => _ = ScanPortsAsync());
        DetectCommand     = new RelayCommand(_ => _ = DetectDeviceAsync());
        DownloadPitCommand= new RelayCommand(_ => _ = DownloadPitAsync());
        FlashCommand      = new RelayCommand(_ => _ = FlashAsync(),
                                             _ => !IsFlashing && HasAnySlot);
        StopCommand       = new RelayCommand(_ => StopFlash(),
                                             _ => IsFlashing);
        ClearLogCommand   = new RelayCommand(_ => ClearLog());
        ClearSlotCommand  = new RelayCommand(slot => ClearSlot(slot as string ?? ""));

        // Initial port scan
        _ = ScanPortsAsync();

        // Check heimdall availability
        CheckHeimdall();
    }

    // ── Heimdall status ───────────────────────────────────────────────────────
    private bool   _heimdallAvailable;
    private string _heimdallPath = "not found";

    public bool   HeimdallAvailable { get => _heimdallAvailable; private set => Set(ref _heimdallAvailable, value); }
    public string HeimdallPath      { get => _heimdallPath;      private set => Set(ref _heimdallPath, value); }

    private void CheckHeimdall()
    {
        var p = HeimdallBackend.FindHeimdall();
        HeimdallAvailable = p != null;
        HeimdallPath      = p ?? "NOT FOUND – brew install heimdall";
        if (!HeimdallAvailable)
            AddLog("⚠  heimdall not found. Install: brew install heimdall (macOS) or sudo apt install heimdall-flash (Linux)", MsgType.Warning);
        else
            AddLog($"✓  heimdall found: {HeimdallPath}", MsgType.Success);
    }

    // ── Port / Device ────────────────────────────────────────────────────────
    public ObservableCollection<string>     PortList        { get; } = new();
    public ObservableCollection<OdinDevice> FoundDevices    { get; } = new();

    private string? _selectedPort;
    private string  _deviceStatus = "No device";
    private bool    _deviceConnected;

    public string? SelectedPort     { get => _selectedPort;     set => Set(ref _selectedPort, value); }
    public string  DeviceStatus     { get => _deviceStatus;     set => Set(ref _deviceStatus, value); }
    public bool    DeviceConnected  { get => _deviceConnected;  set { Set(ref _deviceConnected, value); OnPropertyChanged(nameof(DotClass)); } }
    public string  DotClass         => DeviceConnected ? "dot-ok" : "dot-idle";

    // ── Slots ────────────────────────────────────────────────────────────────
    private string? _slotBL, _slotAP, _slotCP, _slotCSC, _slotUserData;

    public string? SlotBL       { get => _slotBL;       set { Set(ref _slotBL, value);       RefreshSlotState(); } }
    public string? SlotAP       { get => _slotAP;       set { Set(ref _slotAP, value);       RefreshSlotState(); } }
    public string? SlotCP       { get => _slotCP;       set { Set(ref _slotCP, value);       RefreshSlotState(); } }
    public string? SlotCSC      { get => _slotCSC;      set { Set(ref _slotCSC, value);      RefreshSlotState(); } }
    public string? SlotUserData { get => _slotUserData; set { Set(ref _slotUserData, value); RefreshSlotState(); } }

    public bool HasAnySlot =>
        !string.IsNullOrEmpty(SlotBL)  || !string.IsNullOrEmpty(SlotAP) ||
        !string.IsNullOrEmpty(SlotCP)  || !string.IsNullOrEmpty(SlotCSC)||
        !string.IsNullOrEmpty(SlotUserData);

    private void RefreshSlotState()
    {
        OnPropertyChanged(nameof(HasAnySlot));
        (FlashCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ── Options ──────────────────────────────────────────────────────────────
    private bool _optAutoReboot      = true;
    private bool _optBootUpdate      = true;
    private bool _optEfsClear        = false;
    private bool _optBlankFlash      = false;
    private bool _optRepartition     = false;
    private bool _optResetFlashCount = false;
    private bool _optNandEraseAll    = false;
    private bool _optVerifyFlash     = false;

    public bool OptAutoReboot      { get => _optAutoReboot;      set => Set(ref _optAutoReboot, value); }
    public bool OptBootUpdate      { get => _optBootUpdate;      set => Set(ref _optBootUpdate, value); }
    public bool OptEfsClear        { get => _optEfsClear;        set => Set(ref _optEfsClear, value); }
    public bool OptBlankFlash      { get => _optBlankFlash;      set => Set(ref _optBlankFlash, value); }
    public bool OptRepartition     { get => _optRepartition;     set => Set(ref _optRepartition, value); }
    public bool OptResetFlashCount { get => _optResetFlashCount; set => Set(ref _optResetFlashCount, value); }
    public bool OptNandEraseAll    { get => _optNandEraseAll;    set => Set(ref _optNandEraseAll, value); }
    public bool OptVerifyFlash     { get => _optVerifyFlash;     set => Set(ref _optVerifyFlash, value); }

    private FlashOptions BuildOptions() => new()
    {
        AutoReboot       = OptAutoReboot,
        BootUpdate       = OptBootUpdate,
        EfsClear         = OptEfsClear,
        BlankFlash       = OptBlankFlash,
        Repartition      = OptRepartition,
        ResetFlashCount  = OptResetFlashCount,
        NandEraseAll     = OptNandEraseAll,
        VerifyFlash      = OptVerifyFlash,
    };

    // ── Flash state ──────────────────────────────────────────────────────────
    private bool   _isFlashing;
    private double _progressValue;
    private string _progressText  = "Idle";
    private string _flashFile     = "";
    private bool   _showDebug     = false;

    public bool   IsFlashing     { get => _isFlashing;     set { Set(ref _isFlashing, value); (FlashCommand as RelayCommand)?.RaiseCanExecuteChanged(); (StopCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
    public double ProgressValue  { get => _progressValue;  set => Set(ref _progressValue, value); }
    public string ProgressText   { get => _progressText;   set => Set(ref _progressText, value); }
    public string FlashFile      { get => _flashFile;       set => Set(ref _flashFile, value); }
    public bool   ShowDebug      { get => _showDebug;       set => Set(ref _showDebug, value); }

    // ── Log ──────────────────────────────────────────────────────────────────
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private string _rawLog = "";
    public  string RawLog  { get => _rawLog; set => Set(ref _rawLog, value); }

    private void AddLog(string message, MsgType type = MsgType.Normal)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (type == MsgType.Debug && !ShowDebug) return;
            var entry = new LogEntry { Type = type, Message = message };
            LogEntries.Add(entry);
            RawLog += entry + "\n";
        });
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        RawLog = "";
    }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand ScanPortsCommand   { get; }
    public ICommand DetectCommand      { get; }
    public ICommand DownloadPitCommand { get; }
    public ICommand FlashCommand       { get; }
    public ICommand StopCommand        { get; }
    public ICommand ClearLogCommand    { get; }
    public ICommand ClearSlotCommand   { get; }

    // ── Command implementations ───────────────────────────────────────────────

    public async Task ScanPortsAsync()
    {
        AddLog("Scanning ports...", MsgType.Info);
        PortList.Clear();

        var allPorts = DeviceScanner.AllPorts();
        foreach (var p in allPorts) PortList.Add(p);

        var devices = await _scanner.ScanAsync();
        FoundDevices.Clear();
        foreach (var d in devices) { FoundDevices.Add(d); if (PortList.Contains(d.Port)) continue; PortList.Add(d.Port); }

        if (devices.Count > 0)
        {
            SelectedPort    = devices[0].Port;
            DeviceStatus    = devices[0].Name;
            DeviceConnected = true;
            AddLog($"Found device: {devices[0].Name} on {devices[0].Port}", MsgType.Success);
        }
        else if (allPorts.Count > 0)
        {
            SelectedPort    = allPorts[0];
            DeviceStatus    = "Unknown device";
            DeviceConnected = false;
            AddLog($"No Samsung device auto-detected. {allPorts.Count} port(s) available.", MsgType.Warning);
        }
        else
        {
            DeviceStatus    = "No ports found";
            DeviceConnected = false;
            AddLog("No serial ports found. Connect device in download mode.", MsgType.Warning);
        }
    }

    private async Task DetectDeviceAsync()
    {
        AddLog("─── Detect ─────────────────────────────", MsgType.Info);
        var ok = await _heimdall.DetectDeviceAsync();
        DeviceConnected = ok;
        DeviceStatus    = ok ? "Connected ✓" : "Not found";
    }

    private async Task DownloadPitAsync()
    {
        AddLog("─── Download PIT ────────────────────────", MsgType.Info);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "EnneaOdin", "pit");
        var (ok, path) = await _heimdall.DownloadPitAsync(dir);
        if (ok) AddLog($"PIT saved: {path}", MsgType.Success);
    }

    private async Task FlashAsync()
    {
        AddLog("═══ FLASH START ════════════════════════", MsgType.Info);
        IsFlashing   = true;
        ProgressValue = 0;
        ProgressText  = "Initialising...";

        _flashCts = new CancellationTokenSource();

        var slots = new FlashSlots
        {
            BL       = SlotBL,
            AP       = SlotAP,
            CP       = SlotCP,
            CSC      = SlotCSC,
            UserData = SlotUserData,
        };

        try
        {
            var ok = await _heimdall.FlashAsync(slots, BuildOptions(), _flashCts.Token);
            ProgressValue = ok ? 100 : 0;
            ProgressText  = ok ? "Flash Complete ✓" : "Flash Failed ✗";
            if (ok) AddLog("═══ FLASH COMPLETE ════════════════════", MsgType.Success);
            else    AddLog("═══ FLASH FAILED ══════════════════════", MsgType.Error);
        }
        catch (Exception ex)
        {
            AddLog($"Unexpected error: {ex.Message}", MsgType.Error);
            ProgressText = "Error";
        }
        finally
        {
            IsFlashing = false;
        }
    }

    private void StopFlash()
    {
        AddLog("User requested stop...", MsgType.Warning);
        _flashCts?.Cancel();
        _heimdall.Cancel();
        ProgressText = "Stopped";
    }

    private void ClearSlot(string slot)
    {
        switch (slot)
        {
            case "BL":       SlotBL       = null; break;
            case "AP":       SlotAP       = null; break;
            case "CP":       SlotCP       = null; break;
            case "CSC":      SlotCSC      = null; break;
            case "USERDATA": SlotUserData = null; break;
        }
    }

    // ── Backend event handlers ───────────────────────────────────────────────
    private void OnBackendLog(string msg, MsgType type) => AddLog(msg, type);

    private void OnBackendProgress(FlashProgress p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressValue = p.Percent;
            FlashFile     = p.FileName;
            ProgressText  = p.IsError   ? $"ERROR: {p.StatusText}"
                          : p.IsComplete? "Complete ✓"
                          : string.IsNullOrEmpty(p.FileName) ? p.StatusText
                          : $"{p.FileName}  {p.Percent:F0}%";
        });
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Simple ICommand helper ────────────────────────────────────────────────────
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object? p)    => _execute(p);
    public void RaiseCanExecuteChanged() =>
        Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
