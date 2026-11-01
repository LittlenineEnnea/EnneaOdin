// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is derived from Heimdall:
//   native/vendor/Heimdall/heimdall/source/ClosePcScreenAction.cpp  (the "Close PC screen"
//                                                      and "Reboot device"
//                                                      sequence, lines 110-118)
//   native/vendor/Heimdall/heimdall/source/PrintPitAction.cpp       (print-pit's no-reboot
//                                                      default, lines 69-70)
//   native/vendor/Heimdall/heimdall/source/DownloadPitAction.cpp    (download-pit's
//                                                      no-reboot default, :75)
//   native/vendor/Heimdall/heimdall/source/FlashAction.cpp          (the order of a flash)
// Heimdall is Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna
//   https://glassechidna.com.au
// Heimdall is Copyright (c) 2021-2024 Henrik Grimler
//   https://git.sr.ht/~grimler/Heimdall  (the grimler fork, v2.2.2)
// Heimdall is MIT. Its notices are retained as that licence requires;
// NOTICE records what is modified.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnneaOdin.Services;
using OdinCore.Devices;
using OdinCore.Diagnostics;
using OdinCore.Firmware;
using OdinCore.Flash;
using OdinCore.Native;
using OdinCore.Protocol;
using OdinCore.Usb;

namespace EnneaOdin.ViewModels;

/// <summary>
/// The window's state and commands.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// How often the device list is re-enumerated. Two seconds, without
    /// reading string descriptors, so a poll never opens a device.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LogRing _ring = new();
    private readonly UsbEnumerator _enumerator;
    private readonly UsbEnumerator _pollEnumerator;

    private IFilePicker? _picker;
    private IConfirmService? _confirm;

    // The in-flight operation's cancellation source, published only for the
    // lifetime of that operation. It is created by a "using" inside the
    // operation's own scope and cleared in that scope's finally, so nothing
    // outlives a run; Stop needs a handle to it, which is the only reason it
    // is reachable from a field at all.
    private CancellationTokenSource? _cancel;
    private TaskCompletionSource? _inFlight;

    // The last poll failure, so a persistent one is reported once instead of
    // every two seconds.
    private string? _lastPollError;

    // The bundle workspace's lock, so the startup sweep can tell a workspace
    // this program abandoned from one that is in use right now.
    private IDisposable? _bundleClaim;

    private CancellationTokenSource? _pollCancel;
    private Task? _pollTask;

    // True while one of the three commands that show a modal dialogue without
    // holding the one-operation-at-a-time lock is showing it. Stop, Reset and
    // About are all reachable while their own CanExecute stays true, so
    // without this a second click would stack a second ContentDialog on the
    // first; for Stop in particular that is two confirmations for one
    // cancellation.
    private bool _dialogOpen;

    private PitData? _devicePit;

    // What the handset says it is, from the protocol's DVIF command. Asking
    // does not consume the one ODIN handshake it grants, so this is read on
    // connect. See DownloadModeIdentity.
    private DownloadModeIdentity? _identity;

    // A PIT parsed from a file the user chose, which needs no device at all.
    // Kept separate from _devicePit on purpose: the partition table on screen
    // says which of the two it is showing, and a file's layout must never be
    // read as the handset's.
    private PitData? _filePit;
    private string? _filePitPath;
    private IReadOnlyList<InfoField> _sessionRows = Array.Empty<InfoField>();
    private FirmwarePackage? _package;
    private FlashPlan? _plan;

    /// <summary>
    /// True once an operation has left the handset in download mode, which is
    /// what makes the next one a resume. ClosePcScreenAction.cpp:93 derives the
    /// same fact from --no-reboot, and BridgeManager.cpp:500-504 is what then
    /// skips the handshake.
    /// </summary>
    private bool _deviceLeftInDownloadMode;

    // Set once a resumed session is refused by the handset. Sticky, because a
    // device that refuses it once refuses it every time, and each attempt
    // costs a failed BeginSession.
    private bool _resumeRefused;

    private LibUsbStatus? _libUsb;
    private UsbDiagnostic _diagnostic = UsbDiagnostic.NoDevice;
    private string? _diagnosticText;

    public MainWindowViewModel()
    {
        Log = new LogPaneViewModel(_ring);

        // The enumerator used by explicit commands writes to the log; the one
        // the two-second poll uses does not, because one line every two
        // seconds would bury everything else.
        _enumerator = new UsbEnumerator(Log.Sink);
        _pollEnumerator = new UsbEnumerator(null);

        // The labels come from OdinCore so the rows and the firmware layer
        // cannot disagree about what a container is called.
        Slots = new ObservableCollection<FirmwareSlotViewModel>();

        // Nothing is ticked here: the rows are empty, so there is nothing to
        // tick. Importing a bundle ticks BL, AP, CP and CSC.
        foreach (FirmwareSlot slot in new[]
                 {
                     FirmwareSlot.Bl, FirmwareSlot.Ap, FirmwareSlot.Cp,
                     FirmwareSlot.Csc, FirmwareSlot.UserData
                 })
        {
            Slots.Add(new FirmwareSlotViewModel(slot, FirmwarePackage.SlotLabel(slot), false,
                                                false, BrowseSlotAsync, OnSlotChanged));
        }

        PitSlot = new FirmwareSlotViewModel(FirmwareSlot.Loose, "PIT", true, false,
                                            BrowseSlotAsync, OnSlotChanged);


        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        int plus = version.IndexOf('+');
        VersionNumber = plus > 0 ? version[..plus] : version;
        VersionText = "EnneaOdin " + VersionNumber;

        ShowDiagnosticRows();
        UpdateStatusText();
    }

    // ---------------------------------------------------------------- wiring

    /// <summary>
    /// Give the view model the two dialogue services. Done by the application
    /// once the window exists, so that this class never holds a Window.
    /// </summary>
    public void Attach(IFilePicker picker, IConfirmService confirm)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
    }

    /// <summary>Record an unhandled exception caught by the process handlers.</summary>
    public void ReportCrash(string message) => _ring.Add(OdinEvent.Error(message, "crash"));

    // ------------------------------------------------------------- log, text

    public LogPaneViewModel Log { get; }

    /// <summary>"EnneaOdin 1.0.0", read from the assembly and never from XAML.</summary>
    public string VersionText { get; }

    /// <summary>
    /// Just the number, for the band, where the program's name is already the
    /// word to its left. Derived from <see cref="VersionText"/> rather than
    /// read again, so the two can never disagree.
    /// </summary>
    public string VersionNumber { get; }

    public string LicenceText =>
        "GPL-3.0; download-mode protocol ported from Heimdall 2.2.2 (MIT)";

    /// <summary>
    /// The whole of the About dialogue, assembled here rather than written
    /// into markup, so the version cannot drift from the build and so the
    /// Heimdall notice sits in the same file as the source header that cites
    /// it.
    /// </summary>
    public string AboutText =>
        VersionText + "\n" +
        "Samsung download-mode flash tool for macOS and Linux.\n" +
        "\n" +
        "LICENCE\n" +
        "EnneaOdin is free software under the GNU General Public License, version 3 " +
        "or later. It comes with no warranty of any kind, not even for merchantability " +
        "or fitness for a particular purpose. See the file LICENCE in the repository " +
        "root for the full terms.\n" +
        "\n" +
        "HEIMDALL\n" +
        "The Samsung download-mode protocol in this program is ported from Heimdall " +
        "2.2.2, which is distributed under the MIT licence. The copyright notices and " +
        "the permission notice in native/vendor/Heimdall/LICENSE are retained as that licence " +
        "requires, and the statement in NOTICE says where its code now lives.\n" +
        "\n" +
        "Copyright (c) 2010-2017 Benjamin Dobell, Glass Echidna\n" +
        "Copyright (c) 2021-2024 Henrik Grimler\n" +
        "Upstream: https://git.sr.ht/~grimler/Heimdall\n" +
        "\n" +
        "PREREQUISITE\n" +
        "This program talks to the handset through libusb-1.0, a system library " +
        "installed by the platform package manager. No heimdall binary is required or " +
        "used: the protocol runs in this process.\n" +
        "\n" +
        "libusb on this machine: " + LibUsbText;

    /// <summary>The sentence that closes the information panel.</summary>
    public string InfoScopeNote => DeviceInformation.ScopeNote;

    // ------------------------------------------------------------- device row

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    [ObservableProperty]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "not connected";

    /// <summary>The device box's first line: the handset, or that none is here.</summary>
    [ObservableProperty]
    private string _deviceSummary = "Waiting for a device...";

    /// <summary>Its second line: what the handset calls itself, once read.</summary>
    [ObservableProperty]
    private string _deviceDetail = string.Empty;

    // ------------------------------------------------------------- manual

    /// <summary>
    /// The manual partition list: a name and a file per row, written in this
    /// order. Nothing is packed and nothing is spooled - see
    /// FlashPlanner.BuildManual.
    /// </summary>
    public ObservableCollection<ManualRowViewModel> ManualRows { get; } = new();

    /// <summary>
    /// Which page the right-hand column shows: 0 firmware, 1 manual,
    /// 2 options.
    /// </summary>
    [ObservableProperty]
    private int _selectedPage;

    /// <summary>
    /// Which page Resolve and Start work from.
    ///
    /// NOT just "the options page is not selected". Options is a third page
    /// and switching to it to tick a box must not silently move the flash
    /// back to the firmware slots, so only the two content pages set this and
    /// Options leaves it alone.
    /// </summary>
    [ObservableProperty]
    private bool _isManualMode;

    public string ManualSummary
    {
        get
        {
            int ready = ManualRows.Count(r => r.IsComplete);

            return ready == 0
                ? "No partition rows."
                : string.Format(CultureInfo.InvariantCulture,
                    "{0} partition(s): {1}.", ready,
                    string.Join(", ", ManualRows.Where(r => r.IsComplete)
                                                .Select(r => r.PartitionName.Trim())));
        }
    }

    /// <summary>The picker only appears when there is something to pick between.</summary>
    [ObservableProperty]
    private bool _hasMultipleDevices;

    // The device the descriptors were read for, so a reconnect reads again
    // and a poll does not.
    private string? _autoReadKey;

    [ObservableProperty]
    private string _libUsbText = "not probed yet";

    [ObservableProperty]
    private string _sessionText = "closed";

    [ObservableProperty]
    private bool _libUsbAvailable;

    /// <summary>
    /// The protocol engine's one-line status: which library loaded, which
    /// Heimdall it carries, and its ABI. Or why it did not load, with the
    /// build command.
    /// </summary>
    [ObservableProperty]
    private string _engineText = "not probed yet";

    [ObservableProperty]
    private bool _engineAvailable;

    /// <summary>
    /// The engine's own account of itself, for the panel: the loaded path,
    /// the Heimdall version, the bridge version and the ABI, or the error and
    /// every candidate path the resolver tried.
    /// </summary>
    [ObservableProperty]
    private string _engineDetail = "The protocol engine has not been probed yet.";

    /// <summary>
    /// How bad the device status is. It is a presentation value and it lives
    /// here because it is derived from exactly the same two facts as the status
    /// word: the typed enumeration diagnosis and, when there is one, the
    /// selected device's own three-state access word. The device section
    /// always carries that word as text as well, so the colour is never the
    /// only thing saying what happened.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsReady))]
    [NotifyPropertyChangedFor(nameof(StatusIsWarning))]
    [NotifyPropertyChangedFor(nameof(StatusIsError))]
    private DeviceStatusLevel _statusLevel = DeviceStatusLevel.Info;

    /// <summary>The three style-class triggers the window binds to.</summary>
    public bool StatusIsReady => StatusLevel == DeviceStatusLevel.Ready;

    public bool StatusIsWarning => StatusLevel == DeviceStatusLevel.Warning;

    public bool StatusIsError => StatusLevel == DeviceStatusLevel.Error;

    /// <summary>
    /// The status line's body: the status words, then the device, then the
    /// actionable detail, then the libusb line and its install command when
    /// that is what is missing. Never empty.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "No enumeration has run yet.";

    // ---------------------------------------------------------------- slots

    public ObservableCollection<FirmwareSlotViewModel> Slots { get; }

    /// <summary>
    /// The PIT row. Kept enabled whether or not Repartition is set: a PIT
    /// supplied without Repartition is a claim about the layout the device
    /// already has, and FlashPlanner refuses the flash when it disagrees
    /// (FlashAction.cpp:416-428). Disabling the row would make that check
    /// unreachable.
    /// </summary>
    public FirmwareSlotViewModel PitSlot { get; }

    /// <summary>
    /// True when at least one ticked row destroys user data: the USERDATA slot,
    /// or the plain CSC archive rather than HOME_CSC. Drives the red callout
    /// above the file list, so the warning is on screen while the choice is
    /// being made and not only in the confirmation at the end.
    /// </summary>
    public bool SelectionErasesUserData => Slots.Any(s => s.CountsTowardFlash && s.ErasesUserData);

    /// <summary>How many rows are ticked and filled, as a sentence.</summary>
    public string SelectionSummary
    {
        get
        {
            List<FirmwareSlotViewModel> chosen =
                Slots.Where(s => s.CountsTowardFlash).ToList();

            if (chosen.Count == 0)
                return "Nothing selected.";

            return string.Format(CultureInfo.InvariantCulture, "{0} selected: {1}.",
                chosen.Count, string.Join(", ", chosen.Select(s => s.DisplayLabel)));
        }
    }

    // ---------------------------------------------------------------- bundle

    /// <summary>
    /// The imported firmware zip, or null. Holding it open costs one file
    /// handle and the zip's central directory; it is NOT the extracted data,
    /// which only exists after Resolve.
    /// </summary>
    private FirmwareBundle? _bundle;

    /// <summary>
    /// Where a bundle's archives were extracted to, so the directory can be
    /// deleted even though FirmwarePackage owns a different one.
    /// </summary>
    private string? _bundleWorkspace;

    [ObservableProperty]
    private string _bundlePath = string.Empty;

    [ObservableProperty]
    private bool _bundleLoaded;

    /// <summary>
    /// What the imported zip contains, in one line, from the central directory
    /// only. Never a figure that was not read from the archive itself.
    /// </summary>
    [ObservableProperty]
    private string _bundleSummary = "No firmware bundle imported. Rows may also be filled one file at a time.";

    /// <summary>
    /// True when the bundle ships both CSC and HOME_CSC, which is what makes
    /// the choice between them real. With only one of the two, PickCsc falls
    /// back to what is there and says so in the log.
    /// </summary>
    [ObservableProperty]
    private bool _bundleHasBothCsc;

    /// <summary>
    /// The firmware-information text file distributors drop into the zip, when
    /// there is one. Shown as-is; it is the distributor's own text, not a
    /// value this program derived.
    /// </summary>
    [ObservableProperty]
    private string _bundleInfoText = string.Empty;

    private CscMode _cscMode = CscMode.Csc;

    /// <summary>
    /// Which CSC archive a bundle import fills the CSC row with.
    /// </summary>
    public bool IsCscMode => _cscMode == CscMode.Csc;

    public bool IsHomeCscMode => _cscMode == CscMode.HomeCsc;

    /// <summary>
    /// What the two CSC segments mean, on screen next to them rather than in a
    /// tool tip.
    /// </summary>
    public string CscModeNote => IsHomeCscMode
        ? "HOME_CSC writes the regional configuration and leaves the user data partition alone."
        : "CSC writes the regional configuration AND the user data partition, so it ERASES USER DATA.";

    // --------------------------------------------------------------- options

    [ObservableProperty] private bool _rebootWhenFinished = true;
    [ObservableProperty] private bool _repartition;
    [ObservableProperty] private bool _tFlash;
    [ObservableProperty] private bool _skipSizeCheck;
    [ObservableProperty] private bool _resumeSession;
    [ObservableProperty] private bool _waitForDevice;
    [ObservableProperty] private bool _verboseLog;

    /// <summary>
    /// Read each image straight out of its archive while the device is asking
    /// for it, instead of decompressing every member to a temporary directory
    /// first.
    /// </summary>
    [ObservableProperty] private bool _streamImages = true;

    /// <summary>
    /// Hash each archive against the trailing MD5 digest it carries, before
    /// anything is sent.
    /// </summary>
    [ObservableProperty] private bool _verifyDigests;

    /// <summary>The five spellings heimdall's --usb-log-level accepts.</summary>
    // ------------------------------------------------------- information rows

    public ObservableCollection<InfoRowViewModel> InfoRows { get; } = new();

    public ObservableCollection<InfoRowViewModel> PartitionRows { get; } = new();

    /// <summary>
    /// Where the partition table on screen came from: the handset, or a named
    /// file. A layout read from a file is not the layout the device has, and
    /// the panel is not allowed to be ambiguous about which it is showing.
    /// </summary>
    [ObservableProperty]
    private string _partitionTableSource = "No partition table yet.";

    /// <summary>
    /// Whether the information and partition tables are on screen. Driven by
    /// the Details button and nothing else: a read fills the tables, it does
    /// not open them.
    /// </summary>
    [ObservableProperty]
    private bool _showDeviceDetail;

    // -------------------------------------------------------------- progress

    /// <summary>The partition being written, 0 to 100.</summary>
    [ObservableProperty]
    private double _partitionProgress;

    /// <summary>The whole flash, 0 to 100.</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _partitionLabel = "idle";

    [ObservableProperty]
    private string _totalLabel = "0%";

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private string _progressText = "idle";

    [ObservableProperty]
    private string _phaseText = "Phase: idle";

    /// <summary>
    /// Why Start is not available, as one short sentence, or empty when it is.
    ///
    /// A greyed-out button with no reason beside it is the worst thing a tool
    /// can show: Start needs BOTH a claimable download-mode device AND a
    /// resolved plan, and neither was stated anywhere.
    /// </summary>
    [ObservableProperty]
    private string _blockedReason = string.Empty;

    public bool IsBlocked => BlockedReason.Length > 0;

    [ObservableProperty]
    private bool _isBusy;

    // --------------------------------------------------------------- commands

    [RelayCommand]
    private async Task InitialiseAsync()
    {
        Report(OdinLevel.Debug, VersionText + " on " + UsbPlatform.Name + ".");

        // A crash or a kill during Resolve leaves the whole extracted tree
        // behind, and on the reference firmware that is close to 7 GiB. Nothing
        // in this application had ever deleted a workspace it did not create in
        // the same process, so one unlucky afternoon cost the user a volume.
        // Swept here, before anything else, and only for workspaces that have
        // been cold for half an hour - a second copy of this program may be
        // flashing out of its own.
        string sweepRoot = Path.Combine(Path.GetTempPath(), "enneaodin");
        SweepResult swept = await Task.Run(
            () => WorkspaceStorage.SweepStaleWorkspaces(sweepRoot, "enneaodin-", null))
            .ConfigureAwait(true);

        if (!swept.IsQuiet)
        {
            Report(swept.Failed == 0 ? OdinLevel.Info : OdinLevel.Warning, swept.Summary);

            foreach (string path in swept.Paths)
                Report(OdinLevel.Debug, "Swept " + path);
        }

        // The engine first. Without it every device operation is refused, so
        // a user looking at a dead panel should find the reason in the first
        // few lines of the log and in the device card, not in a stack trace
        // after pressing a button.
        OdinEngineStatus engine = await Task.Run(() => OdinSession.Engine).ConfigureAwait(true);
        ApplyEngineStatus(engine);

        LibUsbStatus status = await Task.Run(LibUsbAvailability.Probe).ConfigureAwait(true);
        ApplyLibUsbStatus(status);

        if (status.Available)
            Report(OdinLevel.Debug, "libusb " + status.Summary + ".");

        if (!status.Available)
        {
            Report(OdinLevel.Error, "libusb-1.0 " + status.Summary);
            Report(OdinLevel.Info, "Install it with: " + status.InstallCommand);

            // Through the one code path, so the status bar's severity and body
            // are derived from the same facts as everywhere else rather than
            // assigned here by hand.
            _diagnostic = UsbDiagnostic.LibUsbUnavailable;
            _diagnosticText = status.Error;
            UpdateStatusText();
            ShowDiagnosticRows();
            RefreshCommandStates();
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        StartPolling();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunExclusiveAsync("Refresh", async ct =>
    {
        // Let the automatic descriptor read run again: Refresh is how a user
        // retries one that failed.
        _autoReadKey = null;

        UsbEnumerationResult result = await _enumerator.EnumerateAsync(true, ct).ConfigureAwait(true);

        if (WaitForDevice && result.DownloadModeDevices.Count == 0 &&
            result.Kind != UsbDiagnostic.LibUsbUnavailable)
        {
            // BridgeManager.cpp:81-82, :112-113 -- heimdall's --wait, which
            // polls rather than failing at once. Stop cancels it.
            SetPhase("waiting for a device");
            ProgressIndeterminate = true;
            result = await _enumerator.WaitForDownloadDeviceAsync(TimeSpan.FromSeconds(1), ct)
                                      .ConfigureAwait(true);
            ProgressIndeterminate = false;
        }

        ApplyEnumeration(result);
        SetPhase("idle");
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ReadDeviceInfoAsync() => RunExclusiveAsync("Read device info", async ct =>
    {
        // Reading string descriptors means opening the device; that is the one
        // thing the poll never does.
        UsbEnumerationResult result = await _enumerator.EnumerateAsync(true, ct).ConfigureAwait(true);
        ApplyEnumeration(result);

        // DVIF: the model, firmware version, sales code and capacity, which
        // this program used to say the protocol did not report. It does. And
        // asking does not consume the handshake, so it is safe here.
        DeviceItemViewModel? device = SelectedDevice;

        if (device is not null && device.Record.IsDownloadMode)
        {
            SetPhase("asking the handset what it is");

            _identity = await Task.Run(() => DownloadModeIdentity.Read(Log.Sink), ct)
                                  .ConfigureAwait(true);

            RaiseIdentityChanged();

            if (_identity is null)
            {
                Report(OdinLevel.Warning,
                    "The handset did not answer DVIF. Its protocol server may not be running: " +
                    "that happens when download mode was entered by a boot failure rather than " +
                    "by the key combination.");
            }
            else
            {
                Report(OdinLevel.Info, "Handset: " + Describe(_identity));
            }
        }

        RebuildInfoRows();
        SetPhase("idle");
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ReadPitAsync() => RunExclusiveAsync("Read PIT", async ct =>
    {
        // PrintPitAction.cpp:69-70 and DownloadPitAction.cpp:75 both end the
        // session without rebooting unless told otherwise, which leaves the
        // device in download mode for the next operation to resume into.
        (PitData pit, IReadOnlyList<InfoField> sessionRows) = await WithSessionAsync(
            reboot: false, ct,
            async (session, token) =>
            {
                PitData downloaded = await session.DownloadPitAsync(Log.Sink, token).ConfigureAwait(false);
                return (downloaded, DeviceInformation.FromSession(session));
            }).ConfigureAwait(true);

        _devicePit = pit;
        _sessionRows = sessionRows;
        InvalidatePlan("A new PIT was read.");
        RebuildInfoRows();

        // The full report goes into the log so that "Save log..." carries it.
        Report(OdinLevel.Info, "PIT read from the device:");
        foreach (string line in PitTextFormatter.Format(pit)
                                                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Report(OdinLevel.Info, line.TrimEnd());
        }

        SetPhase("PIT read");
    });

    [RelayCommand(CanExecute = nameof(CanSavePit))]
    private Task SavePitAsync() => RunExclusiveAsync("Save PIT", async ct =>
    {
        PitData pit = _devicePit ?? throw new InvalidOperationException("No PIT has been read yet.");
        IFilePicker picker = RequirePicker();

        string suggested = "enneaodin-pit-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".pit";

        string? path = await picker.SaveFileAsync("Save PIT", suggested, "pit").ConfigureAwait(true);

        if (path is null)
        {
            Report(OdinLevel.Info, "Save PIT cancelled by user.");
            return;
        }

        byte[] packed = pit.Pack();
        await File.WriteAllBytesAsync(path, packed, ct).ConfigureAwait(true);
        Report(OdinLevel.Info, "PIT written to " + path + " (" +
            packed.Length.ToString(CultureInfo.InvariantCulture) + " bytes).");
    });

    /// <summary>ClosePcScreenAction.cpp:110-118 without --no-reboot.</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task RebootDeviceAsync() => RunExclusiveAsync("Reboot device", async ct =>
    {
        bool confirmed = await RequireConfirm().ConfirmAsync("Reboot device",
            "End the session and reboot " + DeviceLabel() + "?\n\n" +
            "The handset will leave download mode. Nothing is written.",
            "Reboot").ConfigureAwait(true);

        if (!confirmed)
        {
            Report(OdinLevel.Info, "Reboot cancelled by user.");
            return;
        }

        await WithSessionAsync(reboot: true, ct,
            (_, _) => Task.FromResult(true)).ConfigureAwait(true);

        Report(OdinLevel.Info, "Attempt complete.");
        SetPhase("idle");
    });

    /// <summary>
    /// Import a distributor's firmware zip and match its archives to the slot
    /// rows. Reads the zip's central directory only: on the reference
    /// firmware, a 5.76 GiB archive listed in 4 ms and not one byte of it was
    /// decompressed. Nothing is extracted until Resolve.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportBundle))]
    private Task ImportBundleAsync() => RunExclusiveAsync("Import bundle", async ct =>
    {
        IFilePicker picker = RequirePicker();

        string? path = await picker
            .OpenFileAsync("Select a firmware bundle (.zip)", FilePickerKind.Bundle)
            .ConfigureAwait(true);

        if (path is null)
        {
            SetPhase("idle");
            return;
        }

        if (!FirmwareBundle.LooksLikeBundle(path))
        {
            throw new InvalidOperationException(
                "\"" + path + "\" is not a zip archive. A firmware bundle is the .zip a " +
                "distributor ships, holding BL, AP, CP, CSC, HOME_CSC and USERDATA archives.");
        }

        SetPhase("reading the bundle index");
        ProgressIndeterminate = true;

        FirmwareBundle bundle = await Task.Run(
            () => FirmwareBundle.Open(path, Log.Sink), ct).ConfigureAwait(true);

        DisposeBundle();

        _bundle = bundle;
        BundlePath = path;
        BundleLoaded = true;
        BundleHasBothCsc = bundle.HasBothCscArchives;
        BundleInfoText = bundle.InfoText ?? string.Empty;

        ApplyBundleToRows();

        ProgressIndeterminate = false;
        SetPhase("idle");
    });

    /// <summary>
    /// Forget the imported bundle. The rows it filled are emptied; a row the
    /// user filled by hand is left alone, because it was never the bundle's.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearBundle))]
    private void ClearBundle()
    {
        foreach (FirmwareSlotViewModel row in Slots.Where(r => r.IsFromBundle).ToList())
            row.Clear();

        DisposeBundle();

        BundlePath = string.Empty;
        BundleLoaded = false;
        BundleHasBothCsc = false;
        BundleInfoText = string.Empty;
        BundleSummary = "No firmware bundle imported. Rows may also be filled one file at a time.";

        Report(OdinLevel.Info, "Firmware bundle closed. Nothing was extracted.");
        InvalidatePlan("Bundle closed.");
        RefreshCommandStates();
    }

    /// <summary>
    /// Two commands rather than one taking "CSC" or "HOME_CSC": nothing in this
    /// window dispatches on a magic string.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeCscMode))]
    private void SelectCsc() => SetCscMode(CscMode.Csc);

    [RelayCommand(CanExecute = nameof(CanChangeCscMode))]
    private void SelectHomeCsc() => SetCscMode(CscMode.HomeCsc);

    /// <summary>
    /// Parse the PIT file named in the PIT row and show its partition table.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadPitFile))]
    private Task LoadPitFileAsync() => RunExclusiveAsync("Load PIT file", async ct =>
    {
        string path = PitSlot.FilePath.Trim();

        PitData pit = await Task.Run(() => LoadPitFile(path), ct).ConfigureAwait(true);

        _filePit = pit;
        _filePitPath = path;

        Report(OdinLevel.Info, "PIT file parsed: " + PitTextFormatter.FormatHeaderLine(pit));
        Report(OdinLevel.Info, string.Format(CultureInfo.InvariantCulture,
            "{0} entries, {1} of them flashable.",
            pit.Entries.Count, pit.Entries.Count(e => e.IsFlashable)));

        RebuildInfoRows();
        SetPhase("idle");
    });

    [RelayCommand]
    private void AddManualRow() =>
        ManualRows.Add(new ManualRowViewModel(BrowseManualAsync, RemoveManualRow, OnManualChanged));

    private void RemoveManualRow(ManualRowViewModel row)
    {
        ManualRows.Remove(row);
        OnManualChanged();
    }

    private async Task BrowseManualAsync(ManualRowViewModel row)
    {
        string? path = await RequirePicker()
            .OpenFileAsync("Select the image for " +
                           (string.IsNullOrWhiteSpace(row.PartitionName)
                               ? "this partition"
                               : row.PartitionName.Trim()))
            .ConfigureAwait(true);

        if (path is not null)
            row.FilePath = path;
    }

    private void OnManualChanged()
    {
        OnPropertyChanged(nameof(ManualSummary));
        InvalidatePlan("Manual list changed.");
        RefreshCommandStates();
    }

    /// <summary>
    /// Resolve the manual list: read the device PIT if it is not already
    /// known, measure each image, and match each row's partition by name.
    /// Nothing is extracted and nothing is written to disk.
    /// </summary>
    private async Task ResolveManualCore(PitData? devicePit, CancellationToken ct)
    {
        List<ManualRowViewModel> rows = ManualRows.Where(r => r.IsComplete).ToList();

        if (rows.Count == 0)
            throw new InvalidOperationException("No partition rows are filled in.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ManualRowViewModel row in rows)
        {
            if (!seen.Add(row.PartitionName.Trim()))
            {
                Report(OdinLevel.Warning, row.PartitionName.Trim() +
                    " appears more than once; the later row wins on the device.");
            }
        }

        PitData pit = devicePit ?? _devicePit ?? throw new InvalidOperationException(
            "The device PIT is not available.");

        await DisposePackageAsync().ConfigureAwait(true);
        DeleteBundleWorkspace();

        SetPhase("measuring images");
        ProgressIndeterminate = true;

        var targets = new List<ManualTarget>(rows.Count);

        foreach (ManualRowViewModel row in rows)
        {
            ct.ThrowIfCancellationRequested();

            FirmwareMember member = await FirmwarePackage
                .OpenLooseImageAsync(row.FilePath.Trim(), Log.Sink, ct).ConfigureAwait(true);

            targets.Add(new ManualTarget(row.PartitionName.Trim(), member));
        }

        FlashOptions options = CurrentFlashOptions();

        SetPhase("resolving partitions");
        FlashPlan plan = await Task.Run(
            () => FlashPlanner.BuildManual(targets, pit, options), ct).ConfigureAwait(true);

        _plan = plan;
        ApplyPlan(plan);
        ProgressIndeterminate = false;
        SetPhase("idle");
    }

    // The whole of Resolve, as a plain method: Start calls it and there
    // is no Resolve button any more.
    /// <summary>
    /// Build the plan. <paramref name="devicePit"/> is passed in rather than
    /// read here, because reading it needs a session and a session costs a
    /// handshake. See the remarks on StartAsync.
    /// </summary>
    private async Task ResolveCore(PitData? devicePit, CancellationToken ct)
    {
        if (IsManualMode)
        {
            await ResolveManualCore(devicePit, ct).ConfigureAwait(true);
            return;
        }

        // Ticked AND filled. An untick is a real exclusion, not a greyed-out
        // row: a user who unticks CP after importing a bundle gets a plan with
        // no CP in it, and the CP archive is never extracted either.
        List<FirmwareSlotViewModel> chosen = Slots.Where(s => s.CountsTowardFlash).ToList();

        if (chosen.Count == 0 && !PitSlot.HasPath)
            throw new InvalidOperationException("No firmware file has been chosen.");

        List<FirmwareSlotViewModel> skipped = Slots
            .Where(s => s.HasPath && !s.IsSelected)
            .ToList();

        if (skipped.Count > 0)
        {
            Report(OdinLevel.Info, "Skipping " +
                string.Join(", ", skipped.Select(s => s.DisplayLabel)) + ": not selected.");
        }

        var sources = new List<FirmwareSource>(chosen.Count);

        // Rows the user filled by hand are already on disk.
        foreach (FirmwareSlotViewModel row in chosen.Where(r => !r.IsFromBundle))
            sources.Add(new FirmwareSource(row.Slot, row.FilePath.Trim()));

        PitData? localPit = null;
        if (PitSlot.HasPath)
        {
            string pitPath = PitSlot.FilePath.Trim();
            localPit = await Task.Run(() => LoadPitFile(pitPath), ct).ConfigureAwait(true);
            Report(OdinLevel.Info, "Local PIT loaded from " + pitPath + ": " +
                PitTextFormatter.FormatHeaderLine(localPit));
        }

        await DisposePackageAsync().ConfigureAwait(true);
        DeleteBundleWorkspace();

        string tempRoot = Path.Combine(Path.GetTempPath(), "enneaodin");

        // Rows filled from the bundle are extracted out of the zip first, in
        // one forward-only pass each, into a workspace of their own.
        List<BundleEntry> fromBundle = chosen
            .Where(r => r.IsFromBundle)
            .Select(r => r.BundleEntry!)
            .ToList();

        if (fromBundle.Count > 0)
        {
            FirmwareBundle bundle = _bundle ?? throw new InvalidOperationException(
                "Rows are filled from a bundle but no bundle is open. Import it again.");

            long needed = FirmwareBundle.ExtractedBytes(fromBundle);

            Directory.CreateDirectory(tempRoot);
            Report(OdinLevel.Info, WorkspaceStorage.RequireFreeSpace(
                tempRoot, needed, "extracting " + fromBundle.Count + " archive(s) from the bundle"));

            string workspace = Path.Combine(
                tempRoot, "enneaodin-bundle-" + Guid.NewGuid().ToString("N"));

            SetPhase("extracting the bundle");
            ProgressIndeterminate = false;

            var extractProgress = new SyncProgress<BundleProgress>(p =>
                Dispatcher.UIThread.Post(() => ApplyBundleProgress(p)));

            // Recorded BEFORE the extraction, not after. Set afterwards, a
            // cancelled or failed extraction left gigabytes of half-written
            // archives in a directory nothing knew about, until the startup
            // sweep found it half an hour later.
            _bundleWorkspace = workspace;
            _bundleClaim = WorkspaceStorage.Claim(workspace);

            IReadOnlyList<FirmwareSource> extracted;

            try
            {
                extracted = await bundle
                    .ExtractAsync(fromBundle, workspace, extractProgress, ct).ConfigureAwait(true);
            }
            catch
            {
                // Immediately, rather than leaving it for the next resolve.
                DeleteBundleWorkspace();
                throw;
            }

            sources.AddRange(extracted);
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "Every firmware row is either empty or unticked. Tick at least one row that " +
                "names an archive, or supply a PIT and tick nothing to check a layout only.");
        }

        SetPhase(StreamImages ? "indexing and measuring firmware" : "unpacking firmware");
        ProgressIndeterminate = true;

        // STREAMED, not spooled. The archives are indexed and every compressed
        // member is decoded to a counter on as many cores as the machine has,
        // which is what tells the protocol each image's exact size; then the
        // flash reads each image out of its archive as the device asks for it.
        // Nothing is written to disk, so there is no free-space requirement
        // here at all - the one above covers extracting archives OUT of a zip,
        // which is a different thing and is still real.
        //
        // What this replaces wrote every member of every archive to a
        // temporary tree first. On this firmware that was roughly 7 GiB
        // written and read back before the handset saw a byte, and the 5.5 GiB
        // super.img was the bulk of it.
        if (StreamImages)
        {
            _package = await FirmwarePackage
                .OpenStreamingAsync(sources, tempRoot, Log.Sink, VerifyDigests, ct)
                .ConfigureAwait(true);
        }
        else
        {
            // The old path, kept reachable. It writes every member of every
            // archive to the temporary tree, so it needs room for all of them;
            // the archives' own sizes are the figure the decompressed members
            // will exceed, so this is a floor and is reported as one.
            long archiveBytes = 0;

            foreach (FirmwareSource source in sources)
            {
                try
                {
                    var info = new FileInfo(source.Path);

                    if (info.Exists)
                        archiveBytes += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Reported by the unpacker with the real reason; this
                    // figure is for the log and must not fail the resolve.
                }
            }

            if (archiveBytes > 0)
            {
                Report(OdinLevel.Info, WorkspaceStorage.RequireFreeSpace(
                    tempRoot, archiveBytes,
                    "extracting " + sources.Count + " archive(s) to the temporary tree"));
            }

            Report(OdinLevel.Warning,
                "Streaming is off: every image will be decompressed to a temporary directory " +
                "before anything is sent, and deleted afterwards.");

            _package = await FirmwarePackage
                .OpenAsync(sources, tempRoot, Log.Sink, ct).ConfigureAwait(true);
        }

        FlashOptions options = CurrentFlashOptions();
        FirmwarePackage package = _package;
        PitData pit = devicePit ?? _devicePit ?? throw new InvalidOperationException(
            "The device PIT is not available.");

        SetPhase("resolving partitions");
        FlashPlan plan = await Task.Run(
            () => FlashPlanner.Build(package, pit, options, localPit), ct).ConfigureAwait(true);

        _plan = plan;
        ApplyPlan(plan);
        ProgressIndeterminate = false;
        SetPhase("idle");
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunExclusiveAsync("Flash", async ct =>
    {
        // ONE SESSION for the whole flash, which is what FlashAction.cpp does:
        // open, begin, read the PIT, write, end. It used to be two. Resolve
        // called EnsureDevicePitAsync, which opened a session of its own to
        // download the PIT, ended it and released the interface; the flash
        // then opened a second session to do the writing.
        //
        // That cost two handshakes, and on a real SM-A166U the handset answers
        // the ODIN handshake ONCE per entry into download mode. The first
        // session got it, spent it on the PIT, and the second session could
        // never open: "Failed to send handshake" with the send going through
        // and no reply, over and over, which is exactly how a device that has
        // already handshaken answers a redundant one.
        //
        // So the firmware is prepared first, with no device involved at all,
        // and the plan is built inside the session that then does the writing.
        FlashOptions options = CurrentFlashOptions();

        DeviceItemViewModel device = RequireDevice();
        byte bus = device.Record.BusNumber;
        byte address = device.Record.DeviceAddress;
        OdinSessionOptions sessionOptions = CurrentSessionOptions();
        IProgress<OdinEvent> log = Log.Sink;

        var progress = new SyncProgress<FlashProgress>(p =>
            Dispatcher.UIThread.Post(() => ApplyProgress(p)));

        ProgressIndeterminate = true;
        SetPhase("opening the session");

        bool resumeRefused = false;
        OdinSession session = await Task.Run(() => OdinSession
            .OpenAndBeginAsync(bus, address, sessionOptions, log, ct)).ConfigureAwait(true);

        FlashOutcome outcome;

        try
        {
            resumeRefused = session.ResumeRefused;

            // Inside the session, so the handshake it cost is the one the
            // writing uses.
            PitData? devicePit = _devicePit;

            if (devicePit is null)
            {
                SetPhase("reading the device PIT");
                devicePit = await session.DownloadPitAsync(log, ct).ConfigureAwait(true);
                _devicePit = devicePit;
                _sessionRows = DeviceInformation.FromSession(session);
                RebuildInfoRows();
            }

            await ResolveCore(devicePit, ct).ConfigureAwait(true);

            FlashPlan plan = _plan ?? throw new InvalidOperationException(
                "The plan could not be built.");

            if (!plan.IsExecutable)
            {
                throw new InvalidOperationException(
                    "The plan has unresolved images and cannot be run.");
            }

            // No confirmation dialogue. Start is the confirmation: the plan is
            // in the log, nothing is ticked by default, and Stop is reachable
            // throughout.
            foreach (string line in BuildStartConfirmation(plan, options)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Report(OdinLevel.Info, line.TrimEnd());
            }

            ProgressIndeterminate = true;
            SetPhase("starting");

            outcome = await Task.Run(async () =>
            {
                var runner = new FlashRunner(session, log);
                FlashOutcome result = await runner.RunAsync(plan, options, progress, ct)
                                                  .ConfigureAwait(false);

                if (!result.Cancelled)
                {
                    // BridgeManager.cpp:560-614. A cancelled run deliberately
                    // skips this: the session stays open, which is what the
                    // Stop dialogue warned about.
                    await session.EndSessionAsync(options.RebootWhenFinished,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return result;
            }, ct).ConfigureAwait(true);
        }
        finally
        {
            session.Dispose();
        }

        _resumeRefused |= resumeRefused;

        // The handset is still in download mode unless a clean end-of-session
        // rebooted it.
        _deviceLeftInDownloadMode = !(outcome.Success && options.RebootWhenFinished);
        SessionText = _deviceLeftInDownloadMode
            ? (_resumeRefused
                ? "closed, device left in download mode; the next operation will handshake again"
                : "closed, device left in download mode; the next operation will resume")
            : "closed, device rebooting";

        ProgressIndeterminate = false;
        await ApplyOutcomeAsync(outcome).ConfigureAwait(true);
    });

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunGuardedAsync("Stop", StopOperationAsync);

    private async Task StopOperationAsync()
    {
        CancellationTokenSource? cancel = _cancel;

        if (cancel is null || cancel.IsCancellationRequested)
            return;

        bool confirmed = await RequireConfirm().ConfirmAsync("Stop",
            "Stop the operation in progress?\n\n" +
            "If a partition is being written, interrupting it abandons an open session with a " +
            "partially written partition, and the device may not boot afterwards.\n\n" +
            "Cancellation takes effect at the next packet boundary, so it can take up to the " +
            "current transfer timeout: about 3 seconds normally, and up to 2 minutes while " +
            "waiting for an end-of-sequence acknowledgement.",
            "Stop").ConfigureAwait(true);

        if (!confirmed)
            return;

        Report(OdinLevel.Warning, "Stop requested by user; cancelling at the next packet boundary.");

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished between the confirmation and the cancel.
        }
    }

    /// <summary>
    /// Odin's Reset button: put the form back to its defaults.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private Task ResetAsync() => RunGuardedAsync("Reset", ResetFormAsync);

    private async Task ResetFormAsync()
    {
        if (!IsFormDirty)
        {
            Report(OdinLevel.Info, "Reset: the form is already at its defaults; nothing to do.");
            return;
        }

        bool confirmed = await RequireConfirm().ConfirmAsync("Reset",
            "Clear the chosen files and put every option back to its default?\n\n" +
            BuildResetSummary() + "\n" +
            "This changes nothing on the device and keeps the log.",
            "Reset").ConfigureAwait(true);

        if (!confirmed)
        {
            Report(OdinLevel.Info, "Reset cancelled by user.");
            return;
        }

        foreach (FirmwareSlotViewModel slot in Slots)
        {
            slot.Clear();
            slot.IsSelected = false;
        }

        PitSlot.Clear();

        if (BundleLoaded)
            ClearBundle();

        ManualRows.Clear();
        OnPropertyChanged(nameof(ManualSummary));

        _filePit = null;
        _filePitPath = null;
        RebuildInfoRows();

        SetCscMode(CscMode.Csc);

        RebootWhenFinished = true;
        Repartition = false;
        TFlash = false;
        SkipSizeCheck = false;
        ResumeSession = false;
        WaitForDevice = false;
        VerboseLog = false;
        StreamImages = true;
        VerifyDigests = false;

        InvalidatePlan("Form reset.");
        Report(OdinLevel.Info, "Reset: files cleared and options returned to their defaults. " +
                               "Nothing was sent to the device.");
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        Report(OdinLevel.Info, "Log cleared.");
    }

    /// <summary>
    /// Version, licence and the attribution the vendored Heimdall's MIT
    /// licence requires be retained. This window has no About page, so the
    /// notice lives in a dialogue reachable from the device section; it is not
    /// optional and it is not allowed to go missing.
    /// </summary>
    [RelayCommand]
    private Task ShowAboutAsync() => RunGuardedAsync("About",
        () => RequireConfirm().ShowMessageAsync("About EnneaOdin", AboutText));

    /// <summary>
    /// For the three commands that show a modal dialogue outside the
    /// one-operation-at-a-time lock. They must not stack two dialogues, and
    /// must not let an exception escape into an unobserved task. Everything
    /// that opens a session goes through <see cref="RunExclusiveAsync"/>.
    /// </summary>
    private async Task RunGuardedAsync(string what, Func<Task> body)
    {
        if (_dialogOpen)
            return;

        _dialogOpen = true;

        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(OdinLevel.Error, what + " failed: " + ex.Message);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        IFilePicker picker = RequirePicker();

        string suggested = "enneaodin-log-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";

        string? path = await picker.SaveFileAsync("Save log", suggested, "txt").ConfigureAwait(true);

        if (path is null)
            return;

        try
        {
            await File.WriteAllTextAsync(path, Log.SnapshotText()).ConfigureAwait(true);
            Report(OdinLevel.Info, "Log written to " + path + ".");
        }
        catch (IOException ex)
        {
            Report(OdinLevel.Error, "Could not write the log: " + ex.Message);
            await RequireConfirm().ShowMessageAsync("Save log", ex.Message).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            Report(OdinLevel.Error, "Could not write the log: " + ex.Message);
            await RequireConfirm().ShowMessageAsync("Save log", ex.Message).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------ can-execute

    private bool CanRefresh() => !IsBusy;

    private bool CanOperate()
    {
        if (IsBusy || !LibUsbAvailable)
            return false;

        DeviceItemViewModel? device = SelectedDevice;

        return device is not null
               && device.Record.IsDownloadMode
               && UsbAccess.CanAttemptClaim(device.Record.Access);
    }

    private bool CanSavePit() => !IsBusy && _devicePit is not null;

    /// <summary>
    /// Importing a bundle needs no device and no libusb: it reads a zip index.
    /// Keeping it available with nothing attached is the point - the firmware
    /// can be chosen while the handset is still booting into download mode.
    /// </summary>
    private bool CanImportBundle() => !IsBusy;

    private bool CanClearBundle() => !IsBusy && BundleLoaded;

    /// <summary>
    /// The CSC choice only means something while a bundle is open: it decides
    /// which of the two CSC archives fills the row. With a hand-picked file
    /// there is nothing to choose between.
    /// </summary>
    private bool CanChangeCscMode() => !IsBusy && BundleLoaded;

    /// <summary>Parsing a PIT file needs no device either.</summary>
    private bool CanLoadPitFile() => !IsBusy && PitSlot.HasPath && !PitSlot.IsFromBundle;

    // Start resolves first, so it does not wait on a plan that a separate
    // button had to build. What it needs is a device and something chosen.
    private bool CanStart() =>
        CanOperate() && (IsManualMode
            ? ManualRows.Any(r => r.IsComplete)
            : Slots.Any(s => s.CountsTowardFlash));

    private bool CanStop() => IsBusy && _cancel is not null;

    /// <summary>
    /// Reset needs no device and no libusb: it only edits this window's own
    /// form. The one thing it must not do is reach into a run in progress.
    /// </summary>
    private bool CanReset() => !IsBusy;

    /// <summary>
    /// True when the form says something other than what it said at startup,
    /// which is what makes Reset worth confirming.
    /// </summary>
    private bool IsFormDirty =>
        Slots.Any(s => s.HasPath)
        || Slots.Any(s => s.IsSelected)
        || PitSlot.HasPath
        || BundleLoaded
        || _cscMode != CscMode.Csc
        || !RebootWhenFinished
        || Repartition
        || TFlash
        || SkipSizeCheck
        || ResumeSession
        || WaitForDevice
        || VerboseLog
        || !StreamImages
        || VerifyDigests
        || ManualRows.Count > 0;

    /// <summary>
    /// What the Reset confirmation says is about to be discarded, so the
    /// dialogue names the files rather than asking in the abstract.
    /// </summary>
    private string BuildResetSummary()
    {
        var text = new StringBuilder();

        if (BundleLoaded)
            text.Append("Closes the bundle: ").Append(BundlePath).Append('\n');

        if (ManualRows.Count > 0)
        {
            text.Append("Clears ").Append(ManualRows.Count)
                .Append(" manual partition row(s)").Append('\n');
        }

        foreach (FirmwareSlotViewModel slot in Slots.Append(PitSlot).Where(s => s.HasPath))
        {
            text.Append("Clears ").Append(slot.DisplayLabel).Append(": ")
                .Append(slot.IsFromBundle ? slot.SourceText : slot.FilePath.Trim())
                .Append('\n');
        }

        if (text.Length == 0)
            text.Append("No file is chosen; only the options change.\n");

        return text.ToString();
    }

    // --------------------------------------------------------- the shared run

    /// <summary>
    /// Every command's body runs through here: one operation at a time, one
    /// cancellation source per run, cancellation reported as cancellation, and
    /// a hard failure logged and shown rather than swallowed.
    /// </summary>
    private async Task RunExclusiveAsync(string what, Func<CancellationToken, Task> body)
    {
        if (IsBusy)
            return;

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource();

        _cancel = cts;
        _inFlight = finished;
        IsBusy = true;

        try
        {
            await body(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Reported from the token, never from whichever exception happened
            // to surface, and the progress figure is left where it stopped.
            Report(OdinLevel.Warning, what + " cancelled by user.");
            SetPhase(what + " cancelled by user");
        }
        catch (Exception ex)
        {
            // Terminal, and said once. There used to be a dialogue here as
            // well, which had to be dismissed before the log underneath it
            // could even be read.
            Report(OdinLevel.Error, what + " failed: " + ex.Message);
            Report(OdinLevel.Error, what + " ABORTED. Nothing further will be attempted.");
            SetPhase(what + " FAILED - aborted");

            ProgressIndeterminate = false;
        }
        finally
        {
            _cancel = null;
            _inFlight = null;
            IsBusy = false;
            finished.TrySetResult();
        }
    }

    private sealed record SessionRun<T>(T Value, bool LeftInDownloadMode, string Description);

    /// <summary>
    /// Open a session on the selected device, begin it, run one body, end it
    /// and dispose it, all on one worker thread.
    /// </summary>
    private async Task<T> WithSessionAsync<T>(bool reboot, CancellationToken ct,
        Func<OdinSession, CancellationToken, Task<T>> body)
    {
        DeviceItemViewModel device = RequireDevice();
        byte bus = device.Record.BusNumber;
        byte address = device.Record.DeviceAddress;
        OdinSessionOptions options = CurrentSessionOptions();
        IProgress<OdinEvent> log = Log.Sink;

        SessionRun<T> run;
        bool opened = false;
        bool refused = false;

        try
        {
            run = await Task.Run(async () =>
            {
                OdinSession session = await OdinSession
                    .OpenAndBeginAsync(bus, address, options, log, ct).ConfigureAwait(false);

                refused = session.ResumeRefused;

                // Past the handshake AND the session start: from here a failure
                // leaves the handset in download mode.
                opened = true;

                try
                {
                    T value = await body(session, ct).ConfigureAwait(false);
                    await session.EndSessionAsync(reboot, ct).ConfigureAwait(false);

                    return new SessionRun<T>(value,
                                             session.Tracker.DeviceLeftInDownloadMode,
                                             session.Tracker.Describe());
                }
                finally
                {
                    // Release, re-attach the Linux kernel driver, close. No
                    // end-session packet: ending a session is explicit.
                    session.Dispose();
                }
            }, ct).ConfigureAwait(true);
        }
        catch
        {
            // ONLY if the handshake got through. This used to be set
            // unconditionally, so a failed ODIN/LOKE handshake marked the
            // device as having an open session, and every later attempt
            // resumed and skipped the handshake - which can never work. It
            // showed up on the first real handset as "Protocol initialisation
            // failed!" followed by "Resuming an open session" on the retry.
            _resumeRefused |= refused;
            _deviceLeftInDownloadMode = opened;

            SessionText = opened
                ? "faulted; the device is probably still in download mode"
                : "not opened; the next attempt will handshake again";

            throw;
        }

        _resumeRefused |= refused;
        _deviceLeftInDownloadMode = run.LeftInDownloadMode;
        SessionText = run.Description;
        return run.Value;
    }

    private OdinSessionOptions CurrentSessionOptions() => new(
        Resume: !_resumeRefused && (ResumeSession || _deviceLeftInDownloadMode),
        UsbLogLevel: UsbLogLevelValue,
        VerboseTransport: VerboseLog);

    private FlashOptions CurrentFlashOptions() => new(
        RebootWhenFinished: RebootWhenFinished,
        Repartition: Repartition,
        TFlash: TFlash,
        SkipSizeCheck: SkipSizeCheck,
        ResumeSession: !_resumeRefused && (ResumeSession || _deviceLeftInDownloadMode),
        WaitForDevice: WaitForDevice,
        UsbLogLevel: UsbLogLevelValue,
        VerboseLog: VerboseLog);

    // One switch, three effects: debug lines in the log, Heimdall's own
    // --verbose, and libusb's log level. There used to be three separate
    // controls for what is one question.
    private int UsbLogLevelValue => VerboseLog
        ? OdinConstants.UsbLogLevelInfo
        : OdinConstants.UsbLogLevelError;

    // ------------------------------------------------------------- enumeration

    private void StartPolling()
    {
        if (_pollTask is not null)
            return;

        _pollCancel = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCancel.Token);
    }

    /// <summary>
    /// A cancellable re-enumeration owned by the window and disposed with it.
    /// Never reads string descriptors, so a poll cannot open a device, and it
    /// stands aside completely while an operation is running.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (IsBusy)
                continue;

            try
            {
                UsbEnumerationResult result = await _pollEnumerator.EnumerateAsync(false, ct)
                                                                   .ConfigureAwait(true);
                ApplyEnumeration(result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Do NOT return. This used to kill the poll for the rest of
                // the session on one bad iteration, so the device list froze
                // and nothing said why. One failure is reported once, and the
                // loop keeps going; a persistent fault reports once and then
                // stays quiet rather than filling the log every two seconds.
                if (!string.Equals(_lastPollError, ex.Message, StringComparison.Ordinal))
                {
                    _lastPollError = ex.Message;
                    Report(OdinLevel.Error, "Device poll failed: " + ex.Message);
                }

                continue;
            }

            _lastPollError = null;
        }
    }

    private void ApplyEngineStatus(OdinEngineStatus engine)
    {
        EngineAvailable = engine.Available;

        EngineText = engine.Available
            ? "Heimdall " + (engine.HeimdallVersion ?? "?") + ", ABI " +
              engine.AbiVersion.ToString(CultureInfo.InvariantCulture)
            : "not loaded";

        if (engine.Available)
        {
            EngineDetail = engine.Summary;
            Report(OdinLevel.Debug, engine.Summary);
            return;
        }

        var detail = new StringBuilder();
        detail.Append(engine.Summary);

        if (engine.Attempted.Count > 0)
        {
            detail.Append("\nTried: ");
            detail.Append(string.Join(", ", engine.Attempted));
        }

        EngineDetail = detail.ToString();

        Report(OdinLevel.Error, engine.Summary);

        foreach (string candidate in engine.Attempted)
            Report(OdinLevel.Debug, "Engine candidate tried: " + candidate);
    }

    private void ApplyLibUsbStatus(LibUsbStatus status)
    {
        _libUsb = status;
        LibUsbAvailable = status.Available;
        LibUsbText = status.Summary;
    }

    /// <summary>
    /// Fold one enumeration into the list without destroying the user's
    /// selection: existing rows are updated in place, missing ones removed,
    /// new ones added, and the selection re-found by bus and address.
    /// </summary>
    private void ApplyEnumeration(UsbEnumerationResult result)
    {
        ApplyLibUsbStatus(result.LibUsb);
        _diagnostic = result.Kind;
        _diagnosticText = result.Diagnostic;

        var fresh = new List<UsbDeviceRecord>(result.DownloadModeDevices);
        fresh.AddRange(result.OtherSamsungDevices);

        string? wantedKey = SelectedDevice?.Key;

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!fresh.Any(r => string.Equals(r.Key, Devices[i].Key, StringComparison.Ordinal)))
                Devices.RemoveAt(i);
        }

        foreach (UsbDeviceRecord record in fresh)
        {
            DeviceItemViewModel? existing = Devices
                .FirstOrDefault(d => string.Equals(d.Key, record.Key, StringComparison.Ordinal));

            if (existing is null)
            {
                Devices.Add(new DeviceItemViewModel(record));
                Report(OdinLevel.Debug, "Device list gained " + record.DisplayName + ".");
            }
            else
            {
                existing.Record = Merge(existing.Record, record);
            }
        }

        DeviceItemViewModel? selection = wantedKey is null
            ? null
            : Devices.FirstOrDefault(d => string.Equals(d.Key, wantedKey, StringComparison.Ordinal));

        if (selection is null)
        {
            // Auto-select only when nothing is selected or the selection went
            // away, and only a download-mode device.
            selection = Devices.FirstOrDefault(d => d.Record.IsDownloadMode);
        }

        if (!ReferenceEquals(selection, SelectedDevice))
            SelectedDevice = selection;

        HasMultipleDevices = Devices.Count > 1;
        UpdateDeviceBox();
        UpdateStatusText();
        RefreshCommandStates();

        if (SelectedDevice is null)
            ShowDiagnosticRows();

        MaybeAutoReadAsync();
    }

    private void UpdateDeviceBox()
    {
        DeviceItemViewModel? device = SelectedDevice;

        if (device is null)
        {
            DeviceSummary = LibUsbAvailable
                ? "Waiting for a device..."
                : "libusb is not available.";
            DeviceDetail = string.Empty;
            return;
        }

        UsbDeviceRecord record = device.Record;

        // The model, when the handset has told us one. The product string is
        // only read by an open, which is what the automatic read below does.
        string? model = record.Product;

        DeviceSummary = string.IsNullOrWhiteSpace(model)
            ? record.DisplayName
            : model + "   " + record.DisplayName;

        DeviceDetail = record.StatusWord;
    }

    /// <summary>
    /// Read the handset's descriptors the moment it appears, once per device.
    ///
    /// The two-second poll deliberately never opens a device, so it learns
    /// nothing but the identifiers. Pressing a button to find out what is
    /// plugged in is a button nobody should have to press, so the first time
    /// a claimable download-mode device shows up this does it. Opening for
    /// descriptors starts no session and writes nothing.
    /// </summary>
    private void MaybeAutoReadAsync()
    {
        DeviceItemViewModel? device = SelectedDevice;

        if (device is null)
        {
            _autoReadKey = null;
            return;
        }

        if (string.Equals(_autoReadKey, device.Key, StringComparison.Ordinal))
            return;

        if (IsBusy || !LibUsbAvailable || !device.Record.IsDownloadMode)
            return;

        if (!UsbAccess.CanAttemptClaim(device.Record.Access))
            return;

        _autoReadKey = device.Key;

        // The command owns its own exclusion, its own cancellation and its own
        // failure reporting; a failed automatic read is a logged line, not a
        // dialogue.
        if (ReadDeviceInfoCommand.CanExecute(null))
            ReadDeviceInfoCommand.Execute(null);
    }

    /// <summary>
    /// A poll does not read string descriptors, so a fresh record's strings
    /// are null. Those are not a reason to forget what an earlier read of the
    /// same attachment reported: the values were read from this device at this
    /// bus and address, and nothing here is invented.
    /// </summary>
    private static UsbDeviceRecord Merge(UsbDeviceRecord known, UsbDeviceRecord fresh)
    {
        if (fresh.Manufacturer is not null || fresh.Product is not null || fresh.SerialNumber is not null)
            return fresh;

        if (known.Manufacturer is null && known.Product is null && known.SerialNumber is null)
            return fresh;

        return fresh with
        {
            Manufacturer = known.Manufacturer,
            Product = known.Product,
            SerialNumber = known.SerialNumber
        };
    }

    private void UpdateStatusText()
    {
        DeviceItemViewModel? device = SelectedDevice;

        // A selected device describes itself: its own three-state access word
        // is more specific than the enumeration-wide diagnosis. The diagnosis
        // is the status only when there is nothing selected to describe.
        StatusText = device is not null
            ? device.Record.StatusWord
            : _diagnostic switch
            {
                UsbDiagnostic.LibUsbUnavailable => "libusb not found",
                UsbDiagnostic.LibUsbFailed => "libusb failed to start",
                UsbDiagnostic.NoDevice => "not connected",
                UsbDiagnostic.SamsungNotInDownloadMode => "present, not in download mode",
                UsbDiagnostic.DownloadModeNotClaimable => "download mode, not claimable",
                _ => "not connected"
            };

        // Folded into StatusMessage rather than published on its own: the
        // window prints that one sentence, which already carries the status
        // word, the device, this detail and the libusb install command.
        string detail = device?.Record.AccessDetail ?? _diagnosticText ?? string.Empty;

        StatusLevel = LevelFor(device);
        StatusMessage = BuildStatusMessage(device, detail);
    }

    /// <summary>
    /// The status level, from the same two facts the status word uses.
    /// </summary>
    private DeviceStatusLevel LevelFor(DeviceItemViewModel? device)
    {
        if (_diagnostic is UsbDiagnostic.LibUsbUnavailable or UsbDiagnostic.LibUsbFailed)
            return DeviceStatusLevel.Error;

        if (_libUsb is not null && !_libUsb.Available)
            return DeviceStatusLevel.Error;

        if (device is null)
        {
            return _diagnostic switch
            {
                UsbDiagnostic.SamsungNotInDownloadMode => DeviceStatusLevel.Warning,
                UsbDiagnostic.DownloadModeNotClaimable => DeviceStatusLevel.Warning,
                _ => DeviceStatusLevel.Info
            };
        }

        if (!device.Record.IsDownloadMode)
            return DeviceStatusLevel.Warning;

        return device.Record.Access switch
        {
            UsbAccessState.Claimable => DeviceStatusLevel.Ready,
            UsbAccessState.NotPresent => DeviceStatusLevel.Info,
            _ => DeviceStatusLevel.Warning
        };
    }

    private string BuildStatusMessage(DeviceItemViewModel? device, string detail)
    {
        var text = new StringBuilder();

        text.Append(StatusText);

        if (device is not null)
            text.Append(". ").Append(device.Record.DisplayName);

        if (!string.IsNullOrWhiteSpace(detail))
            text.Append(". ").Append(detail.TrimEnd('.'));

        if (_libUsb is not null && !_libUsb.Available)
        {
            text.Append(". libusb ").Append(_libUsb.Summary)
                .Append(". Install it with: ").Append(_libUsb.InstallCommand);
        }

        text.Append('.');
        return text.ToString();
    }

    // -------------------------------------------------------- information rows

    private void RebuildInfoRows()
    {
        DeviceItemViewModel? device = SelectedDevice;

        if (device is null)
        {
            ShowDiagnosticRows();
            return;
        }

        InfoRows.Clear();

        foreach (InfoField field in DeviceInformation.FromUsb(device.Record))
            InfoRows.Add(InfoRowViewModel.From(field));

        foreach (InfoField field in _sessionRows)
            InfoRows.Add(InfoRowViewModel.From(field));

        PartitionRows.Clear();

        if (_identity is not null)
        {
            foreach (InfoField field in _identity.Rows())
                InfoRows.Add(InfoRowViewModel.From(field));
        }

        if (_devicePit is not null)
        {
            foreach (InfoField field in DeviceInformation.FromPit(_devicePit))
                InfoRows.Add(InfoRowViewModel.From(field));

            foreach (InfoField field in DeviceInformation.PartitionTable(_devicePit))
                PartitionRows.Add(InfoRowViewModel.From(field));

            PartitionTableSource = "Read from the device: " +
                                   PitTextFormatter.FormatHeaderLine(_devicePit);
        }
        else if (_filePit is not null)
        {
            foreach (InfoField field in DeviceInformation.PartitionTable(_filePit))
                PartitionRows.Add(InfoRowViewModel.From(field));

            PartitionTableSource = "Parsed from the file \"" + (_filePitPath ?? "?") + "\": " +
                                   PitTextFormatter.FormatHeaderLine(_filePit);
        }
        else
        {
            PartitionRows.Add(new InfoRowViewModel("Partition table", "not read",
                "Press Read PIT for the device's own table, or choose a PIT file and press Load.",
                "no PIT has been downloaded or parsed in this session"));

            PartitionTableSource = "No partition table yet.";
        }
    }

    /// <summary>
    /// What the information panel shows when there is no device to describe:
    /// the typed diagnosis, in words, plus the libusb line and the install
    /// command when that is what is missing. Never a blank panel, and never a
    /// value that was not read from something.
    /// </summary>
    private void ShowDiagnosticRows()
    {
        InfoRows.Clear();
        PartitionRows.Clear();

        string word = _diagnostic switch
        {
            UsbDiagnostic.LibUsbUnavailable => "libusb not available",
            UsbDiagnostic.LibUsbFailed => "libusb failed to start",
            UsbDiagnostic.NoDevice => "no device",
            UsbDiagnostic.SamsungNotInDownloadMode => "not in download mode",
            UsbDiagnostic.DownloadModeNotClaimable => "not claimable",
            _ => "ready"
        };

        InfoRows.Add(new InfoRowViewModel("Status", word,
            _diagnosticText ?? "No enumeration has run yet.",
            "OdinCore.Usb.UsbEnumerationResult.Kind"));

        if (_libUsb is not null)
        {
            InfoRows.Add(new InfoRowViewModel("Status", "libusb", _libUsb.Summary,
                "OdinCore.Usb.LibUsbAvailability.Probe()"));

            if (!_libUsb.Available)
            {
                InfoRows.Add(new InfoRowViewModel("Status", "Install libusb with",
                    _libUsb.InstallCommand,
                    "OdinCore.Usb.UsbPlatform.LibUsbInstallCommand()"));
            }
        }

        PartitionRows.Add(new InfoRowViewModel("Partition table", "not read",
            "A partition table can only be read from an attached download-mode device.",
            "no device"));
    }

    // -------------------------------------------------------------------- plan

    // The plan goes into the log rather than its own panel: it is a list of
    // lines, the log is where a user already looks, and Save log carries it.
    private void ApplyPlan(FlashPlan plan)
    {
        foreach (string warning in plan.Warnings)
            Report(OdinLevel.Warning, warning);

        Report(OdinLevel.Info, string.Format(CultureInfo.InvariantCulture,
            "Plan: {0} partition(s), {1} bytes{2}",
            plan.Items.Count, plan.TotalBytes,
            plan.Repartition ? ", repartition" : string.Empty));

        foreach (FlashItem item in plan.Items)
        {
            Report(OdinLevel.Info, string.Format(CultureInfo.InvariantCulture,
                "  {0} <- {1} ({2} bytes)",
                item.Target.PartitionName, item.Source.ResolvedBaseName, item.Length));
        }

        foreach (string entry in plan.Unresolved)
            Report(OdinLevel.Error, "  " + entry + " -> no matching partition");

        if (plan.Unresolved.Count > 0)
        {
            Report(OdinLevel.Error, string.Format(CultureInfo.InvariantCulture,
                "{0} image(s) could not be matched. Start is refused.",
                plan.Unresolved.Count));
        }

        RefreshCommandStates();
    }

    private void InvalidatePlan(string why)
    {
        if (_plan is null)
            return;

        _plan = null;
        Report(OdinLevel.Debug, why);
        RefreshCommandStates();
    }

    private string BuildStartConfirmation(FlashPlan plan, FlashOptions options)
    {
        var text = new StringBuilder();

        text.Append("Device: ").Append(DeviceLabel()).Append('\n');
        text.Append("Session: ").Append(options.ResumeSession ? "resume" : "new handshake").Append('\n');
        text.Append('\n');
        text.Append("Partitions to write, in this order:\n");

        foreach (FlashItem item in plan.Items)
        {
            text.Append("  ").Append(item.Target.PartitionName).Append("  <-  ")
                .Append(item.Source.ResolvedBaseName).Append("  ")
                .Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes\n");
        }

        text.Append('\n');
        text.Append("Total on the wire: ")
            .Append(plan.WireTotalBytes.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes\n");
        text.Append('\n');
        text.Append("Options in force:\n");
        text.Append("  Reboot when finished: ").Append(options.RebootWhenFinished ? "yes" : "no").Append('\n');
        text.Append("  Repartition: ").Append(options.Repartition ? "yes" : "no").Append('\n');
        text.Append("  T-Flash: ").Append(options.TFlash ? "yes" : "no").Append('\n');
        text.Append("  Skip partition size check: ").Append(options.SizeCheckSkipped ? "yes" : "no").Append('\n');
        text.Append("  USB log level: ").Append(OdinConstants.UsbLogLevelName(options.UsbLogLevel)).Append('\n');
        text.Append("  Verbose log: ").Append(options.VerboseLog ? "yes" : "no").Append('\n');
        text.Append('\n');
        text.Append("Writing a wrong image can leave the device unable to boot.");

        return text.ToString();
    }

    private async Task ApplyOutcomeAsync(FlashOutcome outcome)
    {
        if (outcome.Cancelled)
        {
            SetPhase("cancelled by user");
            Report(OdinLevel.Warning, string.Format(CultureInfo.InvariantCulture,
                "Flash cancelled by user after {0} of {1} partition(s) and {2} bytes.",
                outcome.Completed.Count, outcome.Completed.Count + outcome.Failed.Count,
                outcome.BytesTransferred));
            return;
        }

        if (outcome.Success)
        {
            SetPhase("done");
            Report(OdinLevel.Info, "Flash complete: " + string.Join(", ", outcome.Completed) + ".");
            await RequireConfirm().ShowMessageAsync("Flash complete",
                string.Format(CultureInfo.InvariantCulture,
                    "{0} partition(s) written and acknowledged: {1} bytes.\n\n{2}",
                    outcome.Completed.Count, outcome.BytesTransferred,
                    outcome.DeviceLeftInDownloadMode
                        ? "The device was left in download mode."
                        : "The device was rebooted.")).ConfigureAwait(true);
            return;
        }

        SetPhase("FAILED - aborted");

        string message = outcome.Error ?? "The flash did not complete every planned partition.";

        // In the log, not a dialogue: a failure needs to be read next to the
        // lines that led to it, not on top of them.
        Report(OdinLevel.Error, "Flash FAILED: " + message);

        if (outcome.Failed.Count > 0)
            Report(OdinLevel.Error, "Failed partition(s): " + string.Join(", ", outcome.Failed) + ".");

        Report(OdinLevel.Error, "Flash ABORTED. Nothing further will be attempted.");
    }

    // ------------------------------------------------- throughput and the ETA
    //
    // Measured, not estimated. "Is this slow?" is a question about a number,
    // and the only honest way to answer it is to put the number on screen.
    //
    // A rolling window rather than an average since the start: the rate is not
    // constant - a small partition finishes in a blink and the handset pauses
    // between them to commit - and an average over the whole run hides that
    // behind a figure that only creeps.
    private const int RateWindowSeconds = 5;

    private readonly List<(DateTime When, long Bytes)> _rateSamples = new();

    private void ApplyProgress(FlashProgress progress)
    {
        ProgressIndeterminate = progress.Indeterminate;
        PartitionProgress = progress.PartitionPercent;
        ProgressValue = progress.OverallPercent;

        PartitionLabel = progress.PartitionName is null
            ? progress.Phase
            : string.Format(CultureInfo.InvariantCulture, "{0}  {1:0}%",
                progress.PartitionName, progress.PartitionPercent);

        TotalLabel = DescribeRate("flash", progress.OverallBytesDone,
                                  progress.OverallBytesTotal, progress.OverallPercent);

        ProgressText = PartitionLabel;
        PhaseText = progress.Phase;
    }

    /// <summary>
    /// Which run the rate samples belong to. The window must not span two
    /// phases: an extraction sample next to a flash sample produces a rate
    /// that is a ratio of unrelated numbers.
    /// </summary>
    private string? _ratePhase;

    /// <summary>
    /// "46%   28.3 MB/s   4m 12s left", or just the percentage until there is
    /// enough of a window to say anything about the rate.
    /// </summary>
    private string DescribeRate(string phase, long done, long total, int percentValue)
    {
        string percent = string.Format(CultureInfo.InvariantCulture, "{0:0}%", percentValue);

        if (!string.Equals(_ratePhase, phase, StringComparison.Ordinal))
        {
            _ratePhase = phase;
            _rateSamples.Clear();
        }

        if (total <= 0 || done <= 0)
        {
            _rateSamples.Clear();
            return percent;
        }

        DateTime now = DateTime.UtcNow;
        _rateSamples.Add((now, done));

        DateTime cutoff = now.AddSeconds(-RateWindowSeconds);

        // Drop what has fallen out of the window, keeping one sample from just
        // before it so a sparse report stream still has a span to divide by.
        //
        // This used to keep TWO samples unconditionally, which did not bound
        // the window at all: the handset pauses between partitions to commit,
        // those reports carry the same byte count, and the stale one stayed.
        // The next partition's bytes were then divided by the pause as well,
        // so a small partition after a long commit read as a fraction of the
        // real rate.
        int stale = 0;

        while (stale + 1 < _rateSamples.Count && _rateSamples[stale + 1].When < cutoff)
            stale++;

        if (stale > 0)
            _rateSamples.RemoveRange(0, stale);

        (DateTime When, long Bytes) oldest = _rateSamples[0];
        double seconds = (now - oldest.When).TotalSeconds;
        long moved = done - oldest.Bytes;

        // Still far outside the window, which means the transfer was paused
        // for longer than the window is wide. The span says nothing about the
        // rate now, so start again rather than divide by the pause.
        if (seconds > RateWindowSeconds * 2)
        {
            _rateSamples.Clear();
            _rateSamples.Add((now, done));
            return percent;
        }

        // Under a second of window, or nothing moved in it: say nothing rather
        // than a number that swings wildly.
        if (seconds < 1.0 || moved <= 0)
            return percent;

        double bytesPerSecond = moved / seconds;
        double megabytes = bytesPerSecond / (1024.0 * 1024.0);

        long remaining = total - done;
        var left = TimeSpan.FromSeconds(remaining / bytesPerSecond);

        return string.Format(CultureInfo.InvariantCulture, "{0}   {1:0.0} MB/s   {2} left",
                             percent, megabytes, Remaining(left));
    }

    private static string Remaining(TimeSpan left)
    {
        if (left.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m",
                                 (int)left.TotalHours, left.Minutes);
        }

        if (left.TotalMinutes >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s",
                                 (int)left.TotalMinutes, left.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}s", Math.Max(1, (int)left.TotalSeconds));
    }

    // -------------------------------------------------------------- slot rows

    private async Task BrowseSlotAsync(FirmwareSlotViewModel slot)
    {
        IFilePicker picker = RequirePicker();

        string? path = await picker
            .OpenFileAsync(slot.IsPitRow ? "Select a PIT file" : "Select the " + slot.Label + " file")
            .ConfigureAwait(true);

        if (path is null)
            return;

        slot.FilePath = path;

        // Picking a file for a row ticks that row. Choosing the BL file and
        // then finding BL unticked is a step nobody wants: the choosing WAS
        // the intent. A PIT row has no tick of its own, and nothing else is
        // touched, so this never selects a partition the user did not name.
        if (!slot.IsPitRow && !slot.IsSelected)
        {
            slot.IsSelected = true;
            Report(OdinLevel.Info, slot.DisplayLabel + " selected: " +
                System.IO.Path.GetFileName(path));
        }
    }

    // ------------------------------------------------- the handset's identity
    //
    // What the handset itself says, from the protocol's DVIF command. Shown at
    // the top of the DEVICE card rather than down in the Information list,
    // because the model and the carrier are the two things a user checks
    // before pressing Start.

    /// <summary>True once the handset has answered DVIF.</summary>
    public bool HasIdentity => _identity is not null;

    public string IdentityModel => _identity?.Model ?? "(unknown model)";

    /// <summary>
    /// The sales code, which is the carrier or region: CHA, XAA, OYN. Labelled
    /// "Carrier" because that is what it means to a user, while the protocol
    /// field is SALES.
    /// </summary>
    public string IdentityCarrier => _identity?.SalesCode ?? "-";

    /// <summary>
    /// CAPA, which the handset gives as a bare number of gigabytes.
    /// </summary>
    public string IdentityStorage =>
        _identity?.Capacity is string capacity && capacity.Length > 0
            ? (capacity.Any(char.IsLetter) ? capacity : capacity + " GB")
            : "-";

    public string IdentityFirmware => _identity?.FirmwareVersion ?? "-";

    public string IdentityDeviceId => _identity?.DeviceId ?? "-";

    public string IdentityUniqueNumber => _identity?.UniqueNumber ?? "-";

    public string IdentityVendor => _identity?.Vendor ?? "-";

    private void RaiseIdentityChanged()
    {
        OnPropertyChanged(nameof(HasIdentity));
        OnPropertyChanged(nameof(IdentityModel));
        OnPropertyChanged(nameof(IdentityCarrier));
        OnPropertyChanged(nameof(IdentityStorage));
        OnPropertyChanged(nameof(IdentityFirmware));
        OnPropertyChanged(nameof(IdentityDeviceId));
        OnPropertyChanged(nameof(IdentityUniqueNumber));
        OnPropertyChanged(nameof(IdentityVendor));
    }

    /// <summary>One line for the log: what a user actually wants to see.</summary>
    private static string Describe(DownloadModeIdentity identity)
    {
        var parts = new List<string>(4);

        if (identity.Model is not null) parts.Add(identity.Model);
        if (identity.SalesCode is not null) parts.Add(identity.SalesCode);
        if (identity.FirmwareVersion is not null) parts.Add(identity.FirmwareVersion);
        if (identity.Capacity is not null) parts.Add(identity.Capacity);

        return parts.Count > 0 ? string.Join(", ", parts) : identity.Raw;
    }

    private void OnSlotChanged()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectionErasesUserData));
        UpdateBundleSummary();
        InvalidatePlan("Firmware selection changed.");
        RefreshCommandStates();
    }

    // ------------------------------------------------------- bundle plumbing

    /// <summary>
    /// Fill every row the bundle has an archive for, and tick BL, AP, CP and
    /// CSC - what a firmware install writes. USERDATA is filled but not
    /// ticked: its only effect is to wipe the user partition.
    /// </summary>
    private void ApplyBundleToRows()
    {
        FirmwareBundle bundle = _bundle ?? throw new InvalidOperationException(
            "ApplyBundleToRows was called with no bundle open.");

        IReadOnlyList<BundleEntry> ticked = bundle.DefaultSelection(_cscMode);

        foreach (FirmwareSlotViewModel row in Slots)
        {
            BundleEntry? entry = row.Slot == FirmwareSlot.Csc
                ? bundle.PickCsc(_cscMode)
                : bundle.Entries.FirstOrDefault(e => e.Slot == row.Slot);

            if (entry is null)
            {
                if (row.IsFromBundle)
                    row.Clear();

                continue;
            }

            row.Fill(entry);
            row.IsSelected = ticked.Contains(entry);
        }

        UpdateBundleSummary();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectionErasesUserData));
        InvalidatePlan("Bundle imported.");
        RefreshCommandStates();
    }

    /// <summary>
    /// The one line under the import button: what the zip holds, what is
    /// ticked, and what the ticked rows will cost on disk. Every figure comes
    /// from the zip's central directory.
    /// </summary>
    private void UpdateBundleSummary()
    {
        FirmwareBundle? bundle = _bundle;

        if (bundle is null)
            return;

        long stored = bundle.Entries.Sum(e => e.CompressedLength);
        List<BundleEntry> ticked = Slots
            .Where(s => s.CountsTowardFlash && s.IsFromBundle)
            .Select(s => s.BundleEntry!)
            .ToList();

        BundleSummary = string.Format(CultureInfo.InvariantCulture,
            "{0} archive(s) in the zip, {1} stored. Selected {2}, which extract to {3}.",
            bundle.Entries.Count, WorkspaceStorage.Human(stored), ticked.Count,
            WorkspaceStorage.Human(FirmwareBundle.ExtractedBytes(ticked)));
    }

    /// <summary>
    /// Switch between the two CSC archives. The notifications are raised
    /// unconditionally, even when the mode did not change, because the two
    /// segments are ToggleButtons: clicking the one already selected toggles
    /// its own IsChecked off locally, and only a property change pushes the
    /// true value back into it.
    /// </summary>
    private void SetCscMode(CscMode mode)
    {
        bool changed = _cscMode != mode;
        _cscMode = mode;

        if (changed && _bundle is not null)
        {
            FirmwareSlotViewModel? cscRow = Slots.FirstOrDefault(s => s.Slot == FirmwareSlot.Csc);
            BundleEntry? entry = _bundle.PickCsc(mode);

            if (cscRow is not null && entry is not null)
            {
                bool wasTicked = cscRow.IsSelected;
                cscRow.Fill(entry);
                cscRow.IsSelected = wasTicked;
            }

            Report(OdinLevel.Info, "CSC mode set to " + (mode == CscMode.HomeCsc ? "HOME_CSC" : "CSC") +
                                   ". " + CscModeNote);
            UpdateBundleSummary();
        }

        OnPropertyChanged(nameof(IsCscMode));
        OnPropertyChanged(nameof(IsHomeCscMode));
        OnPropertyChanged(nameof(CscModeNote));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectionErasesUserData));
    }

    /// <summary>Extraction progress, as a percentage and a line of words.</summary>
    private void ApplyBundleProgress(BundleProgress progress)
    {
        PartitionProgress = progress.EntryPercent;
        ProgressValue = progress.TotalPercent;

        PartitionLabel = string.Format(CultureInfo.InvariantCulture, "{0}  {1:0}%",
                                       progress.Entry.Label, progress.EntryPercent);
        // BundleProgress names them the other way round: TotalBytes is what
        // has been done so far, Total is the whole selection.
        TotalLabel = DescribeRate("extract", progress.TotalBytes, progress.Total,
                                  (int)progress.TotalPercent);
        ProgressText = PartitionLabel;
    }

    private void DisposeBundle()
    {
        FirmwareBundle? bundle = _bundle;
        _bundle = null;

        bundle?.Dispose();
    }

    private void DeleteBundleWorkspace()
    {
        _bundleClaim?.Dispose();
        _bundleClaim = null;

        string? workspace = _bundleWorkspace;
        _bundleWorkspace = null;

        if (workspace is null || !Directory.Exists(workspace))
            return;

        try
        {
            Directory.Delete(workspace, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the workspace is less bad than masking whatever led here.
            Report(OdinLevel.Warning,
                "Could not delete the extracted bundle workspace \"" + workspace + "\": " + ex.Message);
        }
    }

    // ------------------------------------------------------------ bookkeeping

    private string DeviceLabel() => SelectedDevice?.Record.DisplayName ?? "no device";

    private DeviceItemViewModel RequireDevice() =>
        SelectedDevice ?? throw new InvalidOperationException("No device is selected.");

    private IFilePicker RequirePicker() =>
        _picker ?? throw new InvalidOperationException("The file picker is not available yet.");

    private IConfirmService RequireConfirm() =>
        _confirm ?? throw new InvalidOperationException("The dialogue service is not available yet.");

    private static PitData LoadPitFile(string path)
    {
        byte[] raw = File.ReadAllBytes(path);

        if (!PitData.TryUnpack(raw, out PitData? pit, out string? error) || pit is null)
            throw new InvalidOperationException("\"" + path + "\" is not a usable PIT file: " + error);

        return pit;
    }

    private void Report(OdinLevel level, string message) =>
        _ring.Add(new OdinEvent(DateTimeOffset.Now, level, message, "ui"));

    private void SetPhase(string phase)
    {
        PhaseText = phase;

        if (!IsBusy)
        {
            PartitionProgress = 0;
            PartitionLabel = phase;
        }
    }

    private void UpdateBlockedReason()
    {
        BlockedReason = NextStep();
        OnPropertyChanged(nameof(IsBlocked));
    }

    private string NextStep()
    {
        if (IsBusy)
            return string.Empty;                       // the phase line says what is running

        if (!LibUsbAvailable)
            return "libusb is not available, so no device can be opened.";

        if (!EngineAvailable)
            return "The protocol engine is not loaded.";

        DeviceItemViewModel? device = SelectedDevice;

        if (device is null)
            return "No device. Put the handset in download mode and connect it.";

        if (!device.Record.IsDownloadMode)
            return "The attached device is not in download mode.";

        if (!UsbAccess.CanAttemptClaim(device.Record.Access))
            return "The device cannot be claimed: " + device.Record.StatusWord + ".";

        bool chosen = IsManualMode
            ? ManualRows.Any(r => r.IsComplete)
            : Slots.Any(s => s.CountsTowardFlash);

        if (!chosen)
        {
            return IsManualMode
                ? "Add a partition row: a partition name and a file."
                : "Nothing is ticked. Choose firmware and tick what to flash.";
        }

        // Nothing about the plan here: Start builds it, so there is no state
        // between "something is chosen" and "it is running".
        return string.Empty;
    }

    private void RefreshCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ReadDeviceInfoCommand.NotifyCanExecuteChanged();
        ReadPitCommand.NotifyCanExecuteChanged();
        SavePitCommand.NotifyCanExecuteChanged();
        RebootDeviceCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        ClearBundleCommand.NotifyCanExecuteChanged();
        SelectCscCommand.NotifyCanExecuteChanged();
        SelectHomeCscCommand.NotifyCanExecuteChanged();
        LoadPitFileCommand.NotifyCanExecuteChanged();
        UpdateBlockedReason();
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommandStates();

    partial void OnLibUsbAvailableChanged(bool value)
    {
        UpdateDeviceBox();
        RefreshCommandStates();
    }

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        // A different device invalidates everything that was read from the
        // previous one. Nothing here may survive a selection change, or the
        // panel would describe one device with another's numbers.
        _devicePit = null;
        _sessionRows = Array.Empty<InfoField>();
        _deviceLeftInDownloadMode = false;
        _resumeRefused = false;
        _identity = null;
        RaiseIdentityChanged();
        SessionText = "closed";

        InvalidatePlan("Device selection changed.");
        RebuildInfoRows();
        UpdateStatusText();
        RefreshCommandStates();
    }

    partial void OnRepartitionChanged(bool value) =>
        InvalidatePlan("Repartition changed.");

    partial void OnTFlashChanged(bool value) =>
        InvalidatePlan("T-Flash changed.");

    partial void OnSkipSizeCheckChanged(bool value) =>
        InvalidatePlan("Size-check setting changed.");

    partial void OnVerboseLogChanged(bool value) => Log.ShowDebug = value;

    partial void OnStreamImagesChanged(bool value) =>
        InvalidatePlan("Image source changed.");

    partial void OnSelectedPageChanged(int value)
    {
        if (value == 0)
            IsManualMode = false;
        else if (value == 1)
            IsManualMode = true;
    }

    partial void OnIsManualModeChanged(bool value)
    {
        InvalidatePlan("Page changed.");
        RefreshCommandStates();
    }

    partial void OnVerifyDigestsChanged(bool value) =>
        InvalidatePlan("Digest verification changed.");

    // ------------------------------------------------------------- shutdown

    /// <summary>
    /// Cancel the poll, cancel and await whatever operation is in flight, then
    /// release the temporary firmware tree and the libusb contexts.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _pollCancel?.Cancel();

        Task? poll = _pollTask;
        _pollTask = null;

        if (poll is not null)
        {
            try
            {
                await poll.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        TaskCompletionSource? finished = _inFlight;
        CancellationTokenSource? cancel = _cancel;

        if (cancel is not null && finished is not null)
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await finished.Task.ConfigureAwait(true);
        }

        await DisposePackageAsync().ConfigureAwait(true);
        DeleteBundleWorkspace();
        DisposeBundle();

        _pollCancel?.Dispose();
        _pollCancel = null;

        _enumerator.Dispose();
        _pollEnumerator.Dispose();
        Log.Dispose();
    }

    private async Task DisposePackageAsync()
    {
        FirmwarePackage? package = _package;
        _package = null;
        _plan = null;

        if (package is null)
            return;

        try
        {
            await package.DisposeAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            // Losing the temporary tree is less bad than masking whatever led
            // here, so this is logged and not thrown.
            Report(OdinLevel.Warning, "Could not delete the temporary firmware tree: " + ex.Message);
        }
    }
}
