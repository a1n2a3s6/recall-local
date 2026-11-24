using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RecallLocal.Models;
using RecallLocal.Native;

namespace RecallLocal.Engine
{
    public sealed class CaptureService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly PrivacyGuard _privacyGuard;
        private readonly OcrService _ocrService;
        private readonly VaultStore _vaultStore;

        private readonly SemaphoreSlim _captureGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private ulong? _lastSavedHash;
        private DateTime _lastRetentionCheckUtc = DateTime.MinValue;
        private bool _isPaused;
        private bool _disposed;

        public bool IsPaused => _isPaused;
        public string LastStatusMessage { get; private set; } = "Watching";

        public event Action<string>? StatusChanged;
        public event Action<SnapshotRecord>? SnapshotCaptured;

        public CaptureService(
            AppSettings settings,
            PrivacyGuard privacyGuard,
            OcrService ocrService,
            VaultStore vaultStore)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _privacyGuard = privacyGuard ?? throw new ArgumentNullException(nameof(privacyGuard));
            _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
            _vaultStore = vaultStore ?? throw new ArgumentNullException(nameof(vaultStore));
            _isPaused = settings.StartPaused;
        }

        public void Start()
        {
            if (_loopTask != null) return;

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
            UpdateStatus(_isPaused ? "Paused" : "Watching");
        }

        public void SetPaused(bool paused)
        {
            _isPaused = paused;
            UpdateStatus(_isPaused ? "Paused" : "Watching");
        }

        public void TogglePause()
        {
            SetPaused(!_isPaused);
        }

        public int NukeLastMinutes(int minutes)
        {
            _lastSavedHash = null;
            int removed = _vaultStore.NukeLastMinutes(minutes);
            UpdateStatus($"Nuked {removed} snapshot(s) from last {minutes}m");
            return removed;
        }

        public int NukeAll()
        {
            _lastSavedHash = null;
            int removed = _vaultStore.NukeAll();
            UpdateStatus($"Vault cleared ({removed} snapshot(s))");
            return removed;
        }

        public async Task<SnapshotRecord?> CaptureNowAsync(bool forceSave = false)
        {
            if (_disposed) return null;

            if (!await _captureGate.WaitAsync(0).ConfigureAwait(false))
            {
                return null;
            }

            try
            {
                ActiveWindowInfo activeWindow = Win32Interop.GetActiveWindowInfo();
                if (!forceSave && _privacyGuard.ShouldSkipCapture(activeWindow, out string skipReason))
                {
                    UpdateStatus($"Privacy Shield: {skipReason}");
                    return null;
                }

                Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                using var rawBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(rawBitmap))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                ulong currentHash = ComputePerceptualHash8x8(rawBitmap);
                if (!forceSave && _lastSavedHash.HasValue)
                {
                    int bitDiff = BitOperations.PopCount(currentHash ^ _lastSavedHash.Value);
                    if (bitDiff < _settings.MinHashBitDiff)
                    {
                        UpdateStatus("Watching (unchanged)");
                        return null;
                    }
                }

                using Bitmap scaledBitmap = ScaleDownIfNeeded(rawBitmap, _settings.MaxImageWidth);
                DateTime nowUtc = DateTime.UtcNow;
                string imagePath = SaveCompressedJpeg(scaledBitmap, nowUtc, _settings.JpegQuality);

                OcrExtractionResult ocr = await _ocrService.RecognizeAsync(scaledBitmap).ConfigureAwait(false);

                var record = new SnapshotRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TimestampUtc = nowUtc,
                    AppName = activeWindow.FriendlyAppName,
                    ProcessName = activeWindow.ProcessName,
                    WindowTitle = activeWindow.WindowTitle,
                    ImagePath = imagePath,
                    ImageWidth = scaledBitmap.Width,
                    ImageHeight = scaledBitmap.Height,
                    PerceptualHash = currentHash,
                    FullText = ocr.FullText,
                    OcrWords = ocr.Words
                };

                _vaultStore.AddSnapshot(record);
                _lastSavedHash = currentHash;

                EnforceRetentionIfDue();

                UpdateStatus("Watching");
                SnapshotCaptured?.Invoke(record);
                return record;
            }
            catch
            {
                // Screen may be locked (UAC/Win+L), which causes CopyFromScreen to throw Win32Exception.
                UpdateStatus(_isPaused ? "Paused" : "Watching (screen locked)");
                return null;
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private async Task CaptureLoopAsync(CancellationToken token)
        {
            // Initial retention sweep on startup.
            EnforceRetentionIfDue();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int intervalMs = Math.Clamp(_settings.CaptureIntervalSeconds, 3, 30) * 1000;
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);

                    if (!_isPaused)
                    {
                        await CaptureNowAsync(forceSave: false).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Keep capture loop resilient.
                }
            }
        }

        private void EnforceRetentionIfDue()
        {
            if ((DateTime.UtcNow - _lastRetentionCheckUtc) < TimeSpan.FromMinutes(30))
            {
                return;
            }

            _lastRetentionCheckUtc = DateTime.UtcNow;
            _vaultStore.PruneOlderThanDays(_settings.RetentionDays);
        }

        /// <summary>
        /// Computes a fast 64-bit 8x8 grayscale average perceptual hash (aHash).
        /// </summary>
        public static ulong ComputePerceptualHash8x8(Bitmap source)
        {
            using var tiny = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(source, 0, 0, 8, 8);
            }

            Span<double> luma = stackalloc double[64];
            double sum = 0.0;

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    Color pixel = tiny.GetPixel(x, y);
                    double gray = (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                    int idx = (y * 8) + x;
                    luma[idx] = gray;
                    sum += gray;
                }
            }

            double avg = sum / 64.0;
            ulong hash = 0UL;

            for (int i = 0; i < 64; i++)
            {
                if (luma[i] >= avg)
                {
                    hash |= (1UL << i);
                }
            }

            return hash;
        }

        private static Bitmap ScaleDownIfNeeded(Bitmap source, int maxWidth)
        {
            if (source.Width <= maxWidth)
            {
                return (Bitmap)source.Clone();
            }

            double ratio = (double)maxWidth / source.Width;
            int newWidth = maxWidth;
            int newHeight = Math.Max(1, (int)Math.Round(source.Height * ratio));

            var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(resized);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, newWidth, newHeight);
            return resized;
        }

        private static string SaveCompressedJpeg(Bitmap bitmap, DateTime timestampUtc, int quality)
        {
            DateTime local = timestampUtc.ToLocalTime();
            string dayFolder = Path.Combine(AppSettings.FramesDirectory, local.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayFolder);

            string fileName = $"{local:HH-mm-ss-fff}_{Guid.NewGuid().ToString("N").Substring(0, 6)}.jpg";
            string fullPath = Path.Combine(dayFolder, fileName);

            ImageCodecInfo? jpegEncoder = ImageCodecInfo
                .GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (jpegEncoder != null)
            {
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 35, 90));
                bitmap.Save(fullPath, jpegEncoder, encoderParams);
            }
            else
            {
                bitmap.Save(fullPath, ImageFormat.Jpeg);
            }

            return fullPath;
        }

        private void UpdateStatus(string message)
        {
            LastStatusMessage = message;
            StatusChanged?.Invoke(message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts?.Cancel();
                _loopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore shutdown race.
            }
            finally
            {
                _cts?.Dispose();
                _captureGate.Dispose();
            }
        }
    }
}
