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
            // --data <folder> keeps settings, logs and caches out of the normal data folder (for testing).
            var args = e.Args.ToList();
            int dataArg = args.IndexOf("--data");
            bool isolated = dataArg >= 0 && dataArg + 1 < args.Count;
            if (isolated)
            {
                AppPaths.UseDataDir(args[dataArg + 1]);
                args.RemoveRange(dataArg, 2);
            }
            var eArgs = args.ToArray();
            AppLog.Init(Path.Combine(AppPaths.DataDir, "logs"));
            DispatcherUnhandledException += (s, ex) =>
            {
                // No Windows message box: the player may be in the game. Log it and say so inside the launcher.
                AppLog.Error("Unhandled UI exception", ex.Exception);
                ex.Handled = true;
                try
                {
                    if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    {
                        vm.ShowToast("Something went wrong: " + ex.Exception.Message + " (a problem report was saved)");
                        Services.DebugReport.Auto("error", ex.Exception.GetType().Name + ": " + ex.Exception.Message, vm.State, vm.Monitor);
                    }
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => AppLog.Error("Unhandled exception", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { AppLog.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

            if (eArgs.Length >= 2 && eArgs[0] == "--dump-commands")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    var store = new Services.Store();
                    store.Load();
                    var install = Game.GameInstall.Detect(store.Settings.GameDirOverride);
                    var lines = ExecCatalog.Scan(install.ClientExe)
                        .Select(c => c.Signature.PadRight(64) + (c.Params.Count > 0 ? "  (" + c.ParamText + ")" : "") + (c.Note.Length > 0 ? "  " + c.Note : ""));
                    File.WriteAllLines(eArgs[1], lines);
                }
                catch (Exception ex) { File.WriteAllText(eArgs[1], "FAILED: " + ex); }
                Shutdown();
                return;
            }

            if (eArgs.Length >= 3 && (eArgs[0] == "--probe-test" || eArgs[0] == "--probe"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    if (eArgs[0] == "--probe-test") Services.ProbeTest.Run(eArgs[1], eArgs.Skip(2).ToList());
                    else Services.ProbeTest.Analyse(eArgs[1], eArgs[2], eArgs.Length > 3 ? eArgs[3] : eArgs[2]);
                }
                catch (Exception ex) { File.WriteAllText(eArgs[1], "PROBE TEST CRASHED: " + ex); }
                Shutdown();
                return;
            }

            if (eArgs.Length >= 2 && eArgs[0] == "--render")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                RenderPages(eArgs[1], eArgs.Skip(2).ToList());
                return;
            }

            if (eArgs.Length >= 2 && eArgs[0] == "--grab-test")
            {
                // Captures the bottom of the front window the same way the console check does (read-only).
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    var hwnd = Native.GetForegroundWindow();
                    var f = ScreenGrab.BottomOfWindow(hwnd);
                    if (f == null) File.WriteAllText(eArgs[1] + ".txt", "no picture");
                    else
                    {
                        f.SavePng(eArgs[1]);
                        File.WriteAllText(eArgs[1] + ".txt", $"strip {f.Width}x{f.Height} of {f.ClientWidth}x{f.ClientHeight}");
                    }
                }
                catch (Exception ex) { File.WriteAllText(eArgs[1] + ".txt", "FAILED: " + ex); }
                Shutdown();
                return;
            }

            if (eArgs.Length >= 2 && eArgs[0] == "--selftest")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { SelfTest.Run(eArgs[1]); }
                catch (Exception ex) { File.WriteAllText(eArgs[1], "SELFTEST CRASHED: " + ex); }
                Shutdown();
                return;
            }

            singleInstance = new Mutex(true, isolated ? "SandstormModLauncher-Test-" + Guid.NewGuid().ToString("N") : "SandstormModLauncher-SingleInstance", out bool created);
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

        /// <summary>
        /// --render &lt;folder&gt; [Page ...]: draws the launcher pages to PNG files without ever showing a window
        /// (1920x1080 at 125% scaling). Used to check the layout while someone else is using the PC.
        /// </summary>
        private async void RenderPages(string outDir, System.Collections.Generic.List<string> pages)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                var win = new Views.MainWindow();
                var vm = (ViewModels.MainViewModel)win.DataContext;
                var root = (FrameworkElement)win.Content;
                win.Content = null;
                root.DataContext = vm;
                const double w = 1536, h = 864;
                // The page leaves the window, so give it what it would inherit from there.
                var host = new System.Windows.Controls.Border { Child = root, Width = w, Height = h, Background = win.Background };
                System.Windows.Documents.TextElement.SetForeground(host, win.Foreground);
                System.Windows.Documents.TextElement.SetFontFamily(host, win.FontFamily);
                System.Windows.Documents.TextElement.SetFontSize(host, win.FontSize);
                System.Windows.Media.TextOptions.SetTextFormattingMode(root, System.Windows.Media.TextFormattingMode.Display);
                void Layout() { host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout(); }
                Layout();
                var init = vm.InitializeAsync();
                for (int i = 0; i < 600 && vm.Loading; i++) await Task.Delay(100);
                if (pages.Count == 0) pages = new System.Collections.Generic.List<string> { "Play", "Play-Rules", "Play-Advanced", "Mutators", "Playlists", "Mods", "Live", "Settings" };
                foreach (var page in pages)
                {
                    // "Play-Rules" = page Play, tab Rules
                    var parts = page.Split('-');
                    vm.Page = parts[0];
                    if (parts[0] == "Play") vm.PlayTab = parts.Length > 1 ? parts[1] : "Map";
                    await Task.Delay(700);
                    Layout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Layout();
                    // The first pass lets WPF prepare the glyphs of new text sizes; the second is the real picture.
                    var warm = new System.Windows.Media.Imaging.RenderTargetBitmap(1920, 1080, 120, 120, System.Windows.Media.PixelFormats.Pbgra32);
                    warm.Render(host);
                    await Task.Delay(600);
                    Layout();
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(1920, 1080, 120, 120, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var fs = File.Create(Path.Combine(outDir, page + ".png"))) enc.Save(fs);
                }
                vm.Dispose();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(outDir, "render-error.txt"), ex.ToString()); }
            finally { Shutdown(); }
        }
    }
}
