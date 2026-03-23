using System;
using System.IO;
using System.Text.Json;
using SMDWin.Models;

namespace SMDWin.Services
{
    public static class SettingsService
    {
        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "SMDWin", "settings.json");

        public static AppSettings Current { get; private set; } = new();

        private static readonly string[] ValidThemes =
            { "Dark", "Light", "Auto",
              // legacy — acceptate dar migrate automat
              "Dark Navy", "Dark Slate", "Dark Midnight", "Light Clean", "Light Warm", "Light Refined" };

        private const int CurrentSettingsVersion = 6;

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { Current = new AppSettings(); }

            // ── Settings migration: add new fields without wiping existing user prefs ──
            // Each migration block runs only once (version gate), then bumps the version.
            // JSON deserialization gives default values for missing fields automatically,
            // so we only need to handle cases where we CHANGE defaults or rename fields.
            if (Current.SettingsVersion < 1)
            {
                // v0 → v1: Initial versioning. No breaking changes, just stamp the version.
                Current.SettingsVersion = 1;
                Save(); // persist the bumped version immediately
            }
            if (Current.SettingsVersion < 2)
            {
                // v1 → v2: Mica activat implicit pe Windows 11 pentru instalari noi
                // si utilizatori care nu au schimbat explicit setarea (valoarea default era false)
                if (!Current.UseMica && ThemeManager.IsWindows11())
                    Current.UseMica = true;
                Current.SettingsVersion = 2;
                Save();
            }
            if (Current.SettingsVersion < 3)
            {
                // v2 → v3: redenumire teme (Dark Navy→Dark, Light Clean→Light)
                Current.ThemeName      = MigrateThemeName(Current.ThemeName);
                Current.AutoDarkTheme  = MigrateThemeName(Current.AutoDarkTheme);
                Current.AutoLightTheme = MigrateThemeName(Current.AutoLightTheme);
                Current.SettingsVersion = 3;
                Save();
            }
            if (Current.SettingsVersion < 4)
            {
                // v3 → v4: forteaza UseMica=true pe Win11, seteaza MicaOpacity default
                if (ThemeManager.IsWindows11())
                    Current.UseMica = true;
                if (Current.MicaOpacity <= 0.01)
                    Current.MicaOpacity = 0.80;
                Current.SettingsVersion = 4;
                Save();
            }
            if (Current.SettingsVersion < 5)
            {
                // v4 → v5: reseteaza MicaExplicitlyDisabled (poate fi true din versiuni
                // anterioare buggy care il setau gresit). Mica e default activ pe Win11.
                Current.MicaExplicitlyDisabled = false;
                if (ThemeManager.IsWindows11())
                    Current.UseMica = true;
                if (Current.MicaOpacity <= 0.01)
                    Current.MicaOpacity = 0.80;
                Current.SettingsVersion = 5;
                Save();
            }
            // Template for future migrations:
            // if (Current.SettingsVersion < 2)
            // {
            //     Current.SomeNewField = MigratedValue(Current.OldField);
            //     Current.SettingsVersion = 2;
            //     Save();
            // }
            if (Current.SettingsVersion < 6)
            {
                // v5 → v6: Widget refactor — always dark, 2 modes only (Graphs/Gauges)
                if (Current.WidgetMode == "Compact" || Current.WidgetMode == "Detailed" || Current.WidgetMode == "Full")
                    Current.WidgetMode = "Graphs";
                if (string.IsNullOrWhiteSpace(Current.WidgetMetrics))
                    Current.WidgetMetrics = "CPU,RAM,Disk,Network";
                Current.SettingsVersion = 6;
                Save();
            }

            // Migrate old theme names to new ones
            Current.ThemeName = MigrateThemeName(Current.ThemeName);

            // Validate — reset to default if unknown theme
            if (!Array.Exists(ValidThemes, t => t == Current.ThemeName))
                Current.ThemeName = "Auto";

            // Validate AccentName
            var validAccents = new[] { "Blue", "Red", "Green", "Orange" };
            if (!Array.Exists(validAccents, a => a == Current.AccentName))
                Current.AccentName = "Blue";

            // Validate AutoDark / AutoLight sub-themes
            var validDark = new[] { "Dark", "Dark Navy", "Dark Slate", "Dark Midnight" };
            if (!Array.Exists(validDark, t => t == Current.AutoDarkTheme))
                Current.AutoDarkTheme = "Dark";
            var validLight = new[] { "Light", "Light Clean", "Light Warm", "Light Refined" };
            if (!Array.Exists(validLight, t => t == Current.AutoLightTheme))
                Current.AutoLightTheme = "Light";

            // Back-compat: if old JSON had AutoTheme=true, map it to "Auto"
            if (Current.AutoTheme && Current.ThemeName != "Auto")
                Current.ThemeName = "Auto";

            // Pe Win11: Mica e DEFAULT activata, cu exceptia cazului in care
            // utilizatorul a dezactivat-o explicit din Settings (MicaExplicitlyDisabled=true).
            if (ThemeManager.IsWindows11() && !Current.MicaExplicitlyDisabled)
                Current.UseMica = true;

            // MicaOpacity trebuie sa aiba o valoare valida
            if (Current.MicaOpacity <= 0.01)
                Current.MicaOpacity = 0.80;
        }

        /// <summary>Maps legacy theme names to new equivalents.</summary>
        private static string MigrateThemeName(string? name) => name switch
        {
            "Dark Blue"     => "Dark",
            "Dark Graphite" => "Dark",
            "Dark Green"    => "Dark",
            "Dark Glass"    => "Dark",
            "Dark Navy"     => "Dark",
            "Dark Slate"    => "Dark",
            "Dark Midnight" => "Dark",
            "Light Blue"    => "Light",
            "Light Gray"    => "Light",
            "Light Fluent"  => "Light",
            "Light macOS"   => "Light",
            "Light Clean"   => "Light",
            "Light Warm"    => "Light",
            "Light Refined" => "Light",
            _               => name ?? "Dark"
        };

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                // Write to temp file first, then rename — prevents corruption on crash/power loss
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── Window size helpers ───────────────────────────────────────────────

        /// <summary>
        /// Returns a (width, height) pair that fits within the primary screen work area,
        /// leaving at least 40 px of margin on every side.
        /// Call this from MainWindow.Loaded / OnSourceInitialized to cap the initial size.
        /// </summary>
        public static (double Width, double Height) GetSafeWindowSize(
            double requestedWidth, double requestedHeight)
        {
            try
            {
                var area = System.Windows.SystemParameters.WorkArea;
                double maxW = area.Width  - 40;
                double maxH = area.Height - 40;
                double w = Math.Min(requestedWidth,  maxW);
                double h = Math.Min(requestedHeight, maxH);
                return (Math.Max(w, 800), Math.Max(h, 500));   // never below 800×500
            }
            catch
            {
                return (requestedWidth, requestedHeight);
            }
        }

        /// <summary>
        /// Clamps an existing WPF window so it never overflows any screen it is on.
        /// Safe to call from any UI thread.
        /// </summary>
        public static void ClampWindowToScreen(System.Windows.Window window)
        {
            try
            {
                // Get work area of the screen the window is currently on
                var screen = System.Windows.Forms.Screen.FromHandle(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle);
                var wa = screen.WorkingArea;

                double maxW = wa.Width  - 20;
                double maxH = wa.Height - 20;

                if (window.Width  > maxW) window.Width  = maxW;
                if (window.Height > maxH) window.Height = maxH;

                // Also ensure top-left is on screen
                if (window.Left < wa.Left) window.Left = wa.Left + 10;
                if (window.Top  < wa.Top)  window.Top  = wa.Top  + 10;

                // Ensure bottom-right is on screen
                if (window.Left + window.Width  > wa.Right)
                    window.Left = wa.Right - window.Width  - 10;
                if (window.Top  + window.Height > wa.Bottom)
                    window.Top  = wa.Bottom - window.Height - 10;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }
    }
}
