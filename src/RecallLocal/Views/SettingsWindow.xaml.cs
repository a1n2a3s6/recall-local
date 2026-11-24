using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using RecallLocal.Engine;
using RecallLocal.Models;

namespace RecallLocal.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly VaultStore _vaultStore;
        private readonly CaptureService _captureService;
        private readonly OcrService _ocrService;

        public SettingsWindow(
            AppSettings settings,
            VaultStore vaultStore,
            CaptureService captureService,
            OcrService ocrService)
        {
            InitializeComponent();

            _settings = settings;
            _vaultStore = vaultStore;
            _captureService = captureService;
            _ocrService = ocrService;

            LoadSettingsIntoUi();
            RefreshStorageMetrics();
        }

        private void LoadSettingsIntoUi()
        {
            IntervalSlider.Value = _settings.CaptureIntervalSeconds;
            IntervalValueText.Text = $"{_settings.CaptureIntervalSeconds}s";

            RetentionSlider.Value = _settings.RetentionDays;
            RetentionValueText.Text = _settings.RetentionDays == 1 ? "1 day" : $"{_settings.RetentionDays} days";

            AutoSkipPrivateCheckBox.IsChecked = _settings.AutoSkipPrivateBrowsing;

            BlockedAppsTextBox.Text = string.Join(Environment.NewLine, _settings.BlockedApps);
            OcrPackText.Text = _ocrService.IsAvailable
                ? $"OCR: {_ocrService.ActiveLanguageTag}"
                : "OCR: No Windows Pack";
        }

        private void RefreshStorageMetrics()
        {
            VaultStats stats = _vaultStore.GetStats();
            StorageSummaryText.Text = $"{stats.DiskUsageMb:0.##} MB used • {stats.TotalSnapshots} snapshots";
            IndexedWordsText.Text = $"Indexed Words: {stats.IndexedWords:N0}";
            UniqueAppsText.Text = $"Unique Apps: {stats.UniqueApps}";

            // Visual meter scaled against a 500 MB soft reference bar.
            double ratio = Math.Clamp(stats.DiskUsageMb / 500.0, 0.03, 1.0);
            StorageMeterFill.Width = Math.Round(560.0 * ratio);
        }

        private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IntervalValueText != null)
            {
                IntervalValueText.Text = $"{(int)Math.Round(e.NewValue)}s";
            }
        }

        private void RetentionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RetentionValueText != null)
            {
                int days = (int)Math.Round(e.NewValue);
                RetentionValueText.Text = days == 1 ? "1 day" : $"{days} days";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.CaptureIntervalSeconds = (int)Math.Round(IntervalSlider.Value);
            _settings.RetentionDays = (int)Math.Round(RetentionSlider.Value);
            _settings.AutoSkipPrivateBrowsing = AutoSkipPrivateCheckBox.IsChecked == true;

            _settings.BlockedApps = BlockedAppsTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _settings.Save();
            _vaultStore.PruneOlderThanDays(_settings.RetentionDays);
            RefreshStorageMetrics();

            SavedStatusText.Text = "Settings saved locally.";
        }

        private void OpenVaultFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.VaultRootDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppSettings.VaultRootDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open vault folder:\n{ex.Message}", "RecallLocal", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void NukeEntireVaultButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "Permanently delete ALL captured screenshots and the entire local OCR search index?\n\nThis action cannot be undone.",
                "Nuke Entire RecallLocal Vault",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            int removed = _captureService.NukeAll();
            RefreshStorageMetrics();
            SavedStatusText.Text = $"Vault nuked ({removed} snapshot(s) permanently deleted).";
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }
    }
}
