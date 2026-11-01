// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// Not derived from Heimdall.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EnneaOdin.Services;
using EnneaOdin.Styles;
using EnneaOdin.ViewModels;
using EnneaOdin.Views;

namespace EnneaOdin;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindowViewModel? _model;
    private MainWindow? _window;
    private IConfirmService? _confirm;

    // Avalonia's close and shutdown callbacks are synchronous, so the one
    // asynchronous teardown this program needs has to be started rather than
    // awaited. It is held here so it is rooted and runs exactly once, and it
    // catches everything internally, so there is no unobserved task.
    private Task? _teardown;
    private bool _mayClose;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Safe here and not in Main: the dispatcher now has a platform loop.
        Program.InstallDispatcherHandler();

        // Before any window exists. DynamicResource does not report a missing
        // key, so a typo in Styles/Tokens.axaml would otherwise show up as a
        // card with no background rather than as an error. This throws, which
        // is the point: a design token that is not there is a loud failure.
        DesignTokens.Verify(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            _model = new MainWindowViewModel();
            _window = new MainWindow { DataContext = _model };
            _confirm = new ConfirmService(_window);

            _model.Attach(new StorageFilePicker(_window), _confirm);

            // Unhandled exceptions now have somewhere to be recorded.
            Program.CrashSink = _model.ReportCrash;

            // Nothing blocking runs in the view model's constructor: libusb is
            // probed and the first enumeration happens once the window is up.
            _window.Opened += OnWindowOpened;
            _window.Closing += OnWindowClosing;
            desktop.ShutdownRequested += OnShutdownRequested;

            desktop.MainWindow = _window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_window is not null)
            _window.Opened -= OnWindowOpened;

        // The command owns its own task and catches its own failures.
        _model?.InitialiseCommand.Execute(null);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_mayClose)
            return;

        e.Cancel = true;
        BeginTeardown();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_mayClose)
            return;

        e.Cancel = true;
        BeginTeardown();
    }

    private void BeginTeardown() => _teardown ??= FinishTeardownAsync();

    /// <summary>
    /// Confirm if work is in flight, then cancel it and wait for it before the
    /// process goes away.
    /// </summary>
    private async Task FinishTeardownAsync()
    {
        try
        {
            if (_model is not null && _model.IsBusy && _confirm is not null)
            {
                bool quit = await _confirm.ConfirmAsync("Quit EnneaOdin",
                    "An operation is still running.\n\n" +
                    "Quitting now cancels it at the next packet boundary and closes the " +
                    "session. If a partition is being written it will be left partially " +
                    "written, and the device may not boot afterwards.",
                    "Quit").ConfigureAwait(true);

                if (!quit)
                {
                    // Let a later attempt start a fresh teardown.
                    _teardown = null;
                    return;
                }
            }

            if (_model is not null)
                await _model.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Teardown must not be the thing that kills the process.
            Console.Error.WriteLine("Shutdown failed: " + ex);
        }

        _mayClose = true;
        _desktop?.Shutdown();
    }
}
