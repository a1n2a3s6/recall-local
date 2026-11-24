using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecallLocal.Models;

namespace RecallLocal.Engine
{
    public sealed class VaultStore
    {
        private readonly object _syncLock = new object();
        private readonly List<SnapshotRecord> _records = new List<SnapshotRecord>();
        private readonly Dictionary<string, HashSet<int>> _invertedIndex =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private static readonly char[] TokenSeparators =
        {
            ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}',
            '"', '\'', '/', '\\', '|', '-', '_', '+', '=', '<', '>', '@', '#', '$', '%', '^', '&', '*'
        };

        public event Action? VaultChanged;

        public VaultStore()
        {
            Directory.CreateDirectory(AppSettings.VaultRootDirectory);
            Directory.CreateDirectory(AppSettings.FramesDirectory);
            LoadIndex();
        }

        private void LoadIndex()
        {
            lock (_syncLock)
            {
                _records.Clear();
                _invertedIndex.Clear();

                if (!File.Exists(AppSettings.IndexFilePath))
                {
                    return;
                }

                try
                {
                    foreach (string line in File.ReadLines(AppSettings.IndexFilePath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var record = JsonSerializer.Deserialize<SnapshotRecord>(line);
                            if (record != null && File.Exists(record.ImagePath))
                            {
                                int idx = _records.Count;
                                _records.Add(record);
                                IndexRecordUnsafe(record, idx);
                            }
                        }
                        catch
                        {
                            // Skip any partially written trailing line.
                        }
                    }
                }
                catch
                {
                    // Ignore unreadable index and start cleanly.
                }
            }
        }

        public void AddSnapshot(SnapshotRecord record)
        {
            if (record == null) return;

            lock (_syncLock)
            {
                int idx = _records.Count;
                _records.Add(record);
                IndexRecordUnsafe(record, idx);

                try
                {
                    Directory.CreateDirectory(AppSettings.VaultRootDirectory);
                    string line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
                    File.AppendAllText(AppSettings.IndexFilePath, line);
                }
                catch
                {
                    // Best-effort persistence.
                }
            }

            VaultChanged?.Invoke();
        }

        public List<SnapshotRecord> Search(string? query, string? appFilter = null, DateTime? dateFilterLocal = null)
        {
            string trimmedQuery = query?.Trim() ?? string.Empty;
            string trimmedApp = appFilter?.Trim() ?? string.Empty;
            bool filterByApp = !string.IsNullOrEmpty(trimmedApp) &&
                               !trimmedApp.Equals("All", StringComparison.OrdinalIgnoreCase);

            lock (_syncLock)
            {
                IEnumerable<SnapshotRecord> candidates;

                if (string.IsNullOrEmpty(trimmedQuery))
                {
                    candidates = _records;
                }
                else
                {
                    string[] terms = trimmedQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    HashSet<int>? candidateIndices = null;

                    // Fast inverted-index pre-filter for exact/prefix tokens >= 2 chars.
                    foreach (string term in terms)
                    {
                        if (term.Length < 2) continue;

                        var termMatches = new HashSet<int>();
                        foreach (var kvp in _invertedIndex)
                        {
                            if (kvp.Key.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                termMatches.UnionWith(kvp.Value);
                            }
                        }

                        if (candidateIndices == null)
                        {
                            candidateIndices = termMatches;
                        }
                        else
                        {
                            candidateIndices.IntersectWith(termMatches);
                        }

                        if (candidateIndices.Count == 0) break;
                    }

                    if (candidateIndices != null)
                    {
                        candidates = candidateIndices
                            .OrderBy(i => i)
                            .Select(i => _records[i]);
                    }
                    else
                    {
                        candidates = _records;
                    }

                    // Final substring verification across FullText, AppName, WindowTitle, and ProcessName (Arabic + English).
                    candidates = candidates.Where(r => MatchesAllTerms(r, terms));
                }

                if (filterByApp)
                {
                    candidates = candidates.Where(r =>
                        r.AppName.Equals(trimmedApp, StringComparison.OrdinalIgnoreCase));
                }

                if (dateFilterLocal.HasValue)
                {
                    DateTime targetDate = dateFilterLocal.Value.Date;
                    candidates = candidates.Where(r => r.LocalTime.Date == targetDate);
                }

                return candidates.OrderBy(r => r.TimestampUtc).ToList();
            }
        }

        public List<string> GetTopApps(int maxCount = 8)
        {
            lock (_syncLock)
            {
                return _records
                    .Where(r => !string.IsNullOrWhiteSpace(r.AppName))
                    .GroupBy(r => r.AppName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(maxCount)
                    .Select(g => g.First().AppName)
                    .ToList();
            }
        }

        public VaultStats GetStats()
        {
            lock (_syncLock)
            {
                long totalBytes = 0;
                int totalWords = 0;
                var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (SnapshotRecord r in _records)
                {
                    totalWords += r.OcrWords?.Count ?? 0;
                    if (!string.IsNullOrWhiteSpace(r.AppName))
                    {
                        apps.Add(r.AppName);
                    }
                }

                try
                {
                    if (Directory.Exists(AppSettings.FramesDirectory))
                    {
                        foreach (string file in Directory.EnumerateFiles(AppSettings.FramesDirectory, "*.*", SearchOption.AllDirectories))
                        {
                            try { totalBytes += new FileInfo(file).Length; } catch { }
                        }
                    }

                    if (File.Exists(AppSettings.IndexFilePath))
                    {
                        totalBytes += new FileInfo(AppSettings.IndexFilePath).Length;
                    }
                }
                catch
                {
                    // Ignore transient IO errors.
                }

                return new VaultStats
                {
                    TotalSnapshots = _records.Count,
                    DiskUsageMb = Math.Round(totalBytes / (1024.0 * 1024.0), 2),
                    IndexedWords = totalWords,
                    UniqueApps = apps.Count
                };
            }
        }

        public int PruneOlderThanDays(int retentionDays)
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
            return RemoveWhere(r => r.TimestampUtc < cutoffUtc);
        }

        public int NukeLastMinutes(int minutes)
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-Math.Max(1, minutes));
            return RemoveWhere(r => r.TimestampUtc >= cutoffUtc);
        }

        public int NukeAll()
        {
            int removed = RemoveWhere(_ => true);
            try
            {
                if (Directory.Exists(AppSettings.FramesDirectory))
                {
                    foreach (string dir in Directory.GetDirectories(AppSettings.FramesDirectory))
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { }
                    }
                }
            }
            catch
            {
                // Ignore locked files.
            }
            return removed;
        }

        private int RemoveWhere(Func<SnapshotRecord, bool> predicate)
        {
            int removedCount = 0;
            var imagesToDelete = new List<string>();

            lock (_syncLock)
            {
                var kept = new List<SnapshotRecord>(_records.Count);
                foreach (SnapshotRecord record in _records)
                {
                    if (predicate(record))
                    {
                        removedCount++;
                        if (!string.IsNullOrWhiteSpace(record.ImagePath))
                        {
                            imagesToDelete.Add(record.ImagePath);
                        }
                    }
                    else
                    {
                        kept.Add(record);
                    }
                }

                if (removedCount > 0)
                {
                    _records.Clear();
                    _records.AddRange(kept);
                    RebuildInvertedIndexUnsafe();
                    RewriteIndexUnsafe();
                }
            }

            foreach (string path in imagesToDelete)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Ignore locked image file.
                }
            }

            CleanupEmptyDateDirectories();

            if (removedCount > 0)
            {
                VaultChanged?.Invoke();
            }

            return removedCount;
        }

        private void RebuildInvertedIndexUnsafe()
        {
            _invertedIndex.Clear();
            for (int i = 0; i < _records.Count; i++)
            {
                IndexRecordUnsafe(_records[i], i);
            }
        }

        private void IndexRecordUnsafe(SnapshotRecord record, int index)
        {
            AddTokensUnsafe(record.AppName, index);
            AddTokensUnsafe(record.ProcessName, index);
            AddTokensUnsafe(record.WindowTitle, index);
            AddTokensUnsafe(record.FullText, index);
        }

        private void AddTokensUnsafe(string? text, int index)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string[] tokens = text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in tokens)
            {
                string token = NormalizeArabicAndCase(raw);
                if (token.Length == 0) continue;

                if (!_invertedIndex.TryGetValue(token, out HashSet<int>? postings))
                {
                    postings = new HashSet<int>();
                    _invertedIndex[token] = postings;
                }
                postings.Add(index);
            }
        }

        private void RewriteIndexUnsafe()
        {
            try
            {
                Directory.CreateDirectory(AppSettings.VaultRootDirectory);
                string tempPath = AppSettings.IndexFilePath + ".tmp";
                using (var writer = new StreamWriter(tempPath, append: false))
                {
                    foreach (SnapshotRecord r in _records)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(r, JsonOptions));
                    }
                }
                File.Move(tempPath, AppSettings.IndexFilePath, overwrite: true);
            }
            catch
            {
                // Best-effort rewrite.
            }
        }

        private static void CleanupEmptyDateDirectories()
        {
            try
            {
                if (!Directory.Exists(AppSettings.FramesDirectory)) return;
                foreach (string dir in Directory.GetDirectories(AppSettings.FramesDirectory))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); } catch { }
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }

        private static bool MatchesAllTerms(SnapshotRecord record, string[] terms)
        {
            string combined = NormalizeArabicAndCase(
                $"{record.AppName} {record.ProcessName} {record.WindowTitle} {record.FullText}");

            foreach (string term in terms)
            {
                string normalizedTerm = NormalizeArabicAndCase(term);
                if (normalizedTerm.Length == 0) continue;

                if (combined.IndexOf(normalizedTerm, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        public static string NormalizeArabicAndCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Normalize Arabic alef/teh marbuta/alef maksura variants for resilient Arabic search.
            Span<char> buffer = input.Length <= 512 ? stackalloc char[input.Length] : new char[input.Length];
            int pos = 0;

            foreach (char c in input)
            {
                // Strip Arabic diacritics (tashkeel) U+064B..U+065F and tatweel U+0640.
                if ((c >= '\u064B' && c <= '\u065F') || c == '\u0640')
                {
                    continue;
                }

                char mapped = c switch
                {
                    '\u0622' or '\u0623' or '\u0625' => '\u0627', // آ أ إ -> ا
                    '\u0649' => '\u064A',                         // ى -> ي
                    '\u0629' => '\u0647',                         // ة -> ه
                    _ => char.ToLower(c, CultureInfo.InvariantCulture)
                };

                buffer[pos++] = mapped;
            }

            return new string(buffer.Slice(0, pos));
        }
    }
}
