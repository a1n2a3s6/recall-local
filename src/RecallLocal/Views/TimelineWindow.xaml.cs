using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RecallLocal.Engine;
using RecallLocal.Models;

namespace RecallLocal.Views
{
    public partial class TimelineWindow : Window
    {
        private readonly VaultStore _vaultStore;
        private readonly CaptureService _captureService;
        private readonly AppSettings _settings;
        private readonly Action _openSettingsAction;

        private readonly DispatcherTimer _toastTimer;
        private List<SnapshotRecord> _filteredRecords = new List<SnapshotRecord>();
        private SnapshotRecord? _currentRecord;
        private BitmapImage? _currentBitmap;

        private string _selectedAppFilter = "All";
        private DateTime? _selectedDateFilter;
        private bool _suppressSliderEvent;

        public TimelineWindow(
            VaultStore vaultStore,
            CaptureService captureService,
            AppSettings settings,
            Action openSettingsAction)
        {
            InitializeComponent();

            _vaultStore = vaultStore;
            _captureService = captureService;
            _settings = settings;
            _openSettingsAction = openSettingsAction;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _toastTimer.Tick += (_, _) =>
            {
                ToastBanner.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };

            _vaultStore.VaultChanged += OnVaultChanged;
            _captureService.StatusChanged += OnStatusChanged;
        }

        public void RefreshAndShow()
        {
            UpdatePauseButtonState();
            RebuildAppFilterPills();
            UpdateDateFilterButtonStyles();
            RunSearch(jumpToLatest: true);

            if (!IsVisible)
            {
                Show();
            }

            WindowState = WindowState.Normal;
            Activate();
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }

        private void OnVaultChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsVisible) return;
                RebuildAppFilterPills();
                RunSearch(jumpToLatest: false);
            }));
        }

        private void OnStatusChanged(string _)
        {
            Dispatcher.BeginInvoke(new Action(UpdatePauseButtonState));
        }

        private void UpdatePauseButtonState()
        {
            bool paused = _captureService.IsPaused;
            PauseToggleButton.Content = paused ? "▶ Resume" : "⏸ Pause";
            StatusDot.Fill = paused
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            StatusLabel.Text = paused ? "Paused" : "Privacy Shield Active";
            StatusLabel.Foreground = StatusDot.Fill;
        }

        private void RebuildAppFilterPills()
        {
            AppFilterPillsPanel.Children.Clear();

            var apps = new List<string> { "All" };
            apps.AddRange(_vaultStore.GetTopApps(10));

            if (!_selectedAppFilter.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                !apps.Any(a => a.Equals(_selectedAppFilter, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedAppFilter = "All";
            }

            foreach (string app in apps)
            {
                bool isSelected = app.Equals(_selectedAppFilter, StringComparison.OrdinalIgnoreCase);
                var pill = new Button
                {
                    Content = app,
                    Tag = app,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(11, 4, 11, 4),
                    FontSize = 11.5,
                    Style = (Style)Application.Current.FindResource(isSelected ? "CyanButtonStyle" : "ObsidianButtonStyle")
                };
                pill.Click += AppPill_Click;
                AppFilterPillsPanel.Children.Add(pill);
            }
        }

        private void AppPill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string appName)
            {
                _selectedAppFilter = appName;
                RebuildAppFilterPills();
                RunSearch(jumpToLatest: true);
            }
        }

        private void DateFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _selectedDateFilter = tag switch
                {
                    "TODAY" => DateTime.Now.Date,
                    "YESTERDAY" => DateTime.Now.Date.AddDays(-1),
                    _ => null
                };
                UpdateDateFilterButtonStyles();
                RunSearch(jumpToLatest: true);
            }
        }

        private void UpdateDateFilterButtonStyles()
        {
            var activeStyle = (Style)Application.Current.FindResource("CyanButtonStyle");
            var normalStyle = (Style)Application.Current.FindResource("ObsidianButtonStyle");

            DateAllButton.Style = _selectedDateFilter == null ? activeStyle : normalStyle;
            DateTodayButton.Style = _selectedDateFilter == DateTime.Now.Date ? activeStyle : normalStyle;
            DateYesterdayButton.Style = _selectedDateFilter == DateTime.Now.Date.AddDays(-1) ? activeStyle : normalStyle;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            RunSearch(jumpToLatest: true);
        }

        private void RunSearch(bool jumpToLatest)
        {
            string query = SearchTextBox.Text?.Trim() ?? string.Empty;
            string? previousId = _currentRecord?.Id;

            _filteredRecords = _vaultStore.Search(query, _selectedAppFilter, _selectedDateFilter);
            MatchCounterText.Text = _filteredRecords.Count == 1
                ? "1 moment found"
                : $"{_filteredRecords.Count} moments found";

            _suppressSliderEvent = true;
            try
            {
                if (_filteredRecords.Count == 0)
                {
                    TimelineSlider.Minimum = 0;
                    TimelineSlider.Maximum = 0;
                    TimelineSlider.Value = 0;
                    DisplayRecord(null, -1);
                    RebuildTimelineTicks(-1);
                    return;
                }

                TimelineSlider.Minimum = 0;
                TimelineSlider.Maximum = _filteredRecords.Count - 1;

                int targetIndex = _filteredRecords.Count - 1;
                if (!jumpToLatest && previousId != null)
                {
                    int existingIdx = _filteredRecords.FindIndex(r => r.Id == previousId);
                    if (existingIdx >= 0)
                    {
                        targetIndex = existingIdx;
                    }
                }

                TimelineSlider.Value = targetIndex;
                DisplayRecord(_filteredRecords[targetIndex], targetIndex);
                RebuildTimelineTicks(targetIndex);
            }
            finally
            {
                _suppressSliderEvent = false;
            }
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent || _filteredRecords.Count == 0) return;

            int idx = Math.Clamp((int)Math.Round(e.NewValue), 0, _filteredRecords.Count - 1);
            DisplayRecord(_filteredRecords[idx], idx);
            HighlightActiveTick(idx);
        }

        private void DisplayRecord(SnapshotRecord? record, int index)
        {
            _currentRecord = record;
            HighlightCanvas.Children.Clear();

            if (record == null)
            {
                _currentBitmap = null;
                ScreenshotImage.Source = null;
                EmptyStatePanel.Visibility = Visibility.Visible;
                FrameTimeText.Text = "--:--:-- --";
                FrameDateText.Text = "---";
                FrameAppText.Text = "No Frame";
                FrameTitleText.Text = "No matching snapshots";
                FrameIndexText.Text = "0 / 0";
                return;
            }

            EmptyStatePanel.Visibility = Visibility.Collapsed;
            FrameTimeText.Text = record.DisplayTime;
            FrameDateText.Text = record.DisplayDate;
            FrameAppText.Text = string.IsNullOrWhiteSpace(record.AppName) ? "Unknown" : record.AppName;
            FrameTitleText.Text = record.WindowTitle;
            FrameIndexText.Text = $"{index + 1} / {_filteredRecords.Count}";

            _currentBitmap = LoadBitmapUnlocked(record.ImagePath);
            ScreenshotImage.Source = _currentBitmap;

            RenderOcrHighlights();
        }

        private static BitmapImage? LoadBitmapUnlocked(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void ViewportHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RenderOcrHighlights();
        }

        private void RenderOcrHighlights()
        {
            HighlightCanvas.Children.Clear();

            if (_currentRecord == null || _currentBitmap == null || _currentRecord.OcrWords == null || _currentRecord.OcrWords.Count == 0)
            {
                return;
            }

            double hostW = ViewportHost.ActualWidth;
            double hostH = ViewportHost.ActualHeight;
            if (hostW <= 0 || hostH <= 0 || _currentBitmap.PixelWidth <= 0 || _currentBitmap.PixelHeight <= 0)
            {
                return;
            }

            double scale = Math.Min(hostW / _currentBitmap.PixelWidth, hostH / _currentBitmap.PixelHeight);
            double renderW = _currentBitmap.PixelWidth * scale;
            double renderH = _currentBitmap.PixelHeight * scale;
            double offsetX = (hostW - renderW) / 2.0;
            double offsetY = (hostH - renderH) / 2.0;

            string query = SearchTextBox.Text?.Trim() ?? string.Empty;
            string[] terms = query.Length > 0
                ? query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(VaultStore.NormalizeArabicAndCase)
                       .Where(t => t.Length > 0)
                       .ToArray()
                : Array.Empty<string>();

            var amberBg = new SolidColorBrush(Color.FromArgb(95, 245, 158, 11));     // #5FF59E0B
            var amberBorder = new SolidColorBrush(Color.FromArgb(245, 245, 158, 11)); // #F59E0B
            var hoverBg = new SolidColorBrush(Color.FromArgb(40, 56, 189, 248));      // subtle cyan on hover

            foreach (OcrWordBox word in _currentRecord.OcrWords)
            {
                bool isMatch = false;
                if (terms.Length > 0)
                {
                    string normalizedWord = VaultStore.NormalizeArabicAndCase(word.Text);
                    foreach (string term in terms)
                    {
                        if (normalizedWord.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isMatch = true;
                            break;
                        }
                    }
                }

                // Always draw glowing amber boxes for search matches; also allow clicking any OCR word when no search is active.
                if (!isMatch && terms.Length > 0)
                {
                    continue;
                }

                double left = offsetX + (word.X * renderW);
                double top = offsetY + (word.Y * renderH);
                double width = Math.Max(8, word.W * renderW);
                double height = Math.Max(12, word.H * renderH);

                var box = new Border
                {
                    Width = width + 4,
                    Height = height + 4,
                    CornerRadius = new CornerRadius(3),
                    Background = isMatch ? amberBg : Brushes.Transparent,
                    BorderBrush = isMatch ? amberBorder : Brushes.Transparent,
                    BorderThickness = isMatch ? new Thickness(1.6) : new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Click to copy: \"{word.Text}\""
                };

                if (!isMatch)
                {
                    box.MouseEnter += (_, _) =>
                    {
                        box.Background = hoverBg;
                        box.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 56, 189, 248));
                        box.BorderThickness = new Thickness(1);
                    };
                    box.MouseLeave += (_, _) =>
                    {
                        box.Background = Brushes.Transparent;
                        box.BorderBrush = Brushes.Transparent;
                        box.BorderThickness = new Thickness(0);
                    };
                }

                string capturedWord = word.Text;
                box.MouseLeftButtonDown += (_, args) =>
                {
                    args.Handled = true;
                    CopyTextWithToast(capturedWord, $"Copied \"{capturedWord}\" to clipboard");
                };

                Canvas.SetLeft(box, left - 2);
                Canvas.SetTop(box, top - 2);
                HighlightCanvas.Children.Add(box);
            }
        }

        private void RebuildTimelineTicks(int activeIndex)
        {
            TimelineTicksPanel.Children.Clear();
            if (_filteredRecords.Count == 0) return;

            // Show up to 24 evenly distributed clickable timestamp ticks across the filtered timeline.
            int count = _filteredRecords.Count;
            int maxTicks = Math.Min(count, 24);
            var indices = new SortedSet<int>();

            if (count <= maxTicks)
            {
                for (int i = 0; i < count; i++) indices.Add(i);
            }
            else
            {
                for (int step = 0; step < maxTicks; step++)
                {
                    int mapped = (int)Math.Round(step * (count - 1.0) / (maxTicks - 1.0));
                    indices.Add(mapped);
                }
            }

            foreach (int idx in indices)
            {
                SnapshotRecord rec = _filteredRecords[idx];
                bool isActive = idx == activeIndex;

                var tickButton = new Button
                {
                    Tag = idx,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(8, 4, 8, 4),
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#1D2B3A" : "#090B0E")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#38BDF8" : "#232A36")),
                    BorderThickness = new Thickness(1),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = rec.LocalTime.ToString("hh:mm tt"),
                                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#38BDF8" : "#F1F5F9")),
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 10.5,
                                FontWeight = FontWeights.SemiBold
                            },
                            new TextBlock
                            {
                                Text = Truncate(rec.AppName, 12),
                                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
                                FontSize = 10
                            }
                        }
                    }
                };

                tickButton.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is int targetIdx)
                    {
                        TimelineSlider.Value = targetIdx;
                    }
                };

                TimelineTicksPanel.Children.Add(tickButton);
            }
        }

        private void HighlightActiveTick(int activeIndex)
        {
            foreach (UIElement child in TimelineTicksPanel.Children)
            {
                if (child is Button btn && btn.Tag is int idx)
                {
                    bool isActive = idx == activeIndex;
                    btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#1D2B3A" : "#090B0E"));
                    btn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#38BDF8" : "#232A36"));
                }
            }
        }

        private void CopyAllTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRecord == null || string.IsNullOrWhiteSpace(_currentRecord.FullText))
            {
                ShowToast("No OCR text detected on this frame", isWarning: true);
                return;
            }

            CopyTextWithToast(_currentRecord.FullText, $"Copied {_currentRecord.OcrWords.Count} OCR words to clipboard");
        }

        private void CopyTextWithToast(string text, string toastMessage)
        {
            try
            {
                Clipboard.SetText(text);
                ShowToast(toastMessage, isWarning: false);
            }
            catch
            {
                ShowToast("Clipboard temporarily unavailable", isWarning: true);
            }
        }

        private void ShowToast(string message, bool isWarning)
        {
            ToastText.Text = message;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isWarning ? "#F59E0B" : "#10B981"));
            ToastText.Foreground = brush;
            ToastBanner.BorderBrush = brush;
            ToastBanner.Visibility = Visibility.Visible;

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void Nuke15Button_Click(object sender, RoutedEventArgs e)
        {
            int removed = _captureService.NukeLastMinutes(15);
            ShowToast($"Nuked {removed} snapshot(s) from the last 15 minutes", isWarning: true);
            RunSearch(jumpToLatest: true);
        }

        private void PauseToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _captureService.TogglePause();
            UpdatePauseButtonState();
            ShowToast(_captureService.IsPaused ? "Capture paused" : "Capture resumed", isWarning: _captureService.IsPaused);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _openSettingsAction();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not TextBox)
            {
                DragMove();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
                return;
            }

            bool searchFocusedWithText = SearchTextBox.IsKeyboardFocused && !string.IsNullOrEmpty(SearchTextBox.Text);
            if (!searchFocusedWithText || (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.Left && _filteredRecords.Count > 0)
                {
                    e.Handled = true;
                    TimelineSlider.Value = Math.Max(0, TimelineSlider.Value - 1);
                }
                else if (e.Key == Key.Right && _filteredRecords.Count > 0)
                {
                    e.Handled = true;
                    TimelineSlider.Value = Math.Min(_filteredRecords.Count - 1, TimelineSlider.Value + 1);
                }
            }
        }

        private static string Truncate(string? value, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            return value.Length <= maxLen ? value : value.Substring(0, maxLen - 1) + "…";
        }
    }
}
