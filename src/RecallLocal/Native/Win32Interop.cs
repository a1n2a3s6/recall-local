using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RecallLocal.Native
{
    public readonly struct ActiveWindowInfo
    {
        public IntPtr Handle { get; init; }
        public string WindowTitle { get; init; }
        public string ProcessName { get; init; }
        public string FriendlyAppName { get; init; }
        public uint ProcessId { get; init; }
    }

    public static class Win32Interop
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint VK_R = 0x52;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public static ActiveWindowInfo GetActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return new ActiveWindowInfo
                {
                    Handle = IntPtr.Zero,
                    WindowTitle = "Desktop",
                    ProcessName = "explorer",
                    FriendlyAppName = "Desktop",
                    ProcessId = 0
                };
            }

            string title = string.Empty;
            int length = GetWindowTextLength(hWnd);
            if (length > 0)
            {
                var sb = new StringBuilder(length + 1);
                if (GetWindowText(hWnd, sb, sb.Capacity) > 0)
                {
                    title = sb.ToString().Trim();
                }
            }

            string processName = "unknown";
            string friendlyName = "Unknown";
            uint pid = 0;

            try
            {
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != 0)
                {
                    using Process proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                    friendlyName = ResolveFriendlyAppName(proc);
                }
            }
            catch
            {
                // Protected/elevated processes may deny inspection; fall back cleanly.
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = friendlyName;
            }

            return new ActiveWindowInfo
            {
                Handle = hWnd,
                WindowTitle = title,
                ProcessName = processName,
                FriendlyAppName = friendlyName,
                ProcessId = pid
            };
        }

        private static string ResolveFriendlyAppName(Process proc)
        {
            string raw = proc.ProcessName;
            string lower = raw.ToLowerInvariant();

            switch (lower)
            {
                case "chrome": return "Chrome";
                case "msedge": return "Edge";
                case "firefox": return "Firefox";
                case "brave": return "Brave";
                case "opera": return "Opera";
                case "code": return "VS Code";
                case "devenv": return "Visual Studio";
                case "windowsterminal": return "Terminal";
                case "wt": return "Terminal";
                case "powershell":
                case "pwsh": return "PowerShell";
                case "cmd": return "Command Prompt";
                case "explorer": return "File Explorer";
                case "notepad": return "Notepad";
                case "notepad++": return "Notepad++";
                case "winword": return "Word";
                case "excel": return "Excel";
                case "powerpnt": return "PowerPoint";
                case "outlook":
                case "olk": return "Outlook";
                case "slack": return "Slack";
                case "discord": return "Discord";
                case "teams":
                case "ms-teams": return "Teams";
                case "obsidian": return "Obsidian";
                case "notion": return "Notion";
                case "figma": return "Figma";
                case "spotify": return "Spotify";
                case "whatsapp": return "WhatsApp";
                case "telegram": return "Telegram";
            }

            try
            {
                string? desc = proc.MainModule?.FileVersionInfo?.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }

                string? fileName = proc.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return Path.GetFileNameWithoutExtension(fileName);
                }
            }
            catch
            {
                // Access denied on elevated or system processes.
            }

            return string.IsNullOrWhiteSpace(raw) ? "Unknown" : char.ToUpperInvariant(raw[0]) + raw[1..];
        }
    }
}
