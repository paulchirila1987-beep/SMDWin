using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Application    = System.Windows.Application;
using Button         = System.Windows.Controls.Button;
using Color          = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    /// <summary>
    /// Themed replacement for MessageBox.Show().
    /// Usage:
    ///   AppDialog.Show("Message");
    ///   bool yes = AppDialog.Confirm("Are you sure?");
    ///   AppDialog.Show("msg", "Title", AppDialog.Kind.Warning);
    /// </summary>
    public partial class AppDialog : Window
    {
        public enum Kind { Info, Warning, Error, Success }
        public enum Buttons { Ok, OkCancel, YesNo }

        private bool _result = false;

        public AppDialog()
        {
            InitializeComponent();
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(hwnd, SMDWin.Services.SettingsService.Current.ThemeName);
                    {
                        string _resolved = SMDWin.Services.ThemeManager.Normalize(SMDWin.Services.SettingsService.Current.ThemeName);
                        if (SMDWin.Services.ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                            SMDWin.Services.ThemeManager.SetCaptionColor(hwnd, _t["BgDark"]);
                    }
                }
                catch { }
            };
        }

        // ──────────────────────────────────────────────────────────────
        //  Static helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>Show a simple informational dialog and wait for OK.</summary>
        public static void Show(string message, string title = "SMDWin", Kind kind = Kind.Info, Window? owner = null)
        {
            ShowCore(message, title, kind, Buttons.Ok, owner);
        }

        /// <summary>Show a Yes/No dialog. Returns true if Yes was clicked.</summary>
        public static bool Confirm(string message, string title = "SMDWin", Window? owner = null)
        {
            return ShowCore(message, title, Kind.Warning, Buttons.YesNo, owner);
        }

        /// <summary>Show an Ok/Cancel dialog. Returns true if Ok was clicked.</summary>
        public static bool Ask(string message, string title = "SMDWin", Window? owner = null)
        {
            return ShowCore(message, title, Kind.Info, Buttons.OkCancel, owner);
        }

        // ──────────────────────────────────────────────────────────────
        //  Core builder
        // ──────────────────────────────────────────────────────────────

        private static bool ShowCore(string message, string title, Kind kind, Buttons buttons, Window? owner)
        {
            var dlg = new AppDialog();
            
            // Copy ALL live theme resources (including runtime keys set by ThemeManager.Apply)
            // MergedDictionaries alone misses runtime-set keys like BgCardColor, AccentBrush etc.
            if (Application.Current?.Resources != null)
            {
                foreach (var key in Application.Current.Resources.Keys)
                {
                    try { dlg.Resources[key] = Application.Current.Resources[key]; } catch { }
                }
                foreach (var rd in Application.Current.Resources.MergedDictionaries)
                    if (!dlg.Resources.MergedDictionaries.Contains(rd))
                        dlg.Resources.MergedDictionaries.Add(rd);
            }

            dlg.TxtTitle.Text   = title;
            dlg.TxtMessage.Text = message;
            dlg.SetIcon(kind);
            dlg.BuildButtons(buttons, kind);

            if (owner != null)
                dlg.Owner = owner;
            else if (Application.Current?.MainWindow is Window mw && mw.IsLoaded)
                dlg.Owner = mw;

            dlg.ShowDialog();
            return dlg._result;
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon setup
        // ──────────────────────────────────────────────────────────────

        private void SetIcon(Kind kind)
        {
            // Material Design path data for each icon type
            (string path, string colorHex) = kind switch
            {
                Kind.Warning => ("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z",     "#F59E0B"),
                Kind.Error   => ("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z", "#EF4444"),
                Kind.Success => ("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z",       "#22C55E"),
                _            => ("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z", "#3B82F6"),
            };

            IconPath.Data = Geometry.Parse(path);
            IconPath.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        // ──────────────────────────────────────────────────────────────
        //  Button builder
        // ──────────────────────────────────────────────────────────────

        private void BuildButtons(Buttons kind, Kind dlgKind)
        {
            ButtonPanel.Children.Clear();

            switch (kind)
            {
                case Buttons.Ok:
                    AddButton("OK", isPrimary: true, result: true);
                    break;

                case Buttons.OkCancel:
                    AddButton("Cancel", isPrimary: false, result: false);
                    AddButton("OK",     isPrimary: true,  result: true);
                    break;

                case Buttons.YesNo:
                    AddButton("No",  isPrimary: false, result: false);
                    AddButton("Yes", isPrimary: true,  result: true);
                    break;
            }
        }

        private void AddButton(string label, bool isPrimary, bool result)
        {
            var btn = new Button
            {
                Content = label,
                Style   = (Style)Resources[isPrimary ? "DlgPrimaryBtnStyle" : "DlgBtnStyle"],
                Margin  = new Thickness(8, 0, 0, 0)
            };
            btn.Click += (_, _) => { _result = result; Close(); };
            ButtonPanel.Children.Add(btn);
        }

        // ──────────────────────────────────────────────────────────────
        //  Window chrome
        // ──────────────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _result = false;
            Close();
        }
    }
}
