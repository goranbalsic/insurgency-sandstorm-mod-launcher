using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed class MapCycleItem
    {
        public MapCycleEntry Entry { get; set; }
        public int Number { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public bool IsEntry => Entry.IsEntry;
        public bool IsNight => string.Equals(Entry.Lighting, "Night", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ServerPlayerItem
    {
        public ServerPlayer Player { get; set; }
        public string Name => Player.Name;
        public string Score => Player.Score;
    }

    /// <summary>The Server page: a dedicated server on this PC (files, start, stop) or another PC's (RCON only), and its live control.</summary>
    public sealed partial class MainViewModel
    {
        public ServerInstall ServerInstall { get; private set; } = new ServerInstall();
        public ServerService Server { get; private set; }
        public ObservableCollection<MapCycleItem> MapCycleItems { get; } = new ObservableCollection<MapCycleItem>();
        public ObservableCollection<ServerPlayerItem> ServerPlayers { get; } = new ObservableCollection<ServerPlayerItem>();
        public ObservableCollection<string> ServerWarnings { get; } = new ObservableCollection<string>();
        private ServerPlan serverPlan;
        private DispatcherTimer serverTimer;
        private bool serverBusy, remoteReachable;
        private string serverStatus = T("Not running"), serverStatusKind = "Off", serverOutput = "", sayText = "", serverCommand = "", modioTokenInput = "", serverProgress = "";
        private readonly HashSet<string> backedUpCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ICommand StartServerCommand { get; private set; }
        public ICommand StopServerCommand { get; private set; }
        public ICommand RestartServerCommand { get; private set; }
        public ICommand ChangeServerDirCommand { get; private set; }
        public ICommand FindServerDirCommand { get; private set; }
        public ICommand OpenServerPathCommand { get; private set; }
        public ICommand CopyServerCommandLineCommand { get; private set; }
        public ICommand NewServerRconPasswordCommand { get; private set; }
        public ICommand CopyServerRconPasswordCommand { get; private set; }
        public ICommand AddMatchToCycleCommand { get; private set; }
        public ICommand RemoveCycleItemCommand { get; private set; }
        public ICommand MoveCycleItemUpCommand { get; private set; }
        public ICommand MoveCycleItemDownCommand { get; private set; }
        public ICommand ToggleCycleLightingCommand { get; private set; }
        public ICommand ChooseMapCycleFileCommand { get; private set; }
        public ICommand DefaultMapCycleFileCommand { get; private set; }
        public ICommand AddMatchModsCommand { get; private set; }
        public ICommand SaveModioTokenCommand { get; private set; }
        public ICommand RefreshPlayersCommand { get; private set; }
        public ICommand KickPlayerCommand { get; private set; }
        public ICommand BanPlayerCommand { get; private set; }
        public ICommand SayCommand { get; private set; }
        public ICommand TravelServerCommand { get; private set; }
        public ICommand RestartServerRoundCommand { get; private set; }
        public ICommand SendServerCommandCommand { get; private set; }
        public ICommand CheckServerConnectionCommand { get; private set; }

        private void InitServerCommands()
        {
            StartServerCommand = new AsyncCommand(StartServer, () => CanStartServer);
            StopServerCommand = new AsyncCommand(StopServer, () => !serverBusy && ServerRunningHere);
            RestartServerCommand = new AsyncCommand(RestartServer, () => !serverBusy && ServerRunningHere && serverPlan?.IsValid == true);
            ChangeServerDirCommand = new AsyncCommand(ChangeServerDir, () => !serverBusy);
            FindServerDirCommand = new RelayCommand(() => { State.Settings.ServerDirOverride = ""; ReloadServerInstall(); }, () => !serverBusy);
            OpenServerPathCommand = new RelayCommand(p => OpenServerPath(p as string));
            CopyServerCommandLineCommand = new RelayCommand(CopyServerCommandLine, () => serverPlan?.IsValid == true);
            NewServerRconPasswordCommand = new AsyncCommand(NewServerRconPassword, () => !serverBusy);
            CopyServerRconPasswordCommand = new RelayCommand(() => CopyText(ServerRemote ? State.Settings.ServerRemoteRconPassword : State.Settings.ServerRconPassword, "RCON password copied"));
            AddMatchToCycleCommand = new RelayCommand(AddMatchToCycle, () => CurrentPlan?.IsValid == true && MapCycleFile != null);
            RemoveCycleItemCommand = new RelayCommand(p => EditCycle(p as MapCycleItem, (list, i) => list.RemoveAt(i)));
            MoveCycleItemUpCommand = new RelayCommand(p => EditCycle(p as MapCycleItem, (list, i) => { if (i > 0) { var e = list[i]; list.RemoveAt(i); list.Insert(i - 1, e); } }));
            MoveCycleItemDownCommand = new RelayCommand(p => EditCycle(p as MapCycleItem, (list, i) => { if (i < list.Count - 1) { var e = list[i]; list.RemoveAt(i); list.Insert(i + 1, e); } }));
            ToggleCycleLightingCommand = new RelayCommand(p => EditCycle(p as MapCycleItem, (list, i) =>
            {
                if (!list[i].IsEntry || list[i].Raw != null) return;
                list[i].Lighting = string.Equals(list[i].Lighting, "Night", StringComparison.OrdinalIgnoreCase) ? "Day" : "Night";
            }));
            ChooseMapCycleFileCommand = new RelayCommand(ChooseMapCycleFile);
            DefaultMapCycleFileCommand = new RelayCommand(() => { State.Settings.ServerMapCycleFile = ""; SaveSettingsSoon(); LoadMapCycle(); UpdateServerPlan(); }, () => !string.IsNullOrWhiteSpace(State.Settings.ServerMapCycleFile));
            AddMatchModsCommand = new RelayCommand(AddMatchMods, () => serverPlan != null && serverPlan.MatchModIds.Count > 0);
            SaveModioTokenCommand = new RelayCommand(SaveModioToken, () => ServerInstall.Found && !string.IsNullOrWhiteSpace(modioTokenInput));
            RefreshPlayersCommand = new AsyncCommand(() => RefreshPlayers(false), () => CanControlServer);
            KickPlayerCommand = new AsyncCommand(p => KickPlayer(p as ServerPlayerItem), p => CanControlServer);
            BanPlayerCommand = new AsyncCommand(p => BanPlayer(p as ServerPlayerItem), p => CanControlServer);
            SayCommand = new AsyncCommand(Say, () => CanControlServer && !string.IsNullOrWhiteSpace(sayText));
            TravelServerCommand = new AsyncCommand(TravelServer, () => CanControlServer && serverPlan?.IsValid == true);
            RestartServerRoundCommand = new AsyncCommand(p => RestartServerRound(p as string == "1"), p => CanControlServer);
            SendServerCommandCommand = new AsyncCommand(SendServerCommand, () => CanControlServer && !string.IsNullOrWhiteSpace(serverCommand));
            CheckServerConnectionCommand = new AsyncCommand(() => CheckServerConnection(true), () => !serverBusy);
        }

        /// <summary>After the settings are loaded: find the server, watch it and read its map cycle.</summary>
        private void InitServer()
        {
            if (RconSetup.EnsureServerSettings(State.Settings)) SaveSettingsSoon();
            ServerInstall = ServerInstall.Detect(State.Settings.ServerDirOverride);
            AppLog.Info("Dedicated server: " + (ServerInstall.Root ?? "not found"));
            Server = new ServerService(() => State.Settings, () => ServerInstall);
            WatchServer();
            serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            serverTimer.Tick += async (s, e) =>
            {
                if (Page != "Server" || serverBusy) return;
                if (ServerRemote) await CheckServerConnection(false);
                else if (ServerRunningHere && Server.Monitor?.Phase == GamePhase.InMatch) await RefreshPlayers(true);
            };
            serverTimer.Start();
            LoadMapCycle();
            UpdateServerPlan();
            RaiseServer();
        }

        private void WatchServer()
        {
            var m = Server.Watch();
            if (m != null) m.StateChanged += () => ui.BeginInvoke(new Action(OnServerStateChanged));
            OnServerStateChanged();
        }

        private void ReloadServerInstall()
        {
            ServerInstall = ServerInstall.Detect(State.Settings.ServerDirOverride);
            SaveSettingsSoon();
            WatchServer();
            LoadMapCycle();
            UpdateServerPlan();
            RaiseServer();
        }

        private void RaiseServer()
        {
            RaiseMany(nameof(ServerFound), nameof(ServerDir), nameof(ServerBuild), nameof(ServerRemote), nameof(ServerLocal), nameof(ServerName), nameof(ServerJoinPassword),
                      nameof(ServerMaxPlayersText), nameof(ServerPortText), nameof(ServerQueryPortText), nameof(ServerRconPortText), nameof(ServerRconFromNetwork),
                      nameof(ServerShowLog), nameof(ServerCheats), nameof(ServerGslt), nameof(ServerGameStats), nameof(ServerExtraArgs), nameof(ServerRemoteHost),
                      nameof(ServerRemotePortText), nameof(ServerRemotePassword), nameof(ServerUseMapCycle), nameof(MapCycleFile), nameof(MapCycleIsDefault),
                      nameof(ServerAdmins), nameof(ServerModsEnabled), nameof(ServerModIds), nameof(ModioTokenStatus), nameof(ServerRconAddress));
            OnServerStateChanged();
        }

        // ------------------------------------------------------------------ status

        public bool ServerFound => ServerInstall.Found;
        public string ServerDir => ServerInstall.Root ?? T("Not found");
        public string ServerBuild => ServerInstall.BuildId.Length > 0 ? F("Build {0}", ServerInstall.BuildId) : "";
        public bool ServerRunningHere => !ServerRemote && Server?.Monitor?.IsRunning == true;
        public bool ServerBusy { get => serverBusy; private set { if (Set(ref serverBusy, value)) { RaiseMany(nameof(CanStartServer), nameof(CanControlServer)); RaiseMainAction(); } } }
        public string ServerStatus { get => serverStatus; private set => Set(ref serverStatus, value); }
        public string ServerStatusKind { get => serverStatusKind; private set => Set(ref serverStatusKind, value); }
        public string ServerProgress { get => serverProgress; private set => Set(ref serverProgress, value); }
        public string ServerOutput { get => serverOutput; set => Set(ref serverOutput, value); }
        public bool CanStartServer => !serverBusy && !ServerRemote && ServerInstall.Found && !ServerRunningHere && serverPlan?.IsValid == true;
        /// <summary>A server to talk to: the one here while it runs, or the other PC's.</summary>
        public bool CanControlServer => !serverBusy && (ServerRemote ? !string.IsNullOrWhiteSpace(State.Settings.ServerRemoteHost) : ServerRunningHere && Server?.Monitor?.Phase == GamePhase.InMatch);
        public string ServerRconAddress => ServerRemote ? (State.Settings.ServerRemoteHost ?? "") + ":" + State.Settings.ServerRemoteRconPort
                                                        : (State.Settings.ServerRconFromNetwork ? T("every network card") : RconSetup.Address) + ":" + State.Settings.ServerRconPort;

        private void OnServerStateChanged()
        {
            var m = Server?.Monitor;
            if (ServerRemote)
            {
                ServerStatus = string.IsNullOrWhiteSpace(State.Settings.ServerRemoteHost) ? T("Enter the other PC's address") : remoteReachable ? F("Connected to {0}", State.Settings.ServerRemoteHost) : T("Not connected");
                ServerStatusKind = remoteReachable ? "Match" : "Off";
            }
            else if (!ServerInstall.Found) { ServerStatus = T("Server not found"); ServerStatusKind = "Bad"; }
            else if (m == null || !m.IsRunning) { ServerStatus = T("Not running"); ServerStatusKind = "Off"; ServerPlayers.Clear(); }
            else if (m.Phase == GamePhase.InMatch)
            {
                string level = m.CurrentLevel == null ? "" : m.CurrentLevel.Substring(m.CurrentLevel.LastIndexOf('/') + 1);
                ServerStatus = T("Running") + (level.Length > 0 ? " · " + level : "") + (m.ListeningPort > 0 ? " · " + F("port {0}", m.ListeningPort) : "") + " · "
                               + (ServerPlayers.Count == 1 ? T("1 player") : F("{0} players", ServerPlayers.Count));
                ServerStatusKind = "Match";
            }
            // A server that has been listening is changing maps; otherwise it is still starting.
            else { ServerStatus = m.ListeningPort > 0 ? T("Changing the map...") : T("Starting..."); ServerStatusKind = "Busy"; }
            RaiseMany(nameof(ServerRunningHere), nameof(CanStartServer), nameof(CanControlServer));
            RaiseMainAction();
        }

        // ------------------------------------------------------------------ settings

        private void SetServerSetting(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            apply();
            Raise(name);
            SaveSettingsSoon();
            UpdateServerPlan();
        }

        private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>A number box: a value outside the range is not taken (the box shows the old one again).</summary>
        private void SetNumber(string text, int min, int max, Action<int> apply, string name)
        {
            if (int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max) { apply(n); SaveSettingsSoon(); UpdateServerPlan(); }
            else ShowToast(F("Enter a number from {0} to {1}", min, max));
            Raise(name);
        }

        public bool ServerRemote
        {
            get => State.Settings.ServerRemote;
            set { SetServerSetting(() => State.Settings.ServerRemote = value); remoteReachable = false; Raise(nameof(ServerLocal)); Raise(nameof(ServerRconAddress)); OnServerStateChanged(); if (value) _ = CheckServerConnection(false); }
        }
        public bool ServerLocal { get => !ServerRemote; set => ServerRemote = !value; }
        public string ServerName { get => State.Settings.ServerName; set => SetServerSetting(() => State.Settings.ServerName = value ?? ""); }
        public string ServerJoinPassword { get => State.Settings.ServerPassword; set => SetServerSetting(() => State.Settings.ServerPassword = (value ?? "").Trim()); }
        public string ServerMaxPlayersText { get => Num(State.Settings.ServerMaxPlayers); set => SetNumber(value, 1, 100, n => State.Settings.ServerMaxPlayers = n, nameof(ServerMaxPlayersText)); }
        public string ServerPortText { get => Num(State.Settings.ServerPort); set => SetNumber(value, 1, 65535, n => State.Settings.ServerPort = n, nameof(ServerPortText)); }
        public string ServerQueryPortText { get => Num(State.Settings.ServerQueryPort); set => SetNumber(value, 1, 65535, n => State.Settings.ServerQueryPort = n, nameof(ServerQueryPortText)); }
        public string ServerRconPortText
        {
            get => Num(State.Settings.ServerRconPort);
            set { SetNumber(value, 1024, 65535, n => State.Settings.ServerRconPort = n, nameof(ServerRconPortText)); Raise(nameof(ServerRconAddress)); }
        }
        public bool ServerRconFromNetwork { get => State.Settings.ServerRconFromNetwork; set { SetServerSetting(() => State.Settings.ServerRconFromNetwork = value); Raise(nameof(ServerRconAddress)); } }
        public bool ServerShowLog { get => State.Settings.ServerShowLog; set => SetServerSetting(() => State.Settings.ServerShowLog = value); }
        public bool ServerCheats { get => State.Settings.ServerCheats; set => SetServerSetting(() => State.Settings.ServerCheats = value); }
        public string ServerGslt { get => State.Settings.ServerGslt; set => SetServerSetting(() => State.Settings.ServerGslt = (value ?? "").Trim()); }
        public bool ServerGameStats { get => State.Settings.ServerGameStats; set => SetServerSetting(() => State.Settings.ServerGameStats = value); }
        public string ServerExtraArgs { get => State.Settings.ServerExtraArgs; set => SetServerSetting(() => State.Settings.ServerExtraArgs = value ?? ""); }
        public string ServerRemoteHost
        {
            get => State.Settings.ServerRemoteHost;
            set { SetServerSetting(() => State.Settings.ServerRemoteHost = (value ?? "").Trim()); remoteReachable = false; Raise(nameof(ServerRconAddress)); OnServerStateChanged(); }
        }
        public string ServerRemotePortText
        {
            get => Num(State.Settings.ServerRemoteRconPort);
            set { SetNumber(value, 1, 65535, n => State.Settings.ServerRemoteRconPort = n, nameof(ServerRemotePortText)); remoteReachable = false; Raise(nameof(ServerRconAddress)); OnServerStateChanged(); }
        }
        public string ServerRemotePassword { get => State.Settings.ServerRemoteRconPassword; set { SetServerSetting(() => State.Settings.ServerRemoteRconPassword = value ?? ""); remoteReachable = false; OnServerStateChanged(); } }
        public string ServerAdmins { get => State.Settings.ServerAdmins; set => SetServerSetting(() => State.Settings.ServerAdmins = value ?? ""); }
        public bool ServerModsEnabled { get => State.Settings.ServerModsEnabled; set => SetServerSetting(() => State.Settings.ServerModsEnabled = value); }
        public string ServerModIds { get => State.Settings.ServerMods; set => SetServerSetting(() => State.Settings.ServerMods = value ?? ""); }
        public string ModioTokenInput { get => modioTokenInput; set => Set(ref modioTokenInput, value ?? ""); }
        public string ModioTokenStatus => !ServerInstall.Found ? "" : Server?.HasModioToken() == true
            ? T("A mod.io token is saved in the server's Engine.ini.") : T("No mod.io token yet: the server cannot download mods without one.");
        public string SayText { get => sayText; set => Set(ref sayText, value ?? ""); }
        public string ServerCommand { get => serverCommand; set => Set(ref serverCommand, value ?? ""); }

        // ------------------------------------------------------------------ plan

        private void UpdateServerPlan()
        {
            if (Server == null) return;
            try { serverPlan = CurrentPlan == null ? null : ServerPlanner.Build(CurrentPlan, State.Settings, ServerInstall, State.Rules, ""); }
            catch (Exception ex) { AppLog.Error("Could not build the server plan", ex); serverPlan = null; }
            ServerWarnings.Clear();
            if (serverPlan != null) foreach (var w in serverPlan.Warnings.Distinct()) ServerWarnings.Add(w);
            RaiseMany(nameof(ServerCommandLine), nameof(ServerPlanError), nameof(ServerMatchSummary), nameof(ServerMatchMods), nameof(CanStartServer));
            RaiseMainAction();
        }

        public string ServerCommandLine => serverPlan?.IsValid == true ? "InsurgencyServer.exe " + serverPlan.ShownCommandLine : "";
        public string ServerPlanError => ServerRemote ? null : serverPlan == null ? T("Pick a map and scenario in Play first.") : serverPlan.Error;
        public string ServerMatchSummary => CurrentPlan?.IsValid != true ? T("No match set up in Play yet")
            : MapSummary + " · " + ModeSummary + " · " + RulesSummary + " · " + MutatorSummary;
        public string ServerMatchMods => serverPlan == null || serverPlan.MatchModIds.Count == 0 ? "" : F("This match uses mods {0}.", string.Join(", ", serverPlan.MatchModIds));

        // ------------------------------------------------------------------ start / stop

        private async Task<bool> RunServer()
        {
            string ini = File.Exists(ServerInstall.GameIniPath) ? UeIni.ReadText(ServerInstall.GameIniPath) : "";
            var plan = ServerPlanner.Build(CurrentPlan, State.Settings, ServerInstall, State.Rules, ini);
            if (!plan.IsValid) { await ShowMessage("The server cannot start", plan.Error); return false; }
            var progress = new Progress<string>(t => ServerProgress = t);
            string failed = await Server.Start(plan, progress, CancellationToken.None);
            ServerProgress = "";
            SaveSettingsSoon();
            if (failed != null) { await ShowMessage("The server did not start", failed); return false; }
            ShowToast(F("Server running: {0}", plan.Match.Map?.DisplayName ?? plan.Match.Level));
            await RefreshPlayers(true);
            return true;
        }

        private async Task StartServer()
        {
            ServerBusy = true;
            try { await RunServer(); }
            catch (Exception ex) { AppLog.Error("Server start failed", ex); await ShowMessage("The server did not start", ex.Message); }
            finally { ServerBusy = false; OnServerStateChanged(); }
        }

        private async Task<bool> StopServerCore()
        {
            ServerProgress = T("Stopping the server");
            bool stopped = await Server.Stop(CancellationToken.None);
            ServerProgress = "";
            if (!stopped)
            {
                string answer = await Ask("The server did not close", "It did not answer the request to close. Close it by force? Players on it are dropped at once.", "Close by force", "Leave it running");
                if (answer == "Close by force") stopped = Server.Kill();
            }
            OnServerStateChanged();
            return stopped;
        }

        private async Task StopServer()
        {
            ServerBusy = true;
            try { if (await StopServerCore()) ShowToast("Server stopped"); }
            finally { ServerBusy = false; OnServerStateChanged(); }
        }

        private async Task RestartServer()
        {
            ServerBusy = true;
            try
            {
                if (!await StopServerCore()) return;
                await Task.Delay(1500);
                await RunServer();
            }
            finally { ServerBusy = false; OnServerStateChanged(); }
        }

        private async Task ChangeServerDir()
        {
            string dir = FolderPicker.Pick(OwnerHandle, "Pick the dedicated server folder (it contains InsurgencyServer.exe)", ServerInstall.Root);
            if (dir == null) return;
            if (!ServerInstall.IsServerDir(dir))
            {
                string parent = Directory.GetParent(dir)?.FullName;
                if (parent != null && ServerInstall.IsServerDir(parent)) dir = parent;
                else { await ShowMessage("That is not the server folder", "Pick the folder with InsurgencyServer.exe in it, usually ...\\steamapps\\common\\sandstorm_server."); return; }
            }
            State.Settings.ServerDirOverride = dir;
            ReloadServerInstall();
        }

        private void OpenServerPath(string what)
        {
            switch (what)
            {
                case "Folder": Open(ServerInstall.Root); break;
                case "Config": Open(ServerInstall.SavedConfigDir != null && Directory.Exists(ServerInstall.SavedConfigDir) ? ServerInstall.SavedConfigDir : ServerInstall.Root); break;
                case "Lists": Open(ServerInstall.ServerConfigDir != null && Directory.Exists(ServerInstall.ServerConfigDir) ? ServerInstall.ServerConfigDir : ServerInstall.Root); break;
                case "Logs": Open(ServerInstall.LogPath == null ? null : Path.GetDirectoryName(ServerInstall.LogPath)); break;
                case "MapCycle": if (MapCycleFile != null && File.Exists(MapCycleFile)) Open(MapCycleFile); break;
            }
        }

        private void CopyServerCommandLine()
        {
            if (serverPlan?.IsValid != true) return;
            CopyText("InsurgencyServer.exe " + serverPlan.CommandLine,
                     string.IsNullOrEmpty(State.Settings.ServerGslt) && string.IsNullOrWhiteSpace(State.Settings.ServerPassword) ? "Command line copied"
                     : "Command line copied (it includes your join password or Steam server token: keep it private)");
        }

        private void CopyText(string text, string toast)
        {
            try { Clipboard.SetText(text ?? ""); ShowToast(toast); }
            catch (Exception ex) { ShowToast(F("Could not copy: {0}", ex.Message)); }
        }

        private async Task NewServerRconPassword()
        {
            if (ServerRemote) return;
            if (await Ask("New RCON password", "Programs and people that use the old password lose access. The server takes the new one when it starts next.", "Make a new one") != "Make a new one") return;
            State.Settings.ServerRconPassword = RconSetup.NewPassword();
            SaveSettingsSoon();
            UpdateServerPlan();
            ShowToast(ServerRunningHere ? "New password saved: restart the server to use it" : "New RCON password saved");
        }

        // ------------------------------------------------------------------ map cycle

        /// <summary>The map cycle file shown and edited: the chosen one, or MapCycle.txt of the server.</summary>
        public string MapCycleFile => ServerPlanner.MapCyclePath(State.Settings, ServerInstall);
        public bool MapCycleIsDefault => string.IsNullOrWhiteSpace(State.Settings.ServerMapCycleFile);
        public bool ServerUseMapCycle { get => State.Settings.ServerUseMapCycle; set => SetServerSetting(() => State.Settings.ServerUseMapCycle = value); }

        private void LoadMapCycle()
        {
            MapCycleItems.Clear();
            string file = MapCycleFile;
            List<MapCycleEntry> entries;
            try { entries = file != null && File.Exists(file) ? MapCycle.Parse(File.ReadAllText(file)) : new List<MapCycleEntry>(); }
            catch (Exception ex) { AppLog.Warn("Map cycle: " + ex.Message); entries = new List<MapCycleEntry>(); }
            int n = 0;
            foreach (var e in entries)
            {
                var item = new MapCycleItem { Entry = e, Number = e.IsEntry ? ++n : 0 };
                if (e.IsEntry)
                {
                    var sc = State.AllScenarios.FirstOrDefault(s => s.Id.Equals(e.Scenario, StringComparison.OrdinalIgnoreCase));
                    var map = sc == null ? null : State.Maps.FirstOrDefault(m => m.Scenarios.Contains(sc));
                    item.Title = sc == null ? e.Scenario : (map?.DisplayName ?? sc.MapKey) + " · " + T(sc.GameModeName) + (string.IsNullOrEmpty(sc.Side) ? "" : " (" + sc.Side + ")");
                    var bits = new List<string> { string.IsNullOrEmpty(e.Lighting) ? "Day" : e.Lighting };
                    if (!string.IsNullOrEmpty(e.Mode)) bits.Add(e.Mode.Equals("CheckpointHardcore", StringComparison.OrdinalIgnoreCase) ? "Hardcore" : e.Mode);
                    if (sc == null) bits.Add(T("not installed here"));
                    if (e.Raw != null) bits.Add(T("kept as written"));
                    item.Detail = string.Join(" · ", bits);
                }
                else { item.Title = e.Raw; item.Detail = T("Kept as written"); }
                MapCycleItems.Add(item);
            }
            RaiseMany(nameof(MapCycleFile), nameof(MapCycleIsDefault), nameof(MapCycleCount));
        }

        public int MapCycleCount => MapCycleItems.Count(i => i.IsEntry);

        /// <summary>Changes the map cycle file (a copy of it is kept first) and shows it again.</summary>
        private void SaveMapCycle(List<MapCycleEntry> entries)
        {
            string file = MapCycleFile;
            if (file == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (File.Exists(file) && backedUpCycles.Add(file)) ConsoleBridge.BackupFile(file, "server-");
                File.WriteAllText(file, MapCycle.Render(entries), new UTF8Encoding(false));
            }
            catch (Exception ex) { ShowToast(F("The map cycle could not be saved: {0}", ex.Message)); }
            LoadMapCycle();
            UpdateServerPlan();
        }

        private void EditCycle(MapCycleItem item, Action<List<MapCycleEntry>, int> change)
        {
            if (item == null) return;
            var entries = MapCycleItems.Select(i => i.Entry).ToList();
            int at = entries.IndexOf(item.Entry);
            if (at < 0) return;
            change(entries, at);
            SaveMapCycle(entries);
        }

        private void AddMatchToCycle()
        {
            if (CurrentPlan?.IsValid != true) return;
            var entries = MapCycleItems.Select(i => i.Entry).ToList();
            entries.Add(MapCycle.For(CurrentPlan));
            SaveMapCycle(entries);
            ShowToast(F("Added to the map cycle: {0}", MapSummary));
        }

        private void ChooseMapCycleFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Map cycle file (pick one, or type a new name)"),
                Filter = T("Map cycle (*.txt)|*.txt|All files (*.*)|*.*"),
                CheckFileExists = false,
                InitialDirectory = MapCycleFile != null && Directory.Exists(Path.GetDirectoryName(MapCycleFile)) ? Path.GetDirectoryName(MapCycleFile) : null,
                FileName = MapCycleFile != null ? Path.GetFileName(MapCycleFile) : "MapCycle.txt",
            };
            if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;
            UseMapCycleFile(dlg.FileName);
        }

        /// <summary>Shows and edits another map cycle file (empty = MapCycle.txt of the server).</summary>
        public void UseMapCycleFile(string path)
        {
            State.Settings.ServerMapCycleFile = path ?? "";
            SaveSettingsSoon();
            LoadMapCycle();
            UpdateServerPlan();
        }

        // ------------------------------------------------------------------ mods

        private void AddMatchMods()
        {
            if (serverPlan == null) return;
            var have = ServerPlanner.ModIds(State.Settings.ServerMods, null);
            var add = serverPlan.MatchModIds.Where(id => !have.Contains(id)).ToList();
            if (add.Count == 0) { ShowToast("The list already has this match's mods"); return; }
            string text = (State.Settings.ServerMods ?? "").TrimEnd();
            ServerModIds = (text.Length > 0 ? text + "\n" : "") + string.Join("\n", add.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            ShowToast(F("{0} mod id(s) added", add.Count));
        }

        private void SaveModioToken()
        {
            try
            {
                Server.SaveModioToken(modioTokenInput);
                ModioTokenInput = "";
                Raise(nameof(ModioTokenStatus));
                ShowToast("Token saved in the server's Engine.ini");
            }
            catch (Exception ex) { ShowToast(F("The token could not be saved: {0}", ex.Message)); }
        }

        // ------------------------------------------------------------------ live control (RCON)

        /// <summary>Runs something over the server's RCON on a worker thread; on failure the output says why and null comes back.</summary>
        private async Task<T> OverServer<T>(Func<T> work, bool quiet = false) where T : class
        {
            try { return await Task.Run(work); }
            catch (RconException ex)
            {
                if (!quiet) { ServerOutput = F("No answer from the server over RCON: {0}", ex.Message); ShowToast("No answer from the server"); }
                return null;
            }
        }

        private async Task RefreshPlayers(bool quiet)
        {
            var players = await OverServer(() => Server.Players(), quiet);
            if (players == null) return;
            ServerPlayers.Clear();
            foreach (var p in players) ServerPlayers.Add(new ServerPlayerItem { Player = p });
            if (!quiet) ServerOutput = players.Count == 0 ? T("No players on the server.") : players.Count == 1 ? T("1 player on the server.") : F("{0} players on the server.", players.Count);
            OnServerStateChanged();
        }

        private async Task KickPlayer(ServerPlayerItem p)
        {
            if (p == null) return;
            string reason = await Prompt(F("Kick {0}", p.Name), "Reason (shown to the player, optional):", "", "Kick");
            if (reason == null) return;
            string r = await OverServer(() => Server.Kick(p.Player, reason));
            if (r == null) return;
            ServerOutput = r.Length > 0 ? r : F("Kicked {0}", p.Name);
            await RefreshPlayers(true);
        }

        private async Task BanPlayer(ServerPlayerItem p)
        {
            if (p == null) return;
            string answer = await Ask(F("Ban {0}", p.Name), "The player is removed and cannot join again for the time you pick.", "Ban for a day", "Cancel", "Ban for good");
            if (answer != "Ban for a day" && answer != "Ban for good") return;
            string r = await OverServer(() => answer == "Ban for good" ? Server.PermBan(p.Player, "") : Server.Ban(p.Player, 1440, ""));
            if (r == null) return;
            ServerOutput = r.Length > 0 ? r : F("Banned {0}", p.Name);
            await RefreshPlayers(true);
        }

        private async Task Say()
        {
            string text = sayText;
            string r = await OverServer(() => Server.Say(text));
            if (r == null) return;
            SayText = "";
            ServerOutput = F("Sent to the chat: {0}", text);
        }

        private async Task TravelServer()
        {
            var plan = serverPlan;
            if (plan?.IsValid != true) return;
            var r = await Task.Run(() => Server.Travel(plan));
            ServerOutput = r.Ok ? F("Loading {0} on the server.", MapSummary) : r.Delivered ? T("Sent; the server is busy loading.") : F("Not loaded: {0}", r.Error);
            ShowToast(r.Ok || r.Delivered ? "Loading the match on the server" : "The server did not take the match");
        }

        private async Task RestartServerRound(bool swap)
        {
            var r = await Task.Run(() => Server.Rcon.RestartRound(swap));
            ServerOutput = r.Ok ? (swap ? T("Round restarted with teams swapped.") : T("Round restarted.")) : F("Not done: {0}", r.Error);
        }

        private async Task SendServerCommand()
        {
            string cmd = serverCommand.Trim();
            var replies = await OverServer(() => Server.Rcon.Run(cmd));
            if (replies == null) return;
            string reply = replies[0].Trim();
            ServerOutput = "> " + cmd + "\n" + (reply.Length > 0 ? reply : T("(no answer)"));
            ServerCommand = "";
        }

        private async Task CheckServerConnection(bool announce)
        {
            string problem = await Task.Run(() => Server.Rcon.Probe());
            remoteReachable = problem == null;
            if (announce) ServerOutput = problem == null ? F("Connected over RCON ({0}).", ServerRconAddress) : F("Not connected: {0}.", problem);
            OnServerStateChanged();
        }
    }
}
