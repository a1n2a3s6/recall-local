using System;
using System.Collections.Generic;
using RecallLocal.Models;
using RecallLocal.Native;

namespace RecallLocal.Engine
{
    public sealed class PrivacyGuard
    {
        private static readonly string[] BuiltInPrivateTitleMarkers =
        {
            "inprivate",
            "incognito",
            "private browsing",
            "tor browser"
        };

        private static readonly string[] BuiltInSensitiveProcesses =
        {
            "1password",
            "bitwarden",
            "keepass",
            "keepassxc",
            "credential",
            "credwiz",
            "recalllocal"
        };

        private readonly AppSettings _settings;

        public PrivacyGuard(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool ShouldSkipCapture(ActiveWindowInfo window, out string reason)
        {
            string title = window.WindowTitle ?? string.Empty;
            string proc = window.ProcessName ?? string.Empty;
            string app = window.FriendlyAppName ?? string.Empty;

            // Always avoid self-capturing RecallLocal overlay/settings windows.
            if (ContainsIgnoreCase(proc, "recalllocal") || ContainsIgnoreCase(app, "recalllocal"))
            {
                reason = "RecallLocal self-exclusion";
                return true;
            }

            // Built-in password manager and credential UI protection.
            foreach (string sensitiveProc in BuiltInSensitiveProcesses)
            {
                if (ContainsIgnoreCase(proc, sensitiveProc) || ContainsIgnoreCase(app, sensitiveProc))
                {
                    reason = $"Sensitive app blocked ({sensitiveProc})";
                    return true;
                }
            }

            // Built-in private browsing detection.
            if (_settings.AutoSkipPrivateBrowsing)
            {
                foreach (string marker in BuiltInPrivateTitleMarkers)
                {
                    if (ContainsIgnoreCase(title, marker))
                    {
                        reason = $"Private browsing detected ({marker})";
                        return true;
                    }
                }
            }

            // User-editable process/app blocklist.
            List<string> blockedApps = _settings.BlockedApps;
            if (blockedApps != null)
            {
                foreach (string entry in blockedApps)
                {
                    string clean = entry?.Trim() ?? string.Empty;
                    if (clean.Length == 0) continue;

                    if (ContainsIgnoreCase(proc, clean) ||
                        ContainsIgnoreCase(app, clean) ||
                        ContainsIgnoreCase(title, clean))
                    {
                        reason = $"User blocklist match ({clean})";
                        return true;
                    }
                }
            }

            // User-editable window title keyword blocklist.
            List<string> blockedKeywords = _settings.BlockedTitleKeywords;
            if (blockedKeywords != null)
            {
                foreach (string keyword in blockedKeywords)
                {
                    string clean = keyword?.Trim() ?? string.Empty;
                    if (clean.Length == 0) continue;

                    if (ContainsIgnoreCase(title, clean))
                    {
                        reason = $"Blocked window title ({clean})";
                        return true;
                    }
                }
            }

            reason = string.Empty;
            return false;
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                   !string.IsNullOrEmpty(value) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
