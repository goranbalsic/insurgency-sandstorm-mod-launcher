using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    /// <summary>
    /// Launcher-driven AI teammate reinforcement: every N seconds the game is told to bring its dead AI teammates
    /// back (the game's own AIRespawnFriendlyBots). It is not a game rule, so it stops when the launcher closes.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private DispatcherTimer botRespawnTimer;
        private DateTime nextBotRespawn = DateTime.MinValue;
        private bool botRespawnBusy;
        private string lastBotRespawnError;

        /// <summary>Seconds between two "bring the AI teammates back" commands; 0 turns it off.</summary>
        public int BotReinforceSeconds
        {
            get => Math.Max(0, Math.Min(300, State.Settings.BotReinforceSeconds));
            set
            {
                int v = Math.Max(0, Math.Min(300, value));
                if (State.Settings.BotReinforceSeconds == v) return;
                State.Settings.BotReinforceSeconds = v;
                SaveSettingsSoon();
                nextBotRespawn = DateTime.MinValue;
                RaiseMany(nameof(BotReinforceSeconds), nameof(BotReinforceHint), nameof(BotReinforceChanged));
            }
        }

        public bool BotReinforceChanged => BotReinforceSeconds > 0;
        public string BotReinforceHint => BotReinforceSeconds <= 0
            ? T("Off. The game's own respawn rule brings your team back.")
            : F("Every {0} s, while a match runs, the launcher brings dead AI teammates back. It is not a game rule, and it stops when the launcher closes.", BotReinforceSeconds);

        private void InitBotReinforce()
        {
            botRespawnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            botRespawnTimer.Tick += async (s, e) => await TickBotReinforce();
            botRespawnTimer.Start();
        }

        private async Task TickBotReinforce()
        {
            int seconds = BotReinforceSeconds;
            if (seconds <= 0 || botRespawnBusy) { nextBotRespawn = DateTime.MinValue; return; }
            if (!BotReinforceTarget(out var run)) { nextBotRespawn = DateTime.MinValue; return; }
            DateTime now = DateTime.UtcNow;
            if (nextBotRespawn == DateTime.MinValue) { nextBotRespawn = now.AddSeconds(seconds); return; }
            if (now < nextBotRespawn) return;

            botRespawnBusy = true;
            try
            {
                await Task.Run(run);
                if (lastBotRespawnError != null) { AppLog.Info("AI teammate respawn works again"); lastBotRespawnError = null; }
            }
            catch (Exception ex)
            {
                if (ex.Message != lastBotRespawnError) AppLog.Warn("AI teammate respawn: " + ex.Message);
                lastBotRespawnError = ex.Message;
            }
            finally
            {
                botRespawnBusy = false;
                nextBotRespawn = DateTime.UtcNow.AddSeconds(seconds);
            }
        }

        /// <summary>The match being played, or the server being controlled; false when there is nothing to send to.</summary>
        private bool BotReinforceTarget(out Action run)
        {
            run = null;
            // The game on this PC first: "friendly" is the player's team there, so the AI teammates are the target.
            if (Monitor?.IsRunning == true && Monitor.Phase == GamePhase.InMatch && Rcon != null)
            {
                run = () => Rcon.RunAsPlayer(new[] { "AIRespawnFriendlyBots" });
                return true;
            }
            if (CanControlServer && Server?.Rcon != null)
            {
                run = () => Server.Rcon.Run("AIRespawnFriendlyBots");
                return true;
            }
            return false;
        }
    }
}
