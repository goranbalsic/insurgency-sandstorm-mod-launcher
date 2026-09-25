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
        // A tiny "anything new?" request while the launcher is open (see Updater.Check: usually a 304 with no body).
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(3);
        private string failedTag, lastCheckError;
        private DateTime failedAtUtc, lastRebuildCheckUtc;
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
            ? "Looks for a new release every 3 minutes (a tiny request) and puts it in place for the next start. The launcher's only network access."
            : "Built from source: new releases are only reported. The launcher's only network access.";

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
            if (!manual && launchRunning) return;   // never during a launch
            updateBusy = true;
            CommandManager.InvalidateRequerySuggested();
            if (manual) UpdateStatus = "Checking for updates...";
            try
            {
                var info = await Task.Run(() => Updater.Check());
                // Saved at most once an hour: a check every few minutes should not keep writing the settings file.
                if (DateTime.UtcNow - State.Settings.LastUpdateCheckUtc > TimeSpan.FromHours(1)) { State.Settings.LastUpdateCheckUtc = DateTime.UtcNow; SaveSettingsSoon(); }
                if (lastCheckError != null) { AppLog.Info("Update check works again"); lastCheckError = null; }
                var have = readyVersion ?? Updater.Current;
                // A release rebuilt under the same version number: compare the exe checksum, at most every 30 minutes.
                bool rebuilt = false;
                if (info.Version == have && readyVersion == null && Updater.CanInstall && (manual || DateTime.UtcNow - lastRebuildCheckUtc > TimeSpan.FromMinutes(30)))
                {
                    lastRebuildCheckUtc = DateTime.UtcNow;
                    rebuilt = await Task.Run(() => Updater.IsRebuilt(info));
                    if (rebuilt) AppLog.Info("Update: " + info.Tag + " was rebuilt; installing the new build");
                }
                if (info.Version <= have && !rebuilt)
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
                // A download that failed is not tried again for an hour (it can be up to 50 MB).
                if (!manual && info.Tag == failedTag && DateTime.UtcNow - failedAtUtc < TimeSpan.FromHours(1)) return;
                var result = await Task.Run(() => Updater.Install(info));
                if (!result.Ok) { failedTag = info.Tag; failedAtUtc = DateTime.UtcNow; }
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
                // Offline or GitHub unreachable: logged once, then quiet until it works again.
                if (manual || ex.Message != lastCheckError) AppLog.Warn("Update check failed: " + ex.Message);
                lastCheckError = ex.Message;
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
