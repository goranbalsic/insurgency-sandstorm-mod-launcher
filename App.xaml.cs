using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SandstormModLauncher.Core;

namespace SandstormModLauncher
{
    public partial class App : Application
    {
        private static Mutex singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppLog.Init(Path.Combine(AppPaths.DataDir, "logs"));
            DispatcherUnhandledException += (s, ex) =>
            {
                AppLog.Error("Unhandled UI exception", ex.Exception);
                MessageBox.Show("Something went wrong:\n\n" + ex.Exception.Message + "\n\nDetails were written to " + AppLog.FilePath,
                                "Sandstorm Mod Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => AppLog.Error("Unhandled exception", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { AppLog.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

            if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { SelfTest.Run(e.Args[1]); }
                catch (Exception ex) { File.WriteAllText(e.Args[1], "SELFTEST CRASHED: " + ex); }
                Shutdown();
                return;
            }

            singleInstance = new Mutex(true, "SandstormModLauncher-SingleInstance", out bool created);
            if (!created)
            {
                var me = Process.GetCurrentProcess();
                var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
                if (other != null)
                {
                    Native.ShowWindow(other.MainWindowHandle, Native.SW_RESTORE);
                    Native.SetForegroundWindow(other.MainWindowHandle);
                }
                Shutdown();
                return;
            }

            var window = new Views.MainWindow();
            MainWindow = window;
            window.Show();
        }
    }
}
