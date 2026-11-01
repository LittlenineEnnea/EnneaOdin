// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Text;
using Avalonia.Controls;
using EnneaOdin.ViewModels;

namespace EnneaOdin.Views;

/// <summary>
/// The window. One thing lives here because a view model cannot do it:
/// scrolling the log. Everything else is markup bound to
/// <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private LogPaneViewModel? _log;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Detach();

        if (DataContext is not MainWindowViewModel model)
            return;

        _log = model.Log;
        _log.RowsAppended += OnRowsAppended;

        // The data context can arrive after a flash has filled the ring.
        ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    private void Detach()
    {
        if (_log is null)
            return;

        _log.RowsAppended -= OnRowsAppended;
        _log = null;
    }

    // From the log pane's flush timer: already on the UI thread and already
    // batched, so a burst costs one scroll per tick rather than one per line.
    private void OnRowsAppended(object? sender, EventArgs e) => ScrollToEnd();

    // How many rows are already in the box, so a flush appends only what is
    // new instead of rebuilding the whole log. A flash produces thousands of
    // lines and rebuilding a string that long on every tick is quadratic.
    private int _rendered;

    private readonly StringBuilder _text = new();

    private void Render()
    {
        if (_log is null)
            return;

        // Cleared, or trimmed from the front: start again.
        if (_log.Rows.Count < _rendered)
        {
            _text.Clear();
            _rendered = 0;
        }

        if (_log.Rows.Count == _rendered)
            return;

        for (int i = _rendered; i < _log.Rows.Count; i++)
            _text.Append(_log.Rows[i].Line).Append('\n');

        _rendered = _log.Rows.Count;

        // The caret and selection are reset by assigning Text, so a selection
        // the user made is only disturbed when there is actually new output.
        LogBox.Text = _text.ToString();
    }

    private void ScrollToEnd()
    {
        Render();

        if (_log is null || !_log.AutoScroll)
            return;

        // The box owns its scroll viewer, so this is a caret move rather than
        // a viewport call: putting the caret at the end scrolls it into view.
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }
}
