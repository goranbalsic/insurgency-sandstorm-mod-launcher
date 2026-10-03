using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Services;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        // A tiny "anything new?" request when the launcher opens, then every 10 minutes while it is open (see Updater.Check:
        // usually a HEAD request to the release page, with no body).
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(10);
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
        public string UpdateReadyTip => readyVersion == null ? "" : F("v{0} is installed and starts the next time you open the launcher. Click to restart now.", readyVersion.ToString(3));
        public string AutoUpdateHint => Updater.CanInstall
            ? T("Looks for a new release when the launcher opens and every 10 minutes (a tiny request), and puts it in place for the next start. The launcher's only network access.")
            : T("Built from source: new releases are only reported. The launcher's only network access.");

        /// <summary>
        /// Called when the window is up, every start (not for command-line tools or test runs): one check right away, while the
        /// game and the mods are read, then one every 10 minutes.
        /// </summary>
        public void StartUpdateChecks()
        {
            if (AppPaths.TestRun || updateTimer != null) return;
            Updater.CleanUp();
            ShowLastUpdateCheck();
            updateTimer = new DispatcherTimer { Interval = UpdateInterval };
            updateTimer.Tick += async (s, e) => await CheckForUpdates(false);
            updateTimer.Start();
            _ = CheckForUpdates(false);
        }

        private void ShowLastUpdateCheck()
        {
            var last = State.Settings.LastUpdateCheckUtc;
            if (last != default) UpdateStatus = F("Last checked {0}.", last.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture));
        }

        private async Task CheckForUpdates(bool manual)
        {
            if (updateBusy || (!manual && !State.Settings.AutoUpdate)) return;
            if (!manual && launchRunning) return;   // never during a launch
            updateBusy = true;
            CommandManager.InvalidateRequerySuggested();
            if (manual) UpdateStatus = T("Checking for updates...");
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
                    if (!updateReady) UpdateStatus = F("Up to date (v{0}). Checked {1}.", have.ToString(3), DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                    return;
                }
                string v = "v" + info.Version.ToString(3);
                if (!Updater.CanInstall || string.Equals(info.Tag, State.Settings.SkippedUpdateTag, StringComparison.OrdinalIgnoreCase))
                {
                    SetDownloadOnly(info, F("{0} is available.", v));
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
                    UpdateStatus = F("{0} is installed and starts the next time you open the launcher.", v);
                    ShowToast(F("Launcher updated to {0}. It is used from the next start.", v));
                    return;
                }
                if (result.SkipRelease) { State.Settings.SkippedUpdateTag = info.Tag; SaveSettingsSoon(); }
                SetDownloadOnly(info, F("{0} is available but could not be installed here ({1}).", v, result.Error));
            }
            catch (Exception ex)
            {
                // Offline or GitHub unreachable: logged once, then quiet until it works again.
                if (manual || ex.Message != lastCheckError) AppLog.Warn("Update check failed: " + ex.Message);
                lastCheckError = ex.Message;
                if (manual || string.IsNullOrEmpty(updateStatus)) UpdateStatus = F("Could not check for updates: {0}", ex.Message);
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
