using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>
    /// Launcher diagnostic log. logs\launcher.log keeps INFO and above across runs; every run also
    /// gets its own logs\sessions\&lt;start time&gt;.log with DEBUG detail. Nothing here is ever sent anywhere.
    /// </summary>
    public static class AppLog
    {
        private static readonly object sync = new object();
        private static string filePath, sessionPath;

        public static void Init(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                filePath = Path.Combine(directory, "launcher.log");
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 2 * 1024 * 1024)
                {
                    string old = Path.Combine(directory, "launcher.old.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(filePath, old);
                }
                string sessions = Path.Combine(directory, "sessions");
                Directory.CreateDirectory(sessions);
                sessionPath = Path.Combine(sessions, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                foreach (var old in Directory.GetFiles(sessions, "*.log").OrderByDescending(f => f).Skip(40)) File.Delete(old);
                var v = typeof(AppLog).Assembly.GetName().Version;
                Write("----", $"Sandstorm Mod Launcher {v.ToString(3)} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Environment.OSVersion.VersionString} | session {Path.GetFileName(sessionPath)}", true);
            }
            catch { filePath = null; }
        }

        public static void Debug(string msg) => Write("DEBUG", msg, false);
        public static void Info(string msg) => Write("INFO ", msg, true);
        public static void Warn(string msg) => Write("WARN ", msg, true);
        public static void Error(string msg, Exception ex = null)
        {
            ErrorCount++;
            LastError = msg + (ex != null ? " | " + ex.GetType().Name + ": " + ex.Message : "");
            Write("ERROR", msg + (ex != null ? " | " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace : ""), true);
        }

        /// <summary>Errors logged in this run (checked by the --ui-torture test).</summary>
        public static int ErrorCount { get; private set; }
        public static string LastError { get; private set; }

        private static void Write(string level, string msg, bool main)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg;
            lock (sync)
            {
                if (main && filePath != null)
                {
                    try { File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
                if (sessionPath != null)
                {
                    try { File.AppendAllText(sessionPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
        }

        public static string FilePath => filePath;
        public static string SessionPath => sessionPath;
    }
}
