using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RecallLocal.Models
{
    public sealed class OcrWordBox
    {
        [JsonPropertyName("t")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("w")]
        public double W { get; set; }

        [JsonPropertyName("h")]
        public double H { get; set; }
    }

    public sealed class SnapshotRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("ts")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("app")]
        public string AppName { get; set; } = string.Empty;

        [JsonPropertyName("proc")]
        public string ProcessName { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string WindowTitle { get; set; } = string.Empty;

        [JsonPropertyName("img")]
        public string ImagePath { get; set; } = string.Empty;

        [JsonPropertyName("w")]
        public int ImageWidth { get; set; }

        [JsonPropertyName("h")]
        public int ImageHeight { get; set; }

        [JsonPropertyName("hash")]
        public ulong PerceptualHash { get; set; }

        [JsonPropertyName("text")]
        public string FullText { get; set; } = string.Empty;

        [JsonPropertyName("words")]
        public List<OcrWordBox> OcrWords { get; set; } = new List<OcrWordBox>();

        [JsonIgnore]
        public DateTime LocalTime => TimestampUtc.ToLocalTime();

        [JsonIgnore]
        public string DisplayTime => LocalTime.ToString("hh:mm:ss tt");

        [JsonIgnore]
        public string DisplayDate => LocalTime.ToString("MMM dd, yyyy");
    }

    public sealed class VaultStats
    {
        public int TotalSnapshots { get; set; }
        public double DiskUsageMb { get; set; }
        public int IndexedWords { get; set; }
        public int UniqueApps { get; set; }
    }
}
