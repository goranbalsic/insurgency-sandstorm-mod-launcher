using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>Launcher diagnostic log: memory ring buffer plus a rolling file.</summary>
    public static class AppLog
    {
        private static readonly object sync = new object();
        private static readonly LinkedList<string> recent = new LinkedList<string>();
        private static string filePath;

        public static event Action<string> Line;

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
                Write("---- Sandstorm Mod Launcher started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { filePath = null; }
        }

        public static void Info(string msg) => Write("INFO  " + msg);
        public static void Warn(string msg) => Write("WARN  " + msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR " + msg + (ex != null ? " | " + ex.GetType().Name + ": " + ex.Message : ""));

        private static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
            lock (sync)
            {
                recent.AddLast(line);
                while (recent.Count > 600) recent.RemoveFirst();
                if (filePath != null)
                {
                    try { File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
            try { Line?.Invoke(line); } catch { }
        }

        public static string Recent()
        {
            lock (sync) return string.Join(Environment.NewLine, recent);
        }

        public static string FilePath => filePath;
    }
}
