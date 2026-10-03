using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Narcotics
{
    // A small in-memory ring buffer of the mod's own log lines, filled by a BepInEx log listener, so the
    // on-screen debug panel can show recent activity without alt-tabbing to the console.
    internal static class LogPanel
    {
        internal static bool Show = true;                 // panel visible (only drawn while EnableDebugKeys is on)
        private const int Max = 22;
        private static readonly List<string> _lines = new List<string>();
        internal static List<string> Lines => _lines;

        internal static void Append(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(line)) return;
                if (line.StartsWith("[Narcotics]")) line = line.Substring("[Narcotics]".Length);
                line = line.Trim();
                if (line.Length > 118) line = line.Substring(0, 118);
                _lines.Add(line);
                while (_lines.Count > Max) _lines.RemoveAt(0);
            }
            catch { }
        }
    }

    // Captures this mod's log lines (only) into LogPanel, without touching any of the existing log calls.
    internal sealed class NarcLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null || eventArgs.Source != NarcoticsPlugin.Log) return;
                string msg = eventArgs.Data != null ? eventArgs.Data.ToString() : "";
                if ((eventArgs.Level & LogLevel.Warning) != 0) msg = "! " + msg;
                LogPanel.Append(msg);
            }
            catch { }
        }

        public void Dispose() { }
    }
}
