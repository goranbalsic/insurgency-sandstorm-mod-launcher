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
            InitModioCommands();
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
        /// <summary>The ports the server takes: the player's own -Port/-QueryPort when it starts with their options.</summary>
        private int JoinPort => serverPlan?.OwnArgs != null && serverPlan.GamePort > 0 ? serverPlan.GamePort : State.Settings.ServerPort;
        private int JoinQueryPort => serverPlan?.OwnArgs != null && serverPlan.QueryPort > 0 ? serverPlan.QueryPort : State.Settings.ServerQueryPort;
        /// <summary>What a player on the same network types into the game's console to join.</summary>
        public string ServerJoinCommand => lanAddress == null ? "" : "open " + lanAddress + ":" + JoinPort.ToString(CultureInfo.InvariantCulture);
        public string ServerJoinText => lanAddress == null ? T("No network connection found on this PC.") : ServerJoinCommand;
        public string ServerPortsText => F("UDP {0} (game) and UDP {1} (query)", JoinPort, JoinQueryPort)
                                         + (State.Settings.ServerRconFromNetwork ? " · " + F("TCP {0} for RCON from other PCs", State.Settings.ServerRconPort) : "");
        public string ServerInternetText => F("For players on the internet, forward {0} on your router to this PC ({1}). They find \"{2}\" in the game's server browser.",
            F("UDP {0} and {1}", JoinPort, JoinQueryPort), ServerLanAddress,
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

        // ------------------------------------------------------------------ the player's own start options

        private string ownArgsNote = "";

        /// <summary>Start the server with the options from the player's own .bat file instead of the launcher's.</summary>
        public bool ServerUseOwnArgs
        {
            get => State.Settings.ServerUseOwnArgs;
            set { SetServerSetting(() => State.Settings.ServerUseOwnArgs = value); Raise(nameof(ServerUsesLauncherArgs)); }
        }
        public bool ServerUsesLauncherArgs => !State.Settings.ServerUseOwnArgs;
        public string ServerOwnArgs { get => State.Settings.ServerOwnArgs; set => SetServerSetting(() => State.Settings.ServerOwnArgs = value ?? ""); }
        public string OwnArgsNote { get => ownArgsNote; private set => Set(ref ownArgsNote, value ?? ""); }

        /// <summary>The server loads mods: the Mods switch, or -Mods in the player's own options.</summary>
        public bool ServerModsOn => State.Settings.ServerUseOwnArgs ? ServerArgs.Has(ServerArgs.Clean(State.Settings.ServerOwnArgs), "Mods") : State.Settings.ServerModsEnabled;

        private void ImportServerBat()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Your server's .bat file"),
                Filter = T("Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*"),
                InitialDirectory = ServerInstall.Root != null && Directory.Exists(ServerInstall.Root) ? ServerInstall.Root : null,
            };
            if (dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;
            string text;
            try { text = File.ReadAllText(dlg.FileName); }
            catch (Exception ex) { ShowToast(F("The file could not be read: {0}", ex.Message)); return; }
            var notes = new List<string>();
            string args = ServerArgs.FromBatch(text, notes);
            if (string.IsNullOrWhiteSpace(args)) { ShowToast(F("No line in {0} starts InsurgencyServer", Path.GetFileName(dlg.FileName))); return; }
            ServerOwnArgs = args;
            ServerUseOwnArgs = true;
            OwnArgsNote = notes.Count > 0 ? string.Join(" ", notes.Distinct()) : F("From {0}.", Path.GetFileName(dlg.FileName));
            ShowToast("The server starts with your own options now");
        }

        // ------------------------------------------------------------------ the server's mod.io login and subscriptions

        public ICommand ImportServerBatCommand { get; private set; }
        public ICommand SendModioCodeCommand { get; private set; }
        public ICommand ImportModsTxtCommand { get; private set; }
        public ICommand SubscribeServerModsCommand { get; private set; }
        public ICommand UnsubscribeOtherModsCommand { get; private set; }
        public ICommand OpenModioCommand { get; private set; }

        private ServerModioAccount serverAccount = new ServerModioAccount();
        /// <summary>Subscriptions changed from here: newer than the server's own copy until it starts again and saves one.</summary>
        private List<long> changedSubs;
        private DateTime changedSubsFileTime;
        private List<long> otherSubs = new List<long>();
        private bool modioBusy;
        private string modioText = "", modSyncText = "";

        private void InitModioCommands()
        {
            ImportServerBatCommand = new RelayCommand(ImportServerBat, () => !serverBusy);
            SendModioCodeCommand = new AsyncCommand(SendModioCode, () => !modioBusy && ServerModio.IsEmail(State.Settings.ServerModioEmail));
            ImportModsTxtCommand = new RelayCommand(ImportModsTxt);
            SubscribeServerModsCommand = new AsyncCommand(SubscribeServerMods, () => !modioBusy && serverAccount.LoggedIn);
            UnsubscribeOtherModsCommand = new AsyncCommand(UnsubscribeOtherMods, () => !modioBusy && serverAccount.LoggedIn && otherSubs.Count > 0);
            OpenModioCommand = new RelayCommand(() => Open("https://mod.io/g/insurgencysandstorm"));
        }

        private static string UserFile => Path.Combine(ServerModio.UserDataDir, ServerModio.ProfileName, "user.json");

        /// <summary>Reads the server's mod.io login again (it changes when the server logs in or starts).</summary>
        private void ReadServerAccount()
        {
            var acc = ServerModio.ReadAccount();
            if (changedSubs != null)
            {
                DateTime t = File.Exists(UserFile) ? File.GetLastWriteTimeUtc(UserFile) : DateTime.MinValue;
                if (t <= changedSubsFileTime) acc.Subscriptions = changedSubs.ToList();
                else changedSubs = null;
            }
            bool changed = acc.LoggedIn != serverAccount.LoggedIn || acc.UserId != serverAccount.UserId || acc.SameAsGame != serverAccount.SameAsGame
                           || !acc.Subscriptions.SequenceEqual(serverAccount.Subscriptions) || acc.Expired != serverAccount.Expired || acc.Problem != serverAccount.Problem
                           || acc.ExpiresUtc != serverAccount.ExpiresUtc || acc.UserName != serverAccount.UserName;
            serverAccount = acc;
            if (!changed) return;
            RaiseMany(nameof(ModioAccountText), nameof(ModioAccountKind), nameof(ServerModsOn));
            UpdateServerPlan();
            CommandManager.InvalidateRequerySuggested();
        }

        public string ModioAccountText
        {
            get
            {
                var a = serverAccount;
                if (a.Problem != null) return F("The server's mod.io login could not be read: {0}", a.Problem);
                if (a.LoggedIn)
                {
                    string who = string.IsNullOrEmpty(a.UserName) ? T("its own account") : a.UserName;
                    string text = a.ExpiresUtc.HasValue ? F("Logged in as {0} until {1}", who, a.ExpiresUtc.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : F("Logged in as {0}", who);
                    text += " · " + (a.Subscriptions.Count == 1 ? T("subscribed to 1 mod") : F("subscribed to {0} mods", a.Subscriptions.Count));
                    return a.SameAsGame ? text + "\n" + T("This is the account your game uses: mod.io gives the server nothing that way. Log it in with an account of its own.") : text;
                }
                if (ServerModio.CleanCode(State.Settings.ServerModioCode) != null) return T("A code is waiting: the server logs in with it when it starts next.");
                return a.Expired ? T("The login has expired: send a new code.") : T("Not logged in yet.");
            }
        }

        /// <summary>Match (logged in with its own account), Busy (a code waits), Bad (mods on without a login) or Off.</summary>
        public string ModioAccountKind => serverAccount.LoggedIn && !serverAccount.SameAsGame ? "Match"
            : ServerModio.CleanCode(State.Settings.ServerModioCode) != null ? "Busy" : ServerModsOn ? "Bad" : "Off";

        public string ServerModioEmail
        {
            get => State.Settings.ServerModioEmail;
            set { State.Settings.ServerModioEmail = (value ?? "").Trim(); Raise(); SaveSettingsSoon(); CommandManager.InvalidateRequerySuggested(); }
        }

        /// <summary>The security code from mod.io's e-mail: used at the next start, then dropped.</summary>
        public string ServerModioCode
        {
            get => State.Settings.ServerModioCode;
            set
            {
                State.Settings.ServerModioCode = (value ?? "").Trim();
                RaiseMany(nameof(ServerModioCode), nameof(ModioCodeHint), nameof(ModioAccountText), nameof(ModioAccountKind));
                SaveSettingsSoon();
                UpdateServerPlan();
            }
        }

        public string ModioCodeHint => string.IsNullOrWhiteSpace(State.Settings.ServerModioCode) ? ""
            : ServerModio.CleanCode(State.Settings.ServerModioCode) == null ? T("A code is 5 digits.")
            : T("Start the server now: it logs in with the code, and the code is used up.");

        public bool ModioBusy { get => modioBusy; private set { if (Set(ref modioBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
        /// <summary>The answer to the last code request.</summary>
        public string ModioText { get => modioText; private set => Set(ref modioText, value ?? ""); }
        /// <summary>The answer to the last subscribe, or which listed mods the account lacks.</summary>
        public string ModSyncText { get => modSyncText; private set => Set(ref modSyncText, value ?? ""); }
        public bool HasOtherSubs => otherSubs.Count > 0;

        /// <summary>Which listed mods the server's account lacks (from the server's own copy of its subscriptions).</summary>
        public string ServerModsListText
        {
            get
            {
                if (!serverAccount.LoggedIn) return "";
                var wanted = WantedMods();
                var missing = wanted.Where(id => !serverAccount.Subscriptions.Contains(id)).ToList();
                if (missing.Count > 0) return F("Not subscribed yet: {0}", string.Join(", ", missing));
                return wanted.Count > 0 ? T("The server's account is subscribed to every mod on the list.") : "";
            }
        }

        private async Task SendModioCode()
        {
            string email = State.Settings.ServerModioEmail;
            ModioBusy = true;
            ModioText = T("Asking mod.io...");
            try
            {
                string error = await Task.Run(() => ServerModio.RequestCode(email));
                ModioText = error == null ? F("mod.io sent a code to {0}. Type it below, then start the server.", email) : error;
            }
            finally { ModioBusy = false; }
        }

        private void ImportModsTxt()
        {
            string dir = ServerInstall.ServerConfigDir;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Your Mods.txt"),
                Filter = T("Mod lists (*.txt)|*.txt|All files (*.*)|*.*"),
                InitialDirectory = dir != null && Directory.Exists(dir) ? dir : null,
                FileName = dir != null && File.Exists(Path.Combine(dir, "Mods.txt")) ? "Mods.txt" : "",
            };
            if (dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;
            var bad = new List<string>();
            List<long> ids;
            try { ids = ServerModio.ReadModsTxt(dlg.FileName, bad); }
            catch (Exception ex) { ShowToast(F("The file could not be read: {0}", ex.Message)); return; }
            if (ids == null || ids.Count == 0) { ShowToast("No mod ids in that file"); return; }
            var have = ServerPlanner.ModIds(State.Settings.ServerMods, null);
            var add = ids.Where(i => !have.Contains(i)).ToList();
            string text = (State.Settings.ServerMods ?? "").TrimEnd();
            if (add.Count > 0) ServerModIds = (text.Length > 0 ? text + "\n" : "") + string.Join("\n", add.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            ShowToast(add.Count == 0 ? T("The list already has every mod of that file") : F("{0} mod ids added: press Subscribe so the server loads them", add.Count));
        }

        /// <summary>The mods the server should have: the list, and in the launcher's own start the match's mods.</summary>
        private List<long> WantedMods()
        {
            var ids = ServerPlanner.ModIds(State.Settings.ServerMods, null);
            if (!State.Settings.ServerUseOwnArgs && serverPlan != null) foreach (var id in serverPlan.MatchModIds) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        /// <summary>Subscribes the server's mod.io account to every listed mod it lacks (its subscriptions are what it loads).</summary>
        private async Task SubscribeServerMods()
        {
            var wanted = WantedMods();
            if (wanted.Count == 0) { ModSyncText = T("The list is empty: add mod ids (or import Mods.txt) first."); return; }
            ModioBusy = true;
            ModSyncText = T("Asking mod.io...");
            try
            {
                var r = await Task.Run(() =>
                {
                    var have = ServerModio.Subscribed(out string error);
                    if (have == null) return (error, (List<long>)null, new List<long>(), new List<string>());
                    var added = new List<long>();
                    var failed = new List<string>();
                    foreach (var id in wanted.Where(i => !have.Contains(i)))
                    {
                        string e = ServerModio.Subscribe(id);
                        if (e == null) added.Add(id); else failed.Add(id.ToString(CultureInfo.InvariantCulture) + ": " + e);
                    }
                    return ((string)null, have.Concat(added).Distinct().ToList(), added, failed);
                });
                if (r.Item1 != null) { ModSyncText = r.Item1; return; }
                RememberSubs(r.Item2, wanted);
                var parts = new List<string>
                {
                    r.Item3.Count == 0 && r.Item4.Count == 0 ? T("The server's account is subscribed to every mod on the list.") : F("Subscribed the server to {0} mods.", r.Item3.Count)
                };
                if (r.Item4.Count > 0) parts.Add(F("Not subscribed: {0}", string.Join("; ", r.Item4)));
                if (otherSubs.Count > 0) parts.Add(F("It is also subscribed to {0} more: {1}.", otherSubs.Count, string.Join(", ", otherSubs)));
                if (r.Item3.Count > 0) parts.Add(T("The server picks them up when it starts next."));
                ModSyncText = string.Join(" ", parts);
                AppLog.Info("Server mod.io subscriptions: " + r.Item3.Count + " added, " + r.Item4.Count + " failed");
            }
            finally { ModioBusy = false; }
        }

        /// <summary>Takes the server's account off the mods it is subscribed to that are not on the list.</summary>
        private async Task UnsubscribeOtherMods()
        {
            var drop = otherSubs.ToList();
            if (await Ask("Unsubscribe the server?", F("The server's mod.io account is subscribed to {0} mods that are not on the list ({1}). Unsubscribe it, so the server no longer loads them?",
                    drop.Count, string.Join(", ", drop)), "Unsubscribe") != "Unsubscribe") return;
            ModioBusy = true;
            ModSyncText = T("Asking mod.io...");
            try
            {
                var failed = await Task.Run(() => drop.Select(id => (id, ServerModio.Unsubscribe(id))).Where(x => x.Item2 != null).ToList());
                var kept = failed.Select(f => f.id).ToList();
                RememberSubs(serverAccount.Subscriptions.Where(id => !drop.Contains(id) || kept.Contains(id)).ToList(), WantedMods());
                ModSyncText = failed.Count == 0 ? F("Unsubscribed from {0} mods. The server drops them when it starts next.", drop.Count)
                                                : F("Not unsubscribed: {0}", string.Join("; ", failed.Select(f => f.id.ToString(CultureInfo.InvariantCulture) + ": " + f.Item2)));
            }
            finally { ModioBusy = false; }
        }

        private void RememberSubs(List<long> subs, List<long> wanted)
        {
            changedSubs = subs;
            changedSubsFileTime = File.Exists(UserFile) ? File.GetLastWriteTimeUtc(UserFile) : DateTime.MinValue;
            otherSubs = subs.Where(id => !wanted.Contains(id)).ToList();
            Raise(nameof(HasOtherSubs));
            serverAccount = new ServerModioAccount();   // read again below, with the new subscriptions
            ReadServerAccount();
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
            bool on = ServerModsOn;
            if (!ServerRunningHere)
            {
                ServerModText = on && !automatic ? T("The server is not running.") : "";
                ServerModKind = "Off";
                lastModVerdict = ModVerdict.NotAsked;
                return;
            }
            if (!on && automatic) { ServerModText = ""; return; }
            bool just = DateTime.UtcNow - serverStartedUtc < TimeSpan.FromSeconds(50);
            ReadServerAccount();
            var account = serverAccount;
            var st = await Task.Run(() => Server.ModStatus(on, just, account));
            if (st.Verdict != lastModVerdict && (st.Verdict == ModVerdict.NoLogin || st.Verdict == ModVerdict.LoginFailed || st.Verdict == ModVerdict.NoMods))
                AppLog.Warn("Server mods: " + st.Verdict + (st.LoginError != null ? " (" + st.LoginError + ")" : ""));
            lastModVerdict = st.Verdict;
            ServerModText = on || !automatic ? st.Text : "";
            ServerModKind = st.Verdict == ModVerdict.Loaded ? "Match" : st.Verdict == ModVerdict.NoLogin || st.Verdict == ModVerdict.LoginFailed || st.Verdict == ModVerdict.NoMods ? "Bad"
                          : st.Verdict == ModVerdict.Waiting || st.Verdict == ModVerdict.Downloading ? "Busy" : "Off";
        }

        /// <summary>After a start with mods on: looks at the log a little later, when the server has had time to log in to mod.io.</summary>
        private int modWatchRun;

        private async void WatchServerMods(bool modsOn)
        {
            int run = ++modWatchRun;
            if (!modsOn) { ServerModText = ""; return; }
            serverStartedUtc = DateTime.UtcNow;
            ServerModText = T("Waiting for the server to report its mods...");
            ServerModKind = "Busy";
            try
            {
                // A newer start has its own watch: this one stops, or it would judge the new server too early and toast twice.
                await Task.Delay(TimeSpan.FromSeconds(35));
                if (run != modWatchRun) return;
                await CheckServerMods(true);
                await Task.Delay(TimeSpan.FromSeconds(40));
                if (run != modWatchRun) return;
                await CheckServerMods(true);
                if (lastModVerdict == ModVerdict.NoLogin || lastModVerdict == ModVerdict.LoginFailed || lastModVerdict == ModVerdict.NoMods) ShowToast("The server started, but it has no mods: see the Mods card");
            }
            catch (Exception ex) { AppLog.Warn("Server mod check: " + ex.Message); }
        }

        // ------------------------------------------------------------------ options from the admin guide

        public bool ServerVoteKick { get => State.Settings.ServerVoteKick; set => SetServerSetting(() => State.Settings.ServerVoteKick = value); }
        public bool ServerOfficialRules { get => State.Settings.ServerOfficialRules; set => SetServerSetting(() => State.Settings.ServerOfficialRules = value); }
        public bool ServerOwnRules { get => State.Settings.ServerOwnRules; set => SetServerSetting(() => State.Settings.ServerOwnRules = value); }

        private void RaiseServerSetup()
        {
            missingRuntime = ServerInstall.MissingRuntime();
            RaiseMany(nameof(ServerInstallDir), nameof(ServerCanUpdate), nameof(ServerUpdatedBySteam), nameof(ServerNotInstalled), nameof(RuntimeMissing), nameof(RuntimeMissingText),
                      nameof(ServerVoteKick), nameof(ServerOfficialRules), nameof(ServerOwnRules));
            RefreshReachability();
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
