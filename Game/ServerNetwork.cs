using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    public enum FirewallState { Unknown, Allowed, Blocked, NotAsked, Off }

    /// <summary>
    /// How players reach a server on this PC: its addresses on the local network and whether Windows Firewall lets the
    /// server in. Only reads; the one change (allowing the server) runs with the player's approval in Windows' own prompt.
    /// </summary>
    public static class ServerNetwork
    {
        /// <summary>This PC's IPv4 addresses on networks with a router (the ones other PCs can reach), best first.</summary>
        public static List<string> LanAddresses()
        {
            var list = new List<(string Ip, int Rank)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = nic.GetIPProperties();
                    bool router = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.ToString().StartsWith("0."));
                    string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                    bool virt = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("tap-") || desc.Contains("loopback");
                    foreach (var a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = a.Address.ToString();
                        if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                        list.Add((ip, (router ? 0 : 2) + (virt ? 1 : 0)));
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn("Network addresses: " + ex.Message); }
            return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
        }

        private static object Get(object com, string name) => com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, null);

        /// <summary>
        /// Whether Windows Firewall lets the server program in (read from Windows' firewall rules, no change). A player who
        /// pressed Cancel in Windows' prompt gets a block rule: that shows as Blocked.
        /// </summary>
        public static FirewallState Firewall(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return FirewallState.Unknown;
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return FirewallState.Unknown;
                object policy = Activator.CreateInstance(type);
                // NET_FW_PROFILE2_DOMAIN 1, PRIVATE 2, PUBLIC 4: the firewall is off only when it is off for the current profiles.
                int profiles = Convert.ToInt32(Get(policy, "CurrentProfileTypes"));
                bool anyOn = false;
                foreach (int p in new[] { 1, 2, 4 })
                    if ((profiles & p) != 0 && Convert.ToBoolean(policy.GetType().InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { p }))) anyOn = true;
                if (!anyOn) return FirewallState.Off;
                bool allow = false, block = false;
                foreach (object rule in (IEnumerable)Get(policy, "Rules"))
                {
                    string app = Get(rule, "ApplicationName") as string;
                    if (string.IsNullOrEmpty(app) || !string.Equals(Environment.ExpandEnvironmentVariables(app), exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Convert.ToInt32(Get(rule, "Direction")) != 1 || !Convert.ToBoolean(Get(rule, "Enabled"))) continue;   // 1 = inbound
                    if ((Convert.ToInt32(Get(rule, "Profiles")) & profiles) == 0) continue;
                    if (Convert.ToInt32(Get(rule, "Action")) == 1) allow = true; else block = true;    // 1 = allow, 0 = block
                }
                return block ? FirewallState.Blocked : allow ? FirewallState.Allowed : FirewallState.NotAsked;
            }
            catch (Exception ex) { AppLog.Warn("Firewall check: " + ex.Message); return FirewallState.Unknown; }
        }

        /// <summary>
        /// Lets the server program through Windows Firewall (its old rules for the program go first, a block rule
        /// included). Windows asks the player for administrator rights; false when they said no or it failed.
        /// </summary>
        public static bool AllowThroughFirewall(string exe)
        {
            if (string.IsNullOrEmpty(exe) || exe.IndexOf('"') >= 0) return false;
            string args = "/c netsh advfirewall firewall delete rule name=all dir=in program=\"" + exe + "\" & "
                        + "netsh advfirewall firewall add rule name=\"Insurgency Sandstorm dedicated server\" dir=in action=allow enable=yes profile=any program=\"" + exe + "\"";
            try
            {
                // The full path: a bare "cmd.exe" is looked for in the current folder first, and this one runs as administrator.
                using (var p = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    p.WaitForExit(30000);
                    AppLog.Info("Firewall rule for the server: " + (p.HasExited ? "exit " + p.ExitCode : "still running"));
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex) { AppLog.Warn("Firewall rule not added: " + ex.Message); return false; }
        }
    }
}
