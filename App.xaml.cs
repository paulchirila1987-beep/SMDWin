using System;
using System.Runtime.InteropServices;
using System.Threading;
using SMDWin.Services;

namespace SMDWin
{
    public partial class App : System.Windows.Application
    {
        private Mutex? _singleInstanceMutex;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            // ── Single-instance guard ──────────────────────────────────────────────
            _singleInstanceMutex = new Mutex(true, "SMDWin_SingleInstance_v3", out bool isFirst);
            if (!isFirst)
            {
                // Another instance is running — bring it to front and exit
                try
                {
                    var current = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                    {
                        if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                Shutdown();
                return;
            }

            // 1. Register crash handlers FIRST — before anything else
            DispatcherUnhandledException += (s, ex) =>
            {
                try { AppLogger.Fatal(ex.Exception, "UI unhandled exception"); } catch { }
                ex.Handled = true;  // mark handled first so WPF doesn't terminate
                System.Windows.MessageBox.Show(
                    $"Unexpected error: {ex.Exception.Message}\n\nDetails saved to %AppData%\\SMDWin\\logs\\",
                    "SMDWin Error", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                // Shut down cleanly after showing the error — prevents zombie process
                try { Current?.Shutdown(1); } catch { Environment.Exit(1); }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                try { AppLogger.Fatal(ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString()),
                    "Background thread unhandled exception"); } catch { }
                if (ex.IsTerminating)
                {
                    try
                    {
                        System.Windows.MessageBox.Show(
                            $"Critical background error: {(ex.ExceptionObject as Exception)?.Message ?? ex.ExceptionObject?.ToString()}\n\nDetails saved to %AppData%\\SMDWin\\logs\\",
                            "SMDWin — Unexpected Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    }
                    catch { }
                }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                try
                {
                    // Suppress expected socket errors from LAN/port scans — these are normal
                    // when connecting to IPs that aren't running our server
                    var inner = ex.Exception?.InnerException;
                    if (inner is System.Net.Sockets.SocketException se &&
                        (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.HostUnreachable ||
                         se.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable))
                    {
                        // silent — expected during network scans
                    }
                    else if (inner is OperationCanceledException)
                    {
                        // silent — expected when cancellation token fires
                    }
                    else
                    {
                        AppLogger.Warning(ex.Exception ?? new Exception("Unobserved task exception"), "Unobserved task exception");
                    }
                }
                catch { }
                ex.SetObserved();
            };

            // 2. Init Serilog AFTER handlers — wrapped in try/catch so a Serilog failure
            //    never prevents the app from starting
            try { AppLogger.Initialize(); } catch { }

            base.OnStartup(e);
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            try { AppLogger.Info("SMDWin exiting (code {Code})", e.ApplicationExitCode); } catch { }
            try { AppLogger.Shutdown(); } catch { }
            try { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
            // Nuclear option: force process exit so no background thread can keep it alive.
            // Called after base.OnExit so all WPF cleanup runs first.
            Environment.Exit(e.ApplicationExitCode);
        }
    }
}
