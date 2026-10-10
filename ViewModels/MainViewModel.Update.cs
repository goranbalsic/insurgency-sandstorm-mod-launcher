using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using SandstormModLauncher.Core;
using SandstormModLauncher.Services;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        private string lastCheckError;
        private bool updateBusy, updateReady;
        private string updateStatus = "", updatePage;
        private Version readyVersion;

        public ICommand CheckUpdatesCommand { get; private set; }
        public ICommand RestartToUpdateCommand { get; private set; }
        public ICommand OpenUpdatePageCommand { get; private set; }
        public ICommand ToggleOfflineCommand { get; private set; }

        private void InitUpdateCommands()
        {
            CheckUpdatesCommand = new AsyncCommand(() => CheckForUpdates(), () => !updateBusy);
            RestartToUpdateCommand = new RelayCommand(() => App.RestartForUpdate(), () => updateReady && !launchRunning);
            OpenUpdatePageCommand = new RelayCommand(() => Open(updatePage), () => updatePage != null);
            ToggleOfflineCommand = new RelayCommand(() => OfflineMode = !OfflineMode);
        }

        /// <summary>
        /// Offline mode: the launcher never reaches the internet on its own. Mods, maps and rules are read from this PC
        /// either way, so nothing else changes; the version button below it still checks for an update when clicked.
        /// </summary>
        public bool OfflineMode
        {
            get => State.Settings.OfflineMode;
            set
            {
                if (State.Settings.OfflineMode == value) return;
                State.Settings.OfflineMode = value;
                SaveSettingsSoon();
                RaiseMany(nameof(OfflineMode), nameof(OfflineTip));
                CommandManager.InvalidateRequerySuggested();
                if (value) UpdateStatus = T("Offline mode. Nothing is sent or downloaded; click the version to check for an update.");
                else ShowLastUpdateCheck();
            }
        }

        public string OfflineTip => OfflineMode
            ? T("Offline mode is on: the launcher only reads this PC. Click to turn it off.")
            : T("Offline mode: turn off everything the launcher would do on the internet (update checks, SteamCMD and mod.io). Click to turn it on.");

        public string OfflineModeText => OfflineMode ? T("Offline") : T("Go offline");

        /// <summary>What the version button does; it is the only update check left, and it works in offline mode too.</summary>
        public string CheckUpdateTip => updateReady ? UpdateReadyTip
            : Updater.CanInstall ? T("Check for a new launcher version now. It is downloaded and installed for the next start.")
            : T("Check for a new launcher version now. This build reports updates but does not install them.");

        /// <summary>A newer version is in place and starts with the next launcher start.</summary>
        public bool UpdateReady
        {
            get => updateReady;
            private set { if (Set(ref updateReady, value)) { RaiseMany(nameof(CheckUpdateTip)); CommandManager.InvalidateRequerySuggested(); } }
        }
        /// <summary>A newer version exists but this exe does not install it (built from source, or the folder is read-only).</summary>
        public bool UpdateDownloadOnly => updatePage != null && !updateReady;
        public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
        public string UpdateReadyTip => readyVersion == null ? "" : F("v{0} is installed and starts the next time you open the launcher. Click to restart now.", readyVersion.ToString(3));
        public string AutoUpdateHint => Updater.CanInstall
            ? T("Click the version at the bottom left to look for a new release: it is downloaded and put in place for the next start.")
            : T("Built from source: a new release is only reported, never installed. Click the version at the bottom left to look for one.");

        /// <summary>
        /// Called when the window is up, every start (not for command-line tools or test runs). Only tidies up what an
        /// earlier update left behind and shows when the last check was: nothing is asked on the internet by itself.
        /// </summary>
        public void StartUpdateChecks()
        {
            if (AppPaths.TestRun) return;
            Updater.CleanUp();
            ShowLastUpdateCheck();
        }

        private void ShowLastUpdateCheck()
        {
            var last = State.Settings.LastUpdateCheckUtc;
            if (last != default) UpdateStatus = F("Last checked {0}.", last.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture));
        }

        /// <summary>Looks for a new release, downloads it and puts it in place. Only ever run from the version button.</summary>
        private async Task CheckForUpdates()
        {
            if (updateBusy || launchRunning) return;
            updateBusy = true;
            CommandManager.InvalidateRequerySuggested();
            UpdateStatus = T("Checking for updates...");
            try
            {
                var info = await Task.Run(() => Updater.Check());
                State.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                SaveSettingsSoon();
                if (lastCheckError != null) { AppLog.Info("Update check works again"); lastCheckError = null; }
                var have = readyVersion ?? Updater.Current;
                // A release rebuilt under the same version number: compare the exe checksum (only the small sums file).
                bool rebuilt = false;
                if (info.Version == have && readyVersion == null && Updater.CanInstall)
                {
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
                var result = await Task.Run(() => Updater.Install(info));
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
                if (ex.Message != lastCheckError) AppLog.Warn("Update check failed: " + ex.Message);
                lastCheckError = ex.Message;
                UpdateStatus = F("Could not check for updates: {0}", ex.Message);
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
