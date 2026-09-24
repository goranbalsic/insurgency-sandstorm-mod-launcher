using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Services;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(3);
        private DispatcherTimer updateTimer;
        private bool updateBusy, updateReady;
        private string updateStatus = "", updatePage;
        private Version readyVersion;

        public ICommand CheckUpdatesCommand { get; private set; }
        public ICommand RestartToUpdateCommand { get; private set; }
        public ICommand OpenUpdatePageCommand { get; private set; }

        private void InitUpdateCommands()
        {
            CheckUpdatesCommand = new AsyncCommand(() => CheckForUpdates(true), () => !updateBusy);
            RestartToUpdateCommand = new RelayCommand(() => App.RestartForUpdate(), () => updateReady && !launchRunning);
            OpenUpdatePageCommand = new RelayCommand(() => Open(updatePage), () => updatePage != null);
        }

        public bool AutoUpdate
        {
            get => State.Settings.AutoUpdate;
            set
            {
                SetSetting(() => State.Settings.AutoUpdate = value);
                if (value) _ = CheckForUpdates(false);
            }
        }

        /// <summary>A newer version is in place and starts with the next launcher start.</summary>
        public bool UpdateReady { get => updateReady; private set { if (Set(ref updateReady, value)) CommandManager.InvalidateRequerySuggested(); } }
        /// <summary>A newer version exists but this exe does not install it (built from source, or the folder is read-only).</summary>
        public bool UpdateDownloadOnly => updatePage != null && !updateReady;
        public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
        public string UpdateReadyTip => readyVersion == null ? "" : "v" + readyVersion.ToString(3) + " is installed and starts the next time you open the launcher. Click to restart now.";
        public string AutoUpdateHint => Updater.CanInstall
            ? "Checks this project's GitHub releases every few hours and quietly puts a new version in place. It starts the next time you open the launcher. This is the only network access the launcher makes."
            : "This exe was built from source, so new releases are only reported, not installed. This check is the only network access the launcher makes.";

        /// <summary>Called once the window is up (not for command-line tools or test runs).</summary>
        public void StartUpdateChecks()
        {
            if (AppPaths.TestRun) return;
            Updater.CleanUp();
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            updateTimer.Tick += async (s, e) =>
            {
                updateTimer.Interval = UpdateInterval;
                await CheckForUpdates(false);
            };
            updateTimer.Start();
            var last = State.Settings.LastUpdateCheckUtc;
            if (last != default) UpdateStatus = "Last checked " + last.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture) + ".";
        }

        private async Task CheckForUpdates(bool manual)
        {
            if (updateBusy || (!manual && !State.Settings.AutoUpdate)) return;
            updateBusy = true;
            CommandManager.InvalidateRequerySuggested();
            if (manual) UpdateStatus = "Checking for updates...";
            try
            {
                var info = await Task.Run(() => Updater.Check());
                State.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                SaveSettingsSoon();
                var have = readyVersion ?? Updater.Current;
                if (info.Version <= have)
                {
                    if (!updateReady) UpdateStatus = "Up to date (v" + have.ToString(3) + "). Checked " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
                    return;
                }
                string v = "v" + info.Version.ToString(3);
                if (!Updater.CanInstall || string.Equals(info.Tag, State.Settings.SkippedUpdateTag, StringComparison.OrdinalIgnoreCase))
                {
                    SetDownloadOnly(info, v + " is available.");
                    return;
                }
                var result = await Task.Run(() => Updater.Install(info));
                if (result.Ok)
                {
                    readyVersion = info.Version;
                    updatePage = null;
                    UpdateReady = true;
                    RaiseMany(nameof(UpdateReadyTip), nameof(UpdateDownloadOnly));
                    UpdateStatus = v + " is installed and starts the next time you open the launcher.";
                    ShowToast("Launcher updated to " + v + ". It is used from the next start.");
                    return;
                }
                if (result.SkipRelease) { State.Settings.SkippedUpdateTag = info.Tag; SaveSettingsSoon(); }
                SetDownloadOnly(info, v + " is available but could not be installed here (" + result.Error + ").");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Update check failed: " + ex.Message);
                if (manual || string.IsNullOrEmpty(updateStatus)) UpdateStatus = "Could not check for updates: " + ex.Message;
            }
            finally
            {
                updateBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void SetDownloadOnly(UpdateInfo info, string status)
        {
            updatePage = info.PageUrl ?? "https://github.com/" + Updater.Repo + "/releases/latest";
            UpdateStatus = status;
            Raise(nameof(UpdateDownloadOnly));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
