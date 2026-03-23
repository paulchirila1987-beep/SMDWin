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
        // ── DISK ──────────────────────────────────────────────────────────────

        private async Task LoadDisksInternalAsync()
        {
            // Show cached disks immediately
            if (_allDisks.Count > 0)
            {
                DiskPanel.Children.Clear();
                foreach (var disk in _allDisks)
                    DiskPanel.Children.Add(BuildDiskCard(disk));
            }

            ShowLoading(_L("Reading disks...", "Se citesc discurile..."));
            try
            {
                var fresh = await _hwService.GetDisksAsync();
                if (fresh.Count > 0) _allDisks = fresh;
                DiskPanel.Children.Clear();
                foreach (var disk in _allDisks)
                    DiskPanel.Children.Add(BuildDiskCard(disk));

                // Fire SMART/health notification if any disk needs attention
                try { CheckSmartNotif(_allDisks); } catch (Exception logEx) { AppLogger.Warning(logEx, "CheckSmartNotif(_allDisks);"); }
            }
            finally { HideLoading(); }
        }

        private async void LoadDisks_Click(object s, RoutedEventArgs e) => await LoadDisksInternalAsync();

        // ── Storage Tabs ─────────────────────────────────────────────────────

        private void StorageTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string tag = btn.Tag?.ToString() ?? "Overview";

            StorageTabOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            StorageTabSmart   .Visibility = tag == "Smart"    ? Visibility.Visible : Visibility.Collapsed;
            StorageTabSurface .Visibility = tag == "Surface"  ? Visibility.Visible : Visibility.Collapsed;
            StorageTabBench   .Visibility = tag == "Bench"    ? Visibility.Visible : Visibility.Collapsed;
            StorageTabFiles   .Visibility = tag == "Files"    ? Visibility.Visible : Visibility.Collapsed;

            // FIX: unified AppButtonStyle; active tab gets AccentBrush border
            var appStyle    = (Style)FindResource("AppButtonStyle");
            var accentBrush = TryFindResource("AccentBrush")      as System.Windows.Media.Brush;
            var borderBrush = TryFindResource("BorderBrush2")     as System.Windows.Media.Brush;
            var normalFg    = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush;

            void SetTab(Button? b, bool active)
            {
                if (b == null) return;
                b.Style       = appStyle;
                b.BorderBrush = active ? accentBrush : borderBrush;
                b.Foreground  = active ? accentBrush : normalFg;
                b.Background  = System.Windows.DependencyProperty.UnsetValue as System.Windows.Media.Brush;
            }

            SetTab(BtnStorageTabOverview, tag == "Overview");
            SetTab(BtnStorageTabSmart,    tag == "Smart");
            SetTab(BtnStorageTabSurface,  tag == "Surface");
            SetTab(BtnStorageTabBench,    tag == "Bench");
            SetTab(BtnStorageTabFiles,    tag == "Files");

            if (tag == "Smart"   && CbSmartDisk.Items.Count == 0)   PopulateSmartDiskCombo();
            if (tag == "Surface" && CbSurfaceDisk.Items.Count == 0) PopulateSurfaceDiskCombo();
            if (tag == "Bench"   && CbBenchDrive.Items.Count == 0)  PopulateBenchDriveCombo();
        }

        private void BtnStorageDevMgr_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        // ── LARGE FILES SCANNER ──────────────────────────────────────────────
        private CancellationTokenSource? _fileScanCts;
        private bool _filesSubIsLarge = true; // true = Large Files, false = Top Folders

        private void FilesSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string tag = btn.Tag?.ToString() ?? "LargeFiles";
            bool isLarge   = tag == "LargeFiles";
            bool isFolders = tag == "TopFolders";
            bool isTreeMap = tag == "TreeMap";
            _filesSubIsLarge = isLarge;

            var accentBrush2 = TryFindResource("AccentBrush")  as System.Windows.Media.SolidColorBrush;
            var defaultBg2   = TryFindResource("BgHoverBrush") as System.Windows.Media.SolidColorBrush;
            var pStyle = (Style)FindResource("PrimaryButtonStyle");
            var oStyle = (Style)FindResource("OutlineButtonStyle");
            BtnFilesSubLarge  .Style      = isLarge  ? pStyle : oStyle;
            BtnFilesSubLarge  .Background = isLarge  ? accentBrush2 : defaultBg2;
            BtnFilesSubLarge  .Foreground = isLarge  ? System.Windows.Media.Brushes.White : TryFindResource("TextPrimaryBrush") as System.Windows.Media.SolidColorBrush;
            BtnFilesSubFolders.Style      = isFolders ? pStyle : oStyle;
            BtnFilesSubFolders.Background = isFolders ? accentBrush2 : defaultBg2;
            BtnFilesSubFolders.Foreground = isFolders ? System.Windows.Media.Brushes.White : TryFindResource("TextPrimaryBrush") as System.Windows.Media.SolidColorBrush;
            if (BtnFilesSubTreeMap != null)
            {
                BtnFilesSubTreeMap.Style      = isTreeMap ? pStyle : oStyle;
                BtnFilesSubTreeMap.Background = isTreeMap ? accentBrush2 : defaultBg2;
                BtnFilesSubTreeMap.Foreground = isTreeMap ? System.Windows.Media.Brushes.White : TryFindResource("TextPrimaryBrush") as System.Windows.Media.SolidColorBrush;
            }

            LargeFilesGrid.Visibility  = isLarge   ? Visibility.Visible : Visibility.Collapsed;
            TopFoldersGrid.Visibility  = isFolders ? Visibility.Visible : Visibility.Collapsed;
            if (TreeMapPanel != null) TreeMapPanel.Visibility = isTreeMap ? Visibility.Visible : Visibility.Collapsed;

            if (isTreeMap) BuildTreeMap();

            if (TxtFileScanTitle != null)
                TxtFileScanTitle.Text = isLarge   ? "Large Files Scanner"
                                      : isFolders ? "Top Folders by Size"
                                      : "Disk Space TreeMap";

            if (TxtFileScanSummary != null)
                TxtFileScanSummary.Text = isLarge   ? "Run a scan to find large files."
                                        : isFolders ? "Run a scan to see which folders use the most space."
                                        : "Visual overview of disk usage — scan first.";
        }

        private void FileSizeChip_Checked_OLD_REMOVED(object sender, System.Windows.RoutedEventArgs e) { }

        private static string ClassifyFolder(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.Contains("\\temp") || lower.Contains("\\tmp")) return "Temp";
            if (lower.Contains("\\windows\\softwaredistribution")) return "Windows Update";
            if (lower.Contains("\\windows\\installer")) return "Installer Cache";
            if (lower.Contains("\\downloads")) return "Downloads";
            if (lower.Contains("\\appdata\\local\\microsoft\\windows\\inetcache") ||
                lower.Contains("\\temporary internet")) return "Browser Cache";
            if (lower.Contains("\\program files")) return "Program Files";
            if (lower.Contains("\\appdata")) return "AppData";
            if (lower.Contains("\\recycle.bin")) return "Recycle Bin";
            if (lower.Contains("\\documents") || lower.Contains("\\my documents")) return "Documents";
            if (lower.Contains("\\videos") || lower.Contains("\\my videos")) return "Videos";
            if (lower.Contains("\\pictures") || lower.Contains("\\my pictures")) return "Pictures";
            if (lower.Contains("\\music") || lower.Contains("\\my music")) return "Music";
            if (lower.Contains("\\games")) return "Games";
            return "Other";
        }

        private void FileSizeChip_Checked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb &&
                long.TryParse(rb.Tag?.ToString(), out long mb))
            {
                _currentThresholdMB = mb;
                if (TxtSizeHint != null && _sizeHints.TryGetValue(mb, out var hint))
                    TxtSizeHint.Text = hint;
            }
        }


        // Shared threshold used for bar calculations — updated when chip changes
        private long _currentThresholdMB = 100;

        private static double CalcBarWidth_unused(long sizeBytes, long thresholdMB) => CalcBarWidthStatic(sizeBytes, thresholdMB);
        private static System.Windows.Media.Brush CalcBarColor_unused(long sizeBytes, long thresholdMB) => CalcBarColorStatic(sizeBytes, thresholdMB);

        private class LargeFileEntry
        {
            public string FileName  { get; init; } = "";
            public string Directory { get; init; } = "";
            public string Extension { get; init; } = "";
            public string SizeText  { get; init; } = "";
            public long   SizeBytes { get; init; }
            public string Modified  { get; init; } = "";
            public long   ThresholdMB { get; init; } = 100;

            public double BarWidth => CalcBarWidth(SizeBytes, ThresholdMB);
            public System.Windows.Media.Brush BarColor => CalcBarColor(SizeBytes, ThresholdMB);

            private static double CalcBarWidth(long b, long t) => MainWindow.CalcBarWidthStatic(b, t);
            private static System.Windows.Media.Brush CalcBarColor(long b, long t) => MainWindow.CalcBarColorStatic(b, t);

            // 5.2 — Automatic file category
            public string FileCategory
            {
                get
                {
                    string dir = Directory.ToLowerInvariant();
                    string ext = Extension.ToLowerInvariant();
                    if (dir.Contains("\\temp") || dir.Contains("\\tmp") || ext == ".tmp" || ext == ".log") return "Temporar";
                    if (dir.Contains("\\backup") || ext == ".bak" || ext == ".old") return "Backup";
                    if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".mp3" or ".flac" or ".jpg" or ".png" or ".jpeg") return "Media";
                    if (dir.Contains("\\windows") || dir.Contains("\\system32") || dir.Contains("\\program files")) return "Sistem";
                    return "Other files";
                }
            }

            /// <summary>Files that are generally safe to delete (Temp category, not System).</summary>
            public bool IsSafeToDelete => FileCategory == "Temporar";

            public string CategoryIcon => FileCategory switch
            {
                "Temporar" => "🗑",
                "Backup"   => "💾",
                "Media"    => "🎬",
                "Sistem"   => "⚙",
                _          => "📄"
            };
            public string CategoryColor => FileCategory switch
            {
                "Temporar" => "#22C55E",
                "Backup"   => "#60A5FA",
                "Media"    => "#A78BFA",
                "Sistem"   => "#F59E0B",
                _          => "#94A3B8"
            };
        }

        private class TopFolderEntry
        {
            public string FolderPath { get; init; } = "";
            public string SizeText   { get; init; } = "";
            public long   SizeBytes  { get; init; }
            public int    FileCount  { get; init; }
            public string Category   { get; init; } = "";
            public long   ThresholdMB { get; init; } = 100;

            public double BarWidth => MainWindow.CalcBarWidthStatic(SizeBytes, ThresholdMB);
            public System.Windows.Media.Brush BarColor => MainWindow.CalcBarColorStatic(SizeBytes, ThresholdMB);
        }

        internal static double CalcBarWidthStatic(long sizeBytes, long thresholdMB)
        {
            long redBytes  = thresholdMB * 1024L * 1024L * 10;
            double fraction = Math.Clamp((double)sizeBytes / redBytes, 0.0, 1.0);
            return Math.Max(2, fraction * 72);
        }

        internal static System.Windows.Media.Brush CalcBarColorStatic(long sizeBytes, long thresholdMB)
        {
            long redBytes = thresholdMB * 1024L * 1024L * 10;
            double t      = Math.Clamp((double)sizeBytes / redBytes, 0.0, 1.0);
            byte r, g, b  = 68;
            if (t < 0.5) { double s = t / 0.5; r = (byte)(34 + (245-34)*s); g = (byte)(197 + (158-197)*s); }
            else         { double s = (t-0.5)/0.5; r = (byte)(245+(239-245)*s); g = (byte)(158+(68-158)*s); }
            return new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(r, g, b));
        }

        private static readonly long[] FileSizeThresholds = { 10, 50, 100, 500, 1024 }; // MB

        private static readonly Dictionary<long, string> _sizeHints = new()
        {
            { 10,"Showing files > 10 MB — e.g. documents, archives, small videos. Bar turns red at 100 MB+" },
            { 50,"Showing files > 50 MB — e.g. installers, disk images, large archives. Bar turns red at 500 MB+" },
            { 100,"Showing files > 100 MB — e.g. large installers, videos, backups. Bar turns red at 1 GB+" },
            { 500,"Showing files > 500 MB — e.g. virtual machines, game files, ISO images. Bar turns red at 5 GB+" },
            { 1024, "Showing files > 1 GB — e.g. VMs, Blu-ray rips, large backups. Bar turns red at 10 GB+" },
        };

        private void BrowseFileScanPath_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description   = "Select folder to scan",
                SelectedPath  = TxtFileScanPath.Text
            };
            if (dlg.ShowDialog() == Forms.DialogResult.OK)
                TxtFileScanPath.Text = dlg.SelectedPath;
        }

        private async void StartFileScan_Click(object sender, RoutedEventArgs e)
        {
            if (_fileScanCts != null)
            {
                _fileScanCts.Cancel();
                BtnStartFileScan.Content = "Scan";
                FileScanProgressRow.Visibility = Visibility.Collapsed;
                return;
            }

            string path = TxtFileScanPath.Text.Trim();
            if (!System.IO.Directory.Exists(path))
            {
                AppDialog.Show("Path does not exist.", "SMDWin", AppDialog.Kind.Warning);
                return;
            }

            BtnStartFileScan.Content = "■ Stop";
            FileScanProgressRow.Visibility = Visibility.Visible;
            TxtFileScanStatus.Text = "Scanning...";
            TxtFileScanSummary.Text = "Scanning — please wait...";
            LargeFilesGrid.ItemsSource = null;
            TopFoldersGrid.ItemsSource = null;
            BtnOpenFileScanFolder.Visibility = Visibility.Collapsed;

            _fileScanCts = new CancellationTokenSource();
            var ct = _fileScanCts.Token;

            if (_filesSubIsLarge)
                await RunLargeFilesScan(path, ct);
            else
                await RunTopFoldersScan(path, ct);

            BtnStartFileScan.Content = "Scan";
            FileScanProgressRow.Visibility = Visibility.Collapsed;
            _fileScanCts?.Dispose();
            _fileScanCts = null;
        }

        private async Task RunLargeFilesScan(string path, CancellationToken ct)
        {
            // Read from chip buttons instead of ComboBox
            long minMB = 100; // default
            var checkedChip = new[] { ChipFile10, ChipFile50, ChipFile100, ChipFile500, ChipFile1GB }
                .FirstOrDefault(c => c?.IsChecked == true);
            if (checkedChip != null && long.TryParse(checkedChip.Tag?.ToString(), out long tagMb))
                minMB = tagMb;
            long minBytes = minMB * 1024L * 1024L;

            var liveResults = new System.Collections.ObjectModel.ObservableCollection<LargeFileEntry>();
            LargeFilesGrid.ItemsSource = liveResults;

            int scanned = 0;
            long totalBytes = 0;

            // Re-sort visible results every 1.5s during scan
            var sortTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            sortTimer.Tick += (_, _) =>
            {
                if (liveResults.Count < 2) return;
                var sorted2 = liveResults.OrderByDescending(f => f.SizeBytes).ToList();
                for (int i = 0; i < sorted2.Count; i++)
                {
                    int cur = liveResults.IndexOf(sorted2[i]);
                    if (cur != i) liveResults.Move(cur, i);
                }
            };
            sortTimer.Start();

            var progress = new Progress<LargeFileEntry>(entry =>
            {
                liveResults.Add(entry);
                totalBytes += entry.SizeBytes;
                TxtFileScanStatus.Text = $"{liveResults.Count} files found so far…";
                TxtFileScanSummary.Text = $"{liveResults.Count} files • Total: {FormatFileSize(totalBytes)} • Scanned {scanned:N0} files";
            });

            try
            {
                await Task.Run(() =>
                {
                    var opts = new System.IO.EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = System.IO.FileAttributes.System | System.IO.FileAttributes.ReparsePoint
                    };

                    foreach (var fi in new System.IO.DirectoryInfo(path).EnumerateFiles("*", opts))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            System.Threading.Interlocked.Increment(ref scanned);
                            if (fi.Length < minBytes) continue;
                            var entry = new LargeFileEntry
                            {
                                FileName     = fi.Name,
                                Directory    = fi.DirectoryName ?? "",
                                Extension    = fi.Extension.ToLower(),
                                SizeText     = FormatFileSize(fi.Length),
                                SizeBytes    = fi.Length,
                                Modified     = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                                ThresholdMB  = minMB,
                            };
                            ((IProgress<LargeFileEntry>)progress).Report(entry);
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }, ct);
            }
            catch (OperationCanceledException) { }
            finally { sortTimer.Stop(); }

            // Final summary — sorted descending by size
            var sorted = liveResults.OrderByDescending(f => f.SizeBytes).Take(500).ToList();
            LargeFilesGrid.ItemsSource = sorted;
            // Apply default sort so column header arrow shows correctly
            var cv = System.Windows.Data.CollectionViewSource.GetDefaultView(LargeFilesGrid.ItemsSource);
            cv?.SortDescriptions.Clear();
            cv?.SortDescriptions.Add(new System.ComponentModel.SortDescription("SizeBytes",
                System.ComponentModel.ListSortDirection.Descending));

            long finalBytes = sorted.Sum(f => f.SizeBytes);
            TxtFileScanSummary.Text = sorted.Count == 0
                ? $"No files found above {FormatFileSize(minBytes)} in {path}."
                : $"{sorted.Count} files found • Total: {FormatFileSize(finalBytes)} • Scanned {scanned:N0} files";

            BtnOpenFileScanFolder.Visibility = sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Actualizeaza TreeMap automat dupa scan
            try { BuildTreeMap(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
        }

        private async Task RunTopFoldersScan(string path, CancellationToken ct)
        {
            var folderSizes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
            var folderFileCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            int scanned = 0;

            try
            {
                await Task.Run(() =>
                {
                    var opts = new System.IO.EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = System.IO.FileAttributes.System | System.IO.FileAttributes.ReparsePoint
                    };

                    // Enumerate all first-level subdirectories and accumulate sizes
                    var rootDir = new System.IO.DirectoryInfo(path);
                    System.IO.DirectoryInfo[] topDirs;
                    try { topDirs = rootDir.GetDirectories(); }
                    catch { topDirs = Array.Empty<System.IO.DirectoryInfo>(); }

                    foreach (var dir in topDirs)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            long dirSize = 0;
                            int fileCount = 0;
                            foreach (var fi in dir.EnumerateFiles("*", opts))
                            {
                                if (ct.IsCancellationRequested) break;
                                try { dirSize += fi.Length; fileCount++; System.Threading.Interlocked.Increment(ref scanned); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                            }
                            if (dirSize > 0)
                            {
                                folderSizes[dir.FullName] = dirSize;
                                folderFileCounts[dir.FullName] = fileCount;
                            }
                            int s = scanned;
                            Dispatcher.InvokeAsync(() => TxtFileScanStatus.Text = $"Scanned {s:N0} files...");
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                }, ct);
            }
            catch (OperationCanceledException) { }

            var sorted = folderSizes
                .OrderByDescending(kv => kv.Value)
                .Take(200)
                .Select(kv => new TopFolderEntry
                {
                    FolderPath   = kv.Key,
                    SizeText     = FormatFileSize(kv.Value),
                    SizeBytes    = kv.Value,
                    FileCount    = folderFileCounts.GetValueOrDefault(kv.Key, 0),
                    Category     = ClassifyFolder(kv.Key),
                    ThresholdMB  = _currentThresholdMB,
                })
                .ToList();

            TopFoldersGrid.ItemsSource = sorted;
            var cvf = System.Windows.Data.CollectionViewSource.GetDefaultView(TopFoldersGrid.ItemsSource);
            cvf?.SortDescriptions.Clear();
            cvf?.SortDescriptions.Add(new System.ComponentModel.SortDescription("SizeBytes",
                System.ComponentModel.ListSortDirection.Descending));
            long total = sorted.Sum(f => f.SizeBytes);
            TxtFileScanSummary.Text = sorted.Count == 0
                ? $"No subdirectories found in {path}."
                : $"{sorted.Count} folders • Total: {FormatFileSize(total)} • {scanned:N0} files scanned";

            // Actualizeaza TreeMap automat dupa scan
            try { BuildTreeMap(); } catch (Exception logEx) { AppLogger.Debug(logEx.Message); }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024)        return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / 1024.0:F0} KB";
        }

        private void OpenFileScanFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LargeFilesGrid.SelectedItem is LargeFileEntry f)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{System.IO.Path.Combine(f.Directory, f.FileName)}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", TxtFileScanPath.Text);
        }

        private void LargeFile_OpenFolder(object sender, RoutedEventArgs e)
        {
            if (LargeFilesGrid.SelectedItem is LargeFileEntry f)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{System.IO.Path.Combine(f.Directory, f.FileName)}\"");
        }

        private void LargeFile_Delete(object sender, RoutedEventArgs e)
        {
            if (LargeFilesGrid.SelectedItem is not LargeFileEntry f) return;
            string full = System.IO.Path.Combine(f.Directory, f.FileName);
            if (!AppDialog.Confirm($"Delete {f.FileName}?\\n{f.SizeText}", "Confirm Delete")) return;
            try
            {
                System.IO.File.Delete(full);
                var list = (LargeFilesGrid.ItemsSource as List<LargeFileEntry>)?.ToList() ?? new();
                list.Remove(f);
                LargeFilesGrid.ItemsSource = list;
            }
            catch (Exception ex) { AppDialog.Show($"Could not delete: {ex.Message}", "Error"); }
        }

        private void LargeFile_MoveToRecycleBin(object sender, RoutedEventArgs e)
        {
            if (LargeFilesGrid.SelectedItem is not LargeFileEntry f) return;
            string full = System.IO.Path.Combine(f.Directory, f.FileName);
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    full,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                var list = (LargeFilesGrid.ItemsSource as List<LargeFileEntry>)?.ToList() ?? new();
                list.Remove(f);
                LargeFilesGrid.ItemsSource = list;
                if (TxtFileScanSummary != null)
                    TxtFileScanSummary.Text = $"Moved to Recycle Bin: {f.FileName}  •  {f.SizeText} freed";
            }
            catch (Exception ex) { AppDialog.Show($"Could not move to Recycle Bin: {ex.Message}", "Error"); }
        }

        /// <summary>One-click cleanup: moves all Temporar (safe) files to Recycle Bin.</summary>
        private void CleanUpTempFiles_Click(object sender, RoutedEventArgs e)
        {
            var allFiles = LargeFilesGrid.ItemsSource as List<LargeFileEntry>;
            if (allFiles == null || allFiles.Count == 0)
            {
                AppDialog.Show("Run a scan first to find files.", "Clean Up");
                return;
            }
            var tempFiles = allFiles.Where(f => f.IsSafeToDelete).ToList();
            if (tempFiles.Count == 0)
            {
                AppDialog.Show("No temporary files found in the scan results.", "Clean Up");
                return;
            }
            long totalBytes = tempFiles.Sum(f => f.SizeBytes);
            if (!AppDialog.Confirm(
                $"Move {tempFiles.Count} temporary file(s) ({FormatFileSize(totalBytes)}) to Recycle Bin?\n\nThese are safe to delete.",
                "Clean Up Temp Files")) return;

            int done = 0;
            foreach (var f in tempFiles)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        System.IO.Path.Combine(f.Directory, f.FileName),
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    done++;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }
            var remaining = allFiles.Except(tempFiles.Take(done)).ToList();
            LargeFilesGrid.ItemsSource = remaining;
            if (TxtFileScanSummary != null)
                TxtFileScanSummary.Text = $"✓ Moved {done}/{tempFiles.Count} temp files to Recycle Bin  •  {FormatFileSize(totalBytes)} freed";
        }

        private void LargeFile_CopyPath(object sender, RoutedEventArgs e)
        {
            if (LargeFilesGrid.SelectedItem is LargeFileEntry f)
                System.Windows.Clipboard.SetText(System.IO.Path.Combine(f.Directory, f.FileName));
        }

        // ── TreeMap ───────────────────────────────────────────────────────────
        private static readonly System.Windows.Media.Color[] TreeMapPalette =
        {
            System.Windows.Media.Color.FromRgb(59,  130, 246),  // blue
            System.Windows.Media.Color.FromRgb(34,  197,  94),  // green
            System.Windows.Media.Color.FromRgb(245, 158,  11),  // amber
            System.Windows.Media.Color.FromRgb(239,  68,  68),  // red
            System.Windows.Media.Color.FromRgb(168, 85,  247),  // violet
            System.Windows.Media.Color.FromRgb(20,  184, 166),  // teal
            System.Windows.Media.Color.FromRgb(249, 115,  22),  // orange
            System.Windows.Media.Color.FromRgb(236,  72, 153),  // pink
        };

        // Maps cell draw-order index → full path for drill-down
        private List<string> _treeMapPaths = new();

        private void BuildTreeMap()
        {
            if (TreeMapCanvas == null) return;
            TreeMapCanvas.Children.Clear();
            _treeMapColorIdx = 0;
            _treeMapPaths.Clear();

            var items = new List<(string label, long bytes, string fullPath)>();
            var folders = TopFoldersGrid.ItemsSource as IEnumerable<object>;
            if (folders != null)
                foreach (var item in folders)
                    if (item is TopFolderEntry tf)
                        items.Add((System.IO.Path.GetFileName(tf.FolderPath.TrimEnd('\\')) + "\n" + tf.SizeText,
                            tf.SizeBytes, tf.FolderPath));

            if (items.Count == 0)
            {
                var files = LargeFilesGrid.ItemsSource as IEnumerable<object>;
                if (files != null)
                    foreach (var item in files)
                        if (item is LargeFileEntry lf)
                            items.Add((lf.FileName + "\n" + lf.SizeText, lf.SizeBytes,
                                System.IO.Path.Combine(lf.Directory, lf.FileName)));
            }

            if (items.Count == 0)
            {
                if (TxtTreeMapInfo != null) TxtTreeMapInfo.Text = "No data — run a scan first.";
                return;
            }

            long total = items.Sum(i => i.bytes);
            string scanRoot = TxtFileScanPath?.Text ?? "";
            if (TxtTreeMapInfo != null)
                TxtTreeMapInfo.Text = $"{items.Count} items  •  Total: {FormatTreeBytes(total)}  •  {scanRoot}  •  Click a folder to drill in";

            double W = TreeMapCanvas.Width;
            double H = TreeMapCanvas.Height;
            var sorted = items.OrderByDescending(i => i.bytes).ToList();
            _treeMapPaths = sorted.Select(i => i.fullPath).ToList();
            DrawTreeMapRect(sorted.Select(i => (i.label, i.bytes)).ToList(), 0, 0, W, H, total);
        }

        private void DrawTreeMapRect(List<(string label, long bytes)> items, double x, double y, double w, double h, long total)
        {
            if (items.Count == 0 || w < 2 || h < 2) return;
            if (items.Count == 1)
            {
                DrawCell(items[0].label, items[0].bytes, total, x, y, w, h, 0);
                return;
            }
            // Split: first half fills row, rest fills remaining
            bool horizontal = w >= h;
            long half = total / 2;
            long running = 0;
            int splitIdx = 0;
            for (int i = 0; i < items.Count; i++)
            {
                running += items[i].bytes;
                if (running >= half || i == items.Count - 2) { splitIdx = i + 1; break; }
            }
            if (splitIdx == 0) splitIdx = 1;
            var first  = items.Take(splitIdx).ToList();
            var second = items.Skip(splitIdx).ToList();
            long firstTotal  = first.Sum(i => i.bytes);
            long secondTotal = second.Sum(i => i.bytes);
            double ratio = total > 0 ? (double)firstTotal / total : 0.5;

            if (horizontal)
            {
                double w1 = w * ratio;
                DrawTreeMapRect(first,  x,      y, w1,     h, firstTotal);
                DrawTreeMapRect(second, x + w1, y, w - w1, h, secondTotal);
            }
            else
            {
                double h1 = h * ratio;
                DrawTreeMapRect(first,  x, y,      w, h1,     firstTotal);
                DrawTreeMapRect(second, x, y + h1, w, h - h1, secondTotal);
            }
        }

        private static int _treeMapColorIdx = 0;
        private void DrawCell(string label, long bytes, long total, double x, double y, double w, double h, int depth)
        {
            var color = TreeMapPalette[_treeMapColorIdx++ % TreeMapPalette.Length];
            double pct = total > 0 ? bytes * 100.0 / total : 0;

            // Extract folder path from label (first line = folder name, stored in Tag)
            // For top folders the label is "FolderName\nSize", we store the full path via Tag on DrawCell caller
            var border = new System.Windows.Shapes.Rectangle
            {
                Width  = Math.Max(0, w - 2),
                Height = Math.Max(0, h - 2),
                Fill   = new System.Windows.Media.SolidColorBrush(
                             System.Windows.Media.Color.FromArgb(200, color.R, color.G, color.B)),
                Stroke = new System.Windows.Media.SolidColorBrush(
                             System.Windows.Media.Color.FromArgb(80, 255, 255, 255)),
                StrokeThickness = 1,
                Tag = label,    // used to identify on double-click
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            border.ToolTip = $"{label.Replace("\n", " ")} — {FormatTreeBytes(bytes)} ({pct:F1}%)\nClick to drill into folder";
            Canvas.SetLeft(border, x + 1);
            Canvas.SetTop(border,  y + 1);

            // ── Right-click context menu ─────────────────────────────────────
            int cellIndex = _treeMapPaths.Count - 1; // capture current path index
            // We need to capture the path at draw time; index = current count before Add
            // Actually paths are added via _treeMapPaths = sorted.Select(...) before DrawCell,
            // so we resolve by matching the label's first line against _treeMapPaths.
            border.MouseRightButtonUp += (_, e) =>
            {
                string firstName2 = (border.Tag?.ToString() ?? "").Split('\n')[0].Trim();
                string? cellPath = _treeMapPaths.FirstOrDefault(p =>
                    System.IO.Path.GetFileName(p.TrimEnd('\\'))
                        .Equals(firstName2, StringComparison.OrdinalIgnoreCase))
                    ?? _treeMapPaths.FirstOrDefault(p =>
                        System.IO.Path.GetFileNameWithoutExtension(p)
                            .Equals(firstName2, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(cellPath)) { e.Handled = true; return; }

                bool isDir  = System.IO.Directory.Exists(cellPath);
                bool isFile = !isDir && System.IO.File.Exists(cellPath);

                var cm = BuildTreeMapContextMenu(cellPath, isDir, isFile);
                cm.PlacementTarget = border;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
                e.Handled = true;
            };

            TreeMapCanvas.Children.Add(border);

            // Label (only if cell is big enough)
            if (w > 40 && h > 20)
            {
                var txt = new TextBlock
                {
                    Text = label,
                    FontSize = Math.Max(8, Math.Min(11, w / 10)),
                    Foreground = System.Windows.Media.Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Width  = w - 8,
                    Height = h - 4,
                    Padding = new Thickness(4, 2, 4, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,   // let clicks pass through to rectangle
                };
                Canvas.SetLeft(txt, x + 4);
                Canvas.SetTop(txt,  y + 2);
                TreeMapCanvas.Children.Add(txt);
            }
        }

        /// <summary>Dark-themed context menu for treemap cells (right-click).</summary>
        private ContextMenu BuildTreeMapContextMenu(string path, bool isDir, bool isFile)
        {
            var darkBg = (Application.Current.TryFindResource("BgCardBrush") as System.Windows.Media.SolidColorBrush)
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 27, 38));
            var darkBorder = (Application.Current.TryFindResource("CardBorderBrush") as System.Windows.Media.SolidColorBrush)
                             ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 255, 255, 255));

            var cm = new ContextMenu();

            // Custom dark shell
            var tpl = new ControlTemplate(typeof(ContextMenu));
            var outerBd = new FrameworkElementFactory(typeof(Border));
            outerBd.SetValue(Border.PaddingProperty, new Thickness(8));
            outerBd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            var innerBd = new FrameworkElementFactory(typeof(Border));
            innerBd.SetValue(Border.BackgroundProperty, darkBg);
            innerBd.SetValue(Border.BorderBrushProperty, darkBorder);
            innerBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            innerBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            innerBd.SetValue(Border.PaddingProperty, new Thickness(4));
            innerBd.SetValue(UIElement.ClipToBoundsProperty, true);
            innerBd.SetValue(UIElement.EffectProperty,
                new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 14, ShadowDepth = 3, Color = Colors.Black, Opacity = 0.45 });
            var ip = new FrameworkElementFactory(typeof(StackPanel));
            ip.SetValue(StackPanel.IsItemsHostProperty, true);
            innerBd.AppendChild(ip);
            outerBd.AppendChild(innerBd);
            tpl.VisualTree = outerBd;
            cm.Template = tpl;

            // Helper: styled menu item
            MenuItem MakeItem(string header, string icon, Action action)
            {
                var item = new MenuItem { Header = $"{icon}  {header}" };
                var itemTpl = new ControlTemplate(typeof(MenuItem));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.Name = "MBd";
                bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
                bd.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp);
                itemTpl.VisualTree = bd;
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty,
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(28, 255, 255, 255)), "MBd"));
                itemTpl.Triggers.Add(hover);
                item.Template  = itemTpl;
                item.Foreground = (System.Windows.Media.Brush?)TryFindResource("TextPrimaryBrush")
                                  ?? System.Windows.Media.Brushes.White;
                item.Click += (_, _) => action();
                return item;
            }

            Separator MakeSep()
            {
                var sep = new Separator();
                var sTpl = new ControlTemplate(typeof(Separator));
                var sBd = new FrameworkElementFactory(typeof(Border));
                sBd.SetValue(Border.HeightProperty, 1.0);
                sBd.SetValue(Border.MarginProperty, new Thickness(8, 3, 8, 3));
                sBd.SetValue(Border.BackgroundProperty,
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(35, 255, 255, 255)));
                sTpl.VisualTree = sBd;
                sep.Template = sTpl;
                return sep;
            }

            string displayName = isDir
                ? System.IO.Path.GetFileName(path.TrimEnd('\\'))
                : System.IO.Path.GetFileName(path);

            if (isDir)
            {
                cm.Items.Add(MakeItem("Open folder", "📂", () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", path); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                }));
            }
            else if (isFile)
            {
                cm.Items.Add(MakeItem("Open file", "📄", () =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                }));
                cm.Items.Add(MakeItem("Open containing folder", "📂", () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                }));
            }

            cm.Items.Add(MakeSep());

            cm.Items.Add(MakeItem("Properties", "🔍", () =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = "explorer.exe",
                        Arguments       = $"/select,\"{path}\"",
                        UseShellExecute = true,
                    };
                    // Use shell verb "properties" for the actual Properties dialog
                    var info = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = path,
                        Verb            = "properties",
                        UseShellExecute = true,
                    };
                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }));

            cm.Items.Add(MakeSep());

            cm.Items.Add(MakeItem(isDir ? "Delete folder…" : "Delete file…", "🗑", () =>
            {
                string label2 = isDir ? $"Delete folder \"{displayName}\" and all its contents?" : $"Delete \"{displayName}\"?";
                if (!AppDialog.Confirm(label2, isDir ? "Delete Folder" : "Delete File")) return;
                try
                {
                    if (isDir)  System.IO.Directory.Delete(path, recursive: true);
                    else        System.IO.File.Delete(path);
                    // Refresh the treemap
                    BuildTreeMap();
                }
                catch (Exception ex)
                {
                    AppDialog.Show($"Could not delete: {ex.Message}", "Error");
                }
            }));

            return cm;
        }

        private void TreeMap_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { }

        private void TreeMap_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Find which rectangle was clicked
            var pos = e.GetPosition(TreeMapCanvas);
            System.Windows.Shapes.Rectangle? hit = null;
            for (int i = TreeMapCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (TreeMapCanvas.Children[i] is System.Windows.Shapes.Rectangle rect)
                {
                    double l = Canvas.GetLeft(rect), t = Canvas.GetTop(rect);
                    if (pos.X >= l && pos.X <= l + rect.Width &&
                        pos.Y >= t && pos.Y <= t + rect.Height)
                    { hit = rect; break; }
                }
            }
            if (hit == null) return;

            string label = hit.Tag?.ToString() ?? "";
            string? path = null;

            string firstName = label.Split('\n')[0].Trim();
            path = _treeMapPaths.FirstOrDefault(p =>
                System.IO.Path.GetFileName(p.TrimEnd('\\'))
                    .Equals(firstName, StringComparison.OrdinalIgnoreCase));

            if (path == null || !System.IO.Directory.Exists(path))
            {
                // It's a file — open containing folder in Explorer
                if (path != null && System.IO.File.Exists(path))
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                }
                return;
            }

            // Drill into folder: set scan path and re-scan in TreeMap view
            if (TxtFileScanPath != null) TxtFileScanPath.Text = path;
            // Re-scan and stay on TreeMap tab
            FilesSubTab_Click(new Button { Tag = "TopFolders" }, new RoutedEventArgs());
            StartFileScan_Click(sender, new RoutedEventArgs());
        }

        private static string FormatTreeBytes(long bytes)
        {
            if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes / 1024.0:F0} KB";
        }

        private void LargeFile_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Double-click on a file → open its containing folder in Explorer and select it
            if (LargeFilesGrid.SelectedItem is LargeFileEntry f)
            {
                string fullPath = System.IO.Path.Combine(f.Directory, f.FileName);
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\""); }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
            }
        }

        private void TopFolder_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Double-click on a folder → drill in: set path and re-scan
            if (TopFoldersGrid.SelectedItem is TopFolderEntry f)
            {
                if (!System.IO.Directory.Exists(f.FolderPath)) return;
                TxtFileScanPath.Text = f.FolderPath;
                // Re-trigger scan for the selected folder
                StartFileScan_Click(sender, new RoutedEventArgs());
            }
        }

        private void TopFolder_Open(object sender, RoutedEventArgs e)
        {
            if (TopFoldersGrid.SelectedItem is TopFolderEntry f)
                System.Diagnostics.Process.Start("explorer.exe", $"\"{f.FolderPath}\"");
        }

        private void TopFolder_CopyPath(object sender, RoutedEventArgs e)
        {
            if (TopFoldersGrid.SelectedItem is TopFolderEntry f)
                System.Windows.Clipboard.SetText(f.FolderPath);
        }

        // ── SMART tab ────────────────────────────────────────────────────────


        private void PopulateSmartDiskCombo()
        {
            CbSmartDisk.Items.Clear();
            foreach (var d in _allDisks)
                CbSmartDisk.Items.Add($"{d.Model}  [{d.Size}]  ({d.MediaType})");
            if (CbSmartDisk.Items.Count > 0) CbSmartDisk.SelectedIndex = 0;
        }

        private void SmartDisk_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int idx = CbSmartDisk.SelectedIndex;
            if (idx < 0 || idx >= _allDisks.Count) return;
            var disk = _allDisks[idx];

            SmartGrid.ItemsSource = disk.SmartAttributes.Count > 0
                ? disk.SmartAttributes
                : GetFallbackSmartAttributes(disk);

            TxtSmartHealthBadge.Text = $"{disk.HealthPercent}%";
            TxtSmartHealthBadge.Foreground = new SolidColorBrush(
                (WpfColor)WpfColorConv.ConvertFromString(disk.HealthColor)!);
            TxtSmartDiskType.Text = $"Tip: {disk.MediaType}  •  S/N: {disk.SerialNumber}  •  {disk.PhysicalDevicePath}";
        }

        private static List<SMDWin.Models.SmartAttributeEntry> GetFallbackSmartAttributes(DiskHealthEntry disk)
        {
            // Return basic info when WMI SMART data not available
            return new List<SMDWin.Models.SmartAttributeEntry>
            {
                new() { Id = 0x00, Name = "Model",           CurrentValue = 0, WorstValue = 0, RawValue = 0,
                        IsCritical = false, Description = disk.Model },
                new() { Id = 0x00, Name = "Dimensiune",      CurrentValue = 0, WorstValue = 0, RawValue = 0,
                        IsCritical = false, Description = disk.Size },
                new() { Id = 0x00, Name = "Tip media",       CurrentValue = 0, WorstValue = 0, RawValue = 0,
                        IsCritical = false, Description = disk.MediaType },
                new() { Id = 0x00, Name = "Status",          CurrentValue = 0, WorstValue = 0, RawValue = 0,
                        IsCritical = false, Description = disk.Status },
                new() { Id = 0x00, Name = "SMART (WMI)",     CurrentValue = 0, WorstValue = 0, RawValue = 0,
                        IsCritical = false,
                        Description = "Date SMART detaliate indisponibile via WMI. Necesita rulare ca Administrator." },
            };
        }

        // ── Surface Scan tab ─────────────────────────────────────────────────

        private CancellationTokenSource? _surfaceScanCts;
        private int _surfaceMapBlockCount = 0;

        private void PopulateSurfaceDiskCombo()
        {
            CbSurfaceDisk.Items.Clear();
            foreach (var d in _allDisks)
                CbSurfaceDisk.Items.Add($"{d.Model}  ({d.PhysicalDevicePath})");
            if (CbSurfaceDisk.Items.Count > 0) CbSurfaceDisk.SelectedIndex = 0;
        }

        private async void SurfaceScan_Start_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: if scanning, cancel it
            if (_surfaceScanCts != null && !_surfaceScanCts.IsCancellationRequested)
            {
                _surfaceScanCts.Cancel();
                return;
            }

            int idx = CbSurfaceDisk.SelectedIndex;
            if (idx < 0 || idx >= _allDisks.Count) return;
            var disk = _allDisks[idx];

            string devPath = disk.PhysicalDevicePath;
            if (string.IsNullOrEmpty(devPath))
                devPath = $"\\\\.\\PhysicalDrive{disk.DiskIndex}";

            _surfaceScanCts?.Dispose();
            _surfaceScanCts = new CancellationTokenSource();

            BtnSurfaceStart.IsEnabled  = true;
            BtnSurfaceStart.Content    = "Stop Scan";
            BtnSurfaceStart.Style = (Style)TryFindResource("RedButtonStyle");
            SurfaceMapPanel.Children.Clear();
            BadSectorList.Items.Clear();
            _surfaceMapBlockCount = 0;
            TxtSurfaceBad.Text   = "0";
            TxtSurfaceSlow.Text  = "0";
            TxtSurfaceSpeed.Text = "— MB/s";
            TxtSurfaceEta.Text   = "—";
            TxtSurfaceLba.Text   = "—";

            try
            {
                await Task.Run(() => RunSurfaceScan(devPath, _surfaceScanCts.Token), _surfaceScanCts.Token);
            }
            catch (OperationCanceledException) { TxtSurfaceStatus.Text = "Scan stopped by user."; }
            catch (UnauthorizedAccessException ex)
            {
                AppDialog.Show($"Access denied!\n\n{ex.Message}\n\nRun SMDWin as Administrator.", "Surface Scan Error", AppDialog.Kind.Warning);
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Surface scan error:\n{ex.Message}", "Error", AppDialog.Kind.Error);
            }
            finally
            {
                BtnSurfaceStart.IsEnabled  = true;
                BtnSurfaceStart.Content    = "Start Scan";
                BtnSurfaceStart.Style = (Style)TryFindResource("GreenButtonStyle");
            }
        }

        private void SurfaceScan_Stop_Click(object sender, RoutedEventArgs e)
        {
            _surfaceScanCts?.Cancel();
        }

        private void RunSurfaceScan(string devicePath, CancellationToken ct)
        {
            const int BLOCK_SECTORS = 256;     // 128KB per read
            const int REPORT_EVERY  = 2048;    // report every ~1MB

            // Open disk with NO_BUFFERING to bypass cache
            var handle = NativeMethods.CreateFile(devicePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_RANDOM_ACCESS,
                IntPtr.Zero);

            if (handle == NativeMethods.INVALID_HANDLE_VALUE)
                throw new UnauthorizedAccessException(
                    $"Nu se poate deschide {devicePath}.\nCod eroare Win32: {Marshal.GetLastWin32Error()}");

            Dispatcher.Invoke(() => TxtSurfaceStatus.Text = "Scanning...");

            var startTime = DateTime.Now;
            long totalSectors = GetDiskSectorCount(handle);
            long bad = 0, slow = 0, processed = 0;

            var buffer = Marshal.AllocHGlobal(BLOCK_SECTORS * 512);
            var sw = new System.Diagnostics.Stopwatch();

            try
            {
                long sector = 0;
                while (sector < totalSectors && !ct.IsCancellationRequested)
                {
                    int toRead = (int)Math.Min(BLOCK_SECTORS, totalSectors - sector);
                    NativeMethods.SetFilePointerEx(handle, sector * 512, out _, 0);

                    sw.Restart();
                    bool ok = NativeMethods.ReadFile(handle, buffer, (uint)(toRead * 512),
                        out uint bytesRead, IntPtr.Zero);
                    sw.Stop();

                    double ms = sw.Elapsed.TotalMilliseconds;
                    System.Windows.Media.Color blockColor;

                    if (!ok || bytesRead == 0)
                    {
                        blockColor = System.Windows.Media.Color.FromRgb(239, 68, 68); // red
                        Interlocked.Increment(ref bad);
                        int errCode = Marshal.GetLastWin32Error();
                        long lba = sector;
                        Dispatcher.InvokeAsync(() =>
                            BadSectorList.Items.Add($"LBA {lba:N0}  —  Win32 Error {errCode}"));
                    }
                    else if (ms > 150) { blockColor = System.Windows.Media.Color.FromRgb(255, 100, 0); Interlocked.Increment(ref slow); }
                    else if (ms > 50)  { blockColor = System.Windows.Media.Color.FromRgb(255, 200, 0); Interlocked.Increment(ref slow); }
                    else               { blockColor = System.Windows.Media.Color.FromRgb(30, 180, 50); }

                    // Add map block
                    int blockIdx = Interlocked.Increment(ref _surfaceMapBlockCount);
                    var bc = blockColor;
                    if (blockIdx % 8 == 0) // throttle UI updates
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            var rect = new System.Windows.Shapes.Rectangle
                            {
                                Width = 8, Height = 8, Margin = new Thickness(1),
                                Fill = new System.Windows.Media.SolidColorBrush(bc),
                                ToolTip = $"LBA {sector:N0} — {ms:F1}ms"
                            };
                            SurfaceMapPanel.Children.Add(rect);
                        });
                    }

                    processed += toRead;
                    sector    += toRead;

                    if (processed % REPORT_EVERY == 0 || sector >= totalSectors)
                    {
                        double pct     = totalSectors > 0 ? processed * 100.0 / totalSectors : 0;
                        double elapsed = (DateTime.Now - startTime).TotalSeconds;
                        double speed   = elapsed > 0 ? (processed * 512.0 / 1024 / 1024) / elapsed : 0;
                        double eta     = speed > 0 ? ((totalSectors - processed) * 512.0 / 1024 / 1024) / speed : 0;
                        long badSnap = Interlocked.Read(ref bad);
                        long slowSnap = Interlocked.Read(ref slow);

                        Dispatcher.InvokeAsync(() =>
                        {
                            SurfaceProgressBar.Value = pct;
                            TxtSurfacePct  .Text = $"{pct:F1}%";
                            TxtSurfaceBad  .Text = badSnap.ToString();
                            TxtSurfaceSlow .Text = slowSnap.ToString();
                            TxtSurfaceSpeed.Text = $"{speed:F0} MB/s";
                            TxtSurfaceLba  .Text = $"{sector:N0}";
                            TxtSurfaceEta  .Text = eta > 3600
                                ? $"{eta / 3600:F0}h {(eta % 3600) / 60:F0}m"
                                : $"{eta / 60:F0}m {eta % 60:F0}s";
                            TxtSurfaceStatus.Text = $"Scan: {processed:N0} / {totalSectors:N0} sectors";
                        });
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                NativeMethods.CloseHandle(handle);
            }

            long finalBad = Interlocked.Read(ref bad);
            long finalSlow = Interlocked.Read(ref slow);
            Dispatcher.InvokeAsync(() =>
            {
                SurfaceProgressBar.Value = 100;
                TxtSurfacePct.Text = "100%";
                TxtSurfaceBad.Text  = finalBad.ToString();
                TxtSurfaceSlow.Text = finalSlow.ToString();
                TxtSurfaceStatus.Text = ct.IsCancellationRequested
                    ? $"Stopped at {processed:N0} sectors. Bad: {finalBad}, Slow: {finalSlow}"
                    : finalBad == 0
                        ? $"✓ Scan complete — no bad sectors found! ({finalSlow} slow)"
                        : $"⚠ Scan complete — {finalBad} bad sector(s), {finalSlow} slow";

                // Show repair suggestion when bad sectors found
                if (SurfaceRepairPanel != null)
                {
                    SurfaceRepairPanel.Visibility = finalBad > 0
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (TxtSurfaceRepairInfo != null && finalBad > 0)
                        TxtSurfaceRepairInfo.Text =
                            $"{finalBad} bad sector(s) found. chkdsk /r will attempt to remap them " +
                            $"so Windows never writes data to them again. This requires a reboot and takes 30–90 min.";
                }
            });
        }

        private async void SurfaceRepair_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Determine drive letter from selected disk
            int idx = CbSurfaceDisk.SelectedIndex;
            string driveLetter = "C:";
            if (idx >= 0 && idx < _allDisks.Count)
            {
                var disk = _allDisks[idx];
                // Try to get drive letter from partitions
                var part = disk.Partitions?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Letter));
                if (part != null) driveLetter = part.Letter.TrimEnd('\\');
                else driveLetter = "C:";
            }

            bool confirmed = AppDialog.Confirm(
                $"This will schedule chkdsk /r on {driveLetter} for the next reboot.\n\n" +
                $"• The scan reads and rewrites every sector\n" +
                $"• Bad sectors are remapped so Windows won't use them again\n" +
                $"• Duration: 30–90 minutes depending on drive size\n" +
                $"• Your data is NOT deleted\n\n" +
                $"Reboot now to start repair?",
                "Schedule Disk Repair");

            if (!confirmed) return;

            bool scheduled = false;
            string error = "";
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Schedule chkdsk /r via fsutil or reg key
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        $"/c echo y | chkdsk {driveLetter} /r /x")
                    {
                        UseShellExecute      = true,
                        Verb                 = "runas",   // elevate
                        CreateNoWindow       = true,
                        WindowStyle          = System.Diagnostics.ProcessWindowStyle.Hidden,
                    };
                    // For system drive (C:), chkdsk schedules on reboot automatically
                    // For other drives, /x forces dismount and runs immediately if possible
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(5000);
                    scheduled = true;
                }
                catch (Exception ex) { error = ex.Message; }
            });

            if (scheduled)
            {
                AppDialog.Show(
                    $"chkdsk /r scheduled for {driveLetter}.\n\n" +
                    $"If this is the system drive (C:), it will run automatically on the next reboot.\n" +
                    $"Reboot your computer now to start the repair.",
                    "Repair Scheduled", AppDialog.Kind.Success, this);
            }
            else
            {
                AppDialog.Show(
                    $"Could not schedule repair: {error}\n\n" +
                    $"Try running manually as Administrator:\n  chkdsk {driveLetter} /r",
                    "Repair Failed", AppDialog.Kind.Error, this);
            }
        }

        private static long GetDiskSectorCount(IntPtr handle)
        {
            var ptr = Marshal.AllocHGlobal(48);
            try
            {
                bool ok = NativeMethods.DeviceIoControl(handle, 0x000700A0,
                    IntPtr.Zero, 0, ptr, 48, out uint ret, IntPtr.Zero);
                if (ok && ret >= 32)
                {
                    long diskSize   = Marshal.ReadInt64(ptr, 24);
                    long sectorSize = Marshal.ReadInt32(ptr, 4);
                    if (sectorSize <= 0) sectorSize = 512;
                    return diskSize / sectorSize;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return 200_000_000L; // fallback ~100 GB
        }

        // ── Benchmark tab ────────────────────────────────────────────────────

        private void SelectUsbDrive_Click(object s, RoutedEventArgs e)
        {
            if (CbBenchDrive.Items.Count == 0) PopulateBenchDriveCombo();

            // Find first removable or non-fixed drive (USB stick, external SSD)
            string? usbDrive = null;
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.IsReady && (d.DriveType == System.IO.DriveType.Removable
                                  || d.DriveType == System.IO.DriveType.Network))
                {
                    usbDrive = d.RootDirectory.FullName.TrimEnd('\\', ':') + ":";
                    break;
                }
            }

            if (usbDrive == null)
            {
                if (TxtBenchNote != null)
                    TxtBenchNote.Text = "No USB / removable drive found. Connect a USB drive and try again.";
                return;
            }

            // Select it in the combo (add if not there yet)
            if (!CbBenchDrive.Items.Contains(usbDrive))
                CbBenchDrive.Items.Add(usbDrive);
            CbBenchDrive.SelectedItem = usbDrive;

            if (TxtBenchNote != null)
                TxtBenchNote.Text = $"✓ Selected USB drive {usbDrive} — press Start Full Benchmark to test it.";
        }

        private void PopulateBenchDriveCombo()
        {
            CbBenchDrive.Items.Clear();
            var drives = new HashSet<string>();
            // Add all ready drives including USB/removable
            foreach (var d in System.IO.DriveInfo.GetDrives())
                if (d.IsReady)
                    drives.Add(d.RootDirectory.FullName.TrimEnd('\\', ':') + ":");
            // Also add from SMART data
            foreach (var d in _allDisks)
                foreach (var p in d.Partitions)
                    if (!string.IsNullOrEmpty(p.Letter))
                        drives.Add(p.Letter.TrimEnd('\\', ':') + ":");
            if (drives.Count == 0) drives.Add("C:");
            foreach (var dr in drives.OrderBy(x => x)) CbBenchDrive.Items.Add(dr);
            CbBenchDrive.SelectedIndex = 0;
        }

        private CancellationTokenSource? _fullBenchCts;

        private async void RunFullBench_Click(object sender, RoutedEventArgs e)
        {
            string drive = CbBenchDrive.SelectedItem?.ToString() ?? "C:";
            string root  = drive + "\\";
            _fullBenchCts?.Cancel();
            _fullBenchCts = new CancellationTokenSource();
            var ct = _fullBenchCts.Token;

            BtnRunFullBench.IsEnabled = false;
            if (BtnDiskBench != null) BtnDiskBench.IsEnabled = false;
            if (BenchLiveProgressRow != null) BenchLiveProgressRow.Visibility = Visibility.Visible;
            if (BenchHddAnim != null) BenchHddAnim.Visibility = Visibility.Visible;
            if (TxtBenchAnimPhase != null) TxtBenchAnimPhase.Text = "Preparing benchmark…";
            if (TxtBenchAnimSpeed != null) TxtBenchAnimSpeed.Text = "";
            if (TxtBenchAnimUnit  != null) TxtBenchAnimUnit.Text  = "";

            // ── Stage constants ────────────────────────────────────────────
            // Stage 1: Small files (4K)   → 0 – 25 % of bar
            // Stage 2: Large file seq     → 25 – 75 % of bar  (uses _diskBench service)
            // Stage 3: Random 4K IOPS     → 75 – 90 % of bar
            // Stage 4: Latency            → 90 – 100 % of bar
            const double MaxRefSpeed = 3500.0;  // NVMe reference for bar scaling

            void SetPhase(string msg, double barFraction)
            {
                Dispatcher.Invoke(() =>
                {
                    if (TxtBenchPhase    != null) TxtBenchPhase.Text    = msg;
                    if (TxtBenchAnimPhase != null) TxtBenchAnimPhase.Text = msg;
                    if (TxtBenchLiveSpeed != null) TxtBenchLiveSpeed.Text = "";
                    if (TxtBenchAnimSpeed != null) TxtBenchAnimSpeed.Text = "";
                    if (TxtBenchAnimUnit  != null) TxtBenchAnimUnit.Text  = "";
                    if (BenchLiveBar     != null)
                    {
                        double trackW = (BenchLiveBar.Parent as FrameworkElement)?.ActualWidth ?? 300;
                        BenchLiveBar.Width = Math.Max(4, trackW * Math.Clamp(barFraction, 0, 1));
                    }
                });
            }

            void SetSpeed(double mbps, double barFractionBase, double barFractionRange)
            {
                Dispatcher.Invoke(() =>
                {
                    if (TxtBenchLiveSpeed != null) TxtBenchLiveSpeed.Text = $"{mbps:F0}";
                    if (TxtBenchAnimSpeed != null) TxtBenchAnimSpeed.Text = $"{mbps:F0}";
                    if (TxtBenchAnimUnit  != null) TxtBenchAnimUnit.Text  = "MB/s";
                    if (BenchLiveBar != null)
                    {
                        double trackW   = (BenchLiveBar.Parent as FrameworkElement)?.ActualWidth ?? 300;
                        double speedPct = Math.Clamp(mbps / MaxRefSpeed, 0, 1);
                        BenchLiveBar.Width = Math.Max(4, trackW * (barFractionBase + speedPct * barFractionRange));
                    }
                });
            }

            DiskBenchmarkResult? seqResult   = null;
            Random4KResult?      rand4kResult = null;
            SmallFilesResult?    smallResult  = null;

            try
            {
                // ═══════════════════════════════════════════════════════════
                // STAGE 1 — Small files (4 KB writes + reads, 512 iterations)
                // ═══════════════════════════════════════════════════════════
                SetPhase("Stage 1/4 — Small files (4 KB)…", 0.02);
                smallResult = await RunSmallFilesTestAsync(root, ct,
                    onProgress: (done, total, mbps) =>
                        SetSpeed(mbps, 0.0, 0.25 * done / total));

                ct.ThrowIfCancellationRequested();

                // ═══════════════════════════════════════════════════════════
                // STAGE 2 — Large file sequential (existing service)
                // ═══════════════════════════════════════════════════════════
                SetPhase("Stage 2/4 — Large file sequential…", 0.25);
                var seqProgress = new Progress<string>(msg => Dispatcher.Invoke(() =>
                {
                    if (TxtBenchPhase != null) TxtBenchPhase.Text = $"Stage 2/4 — {msg}";
                    var m = System.Text.RegularExpressions.Regex.Match(msg, @"([\d]+\.[\d]+)\s*MB/s");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double spd))
                        SetSpeed(spd, 0.25, 0.50);
                }));
                seqResult = await _diskBench.RunAsync(root, seqProgress, ct);

                ct.ThrowIfCancellationRequested();

                // ═══════════════════════════════════════════════════════════
                // STAGE 3 — Random 4K IOPS
                // ═══════════════════════════════════════════════════════════
                SetPhase("Stage 3/4 — Random 4K IOPS…", 0.75);
                rand4kResult = await RunRandom4KTestAsync(root, ct);

                ct.ThrowIfCancellationRequested();

                // ═══════════════════════════════════════════════════════════
                // STAGE 4 — Latency (already included in rand4kResult)
                // ═══════════════════════════════════════════════════════════
                SetPhase("Stage 4/4 — Measuring latency…", 0.90);
                // Latency is already collected during Stage 3; small pause for visual clarity
                await Task.Delay(300, ct);

                // ── Done ───────────────────────────────────────────────────
                Dispatcher.Invoke(() =>
                {
                    if (BenchLiveProgressRow != null) BenchLiveProgressRow.Visibility = Visibility.Visible;
                    if (BenchHddAnim         != null) BenchHddAnim.Visibility = Visibility.Collapsed;
                    if (TxtBenchPhase        != null) TxtBenchPhase.Text        = "✓ Benchmark complete";
                    if (TxtBenchLiveSpeed    != null) TxtBenchLiveSpeed.Text    = seqResult != null ? $"{seqResult.SeqReadMBs:F0}" : "";
                    if (BenchLiveBar != null && seqResult != null)
                    {
                        double trackW = (BenchLiveBar.Parent as FrameworkElement)?.ActualWidth ?? 300;
                        BenchLiveBar.Width = Math.Max(4, trackW * Math.Clamp(seqResult.SeqReadMBs / MaxRefSpeed, 0, 1));
                    }
                    if (seqResult != null)
                        BenchResultsPanel.Children.Add(
                            BuildFullBenchCard(drive, seqResult, rand4kResult, smallResult));
                });
            }
            catch (OperationCanceledException)
            {
                if (BenchLiveProgressRow != null) BenchLiveProgressRow.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                if (BenchLiveProgressRow != null) BenchLiveProgressRow.Visibility = Visibility.Collapsed;
                AppDialog.Show(_L($"Benchmark error:\n{ex.Message}", $"Eroare benchmark:\n{ex.Message}"));
            }
            finally
            {
                BtnRunFullBench.IsEnabled = true;
                if (BtnDiskBench != null) BtnDiskBench.IsEnabled = true;
            }
        }

        // ── Small files test ─────────────────────────────────────────────────
        private record SmallFilesResult(double WriteIOPS, double ReadIOPS, double WriteMBs, double ReadMBs);

        private async Task<SmallFilesResult> RunSmallFilesTestAsync(
            string root, CancellationToken ct,
            Action<int, int, double>? onProgress = null)
        {
            return await Task.Run(() =>
            {
                const int BLOCK     = 4096;
                const int FILES     = 128;   // 128 small files × 4 KB
                const int ITER_EACH = 4;     // write+read each file 4 times
                var buf = new byte[BLOCK];
                new Random(42).NextBytes(buf);

                string dir = System.IO.Path.Combine(root, $"_smd_bench_{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(dir);
                var paths = Enumerable.Range(0, FILES)
                    .Select(i => System.IO.Path.Combine(dir, $"f{i}.tmp"))
                    .ToArray();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long writtenBytes = 0, readBytes = 0;
                int total = FILES * ITER_EACH;
                int done  = 0;

                // Write pass
                foreach (var p in paths)
                {
                    for (int k = 0; k < ITER_EACH; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var fs = new FileStream(p, FileMode.Create, FileAccess.Write,
                            FileShare.None, BLOCK, FileOptions.WriteThrough);
                        fs.Write(buf, 0, BLOCK);
                        writtenBytes += BLOCK;
                        done++;
                        double elSec  = sw.Elapsed.TotalSeconds;
                        double mbps   = elSec > 0 ? writtenBytes / 1_048_576.0 / elSec : 0;
                        onProgress?.Invoke(done, total * 2, mbps);
                    }
                }
                double writeElapsed = sw.Elapsed.TotalSeconds;
                double writeIOPS    = (FILES * ITER_EACH) / Math.Max(writeElapsed, 0.001);
                double writeMBs     = writtenBytes / 1_048_576.0 / Math.Max(writeElapsed, 0.001);

                sw.Restart();

                // Read pass
                foreach (var p in paths)
                {
                    for (int k = 0; k < ITER_EACH; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!System.IO.File.Exists(p)) continue;
                        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read,
                            FileShare.Read, BLOCK, FileOptions.SequentialScan);
                        fs.Read(buf, 0, BLOCK);
                        readBytes += BLOCK;
                        done++;
                        double elSec = sw.Elapsed.TotalSeconds;
                        double mbps  = elSec > 0 ? readBytes / 1_048_576.0 / elSec : 0;
                        onProgress?.Invoke(done, total * 2, mbps);
                    }
                }
                double readElapsed = sw.Elapsed.TotalSeconds;
                double readIOPS    = (FILES * ITER_EACH) / Math.Max(readElapsed, 0.001);
                double readMBs     = readBytes / 1_048_576.0 / Math.Max(readElapsed, 0.001);

                // Cleanup
                try { System.IO.Directory.Delete(dir, true); } catch { }

                return new SmallFilesResult(
                    Math.Round(writeIOPS), Math.Round(readIOPS), writeMBs, readMBs);
            }, ct);
        }

        private record Random4KResult(double ReadIOPS, double WriteIOPS, double LatP50, double LatP95, double LatP99);

        private async Task<Random4KResult> RunRandom4KTestAsync(string root, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                const int BLOCK = 4096;
                const int ITER  = 3000;
                string tmp = System.IO.Path.Combine(root, $"_wd_bench_{Guid.NewGuid():N}.tmp");
                var rng    = new Random(42);
                var buf    = new byte[BLOCK];
                rng.NextBytes(buf);

                // Create 256MB test file
                try
                {
                    using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BLOCK,
                        FileOptions.WriteThrough | (FileOptions)0x20000000);
                    for (int i = 0; i < 256 * 1024 * 1024 / BLOCK && !ct.IsCancellationRequested; i++)
                        fs.Write(buf, 0, BLOCK);
                }
                catch { return new Random4KResult(0, 0, 0, 0, 0); }

                long maxOff = (256L * 1024 * 1024 / BLOCK - 1) * BLOCK;
                var latencies = new List<double>(ITER);
                var sw = new System.Diagnostics.Stopwatch();

                // 4K random read
                double readIops = 0;
                try
                {
                    using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, BLOCK,
                        FileOptions.RandomAccess);
                    sw.Restart();
                    for (int i = 0; i < ITER && !ct.IsCancellationRequested; i++)
                    {
                        fs.Seek((long)(rng.NextDouble() * maxOff / BLOCK) * BLOCK, SeekOrigin.Begin);
                        var t = System.Diagnostics.Stopwatch.GetTimestamp();
                        fs.Read(buf, 0, BLOCK);
                        latencies.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    }
                    sw.Stop();
                    readIops = ITER / sw.Elapsed.TotalSeconds;
                }
                catch { }

                // 4K random write
                double writeIops = 0;
                try
                {
                    using var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BLOCK,
                        FileOptions.RandomAccess | FileOptions.WriteThrough);
                    sw.Restart();
                    for (int i = 0; i < ITER / 3 && !ct.IsCancellationRequested; i++)
                    {
                        fs.Seek((long)(rng.NextDouble() * maxOff / BLOCK) * BLOCK, SeekOrigin.Begin);
                        fs.Write(buf, 0, BLOCK);
                    }
                    sw.Stop();
                    writeIops = (ITER / 3) / sw.Elapsed.TotalSeconds;
                }
                catch { }

                try { File.Delete(tmp); } catch { }

                latencies.Sort();
                int n = latencies.Count;
                return new Random4KResult(
                    Math.Round(readIops),
                    Math.Round(writeIops),
                    n > 0 ? latencies[(int)(n * 0.50)] : 0,
                    n > 0 ? latencies[(int)(n * 0.95)] : 0,
                    n > 0 ? latencies[(int)(n * 0.99)] : 0
                );
            }, ct);
        }

        private UIElement BuildFullBenchCard(string drive, DiskBenchmarkResult seq,
            Random4KResult? r4k, SmallFilesResult? small = null)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("BgCardBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush2"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12)
            };
            var sp = new StackPanel();

            sp.Children.Add(new TextBlock
            {
                Text = $"Benchmark complet — {drive}  •  {DateTime.Now:HH:mm:ss}",
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 12)
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"Rating: {seq.Rating}",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(seq.RatingColor)!),
                Margin = new Thickness(0, 0, 0, 10)
            });

            // ── 5.1: Drive type assessment & comparison to industry averages ───
            string driveAssessment;
            string assessColor;
            string avgCompare;
            double seqReadMbs = seq.SeqReadMBs;
            if (seqReadMbs >= 2500)
            {
                driveAssessment = "💡 Your SSD performs like an NVMe PCIe 4.0 — excellent performance!";
                assessColor = "#22C55E";
                avgCompare  = $"Seq Read: {seqReadMbs:F0} MB/s vs. medie NVMe PCIe 4.0: ~5000 MB/s";
            }
            else if (seqReadMbs >= 1200)
            {
                driveAssessment = "✅ Your SSD performs like a standard NVMe PCIe 3.0.";
                assessColor = "#60A5FA";
                avgCompare  = $"Seq Read: {seqReadMbs:F0} MB/s vs. medie NVMe PCIe 3.0: ~3500 MB/s";
            }
            else if (seqReadMbs >= 400)
            {
                driveAssessment = "⚠ Your SSD performs like a standard SATA SSD — below NVMe potential.";
                assessColor = "#F59E0B";
                avgCompare  = $"Seq Read: {seqReadMbs:F0} MB/s vs. medie SSD SATA: ~550 MB/s {(seqReadMbs >= 500 ? "✓" : "↓")}";
            }
            else
            {
                driveAssessment = "⚠ Low performance — possible HDD or degraded SSD.";
                assessColor = "#EF4444";
                avgCompare  = $"Seq Read: {seqReadMbs:F0} MB/s vs. medie HDD: ~120 MB/s {(seqReadMbs >= 100 ? "✓" : "↓")}";
            }

            sp.Children.Add(new Border
            {
                Background  = new SolidColorBrush(WpfColor.FromArgb(30,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).R,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).G,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).B)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).R,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).G,
                    (byte)((WpfColor)WpfColorConv.ConvertFromString(assessColor)!).B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin  = new Thickness(0, 0, 0, 12),
                Child   = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = driveAssessment, FontSize = 12,
                            Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(assessColor)!),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 4)
                        },
                        new TextBlock
                        {
                            Text = avgCompare, FontSize = 11,
                            Foreground = (Brush)FindResource("TextSecondaryBrush"),
                        }
                    }
                }
            });

            // ── Stage 1: Small files ──────────────────────────────────────
            if (small != null)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "Stage 1 — Small files (4 KB)",
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 6)
                });
                var smallGrid = new System.Windows.Controls.Primitives.UniformGrid
                    { Columns = 2, Margin = new Thickness(0, 0, 0, 12) };
                smallGrid.Children.Add(MakeBenchStat("Small Write", $"{small.WriteIOPS:N0} IOPS", GetResourceHex("StatusWarningColor", "#F59E0B")));
                smallGrid.Children.Add(MakeBenchStat("Small Read",  $"{small.ReadIOPS:N0} IOPS",  GetResourceHex("StatusInfoColor",    "#60A5FA")));
                sp.Children.Add(smallGrid);
            }

            // ── Stage 2 + 3: Sequential + 4K random ──────────────────────
            sp.Children.Add(new TextBlock
            {
                Text = "Stage 2–4 — Sequential & 4K random",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 10) };
            grid.Children.Add(MakeBenchStat("Seq Read",  $"{seq.SeqReadMBs:F0} MB/s", GetResourceHex("ChartBlueColor",    "#3B82F6")));
            grid.Children.Add(MakeBenchStat("Seq Write", $"{seq.SeqWriteMBs:F0} MB/s", GetResourceHex("StatusSuccessColor", "#059669")));
            bool hasR4k = r4k != null;
            grid.Children.Add(MakeBenchStat("4K Read",  hasR4k ? $"{r4k!.ReadIOPS:N0} IOPS"  : "—", GetResourceHex("NavPageAccentColor",  "#8B5CF6")));
            grid.Children.Add(MakeBenchStat("4K Write", hasR4k ? $"{r4k!.WriteIOPS:N0} IOPS" : "—", GetResourceHex("StatusWarningColor", "#F59E0B")));
            string latColor = GetResourceHex("StatusSuccessColor", ThemeManager.IsLight(SettingsService.Current.ThemeName) ? "#16A34A" : "#22C55E");
            grid.Children.Add(MakeBenchStat("Lat P50", hasR4k ? $"{r4k!.LatP50:F2} ms" : "—", latColor));
            grid.Children.Add(MakeBenchStat("Lat P99", hasR4k ? $"{r4k!.LatP99:F2} ms" : "—",
                hasR4k && r4k!.LatP99 > 10 ? GetResourceHex("StatusErrorColor", "#EF4444") : latColor));
            sp.Children.Add(grid);

            card.Child = sp;
            return card;
        }

        /// <summary>Reads a Color resource and returns its hex string, or the fallback if not found.</summary>
        private static string GetResourceHex(string resourceKey, string fallbackHex)
        {
            if (Application.Current.TryFindResource(resourceKey) is WpfColor c)
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return fallbackHex;
        }

        private UIElement MakeBenchStat(string label, string value, string color)
        {
            var border = new Border
            {
                Background = (Brush)FindResource("BgCardBrush"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4),
                BorderBrush = (Brush)FindResource("CardBorderBrush"), BorderThickness = new Thickness(1)
            };
            var sp = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush"), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(color)!),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            border.Child = sp;
            return border;
        }

        private void OpenCrystalDiskInfo_Click(object s, RoutedEventArgs e)
        {
            var path = SettingsService.Current.CrystalDiskInfoPath;
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                if (AppDialog.Confirm(
"CrystalDiskInfo not found.\nOpen the download page?", "CrystalDiskInfo"))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"https://crystalmark.info/en/software/crystaldiskinfo/") { UseShellExecute = true });
            }
        }

        private UIElement BuildDiskCard(DiskHealthEntry disk)
        {
            var outer = new Border
            {
                Background = (Brush)FindResource("BgCardBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var sp = new StackPanel();

            // ── Header ────────────────────────────────────────────────────────
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = $"{disk.Model}",
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var healthBlock = new TextBlock
            {
                Text = $"{disk.HealthPercent}%  Health",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(disk.HealthColor)!),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 0); Grid.SetColumn(healthBlock, 1);
            header.Children.Add(nameBlock); header.Children.Add(healthBlock);
            sp.Children.Add(header);

            // ── Subtitle ──────────────────────────────────────────────────────
            // SMART attribute 0xC2 (194) = Temperature
            var tempAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC2);
            string tempStr = tempAttr != null && tempAttr.RawValue > 0 && tempAttr.RawValue < 100
                ? $"•   {tempAttr.RawValue}°C"
                : "";
            sp.Children.Add(new TextBlock
            {
                Text = $"{disk.MediaType}   •   {disk.Size}   •   S/N: {disk.SerialNumber}   •   Status: {disk.Status}{tempStr}",
                FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 10)
            });

            // ── Health bar — proportional to ActualWidth ──────────────────────
            var healthBarBg = new Border
            {
                Background = (Brush)FindResource("BgInputBrush"),
                CornerRadius = new CornerRadius(4), Height = 10,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var healthBarFg = new Border
            {
                Background = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(disk.HealthColor)!),
                CornerRadius = new CornerRadius(4), Height = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            healthBarFg.Loaded += (_, _) =>
                healthBarFg.Width = Math.Max(4, healthBarBg.ActualWidth * disk.HealthPercent / 100.0);
            healthBarBg.SizeChanged += (_, _) =>
                healthBarFg.Width = Math.Max(4, healthBarBg.ActualWidth * disk.HealthPercent / 100.0);
            healthBarBg.Child = healthBarFg;
            sp.Children.Add(healthBarBg);

            // Health scale labels
            var scaleRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition());
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition());
            scaleRow.Children.Add(new TextBlock { Text = "0%  Bad", FontSize = 9, Foreground = (Brush)FindResource("TextSecondaryBrush") });
            var maxLbl = new TextBlock { Text = "100%  Perfect", FontSize = 9, Foreground = (Brush)FindResource("TextSecondaryBrush"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Grid.SetColumn(maxLbl, 1); scaleRow.Children.Add(maxLbl);
            sp.Children.Add(scaleRow);

            // ── Partitions — separate card ────────────────────────────────────
            if (disk.Partitions.Count > 0)
            {
                var partCard = new Border
                {
                    Background = (Brush)FindResource("BgCardBrush"),
                    BorderBrush = (Brush)FindResource("CardBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(0, 0, 0, 4),
                    Margin = new Thickness(0, 14, 0, 0)
                };
                var partOuter = new StackPanel();
                partCard.Child = partOuter;

                // Card header
                var partHeader = new Border
                {
                    Background = (Brush)(TryFindResource("BgHoverBrush") ?? System.Windows.Media.Brushes.Transparent),
                    CornerRadius = new CornerRadius(10, 10, 0, 0),
                    Padding = new Thickness(14, 8, 14, 8)
                };
                partHeader.Child = new TextBlock
                {
                    Text = "Partitions",
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                };
                partOuter.Children.Add(partHeader);

                var partSp2 = new StackPanel { Margin = new Thickness(14, 8, 14, 4) };
                partOuter.Children.Add(partSp2);

                foreach (var part in disk.Partitions)
                {
                    var partSp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                    // Row 1: letter + label + free text (no overlap)
                    var row1 = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    row1.Children.Add(new TextBlock
                    {
                        Text = part.Letter,
                        FontSize = 14, FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("AccentBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                    var labelTb = new TextBlock
                    {
                        Text = $"{part.Label}  ({part.FileSystem})  •  {part.TotalGB:F1} GB total",
                        FontSize = 11,
                        Foreground = (Brush)FindResource("TextSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(labelTb, 1);
                    row1.Children.Add(labelTb);

                    var freeTb = new TextBlock
                    {
                        Text = $"{part.FreeGB:F1} GB free",
                        FontSize = 11, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(part.FreeColor)!),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(freeTb, 2);
                    row1.Children.Add(freeTb);

                    partSp.Children.Add(row1);

                    // Row 2: gradient bar (same style as dashboard)
                    var pBarGrid = new Grid { Height = 12, Margin = new Thickness(0, 4, 0, 0) };
                    pBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part.UsedPct, GridUnitType.Star) });
                    pBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - part.UsedPct, GridUnitType.Star) });

                    // Used portion — gradient based on usage level
                    WpfColor gradStart, gradEnd;
                    if (part.UsedPct >= 85)
                    {
                        gradStart = (Application.Current.TryFindResource("ChartOrangeColor") as WpfColor?) ?? (WpfColor)WpfColorConv.ConvertFromString("#F97316")!;
                        gradEnd   = (Application.Current.TryFindResource("StatusErrorColor")  as WpfColor?) ?? (WpfColor)WpfColorConv.ConvertFromString("#EF4444")!;
                    }
                    else
                    {
                        gradStart = (Application.Current.TryFindResource("StatusSuccessColor") as WpfColor?) ?? (WpfColor)WpfColorConv.ConvertFromString("#22C55E")!;
                        gradEnd   = (Application.Current.TryFindResource("StatusSuccessColor") as WpfColor?) ?? (WpfColor)WpfColorConv.ConvertFromString("#16A34A")!;
                    }
                    var gradBrush = new LinearGradientBrush(
                        new GradientStopCollection { new GradientStop(gradStart, 0.0), new GradientStop(gradEnd, 1.0) },
                        new System.Windows.Point(0, 0), new System.Windows.Point(1, 0));
                    var pBarUsed2 = new Border
                    {
                        Background   = gradBrush,
                        CornerRadius = part.UsedPct >= 99 ? new CornerRadius(4) : new CornerRadius(4, 0, 0, 4),
                        Height       = 12,
                    };
                    var pBarFree2 = new Border
                    {
                        Background   = (Brush)FindResource("BgInputBrush"),
                        CornerRadius = part.UsedPct <= 1 ? new CornerRadius(4) : new CornerRadius(0, 4, 4, 0),
                        Height       = 12,
                    };
                    Grid.SetColumn(pBarFree2, 1);
                    pBarGrid.Children.Add(pBarUsed2);
                    pBarGrid.Children.Add(pBarFree2);
                    partSp.Children.Add(pBarGrid);

                    // Row 3: used% label aligned right
                    partSp.Children.Add(new TextBlock
                    {
                        Text = $"{part.UsedPct}% used  •  {part.FreeGB:F1} GB free",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("TextSecondaryBrush"),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                    // Click pe partitie → deschide in Explorer
                    if (!string.IsNullOrEmpty(part.Letter))
                    {
                        var letter = part.Letter.TrimEnd('\\');
                        partSp.Cursor = System.Windows.Input.Cursors.Hand;
                        partSp.ToolTip = $"Click to open {letter} in Explorer";
                        partSp.MouseLeftButtonUp += (s, e) =>
                        {
                            try { System.Diagnostics.Process.Start("explorer.exe", letter + "\\"); }
                            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                            e.Handled = true;
                        };
                        partSp.MouseEnter += (s, _) => ((StackPanel)s).Background =
                            (Brush)(TryFindResource("BgHoverBrush") ?? System.Windows.Media.Brushes.Transparent);
                        partSp.MouseLeave += (s, _) => ((StackPanel)s).Background =
                            System.Windows.Media.Brushes.Transparent;
                    }

                    partSp2.Children.Add(partSp);
                }
                sp.Children.Add(partCard);
            }

            outer.Child = sp;
            return outer;
        }

        // ── RAM ───────────────────────────────────────────────────────────────

        private async Task LoadRamInternalAsync()
        {
            ShowLoading(_L("Reading RAM modules...", "Se citesc modulele RAM..."));
            try
            {
                _ramModules = await _hwService.GetRamAsync();
                RamGrid.ItemsSource = _ramModules;
                long totalGB = _ramModules.Sum(r => {
                    if (r.Capacity.EndsWith(" GB") && double.TryParse(r.Capacity[..^3], out double g)) return (long)g;
                    return 0L;
                });
                int occupied = _ramModules.Count(r => !r.IsEmpty);
                int total    = _ramModules.Count;
                TxtRamTotal.Text = $"RAM total: {_summary.TotalRam}  •  Sloturi: {occupied}/{total} ocupate";
                BuildRamSlotDiagram(_ramModules);

                // ── XMP/EXPO Detection ────────────────────────────────────────────
                await DetectXmpStatusAsync(_ramModules);
            }
            finally { HideLoading(); }
        }

        private async Task DetectXmpStatusAsync(List<SMDWin.Models.RamEntry> modules)
        {
            try
            {
                // Get configured speed (what's actually running) and rated speed (SPD max)
                var (configuredMHz, ratedMHz) = await Task.Run(() =>
                {
                    int configured = 0, rated = 0;
                    try
                    {
                        using var s1 = SMDWin.Services.WmiHelper.Searcher(
"SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
                        foreach (System.Management.ManagementObject obj in s1.Get())
                        {
                            if (obj["ConfiguredClockSpeed"] is uint c && c > 0)
                                configured = (int)Math.Max(configured, c);
                            if (obj["Speed"] is uint sp && sp > 0)
                                rated = (int)Math.Max(rated, sp);
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    return (configured, rated);
                });

                bool ro = LanguageService.CurrentCode == "ro";
                if (configuredMHz > 0 && ratedMHz > 0)
                {
                    // XMP/EXPO = running significantly below rated speed
                    // Threshold: >8% below rated (e.g. DDR4-3200 running at 2133 = XMP off)
                    double ratio = configuredMHz / (double)ratedMHz;
                    bool xmpOff = ratio < 0.92;

                    // Warning banner (only when XMP is off)
                    if (XmpAlertBanner != null)
                    {
                        XmpAlertBanner.Visibility = xmpOff ? Visibility.Visible : Visibility.Collapsed;
                        if (xmpOff && TxtXmpAlertTitle != null && TxtXmpAlertSub != null)
                        {
                            TxtXmpAlertTitle.Text = ro
                                ? "XMP/EXPO disabled — RAM running below rated speed"
                                : "XMP/EXPO not enabled — RAM running below spec";
                            TxtXmpAlertSub.Text = ro
                                ? $"Viteza curentă: {configuredMHz} MHz  •  Viteza max (SPD): {ratedMHz} MHz  •  Activați XMP/EXPO din BIOS pentru performanță maximă"
                                : $"Current speed: {configuredMHz} MHz  •  Rated max (SPD): {ratedMHz} MHz  •  Enable XMP/EXPO in BIOS for full performance";
                        }
                    }

                    // LED status badge — always visible when data is available
                    if (XmpStatusBadge != null)
                    {
                        XmpStatusBadge.Visibility = Visibility.Visible;
                        var ledColor  = xmpOff
                            ? (Application.Current.TryFindResource("StatusWarningColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(245, 158, 11)
                            : (Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34,  197, 94);
                        var ledBase   = ledColor;
                        var badgeBorderColor = System.Windows.Media.Color.FromArgb(80, ledBase.R, ledBase.G, ledBase.B);
                        if (XmpLedDot  != null) XmpLedDot.Fill = new System.Windows.Media.SolidColorBrush(ledColor);
                        if (XmpStatusBadge != null) XmpStatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(badgeBorderColor);
                        if (TxtXmpBadgeState != null)
                        {
                            TxtXmpBadgeState.Text       = xmpOff ? (ro ? "Dezactivat" : "Disabled") : (ro ? "Activ" : "Active");
                            TxtXmpBadgeState.Foreground = new System.Windows.Media.SolidColorBrush(ledColor);
                        }
                        if (TxtXmpTooltipTitle != null)
                            TxtXmpTooltipTitle.Text = xmpOff
                                ? (ro ? "XMP/EXPO neactivat" : "XMP/EXPO not enabled")
                                : (ro ? "XMP/EXPO activ" : "XMP/EXPO active");
                        if (TxtXmpTooltipSub != null)
                            TxtXmpTooltipSub.Text = ro
                                ? $"Viteză curentă: {configuredMHz} MHz  •  Viteză max (SPD): {ratedMHz} MHz"
                                : $"Current: {configuredMHz} MHz  •  Rated max (SPD): {ratedMHz} MHz";
                    }
                }
                else
                {
                    // WMI returned no data (VM or unsupported hardware) — show badge as N/A
                    if (XmpAlertBanner  != null) XmpAlertBanner.Visibility  = Visibility.Collapsed;
                    if (XmpStatusBadge  != null)
                    {
                        XmpStatusBadge.Visibility = Visibility.Visible;
                        var naColor = (Application.Current.TryFindResource("TextSecondaryColor") as System.Windows.Media.Color?)
                                      ?? System.Windows.Media.Color.FromRgb(107, 114, 128);
                        if (XmpLedDot       != null) XmpLedDot.Fill        = new System.Windows.Media.SolidColorBrush(naColor);
                        if (TxtXmpBadgeState!= null) { TxtXmpBadgeState.Text = "N/A"; TxtXmpBadgeState.Foreground = new System.Windows.Media.SolidColorBrush(naColor); }
                        if (TxtXmpTooltipTitle != null) TxtXmpTooltipTitle.Text = "XMP/EXPO — data unavailable";
                        if (TxtXmpTooltipSub   != null) TxtXmpTooltipSub.Text   = "RAM clock data could not be read (may be virtual machine or unsupported hardware)";
                        XmpStatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(60, naColor.R, naColor.G, naColor.B));
                    }
                }
            }
            catch
            {
                if (XmpAlertBanner != null) XmpAlertBanner.Visibility = Visibility.Collapsed;
                if (XmpStatusBadge != null) XmpStatusBadge.Visibility = Visibility.Collapsed;
            }
        }

        private async void LoadRam_Click(object s, RoutedEventArgs e) => await LoadRamInternalAsync();
        private void OpenMemDiag_Click(object s, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppDialog.Show(ex.Message); }
        }

        private async void RunRamBench_Click(object s, RoutedEventArgs e)
        {
            if (BtnRamBench != null) { BtnRamBench.IsEnabled = false; BtnRamBench.Content = "Benchmark..."; }
            StartRamScanAnimation();
            try
            {
                var cts = new CancellationTokenSource();
                var (readGBs, writeGBs) = await Task.Run(() => RunRamBenchmark(cts.Token));

                if (TxtRamReadSpeed  != null) TxtRamReadSpeed.Text  = $"{readGBs:F1}";
                if (TxtRamWriteSpeed != null) TxtRamWriteSpeed.Text = $"{writeGBs:F1}";

                // Proportional bars inside the card (max ref ~50 GB/s)
                double maxRef = 50.0;
                UpdateRamBar(RamReadBar,  readGBs  / maxRef);
                UpdateRamBar(RamWriteBar, writeGBs / maxRef);

                string rating = readGBs >= 40 ? "DDR5 Excellent"
                              : readGBs >= 25 ? "DDR4 Dual-Channel"
                              : readGBs >= 15 ? "DDR4 Single-Channel"
                              : "Low bandwidth";
                string note = readGBs >= 25 ? "Dual-channel active" : "Enable dual-channel for better performance";
                if (TxtRamBenchRating != null) TxtRamBenchRating.Text = rating;
                if (TxtRamBenchNote   != null) TxtRamBenchNote.Text   = note;
                if (BenchResultGrid   != null) BenchResultGrid.Visibility = Visibility.Visible;

                // Show on each populated module
                SetModuleResults(null);  // null = success (no error info)
            }
            finally
            {
                StopRamScanAnimation();
                if (BtnRamBench != null) { BtnRamBench.IsEnabled = true; BtnRamBench.Content = "Run Benchmark"; }
            }
        }

        /// Resize a RamBar border proportionally once it has actual width.
        private static void UpdateRamBar(Border? bar, double fraction)
        {
            if (bar == null) return;
            fraction = Math.Clamp(fraction, 0, 1);
            void Apply()
            {
                if (bar.Parent is Border track && track.ActualWidth > 0)
                    bar.Width = track.ActualWidth * fraction;
            }
            // If parent already has width, apply immediately
            if (bar.Parent is Border t && t.ActualWidth > 0) { Apply(); return; }
            // Otherwise wait for layout — use LayoutUpdated on parent track
            EventHandler? h = null;
            h = (_, _) =>
            {
                if (bar.Parent is Border track2 && track2.ActualWidth > 0)
                {
                    bar.Width = track2.ActualWidth * fraction;
                    track2.LayoutUpdated -= h;
                }
            };
            if (bar.Parent is Border parentTrack)
                parentTrack.LayoutUpdated += h;
            else
            {
                // Fallback: defer via Dispatcher
                bar.Dispatcher.InvokeAsync(Apply, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // ── TEMPERATURES ──────────────────────────────────────────────────────

        // 4.2 — Track daily max temperatures per sensor name
        private readonly Dictionary<string, double> _maxTodayTemps = new(StringComparer.OrdinalIgnoreCase);

        private async Task LoadTempsInternalAsync()
        {
            ShowLoading(_L("Reading temperatures...", "Se citesc temperaturile..."));
            try
            {
                _lastTemps = await _hwService.GetTemperaturesAsync();

                // 4.2 — Update daily max per sensor
                foreach (var t in _lastTemps)
                {
                    if (t.Temperature <= 0) continue;
                    if (!_maxTodayTemps.TryGetValue(t.Name, out double prev) || t.Temperature > prev)
                        _maxTodayTemps[t.Name] = t.Temperature;
                    t.MaxToday = _maxTodayTemps.GetValueOrDefault(t.Name, 0);
                }

                if (TxtTempBackend != null)
                    TxtTempBackend.Text = _lastTemps.Count > 0
                        ? string.Join("|", _lastTemps.Select(t => $"{t.Name}: {t.Display}"))
                        : "No temperature sensors detected.";
            }
            finally { HideLoading(); }
        }

        private async void LoadTemps_Click(object s, RoutedEventArgs e) => await LoadTempsInternalAsync();

        private UIElement BuildTempCard(TemperatureEntry t)
        {
            var color = t.Temperature < 0 ? "#94A3B8" : t.TempColor;
            var card = new Border
            {
                Background = (Brush)FindResource("BgCardBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 12, 18, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left column: name + context label
            var leftSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            leftSp.Children.Add(new TextBlock
            {
                Text = $"{t.Name}", FontSize = 14,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            // 4.2 Context label: "Normal" / "⚠ Ridicat (prag: 70°C)" / "✗ Critic"
            if (t.Temperature >= 0)
            {
                var ctxLabel = new TextBlock
                {
                    FontSize = 11,
                    Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(color)!),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                if (t.Temperature >= 70)
                    ctxLabel.Text = $"{t.StatusIcon} {t.StatusLabel}  (prag: {t.ThresholdValue}°C)";
                else
                    ctxLabel.Text = $"✔ Normal";
                if (t.MaxToday > 0)
                    ctxLabel.Text += $"  •  Max azi: {t.MaxToday:F0}°C";
                leftSp.Children.Add(ctxLabel);
            }

            var temp = new TextBlock
            {
                Text = t.Display, FontSize = 30, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString(color)!)
            };
            Grid.SetColumn(leftSp, 0); Grid.SetColumn(temp, 1);
            grid.Children.Add(leftSp); grid.Children.Add(temp);
            card.Child = grid;
            return card;
        }

        // ── NETWORK ───────────────────────────────────────────────────────────

        // ── Adapter card view-model ───────────────────────────────────────────
        private class AdapterCardVm
        {
            public string Name       { get; set; } = "";
            public string Type       { get; set; } = "";
            public string Status     { get; set; } = "";
            public string IpAddress  { get; set; } = "—";
            public string MacAddress { get; set; } = "—";
            public string Gateway    { get; set; } = "—";
            public string Dns        { get; set; } = "—";
            public string Speed      { get; set; } = "—";
            public string ExtraLabel { get; set; } = "";
            public string ExtraValue { get; set; } = "";

            // Icon per type — SVG path data for Viewbox rendering
            public string TypeIcon => Type switch
            {
"Wi-Fi"=> "M5 12.55a11 11 0 0 1 14.08 0 M1.42 9a16 16 0 0 1 21.16 0 M8.53 16.11a6 6 0 0 1 6.95 0 M12 20h.01",
"Bluetooth" => "M6.5 6.5l11 11L12 23V1l5.5 5.5-11 11",
"VPN"=> "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z",
"Ethernet"=> "M4 7h16v10H4V7z M7 7V4h2v3 M11 7V4h2v3 M15 7V4h2v3 M8 17v2 M16 17v2",
                _           => "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2z",
            };

            // Coloured circle behind icon
            public System.Windows.Media.Brush IconBg => Type switch
            {
"Wi-Fi"=> new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromArgb(40, 14, 165, 233)),  // sky-blue tint
"Bluetooth" => new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromArgb(40, 99, 102, 241)),  // indigo tint
"VPN"=> new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromArgb(40, 124, 58, 237)),  // violet tint
"Ethernet"=> new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromArgb(40, 5, 150, 105)),   // green tint
                _           => new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromArgb(40, 100, 116, 139)), // slate tint
            };

            // Status pill colours
            public System.Windows.Media.Brush PillBg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40,
                    ((Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94)).R,
                    ((Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94)).G,
                    ((Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94)).B))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 100, 116, 139));
            public System.Windows.Media.Brush PillFg => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94))
                : new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("TextSecondaryColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(148, 163, 184));

            // LED indicator — green dot for Up, dim red for Down
            public System.Windows.Media.Brush LedColor => Status == "Up"
                ? new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94))
                : new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("StatusErrorColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(180, 50, 50));
            public System.Windows.Media.Color LedGlowColor => Status == "Up"
                ? (Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94)
                : (Application.Current.TryFindResource("StatusErrorColor")   as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(200, 60, 60);
            public double LedGlowOpacity => Status == "Up" ? 0.85 : 0.35;
        }

        private async Task LoadNetworkAsync()
        {
            ShowLoading(_L("Reading network adapters...", "Se citesc adaptoarele de rețea..."));
            // Ensure tab buttons start with correct styles on first load
            ApplyNetTabStyles("NetOverview");
            try
            {
                var adapters = await _netService.GetAdaptersAsync();

                // Keep legacy NetGrid working
                NetGrid.ItemsSource = adapters;

                // Build card view-models for the visual cards panel
                if (AdapterCardsPanel != null)
                {
                    var cards = adapters.Select(a => new AdapterCardVm
                    {
                        Name       = a.Name,
                        Type       = a.Type,
                        Status     = a.Status,
                        IpAddress  = string.IsNullOrEmpty(a.IpAddress)  ? "—" : a.IpAddress,
                        MacAddress = string.IsNullOrEmpty(a.MacAddress) ? "—" : a.MacAddress,
                        Gateway    = string.IsNullOrEmpty(a.Gateway)    ? "—" : a.Gateway,
                        Dns        = string.IsNullOrEmpty(a.Dns)        ? "—" : a.Dns,
                        Speed      = string.IsNullOrEmpty(a.Speed)      ? "—" : a.Speed,
                        ExtraLabel = a.Type == "Wi-Fi" ? "Band" : "",
                        ExtraValue = "",   // signal % could be added later
                    }).ToList();
                    AdapterCardsPanel.ItemsSource = cards;
                }
            }
            finally { HideLoading(); }
        }

        private async void LoadNetwork_Click(object s, RoutedEventArgs e) => await LoadNetworkAsync();

        // ── Open router admin page in default browser ─────────────────────────
        private void OpenRouterPage_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // Find first active adapter with a gateway
                string? gw = null;
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var gwa in ni.GetIPProperties().GatewayAddresses)
                    {
                        string g = gwa.Address.ToString();
                        if (!g.StartsWith("169.254") && g.Contains('.'))
                        { gw = g; break; }
                    }
                    if (gw != null) break;
                }
                if (string.IsNullOrEmpty(gw))
                { AppDialog.Show(_L("No gateway found.", "Nu s-a găsit gateway.")); return; }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"http://{gw}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Could not open router page:\n{ex.Message}");
            }
        }

        // ── Inline DNS Lookup ─────────────────────────────────────────────────
        private void DnsHost_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) RunDnsLookup_Click(s, null!);
        }

        private void DnsQuick_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn && btn.Tag is string host)
            {
                if (TxtDnsHost != null) TxtDnsHost.Text = host;
                RunDnsLookup_Click(s, e);
            }
        }

        private async void RunDnsLookup_Click(object s, RoutedEventArgs e)
        {
            string host = TxtDnsHost?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(host)) return;

            if (TxtDnsStatus  != null) TxtDnsStatus.Text = $"Looking up {host}…";
            if (DnsResultBorder != null) DnsResultBorder.Visibility = Visibility.Collapsed;
            if (BtnRunDns != null) BtnRunDns.IsEnabled = false;

            try
            {
                var entry = await System.Threading.Tasks.Task.Run(
                    () => System.Net.Dns.GetHostEntry(host));

                var addresses = entry.AddressList.Select(a => a.ToString()).ToList();
                string displayHost = entry.HostName;

                if (TxtDnsStatus    != null) TxtDnsStatus.Text = $"Resolved {addresses.Count} address(es):";
                if (TxtDnsHostResult!= null) TxtDnsHostResult.Text = $"{displayHost}";
                if (DnsAddressList  != null) DnsAddressList.ItemsSource = addresses;
                if (DnsResultBorder != null) DnsResultBorder.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                if (TxtDnsStatus != null)
                    TxtDnsStatus.Text = $"Lookup failed: {ex.Message}";
            }
            finally
            {
                if (BtnRunDns != null) BtnRunDns.IsEnabled = true;
            }
        }

        private void NetTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string tab = btn.Tag?.ToString() ?? "NetOverview";
            ApplyNetTabStyles(tab);

            if (NetTabOverview  != null) NetTabOverview.Visibility  = tab == "NetOverview" ? Visibility.Visible : Visibility.Collapsed;
            if (NetTabTools     != null) NetTabTools.Visibility     = tab == "NetTools"    ? Visibility.Visible : Visibility.Collapsed;
            if (NetTabSecurity  != null) NetTabSecurity.Visibility  = tab == "NetSecurity" ? Visibility.Visible : Visibility.Collapsed;
            if (NetTabApps      != null) NetTabApps.Visibility      = tab == "NetApps"     ? Visibility.Visible : Visibility.Collapsed;

            // Auto-load firewall rules on first open
            if (tab == "NetSecurity" && FirewallGrid?.ItemsSource == null)
                LoadFirewallRules_Click(this, new RoutedEventArgs());

            // Load network apps on first open
            if (tab == "NetApps")
                LoadNetApps();

            // Ensure sub-pills are initialised when Tools tab first shown
            if (tab == "NetTools")
            {
                var activeStyle   = (Style)(TryFindResource("SubTabButtonActiveStyle") ?? new Style(typeof(Button)));
                var inactiveStyle = (Style)(TryFindResource("SubTabButtonStyle")       ?? new Style(typeof(Button)));
                if (BtnNetSubSpeed    != null) BtnNetSubSpeed.Style    = activeStyle;
                if (BtnNetSubDiagnose != null) BtnNetSubDiagnose.Style = inactiveStyle;
                if (BtnNetSubScanners != null) BtnNetSubScanners.Style = inactiveStyle;
                if (NetPaneSpeed    != null) NetPaneSpeed.Visibility    = Visibility.Visible;
                if (NetPaneDiagnose != null) NetPaneDiagnose.Visibility = Visibility.Collapsed;
                if (NetPaneScanners != null) NetPaneScanners.Visibility = Visibility.Collapsed;
            }
        }

        // ── NetApps live traffic ──────────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _netAppsLiveTimer;

        private void LoadNetApps()
        {
            if (TxtNetAppsStatus != null) TxtNetAppsStatus.Text = "Loading…";
            System.Threading.Tasks.Task.Run(() =>
            {
                // Primul Refresh() activează EStats și colectează snapshot inițial (KB/s = 0 la primul apel)
                SMDWin.Services.TcpEStatsService.Instance.Refresh();
                var entries = GetNetConnections();
                Dispatcher.BeginInvoke(() =>
                {
                    if (NetAppsGrid != null) NetAppsGrid.ItemsSource = entries;
                    if (TxtNetAppsStatus != null)
                        TxtNetAppsStatus.Text = $"{entries.Count} connections";
                    // Start live refresh timer — 2s interval pentru delta KB/s precis
                    if (_netAppsLiveTimer == null)
                    {
                        _netAppsLiveTimer = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromSeconds(2) };
                        _netAppsLiveTimer.Tick += (_, _) => RefreshNetAppsLive();
                    }
                    _netAppsLiveTimer.Start();
                });
            });
        }

        private void RefreshNetAppsLive()
        {
            // Only refresh when tab is visible
            if (NetTabApps?.Visibility != Visibility.Visible) { _netAppsLiveTimer?.Stop(); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                // Refresh EStats snapshot → calculează delta față de snapshot anterior
                SMDWin.Services.TcpEStatsService.Instance.Refresh();
                var entries = GetNetConnections();
                Dispatcher.BeginInvoke(() =>
                {
                    if (NetAppsGrid != null) NetAppsGrid.ItemsSource = entries;
                    if (TxtNetAppsStatus != null)
                        TxtNetAppsStatus.Text = $"{entries.Count} connections • live";
                });
            });
        }

        private void RefreshNetApps_Click(object sender, RoutedEventArgs e) => LoadNetApps();

        /// <summary>5.5 — Quick Block/Unblock from inline grid button (Tag = NetAppEntry).</summary>
        private async void NetAppsQuickBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SMDWin.Models.NetAppEntry entry) return;

            if (entry.IsBlocked)
            {
                // Unblock
                if (string.IsNullOrEmpty(entry.ProcessPath)) { AppDialog.Show($"No path found for {entry.ProcessName}.", "Cannot unblock", AppDialog.Kind.Warning, this); return; }
                bool ok = await _firewallSvc.UnblockAppAsync(entry.ProcessPath);
                if (ok) AppDialog.Show($"{entry.ProcessName} unblocked.", "Firewall", AppDialog.Kind.Success, this);
                else    AppDialog.Show("Could not remove rule — run as Administrator.", "Failed", AppDialog.Kind.Error, this);
            }
            else
            {
                // Block
                if (string.IsNullOrEmpty(entry.ProcessPath)) { AppDialog.Show($"No path found for {entry.ProcessName}.\nRun as Administrator to detect paths.", "Cannot block", AppDialog.Kind.Warning, this); return; }
                bool ok = await _firewallSvc.BlockAppAsync(entry.ProcessPath);
                if (ok) AppDialog.Show($"{entry.ProcessName} blocked from outbound connections.", "Firewall", AppDialog.Kind.Success, this);
                else    AppDialog.Show("Could not add rule — run as Administrator.", "Failed", AppDialog.Kind.Error, this);
            }
            LoadNetApps();
        }

        // PERF FIX: process name/path cache with TTL — avoids Process.GetProcesses() every 2s.
        // Processes don't change frequently; 15s TTL is more than enough.
        private Dictionary<int, (string name, string path)> _procInfoCache = new();
        private DateTime _procInfoCacheBuilt = DateTime.MinValue;
        private static readonly TimeSpan ProcInfoCacheTtl = TimeSpan.FromSeconds(15);

        private Dictionary<int, (string name, string path)> GetCachedProcInfo()
        {
            if ((DateTime.Now - _procInfoCacheBuilt) < ProcInfoCacheTtl && _procInfoCache.Count > 0)
                return _procInfoCache;

            var fresh = new Dictionary<int, (string name, string path)>();
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
                    fresh[p.Id] = (p.ProcessName, path);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                finally { p.Dispose(); }
            }
            _procInfoCache = fresh;
            _procInfoCacheBuilt = DateTime.Now;
            return fresh;
        }

        private List<SMDWin.Models.NetAppEntry> GetNetConnections()
        {
            var result = new List<SMDWin.Models.NetAppEntry>();
            try
            {
                // PERF FIX: GetExtendedTcpTable via P/Invoke — replaces netstat subprocess.
                // No child process, no stdout parsing, no 300–500ms wait. ~10x faster.
                var tcpRows = SMDWin.Services.TcpEStatsService.GetTcpConnectionsWithPid();
                var procInfo = GetCachedProcInfo();

                // Group connections per PID
                var connsByPid = new Dictionary<int, List<SMDWin.Services.TcpConnectionInfo>>();
                foreach (var row in tcpRows)
                {
                    if (row.Pid == 0) continue;
                    // Skip pure loopback
                    if (row.Local.StartsWith("127.0.0.1") &&
                        (row.Remote == "*:*" || row.Remote.StartsWith("127.0.0.1"))) continue;

                    if (!connsByPid.TryGetValue(row.Pid, out var list))
                        connsByPid[row.Pid] = list = new();
                    list.Add(row);
                }

                foreach (var kv in connsByPid)
                {
                    int pid   = kv.Key;
                    var conns = kv.Value;

                    // Best connection: ESTABLISHED > LISTEN > first
                    var best = conns.Find(c => c.State == "ESTABLISHED")
                            ?? conns.Find(c => c.State == "LISTEN")
                            ?? conns[0];

                    procInfo.TryGetValue(pid, out var info);
                    string procName = info.name ?? $"PID {pid}";
                    string procPath = info.path ?? "";

                    bool isBlocked = !string.IsNullOrEmpty(procPath) && _firewallSvc.IsAppBlocked(procPath);

                    // Traffic via EStats (already collected by background Refresh tick)
                    SMDWin.Services.TcpEStatsService.Instance.TryGetPidTraffic(
                        pid, out double sendKBs, out double recvKBs,
                        out ulong totalSent, out ulong totalRecv);

                    bool hasEstablished = conns.Any(c => c.State == "ESTABLISHED");
                    string displayState = hasEstablished ? "ESTABLISHED"
                        : conns.Any(c => c.State == "LISTEN") ? "LISTEN"
                        : best.State;

                    result.Add(new SMDWin.Models.NetAppEntry
                    {
                        ProcessName     = procName,
                        ProcessPath     = procPath,
                        Pid             = pid,
                        Protocol        = "TCP",
                        LocalEndpoint   = best.Local,
                        RemoteEndpoint  = best.Remote,
                        State           = displayState,
                        SendKBs         = sendKBs,
                        RecvKBs         = recvKBs,
                        TotalSentMB     = totalSent / 1_048_576.0,
                        TotalRecvMB     = totalRecv / 1_048_576.0,
                        IsBlocked       = isBlocked,
                        ConnectionCount = conns.Count,
                    });
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            return result
                .OrderByDescending(x => x.TotalTrafficKBs)
                .ThenByDescending(x => x.State == "ESTABLISHED")
                .ThenBy(x => x.AppCategory)
                .ThenBy(x => x.ProcessName)
                .ToList();
        }

        private void NetAppsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (NetAppsGrid?.SelectedItem is SMDWin.Models.NetAppEntry entry)
                ShowNetAppDetails(entry);
        }

        private void NetAppsGrid_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Context menu is bound in XAML — just ensure selection
            var dg = sender as System.Windows.Controls.DataGrid;
            if (dg?.InputHitTest(e.GetPosition(dg)) is System.Windows.FrameworkElement fe)
            {
                var row = FindParent<System.Windows.Controls.DataGridRow>(fe);
                if (row != null) row.IsSelected = true;
            }
        }

        private void NetAppsDetails_Click(object sender, RoutedEventArgs e)
        {
            if (NetAppsGrid?.SelectedItem is SMDWin.Models.NetAppEntry entry)
                ShowNetAppDetails(entry);
        }

        private async void NetAppsBlock_Click(object sender, RoutedEventArgs e)
        {
            if (NetAppsGrid?.SelectedItem is not SMDWin.Models.NetAppEntry entry) return;
            if (string.IsNullOrEmpty(entry.ProcessPath))
            {
                AppDialog.Show(
                    $"No executable path found for {entry.ProcessName}.\nRun SMDWin as Administrator to enable path detection.",
                    "Cannot block", AppDialog.Kind.Warning, this);
                return;
            }
            bool ok = await _firewallSvc.BlockAppAsync(entry.ProcessPath);
            if (ok)
                AppDialog.Show(
                    $"{entry.ProcessName} has been blocked from making outbound connections.\nRule added to Windows Firewall.",
                    "Blocked", AppDialog.Kind.Success, this);
            else
                AppDialog.Show(
                    "Could not add firewall rule. Make sure SMDWin is running as Administrator.",
                    "Failed", AppDialog.Kind.Error, this);
            LoadNetApps();
        }

        private async void NetAppsUnblock_Click(object sender, RoutedEventArgs e)
        {
            if (NetAppsGrid?.SelectedItem is not SMDWin.Models.NetAppEntry entry) return;
            if (string.IsNullOrEmpty(entry.ProcessPath))
            {
                AppDialog.Show(
                    $"No executable path found for {entry.ProcessName}.",
                    "Cannot unblock", AppDialog.Kind.Warning, this);
                return;
            }
            bool ok = await _firewallSvc.UnblockAppAsync(entry.ProcessPath);
            if (ok)
                AppDialog.Show(
                    $"{entry.ProcessName} has been unblocked.\nFirewall rule removed.",
                    "Unblocked", AppDialog.Kind.Success, this);
            else
                AppDialog.Show(
                    "Could not remove firewall rule. Make sure SMDWin is running as Administrator.",
                    "Failed", AppDialog.Kind.Error, this);
            LoadNetApps();
        }

        private void NetAppsAnalyzeProcess_Click(object sender, RoutedEventArgs e)
        {
            if (NetAppsGrid?.SelectedItem is SMDWin.Models.NetAppEntry entry)
            {
                // Navigate to Process Monitor and search for this process
                _ = NavigateTo("Processes");
                Dispatcher.BeginInvoke(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(400);
                    if (TxtProcSearch != null)
                    {
                        TxtProcSearch.Text = entry.ProcessName;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void ShowNetAppDetails(SMDWin.Models.NetAppEntry entry)
        {
            var bg      = Application.Current.TryFindResource("BgDarkBrush")        as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11,0x14,0x18));
            var bgCard  = Application.Current.TryFindResource("BgCardBrush")        as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C,0x21,0x28));
            var border  = Application.Current.TryFindResource("CardBorderBrush")    as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23,0x2D,0x3F));
            var textPri = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.SolidColorBrush ?? System.Windows.Media.Brushes.White;
            var textSec = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
            var green   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34,197,94));
            var red     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239,68,68));

            var win = new System.Windows.Window
            {
                Title  = $"Network Details — {entry.ProcessName}",
                Width  = 660,
                SizeToContent = System.Windows.SizeToContent.Height,
                MaxHeight = 640,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner  = this,
                ResizeMode   = System.Windows.ResizeMode.NoResize,
                WindowStyle  = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background   = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new System.Windows.Thickness(0),
                GlassFrameThickness = new System.Windows.Thickness(0), UseAeroCaptionButtons = false,
            });
            win.KeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape) win.Close(); };

            var outerGrid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(12) };
            // Shadow wrapper (no ClipToBounds so shadow isn't clipped)
            var shadowBorder = new System.Windows.Controls.Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
            };
            shadowBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28, ShadowDepth = 4, Direction = 270,
                Color = System.Windows.Media.Color.FromRgb(0,0,0), Opacity = 0.55,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
            // Card with ClipToBounds for rounded corners
            var outerBorder = new System.Windows.Controls.Border
            {
                Background = bg, BorderBrush = border, BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(12), ClipToBounds = true,
            };
            shadowBorder.Child = outerBorder;
            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            outerBorder.Child = root;
            outerGrid.Children.Add(shadowBorder);
            win.Content = outerGrid;

            // ── Title bar ──────────────────────────────────────────────────
            var titleBar = new System.Windows.Controls.Border { Background = bg, Padding = new System.Windows.Thickness(20,14,12,14) };
            var titleGrid = new System.Windows.Controls.Grid();
            titleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var titleStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            titleStack.Children.Add(new System.Windows.Controls.TextBlock { Text = entry.CategoryIcon, FontSize = 16, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0,0,8,0) });
            titleStack.Children.Add(new System.Windows.Controls.TextBlock { Text = entry.ProcessName, FontSize = 14, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = textPri, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            System.Windows.Controls.Grid.SetColumn(titleStack, 0);
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Width = 32, Height = 32, FontSize = 13,
                Foreground = textSec, Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Style = TryFindResource("CloseIconButtonStyle") as Style, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            closeBtn.Click += (_, _) => win.Close();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(titleStack); titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            titleBar.MouseLeftButtonDown += (_, me) => { if (me.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            // ── 2-column content ───────────────────────────────────────────
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            };
            System.Windows.Controls.Grid.SetRow(scroll, 1);

            var twoCol = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16,8,16,20) };
            twoCol.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            twoCol.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(12) });
            twoCol.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            var leftCol  = new System.Windows.Controls.StackPanel();
            var rightCol = new System.Windows.Controls.StackPanel();
            System.Windows.Controls.Grid.SetColumn(leftCol, 0);
            System.Windows.Controls.Grid.SetColumn(rightCol, 2);

            var sep = new System.Windows.Controls.Border { Height = 1, Background = border, Opacity = 0.5, Margin = new System.Windows.Thickness(0,0,0,14) };
            System.Windows.Controls.Grid.SetColumnSpan(sep, 3);
            twoCol.Children.Add(sep);
            twoCol.Children.Add(leftCol);
            twoCol.Children.Add(rightCol);

            // helpers
            void Section(System.Windows.Controls.StackPanel col, string title) =>
                col.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = title.ToUpperInvariant(), FontSize = 10, FontWeight = System.Windows.FontWeights.Bold,
                    Foreground = textSec, Opacity = 0.6, Margin = new System.Windows.Thickness(0,6,0,8),
                });

            System.Windows.Controls.TextBlock MakeRow(System.Windows.Controls.StackPanel col,
                string label, string value, System.Windows.Media.Brush? valueBrush = null)
            {
                var row = new System.Windows.Controls.Border
                {
                    Background = bgCard, BorderBrush = border, BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new System.Windows.Thickness(12,8,12,8), Margin = new System.Windows.Thickness(0,0,0,6),
                };
                var g = new System.Windows.Controls.Grid();
                g.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
                g.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
                g.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 10, Foreground = textSec, Opacity = 0.75, Margin = new System.Windows.Thickness(0,0,0,2) });
                var valTb = new System.Windows.Controls.TextBlock
                {
                    Text = value, FontSize = 12, FontWeight = System.Windows.FontWeights.SemiBold,
                    Foreground = valueBrush ?? textPri, TextWrapping = System.Windows.TextWrapping.Wrap,
                };
                System.Windows.Controls.Grid.SetRow(valTb, 1);
                g.Children.Add(valTb);
                row.Child = g;
                col.Children.Add(row);
                return valTb;
            }

            // ── STÂNGA: CONNECTION + TRAFFIC ───────────────────────────────
            Section(leftCol, "Connection");
            MakeRow(leftCol, "PID", entry.Pid.ToString());
            MakeRow(leftCol, "Protocol", entry.Protocol);
            string stateColor = entry.StatusColor;
            var stateBrush = new System.Windows.Media.SolidColorBrush(SMDWin.Services.ThemeManager.ParseHex(stateColor));
            MakeRow(leftCol, "State", string.IsNullOrEmpty(entry.State) ? "—" : entry.State, stateBrush);

            Section(leftCol, "Traffic");
            MakeRow(leftCol, "↑ Sent",     $"{entry.TotalSentMB:F2} MB");
            MakeRow(leftCol, "↓ Received", $"{entry.TotalRecvMB:F2} MB");
            MakeRow(leftCol, "Speed",      $"↑ {entry.SendKBs:F1} KB/s  ↓ {entry.RecvKBs:F1} KB/s");

            // ── DREAPTA: ENDPOINTS + SERVER + FIREWALL ─────────────────────
            Section(rightCol, "Endpoints");
            MakeRow(rightCol, "Local",  string.IsNullOrEmpty(entry.LocalEndpoint)  ? "—" : entry.LocalEndpoint);
            string remote = entry.RemoteEndpoint;
            bool hasRemote = !string.IsNullOrEmpty(remote) && remote != "*:*" && remote != "0.0.0.0:0";
            MakeRow(rightCol, "Remote", hasRemote ? remote : "—");

            Section(rightCol, "Remote Server");
            var hostTb = MakeRow(rightCol, "Hostname", "Resolving…");
            var geoTb  = MakeRow(rightCol, "Location", string.IsNullOrEmpty(entry.GeoCountry) ? "Resolving…" : entry.GeoDisplay);
            var ispTb  = MakeRow(rightCol, "ISP / Org", "—");

            Section(rightCol, "Firewall");
            MakeRow(rightCol, "Status", entry.BlockedDisplay, entry.IsBlocked ? red : green);

            // Async DNS + geo
            if (hasRemote)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    string ip = remote.Contains(':') ? remote[..remote.LastIndexOf(':')] : remote;
                    ip = ip.Trim('[', ']');
                    string hostname = "—";
                    try { var host = await System.Net.Dns.GetHostEntryAsync(ip); hostname = host.HostName; }
                    catch { hostname = "Could not resolve"; }
                    Dispatcher.BeginInvoke(() => hostTb.Text = hostname);

                    if (string.IsNullOrEmpty(entry.GeoCountry))
                    {
                        try
                        {
                            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                            var json = await http.GetStringAsync($"http://ip-api.com/json/{ip}?fields=status,country,city,isp,org");
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var r = doc.RootElement;
                            string country = r.TryGetProperty("country", out var c)  ? c.GetString()   ?? "" : "";
                            string city    = r.TryGetProperty("city",    out var ci)  ? ci.GetString()  ?? "" : "";
                            string isp     = r.TryGetProperty("isp",     out var is_) ? is_.GetString() ?? "" : "";
                            string org     = r.TryGetProperty("org",     out var og)  ? og.GetString()  ?? "" : "";
                            string geo     = string.IsNullOrEmpty(city) ? country : $"{city}, {country}";
                            string ispStr  = string.IsNullOrEmpty(org)  ? isp : $"{isp} / {org}";
                            Dispatcher.BeginInvoke(() => { geoTb.Text = string.IsNullOrEmpty(geo) ? "Unknown" : geo; ispTb.Text = string.IsNullOrEmpty(ispStr) ? "—" : ispStr; });
                        }
                        catch { Dispatcher.BeginInvoke(() => geoTb.Text = "Unavailable"); }
                    }
                    else { Dispatcher.BeginInvoke(() => geoTb.Text = entry.GeoDisplay); }
                });
            }
            else { hostTb.Text = "—"; geoTb.Text = "—"; }

            scroll.Content = twoCol;
            root.Children.Add(scroll);
            win.Show();
        }

        private static T? FindParent<T>(System.Windows.DependencyObject child) where T : System.Windows.DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T t) return t;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private void ApplyNetTabStyles(string activeTab)
        {
            var activeStyle   = (Style)(TryFindResource("SubTabButtonActiveStyle") ?? new Style(typeof(Button)));
            var inactiveStyle = (Style)(TryFindResource("SubTabButtonStyle")       ?? new Style(typeof(Button)));

            void SetTab(Button? b, bool active)
            {
                if (b == null) return;
                b.Style = active ? activeStyle : inactiveStyle;
            }

            SetTab(BtnNetTabOverview, activeTab == "NetOverview");
            SetTab(BtnNetTabTools,    activeTab == "NetTools");
            SetTab(BtnNetTabSecurity, activeTab == "NetSecurity");
            SetTab(BtnNetTabApps,     activeTab == "NetApps");
        }
        private void OpenNetworkSettings_Click(object s, RoutedEventArgs e) => _netService.OpenNetworkSettings();
        private void OpenNetAdapters_Click(object s, RoutedEventArgs e) => _netService.OpenNetworkAdapters();

        // ── Network sub-tab pill navigation ──────────────────────────────────

        private void NetSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string pane = btn.Tag?.ToString() ?? "Speed";

            // Show/hide panes
            if (NetPaneSpeed         != null) NetPaneSpeed.Visibility         = pane == "Speed"? Visibility.Visible : Visibility.Collapsed;
            if (NetPaneDiagnose      != null) NetPaneDiagnose.Visibility      = pane == "Diagnose" ? Visibility.Visible : Visibility.Collapsed;
            if (NetPaneScanners      != null) NetPaneScanners.Visibility      = pane == "Scanners" ? Visibility.Visible : Visibility.Collapsed;
            if (NetPaneWifiPasswords != null) NetPaneWifiPasswords.Visibility = pane == "Wifi"? Visibility.Visible : Visibility.Collapsed;

            // Use proper Style resources so template triggers (hover) still work
            var activeStyle   = (Style)(TryFindResource("SubTabButtonActiveStyle") ?? new Style(typeof(Button)));
            var inactiveStyle = (Style)(TryFindResource("SubTabButtonStyle")       ?? new Style(typeof(Button)));

            if (BtnNetSubSpeed    != null) BtnNetSubSpeed.Style    = pane == "Speed"? activeStyle : inactiveStyle;
            if (BtnNetSubDiagnose != null) BtnNetSubDiagnose.Style = pane == "Diagnose" ? activeStyle : inactiveStyle;
            if (BtnNetSubScanners != null) BtnNetSubScanners.Style = pane == "Scanners" ? activeStyle : inactiveStyle;
            if (BtnNetSubWifi     != null) BtnNetSubWifi.Style     = pane == "Wifi"? activeStyle : inactiveStyle;

            // Auto-scan on first open
            if (pane == "Wifi" && WifiPasswordsList?.Children.Count == 0)
                WifiScan_Click(this, new RoutedEventArgs());
        }

        private void DiagSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string pane = btn.Tag?.ToString() ?? "Ping";

            // Show/hide panes
            if (DiagPanePing  != null) DiagPanePing.Visibility  = pane == "Ping"? Visibility.Visible : Visibility.Collapsed;
            if (DiagPaneTrace != null) DiagPaneTrace.Visibility  = pane == "Trace" ? Visibility.Visible : Visibility.Collapsed;
            if (DiagPaneDns   != null) DiagPaneDns.Visibility    = pane == "DNS"? Visibility.Visible : Visibility.Collapsed;

            var activeStyle2   = (Style)(TryFindResource("SubTabButtonActiveStyle") ?? new Style(typeof(Button)));
            var inactiveStyle2 = (Style)(TryFindResource("SubTabButtonStyle")       ?? new Style(typeof(Button)));

            if (BtnDiagSubPing  != null) BtnDiagSubPing.Style  = pane == "Ping"? activeStyle2 : inactiveStyle2;
            if (BtnDiagSubTrace != null) BtnDiagSubTrace.Style = pane == "Trace" ? activeStyle2 : inactiveStyle2;
            if (BtnDiagSubDns   != null) BtnDiagSubDns.Style   = pane == "DNS"? activeStyle2 : inactiveStyle2;
        }

        private void ScanSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string pane = btn.Tag?.ToString() ?? "Ports";

            if (ScanPanePorts    != null) ScanPanePorts.Visibility    = pane == "Ports"? Visibility.Visible : Visibility.Collapsed;
            if (ScanPaneLan      != null) ScanPaneLan.Visibility      = pane == "Lan"? Visibility.Visible : Visibility.Collapsed;
            if (ScanPaneWifi     != null) ScanPaneWifi.Visibility     = pane == "Wifi"? Visibility.Visible : Visibility.Collapsed;
            if (ScanPaneLanSpeed != null) ScanPaneLanSpeed.Visibility = pane == "LanSpeed" ? Visibility.Visible : Visibility.Collapsed;

            var activeStyle3   = (Style)(TryFindResource("SubTabButtonActiveStyle") ?? new Style(typeof(Button)));
            var inactiveStyle3 = (Style)(TryFindResource("SubTabButtonStyle")       ?? new Style(typeof(Button)));

            if (BtnScanSubPorts    != null) BtnScanSubPorts.Style    = pane == "Ports"? activeStyle3 : inactiveStyle3;
            if (BtnScanSubLan      != null) BtnScanSubLan.Style      = pane == "Lan"? activeStyle3 : inactiveStyle3;
            if (BtnScanSubWifi     != null) BtnScanSubWifi.Style     = pane == "Wifi"? activeStyle3 : inactiveStyle3;
            if (BtnScanSubLanSpeed != null) BtnScanSubLanSpeed.Style = pane == "LanSpeed" ? activeStyle3 : inactiveStyle3;
        }

        // LAN Speed Test in-panel handlers
        private void RunLanSpeedTest_Click(object s, RoutedEventArgs e)
        {
            // Toggle: if running, stop
            if (_lanSpeedTestCts != null && !_lanSpeedTestCts.IsCancellationRequested)
            {
                _lanSpeedTestCts.Cancel();
                if (BtnLanSpeedRun != null) { BtnLanSpeedRun.Content = "Run Test"; BtnLanSpeedRun.Background = FindAccentBrush(); }
                return;
            }

            string ip  = TxtLanSpeedIp?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(ip))
            {
                if (TxtLanSpeedStatus != null)
                    TxtLanSpeedStatus.Text = "Enter a target IP or use Find Server first.";
                return;
            }
            if (!int.TryParse(TxtLanSpeedDuration?.Text?.Trim(), out int dur) || dur < 1 || dur > 60)
                dur = 5;

            // Run inline TCP throughput test
            _ = RunLanSpeedInlineAsync(ip, dur);
        }

        private CancellationTokenSource? _lanSpeedTestCts;

        private async Task RunLanSpeedInlineAsync(string ip, int durationSec)
        {
            _lanSpeedTestCts?.Cancel();
            _lanSpeedTestCts = new CancellationTokenSource();
            var ct = _lanSpeedTestCts.Token;

            if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = $"Connecting to {ip}:5201…";
            if (BtnLanSpeedRun != null)
            {
                BtnLanSpeedRun.Content = "■ Stop";
                BtnLanSpeedRun.Style = (Style)TryFindResource("RedButtonStyle");
            }
            if (TxtLanSpeedDown  != null) TxtLanSpeedDown.Text = "—";
            if (TxtLanSpeedUp    != null) TxtLanSpeedUp.Text   = "—";
            if (TxtLanSpeedPing  != null) TxtLanSpeedPing.Text = "—";

            try
            {
                const int port    = 5201;
                const int bufSize = 128 * 1024; // 128KB chunks

                // ── Ping first ────────────────────────────────────────────────
                long pingMs = -1;
                try
                {
                    var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(ip, 2000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        pingMs = reply.RoundtripTime;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                if (TxtLanSpeedPing != null)
                    TxtLanSpeedPing.Text = pingMs >= 0 ? $"{pingMs}" : "—";

                // ── Download test (PC-A reads from server) ─────────────────────
                if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = $"Testing download from {ip}…";
                double downMbps = 0;
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync(ip, port);
                    tcp.SendTimeout = tcp.ReceiveTimeout = (durationSec + 5) * 1000;
                    using var stream = tcp.GetStream();
                    // Send "READ <secs>\n" command
                    var cmd = System.Text.Encoding.ASCII.GetBytes($"READ {durationSec}\n");
                    await stream.WriteAsync(cmd, ct);

                    var buf = new byte[bufSize];
                    long totalBytes = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < durationSec && !ct.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(buf, ct);
                        if (n == 0) break;
                        totalBytes += n;
                        double pct = sw.Elapsed.TotalSeconds / durationSec;
                        if (LanSpeedProgressBar != null)
                            LanSpeedProgressBar.Width = Math.Max(0, Math.Min(1, pct))
                                * (LanSpeedProgressBar.Parent is System.Windows.FrameworkElement p ? p.ActualWidth : 300);
                    }
                    downMbps = totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (TxtLanSpeedStatus != null)
                        TxtLanSpeedStatus.Text = $"Download failed: {ex.Message}";
                }

                if (TxtLanSpeedDown != null)
                    TxtLanSpeedDown.Text = downMbps > 0 ? $"{downMbps:F0}" : "—";

                // ── Upload test (PC-A writes to server) ────────────────────────
                ct.ThrowIfCancellationRequested();
                if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = $"Testing upload to {ip}…";
                double upMbps = 0;
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync(ip, port);
                    tcp.SendTimeout = tcp.ReceiveTimeout = (durationSec + 5) * 1000;
                    using var stream = tcp.GetStream();
                    // Send "WRITE <secs>\n" command
                    var cmd = System.Text.Encoding.ASCII.GetBytes($"WRITE {durationSec}\n");
                    await stream.WriteAsync(cmd, ct);

                    var buf = new byte[bufSize];
                    new Random(1).NextBytes(buf);
                    long totalBytes = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < durationSec && !ct.IsCancellationRequested)
                    {
                        await stream.WriteAsync(buf, ct);
                        totalBytes += buf.Length;
                    }
                    upMbps = totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (TxtLanSpeedStatus != null)
                        TxtLanSpeedStatus.Text = $"Upload failed: {ex.Message}";
                }

                if (TxtLanSpeedUp != null)
                    TxtLanSpeedUp.Text = upMbps > 0 ? $"{upMbps:F0}" : "—";

                if (!ct.IsCancellationRequested && TxtLanSpeedStatus != null)
                    TxtLanSpeedStatus.Text = $"Done. ↓{downMbps:F0} Mbps  ↑{upMbps:F0} Mbps  ping {(pingMs >= 0 ? $"{pingMs}ms" : "—")}";
            }
            catch (OperationCanceledException)
            {
                if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = "Test stopped.";
            }
            catch (Exception ex)
            {
                if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (BtnLanSpeedRun != null) { BtnLanSpeedRun.IsEnabled = true; BtnLanSpeedRun.Content = "Run Test"; BtnLanSpeedRun.Background = FindAccentBrush(); }
                if (LanSpeedProgressBar != null) LanSpeedProgressBar.Width = 0;
            }
        }

        private void StopLanSpeedTest_Click(object s, RoutedEventArgs e)
        {
            _lanSpeedTestCts?.Cancel();
        }

        // ── UDP auto-discovery of SMDWin server ───────────────────────────────
        private async void DiscoverLanSpeedServer_Click(object s, RoutedEventArgs e)
        {
            if (TxtLanSpeedDiscoverStatus != null) TxtLanSpeedDiscoverStatus.Text = "Scanning…";
            if (BtnLanSpeedDiscover != null) BtnLanSpeedDiscover.IsEnabled = false;

            try
            {
                string? found = await Task.Run(async () =>
                {
                    const int udpPort  = 5202;
                    const int tcpPort  = 5201;
                    const int timeoutMs = 3000;

                    // Get local subnet from first non-loopback IPv4
                    string? localIp = null;
                    string? subnet  = null;
                    try
                    {
                        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                        foreach (var a in host.AddressList)
                            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && !System.Net.IPAddress.IsLoopback(a))
                            { localIp = a.ToString(); break; }
                        if (localIp != null)
                            subnet = string.Join(".", localIp.Split('.').Take(3));
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                    // Method 1: listen for UDP beacon that server broadcasts
                    using var udp = new System.Net.Sockets.UdpClient();
                    udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                        System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                    try
                    {
                        udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, udpPort));
                        udp.Client.ReceiveTimeout = timeoutMs;
                        var result = await Task.Run(() =>
                        {
                            try
                            {
                                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                                var data = udp.Receive(ref ep);
                                string msg = System.Text.Encoding.ASCII.GetString(data);
                                if (msg.StartsWith("SMDWIN_SERVER"))
                                    return ep.Address.ToString();
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                            return null;
                        });
                        if (result != null) return result;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                    // Method 2: TCP port scan on /24 subnet
                    if (subnet == null) return null;
                    var tasks = Enumerable.Range(1, 254)
                        .Select(async i =>
                        {
                            string ip = $"{subnet}.{i}";
                            if (ip == localIp) return null;
                            try
                            {
                                using var tcp = new System.Net.Sockets.TcpClient();
                                using var cts = new CancellationTokenSource(400);
                                await tcp.ConnectAsync(ip, tcpPort, cts.Token);
                                return ip;
                            }
                            catch (OperationCanceledException) { return null; }
                            catch (System.Net.Sockets.SocketException) { return null; }
                            catch { return null; }
                        });

                    var results = await Task.WhenAll(tasks);
                    return results.FirstOrDefault(r => r != null);
                });

                if (found != null)
                {
                    if (TxtLanSpeedIp != null) TxtLanSpeedIp.Text = found;
                    if (TxtLanSpeedDiscoverStatus != null)
                        TxtLanSpeedDiscoverStatus.Text = $"✓ Found: {found}";
                    if (TxtLanSpeedStatus != null)
                        TxtLanSpeedStatus.Text = $"Server found at {found} — press Run Test.";
                }
                else
                {
                    if (TxtLanSpeedDiscoverStatus != null)
                        TxtLanSpeedDiscoverStatus.Text = "Not found — enter IP manually.";
                }
            }
            catch (Exception ex)
            {
                if (TxtLanSpeedDiscoverStatus != null)
                    TxtLanSpeedDiscoverStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (BtnLanSpeedDiscover != null) BtnLanSpeedDiscover.IsEnabled = true;
            }
        }
        private System.Net.Sockets.TcpListener? _lanSpeedServer;
        private bool _lanSpeedServerRunning = false;
        private System.Threading.CancellationTokenSource? _lanSpeedServerCts;

        private void ToggleLanSpeedServer_Click(object s, RoutedEventArgs e)
        {
            if (_lanSpeedServerRunning)
            {
                // Stop server
                _lanSpeedServerCts?.Cancel();
                try { _lanSpeedServer?.Stop(); } catch { }
                _lanSpeedServer = null;
                _lanSpeedServerRunning = false;
                if (TxtLanSpeedStatus != null)
                    TxtLanSpeedStatus.Text = "Server stopped.";
                if (BtnLanSpeedServer != null)
                    BtnLanSpeedServer.Content = "Start Server";
                return;
            }

            // Get local IP
            string localIp = "?.?.?.?";
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var addr in host.AddressList)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(addr))
                    { localIp = addr.ToString(); break; }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

            // Start TCP listener on port 5201
            try
            {
                _lanSpeedServer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 5201);
                _lanSpeedServer.Start();
                _lanSpeedServerRunning = true;
                _lanSpeedServerCts = new System.Threading.CancellationTokenSource();
            }
            catch (Exception ex)
            {
                AppDialog.Show($"Could not start server: {ex.Message}", "SMDWin", AppDialog.Kind.Warning);
                return;
            }

            if (BtnLanSpeedServer != null)
                BtnLanSpeedServer.Content = "Stop Server";
            if (TxtLanSpeedStatus != null)
                TxtLanSpeedStatus.Text = $"Server running — this PC's IP: {localIp}";

            // Accept connections + send UDP beacon in background
            var ct = _lanSpeedServerCts.Token;
            _ = Task.Run(() => RunLanSpeedServerLoop(_lanSpeedServer, localIp, ct), ct);
            _ = Task.Run(() => BroadcastUdpBeacon(ct), ct);
        }

        private static async Task RunLanSpeedServerLoop(System.Net.Sockets.TcpListener srv,
            string localIp, System.Threading.CancellationToken ct)
        {
            const int bufSize = 128 * 1024;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tcp = await srv.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (tcp)
                            {
                                tcp.SendTimeout = tcp.ReceiveTimeout = 30_000;
                                using var stream = tcp.GetStream();
                                // Read command line: "READ <secs>\n" or "WRITE <secs>\n"
                                var lineBytes = new System.Collections.Generic.List<byte>(32);
                                int b;
                                while ((b = stream.ReadByte()) != '\n' && b != -1)
                                    lineBytes.Add((byte)b);
                                string line = System.Text.Encoding.ASCII.GetString(lineBytes.ToArray()).Trim();
                                string[] parts = line.Split(' ');
                                string cmd = parts.Length > 0 ? parts[0] : "";
                                int secs = parts.Length > 1 && int.TryParse(parts[1], out int ds) ? ds : 5;

                                if (cmd == "READ")
                                {
                                    // Server sends data → client measures download
                                    var buf = new byte[bufSize];
                                    new Random(42).NextBytes(buf);
                                    var sw = System.Diagnostics.Stopwatch.StartNew();
                                    while (sw.Elapsed.TotalSeconds < secs)
                                        await stream.WriteAsync(buf);
                                }
                                else if (cmd == "WRITE")
                                {
                                    // Server reads data → client measures upload
                                    var buf = new byte[bufSize];
                                    var sw = System.Diagnostics.Stopwatch.StartNew();
                                    while (sw.Elapsed.TotalSeconds < secs)
                                        await stream.ReadAsync(buf);
                                }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200, ct); }
            }
        }

        private static async Task BroadcastUdpBeacon(System.Threading.CancellationToken ct)
        {
            // Broadcast UDP beacon every 2 seconds so other PCs can auto-discover
            const int udpPort = 5202;
            try
            {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.EnableBroadcast = true;
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, udpPort);
                var msg = System.Text.Encoding.ASCII.GetBytes("SMDWIN_SERVER");
                while (!ct.IsCancellationRequested)
                {
                    try { await udp.SendAsync(msg, ep, ct); } catch (Exception logEx) { AppLogger.Warning(logEx, "await udp.SendAsync(msg, ep, ct);"); }
                    await Task.Delay(2000, ct);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
        }

        private void ShowLanServerInfoWindow(string localIp)
        {
            var win = new System.Windows.Window
            {
                Title = "LAN Speed Test — Server",
                Width = 420, Height = 300,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["BgDarkBrush"],
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            // Status indicator
            var statusRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var dot = new System.Windows.Controls.Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("StatusSuccessColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(34, 197, 94)),
                Margin = new Thickness(0, 3, 8, 0) };
            var statusText = new System.Windows.Controls.TextBlock { Text = "Server running", FontSize = 15, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"] };
            statusRow.Children.Add(dot);
            statusRow.Children.Add(statusText);
            panel.Children.Add(statusRow);

            // IP display
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "This PC's IP address:", FontSize = 11, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 0, 0, 4) });
            var ipBox = new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Application.Current.Resources["BgCardBrush"], CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 18) };
            ipBox.Child = new System.Windows.Controls.TextBlock { Text = localIp, FontSize = 22, FontWeight = System.Windows.FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"] };
            panel.Children.Add(ipBox);

            // Instructions
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "On the other PC:\n1. Open SMDWin  >  Network  >  LAN Speed\n2. Enter the IP above in the Target IP field\n3. Press  [ Start Test ]",
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondaryBrush"],
                LineHeight = 20,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 22)
            });

            // Stop button — RedButtonStyle (outline, consistent with design system)
            var stopBtn = new System.Windows.Controls.Button
            {
                Content = "Stop Server",
                Height = 36,
                FontSize = 13,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Style = (System.Windows.Style)Application.Current.Resources["RedButtonStyle"],
            };
            stopBtn.Click += (_, _) =>
            {
                try { _lanSpeedServer?.Stop(); } catch { }
                _lanSpeedServer = null;
                _lanSpeedServerRunning = false;
                if (TxtLanSpeedStatus != null) TxtLanSpeedStatus.Text = "Server stopped.";
                dot.Background = new System.Windows.Media.SolidColorBrush(
                    (Application.Current.TryFindResource("StatusErrorColor") as System.Windows.Media.Color?) ?? System.Windows.Media.Color.FromRgb(185, 28, 28));
                statusText.Text = "Server stopped";
                stopBtn.IsEnabled = false;
                stopBtn.Content = "Server Stopped";
            };
            panel.Children.Add(stopBtn);

            win.Content = panel;
            win.Loaded += (_, _) => ThemeManager.ApplyTitleBarColor(new System.Windows.Interop.WindowInteropHelper(win).Handle, Services.SettingsService.Current.ThemeName ?? "Dark Midnight");
            {
                string _resolved = SMDWin.Services.ThemeManager.Normalize(Services.SettingsService.Current.ThemeName ?? "Dark Midnight");
                if (SMDWin.Services.ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                    SMDWin.Services.ThemeManager.SetCaptionColor(new System.Windows.Interop.WindowInteropHelper(win).Handle, _t["BgDark"]);
            }
            win.Closed += (_, _) =>
            {
                if (_lanSpeedServerRunning)
                {
                    try { _lanSpeedServer?.Stop(); } catch { }
                    _lanSpeedServer = null;
                    _lanSpeedServerRunning = false;
                }
            };
            win.Show();
        }

        private void DnsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && TxtDnsHost != null)
                TxtDnsHost.Text = b.Tag?.ToString() ?? "";
        }

        private void TogglePingSection_Click(object s, RoutedEventArgs e)
        {
            bool show = PingSectionContent.Visibility != Visibility.Visible;
            PingSectionContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtPingSectionArrow.Text = show ? "▼ Hide" : "Show";
        }

        private void TogglePortScanSection_Click(object s, RoutedEventArgs e)
        {
            bool show = PortScanSectionContent.Visibility != Visibility.Visible;
            PortScanSectionContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtPortScanSectionArrow.Text = show ? "▼ Hide" : "Show";
        }
        // ── LAN Scanner (inline, replaces popup window) ─────────────────────
        private void OpenLanScan_Click(object s, RoutedEventArgs e) => RunLanScan_Click(s, e);
        private void OpenLanSpeed_Click(object s, RoutedEventArgs e)
        {
            var win = new NetworkToolWindow("lan_speed") { Owner = this };
            win.Show();
        }

        private async void RunLanScan_Click(object s, RoutedEventArgs e)
        {
            // If already scanning, act as Stop
            if (_lanScanCts != null && !_lanScanCts.IsCancellationRequested)
            {
                _lanScanCts.Cancel();
                return;
            }

            _lanScanCts?.Cancel(); _lanScanCts?.Dispose();
            _lanScanCts = new CancellationTokenSource();

            // Switch button to Stop state
            if (BtnRunLanScan != null)
            {
                BtnRunLanScan.Content    = "Stop";
                BtnRunLanScan.Style = (Style)TryFindResource("RedButtonStyle");
                BtnRunLanScan.IsEnabled  = true;
            }
            if (PbLanScan != null) { PbLanScan.Value = 0; PbLanScan.Visibility = Visibility.Visible; }
            if (TxtLanScanStatus != null) TxtLanScanStatus.Text = "Scanning... (10–20s)";

            // Live result list (sorted by IP last octet)
            var liveList = new System.Collections.ObjectModel.ObservableCollection<LanScanItem>();
            if (LanScanGrid != null) LanScanGrid.ItemsSource = liveList;

            string subnet = TxtLanScanSubnet?.Text?.Trim() ?? "";
            int foundCount = 0;

            var progress = new Progress<(int done, int total)>(p =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (PbLanScan != null) PbLanScan.Value = p.done * 100.0 / p.total;
                    if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Scanning... {p.done}/{p.total}  •  {foundCount} found";
                });
            });

            var deviceFound = new Progress<NetworkScanResult>(r =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    foundCount++;
                    // Insert sorted by last IP octet
                    int newLast = int.TryParse(r.IpAddress.Split('.').LastOrDefault(), out int n) ? n : 999;
                    int insertAt = 0;
                    for (int i = 0; i < liveList.Count; i++)
                    {
                        int existLast = int.TryParse(liveList[i].Ip.Split('.').LastOrDefault(), out int en) ? en : 999;
                        if (newLast < existLast) break;
                        insertAt = i + 1;
                    }
                    liveList.Insert(insertAt, new LanScanItem
                    {
                        Ip          = r.IpAddress,
                        Hostname    = r.Hostname ?? "—",
                        Vendor      = r.Vendor ?? "—",
                        PingMs      = $"{r.PingMs} ms",
                        StatusColor = "#22C55E"
                    });
                    if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Scanning...  •  {foundCount} found";
                });
            });

            try
            {
                await _netScanSvc.ScanSubnetAsync(subnet, 800, progress, deviceFound, _lanScanCts.Token);
                if (TxtLanScanStatus != null)
                    TxtLanScanStatus.Text = _lanScanCts.Token.IsCancellationRequested
                        ? $"Scan stopped.  {foundCount} device(s) found."
                        : $"✓ Done — {foundCount} device(s) online.";
            }
            catch (OperationCanceledException)
            {
                if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Scan cancelled.  {foundCount} device(s) found.";
            }
            catch (Exception ex)
            {
                if (TxtLanScanStatus != null) TxtLanScanStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                // Reset button back to Start state
                if (BtnRunLanScan != null)
                {
                    BtnRunLanScan.Content    = "Scan";
                    BtnRunLanScan.Style = (Style)TryFindResource("GreenButtonStyle");
                    BtnRunLanScan.IsEnabled  = true;
                }
                if (PbLanScan != null) PbLanScan.Visibility = Visibility.Collapsed;
            }
        }

        private void StopLanScan_Click(object s, RoutedEventArgs e) => _lanScanCts?.Cancel();

        // ── WiFi Analyzer ────────────────────────────────────────────────────
        private List<SMDWin.Services.WifiNetwork>? _wifiAllNetworks;
        private bool _wifiShowAll = false;
        private const int WifiInitialLimit = 10;

        private async void ScanWifi_Click(object s, RoutedEventArgs e)
        {
            if (BtnScanWifi != null)
            {
                BtnScanWifi.IsEnabled = false;
                BtnScanWifi.Content = "Scanning…";
            }
            if (TxtWifiStatus != null) TxtWifiStatus.Text = "Scanning WiFi networks...";

            try
            {
                var networks = await _netScanSvc.ScanWifiAsync();
                _wifiAllNetworks = networks;
                _wifiShowAll = false;
                ApplyWifiFilter();
                if (TxtWifiStatus != null)
                    TxtWifiStatus.Text = networks.Count > 0
                        ? $"Found {networks.Count} network(s). Connected network shown first."
                        : "No WiFi networks found. Is WiFi enabled?";
            }
            catch (Exception ex)
            {
                if (TxtWifiStatus != null) TxtWifiStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (BtnScanWifi != null) { BtnScanWifi.IsEnabled = true; BtnScanWifi.Content = "Scan"; }
            }
        }

        private void ApplyWifiFilter()
        {
            if (_wifiAllNetworks == null || WifiGrid == null) return;
            bool hasMore = _wifiAllNetworks.Count > WifiInitialLimit;
            // Show first 10 or all depending on toggle
            WifiGrid.ItemsSource = _wifiShowAll || !hasMore
                ? _wifiAllNetworks
                : _wifiAllNetworks.Take(WifiInitialLimit).ToList();

            if (BtnWifiShowMore != null)
            {
                BtnWifiShowMore.Visibility = hasMore ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                BtnWifiShowMore.Content = _wifiShowAll
                    ? "▲  Show less"
                    : $"▼  Show all {_wifiAllNetworks.Count} networks";
            }
        }

        private void BtnWifiShowMore_Click(object s, RoutedEventArgs e)
        {
            _wifiShowAll = !_wifiShowAll;
            ApplyWifiFilter();
        }

        // ── Traceroute ───────────────────────────────────────────────────────
        private System.Threading.CancellationTokenSource? _tracerouteCts;

        private class TracerouteHop
        {
            public int    Hop        { get; set; }
            public string IpAddress  { get; set; } = "";
            public string Hostname   { get; set; } = "";
            public string RttDisplay { get; set; } = "";
            public System.Windows.Media.Brush RttColor { get; set; } = System.Windows.Media.Brushes.Gray;
        }

        private async void RunTraceroute_Click(object s, RoutedEventArgs e)
        {
            // Toggle: if running, stop
            if (_tracerouteCts != null && !_tracerouteCts.IsCancellationRequested)
            {
                _tracerouteCts.Cancel();
                if (BtnRunTraceroute != null) { BtnRunTraceroute.Content = "Trace"; BtnRunTraceroute.Background = null; }
                return;
            }

            string host = TxtTracerouteHost?.Text.Trim() ?? "8.8.8.8";
            if (string.IsNullOrWhiteSpace(host)) return;

            _tracerouteCts?.Dispose();
            _tracerouteCts = new System.Threading.CancellationTokenSource();
            var token = _tracerouteCts.Token;

            if (BtnRunTraceroute != null)
            {
                BtnRunTraceroute.Content = "■ Stop";
                BtnRunTraceroute.Style = (Style)TryFindResource("RedButtonStyle");
            }
            if (TxtTracerouteStatus != null) TxtTracerouteStatus.Text = $"Tracing route to {host}…";

            var hops = new System.Collections.ObjectModel.ObservableCollection<TracerouteHop>();
            if (TracerouteGrid != null) TracerouteGrid.ItemsSource = hops;

            try
            {
                await Task.Run(async () =>
                {
                    // Pre-resolve hostname to IP to avoid repeated DNS lookups
                    string targetIp = host;
                    try
                    {
                        var addrs = await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                        if (addrs.Length > 0) targetIp = addrs[0].ToString();
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                    using var pinger = new System.Net.NetworkInformation.Ping();
                    // Note: DontFragment=false is more compatible across routers
                    var opts = new System.Net.NetworkInformation.PingOptions { DontFragment = false };

                    for (int ttl = 1; ttl <= 30 && !token.IsCancellationRequested; ttl++)
                    {
                        opts.Ttl = ttl;
                        System.Net.NetworkInformation.PingReply? reply = null;
                        try { reply = pinger.Send(targetIp, 3000, new byte[32], opts); }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }

                        string ip = reply?.Address?.ToString() ?? "*";
                        long rtt = reply?.RoundtripTime ?? -1;
                        string rttStr = rtt >= 0 ? $"{rtt} ms" : "*";
                        System.Windows.Media.Brush rttBrush =
                            rtt < 0   ? System.Windows.Media.Brushes.Gray :
                            rtt < 30  ? System.Windows.Media.Brushes.LimeGreen :
                            rtt < 100 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 166, 35)) :
                                        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));

                        // Reverse DNS (best effort, short timeout)
                        string hostname = ip;
                        if (ip != "*" && ip != "0.0.0.0")
                        {
                            try
                            {
                                using var dnsCts = new System.Threading.CancellationTokenSource(800);
                                var entry = await System.Net.Dns.GetHostEntryAsync(ip).WaitAsync(dnsCts.Token).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != ip)
                                    hostname = entry.HostName;
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                        }

                        var hop = new TracerouteHop { Hop = ttl, IpAddress = ip, Hostname = hostname, RttDisplay = rttStr, RttColor = rttBrush };
                        Dispatcher.Invoke(() => hops.Add(hop));

                        // Success = reached destination; TtlExpired = intermediate hop (continue)
                        bool done = reply?.Status == System.Net.NetworkInformation.IPStatus.Success;
                        if (done)
                        {
                            Dispatcher.Invoke(() => { if (TxtTracerouteStatus != null) TxtTracerouteStatus.Text = $"Route to {host} complete — {ttl} hop(s)."; });
                            break;
                        }
                        // If not TtlExpired and not Success (e.g. DestinationUnreachable after many *), stop early
                        bool shouldContinue = reply == null ||
                            reply.Status == System.Net.NetworkInformation.IPStatus.TtlExpired ||
                            reply.Status == System.Net.NetworkInformation.IPStatus.TimedOut ||
                            ip == "*";
                        if (!shouldContinue) break;
                    }
                }, token);
            }
            catch (OperationCanceledException) { if (TxtTracerouteStatus != null) TxtTracerouteStatus.Text = "Traceroute cancelled."; }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Traceroute failed");
                if (TxtTracerouteStatus != null) TxtTracerouteStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (BtnRunTraceroute != null) { BtnRunTraceroute.Content = "Trace"; BtnRunTraceroute.Background = null; BtnRunTraceroute.IsEnabled = true; }
            }
        }

        private void StopTraceroute_Click(object s, RoutedEventArgs e)
        {
            _tracerouteCts?.Cancel();
        }

        // ── Quick Network Utilities ──────────────────────────────────────────
        private async void FlushDns_Click(object s, RoutedEventArgs e)
        {
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "Flushing DNS cache…";
            try
            {
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true
                    };
                    var p = System.Diagnostics.Process.Start(psi)!;
                    p.WaitForExit(5000);
                });
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "DNS cache flushed successfully.";
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = $"Error: {ex.Message}"; }
        }

        private async void ReleaseRenewIp_Click(object s, RoutedEventArgs e)
        {
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "Releasing IP… (may take a moment)";
            try
            {
                await Task.Run(() =>
                {
                    void Run(string args)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", args)
                        { UseShellExecute = false, CreateNoWindow = true };
                        var p = System.Diagnostics.Process.Start(psi)!;
                        p.WaitForExit(15000);
                    }
                    Run("/release");
                    Run("/renew");
                });
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "IP released and renewed successfully.";
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = $"Error: {ex.Message}"; }
        }

        private void ResetWinsock_Click(object s, RoutedEventArgs e)
        {
            if (!AppDialog.Confirm(
"This will reset the Winsock catalog.\nA system restart is required afterward.\n\nContinue?", "Reset Winsock")) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "winsock reset")
                { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi);
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "Winsock reset initiated. Please restart your computer.";
            }
            catch (Exception ex) { if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = $"Error: {ex.Message}"; }
        }

        private async void TestMtu_Click(object s, RoutedEventArgs e)
        {
            if (TxtMtuResult != null) TxtMtuResult.Text = "Testing MTU… (this may take a few seconds)";
            if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "";
            try
            {
                int optimalMtu = await Task.Run(() =>
                {
                    using var pinger = new System.Net.NetworkInformation.Ping();
                    for (int size = 1472; size >= 576; size -= 8)
                    {
                        var opts = new System.Net.NetworkInformation.PingOptions { DontFragment = true, Ttl = 128 };
                        try
                        {
                            var reply = pinger.Send("8.8.8.8", 2000, new byte[size], opts);
                            if (reply?.Status == System.Net.NetworkInformation.IPStatus.Success)
                                return size + 28; // add IP+ICMP headers
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    }
                    return 576;
                });
                string quality = optimalMtu >= 1500 ? "Excellent (standard)" :
                                 optimalMtu >= 1400 ? "Slightly reduced (VPN/tunnel?)" :
"Low — may affect performance";
                if (TxtMtuResult  != null) TxtMtuResult.Text  = $"Optimal MTU: {optimalMtu} bytes — {quality}";
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = "";
            }
            catch (Exception ex)
            {
                if (TxtMtuResult != null) TxtMtuResult.Text = "Test failed.";
                if (TxtNetUtilStatus != null) TxtNetUtilStatus.Text = $"Error: {ex.Message}";
            }
        }

        // ── Network tool windows ──────────────────────────────────────────────
        private void OpenPingMonitor_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("ping_monitor") { Owner = this };
            win.Show();
        }
        private void OpenTrafficMonitor_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("traffic") { Owner = this };
            win.Show();
        }
        private void OpenPortScanner_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("port_scan") { Owner = this };
            win.Show();
        }
        private void OpenManualPing_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("manual_ping") { Owner = this };
            win.Show();
        }
        private void OpenNetworkScanner_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("net_scan") { Owner = this };
            win.Show();
        }
        private void OpenDnsLookup_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new NetworkToolWindow("dns") { Owner = this };
            win.Show();
        }

        private void StopScan_Click(object sender, RoutedEventArgs e) { /* LAN scan runs in its own window */ }

        private async void LoadApps_Click(object s, RoutedEventArgs e) => await LoadAppsInternalAsync();

        private void SearchApps_Click(object sender, RoutedEventArgs e)
        {
            var q = TxtAppSearch.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q)) { AppsGrid.ItemsSource = _allApps; return; }
            AppsGrid.ItemsSource = _allApps.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void UninstallApp_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) { AppDialog.Show(_L("Select an application.", "Selectați o aplicație.")); return; }
            if (AppDialog.Confirm(_L($"Uninstall '{app.Name}'?", $"Dezinstalați '{app.Name}'?"), _L("Confirm", "Confirmare")))
            {
                try { _appsService.Uninstall(app); }
                catch (Exception ex) { AppDialog.Show(_L($"Error: {ex.Message}", $"Eroare: {ex.Message}")); }
            }
        }

        private void OpenAddRemovePrograms_Click(object s, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("appwiz.cpl") { UseShellExecute = true });
        }

        // ── SERVICES ──────────────────────────────────────────────────────────

        private async Task LoadKeyServicesAsync()
        {
            ShowLoading(_L("Reading services...", "Se citesc serviciile..."));
            try { ServicesGrid.ItemsSource = await _svcService.GetServicesAsync(onlyKnown: true); }
            finally { HideLoading(); }
        }

        private async void LoadKeyServices_Click(object s, RoutedEventArgs e) => await LoadKeyServicesAsync();

        /// <summary>5.3 — One-click: disables all pre-selected low-risk services.</summary>
        private async void ApplySafeServiceOptimizations_Click(object sender, RoutedEventArgs e)
        {
            var all = ServicesGrid.ItemsSource as System.Collections.IEnumerable;
            if (all == null) { AppDialog.Show("Load services first.", "SMDWin"); return; }

            var safeList = all.OfType<SMDWin.Models.WinServiceEntry>()
                             .Where(svc => svc.SafeToDisable && svc.StartType != "Disabled")
                             .ToList();

            if (safeList.Count == 0)
            {
                AppDialog.Show("All safe-to-disable services are already disabled.", "Safe Optimizations", AppDialog.Kind.Success);
                return;
            }

            string names = string.Join("\n  • ", safeList.Select(svc => svc.DisplayName));
            if (!AppDialog.Confirm(
                $"Disable {safeList.Count} low-risk service(s)?\n\n  • {names}\n\nThese are safe to disable and won't affect system stability.",
                "Apply Safe Optimizations")) return;

            int done = 0;
            foreach (var svc in safeList)
            {
                bool ok = await _svcService.SetServiceStartTypeAsync(svc.Name, "Disabled");
                if (ok) done++;
            }

            AppDialog.Show(
                $"✓ Disabled {done}/{safeList.Count} services.\nFull effect after next reboot.",
                "Safe Optimizations Applied", AppDialog.Kind.Success);
            await LoadKeyServicesAsync();
        }

        /// <summary>5.4 — One-click toggle for startup entries (used from grid buttons).</summary>
        private async void QuickToggleStartup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not StartupEntry entry) return;
            bool enable = !entry.IsEnabled;
            bool ok = _startupSvc.SetEnabled(entry, enable);
            if (ok) await LoadStartupAsync();
            else AppDialog.Show(_L("Could not modify. May require Administrator rights.", "Nu s-a putut modifica."), "SMDWin");
        }

    }

    // ── LAN Scanner live-binding model ───────────────────────────────────────
    public class LanScanItem
    {
        public string Ip          { get; set; } = "";
        public string Hostname    { get; set; } = "—";
        public string Vendor      { get; set; } = "—";
        public string PingMs      { get; set; } = "—";
        public string StatusColor { get; set; } = "#22C55E";
    }
}
