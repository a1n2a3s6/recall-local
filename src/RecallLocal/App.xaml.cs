using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using RecallLocal.Engine;
using RecallLocal.Models;
using RecallLocal.Native;
using RecallLocal.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RecallLocal
{
    public partial class App : Application
    {
        private const int HotkeyId = 0x524C; // "RL"
        private const string MutexName = "RecallLocal_SingleInstance_Mutex_v1";

        private Mutex? _singleInstanceMutex;
        private HwndSource? _hotkeyHwndSource;
        private Forms.NotifyIcon? _trayIcon;
        private Forms.ToolStripMenuItem? _pauseMenuItem;
        private Forms.ToolStripMenuItem? _statusMenuItem;

        private AppSettings _settings = null!;
        private PrivacyGuard _privacyGuard = null!;
        private OcrService _ocrService = null!;
        private VaultStore _vaultStore = null!;
        private CaptureService _captureService = null!;

        private TimelineWindow? _timelineWindow;
        private SettingsWindow? _settingsWindow;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "RecallLocal is already running in your system tray.\nPress Ctrl + Shift + R to open the Time-Machine.",
                    "RecallLocal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _settings = AppSettings.Load();
            _privacyGuard = new PrivacyGuard(_settings);
            _ocrService = new OcrService();
            _vaultStore = new VaultStore();
            _captureService = new CaptureService(_settings, _privacyGuard, _ocrService, _vaultStore);
            _captureService.StatusChanged += OnCaptureStatusChanged;

            InitializeTrayIcon();
            RegisterGlobalHotkey();

            _captureService.Start();

            // Capture an initial snapshot shortly after startup so the timeline is never empty.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200).ConfigureAwait(false);
                if (!_captureService.IsPaused)
                {
                    await _captureService.CaptureNowAsync(forceSave: false).ConfigureAwait(false);
                }
            });
        }

        private void InitializeTrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();

            _statusMenuItem = new Forms.ToolStripMenuItem("RecallLocal — Watching")
            {
                Enabled = false
            };
            menu.Items.Add(_statusMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var openItem = new Forms.ToolStripMenuItem("Open Time-Machine (Ctrl+Shift+R)");
            openItem.Click += (_, _) => Dispatcher.Invoke(ShowTimelineWindow);
            menu.Items.Add(openItem);

            var captureNowItem = new Forms.ToolStripMenuItem("Capture Snapshot Now");
            captureNowItem.Click += async (_, _) =>
            {
                var snap = await _captureService.CaptureNowAsync(forceSave: true);
                if (snap != null)
                {
                    ShowTrayBalloon("Snapshot Captured", $"{snap.AppName} — {snap.WindowTitle}");
                }
            };
            menu.Items.Add(captureNowItem);

            _pauseMenuItem = new Forms.ToolStripMenuItem(_captureService.IsPaused ? "Resume Capture" : "Pause Capture");
            _pauseMenuItem.Click += (_, _) => Dispatcher.Invoke(TogglePauseCapture);
            menu.Items.Add(_pauseMenuItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var nuke15Item = new Forms.ToolStripMenuItem("🔥 Nuke Last 15 Minutes");
            nuke15Item.Click += (_, _) =>
            {
                int removed = _captureService.NukeLastMinutes(15);
                ShowTrayBalloon("Privacy Nuke Complete", $"Deleted {removed} snapshot(s) from the last 15 minutes.");
            };
            menu.Items.Add(nuke15Item);

            var settingsItem = new Forms.ToolStripMenuItem("Settings");
            settingsItem.Click += (_, _) => Dispatcher.Invoke(ShowSettingsWindow);
            menu.Items.Add(settingsItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = new Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => Dispatcher.Invoke(Shutdown);
            menu.Items.Add(exitItem);

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = CreateObsidianTrayIcon(isPaused: _captureService.IsPaused),
                Text = _captureService.IsPaused ? "RecallLocal — Paused" : "RecallLocal — Watching",
                ContextMenuStrip = menu,
                Visible = true
            };

            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleTimelineWindow);
            ShowTrayBalloon("RecallLocal Active", "100% offline photographic memory. Press Ctrl + Shift + R anytime.");
        }

        private void RegisterGlobalHotkey()
        {
            var parameters = new HwndSourceParameters("RecallLocalHotkeyHost")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };

            _hotkeyHwndSource = new HwndSource(parameters);
            _hotkeyHwndSource.AddHook(HotkeyWndProc);

            bool ok = Win32Interop.RegisterHotKey(
                _hotkeyHwndSource.Handle,
                HotkeyId,
                Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT,
                Win32Interop.VK_R);

            if (!ok)
            {
                ShowTrayBalloon("Hotkey Notice", "Could not register Ctrl+Shift+R (already in use). Double-click tray icon to open.");
            }
        }

        private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32Interop.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Dispatcher.Invoke(ToggleTimelineWindow);
            }
            return IntPtr.Zero;
        }

        public void ToggleTimelineWindow()
        {
            if (_timelineWindow != null && _timelineWindow.IsVisible)
            {
                _timelineWindow.Hide();
                return;
            }

            ShowTimelineWindow();
        }

        public void ShowTimelineWindow()
        {
            if (_timelineWindow == null)
            {
                _timelineWindow = new TimelineWindow(_vaultStore, _captureService, _settings, ShowSettingsWindow);
            }

            _timelineWindow.RefreshAndShow();
        }

        public void ShowSettingsWindow()
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings, _vaultStore, _captureService, _ocrService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void TogglePauseCapture()
        {
            _captureService.TogglePause();
            UpdateTrayVisuals();
        }

        private void OnCaptureStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateTrayVisuals(status);
            }));
        }

        private void UpdateTrayVisuals(string? detail = null)
        {
            if (_trayIcon == null) return;

            bool paused = _captureService.IsPaused;
            string headline = paused ? "RecallLocal — Paused" : "RecallLocal — Watching";

            // NotifyIcon.Text is limited to 63 characters on Windows.
            string tooltip = string.IsNullOrWhiteSpace(detail) ? headline : $"RecallLocal — {detail}";
            if (tooltip.Length > 63)
            {
                tooltip = tooltip.Substring(0, 60) + "...";
            }

            _trayIcon.Text = tooltip;

            if (_statusMenuItem != null)
            {
                _statusMenuItem.Text = headline;
            }

            if (_pauseMenuItem != null)
            {
                _pauseMenuItem.Text = paused ? "Resume Capture" : "Pause Capture";
            }

            Drawing.Icon? oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = CreateObsidianTrayIcon(paused);
            oldIcon?.Dispose();
        }

        private void ShowTrayBalloon(string title, string message)
        {
            try
            {
                _trayIcon?.ShowBalloonTip(2500, title, message, Forms.ToolTipIcon.None);
            }
            catch
            {
                // Ignore balloon errors if OS notifications are muted.
            }
        }

        private static Drawing.Icon CreateObsidianTrayIcon(bool isPaused)
        {
            using var bmp = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);

                // Obsidian circular badge
                using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 18, 22, 30));
                using var borderPen = new Drawing.Pen(
                    isPaused ? Drawing.Color.FromArgb(255, 245, 158, 11) : Drawing.Color.FromArgb(255, 56, 189, 248),
                    2.5f);
                g.FillEllipse(bgBrush, 2, 2, 28, 28);
                g.DrawEllipse(borderPen, 2.5f, 2.5f, 27, 27);

                // Inner time-machine lens / pause glyph
                if (isPaused)
                {
                    using var pauseBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 245, 158, 11));
                    g.FillRectangle(pauseBrush, 11, 10, 3.5f, 12);
                    g.FillRectangle(pauseBrush, 17.5f, 10, 3.5f, 12);
                }
                else
                {
                    using var ringPen = new Drawing.Pen(Drawing.Color.FromArgb(255, 16, 185, 129), 2f);
                    g.DrawArc(ringPen, 9, 9, 14, 14, 30, 290);
                    using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 56, 189, 248));
                    g.FillEllipse(dotBrush, 13.5f, 13.5f, 5, 5);
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Drawing.Icon.FromHandle(hIcon);
                return (Drawing.Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_hotkeyHwndSource != null)
                {
                    Win32Interop.UnregisterHotKey(_hotkeyHwndSource.Handle, HotkeyId);
                    _hotkeyHwndSource.RemoveHook(HotkeyWndProc);
                    _hotkeyHwndSource.Dispose();
                }
            }
            catch { }

            _captureService?.Dispose();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();

            base.OnExit(e);
        }
    }
}
