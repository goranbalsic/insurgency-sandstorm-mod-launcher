using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed class ServerTypeItem
    {
        public ServerType Type { get; set; }
        public string Name => T(Type.Name);
        public string Description => T(Type.Description);
    }

    /// <summary>
    /// Getting a dedicated server going without knowing how: install and update its files with SteamCMD, set it up as
    /// one of the guide's kinds of server, and show how players reach it.
    /// </summary>
    public sealed partial class MainViewModel
    {
        public ObservableCollection<ServerTypeItem> ServerTypeItems { get; } = new ObservableCollection<ServerTypeItem>();
        public ICommand InstallServerCommand { get; private set; }
        public ICommand UpdateServerFilesCommand { get; private set; }
        public ICommand RepairServerFilesCommand { get; private set; }
        public ICommand CancelServerInstallCommand { get; private set; }
        public ICommand ChooseServerInstallDirCommand { get; private set; }
        public ICommand OpenRuntimeDownloadCommand { get; private set; }
        public ICommand ApplyServerTypeCommand { get; private set; }
        public ICommand CopyJoinCommandCommand { get; private set; }
        public ICommand AllowServerFirewallCommand { get; private set; }
        public ICommand RefreshReachabilityCommand { get; private set; }
        public ICommand CheckServerModsCommand { get; private set; }

        private CancellationTokenSource installCts;
        private bool serverInstalling, installHasPercent, serverTypeNight;
        private string installStage = "", installDetail = "", installElapsed = "";
        private System.Windows.Threading.DispatcherTimer installClock;
        private double installPercent;
        private List<string> missingRuntime = new List<string>();
        private FirewallState firewall = FirewallState.Unknown;
        private string lanAddress;

        private void InitServerSetupCommands()
        {
            InstallServerCommand = new AsyncCommand(() => InstallServerFiles(false, false), () => !serverBusy && !ServerRemote && !ServerInstall.Found);
            UpdateServerFilesCommand = new AsyncCommand(() => InstallServerFiles(true, false), () => !serverBusy && ServerCanUpdate && !ServerRunningHere);
            RepairServerFilesCommand = new AsyncCommand(() => InstallServerFiles(true, true), () => !serverBusy && ServerCanUpdate && !ServerRunningHere);
            CancelServerInstallCommand = new RelayCommand(() => installCts?.Cancel(), () => serverInstalling);
            ChooseServerInstallDirCommand = new RelayCommand(ChooseServerInstallDir, () => !serverInstalling);
            OpenRuntimeDownloadCommand = new RelayCommand(() => Open(ServerInstall.RuntimeUrl));
            ApplyServerTypeCommand = new AsyncCommand(p => ApplyServerType(p as ServerTypeItem), p => !serverBusy && CanChangeSetup && !ServerRemote && ServerInstall.Found);
            CopyJoinCommandCommand = new RelayCommand(() => CopyText(ServerJoinCommand, "Copied: players paste it into the game's console (the ` key)"), () => ServerJoinCommand.Length > 0);
            AllowServerFirewallCommand = new AsyncCommand(AllowServerFirewall, () => !serverBusy && ServerInstall.Found && !ServerRemote);
            RefreshReachabilityCommand = new RelayCommand(RefreshReachability);
            CheckServerModsCommand = new AsyncCommand(() => CheckServerMods(false), () => !ServerRemote);
            RebuildServerTypes();
        }

        /// <summary>The server type list (again after a language change: its texts are read when made).</summary>
        private void RebuildServerTypes()
        {
            ServerTypeItems.Clear();
            foreach (var t in ServerTypes.All) ServerTypeItems.Add(new ServerTypeItem { Type = t });
        }

        // ------------------------------------------------------------------ server files

        /// <summary>Where SteamCMD puts a new server (the settings folder, or C:\SandstormServer on the Windows drive).</summary>
        public string ServerInstallDir
        {
            get => string.IsNullOrWhiteSpace(State.Settings.ServerInstallDir) ? DefaultServerInstallDir : State.Settings.ServerInstallDir;
            set { State.Settings.ServerInstallDir = (value ?? "").Trim(); Raise(); SaveSettingsSoon(); }
        }

        private static string DefaultServerInstallDir => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "SandstormServer");

        public bool ServerCanUpdate => !ServerRemote && ServerInstall.Found && ServerInstall.ManagedBySteamCmd;
        /// <summary>A server in a Steam library: Steam keeps it up to date.</summary>
        public bool ServerUpdatedBySteam => !ServerRemote && ServerInstall.Found && !ServerInstall.ManagedBySteamCmd;
        public bool ServerNotInstalled => !ServerRemote && !ServerInstall.Found;
        public bool ServerInstalling { get => serverInstalling; private set { if (Set(ref serverInstalling, value)) CommandManager.InvalidateRequerySuggested(); } }
        public string InstallStage { get => installStage; private set => Set(ref installStage, value); }
        public string InstallDetail { get => installDetail; private set => Set(ref installDetail, value); }
        /// <summary>Time since the install started (SteamCMD reports its progress only every half minute or so).</summary>
        public string InstallElapsed { get => installElapsed; private set => Set(ref installElapsed, value); }
        public double InstallPercent { get => installPercent; private set => Set(ref installPercent, value); }
        public bool InstallHasPercent { get => installHasPercent; private set { if (Set(ref installHasPercent, value)) Raise(nameof(InstallIndeterminate)); } }
        /// <summary>A step without a percentage (the bar moves without a value).</summary>
        public bool InstallIndeterminate => !installHasPercent;
        public bool RuntimeMissing => !ServerRemote && missingRuntime.Count > 0;
        public string RuntimeMissingText => F("The Microsoft Visual C++ runtime the server needs is missing ({0}). Install it from Microsoft, then start the server.", string.Join(", ", missingRuntime));

        private void ChooseServerInstallDir()
        {
            string dir = FolderPicker.Pick(OwnerHandle, T("Pick an empty folder for the server (a new one is made inside if you like)"), Directory.Exists(ServerInstallDir) ? ServerInstallDir : null);
            if (dir != null) ServerInstallDir = dir;
        }

        private static string Size(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                                                                  : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>Installs the server (or updates or repairs the one SteamCMD installed) and uses it.</summary>
        private async Task InstallServerFiles(bool update, bool repair)
        {
            string dir = update ? ServerInstall.Root : ServerInstallDir;
            string problem = update ? null : SteamCmd.CheckFolder(dir);
            if (problem != null) { await ShowMessage("The server cannot go there", problem); return; }
            if (!update)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
                    if (drive.AvailableFreeSpace < 10L << 30
                        && await Ask("Little free space", F("Drive {0} has {1} free. The server needs about 5 GB, and more room while it updates. Install it there anyway?", drive.Name, Size(drive.AvailableFreeSpace)), "Install anyway") != "Install anyway")
                        return;
                }
                catch (Exception ex) { AppLog.Warn("Free space: " + ex.Message); }
            }
            ServerBusy = true;
            ServerInstalling = true;
            InstallStage = T(repair ? "Checking every file" : update ? "Updating the server" : "Installing the server");
            InstallDetail = "";
            InstallPercent = 0;
            InstallHasPercent = false;
            installCts = new CancellationTokenSource();
            var started = DateTime.UtcNow;
            InstallElapsed = "0:00";
            installClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            installClock.Tick += (s, e) => { var t = DateTime.UtcNow - started; InstallElapsed = ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture); };
            installClock.Start();
            var progress = new Progress<SteamCmdProgress>(p =>
            {
                if (p.Stage != null) InstallStage = T(p.Stage);
                InstallHasPercent = p.Percent.HasValue;
                if (p.Percent.HasValue) InstallPercent = p.Percent.Value;
                InstallDetail = p.Total > 0 ? F("{0} of {1}", Size(p.Done), Size(p.Total)) : "";
            });
            SteamCmdResult result;
            try { result = await SteamCmd.InstallServer(dir, repair, progress, installCts.Token); }
            finally
            {
                installClock.Stop();
                ServerInstalling = false;
                ServerBusy = false;
                installCts = null;
            }
            if (result.Cancelled) { ShowToast("Stopped. Press it again later: SteamCMD carries on where it stopped."); RaiseServerSetup(); return; }
            if (!result.Ok) { await ShowMessage(update ? "The server was not updated" : "The server was not installed", result.Error); RaiseServerSetup(); return; }
            if (!update)
            {
                State.Settings.ServerDirOverride = dir;
                ReloadServerInstall();
            }
            else ReloadServerInstall();
            ShowToast(result.UpToDate ? "The server is up to date" : update ? "Server updated" : "Server installed. Pick a server type, then start it.");
        }

        // ------------------------------------------------------------------ server types

        public bool ServerTypeNight { get => serverTypeNight; set => Set(ref serverTypeNight, value); }

        /// <summary>Sets up the Play match, the map cycle and the player count as one of the guide's kinds of server.</summary>
        private async Task ApplyServerType(ServerTypeItem item)
        {
            if (item == null) return;
            var type = item.Type;
            if (SetupChanged && await Ask(F("Set up a {0} server", T(type.Name)),
                    "This sets up a new match in Play for the server. The setup on screen is replaced (save it first to keep it).", "Set it up") != "Set it up")
                return;
            var cycle = ServerTypes.Cycle(State, type, serverTypeNight);
            bool writeCycle = MapCycleFile != null;
            if (writeCycle && MapCycleCount > 0)
            {
                string answer = await Ask("Replace the map cycle?",
                    F("The map cycle has {0} scenarios. Replace them with the {1} of this server type (a copy of the file is kept)?", MapCycleCount, cycle.Count),
                    "Replace", "Keep mine", "Cancel");
                if (answer == null || answer == "Cancel") return;
                writeCycle = answer == "Replace";
            }
            string message = ServerTypes.Apply(Profile, State, type);
            if (message == null) { ShowToast(F("The game has no {0} scenario", T(type.Name))); return; }
            RefreshFromProfile(true);
            RaiseSetup();
            State.Settings.ServerMaxPlayers = type.MaxPlayers;
            if (writeCycle) SaveMapCycle(cycle.ToList());
            if (MapCycleCount > 0) State.Settings.ServerUseMapCycle = true;
            SaveSettingsSoon();
            // The big button now starts this server.
            PlayTarget = "Server";
            RaiseServer();
            UpdateServerPlan();
            ShowToast(message + " · " + (MapCycleCount == 1 ? T("1 scenario in the map cycle") : F("{0} scenarios in the map cycle", MapCycleCount)));
        }

        // ------------------------------------------------------------------ how players join

        public string ServerLanAddress => lanAddress ?? T("not found");
        /// <summary>What a player on the same network types into the game's console to join.</summary>
        public string ServerJoinCommand => lanAddress == null ? "" : "open " + lanAddress + ":" + State.Settings.ServerPort.ToString(CultureInfo.InvariantCulture);
        public string ServerJoinText => lanAddress == null ? T("No network connection found on this PC.") : ServerJoinCommand;
        public string ServerPortsText => F("UDP {0} (game) and UDP {1} (query)", State.Settings.ServerPort, State.Settings.ServerQueryPort)
                                         + (State.Settings.ServerRconFromNetwork ? " · " + F("TCP {0} for RCON from other PCs", State.Settings.ServerRconPort) : "");
        public string ServerInternetText => F("For players on the internet, forward {0} on your router to this PC ({1}). They find \"{2}\" in the game's server browser.",
            F("UDP {0} and {1}", State.Settings.ServerPort, State.Settings.ServerQueryPort), ServerLanAddress,
            string.IsNullOrWhiteSpace(State.Settings.ServerName) ? T("your server") : State.Settings.ServerName.Trim());
        public bool FirewallNeedsAllow => firewall == FirewallState.Blocked || firewall == FirewallState.NotAsked;
        public string FirewallText
        {
            get
            {
                switch (firewall)
                {
                    case FirewallState.Allowed: return T("Windows Firewall lets the server in.");
                    case FirewallState.Off: return T("Windows Firewall is off on this network.");
                    case FirewallState.Blocked: return T("Windows Firewall blocks the server (Cancel was pressed when Windows asked). Allow it, or other PCs cannot join.");
                    case FirewallState.NotAsked: return T("Windows asks to let the server in when it first starts: allow it (or press Allow now).");
                    default: return T("The Windows Firewall setting of the server could not be read.");
                }
            }
        }

        private int reachabilityRun;

        /// <summary>The server's addresses and firewall setting, read again (after a network or install change). Windows'
        /// firewall rules are read on a worker thread: there can be hundreds of them.</summary>
        private async void RefreshReachability()
        {
            int run = ++reachabilityRun;
            string exe = ServerInstall.Found ? ServerInstall.ShippingExe : null;
            var (ip, fw) = await Task.Run(() => (ServerNetwork.LanAddresses().FirstOrDefault(), exe == null ? FirewallState.Unknown : ServerNetwork.Firewall(exe)));
            if (run != reachabilityRun) return;   // a newer check is on its way
            lanAddress = ip;
            firewall = fw;
            RaiseMany(nameof(ServerLanAddress), nameof(ServerJoinCommand), nameof(ServerJoinText), nameof(ServerPortsText), nameof(ServerInternetText), nameof(FirewallText), nameof(FirewallNeedsAllow));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task AllowServerFirewall()
        {
            if (!ServerInstall.Found) return;
            if (await Ask("Allow the server through Windows Firewall?",
                    "Windows asks you for administrator rights next. The rule lets other PCs reach this server program only (its old rules are replaced).", "Continue") != "Continue")
                return;
            string exe = ServerInstall.ShippingExe;
            bool ok = await Task.Run(() => ServerNetwork.AllowThroughFirewall(exe));
            var fw = await Task.Run(() => ServerNetwork.Firewall(exe));
            firewall = fw;
            RefreshReachability();
            ShowToast(ok && fw == FirewallState.Allowed ? "Windows Firewall now lets the server in" : "Windows Firewall was not changed");
        }

        // ------------------------------------------------------------------ mods on the server

        private string serverModText = "", serverModKind = "Off";
        private DateTime serverStartedUtc;
        private ModVerdict lastModVerdict = ModVerdict.NotAsked;

        /// <summary>What the running server's log says about its mods (empty when there is nothing to say).</summary>
        public string ServerModText { get => serverModText; private set { if (Set(ref serverModText, value)) Raise(nameof(ServerModShown)); } }
        /// <summary>Off, Busy, Match (loaded) or Bad, for the status colours.</summary>
        public string ServerModKind { get => serverModKind; private set => Set(ref serverModKind, value); }
        public bool ServerModShown => serverModText.Length > 0;

        /// <summary>
        /// Reads the server's own log for what it did with the mods. A server that starts "fine" with mods on but never logs in
        /// to mod.io has no mods (players cannot join a modded match), and the launcher says so instead of showing a green light.
        /// </summary>
        private async Task CheckServerMods(bool automatic)
        {
            if (Server == null || ServerRemote) return;
            bool on = State.Settings.ServerModsEnabled;
            if (!ServerRunningHere)
            {
                ServerModText = on && !automatic ? T("The server is not running.") : "";
                ServerModKind = "Off";
                lastModVerdict = ModVerdict.NotAsked;
                return;
            }
            if (!on && automatic) { ServerModText = ""; return; }
            bool just = DateTime.UtcNow - serverStartedUtc < TimeSpan.FromSeconds(50);
            var st = await Task.Run(() => Server.ModStatus(on, just));
            if (st.Verdict != lastModVerdict && (st.Verdict == ModVerdict.NoLogin || st.Verdict == ModVerdict.LoginFailed))
                AppLog.Warn("Server mods: " + st.Verdict + (st.LoginError != null ? " (" + st.LoginError + ")" : ""));
            lastModVerdict = st.Verdict;
            ServerModText = on || !automatic ? st.Text : "";
            ServerModKind = st.Verdict == ModVerdict.Loaded ? "Match" : st.Verdict == ModVerdict.NoLogin || st.Verdict == ModVerdict.LoginFailed ? "Bad"
                          : st.Verdict == ModVerdict.Waiting ? "Busy" : "Off";
        }

        /// <summary>After a start with mods on: looks at the log a little later, when the server has had time to log in to mod.io.</summary>
        private async void WatchServerMods()
        {
            if (!State.Settings.ServerModsEnabled) { ServerModText = ""; return; }
            serverStartedUtc = DateTime.UtcNow;
            ServerModText = T("Waiting for the server to report its mods...");
            ServerModKind = "Busy";
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(35));
                await CheckServerMods(true);
                await Task.Delay(TimeSpan.FromSeconds(40));
                await CheckServerMods(true);
                if (lastModVerdict == ModVerdict.NoLogin || lastModVerdict == ModVerdict.LoginFailed) ShowToast("The server started, but it has no mods: see the Mods card");
            }
            catch (Exception ex) { AppLog.Warn("Server mod check: " + ex.Message); }
        }

        // ------------------------------------------------------------------ options from the admin guide

        public bool ServerVoteKick { get => State.Settings.ServerVoteKick; set => SetServerSetting(() => State.Settings.ServerVoteKick = value); }
        public bool ServerOfficialRules { get => State.Settings.ServerOfficialRules; set => SetServerSetting(() => State.Settings.ServerOfficialRules = value); }

        private void RaiseServerSetup()
        {
            missingRuntime = ServerInstall.MissingRuntime();
            RaiseMany(nameof(ServerInstallDir), nameof(ServerCanUpdate), nameof(ServerUpdatedBySteam), nameof(ServerNotInstalled), nameof(RuntimeMissing), nameof(RuntimeMissingText),
                      nameof(ServerVoteKick), nameof(ServerOfficialRules));
            RefreshReachability();
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
