using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Services
{
    /// <summary>
    /// SMDWin Theme Manager v3 — 2 base themes (Dark / Light) x 4 accent colors
    /// (Blue / Red / Green / Orange). Sidebar gets an accent-tinted gradient.
    /// </summary>
    public static class ThemeManager
    {
        // ── Accent palette ────────────────────────────────────────────────────
        public static readonly Dictionary<string, string> AccentHex = new()
        {
            ["Blue"]   = "#0078D4",
            ["Red"]    = "#F53333",   // more vivid red
            ["Green"]  = "#18B248",   // more vivid green
            ["Orange"] = "#F97316",
        };

        // ── Base themes ───────────────────────────────────────────────────────
        public static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
        {
            ["Dark"] = new()
            {
                ["BgDark"]        = "#131820",
                ["BgCard"]        = "#1C2431",
                ["BgHover"]       = "#252D3A",
                ["TextPrimary"]   = "#FFFFFF",
                ["TextSecondary"] = "#D4E4F7",
                ["Border"]        = "#2A3345",
                ["SidebarBg"]     = "#0A0F1A",
                ["NavActive"]     = "#1E3A64",
                ["NavActiveFg"]   = "#93C5FD",
                ["ChartGreen"]    = "#22C55E",
                ["ChartGreenDim"] = "#16A34A",
            },
            ["Light"] = new()
            {
                // Modern light — crisp whites, stronger contrast, more visual depth
                ["BgDark"]        = "#EDF1F7",   // page bg — cooler, more distinct from cards
                ["BgCard"]        = "#FFFFFF",
                ["BgHover"]       = "#E4EAF4",
                ["TextPrimary"]   = "#080E1D",   // near-black with blue undertone
                ["TextSecondary"] = "#2E3D55",   // darker secondary — more readable
                ["Border"]        = "#B8CADF",   // stronger border — cards pop more
                ["SidebarBg"]     = "#E0E9F5",
                ["NavActive"]     = "#CDDCF8",   // blue-100 stronger
                ["NavActiveFg"]   = "#1A45C4",   // blue-700 deeper
                ["ChartGreen"]    = "#15803D",
                ["ChartGreenDim"] = "#166534",
            },
        };

        // Legacy aliases
        public static readonly Dictionary<string, string> LegacyAliases = new()
        {
            ["Dark Navy"]     = "Dark",
            ["Dark Slate"]    = "Dark",
            ["Dark Midnight"] = "Dark",
            ["Dark Red"]      = "Dark",
            ["Gray"]          = "Light",
            ["Light Clean"]   = "Light",
            ["Light Warm"]    = "Light",
            ["Light Refined"] = "Light",
        };

        public static bool IsDark(string? themeName) =>
            themeName is "Dark" or "Dark Navy" or "Dark Slate" or "Dark Midnight" or "Dark Red";
        public static bool IsLight(string? themeName) =>
            themeName is "Light" or "Light Clean" or "Light Warm" or "Light Refined" or "Gray";

        public static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Dark";
            if (LegacyAliases.TryGetValue(name, out var mapped)) return mapped;
            return Themes.ContainsKey(name) ? name : "Dark";
        }

        public static string ResolveAuto() => ResolveAutoTheme(
            SettingsService.Current.AutoDarkTheme,
            SettingsService.Current.AutoLightTheme);

        public static bool WindowsIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                return val is int i && i == 0;
            }
            catch { return true; }
        }

        public static string ResolveAutoTheme(string? autoDark, string? autoLight)
        {
            bool isDark = WindowsIsDark();
            string dark  = Normalize(autoDark  ?? "Dark");
            string light = Normalize(autoLight ?? "Light");
            return isDark ? dark : light;
        }

        // ── Accent helpers ────────────────────────────────────────────────────
        public static string GetAccentHex(string? accentName)
        {
            if (accentName != null && AccentHex.TryGetValue(accentName, out var hex)) return hex;
            return AccentHex["Blue"];
        }

        private static (string top, string mid, string bot) GetSidebarGradientHex(string themeName, string accentHex)
        {
            // Glass Border concept: very subtle tint, almost neutral
            bool isDark = IsDark(themeName);
            try
            {
                var ac = ParseHex(accentHex);
                if (isDark)
                {
                    // Deep dark, barely tinted — sidebar is a dark glass panel
                    byte r = (byte)(ac.R * 0.03);
                    byte g = (byte)(ac.G * 0.03);
                    byte b = (byte)(ac.B * 0.03);
                    string top = string.Format("#{0:X2}{1:X2}{2:X2}",
                        (byte)Math.Min(255, 0x0E + r),
                        (byte)Math.Min(255, 0x12 + g),
                        (byte)Math.Min(255, 0x1C + b));
                    string mid = string.Format("#{0:X2}{1:X2}{2:X2}",
                        (byte)Math.Min(255, 0x0A + r),
                        (byte)Math.Min(255, 0x0E + g),
                        (byte)Math.Min(255, 0x17 + b));
                    string bot = string.Format("#{0:X2}{1:X2}{2:X2}",
                        (byte)Math.Min(255, 0x07 + r),
                        (byte)Math.Min(255, 0x0A + g),
                        (byte)Math.Min(255, 0x11 + b));
                    return (top, mid, bot);
                }
                else
                {
                    // Light: sidebar distinctly different from page bg — cooler blue-gray tint
                    byte rt = (byte)Math.Min(255, (int)(ac.R * 0.04) + 0xDF);
                    byte gt = (byte)Math.Min(255, (int)(ac.G * 0.04) + 0xE6);
                    byte bt = (byte)Math.Min(255, (int)(ac.B * 0.05) + 0xF0);
                    string top = string.Format("#{0:X2}{1:X2}{2:X2}", rt, gt, bt);
                    string mid = string.Format("#{0:X2}{1:X2}{2:X2}",
                        (byte)Math.Max(0, rt - 4),
                        (byte)Math.Max(0, gt - 4),
                        (byte)Math.Max(0, bt - 3));
                    string bot2 = string.Format("#{0:X2}{1:X2}{2:X2}",
                        (byte)Math.Max(0, rt - 8),
                        (byte)Math.Max(0, gt - 8),
                        (byte)Math.Max(0, bt - 5));
                    return (top, mid, bot2);
                }
            }
            catch
            {
                return isDark
                    ? ("#0E121C", "#0A0E17", "#070A11")
                    : ("#F2F4F8", "#F2F4F8", "#EDF0F5");
            }
        }

        public static void Apply(string rawThemeName, ResourceDictionary resources)
        {
            if (rawThemeName == "Auto") return;

            string themeName = Normalize(rawThemeName);
            if (!Themes.TryGetValue(themeName, out var t)) return;

            bool isLight = IsLight(themeName);
            bool isDark  = IsDark(themeName);
            string accentHex = GetAccentHex(SettingsService.Current.AccentName);

            void Set(string key, string hex)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex)!;
                    resources[key + "Color"] = color;
                    resources[key + "Brush"] = new SolidColorBrush(color);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }

            void SetColor(string key, string hex)
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex)!;
                    resources[key + "Color"] = c;
                    resources[key + "Brush"] = new SolidColorBrush(c);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }

            // Base palette
            Set("BgDark",        t["BgDark"]);
            Set("BgCard",        t["BgCard"]);
            Set("BgHover",       t["BgHover"]);
            // Input background: neutral (not the accent-tinted hover blue)
            SetColor("BgInput",  isLight ? "#F0F4FA" : t["BgHover"]);
            Set("Accent",        accentHex);
            Set("TextPrimary",   t["TextPrimary"]);
            Set("TextSecondary", t["TextSecondary"]);
            Set("Border",        t["Border"]);
            Set("Sidebar",       t["SidebarBg"]);

            // NavActive/NavActiveFg tinted by accent
            try
            {
                var ac = ParseHex(accentHex);
                // Glass Border: active = very subtle tint + foreground is accent color
                if (isDark)
                {
                    var navBg = Color.FromArgb(38, ac.R, ac.G, ac.B);   // subtle ghost
                    var navFg = Color.FromRgb(
                        (byte)Math.Min(255, ac.R + 70),
                        (byte)Math.Min(255, ac.G + 70),
                        (byte)Math.Min(255, ac.B + 70));
                    resources["NavActiveColor"]   = navBg;
                    resources["NavActiveBrush"]   = new SolidColorBrush(navBg);
                    resources["NavActiveFgColor"] = navFg;
                    resources["NavActiveFgBrush"] = new SolidColorBrush(navFg);
                }
                else
                {
                    var navBg = Color.FromArgb(28, ac.R, ac.G, ac.B);   // very light tint
                    resources["NavActiveColor"]   = navBg;
                    resources["NavActiveBrush"]   = new SolidColorBrush(navBg);
                    resources["NavActiveFgColor"] = ac;
                    resources["NavActiveFgBrush"] = new SolidColorBrush(ac);
                }
            }
            catch
            {
                Set("NavActive",   t["NavActive"]);
                Set("NavActiveFg", t["NavActiveFg"]);
            }

            // Card border
            if (isDark)
            {
                try
                {
                    var bc = ParseHex(t["Border"]);
                    var cardBorder = Color.FromArgb(70, bc.R, bc.G, bc.B);
                    resources["CardBorderColor"] = cardBorder;
                    resources["CardBorderBrush"] = new SolidColorBrush(cardBorder);
                    var border2 = Color.FromArgb(220, bc.R, bc.G, bc.B);
                    resources["BorderColor"]  = border2;
                    resources["BorderBrush2"] = new SolidColorBrush(border2);
                }
                catch { Set("CardBorder", t["Border"]); }
            }
            else
            {
                Set("CardBorder", t["Border"]);
                // Light theme: also set BorderBrush2 to match the light border
                try
                {
                    var bc = ParseHex(t["Border"]);
                    resources["BorderColor"]  = bc;
                    resources["BorderBrush2"] = new SolidColorBrush(bc);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }

            Set("NavAccentBar", accentHex);

            // RowSelect
            try
            {
                var ac = ParseHex(accentHex);
                byte alpha = isLight ? (byte)55 : (byte)42;
                var rowSel = new SolidColorBrush(Color.FromArgb(alpha, ac.R, ac.G, ac.B));
                rowSel.Freeze();
                resources["RowSelectBrush"] = rowSel;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // AccentHover
            try
            {
                var ac = ParseHex(accentHex);
                byte rh = (byte)Math.Min(255, ac.R + (isDark ? 30 : -20));
                byte gh = (byte)Math.Min(255, ac.G + (isDark ? 30 : -20));
                byte bh = (byte)Math.Min(255, ac.B + (isDark ? 30 : -20));
                var hover = new SolidColorBrush(Color.FromRgb(rh, gh, bh));
                hover.Freeze();
                resources["AccentHoverBrush"] = hover;
                resources["AccentHoverColor"] = Color.FromRgb(rh, gh, bh);
            }
            catch { resources["AccentHoverBrush"] = resources.Contains("AccentBrush") ? resources["AccentBrush"] : new SolidColorBrush(ParseHex(accentHex)); }

            Set("ActionGreen", isLight ? "#16A34A" : "#22C55E");
            Set("ActionRed",   isLight ? "#DC2626" : "#EF4444");

            resources["PrimaryButtonFgColor"] = System.Windows.Media.Colors.White;
            resources["PrimaryButtonFgBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);

            // ── Semantic status colors ────────────────────────────────────────────
            // Used in XAML via {DynamicResource StatusSuccessBrush} etc.
            // Replaces hardcoded #22C55E / #EF4444 / #F59E0B / #60A5FA inline values.
            Set("StatusSuccess", isLight ? "#16A34A" : "#22C55E");
            Set("StatusWarning", isLight ? "#D97706" : "#F59E0B");
            Set("StatusError",   isLight ? "#DC2626" : "#EF4444");
            Set("StatusInfo",    isLight ? "#3B82F6" : "#60A5FA");

            // Semi-transparent overlay (replaces Background="#15FFFFFF" inline usages)
            var overlayAlpha = isLight ? (byte)20 : (byte)21;
            var overlayClr   = Color.FromArgb(overlayAlpha, 255, 255, 255);
            resources["BgOverlayColor"] = overlayClr;
            resources["BgOverlayBrush"] = new SolidColorBrush(overlayClr);

            // Chart-specific named colors (replaces FromRgb(59,130,246) in code-behind)
            Set("ChartBlue",   isLight ? "#2563EB" : "#3B82F6");
            Set("ChartOrange", isLight ? "#EA580C" : "#F97316");
            // ─────────────────────────────────────────────────────────────────────

            var accentBase = ParseHex(accentHex);
            resources["AccentGradientBrush"] = new System.Windows.Media.SolidColorBrush(accentBase);
            resources["AccentColor"] = accentBase;
            resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(accentBase);
            resources["GreenGradientBrush"]   = new System.Windows.Media.SolidColorBrush(ParseHex("#107C10"));
            resources["RedGradientBrush"]     = new System.Windows.Media.SolidColorBrush(ParseHex("#C42B1C"));
            resources["PurpleGradientBrush"]  = new System.Windows.Media.SolidColorBrush(accentBase);
            resources["OutlineGradientBrush"] = new System.Windows.Media.SolidColorBrush(Colors.Transparent);

            // Throttle banner
            if (isLight)
            {
                SetColor("ThrottleBannerBg",    "#FEF9C3");
                SetColor("ThrottleBannerBorder", "#CA8A04");
                SetColor("ThrottleBannerFg",     "#713F12");
            }
            else
            {
                SetColor("ThrottleBannerBg",    "#7F1D1D");
                SetColor("ThrottleBannerBorder", "#EF4444");
                SetColor("ThrottleBannerFg",     "#FCA5A5");
            }

            // DataGrid row colors
            SetColor("RowSeparator", isLight ? t["Border"] : "#1A2438");
            SetColor("RowAlt",       isLight ? "#E8EFF8"   : "#111620");  // more visible alternating rows
            SetColor("RowHover",     isLight ? "#D8E4F2"   : t["BgHover"]);
            SetColor("TableHeaderBg",isLight ? "#DDE6F2"   : "#0D1422");  // stronger header bg

            // Sidebar hover — accent-tinted
            try
            {
                var ac = ParseHex(accentHex);
                // Glass Border hover: very subtle, just a ghost
                var sideHov = isLight
                    ? Color.FromArgb(35, ac.R, ac.G, ac.B)
                    : Color.FromArgb(22, ac.R, ac.G, ac.B);
                resources["SidebarHoverColor"] = sideHov;
                resources["SidebarHoverBrush"] = new SolidColorBrush(sideHov);
            }
            catch { SetColor("SidebarHover", isLight ? "#DBEAFE" : "#1A2D4A"); }

            // Sidebar gradient (accent-tinted)
            var (sTop, sMid, sBot) = GetSidebarGradientHex(themeName, accentHex);
            resources["_SidebarTopHex"] = sTop;
            resources["_SidebarMidHex"] = sMid;
            resources["_SidebarBotHex"] = sBot;
            resources["_SidebarIsColorful"] = false;

            // Sidebar border accent-tinted
            try
            {
                var ac = ParseHex(accentHex);
                // Glass Border: right accent line — more visible alpha
                var sideBorder = isDark
                    ? Color.FromArgb(110, ac.R, ac.G, ac.B)
                    : Color.FromArgb(80,  ac.R, ac.G, ac.B);
                resources["SidebarBorderColor"] = sideBorder;
                resources["SidebarBorderBrush"] = new SolidColorBrush(sideBorder);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            SetColor("NavCatFg", isLight ? "#94A3B8" : "#3A5A8A");

            // Chart green
            try
            {
                var c = ParseHex(t["ChartGreen"]);
                resources["ChartGreenColor"] = c;
                resources["ChartGreenBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Sidebar text — high contrast
            try
            {
                var c = ParseHex(isLight ? "#1E2D42" : "#B8D4F0");  // darker on light for readability
                resources["SidebarTextColor"] = c;
                resources["SidebarTextBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            try
            {
                var c = ParseHex(isLight ? "#111827" : "#F0F6FF");  // near-black on light, near-white on dark
                resources["SidebarTextPrimaryColor"] = c;
                resources["SidebarTextPrimaryBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Toast
            try
            {
                var bg = ParseHex(t["BgDark"]);
                var toastBg = Color.FromArgb(210, bg.R, bg.G, bg.B);
                resources["ToastBgColor"]  = toastBg;
                resources["ToastBgBrush"]  = new SolidColorBrush(toastBg);
                var toastBorder = isLight
                    ? Color.FromArgb(80,  0,   0,   0)
                    : Color.FromArgb(68, 255, 255, 255);
                resources["ToastBorderBrush"] = new SolidColorBrush(toastBorder);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Warning banner
            try
            {
                var warnBg = isLight ? ParseHex("#D97706") : ParseHex("#EF7C00");
                resources["WarningBannerBgColor"]  = warnBg;
                resources["WarningBannerBgBrush"]  = new SolidColorBrush(warnBg);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        public static void ApplySidebarGradient(System.Windows.Controls.Border? sidebarBorder,
                                                 ResourceDictionary resources)
        {
            if (sidebarBorder == null) return;
            try
            {
                Color top = ParseHex(resources["_SidebarTopHex"] as string ?? "#0E121C");
                Color mid = ParseHex(resources["_SidebarMidHex"] as string ?? "#0A0E17");
                Color bot = ParseHex(resources["_SidebarBotHex"] as string ?? "#070A11");
                sidebarBorder.Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(top, 0.0),
                        new GradientStop(mid, 0.55),
                        new GradientStop(bot, 1.0),
                    },
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(0, 1));

                // Glass Border: accent-colored right border line
                if (resources.Contains("SidebarBorderBrush"))
                    sidebarBorder.BorderBrush = (System.Windows.Media.Brush)resources["SidebarBorderBrush"];
                sidebarBorder.BorderThickness = new System.Windows.Thickness(0, 0, 1, 0);

                // Subtle accent glow: very faint horizontal gradient tinted by accent
                if (resources.Contains("AccentColor") && resources["AccentColor"] is System.Windows.Media.Color acGlow)
                {
                    bool sidebarIsDark = top.R < 128;
                    byte alpha1 = sidebarIsDark ? (byte)9 : (byte)6;
                    byte alpha2 = sidebarIsDark ? (byte)14 : (byte)9;
                    var glowBrush = new System.Windows.Media.LinearGradientBrush(
                        new System.Windows.Media.GradientStopCollection
                        {
                            new System.Windows.Media.GradientStop(
                                System.Windows.Media.Color.FromArgb(0, acGlow.R, acGlow.G, acGlow.B), 0.0),
                            new System.Windows.Media.GradientStop(
                                System.Windows.Media.Color.FromArgb(alpha1, acGlow.R, acGlow.G, acGlow.B), 0.6),
                            new System.Windows.Media.GradientStop(
                                System.Windows.Media.Color.FromArgb(alpha2, acGlow.R, acGlow.G, acGlow.B), 1.0),
                        },
                        new System.Windows.Point(0, 0),
                        new System.Windows.Point(1, 0));
                    resources["_SidebarAccentGlowBrush"] = glowBrush;
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── DWM ───────────────────────────────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        private const int DWMWA_CAPTION_COLOR           = 35;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND                   = 2;
        private const int DWMWCP_ROUNDSMALL              = 3;
        private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;
        private const int DWMWA_MICA_EFFECT              = 1029;
        private const int DWM_SYSTEMBACKDROP_MICA        = 2;
        private const int DWM_SYSTEMBACKDROP_NONE        = 1;

        public static void ApplyRoundedCorners(IntPtr hwnd, bool small = false)
        {
            try
            {
                int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        public static void SetCaptionColor(IntPtr hwnd, string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                int colorref = (color.B << 16) | (color.G << 8) | color.R;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        public static void ApplyTitleBarColor(IntPtr hwnd, string rawThemeName)
        {
            try
            {
                string themeName = Normalize(rawThemeName);
                bool dark = IsDark(themeName);
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                bool useMica = SettingsService.Current.UseMica && IsWindows11();
                if (useMica)
                {
                    int colorNone = unchecked((int)0xFFFFFFFE);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
                }
                else
                {
                    if (!Themes.TryGetValue(themeName, out var t)) return;
                    string hex = IsLight(themeName) ? t["BgDark"] : t["SidebarBg"];
                    var color  = (Color)ColorConverter.ConvertFromString(hex)!;
                    int colorref = (color.B << 16) | (color.G << 8) | color.R;
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        public static void ApplyNeonGlow(System.Windows.DependencyObject root, string themeName) { }

        public static bool IsWindows11()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                if (v.Major == 10 && v.Build >= 22000) return true;
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("CurrentBuildNumber") is string buildStr &&
                    int.TryParse(buildStr, out int build) && build >= 22000) return true;
                return false;
            }
            catch { return false; }
        }

        public static double MicaLayerOpacity { get; set; } = 1.0;

        public static void ApplyMica(IntPtr hwnd, bool enable, bool isDark)
        {
            if (!IsWindows11()) return;
            try
            {
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                var v = Environment.OSVersion.Version;
                if (v.Build >= 22621)
                {
                    int backdrop = enable ? DWM_SYSTEMBACKDROP_MICA : DWM_SYSTEMBACKDROP_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                else
                {
                    int mica = enable ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int));
                }

                if (enable)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
                else
                {
                    var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── Custom Accent (compat API) ────────────────────────────────────────
        private static string? _customAccentHex = null;

        public static void ApplyCustomAccent(string hexColor, ResourceDictionary resources)
        {
            if (string.IsNullOrWhiteSpace(hexColor)) return;
            try
            {
                _customAccentHex = hexColor;
                var color = (Color)ColorConverter.ConvertFromString(hexColor)!;
                var brush = new SolidColorBrush(color);
                resources["AccentColor"]      = color;
                resources["AccentBrush"]      = brush;
                resources["NavActiveFgColor"] = color;
                resources["NavActiveFgBrush"] = brush;
                var navBg = Color.FromArgb(38, color.R, color.G, color.B);
                resources["NavActiveColor"] = navBg;
                resources["NavActiveBrush"] = new SolidColorBrush(navBg);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        public static void ClearCustomAccent(ResourceDictionary resources, string currentTheme)
        {
            _customAccentHex = null;
            Apply(currentTheme, resources);
        }

        public static string? GetCustomAccentHex() => _customAccentHex;

        // ── Helpers ───────────────────────────────────────────────────────────
        public static Color ParseHex(string hex)
            => (Color)ColorConverter.ConvertFromString(hex)!;

        private static string AdjustLightness(string hex, double delta)
        {
            try
            {
                var c = ParseHex(hex);
                byte Clamp(double v) => (byte)System.Math.Max(0, System.Math.Min(255, v));
                return string.Format("#{0:X2}{1:X2}{2:X2}",
                    Clamp(c.R + 255 * delta),
                    Clamp(c.G + 255 * delta),
                    Clamp(c.B + 255 * delta));
            }
            catch { return hex; }
        }

        // ── Per-page nav accent colors ────────────────────────────────────────
        public static readonly Dictionary<string, string> NavPageAccents = new()
        {
            ["Dashboard"]   = "#3B82F6",
            ["Hardware"]    = "#8B5CF6",
            ["Diagnose"]    = "#10B981",
            ["Crash"]       = "#EF4444",
            ["Events"]      = "#F59E0B",
            ["Drivers"]     = "#06B6D4",
            ["Storage"]     = "#F97316",
            ["RAM"]         = "#A855F7",
            ["Network"]     = "#14B8A6",
            ["Services"]    = "#6366F1",
            ["Apps"]        = "#EC4899",
            ["Tools"]       = "#84CC16",
            ["Settings"]    = "#94A3B8",
            ["About"]       = "#64748B",
        };

        public static void SetNavPageAccent(string pageTag, ResourceDictionary resources)
        {
            if (!NavPageAccents.TryGetValue(pageTag, out var hex)) return;
            try
            {
                var c = ParseHex(hex);
                resources["NavPageAccentColor"] = c;
                resources["NavPageAccentBrush"] = new SolidColorBrush(c);
                resources["NavPageActiveBgBrush"] = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }
    }
}
