using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow : Window
    {
        // ── EVENTS ────────────────────────────────────────────────────────────

        private DateTime _evtFrom = DateTime.Today.AddDays(-1);
        private DateTime _evtTo   = DateTime.Now.AddDays(1);

        private async void EventTimeRange_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var tag = btn.Tag?.ToString() ?? "24h";

            // Highlight active button — SubTab styles so hover still works
            var activeStyle = (Style)FindResource("SubTabButtonActiveStyle");
            var inactStyle  = (Style)FindResource("SubTabButtonStyle");
            foreach (var b in new[] { BtnEvt1h, BtnEvt6h, BtnEvt24h, BtnEvt7d, BtnEvt30d })
                if (b != null) b.Style = inactStyle;
            btn.Style = activeStyle;

            _evtTo   = DateTime.Now;
            _evtFrom = tag switch
            {
"1h"=> DateTime.Now.AddHours(-1),
"6h"=> DateTime.Now.AddHours(-6),
"24h" => DateTime.Now.AddDays(-1),
"7d"=> DateTime.Now.AddDays(-7),
"30d" => DateTime.Now.AddDays(-30),
                _     => DateTime.Now.AddDays(-1)
            };

            await RunEventScan();
        }

        private async void ScanEvents_Click(object sender, RoutedEventArgs e) => await RunEventScan();

        private string _selectedEventLevel = "All";

        private void EventLevel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            _selectedEventLevel = btn.Tag?.ToString() ?? "All";

            // Update button styles: active = SubTabActive, others = SubTab
            var levelBtns = new[] { BtnLevelAll, BtnLevelCritical, BtnLevelError, BtnLevelWarning, BtnLevelErrWarn };
            var active2 = (Style)FindResource("SubTabButtonActiveStyle");
            var inact2  = (Style)FindResource("SubTabButtonStyle");
            foreach (var b in levelBtns)
                if (b != null) b.Style = b == btn ? active2 : inact2;
        }

        private async Task RunEventScan()
        {
            var level  = _selectedEventLevel;
            var search = TxtSearch.Text.Trim();

            ShowLoading(_L("Scanning Event Log...", "Se scanează Event Log..."));
            EventDetailPanel.Visibility = Visibility.Collapsed;
            try
            {
                _allEvents = await _eventService.GetEventsAsync(_evtFrom, _evtTo, level, search);
                EventsGrid.ItemsSource = _allEvents;
                int errorCount = _allEvents.Count(ev => ev.Level == "Error" || ev.Level == "Critical");
                UpdateSidebarBadge("Events", errorCount);
            }
            finally { HideLoading(); }
        }

        private void EventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EventsGrid.SelectedItem is EventLogEntry entry)
            {
                EventDetailPanel.Visibility = Visibility.Visible;
                TxtEventDetail.Text = $"[{entry.TimeCreated:dd.MM.yyyy HH:mm:ss}]  {entry.Level}  •  {entry.Source}  (EventID: {entry.EventId})\n\n{entry.Message}";
            }
        }

        private async void ExportEvents_Click(object sender, RoutedEventArgs e)
        {
            if (_allEvents.Count == 0) { ShowToastWarning(_L("No data to export.", "Nu există date de exportat.")); return; }
            var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"SMDWin_Events_{DateTime.Now:yyyyMMdd}.csv" };
            if (dlg.ShowDialog() == true)
            {
                await Task.Run(() =>
                {
                    var sb = new StringBuilder("Timp,Nivel,Log,Sursa,EventID,Mesaj\n");
                    foreach (var ev in _allEvents)
                        sb.AppendLine($"\"{ev.TimeCreated:dd.MM.yyyy HH:mm:ss}\",\"{ev.Level}\",\"{ev.LogName}\",\"{ev.Source}\",{ev.EventId},\"{ev.Message.Replace("\"", "'")}\"");
                    File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                });
                ShowToastSuccess(_L("Export saved: ", "Export salvat: ") + dlg.FileName);
            }
        }

        // ── CRASHES ───────────────────────────────────────────────────────────

        private async Task LoadCrashesInternalAsync()
        {
            ShowLoading(_L("Searching crash dumps...", "Se caută crash dump-uri..."));
            try
            {
                var crashes = await _crashService.GetCrashesAsync();
                CrashGrid.ItemsSource = crashes;
                int realCrashes = crashes.Count(c => c.FileName != "Niciun crash detectat");
                UpdateSidebarBadge("Crash", realCrashes);
            }
            finally { HideLoading(); }
        }

        private async void LoadCrashes_Click(object s, RoutedEventArgs e) => await LoadCrashesInternalAsync();
        private void OpenMiniDumpFolder_Click(object s, RoutedEventArgs e) => _crashService.OpenMinidumpFolder();
        private void OpenWinDbg_Click(object s, RoutedEventArgs e)
        {
            if (CrashGrid.SelectedItem is CrashEntry entry && !string.IsNullOrEmpty(entry.FilePath))
                _crashService.OpenWithWinDbg(entry.FilePath);
            else
                ShowToastWarning(_L("Select a file from the list.", "Selectați un fișier din listă."));
        }

        // ── DRIVERS ───────────────────────────────────────────────────────────

        // ── DRIVERS ──────────────────────────────────────────────────────────

        private string _driverViewMode = "Basic";
        private string _driverBasicFilter = "All";
        private List<DeviceManagerEntry> _allDevices = new();
        private DeviceManagerEntry? _selectedDriverEntry = null;
        private List<DeviceManagerEntry> _filteredDevices = new();

        // ─────────────────────────────────────────────────────────────────────────────
        //  Categorii simplificate: mapare DeviceClass → categorie afișată
        // ─────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> _categoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Grafică
            { "Display","Grafică" },
            { "VideoAdaptor","Grafică" },
            // Audio
            { "Media","Audio" },
            { "AudioEndpoint","Audio" },
            // Rețea
            { "Net","Rețea" },
            { "NetClient","Rețea" },
            { "NetService","Rețea" },
            { "NetTrans","Rețea" },
            { "Bluetooth","Bluetooth" },
            // USB & Input
            { "USB","USB" },
            { "HIDClass","Input / HID" },
            { "Mouse","Input / HID" },
            { "Keyboard","Input / HID" },
            // Storage
            { "DiskDrive","Storage" },
            { "SCSIAdapter","Storage" },
            { "HDC","Storage" },
            { "Volume","Storage" },
            { "VolumeSnapshot","Storage" },
            // Sistem
            { "System","Sistem" },
            { "Computer","Sistem" },
            { "Processor","Sistem" },
            { "Battery","Baterie" },
            // Imprimante & imaging
            { "PrintQueue","Imprimantă" },
            { "Image","Cameră / Imagine" },
            { "Camera","Cameră / Imagine" },
            // Altele
            { "SoftwareDevice","Software / Virtual" },
            { "SoftwareComponent","Software / Virtual" },
            { "Extension","Software / Virtual" },
        };

        private static readonly HashSet<string> _importantCategories = new(StringComparer.OrdinalIgnoreCase)
        {
"Grafică", "Audio", "Rețea", "Bluetooth"
        };

        private string GetSimplifiedCategory(DeviceManagerEntry dev)
        {
            if (_categoryMap.TryGetValue(dev.DeviceClass ?? "", out var cat))
                return cat;
            return "Altele";
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Helpers pentru vârsta driverului
        // ─────────────────────────────────────────────────────────────────────────────
        private static (string label, string color, string glow) GetDriverAgeBadge(string? dateStr)
        {
            if (string.IsNullOrEmpty(dateStr) || dateStr == "—")
                return ("?", "#94A3B8", "#94A3B8");

            if (!DateTime.TryParse(dateStr, out var dt))
                return ("?", "#94A3B8", "#94A3B8");

            var age = DateTime.Now - dt;
            int years = (int)(age.TotalDays / 365);
            bool ro = LanguageService.CurrentCode == "ro";

            if (years >= 5)  return (ro ? $"{years}a vechi" : $"{years}y old", "#EF4444", "#EF4444");
            if (years >= 2)  return (ro ? $"{years}a vechi" : $"{years}y old", "#F59E0B", "#F59E0B");
            if (age.TotalDays < 365) return (ro ? "< 1 an" : "< 1y","#22C55E", "#22C55E");
            return ($"{years}y","#94A3B8", "#94A3B8");
        }

        private static (string color, string glowColor, string message) GetDriverStatusLed(DeviceManagerEntry dev, string? dateStr)
        {
            bool ro = LanguageService.CurrentCode == "ro";
            if (dev.IsMissing)
                return ("#EF4444", "#EF4444", ro ? $"Problemă: {dev.Status}" : $"Issue: {dev.Status}");
            if (dev.IsSigned == false)
                return ("#F59E0B", "#F59E0B", ro ? "Driver nesemnat digital" : "Driver not digitally signed");

            if (!string.IsNullOrEmpty(dateStr) && dateStr != "—" && DateTime.TryParse(dateStr, out var dt))
            {
                var years = (DateTime.Now - dt).TotalDays / 365;
                if (years >= 5) return ("#EF4444", "#EF4444", ro ? $"Driver foarte vechi ({(int)years} ani)" : $"Very old driver ({(int)years} years)");
                if (years >= 2) return ("#F59E0B", "#F59E0B", ro ? $"Driver vechi ({(int)years} ani) — recomandat update" : $"Old driver ({(int)years} years) — update recommended");
            }
            return ("#22C55E", "#22C55E", ro ? "Driver OK" : "Driver OK");
        }

        private static string GetPowerEstimate(string category) => category switch
        {
"Grafică"=> "50–350W (în load)",
"Audio"=> "1–5W",
"Rețea"=> "1–3W",
"Bluetooth"=> "0.1–1W",
"USB"=> "depinde de device",
"Storage"=> "2–10W",
"Sistem"=> "—",
            _               => "—"
        };

        private static string GetPowerProfile(string category) => category switch
        {
"Grafică"=> "High Performance / Auto",
"Storage"=> "Balanced (spin-down activ)",
"Bluetooth"=> "Power Save activ",
"Rețea"=> "Balanced",
            _               => "Normal"
        };

        // ─────────────────────────────────────────────────────────────────────────────
        //  LoadAllDriversInternalAsync — punct de intrare principal
        // ─────────────────────────────────────────────────────────────────────────────
        private async Task LoadAllDriversInternalAsync()
        {
            RefreshDriverBlockerUI();
            ApplyDriversLocalization();

            if (_driverViewMode == "Basic")
                await LoadDriversBasicAsync(_driverBasicFilter);
            else
            {
                ShowLoading(_L("Reading drivers...", "Se citesc driverele..."));
                try { DriversGrid.ItemsSource = await _driverService.GetDriversAsync(); }
                finally { HideLoading(); }
            }
        }

        private void ApplyDriversLocalization()
        {
            bool ro = LanguageService.CurrentCode == "ro";
            if (TxtDriversTitle   != null) TxtDriversTitle.Text   = ro ? "Drivere" : "Drivers";
            if (BtnDriverBasicAll      != null) BtnDriverBasicAll.Content      = ro ? "Toate": "All";
            if (BtnDriverBasicMissing  != null) BtnDriverBasicMissing.Content  = ro ? "Probleme": "Issues";
            if (BtnDriverBasicOld      != null) BtnDriverBasicOld.Content      = ro ? "Lipsă": "Missing";
            if (BtnDriverBasicUnsigned != null) BtnDriverBasicUnsigned.Content = ro ? "Nesemnate" : "Unsigned";
            if (BtnDriverBasicImportant!= null) BtnDriverBasicImportant.Content= ro ? "Importante": "Important";
            if (BtnDriverModeBasic     != null) BtnDriverModeBasic.Content     = ro ? "Simplu": "Basic";
            if (BtnDriverModeAdvanced  != null) BtnDriverModeAdvanced.Content  = ro ? "Avansat": "Advanced";
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverMode_Click
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverMode_Click(object s, RoutedEventArgs e)
        {
            // Guard: evenimentul Checked e declanșat din XAML (IsChecked="True") în timpul
            // InitializeComponent(), înainte ca panourile să fie create → NullReferenceException.
            if (DriversBasicPanel == null || DriversAdvancedPanel == null ||
                DriversBasicToolbar == null || DriversAdvancedToolbar == null) return;

            // Accepts RadioButton (new) or Button (legacy)
            string? tag = s is System.Windows.Controls.RadioButton rb ? rb.Tag?.ToString()
                        : s is Button btn2 ? btn2.Tag?.ToString() : null;
            _driverViewMode = tag ?? "Basic";
            bool basic = _driverViewMode == "Basic";

            // RadioButton selection is automatic — no manual style setting needed
            if (BtnDriverModeBasic    is System.Windows.Controls.RadioButton rb1) rb1.IsChecked = basic;
            if (BtnDriverModeAdvanced is System.Windows.Controls.RadioButton rb2) rb2.IsChecked = !basic;

            DriversBasicPanel.Visibility      = basic ? Visibility.Visible  : Visibility.Collapsed;
            DriversAdvancedPanel.Visibility   = basic ? Visibility.Collapsed : Visibility.Visible;
            DriversBasicToolbar.Visibility    = basic ? Visibility.Visible  : Visibility.Collapsed;
            DriversAdvancedToolbar.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;

            HideDriverDetailCard();
            SettingsService.Current.DriverViewMode = _driverViewMode;
            _ = LoadAllDriversInternalAsync();
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverBasicFilter_Click — filtre rapide (Toate / Probleme / Vechi / etc.)
        // ─────────────────────────────────────────────────────────────────────────────
        private async void DriverBasicFilter_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn) _driverBasicFilter = btn.Tag?.ToString() ?? "All";

            var activeStyle = (Style)FindResource("ChipButtonActiveStyle");
            var normalStyle = (Style)FindResource("ChipButtonStyle");

            void SetBtn(Button b, bool active) => b.Style = active ? activeStyle : normalStyle;

            SetBtn(BtnDriverBasicAll,       _driverBasicFilter == "All");
            SetBtn(BtnDriverBasicMissing,   _driverBasicFilter == "Missing");
            SetBtn(BtnDriverBasicOld,       _driverBasicFilter == "NotInstalled");
            SetBtn(BtnDriverBasicUnsigned,  _driverBasicFilter == "Unsigned");
            SetBtn(BtnDriverBasicImportant, _driverBasicFilter == "Important");

            await LoadDriversBasicAsync(_driverBasicFilter);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverSearch_TextChanged — filtrare live după text
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allDevices.Count == 0) return;
            PopulateDriversBasicList(_driverBasicFilter);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverScan_Click — rescanează și invalidează cache
        // ─────────────────────────────────────────────────────────────────────────────
        private async void DriverScan_Click(object s, RoutedEventArgs e)
        {
            _allDevices.Clear();
            HideDriverDetailCard();
            await LoadDriversBasicAsync("All");
            _driverBasicFilter = "All";
            DriverBasicFilter_Click(BtnDriverBasicAll, new RoutedEventArgs());
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  LoadDriversBasicAsync
        // ─────────────────────────────────────────────────────────────────────────────
        private async Task LoadDriversBasicAsync(string filter)
        {
            if (_allDevices.Count > 0)
                PopulateDriversBasicList(filter);

            ShowLoading(_L("Reading devices...", "Se citesc dispozitivele..."));
            try
            {
                if (_allDevices.Count == 0)
                    _allDevices = await _driverService.GetDeviceManagerDevicesAsync();
                else
                {
                    var fresh = await _driverService.GetDeviceManagerDevicesAsync();
                    if (fresh.Count > 0) _allDevices = fresh;
                }
                PopulateDriversBasicList(filter);
                int problemCount = _allDevices.Count(d => d.IsMissing);
                UpdateSidebarBadge("Drivers", problemCount);
            }
            finally { HideLoading(); }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  PopulateDriversBasicList — construiește lista vizuală
        // ─────────────────────────────────────────────────────────────────────────────
        private void PopulateDriversBasicList(string filter)
        {
            string searchText = TxtDriverSearch?.Text?.Trim().ToLowerInvariant() ?? "";

            // Aplică filtrul activ + căutare
            var list = _allDevices.AsEnumerable();

            switch (filter)
            {
                case "Missing":
                    list = list.Where(d => d.IsMissing || d.ErrorCode != 0);
                    break;
                case "NotInstalled":
                    list = list.Where(d => d.IsMissing || d.ErrorCode == 28 ||
                                          (string.IsNullOrEmpty(d.HardwareId) && d.ErrorCode != 0));
                    break;
                case "Unsigned":
                    list = list.Where(d => !d.IsSigned);
                    break;
                case "Important":
                    list = list.Where(d => _importantCategories.Contains(GetSimplifiedCategory(d)));
                    break;
            }

            if (!string.IsNullOrEmpty(searchText))
                list = list.Where(d =>
                    d.Name.ToLowerInvariant().Contains(searchText) ||
                    (d.Manufacturer?.ToLowerInvariant().Contains(searchText) ?? false) ||
                    (d.HardwareId?.ToLowerInvariant().Contains(searchText) ?? false) ||
                    (d.DeviceClass?.ToLowerInvariant().Contains(searchText) ?? false));

            var filtered = list.ToList();
            _filteredDevices = filtered;

            DriversBasicList.Children.Clear();

            // Actualizează contorul
            if (TxtDriverCount != null)
                TxtDriverCount.Text = $"{filtered.Count} device{(filtered.Count != 1 ? "s" : "")}";

            if (!filtered.Any())
            {
                DriversBasicList.Children.Add(new TextBlock
                {
                    Text       = filter == "Missing" ? "Nicio problemă detectată!" : "Niciun device găsit.",
                    Foreground = _brGreen,
                    FontSize   = 13,
                    Margin     = new Thickness(16)
                });
                return;
            }

            bool isDark = ThemeManager.IsDark(SettingsService.Current.ThemeName);

            var clrHdrBg   = isDark ? WpfColor.FromRgb(22, 30, 42)    : WpfColor.FromRgb(235, 241, 248);
            var clrHdrFg   = isDark ? WpfColor.FromRgb(203, 213, 225) : WpfColor.FromRgb(30,  41,  59);
            var clrRow     = isDark ? WpfColor.FromRgb(22, 30, 42)    : WpfColor.FromRgb(255, 255, 255);
            var clrRowAlt  = isDark ? WpfColor.FromRgb(25, 33, 46)    : WpfColor.FromRgb(245, 248, 252);
            var clrErrHdr  = isDark ? WpfColor.FromRgb(80, 25, 25)    : WpfColor.FromRgb(254, 210, 210);
            var clrErrFg   = isDark ? WpfColor.FromRgb(252, 165, 165) : WpfColor.FromRgb(185, 28,  28);
            var clrErrBg   = isDark ? WpfColor.FromRgb(60, 20, 20)    : WpfColor.FromRgb(254, 226, 226);
            var clrDevFg   = isDark ? WpfColor.FromRgb(203, 213, 225) : WpfColor.FromRgb(30,  41,  59);
            var clrSecFg   = WpfColor.FromRgb(100, 116, 139);
            var clrSep     = isDark ? WpfColor.FromArgb(30, 255,255,255) : WpfColor.FromArgb(30, 0,0,0);
            var clrChevron = WpfColor.FromRgb(100, 116, 139);
            var clrWarnBg  = isDark ? WpfColor.FromRgb(60, 45, 0)     : WpfColor.FromRgb(255, 251, 220);
            var clrWarnFg  = isDark ? WpfColor.FromRgb(252, 200, 60)  : WpfColor.FromRgb(146, 64, 14);

            // Grupează după categorie simplificată
            var groups = filtered
                .GroupBy(d => GetSimplifiedCategory(d))
                .OrderBy(g => g.Any(d => d.IsMissing) ? 0 : 1)
                .ThenBy(g => g.Key);

            int rowIndex = 0;

            foreach (var grp in groups)
            {
                bool hasMissing  = grp.Any(d => d.IsMissing || d.ErrorCode != 0);
                bool hasOld      = grp.Any(d =>
                {
                    if (string.IsNullOrEmpty(d.Date) || d.Date == "—") return false;
                    return DateTime.TryParse(d.Date, out var dt) && (DateTime.Now - dt).TotalDays > 730;
                });
                bool hasUnsigned = grp.Any(d => !d.IsSigned);
                int  total       = grp.Count();
                int  problems    = grp.Count(d => d.IsMissing || d.ErrorCode != 0);
                bool startExpanded = hasMissing;

                // ── Header categorie ─────────────────────────────────────────────────
                var catBg = hasMissing ? clrErrHdr : clrHdrBg;
                var catBorder = new Border
                {
                    Background      = new SolidColorBrush(catBg),
                    BorderThickness = new Thickness(0, rowIndex == 0 ? 0 : 1, 0, 0),
                    BorderBrush     = new SolidColorBrush(clrSep),
                    Padding         = new Thickness(10, 8, 10, 8),
                    Cursor          = System.Windows.Input.Cursors.Hand
                };

                var catGrid = new Grid();
                catGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron
                catGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // nome
                catGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // badge

                // Chevron
                var chevron = new TextBlock
                {
                    Text              = startExpanded ? "▼" : "",
                    FontSize          = 8,
                    Foreground        = new SolidColorBrush(clrChevron),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width             = 18,
                    Margin            = new Thickness(0, 0, 4, 0)
                };
                Grid.SetColumn(chevron, 0);
                catGrid.Children.Add(chevron);

                // Categoria
                var catLabel = new TextBlock
                {
                    Text              = grp.Key,
                    FontSize          = 12,
                    FontWeight        = FontWeights.SemiBold,
                    Foreground        = new SolidColorBrush(hasMissing ? clrErrFg : clrHdrFg),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(catLabel, 1);
                catGrid.Children.Add(catLabel);

                // Badge
                string badgeTxt = hasMissing ? $"{problems}" : $"{total}";
                var badgeClr = hasMissing ? WpfColor.FromRgb(196, 43, 28)
                             : (isDark   ? WpfColor.FromRgb(37, 47, 63)
                                         : WpfColor.FromRgb(220, 230, 242));
                var badgeFg = hasMissing ? Colors.White
                            : (isDark    ? WpfColor.FromRgb(148, 163, 184)
                                         : WpfColor.FromRgb(71, 85, 105));
                var badge = new Border
                {
                    CornerRadius      = new CornerRadius(10),
                    Padding           = new Thickness(8, 2, 8, 2),
                    Background        = new SolidColorBrush(badgeClr),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0),
                    Child             = new TextBlock
                    {
                        Text       = badgeTxt,
                        FontSize   = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(badgeFg)
                    }
                };
                Grid.SetColumn(badge, 2);
                catGrid.Children.Add(badge);
                catBorder.Child = catGrid;

                // ── Lista devices din categorie ──────────────────────────────────────
                var devPanel = new StackPanel
                {
                    Visibility = startExpanded ? Visibility.Visible : Visibility.Collapsed
                };

                int devIdx = 0;
                foreach (var dev in grp.OrderBy(d => d.IsMissing ? 0 : 1).ThenBy(d => d.Name))
                {
                    bool isOdd = devIdx % 2 == 1;
                    bool isOld = !string.IsNullOrEmpty(dev.Date) && dev.Date != "—" &&
                                 DateTime.TryParse(dev.Date, out var devDt) &&
                                 (DateTime.Now - devDt).TotalDays > 730;
                    bool isProblem = dev.IsMissing || dev.ErrorCode != 0;
                    bool isUnsigned = !dev.IsSigned;

                    var rowBg = isProblem ? clrErrBg
                              : isUnsigned ? clrWarnBg
                              : (isOdd ? clrRowAlt : clrRow);

                    var devBorder = new Border
                    {
                        Background      = new SolidColorBrush(rowBg),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush     = new SolidColorBrush(clrSep),
                        Padding         = new Thickness(36, 7, 12, 7),
                        Cursor          = System.Windows.Input.Cursors.Hand,
                        Tag             = dev
                    };

                    var devRow = new Grid();
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // LED
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // info
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // age badge
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // action btn

                    // LED indicator
                    string ledClr = isProblem  ? "#EF4444"
                                  : isUnsigned ? "#F59E0B"
                                  : isOld      ? "#F59E0B"
                                  : "#22C55E";
                    var led = new Ellipse
                    {
                        Width = 8, Height = 8,
                        Fill  = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ledClr)!),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    Grid.SetColumn(led, 0);
                    devRow.Children.Add(led);

                    // Info stack: nome + sub
                    var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    infoStack.Children.Add(new TextBlock
                    {
                        Text         = dev.Name,
                        FontSize     = 11,
                        FontWeight   = isProblem ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground   = new SolidColorBrush(isProblem ? clrErrFg : (isUnsigned ? clrWarnFg : clrDevFg)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    string sub = "";
                    if (isProblem)   sub = $"Status: {dev.Status}";
                    else if (isUnsigned) sub = "Nesemnat digital";
                    if (!string.IsNullOrEmpty(dev.HardwareId) && !isProblem)
                        sub = (string.IsNullOrEmpty(sub) ? "" : sub + "·") + dev.HardwareId;

                    if (!string.IsNullOrEmpty(sub))
                        infoStack.Children.Add(new TextBlock
                        {
                            Text       = sub,
                            FontSize   = 9,
                            Foreground = new SolidColorBrush(clrSecFg),
                            Margin     = new Thickness(0, 1, 0, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });

                    Grid.SetColumn(infoStack, 1);
                    devRow.Children.Add(infoStack);

                    // Age badge (doar dacă are dată)
                    if (!string.IsNullOrEmpty(dev.Date) && dev.Date != "—")
                    {
                        var (ageLabel, ageColor, _) = GetDriverAgeBadge(dev.Date);
                        var ageBadge = new Border
                        {
                            CornerRadius      = new CornerRadius(4),
                            Padding           = new Thickness(5, 1, 5, 1),
                            Background        = new SolidColorBrush(WpfColor.FromArgb(30, 0, 0, 0)),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin            = new Thickness(6, 0, 4, 0),
                            Child             = new TextBlock
                            {
                                Text       = ageLabel,
                                FontSize   = 9,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ageColor)!)
                            }
                        };
                        Grid.SetColumn(ageBadge, 2);
                        devRow.Children.Add(ageBadge);
                    }

                    // Buton acțiune (Find Driver dacă lipsă, altfel Search)
                    var actBorder = new Border
                    {
                        CornerRadius      = new CornerRadius(4),
                        Background        = new SolidColorBrush(isProblem
                            ? WpfColor.FromRgb(0, 120, 212)
                            : WpfColor.FromArgb(0, 0, 0, 0)),
                        BorderThickness   = new Thickness(isProblem ? 0 : 1),
                        BorderBrush       = new SolidColorBrush(isDark
                            ? WpfColor.FromArgb(50, 148, 163, 184)
                            : WpfColor.FromArgb(50, 100, 116, 139)),
                        Padding           = new Thickness(8, 3, 8, 3),
                        Margin            = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor            = System.Windows.Input.Cursors.Hand,
                        Child             = new TextBlock
                        {
                            Text       = isProblem ? "Caută Driver" : "Detalii",
                            FontSize   = 10,
                            Foreground = new SolidColorBrush(isProblem ? Colors.White
                                : (isDark ? WpfColor.FromRgb(148, 163, 184) : WpfColor.FromRgb(71, 85, 105)))
                        },
                        Tag = dev
                    };

                    // Click pe buton acțiune: caută driver dacă e problemă, altfel deschide cardul
                    actBorder.MouseLeftButtonUp += (sender, _) =>
                    {
                        if (sender is FrameworkElement fe && fe.Tag is DeviceManagerEntry d)
                        {
                            if (d.IsMissing)
                                SearchDriverOnline(d);
                            else
                                ShowDriverDetailCard(d);
                        }
                    };
                    Grid.SetColumn(actBorder, 3);
                    devRow.Children.Add(actBorder);

                    devBorder.Child = devRow;

                    // Click pe rând = deschide cardul detalii
                    devBorder.MouseLeftButtonUp += (sender, _) =>
                    {
                        if (sender is FrameworkElement fe && fe.Tag is DeviceManagerEntry d)
                            ShowDriverDetailCard(d);
                    };

                    devPanel.Children.Add(devBorder);
                    devIdx++;
                }

                // Toggle expand/collapse categorie
                catBorder.MouseLeftButtonUp += (_, _) =>
                {
                    bool expanded = devPanel.Visibility == Visibility.Visible;
                    devPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                    chevron.Text = expanded ? "" : "▼";
                };

                // Container outer
                var container = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush     = new SolidColorBrush(hasMissing
                        ? WpfColor.FromRgb(196, 43, 28)
                        : (isDark ? WpfColor.FromRgb(37, 47, 63) : WpfColor.FromRgb(203, 213, 225))),
                    CornerRadius    = new CornerRadius(6),
                    Margin          = new Thickness(0, 0, 0, 4),
                    ClipToBounds    = true
                };
                var inner = new StackPanel();
                inner.Children.Add(catBorder);
                inner.Children.Add(devPanel);
                container.Child = inner;

                DriversBasicList.Children.Add(container);
                rowIndex++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  ShowDriverDetailCard — populează și afișează cardul dreapta
        // ─────────────────────────────────────────────────────────────────────────────
        private void ShowDriverDetailCard(DeviceManagerEntry dev)
        {
            _selectedDriverEntry = dev;

            string category = GetSimplifiedCategory(dev);

            // Populează câmpurile
            TxtDetailIcon.Text     = category.Length > 2 ? category[..2].Trim() : "";
            TxtDetailName.Text     = dev.Name;
            TxtDetailCategory.Text = category;

            string version  = dev.Version  ?? "—";
            string provider = dev.Manufacturer ?? "—";
            string date     = dev.Date     ?? "—";

            TxtDetailVersion.Text  = version;
            TxtDetailProvider.Text = provider;
            TxtDetailDate.Text     = date;

            // Age badge
            var (ageLabel, ageColor, _) = GetDriverAgeBadge(date);
            TxtDetailAge.Text      = ageLabel;
            TxtDetailAge.Foreground = new SolidColorBrush(
                (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ageColor)!);
            DetailAgeBadge.Background = new SolidColorBrush(
                WpfColor.FromArgb(30, 0, 0, 0));

            // Status
            TxtDetailStatus.Text = dev.ErrorCode == 0 ? "OK"
                                 : dev.Status;
            TxtDetailStatus.Foreground = dev.ErrorCode == 0
                ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                : new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));

            // Semnat
            bool signed = dev.IsSigned;
            TxtDetailSigned.Text = signed ? "Da" : "Nesemnat";
            TxtDetailSigned.Foreground = signed
                ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                : new SolidColorBrush(WpfColor.FromRgb(245, 158, 11));

            // Hardware ID
            TxtDetailHwId.Text = string.IsNullOrEmpty(dev.HardwareId) ? "—" : dev.HardwareId;

            // LED status
            var (ledClr, glowClr, msg) = GetDriverStatusLed(dev, date);
            var ledColor = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ledClr)!;
            DetailLed.Fill = new SolidColorBrush(ledColor);
            DetailLedGlow.Color = ledColor;
            TxtDetailStatusMsg.Text = msg;
            TxtDetailStatusMsg.Foreground = new SolidColorBrush(ledColor);

            // Energie
            TxtDetailPowerProfile.Text = GetPowerProfile(category);
            TxtDetailPowerEst.Text     = GetPowerEstimate(category);

            // Afișează coloana cardului animat
            DriverDetailCard.Visibility = Visibility.Visible;
            DriverDetailColumn.Width    = new GridLength(300);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  HideDriverDetailCard
        // ─────────────────────────────────────────────────────────────────────────────
        private void HideDriverDetailCard()
        {
            DriverDetailCard.Visibility  = Visibility.Collapsed;
            DriverDetailColumn.Width     = new GridLength(0);
            _selectedDriverEntry = null;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Helper: căutare online driver
        // ─────────────────────────────────────────────────────────────────────────────
        private void SearchDriverOnline(DeviceManagerEntry dev)
        {
            string site = SettingsService.Current.DriverSearchSite ?? "driverpack";
            string url;
            string hwId = dev.HardwareId ?? "";
            string name = dev.Name;

            if (site == "google")
            {
                string q = string.IsNullOrEmpty(hwId) ? name + " driver" : hwId + " driver download";
                url = $"https://www.google.com/search?q={Uri.EscapeDataString(q)}";
            }
            else
            {
                if (!string.IsNullOrEmpty(hwId))
                {
                    string trimmedHwId = hwId;
                    try
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            hwId, @"^([^\\]+\\VEN_[0-9A-Fa-f]+&DEV_[0-9A-Fa-f]+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) trimmedHwId = m.Groups[1].Value;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    url = $"https://driverpack.io/en/search?query={Uri.EscapeDataString(trimmedHwId)}";
                }
                else
                {
                    url = $"https://www.google.com/search?q={Uri.EscapeDataString(name + " driver site:driverpack.io")}";
                }
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Butoane card detalii
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverDetailClose_Click(object s, RoutedEventArgs e)
            => HideDriverDetailCard();

        private void DetailSearchOnline_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry != null)
                SearchDriverOnline(_selectedDriverEntry);
        }

        private void CopyDeviceId_Click(object s, RoutedEventArgs e)
        {
            string hwId = TxtDetailHwId.Text;
            if (!string.IsNullOrEmpty(hwId) && hwId != "—")
            {
                System.Windows.Clipboard.SetText(hwId);
                ShowToastSuccess("Device ID copiat în clipboard!");
            }
        }

        private void DetailBlockDriver_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;
            string hwId = _selectedDriverEntry.HardwareId ?? "";
            if (string.IsNullOrEmpty(hwId))
            {
                ShowToastWarning("Acest device nu are un Hardware ID disponibil.");
                return;
            }
            try
            {
                // Trim to VEN_&DEV_ for cleaner match
                string trimmed = hwId;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        hwId, @"^([^\\]+\\VEN_[0-9A-Fa-f]+&DEV_[0-9A-Fa-f]+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) trimmed = m.Groups[1].Value;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                SMDWin.Services.DriverUpdateBlocker.BlockDevice(trimmed);
                ShowToastSuccess($"Driver blocat: {trimmed.Split('\\').LastOrDefault() ?? trimmed}");
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show("Necesită drepturi de Administrator.\nRulează SMDWin ca Administrator.", "Blochează Driver", AppDialog.Kind.Warning);
            }
        }

        private void DetailBackup_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;
            // Export driver specific via pnputil (necesită admin)
            string name = _selectedDriverEntry.Name.Replace(" ", "_");
            string dest = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"DriverBackup_{name}");
            string cmd = $"Export-WindowsDriver -Online -Destination \"{dest}\"";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{cmd}\"",
                    UseShellExecute  = true,
                    Verb = "runas"
                });
                ShowToastSuccess($"Backup pornit → {System.IO.Path.GetFileName(dest)}");
            }
            catch (Exception ex)
            {
                ShowToastError($"Eroare backup: {ex.Message}");
            }
        }

        private void DetailRepair_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;
            _driverService.OpenDeviceManager();
        }



        // ─────────────────────────────────────────────────────────────────────────────
        //  Handlers păstrați din original
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverSearchOnline_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DeviceManagerEntry dev) return;
            SearchDriverOnline(dev);
        }

        private async void LoadAllDrivers_Click(object s, RoutedEventArgs e)
            => await LoadAllDriversInternalAsync();

        private void OpenDeviceManager_Click(object s, RoutedEventArgs e)
            => _driverService.OpenDeviceManager();

        private async void LoadUnsignedDrivers_Click(object s, RoutedEventArgs e)
        {
            ShowLoading(_L("Searching unsigned drivers...", "Se caută drivere nesemnate..."));
            try
            {
                var drivers = await _driverService.GetUnsignedDriversAsync();
                DriversGrid.ItemsSource = drivers;
                if (drivers.Count == 0)
                    ShowToastSuccess(_L("No unsigned drivers found!", "Nu s-au găsit drivere nesemnate!"));
            }
            finally { HideLoading(); }
        }


        // ── Driver Update Blocker ─────────────────────────────────────────────

        private void RefreshDriverBlockerUI()
        {
            // Global block status
            bool globalBlocked = SMDWin.Services.DriverUpdateBlocker.IsGlobalBlocked();
            var greenColor = WpfColor.FromRgb(34, 197, 94);
            var redColor   = WpfColor.FromRgb(239, 68, 68);

            if (TxtGlobalBlockStatus != null)
            {
                TxtGlobalBlockStatus.Text       = globalBlocked ? "BLOCKED by policy" : "Auto-update ON";
                TxtGlobalBlockStatus.Foreground = globalBlocked
                    ? new SolidColorBrush(redColor)
                    : new SolidColorBrush(greenColor);
            }
            // LED indicator
            if (LedBlockStatus != null)
            {
                LedBlockStatus.Fill = globalBlocked
                    ? new SolidColorBrush(redColor)
                    : new SolidColorBrush(greenColor);
                if (LedBlockStatus.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
                    fx.Color = globalBlocked ? redColor : greenColor;
            }
            if (BtnToggleGlobalBlock != null)
                BtnToggleGlobalBlock.Content = globalBlocked
                    ? "Restore Auto-Update"
                    : "Block ALL Updates";

            // Per-device blocked list
            var blocked = SMDWin.Services.DriverUpdateBlocker.GetBlockedDevices();
            if (TxtBlockedCount != null)
                TxtBlockedCount.Text = blocked.Count == 0
                    ? "0 devices blocked"
                    : $"{blocked.Count} device{(blocked.Count > 1 ? "s" : "")} blocked";

            if (BlockedDevicesList != null)
                BlockedDevicesList.ItemsSource = blocked.Select(x => new BlockedDriverItem
                {
                    Index      = x.index,
                    HardwareId = x.hwid,
                    DeviceName = FriendlyNameForHwId(x.hwid),
                }).ToList();

            // Show/hide blocked devices card
            if (BlockedDevicesCard != null)
                BlockedDevicesCard.Visibility = blocked.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;

            if (TxtNoBlockedDrivers != null)
                TxtNoBlockedDrivers.Visibility = blocked.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FriendlyNameForHwId(string hwid)
        {
            // Try to find a display name via WMI matching
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_PnPEntity WHERE HardwareID = '{hwid.Replace("'","\\'")}' OR HardwareID LIKE '%{hwid.Split('&')[0].Replace("'","\\'")}%'");
                foreach (System.Management.ManagementObject o in s.Get())
                    return o["Name"]?.ToString() ?? hwid;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return hwid;
        }

        private void BlockGpuDriver_Click(object s, RoutedEventArgs e)
        {
            // Find GPU hardware IDs
            var gpuIds = SMDWin.Services.DriverUpdateBlocker.GetHardwareIds("NVIDIA")
                .Concat(SMDWin.Services.DriverUpdateBlocker.GetHardwareIds("AMD Radeon"))
                .Concat(SMDWin.Services.DriverUpdateBlocker.GetHardwareIds("Intel Arc"))
                .Where(id => id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();

            if (gpuIds.Count == 0)
            {
                AppDialog.Show(
                    _L("Could not find a GPU device ID.\nMake sure your GPU driver is installed.", "GPU device ID not found.\nCheck that the GPU driver is installed."),
"WinDiag");
                return;
            }

            // Use the most specific ID (first)
            string id = gpuIds[0];
            if (SMDWin.Services.DriverUpdateBlocker.IsDeviceBlocked(id))
            {
                AppDialog.Show(
                    _L($"GPU driver is already blocked:\n{id}", $"Driver GPU deja blocat:\n{id}"),
"WinDiag");
                return;
            }

            try
            {
                bool ok = SMDWin.Services.DriverUpdateBlocker.BlockDevice(id);
                if (ok)
                {
                    AppDialog.Show(
                        _L($"✓ GPU driver blocked from auto-update:\n{id}\n\nWindows Update will no longer replace this driver.",
                           $"✓ Driver GPU blocat:\n{id}\n\nWindows Update nu va mai înlocui acest driver."),
"WinDiag");
                    RefreshDriverBlockerUI();
                }
                else
                {
                    AppDialog.Show(
                        _L("Failed to write registry. Run WinDiag as Administrator.", "Could not write to registry. Run SMD Win as Administrator."),
"WinDiag", AppDialog.Kind.Warning);
                }
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _L("Requires Administrator privileges.\nRight-click WinDiag → Run as administrator.", "Necesită drepturi de Administrator.\nClick dreapta WinDiag → Rulează ca administrator."),
"WinDiag", AppDialog.Kind.Warning);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // AUTO-CLEAN TOOL
        // ══════════════════════════════════════════════════════════════════════

        private Dictionary<string, long> _cleanSizes = new();

        private void AutoClean_Click(object s, RoutedEventArgs e)
        {
            bool isLight = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.SettingsService.Current.ThemeName) ||
                           (SMDWin.Services.SettingsService.Current.ThemeName == "Auto" &&
                            !SMDWin.Services.ThemeManager.IsDark(SMDWin.Services.ThemeManager.ResolveAuto()));

            var dlg = new AutoCleanDialog(isLight)
            {
                Owner       = this,
                FormatBytes = FormatBytes,
                ScanAction  = () => Task.Run(() =>
                {
                    var sizes = new Dictionary<string, long>();
                    sizes["Temp"]       = GetFolderSize(System.IO.Path.GetTempPath())
                                        + GetFolderSize(@"C:\Windows\Temp");
                    sizes["Prefetch"]   = GetFolderSize(@"C:\Windows\Prefetch");
                    sizes["Thumb"]      = GetFolderSize(System.IO.Path.Combine(
                                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                    sizes["WinUpdate"]  = GetFolderSize(@"C:\Windows\SoftwareDistribution\Download");
                    sizes["EventLog"]   = GetFolderSize(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                    sizes["RecycleBin"] = GetRecycleBinSize();
                    return sizes;
                }),
                CleanAction = async (selected) =>
                {
                    long freed = 0;
                    await Task.Run(() =>
                    {
                        if (selected.GetValueOrDefault("Temp"))
                        { freed += DeleteFolder(System.IO.Path.GetTempPath()); freed += DeleteFolder(@"C:\Windows\Temp"); }
                        if (selected.GetValueOrDefault("Prefetch"))
                            freed += DeleteFolder(@"C:\Windows\Prefetch");
                        if (selected.GetValueOrDefault("Thumb"))
                            freed += DeleteFiles(System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                        if (selected.GetValueOrDefault("WinUpdate"))
                            freed += DeleteFolder(@"C:\Windows\SoftwareDistribution\Download");
                        if (selected.GetValueOrDefault("EventLog"))
                            freed += DeleteFiles(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                        if (selected.GetValueOrDefault("RecycleBin"))
                            EmptyRecycleBin();
                    });
                    return freed;
                },
            };
            dlg.ShowDialog();
        }

        private static long GetFolderSize(string path, string pattern = "*")
        {
            try
            {
                if (!System.IO.Directory.Exists(path)) return 0;
                return System.IO.Directory.EnumerateFiles(path, pattern, System.IO.SearchOption.AllDirectories)
                    .Sum(f => { try { return new System.IO.FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        private static long DeleteFolder(string path)
        {
            long freed = 0;
            try
            {
                if (!System.IO.Directory.Exists(path)) return 0;
                foreach (var f in System.IO.Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories))
                    try { var fi = new System.IO.FileInfo(f); freed += fi.Length; fi.Delete(); } catch { }
                foreach (var d in System.IO.Directory.EnumerateDirectories(path))
                    try { System.IO.Directory.Delete(d, true); } catch { }
            }
            catch { }
            return freed;
        }

        private static long DeleteFiles(string path, string pattern)
        {
            long freed = 0;
            try
            {
                if (!System.IO.Directory.Exists(path)) return 0;
                foreach (var f in System.IO.Directory.EnumerateFiles(path, pattern))
                    try { var fi = new System.IO.FileInfo(f); freed += fi.Length; fi.Delete(); } catch { }
            }
            catch { }
            return freed;
        }

        private static long GetRecycleBinSize()
        {
            try
            {
                long total = 0;
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var rb = System.IO.Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                    total += GetFolderSize(rb);
                }
                return total;
            }
            catch { return 0; }
        }

        [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private static void EmptyRecycleBin()
        {
            try { SHEmptyRecycleBinW(IntPtr.Zero, null, 0x00000007); } catch (Exception logEx) { AppLogger.Warning(logEx, "SHEmptyRecycleBinW(IntPtr.Zero, null, 0x00000007);"); }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ══════════════════════════════════════════════════════════════════════
        // HIBERNATE
        // ══════════════════════════════════════════════════════════════════════

        private async void ToggleHibernate_Click(object s, RoutedEventArgs e)
        {
            bool isEnabled = await Task.Run(() =>
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"powercfg", "/query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    // Simpler: check if hiberfil.sys exists
                    return System.IO.File.Exists(@"C:\hiberfil.sys");
                }
                catch { return false; }
            });

            if (isEnabled)
            {
                if (!AppDialog.Confirm(
                    _L("Disabling hibernate will delete hiberfil.sys and free disk space.\n\nSleep mode will remain available.\n\nContinue?", "Dezactivarea hibernării va șterge hiberfil.sys și va elibera spațiu pe disc.\n\nOpțiunea Sleep va rămâne disponibilă.\n\nContinuați?"),
"Dezactivare Hibernate")) return;
                RunPowercfg("/h off");
                ShowToastSuccess(_L("Hibernate disabled. Disk space freed!", "Hibernate dezactivat. Spațiu eliberat!"));
            }
            else
            {
                RunPowercfg("/h on");
                ShowToastSuccess(_L("Hibernate enabled.", "Hibernate activat."));
            }
            await Task.Delay(500);
            RefreshHibernateStatus();
        }

        private static void RunPowercfg(string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"powercfg", args)
                { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        internal async void RefreshHibernateStatus()
        {
            bool exists = await Task.Run(() => System.IO.File.Exists(@"C:\hiberfil.sys"));
            long sizeBytes = 0;
            if (exists)
                try { sizeBytes = new System.IO.FileInfo(@"C:\hiberfil.sys").Length; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

            if (TxtHibernateStatus != null)
            {
                TxtHibernateStatus.Text = exists ? _L("Enabled", "Activat") : _L("Disabled", "Dezactivat");
                TxtHibernateStatus.Foreground = exists
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
            }
            if (TxtHibernateInfo != null)
                TxtHibernateInfo.Text = exists
                    ? ($"hiberfil.sys: {FormatBytes(sizeBytes)} " + _L("used on disk (C:\\)", "pe disc (C:\\)"))
                    : _L("Hibernate disabled — hiberfil.sys not found", "Hibernate dezactivat — hiberfil.sys nu există");
            if (BtnToggleHibernate != null)
            {
                // Button content is a StackPanel - update the TextBlock label inside
                var lbl = BtnToggleHibernate.FindName("_HibBtnLbl") as System.Windows.Controls.TextBlock;
                if (lbl == null)
                {
                    // fallback: search visual tree
                    foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(BtnToggleHibernate))
                    {
                        lbl = tb; break;
                    }
                }
                if (lbl != null)
                    lbl.Text = exists ? _L("Disable Hibernate", "Dezactivează Hibernate") : _L("Enable Hibernate", "Activează Hibernate");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // WIFI SAVED NETWORKS
        // ══════════════════════════════════════════════════════════════════════

        private record WifiEntry(string Ssid, string Password, string Auth, string Encryption);
        private List<WifiEntry> _wifiEntries = new();

        private async void WifiScan_Click(object s, RoutedEventArgs e)
        {
            if (BtnWifiScan != null) { BtnWifiScan.IsEnabled = false; BtnWifiScan.Content = _L("Reading...", "Se citesc..."); }
            TxtWifiEmpty?.SetValue(System.Windows.UIElement.VisibilityProperty, Visibility.Collapsed);
            WifiPasswordsList?.Children.Clear();
            ShowLoading(_L("Reading saved WiFi networks...", "Se citesc rețelele WiFi..."), "");

            try
            {
                var entries = await Task.Run(ReadWifiProfiles);
                _wifiEntries = entries;
                HideLoading();
                BuildWifiList(entries);
                if (entries.Count == 0 && TxtWifiEmpty != null)
                {
                    TxtWifiEmpty.Text       = _L("No saved WiFi networks found, or Admin rights required.", "Nu s-au găsit rețele WiFi salvate sau lipsesc drepturile Admin.");
                    TxtWifiEmpty.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                HideLoading();
                ShowToastError(_L("WiFi read error:", "Eroare citire WiFi:") + " " + ex.Message);
                if (TxtWifiEmpty != null) { TxtWifiEmpty.Text = ex.Message; TxtWifiEmpty.Visibility = Visibility.Visible; }
            }
            finally
            {
                if (BtnWifiScan != null) { BtnWifiScan.IsEnabled = true; BtnWifiScan.Content = _L("Scan", "Scanează"); }
            }
        }

        private static List<WifiEntry> ReadWifiProfiles()
        {
            var result = new List<WifiEntry>();
            try
            {
                var listProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"netsh", "wlan show profiles")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                string listOut = listProc?.StandardOutput.ReadToEnd() ?? "";
                listProc?.WaitForExit();

                foreach (var line in listOut.Split('\n'))
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0 || !line.Contains("All User Profile")) continue;
                    string ssid = line[(colonIdx + 1)..].Trim();
                    if (string.IsNullOrEmpty(ssid)) continue;

                    var detailProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"netsh", $"wlan show profile name=\"{ssid}\" key=clear")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    string detail = detailProc?.StandardOutput.ReadToEnd() ?? "";
                    detailProc?.WaitForExit();

                    string password = "—", auth = "—", encryption = "—";
                    foreach (var dl in detail.Split('\n'))
                    {
                        if (dl.Contains("Key Content")   && dl.Contains(':')) password   = dl[(dl.IndexOf(':') + 1)..].Trim();
                        if (dl.Contains("Authentication") && dl.Contains(':') && !dl.Contains("Network")) auth = dl[(dl.IndexOf(':') + 1)..].Trim();
                        if (dl.Contains("Cipher")        && dl.Contains(':')) encryption = dl[(dl.IndexOf(':') + 1)..].Trim();
                    }
                    result.Add(new WifiEntry(ssid, password, auth, encryption));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            return result;
        }

        private void BuildWifiList(List<WifiEntry> entries)
        {
            WifiPasswordsList?.Children.Clear();
            if (entries.Count == 0) return;

            bool isDark = ThemeManager.IsDark(SettingsService.Current.ThemeName);
            var clrRow    = isDark ? WpfColor.FromRgb(22, 30, 42)   : WpfColor.FromRgb(255, 255, 255);
            var clrRowAlt = isDark ? WpfColor.FromRgb(25, 33, 46)   : WpfColor.FromRgb(245, 248, 252);
            var clrSep    = isDark ? WpfColor.FromArgb(25, 255,255,255) : WpfColor.FromArgb(25, 0,0,0);
            var clrPrimary = isDark ? WpfColor.FromRgb(203,213,225) : WpfColor.FromRgb(30,41,59);
            var clrSec     = WpfColor.FromRgb(100, 116, 139);
            var clrReveal  = isDark ? WpfColor.FromRgb(96, 165, 250) : WpfColor.FromRgb(37, 99, 235);

            int idx = 0;
            foreach (var wifi in entries)
            {
                bool isOdd = idx % 2 == 1;
                bool hasPassword = wifi.Password != "—" && !string.IsNullOrEmpty(wifi.Password);
                bool revealed = false;

                var row = new Border
                {
                    Background      = new SolidColorBrush(isOdd ? clrRowAlt : clrRow),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush     = new SolidColorBrush(clrSep),
                    Padding         = new Thickness(12, 8, 12, 8),
                    Cursor          = hasPassword ? System.Windows.Input.Cursors.Hand : null,
                    ToolTip         = hasPassword ? _L("Double-click to show / hide password", "Dublu-click pentru a vedea / ascunde parola") : null
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

                // SSID
                var ssidBlock = new TextBlock { Text = wifi.Ssid, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(clrPrimary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(ssidBlock, 0); grid.Children.Add(ssidBlock);

                // Password (masked) — no click handler here, use row double-click
                var pwdBlock = new TextBlock
                {
                    Text       = hasPassword ? new string('•', Math.Min(wifi.Password.Length, 10)) : "—",
                    FontSize   = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(hasPassword ? clrPrimary : clrSec),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = wifi.Password
                };
                Grid.SetColumn(pwdBlock, 1); grid.Children.Add(pwdBlock);

                // Auth
                var authBlock = new TextBlock { Text = wifi.Auth, FontSize = 10, Foreground = new SolidColorBrush(clrSec),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(authBlock, 2); grid.Children.Add(authBlock);

                // Actions
                var actPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (hasPassword)
                {
                    string capturedPwd2 = wifi.Password;
                    var copyBtn = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Background   = new SolidColorBrush(isDark ? WpfColor.FromArgb(50,80,130,220) : WpfColor.FromArgb(40,37,99,235)),
                        BorderBrush  = new SolidColorBrush(isDark ? WpfColor.FromArgb(80,96,165,250) : WpfColor.FromArgb(100,37,99,235)),
                        BorderThickness = new Thickness(1),
                        Padding      = new Thickness(9, 4, 9, 4),
                        Cursor       = System.Windows.Input.Cursors.Hand,
                        ToolTip      = _L("Copy password", "Copiază parola"),
                        Child        = new TextBlock
                        {
                            Text       = _L("Copy", "Copiază"),
                            FontSize   = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(isDark ? WpfColor.FromRgb(147,197,253) : WpfColor.FromRgb(37,99,235)),
                        }
                    };
                    copyBtn.MouseLeftButtonUp += (_, _) =>
                    {
                        System.Windows.Clipboard.SetText(capturedPwd2);
                        ShowToastSuccess(_L("Password copied!", "Parolă copiată!"));
                    };
                    actPanel.Children.Add(copyBtn);

                    // QR button
                    string capturedSsid = wifi.Ssid;
                    var qrBtn = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Background   = new SolidColorBrush(isDark ? WpfColor.FromArgb(50,60,160,100) : WpfColor.FromArgb(40,22,163,74)),
                        BorderBrush  = new SolidColorBrush(isDark ? WpfColor.FromArgb(80,74,222,128) : WpfColor.FromArgb(100,22,163,74)),
                        BorderThickness = new Thickness(1),
                        Padding      = new Thickness(9, 4, 9, 4),
                        Margin       = new Thickness(6, 0, 0, 0),
                        Cursor       = System.Windows.Input.Cursors.Hand,
                        ToolTip      = _L("Show QR code to share WiFi", "Arată QR code pentru partajare WiFi"),
                        Child        = new TextBlock
                        {
                            Text       = "QR",
                            FontSize   = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(isDark ? WpfColor.FromRgb(74,222,128) : WpfColor.FromRgb(22,163,74)),
                        }
                    };
                    qrBtn.MouseLeftButtonUp += (s, _) =>
                    {
                        if (s is Border b) { b.Tag = capturedSsid; ShowWifiQr_Click(
                            new System.Windows.Controls.Button { Tag = capturedSsid }, new RoutedEventArgs()); }
                    };
                    actPanel.Children.Add(qrBtn);

                    // Double-click on row to reveal password
                    string capturedPwdRow = wifi.Password;
                    row.MouseLeftButtonDown += (_, mouseArgs) =>
                    {
                        if (mouseArgs.ClickCount == 2)
                        {
                            revealed = !revealed;
                            pwdBlock.Text = revealed ? capturedPwdRow : new string('•', Math.Min(capturedPwdRow.Length, 10));
                            pwdBlock.Foreground = new SolidColorBrush(revealed ? clrReveal : clrPrimary);
                        }
                    };
                }
                Grid.SetColumn(actPanel, 3); grid.Children.Add(actPanel);
                row.Child = grid;
                WifiPasswordsList?.Children.Add(row);
                idx++;
            }
            if (TxtWifiEmpty != null) TxtWifiEmpty.Visibility = Visibility.Collapsed;
        }

        private void WifiExportCsv_Click(object s, RoutedEventArgs e)
        {
            if (_wifiEntries.Count == 0) { ShowToastWarning(_L("Scan WiFi networks first.", "Scanează mai întâi.")); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "wifi_passwords", DefaultExt = ".csv", Filter = "CSV files|*.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("SSID,Password,Authentication,Encryption");
                foreach (var w in _wifiEntries)
                    sb.AppendLine($"\"{w.Ssid}\",\"{w.Password}\",\"{w.Auth}\",\"{w.Encryption}\"");
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                ShowToastSuccess("" + _L("Exported:", "Exportat:") + " " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { ShowToastError(_L("Export error:", "Eroare export:") + " " + ex.Message); }
        }

        private void BlockCustomDriver_Click(object s, RoutedEventArgs e)
        {
            // Show a picker from the current device list
            var devices = _allDevices ?? new List<SMDWin.Models.DeviceManagerEntry>();
            if (devices.Count == 0)
            {
                AppDialog.Show(
                    _L("Load the device list first (Device Manager tab).",
"Încarcă lista de dispozitive mai întâi (tab Device Manager)."),
"WinDiag");
                return;
            }

            // Simple input dialog asking for device name
            var input = Microsoft.VisualBasic.Interaction.InputBox(
                _L("Enter part of the device name to search for its Hardware ID:\n(e.g. 'NVIDIA', 'Realtek', 'Intel Wireless')",
"Introdu o parte din numele dispozitivului:\n(ex: 'NVIDIA', 'Realtek', 'Intel Wireless')"),
"WinDiag — Block Driver",
"");

            if (string.IsNullOrWhiteSpace(input)) return;

            var hwIds = SMDWin.Services.DriverUpdateBlocker.GetHardwareIds(input)
                .Where(id => id.Contains('\\'))
                .ToList();

            if (hwIds.Count == 0)
            {
                AppDialog.Show(
                    _L($"No Hardware IDs found for '{input}'.\nCheck the device name in Device Manager.",
                       $"No Hardware IDs found for '{input}'."),
"WinDiag", AppDialog.Kind.Warning);
                return;
            }

            try
            {
                SMDWin.Services.DriverUpdateBlocker.BlockDevice(hwIds[0]);
                AppDialog.Show(
                    _L($"✓ Blocked: {hwIds[0]}", $"✓ Blocat: {hwIds[0]}"),
"WinDiag");
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _L("Requires Administrator privileges.", "Necesită drepturi de Administrator."),
"WinDiag", AppDialog.Kind.Warning);
            }
        }

        private void UnblockDriver_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn && btn.Tag is string idx)
            {
                try
                {
                    SMDWin.Services.DriverUpdateBlocker.UnblockDevice(idx);
                    RefreshDriverBlockerUI();
                }
                catch (UnauthorizedAccessException)
                {
                    AppDialog.Show(
                        _L("Requires Administrator privileges.", "Necesită drepturi de Administrator."),
"WinDiag", AppDialog.Kind.Warning);
                }
            }
        }

        private void ToggleGlobalDriverBlock_Click(object s, RoutedEventArgs e)
        {
            bool currentlyBlocked = SMDWin.Services.DriverUpdateBlocker.IsGlobalBlocked();
            string msg = currentlyBlocked
                ? _L("This will RESTORE Windows Update driver installation globally.\n\nContinue?",
"This will RESTORE automatic driver installation via Windows Update.\n\nContinue?")
                : _L("This will BLOCK Windows Update from installing ANY drivers automatically.\n\nAggressive: use per-device blocking when possible.\n\nContinue?",
"Va BLOCA Windows Update să instaleze ORICE driver automat.\n\nAgresiv: preferă blocarea per-dispozitiv.\n\nContinui?");

            if (!AppDialog.Confirm(msg, "WinDiag"))
                return;

            try
            {
                SMDWin.Services.DriverUpdateBlocker.ToggleGlobalBlock(!currentlyBlocked);
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _L("Requires Administrator privileges.", "Necesită drepturi de Administrator."),
"WinDiag", AppDialog.Kind.Warning);
            }
        }



        // ══════════════════════════════════════════════════════════════════════
        // EXPORT DRIVERS CSV
        // ══════════════════════════════════════════════════════════════════════
        private void ExportDriversCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_allDevices == null || _allDevices.Count == 0)
            { ShowToastWarning(_L("Scan drivers first.", "Scanează driverele mai întâi.")); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = $"drivers_{System.Environment.MachineName}_{DateTime.Now:yyyyMMdd}",
                DefaultExt = ".csv",
                Filter     = "CSV files|*.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                static string esc(string? v) => $"\"{(v ?? "").Replace("\"", "\"\"")}\"";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Name,Class,Manufacturer,Version,Date,Status,Signed,HardwareId,DeviceId");
                foreach (var d in _allDevices)
                    sb.AppendLine(string.Join(",",
                        esc(d.Name), esc(d.DeviceClass), esc(d.Manufacturer),
                        esc(d.Version), esc(d.Date),
                        esc(d.IsMissing ? "Missing" : d.Status),
                        esc(d.IsSigned ? "Yes" : "No"),
                        esc(d.HardwareId), esc(d.DeviceId)));

                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                ShowToastSuccess("" + _L("Exported:", "Exportat:") + " " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { ShowToastError(_L("Export error:", "Eroare export:") + " " + ex.Message); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // USB DEVICE HISTORY
        // ══════════════════════════════════════════════════════════════════════
        private record UsbDeviceRecord(string Name, string Manufacturer, string Serial, string DeviceType);
        private List<UsbDeviceRecord> _usbDevices = new();

        private async void UsbScan_Click(object sender, RoutedEventArgs e)
        {
            if (BtnUsbScan != null) { BtnUsbScan.IsEnabled = false; BtnUsbScan.Content = _L("Reading...", "Se citesc..."); }
            if (TxtUsbEmpty != null) TxtUsbEmpty.Visibility = Visibility.Collapsed;
            UsbDeviceList?.Children.Clear();
            ShowLoading(_L("Reading USB history from registry...", "Se citesc dispozitivele USB..."), "");
            try
            {
                var devices = await Task.Run(ReadUsbHistory);
                _usbDevices = devices;
                HideLoading();
                BuildUsbList(devices);
                if (devices.Count == 0 && TxtUsbEmpty != null)
                { TxtUsbEmpty.Text = _L("No USB device history found.", "Nu s-au găsit dispozitive USB."); TxtUsbEmpty.Visibility = Visibility.Visible; }
            }
            catch (Exception ex)
            {
                HideLoading();
                ShowToastError(_L("Error reading USB history:", "Eroare USB:") + " " + ex.Message);
                if (TxtUsbEmpty != null) { TxtUsbEmpty.Text = ex.Message; TxtUsbEmpty.Visibility = Visibility.Visible; }
            }
            finally
            {
                if (BtnUsbScan != null) { BtnUsbScan.IsEnabled = true; BtnUsbScan.Content = _L("Scan", "Scan"); }
            }
        }

        private static List<UsbDeviceRecord> ReadUsbHistory()
        {
            var result = new List<UsbDeviceRecord>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root   = Microsoft.Win32.Registry.LocalMachine;

            var hives = new[] {
                (@"SYSTEM\CurrentControlSet\Enum\USBSTOR", "Storage"),
                (@"SYSTEM\CurrentControlSet\Enum\USB","USB"),
                (@"SYSTEM\CurrentControlSet\Enum\HID","HID"),
            };

            foreach (var (hive, devType) in hives)
            {
                using var baseKey = root.OpenSubKey(hive);
                if (baseKey == null) continue;

                foreach (var vidPid in baseKey.GetSubKeyNames())
                {
                    using var vidKey = baseKey.OpenSubKey(vidPid);
                    if (vidKey == null) continue;

                    foreach (var serial in vidKey.GetSubKeyNames())
                    {
                        string key = vidPid + "|" + serial;
                        if (!seen.Add(key)) continue;

                        using var devKey = vidKey.OpenSubKey(serial);
                        if (devKey == null) continue;

                        string name = devKey.GetValue("FriendlyName") as string
                                   ?? devKey.GetValue("DeviceDesc")   as string
                                   ?? vidPid;
                        string mfr  = devKey.GetValue("Mfg") as string ?? "—";
                        string sn   = serial.Length > 30 ? serial[..30] + "…" : serial;

                        // Clean up junk prefixes
                        if (mfr.StartsWith("(Standard") || mfr == "%Mfg%") mfr = "—";
                        if (name.StartsWith("@") || name.StartsWith("%")) name = vidPid;

                        result.Add(new UsbDeviceRecord(name.Trim(), mfr.Trim(), sn, devType));
                    }
                }
            }
            return result.OrderBy(d => d.DeviceType).ThenBy(d => d.Name).ToList();
        }

        private void BuildUsbList(List<UsbDeviceRecord> devices)
        {
            UsbDeviceList?.Children.Clear();
            if (devices.Count == 0) return;

            bool isDark    = ThemeManager.IsDark(SettingsService.Current.ThemeName);
            var clrRow     = isDark ? WpfColor.FromRgb(22, 30, 42)      : WpfColor.FromRgb(255, 255, 255);
            var clrAlt     = isDark ? WpfColor.FromRgb(25, 33, 46)      : WpfColor.FromRgb(245, 248, 252);
            var clrSep     = isDark ? WpfColor.FromArgb(20,255,255,255) : WpfColor.FromArgb(20,0,0,0);
            var clrPrimary = isDark ? WpfColor.FromRgb(203,213,225)     : WpfColor.FromRgb(30,41,59);
            var clrSec     = WpfColor.FromRgb(100,116,139);

            // Group info per type
            var typeInfo = new Dictionary<string,(string icon, WpfColor badgeBg, WpfColor headerAccent)>
            {
                ["Storage"] = ("", WpfColor.FromArgb(40, 59,130,246), WpfColor.FromRgb(59,130,246)),
                ["HID"]     = ("",  WpfColor.FromArgb(40,168, 85,247), WpfColor.FromRgb(168,85,247)),
                ["USB"]     = ("", WpfColor.FromArgb(40, 34,197, 94),  WpfColor.FromRgb(34,197,94)),
            };

            var grouped = devices
                .GroupBy(d => d.DeviceType)
                .OrderBy(g => g.Key == "Storage" ? 0 : g.Key == "HID" ? 1 : 2);

            foreach (var group in grouped)
            {
                var (icon, badgeBg, headerAccent) = typeInfo.TryGetValue(group.Key, out var ti)
                    ? ti : ("", WpfColor.FromArgb(40,150,150,150), WpfColor.FromRgb(150,150,150));

                string typeLabelEn = group.Key == "Storage" ? "Storage Devices"
                                   : group.Key == "HID"? "Input Devices (HID)"
                                   : "USB Devices";
                string typeLabelRo = group.Key == "Storage" ? "Dispozitive Stocare"
                                   : group.Key == "HID"? "Dispozitive Input (HID)"
                                   : "Dispozitive USB";

                // Group header
                var header = new Border
                {
                    Background      = new SolidColorBrush(isDark
                        ? WpfColor.FromArgb(30, headerAccent.R, headerAccent.G, headerAccent.B)
                        : WpfColor.FromArgb(18, headerAccent.R, headerAccent.G, headerAccent.B)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(60, headerAccent.R, headerAccent.G, headerAccent.B)),
                    Padding         = new Thickness(12, 7, 12, 7),
                    Margin          = new Thickness(0, group == grouped.First() ? 0 : 8, 0, 0),
                };
                var headerRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                headerRow.Children.Add(new TextBlock { Text = icon, FontSize = 13, Margin = new Thickness(0,0,7,0), VerticalAlignment = VerticalAlignment.Center });
                headerRow.Children.Add(new TextBlock
                {
                    Text       = _L(typeLabelEn, typeLabelRo),
                    FontSize   = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(headerAccent),
                    VerticalAlignment = VerticalAlignment.Center
                });
                headerRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background   = new SolidColorBrush(WpfColor.FromArgb(50, headerAccent.R, headerAccent.G, headerAccent.B)),
                    Padding      = new Thickness(7,1,7,1),
                    Margin       = new Thickness(8,0,0,0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = group.Count().ToString(),
                        FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(headerAccent)
                    }
                });
                header.Child = headerRow;
                UsbDeviceList?.Children.Add(header);

                // Rows within this group
                int rowIdx = 0;
                foreach (var d in group.OrderBy(x => x.Name))
                {
                    var row = new Border
                    {
                        Background      = new SolidColorBrush(rowIdx % 2 == 1 ? clrAlt : clrRow),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush     = new SolidColorBrush(clrSep),
                        Padding         = new Thickness(10, 6, 10, 6)
                    };

                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

                    // Name
                    var nameTb = new TextBlock { Text = d.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(clrPrimary), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(nameTb, 0); g.Children.Add(nameTb);

                    // Manufacturer
                    var mfrTb = new TextBlock { Text = d.Manufacturer, FontSize = 10,
                        Foreground = new SolidColorBrush(clrSec), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(mfrTb, 1); g.Children.Add(mfrTb);

                    // Serial (truncated, with tooltip)
                    var snTb = new TextBlock { Text = d.Serial, FontSize = 10, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(clrSec), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.Serial };
                    Grid.SetColumn(snTb, 2); g.Children.Add(snTb);

                    row.Child = g;
                    UsbDeviceList?.Children.Add(row);
                    rowIdx++;
                }
            }

            if (TxtUsbEmpty != null) TxtUsbEmpty.Visibility = Visibility.Collapsed;
            ShowToastSuccess($"{devices.Count} " + _L("USB devices found", "dispozitive USB găsite"));
        }


        private void UsbExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_usbDevices.Count == 0) { ShowToastWarning(_L("Scan USB history first.", "Scanează mai întâi.")); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = $"usb_history_{System.Environment.MachineName}_{DateTime.Now:yyyyMMdd}",
                DefaultExt = ".csv",
                Filter     = "CSV files|*.csv"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                static string esc(string v) => $"\"{v.Replace("\"", "\"\"")}\"";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Name,Manufacturer,Serial,Type");
                foreach (var d in _usbDevices)
                    sb.AppendLine(string.Join(",", esc(d.Name), esc(d.Manufacturer), esc(d.Serial), esc(d.DeviceType)));
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                ShowToastSuccess("" + _L("Exported:", "Exportat:") + " " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { ShowToastError(_L("Export error:", "Eroare export:") + " " + ex.Message); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // WIFI QR CODE
        // ══════════════════════════════════════════════════════════════════════
        private void ShowWifiQr_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            string ssid = btn.Tag?.ToString() ?? "";
            // Find password from _wifiEntries
            var entry = _wifiEntries.FirstOrDefault(w => w.Ssid == ssid);
            if (entry.Ssid == null) return;

            // WiFi QR format: WIFI:T:WPA;S:<ssid>;P:<password>;;
            string qrData = $"WIFI:T:WPA;S:{EscapeWifiQr(entry.Ssid)};P:{EscapeWifiQr(entry.Password)};;";

            bool isDark = ThemeManager.IsDark(SettingsService.Current.ThemeName);
            var fgColor = isDark ? WpfColor.FromRgb(220,230,255) : WpfColor.FromRgb(10,15,40);
            var bgColor = isDark ? WpfColor.FromRgb(12,18,32)    : WpfColor.FromRgb(248,250,255);
            var borderC = isDark ? WpfColor.FromArgb(60,100,140,255) : WpfColor.FromArgb(80,59,130,246);

            var dlg = new System.Windows.Window
            {
                Title                 = "WiFi QR Code",
                SizeToContent         = SizeToContent.WidthAndHeight,
                WindowStyle           = WindowStyle.None,
                ResizeMode            = ResizeMode.NoResize,
                AllowsTransparency    = true,
                Background            = new SolidColorBrush(WpfColor.FromArgb(0,0,0,0)),
                Owner                 = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar         = false,
            };
            // Close on Escape or click outside
            bool _dlgClosing = false;
            void SafeClose()
            {
                if (_dlgClosing) return;
                _dlgClosing = true;
                // Delay Close() via dispatcher to avoid WM_ACTIVATE re-entrancy crash
                dlg.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(() => { try { if (dlg.IsLoaded) dlg.Close(); } catch { } }));
            }
            dlg.KeyDown     += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) { ke.Handled = true; SafeClose(); } };
            dlg.Deactivated += (_, _)  => SafeClose();

            var outer = new Border
            {
                Margin          = new Thickness(12),
                CornerRadius    = new CornerRadius(16),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderC),
                BorderThickness = new Thickness(1.5),
                Padding         = new Thickness(24, 20, 24, 20),
            };

            var shadow = new System.Windows.Controls.Grid();
            shadow.Children.Add(new Border
            {
                Margin           = new Thickness(12),
                CornerRadius     = new CornerRadius(16),
                Background       = new SolidColorBrush(WpfColor.FromArgb(isDark ? (byte)120 : (byte)25, 0,0,0)),
                Effect           = new System.Windows.Media.Effects.BlurEffect { Radius = 16 },
                IsHitTestVisible = false,
            });
            shadow.Children.Add(outer);

            var root = new StackPanel { Width = 260 };

            // Title row
            var titleGrid = new System.Windows.Controls.Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new TextBlock { Text = "WiFi QR Code", FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(fgColor), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Controls.Grid.SetColumn(titleTb, 0);
            titleGrid.Children.Add(titleTb);
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Width = 28, Height = 28, Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60, 100, 140, 200)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(100,116,139)),
                FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Close (Esc)"
            };
            closeBtn.Click += (_, _) => SafeClose();
            System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(closeBtn);
            root.Children.Add(titleGrid);

            root.Children.Add(new TextBlock
            {
                Text = ssid, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fgColor),
                Margin = new Thickness(0,8,0,12), HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });

            // QR bitmap rendered as WPF canvas
            var qrImage = RenderQrCode(qrData, 220, isDark);
            root.Children.Add(new System.Windows.Controls.Image
            {
                Source = qrImage, Width = 220, Height = 220,
                Margin = new Thickness(0,0,0,12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            });

            root.Children.Add(new TextBlock
            {
                Text = _L("Scan with your phone to connect", "Scanează cu telefonul pentru conectare"),
                FontSize = 11, Foreground = new SolidColorBrush(WpfColor.FromRgb(100,116,139)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
            });

            outer.Child = root;
            dlg.Content = shadow;
            dlg.ShowDialog();
        }

        private static string EscapeWifiQr(string s) =>
            s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\"", "\\\"");

        /// <summary>Pure .NET QR code renderer — no external libs required.</summary>
        private static System.Windows.Media.Imaging.BitmapSource RenderQrCode(string data, int size, bool darkTheme)
        {
            // Build QR matrix using a minimal QR encoder (version 2, ECC-M, byte mode)
            bool[,] matrix = QrCodeMatrix.Generate(data);
            int modules = matrix.GetLength(0);
            int cell    = Math.Max(1, size / modules);
            int imgSize = modules * cell;

            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
                imgSize, imgSize, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null);

            int stride  = imgSize * 4;
            var pixels  = new byte[imgSize * stride];
            var dark    = darkTheme ? new byte[] { 220,230,255,255 } : new byte[] { 10,15,40,255 };
            var light   = darkTheme ? new byte[] { 12,18,32,255   } : new byte[] { 248,250,255,255 };

            for (int row = 0; row < modules; row++)
            for (int col = 0; col < modules; col++)
            {
                var color = matrix[row, col] ? dark : light;
                for (int pr = 0; pr < cell; pr++)
                for (int pc = 0; pc < cell; pc++)
                {
                    int offset = ((row * cell + pr) * imgSize + col * cell + pc) * 4;
                    pixels[offset]     = color[2]; // B
                    pixels[offset + 1] = color[1]; // G
                    pixels[offset + 2] = color[0]; // R
                    pixels[offset + 3] = color[3]; // A (unused for Bgr32)
                }
            }
            bmp.WritePixels(new System.Windows.Int32Rect(0,0,imgSize,imgSize), pixels, stride, 0);
            return bmp;
        }

        private void OpenOptimizer_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                // Open Windows Power Options (Control Panel → Power Options)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "/name Microsoft.PowerOptions",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppDialog.Show(ex.Message, "SMDWin", AppDialog.Kind.Error);
            }
        }

        /// <summary>Re-applies driver mode button colors after a theme change.</summary>
        internal void RefreshDriverModeButtons()
        {
            if (BtnDriverModeBasic == null || BtnDriverModeAdvanced == null) return;
            bool basic = _driverViewMode == "Basic";
            var activeStyle = TryFindResource("ChipButtonActiveStyle") as Style;
            var normalStyle = TryFindResource("ChipButtonStyle")       as Style;

            BtnDriverModeBasic.Style    = basic  ? activeStyle : normalStyle;
            BtnDriverModeAdvanced.Style = !basic ? activeStyle : normalStyle;
        }

        /// <summary>Re-applies ping interval button colors after a theme change.</summary>
        internal void RefreshPingIntervalButtons()
        {
            var intervalBtns = new[] { BtnPingInterval1m, BtnPingInterval5m, BtnPingInterval10m, BtnPingInterval1h };
            if (intervalBtns.All(b => b == null)) return;
            var accentBrush = TryFindResource("AccentBrush")        as System.Windows.Media.Brush
                              ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 143, 255));
            var transBrush  = System.Windows.Media.Brushes.Transparent;
            var secBrush    = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                              ?? System.Windows.Media.Brushes.Gray;

            var tagToMinutes = new System.Collections.Generic.Dictionary<Button, int>
            {
                { BtnPingInterval1m,  1  },
                { BtnPingInterval5m,  5  },
                { BtnPingInterval10m, 10 },
                { BtnPingInterval1h,  60 },
            };
            foreach (var b in intervalBtns)
            {
                if (b == null) continue;
                bool active = tagToMinutes.TryGetValue(b, out int m) && m == _pingWindowMinutes;
                b.Background = active ? accentBrush : transBrush;
                b.Foreground = active ? System.Windows.Media.Brushes.White : secBrush;
            }
        }

    } // end partial class MainWindow

/// <summary>
/// Minimal QR Code matrix generator. Produces a boolean[,] with dark=true cells.
/// Supports strings up to 47 bytes using Version 3, ECC-M, byte mode.
/// No external dependencies required.
/// </summary>
internal static class QrCodeMatrix
{
    public static bool[,] Generate(string data)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(data);
        // Version 3 = 29x29, ECC-M allows up to 47 data bytes
        if (bytes.Length > 47) bytes = bytes[..47];
        return BuildMatrix(bytes);
    }

    private static bool[,] BuildMatrix(byte[] data)
    {
        const int size = 29; // Version 3
        var m = new bool[size, size];

        // Finder patterns
        void Finder(int r, int c)
        {
            for (int i = 0; i < 7; i++) for (int j = 0; j < 7; j++)
                m[r+i, c+j] = (i==0||i==6||j==0||j==6)||(i>=2&&i<=4&&j>=2&&j<=4);
        }
        Finder(0, 0); Finder(0, 22); Finder(22, 0);

        // Timing strips
        for (int i = 8; i < 21; i++) { m[6, i] = (i % 2 == 0); m[i, 6] = (i % 2 == 0); }

        // Alignment pattern (version 3: center at 22,22)
        for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++)
            m[22+i, 22+j] = (Math.Abs(i)==2||Math.Abs(j)==2||(i==0&&j==0));

        // Format bits (ECC-M, mask 2 = (row+col)%3==0) pre-computed: 101011100011001
        int[] fmt = {1,0,1,0,1,1,1,0,0,0,1,1,0,0,1};
        int fi = 0;
        for (int i = 0; i <= 5; i++) m[8, i]   = fmt[fi++] == 1;
        m[8, 7] = fmt[fi++] == 1; m[8, 8] = fmt[fi++] == 1; m[7, 8] = fmt[fi++] == 1;
        for (int i = 5; i >= 0; i--) m[i, 8]   = fmt[fi++] == 1;
        fi = 0;
        for (int i = 0; i <= 6; i++) m[size-1-i, 8] = fmt[fi++] == 1;
        for (int i = 0; i <= 7; i++) m[8, size-1-i] = fmt[fi++] == 1;
        m[size-8, 8] = true; // dark module

        // Data encoding: byte mode
        var bits = new System.Collections.BitArray(0);
        void PushBits(int val, int count)
        {
            var tmp = new System.Collections.BitArray(bits.Length + count);
            for (int i = 0; i < bits.Length; i++) tmp[i] = bits[i];
            for (int i = count-1; i >= 0; i--) tmp[bits.Length + (count-1-i)] = ((val >> i) & 1) == 1;
            bits = tmp;
        }
        PushBits(0b0100, 4);
        PushBits(data.Length, 8);
        foreach (var b in data) PushBits(b, 8);
        PushBits(0, 4);
        while (bits.Length % 8 != 0) PushBits(0, 1);
        int[] padBytes = {0b11101100, 0b00010001};
        int pi = 0;
        // Version 3 ECC-M: 70 data codewords
        while (bits.Length < 70*8) { PushBits(padBytes[pi % 2], 8); pi++; }

        // Place data bits using up-column zigzag
        bool IsFunction(int r, int c)
        {
            if (r < 9 && c < 9) return true;
            if (r < 9 && c >= 21) return true;
            if (r >= 21 && c < 9) return true;
            if (r == 6 || c == 6) return true;
            if (r >= 20 && c >= 20) return true;
            return false;
        }
        int bi = 0;
        bool right = true;
        for (int col = size-1; col >= 1; col -= 2)
        {
            if (col == 6) col = 5;
            for (int row = right ? size-1 : 0; right ? row >= 0 : row < size; row += right ? -1 : 1)
            {
                for (int dc = 0; dc < 2; dc++)
                {
                    int c = col - dc;
                    if (IsFunction(row, c)) continue;
                    if (bi >= bits.Length) break;
                    bool bit = bits[bi++];
                    if ((row + c) % 3 == 0) bit = !bit;
                    m[row, c] = bit;
                }
            }
            right = !right;
        }
        return m;
    }
}

} // end namespace SMDWin.Views
