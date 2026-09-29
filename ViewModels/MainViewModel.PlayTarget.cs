using System.Threading.Tasks;
using System.Windows.Input;
using SandstormModLauncher.Game;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    /// <summary>
    /// The big button at the bottom right: the match goes to the game on this PC ("Start and launch") or to the
    /// dedicated server ("Start the server"). A server that is not set up yet opens the Server page instead.
    /// </summary>
    public sealed partial class MainViewModel
    {
        public ICommand MainActionCommand { get; private set; }
        public ICommand SetPlayTargetCommand { get; private set; }

        private void InitPlayTargetCommands()
        {
            MainActionCommand = new AsyncCommand(MainAction, () => CanMainAction);
            SetPlayTargetCommand = new RelayCommand(p => PlayTarget = p as string);
        }

        /// <summary>"Local" (the game on this PC) or "Server" (the dedicated server).</summary>
        public string PlayTarget
        {
            get => State.Settings.PlayTarget == "Server" ? "Server" : "Local";
            set
            {
                string v = value == "Server" ? "Server" : "Local";
                if (State.Settings.PlayTarget == v) return;
                State.Settings.PlayTarget = v;
                SaveSettingsSoon();
                RaiseMainAction();
            }
        }

        public bool PlaysOnServer => PlayTarget == "Server";

        /// <summary>What the server button does now: set the server up, start it, or load the match on it.</summary>
        private enum ServerStep { SetUp, Start, Starting, Load }

        private ServerStep NextServerStep()
        {
            if (Server == null) return ServerStep.SetUp;
            if (ServerRemote)
                return !string.IsNullOrWhiteSpace(State.Settings.ServerRemoteHost) && serverPlan?.IsValid == true ? ServerStep.Load : ServerStep.SetUp;
            if (!ServerInstall.Found || serverPlan?.IsValid != true) return ServerStep.SetUp;
            if (ServerRunningHere) return Server.Monitor?.Phase == GamePhase.InMatch ? ServerStep.Load : ServerStep.Starting;
            return ServerStep.Start;
        }

        public string MainActionTitle
        {
            get
            {
                if (!PlaysOnServer) return LaunchTitle;
                if (serverBusy) return T("Working on the server...");
                switch (NextServerStep())
                {
                    case ServerStep.Start: return T("Start the server");
                    // A server that has been listening is changing maps (a match was just loaded on it).
                    case ServerStep.Starting: return Server?.Monitor?.ListeningPort > 0 ? T("Changing the map...") : T("Server starting...");
                    case ServerStep.Load: return T("Load on the server");
                    default: return T("Set up the server");
                }
            }
        }

        public string MainActionTip
        {
            get
            {
                if (!PlaysOnServer) return LaunchSubtitle;
                switch (NextServerStep())
                {
                    case ServerStep.Start: return T("Starts the dedicated server with this match, its own settings and map cycle");
                    case ServerStep.Starting: return Server?.Monitor?.ListeningPort > 0 ? T("The server is loading the match") : T("The server is loading its first map");
                    case ServerStep.Load: return T("Loads this match on the running server");
                    default: return ServerPlanError ?? T("Opens the Server page: set up the dedicated server there first");
                }
            }
        }

        public bool CanMainAction => PlaysOnServer ? !serverBusy && !launchRunning && !Loading && NextServerStep() != ServerStep.Starting : CanLaunch;

        private void RaiseMainAction()
        {
            RaiseMany(nameof(PlayTarget), nameof(PlaysOnServer), nameof(MainActionTitle), nameof(MainActionTip), nameof(CanMainAction));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task MainAction()
        {
            if (!PlaysOnServer) { await Launch(false); return; }
            switch (NextServerStep())
            {
                case ServerStep.Start: await StartServer(); break;
                case ServerStep.Load: await TravelServer(); break;
                case ServerStep.Starting: break;
                default:
                    Page = "Server";
                    ShowToast(serverPlan?.Error != null && ServerInstall.Found ? serverPlan.Error
                              : T("Set up the dedicated server on this page, then press the button again."));
                    break;
            }
            RaiseMainAction();
        }
    }
}
