using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SMDWin.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfApp   = System.Windows.Application;

namespace SMDWin.Views
{
    public partial class FeaturesWindow : Window
    {
        public FeaturesWindow()
        {
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    string theme = SMDWin.Services.SettingsService.Current.ThemeName;
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                    string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
                }
                catch { }
            };
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            ApplyTheme();
            PopulateContent();
        }

        // ── Theme ─────────────────────────────────────────────────────────────
        private void ApplyTheme()
        {
            // Inherit all resource dictionaries from the running app
            Resources.MergedDictionaries.Clear();
            foreach (ResourceDictionary rd in WpfApp.Current.Resources.MergedDictionaries)
                Resources.MergedDictionaries.Add(rd);

            // Copy all dynamic resources from App so DynamicResource works in this window
            foreach (var key in WpfApp.Current.Resources.Keys)
            {
                try { Resources[key] = WpfApp.Current.Resources[key]; } catch { }
            }

            // Window + banner background from theme resources
            var bgBrush   = (WpfBrush)WpfApp.Current.Resources["BgDarkBrush"];
            var cardBrush = (WpfBrush)WpfApp.Current.Resources["BgCardBrush"];
            var textBrush = (WpfBrush)WpfApp.Current.Resources["TextPrimaryBrush"];
            Background              = bgBrush;
            BannerBorder.Background = bgBrush;
            TxtAppName.Foreground   = textBrush;
            TxtBannerSub.Foreground = textBrush;

            // Force card backgrounds explicitly (DynamicResource may not propagate in child windows)
            var accentBrush = (WpfBrush)WpfApp.Current.Resources["AccentBrush"];
            var secondaryBrush = (WpfBrush)WpfApp.Current.Resources["TextSecondaryBrush"];
            var borderBrush = (WpfBrush)WpfApp.Current.Resources["BorderBrush2"];
            foreach (var card in new[] { Card1, Card2, Card3, Card4, Card5, Card6 })
            {
                card.Background  = cardBrush;
                card.BorderBrush = borderBrush;
            }
            // Force text colors on headers and icons
            foreach (var hdr in new[] { Hdr1, Hdr2, Hdr3, Hdr4, Hdr5, Hdr6 })
                hdr.Foreground = accentBrush;
            foreach (var ico in new[] { Ico1, Ico2, Ico3, Ico4, Ico5, Ico6 })
                ico.Foreground = textBrush;
            // Force ScrollViewer and ContentGrid background
            ContentGrid.Background = bgBrush;

            string themeName = SettingsService.Current.ThemeName ?? "Dark Midnight";
            if (themeName == "Auto")
                themeName = ThemeManager.ResolveAuto();

            Loaded += (_, _) =>
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    ThemeManager.ApplyTitleBarColor(handle, themeName);
                }
                catch { }
            };
        }

        // ── Localised content ─────────────────────────────────────────────────
        private static string S(string key, string fallback = "")
        {
            var v = LanguageService.S("Features", key);
            // LanguageService returns "[Features.Key]" when key is missing
            return (v.Length > 0 && !v.StartsWith("[Features.")) ? v : fallback;
        }

        private static List<string> Items(string prefix)
        {
            var list = new List<string>();
            for (int i = 1; i <= 6; i++)   // max 6 items per column
            {
                string val = LanguageService.S("Features", $"{prefix}{i}");
                if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[Features.")) break;
                list.Add("• " + val);
            }
            return list;
        }

        private void PopulateContent()
        {
            bool isRo = LanguageService.CurrentCode == "ro";

            Title = isRo ? "SMD Win — Capabilități" : "SMD Win — Capabilities";
            TxtBannerSub.Text = isRo ? "— Ce face aplicația și limitările cunoscute" : "— Application capabilities & known limitations";

            string appVersion = LanguageService.S("App", "Version") is { Length: > 0 } v
                && !v.StartsWith("[") ? v : "v0.4";
            TxtFooter.Text = $"SMD Win {appVersion}  •  2026  •  Rulează pe Windows 10/11";

            // Bilingual card definitions: (icon, title_en, title_ro, items_en, items_ro)
            var cardDefs = new[]
            {
                ("🆚",
                 "What Makes SMD Win Different",
                 "Ce Diferențiază SMD Win",
                 new[] {
                     "✦ Single portable .exe — no installation, no dependencies",
                     "✦ Dark / Light theme with Windows 11 Mica effect",
                     "✦ Driver manager with detail card and auto-update blocking",
                     "✦ WiFi saved passwords with one-click copy",
                     "✦ Auto-clean with size preview before deletion",
                     "✦ Full HTML system report with a single click",
                 },
                 new[] {
                     "✦ Un singur .exe portabil — fără instalare, fără dependințe",
                     "✦ Temă Dark / Light cu efect Mica (Windows 11)",
                     "✦ Driver manager cu card detalii + blocare auto-update",
                     "✦ Parole WiFi salvate cu copiere directă",
                     "✦ Auto-clean cu preview dimensiuni înainte de ștergere",
                     "✦ Raport HTML complet al sistemului cu un singur click",
                 }),

                ("📊",
                 "Hardware Monitoring",
                 "Monitorizare Hardware",
                 new[] {
                     "• CPU: usage, frequency, cores, live temperatures",
                     "• RAM: used / available, modules, XMP speed",
                     "• GPU: load, VRAM, temperature, throttling detection",
                     "• Disk I/O in real time + S.M.A.R.T. temperature",
                     "• Network: live traffic, ping, latency graph",
                     "• Battery (laptop): health, wear, autonomy estimate",
                 },
                 new[] {
                     "• CPU: usage, frecvență, nuclee, temperaturi live",
                     "• RAM: utilizat / disponibil, module, viteză XMP",
                     "• GPU: load, VRAM, temperatură, throttling detection",
                     "• Disk I/O în timp real + temperatură S.M.A.R.T.",
                     "• Rețea: trafic live, ping, grafic latență",
                     "• Baterie (laptop): sănătate, uzură, estimare autonomie",
                 }),

                ("🔧",
                 "Tools & Optimization",
                 "Unelte & Optimizare",
                 new[] {
                     "• Shutdown / Sleep timer with quick presets",
                     "• Auto-clean: Temp, Prefetch, WU cache, Recycle Bin",
                     "• Hibernate toggle with automatic disk space reclaim",
                     "• Startup Manager: enable / disable startup programs",
                     "• Windows Services: start / stop system services",
                     "• Integrated PowerShell runner with quick commands",
                 },
                 new[] {
                     "• Shutdown / Sleep timer cu preseturi rapide",
                     "• Auto-clean: Temp, Prefetch, cache WU, Coș reciclare",
                     "• Hibernate on/off cu eliberare automată spațiu",
                     "• Startup Manager: activare/dezactivare programe",
                     "• Windows Services: start/stop servicii de sistem",
                     "• PowerShell runner integrat cu comenzi rapide",
                 }),

                ("🌐",
                 "Network & Security",
                 "Rețea & Securitate",
                 new[] {
                     "• Ping monitor with real-time latency graph",
                     "• Saved WiFi networks with passwords and copy",
                     "• LAN scanner: active devices on the network",
                     "• WiFi analyzer: signal, channel, interference",
                     "• Visual Traceroute + TCP Port scanner",
                     "• Internet speed test (download / upload / ping)",
                 },
                 new[] {
                     "• Ping monitor + grafic latență în timp real",
                     "• Rețele WiFi salvate cu parole și copiere",
                     "• LAN scanner: dispozitive active în rețea",
                     "• WiFi analyzer: semnal, canal, interferențe",
                     "• Traceroute vizual + Port scanner TCP",
                     "• Test viteză internet (download / upload / ping)",
                 }),

                ("🏎",
                 "Benchmark & Diagnostics",
                 "Benchmark & Diagnoză",
                 new[] {
                     "• CPU + GPU simultaneous stress test",
                     "• Disk benchmark: sequential read / write",
                     "• RAM latency + bandwidth measurement",
                     "• BSOD / Crash dump analyzer with stop codes",
                     "• Event Viewer with level and source filtering",
                     "• Full HTML system report (hardware + health)",
                 },
                 new[] {
                     "• Stress test CPU + GPU simultan",
                     "• Disk benchmark: citire/scriere secvențială",
                     "• Măsurare latență + bandwith RAM",
                     "• BSOD / Crash dump analyzer cu stop codes",
                     "• Event Viewer cu filtrare după nivel și sursă",
                     "• Raport HTML complet (hardware + sănătate)",
                 }),

                ("⚠️",
                 "Known Limitations",
                 "Limitări Cunoscute",
                 new[] {
                     "⚠ GPU temperatures may require Administrator rights",
                     "⚠ S.M.A.R.T. unavailable on some NVMe via RAID",
                     "⚠ WiFi passwords require Administrator privileges",
                     "⚠ GPU stress test uses DirectX — results may vary",
                     "⚠ Speed test depends on the automatically selected server",
                     "⚠ Some features are disabled without Admin rights",
                 },
                 new[] {
                     "⚠ Temperaturi GPU pot necesita drepturi Administrator",
                     "⚠ S.M.A.R.T. indisponibil pe unele NVMe via RAID",
                     "⚠ Parolele WiFi necesită drepturi Administrator",
                     "⚠ Stress test GPU folosește DirectX — poate varia",
                     "⚠ Speed test depinde de serverul ales automat",
                     "⚠ Unele funcții sunt dezactivate fără drepturi Admin",
                 }),
            };

            var cardControls = new[] {
                (Ico1, Hdr1, Lst1), (Ico2, Hdr2, Lst2), (Ico3, Hdr3, Lst3),
                (Ico4, Hdr4, Lst4), (Ico5, Hdr5, Lst5), (Ico6, Hdr6, Lst6),
            };

            var cards = cardDefs.Zip(cardControls, (def, ctrl) =>
                (ctrl.Item1, ctrl.Item2, ctrl.Item3, def.Item1,
                 isRo ? def.Item3 : def.Item2,
                 isRo ? def.Item5 : def.Item4)).ToArray();

            foreach (var (ico, hdr, lst, icon, title, items) in cards)
            {
                ico.Text        = icon;
                hdr.Text        = title;
                lst.ItemsSource = items.ToList();

                // Known Limitations card — amber accent
                if (icon == "⚠️")
                    hdr.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(245, 158, 11));
            }
        }
    }
}
