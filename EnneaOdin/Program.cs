// EnneaOdin. Copyright (c) 2025 EnneaOdin contributors.
//
// This file is not derived from Heimdall. Heimdall 2.2.2 is a command line
// program; the Qt frontend in the vendored tree is a separate application and
// nothing here is ported from it.
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace EnneaOdin;

internal static class Program
{
    /// <summary>
    /// Where an unhandled exception goes once there is a log to put it in. The
    /// application sets this after the view model exists; until then the only
    /// sink is standard error.
    /// </summary>
    internal static Action<string>? CrashSink;

    // No [STAThread]: that attribute is a Windows apartment-model declaration
    // and this program targets macOS and Linux only.
    public static void Main(string[] args)
    {
        InstallProcessHandlers();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Referenced by the Avalonia XAML previewer and by the designer; keep it
    // public and keep the name.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  // No WithInterFont: the window inherits the system UI font,
                  // which is what makes it look like a platform dialog.
                  .LogToTrace();

    /// <summary>
    /// Two of the three places an exception can surface without anybody
    /// catching it. The third is the dispatcher, and that one has to wait:
    /// see <see cref="InstallDispatcherHandler"/>.
    /// </summary>
    private static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("background thread", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// The third handler, installed by the application once Avalonia has
    /// started.
    /// </summary>
    internal static void InstallDispatcherHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Report("user interface", e.Exception);
            e.Handled = true;
        };
    }

    private static void Report(string where, Exception? error)
    {
        string message = "Unhandled exception on the " + where + ": " +
                         (error?.ToString() ?? "no exception object was supplied");

        try
        {
            Console.Error.WriteLine(message);
        }
        catch (System.IO.IOException)
        {
            // A GUI launched from Finder has no usable standard error. Losing
            // the console copy must not mask the exception itself.
        }

        CrashSink?.Invoke(message);
    }
}
