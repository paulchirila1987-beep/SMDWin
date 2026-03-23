using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace SMDWin.Services
{
    /// <summary>
    /// Thin static wrapper around Serilog.
    /// Usage:  AppLogger.Info("message");
    ///         AppLogger.Warning(ex, "context");
    ///         AppLogger.Error(ex, "fatal context");
    ///
    /// Logs are written to:
    ///   %AppData%\SMDWin\logs\smdwin-.log  (rolling, one file per day, 7 days kept)
    /// Debug sink is also active in Debug builds.
    /// </summary>
    public static class AppLogger
    {
        private static ILogger _log = Serilog.Core.Logger.None;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SMDWin", "logs");

            Directory.CreateDirectory(logDir);

            string logPath = Path.Combine(logDir, "smdwin-.log");

            var cfg = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithThreadId()
                .WriteTo.File(
                    path:              logPath,
                    rollingInterval:   RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate:    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] (tid:{ThreadId}) {Message:lj}{NewLine}{Exception}",
                    shared:            false)
#if DEBUG
                .WriteTo.Debug(
                    outputTemplate:    "[SMDWin {Level:u3}] {Message:lj}{NewLine}{Exception}")
#endif
                ;

            Log.Logger = cfg.CreateLogger();
            _log = Log.Logger;

            _log.Information("─── SMDWin started ─── v{Version}",
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString() ?? "?");
        }

        public static void Shutdown()
        {
            try { Log.CloseAndFlush(); } catch { }
        }

        // ── Convenience methods ───────────────────────────────────────────────

        public static void Info(string message, params object?[] args)
            => _log.Information(message, args);

        public static void Debug(string message, params object?[] args)
            => _log.Debug(message, args);

        public static void Warning(string message, params object?[] args)
            => _log.Warning(message, args);

        public static void Warning(Exception ex, string context)
            => _log.Warning(ex, context);

        public static void Error(string message, params object?[] args)
            => _log.Error(message, args);

        public static void Error(Exception ex, string context)
            => _log.Error(ex, context);

        public static void Fatal(Exception ex, string context)
            => _log.Fatal(ex, context);

        /// <summary>
        /// Returns last N lines from the most recent log file.
        /// Used by the Debug Panel.
        /// </summary>
        public static string GetRecentLines(int count = 50)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SMDWin", "logs");

                if (!Directory.Exists(logDir)) return "(no log files yet)";

                var files = Directory.GetFiles(logDir, "smdwin-*.log");
                if (files.Length == 0) return "(no log files yet)";

                // Most recent file
                Array.Sort(files);
                string latest = files[^1];

                // Read with FileShare.ReadWrite — Serilog keeps the file open
                using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string content = reader.ReadToEnd();

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                int start = Math.Max(0, lines.Length - count);
                return string.Join('\n', lines[start..]);
            }
            catch (Exception ex)
            {
                return $"(error reading log: {ex.Message})";
            }
        }
    }
}
