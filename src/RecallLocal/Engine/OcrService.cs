using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using RecallLocal.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RecallLocal.Engine
{
    public sealed class OcrExtractionResult
    {
        public string FullText { get; init; } = string.Empty;
        public List<OcrWordBox> Words { get; init; } = new List<OcrWordBox>();
        public string LanguageTag { get; init; } = "none";
    }

    public sealed class OcrService
    {
        private readonly OcrEngine? _primaryEngine;
        private readonly OcrEngine? _secondaryArabicEngine;
        private readonly string _activeLanguageTag;

        public bool IsAvailable => _primaryEngine != null;
        public string ActiveLanguageTag => _activeLanguageTag;

        public OcrService()
        {
            try
            {
                _primaryEngine = OcrEngine.TryCreateFromUserProfileLanguages();

                if (_primaryEngine == null)
                {
                    _primaryEngine = TryCreatePreferredFallbackEngine();
                }

                _activeLanguageTag = _primaryEngine?.RecognizerLanguage?.LanguageTag ?? "unavailable";

                // If primary engine is not Arabic, try loading an installed Arabic OCR pack as secondary helper.
                if (_primaryEngine != null &&
                    !_activeLanguageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
                {
                    _secondaryArabicEngine = TryCreateLanguagePrefixEngine("ar");
                }
            }
            catch
            {
                _primaryEngine = null;
                _secondaryArabicEngine = null;
                _activeLanguageTag = "unavailable";
            }
        }

        private static OcrEngine? TryCreatePreferredFallbackEngine()
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            if (available == null || available.Count == 0)
            {
                return null;
            }

            // Prefer English, then Arabic, then first available installed language pack.
            Language? preferred =
                available.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ??
                available.FirstOrDefault(l => l.LanguageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) ??
                available[0];

            return preferred != null ? OcrEngine.TryCreateFromLanguage(preferred) : null;
        }

        private static OcrEngine? TryCreateLanguagePrefixEngine(string prefix)
        {
            try
            {
                var available = OcrEngine.AvailableRecognizerLanguages;
                Language? match = available?.FirstOrDefault(l =>
                    l.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                return match != null ? OcrEngine.TryCreateFromLanguage(match) : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<OcrExtractionResult> RecognizeAsync(Bitmap bitmap)
        {
            if (bitmap == null || _primaryEngine == null)
            {
                return new OcrExtractionResult();
            }

            try
            {
                using SoftwareBitmap softwareBitmap = ConvertToSoftwareBitmap(bitmap);
                OcrResult primaryResult = await _primaryEngine.RecognizeAsync(softwareBitmap).AsTask().ConfigureAwait(false);

                int imgW = Math.Max(1, bitmap.Width);
                int imgH = Math.Max(1, bitmap.Height);

                var words = new List<OcrWordBox>(128);
                var textBuilder = new StringBuilder(1024);

                AppendOcrResult(primaryResult, imgW, imgH, words, textBuilder);

                // If primary produced very little text and an Arabic pack is installed, run secondary pass.
                if (words.Count < 4 && _secondaryArabicEngine != null)
                {
                    OcrResult arabicResult = await _secondaryArabicEngine.RecognizeAsync(softwareBitmap).AsTask().ConfigureAwait(false);
                    if (arabicResult != null && !string.IsNullOrWhiteSpace(arabicResult.Text))
                    {
                        var arWords = new List<OcrWordBox>(128);
                        var arBuilder = new StringBuilder(1024);
                        AppendOcrResult(arabicResult, imgW, imgH, arWords, arBuilder);

                        if (arWords.Count > words.Count)
                        {
                            return new OcrExtractionResult
                            {
                                FullText = arBuilder.ToString().Trim(),
                                Words = arWords,
                                LanguageTag = _secondaryArabicEngine.RecognizerLanguage?.LanguageTag ?? "ar"
                            };
                        }
                    }
                }

                return new OcrExtractionResult
                {
                    FullText = textBuilder.ToString().Trim(),
                    Words = words,
                    LanguageTag = _activeLanguageTag
                };
            }
            catch
            {
                // Safely handle missing language packs, oversized bitmaps, or WinRT runtime issues.
                return new OcrExtractionResult();
            }
        }

        private static void AppendOcrResult(
            OcrResult result,
            int imgW,
            int imgH,
            List<OcrWordBox> words,
            StringBuilder textBuilder)
        {
            if (result?.Lines == null) return;

            foreach (OcrLine line in result.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    textBuilder.AppendLine(line.Text.Trim());
                }

                if (line.Words == null) continue;

                foreach (OcrWord word in line.Words)
                {
                    string wordText = word.Text?.Trim() ?? string.Empty;
                    if (wordText.Length == 0) continue;

                    var rect = word.BoundingRect;
                    words.Add(new OcrWordBox
                    {
                        Text = wordText,
                        X = Math.Round(Math.Clamp(rect.X / imgW, 0.0, 1.0), 4),
                        Y = Math.Round(Math.Clamp(rect.Y / imgH, 0.0, 1.0), 4),
                        W = Math.Round(Math.Clamp(rect.Width / imgW, 0.0, 1.0), 4),
                        H = Math.Round(Math.Clamp(rect.Height / imgH, 0.0, 1.0), 4)
                    });
                }
            }
        }

        private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;

            // Ensure premultiplied 32bpp BGRA pixel format matching BitmapPixelFormat.Bgra8.
            Bitmap working = source;
            bool ownsWorking = false;

            if (source.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                working = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                ownsWorking = true;
                using Graphics g = Graphics.FromImage(working);
                g.DrawImage(source, 0, 0, width, height);
            }

            try
            {
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = working.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int rowBytes = width * 4;
                    byte[] pixels = new byte[rowBytes * height];

                    if (data.Stride == rowBytes)
                    {
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr srcRow = IntPtr.Add(data.Scan0, y * data.Stride);
                            Marshal.Copy(srcRow, pixels, y * rowBytes, rowBytes);
                        }
                    }

                    using var writer = new DataWriter();
                    writer.WriteBytes(pixels);
                    IBuffer buffer = writer.DetachBuffer();

                    return SoftwareBitmap.CreateCopyFromBuffer(
                        buffer,
                        BitmapPixelFormat.Bgra8,
                        width,
                        height,
                        BitmapAlphaMode.Premultiplied);
                }
                finally
                {
                    working.UnlockBits(data);
                }
            }
            finally
            {
                if (ownsWorking)
                {
                    working.Dispose();
                }
            }
        }
    }
}
