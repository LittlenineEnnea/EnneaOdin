// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OdinCore.Diagnostics;

namespace EnneaOdin.ViewModels;

/// <summary>
/// The rendered projection of the one and only log store.
/// </summary>
public sealed partial class LogPaneViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How often queued events are moved into the bound collection. 100 ms is
    /// fast enough to read as live and slow enough to coalesce a burst.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly LogRing _ring;
    private readonly IProgress<OdinEvent> _sink;
    private readonly ConcurrentQueue<OdinEvent> _pending = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public LogPaneViewModel(LogRing ring)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _sink = _ring.AsProgress();
        _ring.Added += OnEventAdded;

        _timer = new DispatcherTimer(FlushInterval, DispatcherPriority.Background, (_, _) => Flush());
        _timer.Start();
    }

    /// <summary>
    /// The visible log, oldest first. The pane draws one coloured run per row;
    /// the colour key is on the row.
    /// </summary>
    public ObservableCollection<LogRowViewModel> Rows { get; } = new();

    /// <summary>
    /// Whether Debug events are shown. Toggling this rebuilds the rows from the
    /// ring, so turning it on reveals debug lines that were recorded while it
    /// was off.
    /// </summary>
    [ObservableProperty]
    private bool _showDebug;

    /// <summary>Whether the list follows the newest row.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Raised on the user interface thread after rows were appended.</summary>
    public event EventHandler? RowsAppended;

    /// <summary>
    /// The sink every OdinCore call is given. Appends go straight into the
    /// ring from whatever thread produced them; the ring is the thread-safe
    /// part, and this class is what crosses to the user interface thread.
    /// </summary>
    public IProgress<OdinEvent> Sink => _sink;

    /// <summary>Record one event. Safe from any thread.</summary>
    public void Add(OdinEvent e) => _ring.Add(e);

    /// <summary>Forget everything, in the store and on screen.</summary>
    public void Clear()
    {
        _ring.Clear();
        while (_pending.TryDequeue(out _)) { }
        Rows.Clear();
    }

    /// <summary>
    /// Every retained event as text, at every level, regardless of what the
    /// filter currently shows. A saved log that honoured the filter would be a
    /// log with the evidence removed.
    /// </summary>
    public string SnapshotText()
    {
        IReadOnlyList<OdinEvent> events = _ring.Snapshot();
        var text = new StringBuilder(events.Count * 64);

        foreach (OdinEvent e in events)
            text.Append(Format(e)).Append('\n');

        return text.ToString();
    }

    private void OnEventAdded(OdinEvent e) => _pending.Enqueue(e);

    private void Flush()
    {
        bool appended = false;

        while (_pending.TryDequeue(out OdinEvent? e))
        {
            if (e.Level == OdinLevel.Debug && !ShowDebug)
                continue;

            Rows.Add(LogRowViewModel.From(e));
            appended = true;
        }

        while (Rows.Count > _ring.Capacity)
            Rows.RemoveAt(0);

        if (appended)
            RowsAppended?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowDebugChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        while (_pending.TryDequeue(out _)) { }

        Rows.Clear();

        foreach (OdinEvent e in _ring.Snapshot())
        {
            if (e.Level == OdinLevel.Debug && !ShowDebug)
                continue;

            Rows.Add(LogRowViewModel.From(e));
        }

        RowsAppended?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(OdinEvent e) => LogRowViewModel.From(e).Line;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ring.Added -= OnEventAdded;
        _timer.Stop();
    }
}
