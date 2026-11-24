using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RecallLocal.Models
{
    public sealed class AppSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public int CaptureIntervalSeconds { get; set; } = 5;
        public int RetentionDays { get; set; } = 7;
        public int JpegQuality { get; set; } = 62;
        public int MaxImageWidth { get; set; } = 1600;
        public int MinHashBitDiff { get; set; } = 3;
        public bool AutoSkipPrivateBrowsing { get; set; } = true;
        public bool StartPaused { get; set; } = false;

        public List<string> BlockedApps { get; set; } = new List<string>
        {
            "1password",
            "bitwarden",
            "keepass",
            "keepassxc",
            "lastpass",
            "dashlane",
            "nordpass",
            "credential",
            "credwiz",
            "keychain",
            "tor",
            "recalllocal"
        };

        public List<string> BlockedTitleKeywords { get; set; } = new List<string>
        {
            "inprivate",
            "incognito",
            "private browsing",
            "tor browser",
            "password manager",
            "windows security",
            "credential manager"
        };

        public static string VaultRootDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecallLocal");

        public static string FramesDirectory =>
            Path.Combine(VaultRootDirectory, "frames");

        public static string IndexFilePath =>
            Path.Combine(VaultRootDirectory, "index.jsonl");

        public static string SettingsFilePath =>
            Path.Combine(VaultRootDirectory, "settings.json");

        public void Normalize()
        {
            CaptureIntervalSeconds = Math.Clamp(CaptureIntervalSeconds, 3, 30);
            RetentionDays = Math.Clamp(RetentionDays, 1, 30);
            JpegQuality = Math.Clamp(JpegQuality, 35, 90);
            MaxImageWidth = Math.Clamp(MaxImageWidth, 800, 3840);
            MinHashBitDiff = Math.Clamp(MinHashBitDiff, 1, 20);
            BlockedApps ??= new List<string>();
            BlockedTitleKeywords ??= new List<string>();
        }

        public static AppSettings Load()
        {
            try
            {
                Directory.CreateDirectory(VaultRootDirectory);
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        loaded.Normalize();
                        return loaded;
                    }
                }
            }
            catch
            {
                // Fall back to safe defaults if settings file is corrupted.
            }

            var defaults = new AppSettings();
            defaults.Normalize();
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            try
            {
                Normalize();
                Directory.CreateDirectory(VaultRootDirectory);
                string tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tempPath, SettingsFilePath, overwrite: true);
            }
            catch
            {
                // Ignore transient file-lock errors when persisting settings.
            }
        }
    }
}
