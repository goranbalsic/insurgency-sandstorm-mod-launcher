using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SandstormModLauncher.Core
{
    /// <summary>
    /// Sends keystrokes to the game window. Every keystroke is guarded by a foreground check so
    /// nothing is ever typed into another application.
    /// </summary>
    public sealed class GameInput
    {
        private readonly IntPtr window;
        private readonly IntPtr layout;
        public int KeyDelayMs { get; set; } = 60;

        public GameInput(IntPtr window)
        {
            this.window = window;
            uint thread = Native.GetWindowThreadProcessId(window, out _);
            layout = Native.GetKeyboardLayout(thread);
        }

        public IntPtr KeyboardLayout => layout;
        public bool IsForeground => Native.GetForegroundWindow() == window;

        public bool Focus(int timeoutMs = 2500)
        {
            if (!Native.IsWindow(window)) return false;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int attempt = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (IsForeground) return true;
                if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
                switch (attempt++ % 3)
                {
                    case 0:
                        Native.SetForegroundWindow(window);
                        break;
                    case 1:
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
                        uint me = Native.GetCurrentThreadId();
                        Native.AttachThreadInput(me, fgThread, true);
                        Native.BringWindowToTop(window);
                        Native.SetForegroundWindow(window);
                        Native.AttachThreadInput(me, fgThread, false);
                        break;
                    }
                    default:
                        // A synthetic Alt tap lifts the foreground lock for this process.
                        Native.keybd_event(0x12, 0, Native.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                        Native.keybd_event(0x12, 0, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                        Native.SetForegroundWindow(window);
                        break;
                }
                Thread.Sleep(120);
            }
            return IsForeground;
        }

        private static bool IsExtended(ushort vk) =>
            vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || (vk >= 0x25 && vk <= 0x28) || vk == 0x2D || vk == 0x2E ||
            vk == 0x6F || vk == 0x90 || vk == 0xA3 || vk == 0xA5;

        private Native.INPUT KeyInput(ushort vk, bool up) => new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)Native.MapVirtualKeyEx(vk, Native.MAPVK_VK_TO_VSC, layout),
                    dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0)
                }
            }
        };

        private void Send(params Native.INPUT[] inputs)
        {
            if (!IsForeground) throw new FocusLostException();
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public void Tap(ushort vk)
        {
            Send(KeyInput(vk, false));
            Thread.Sleep(25);
            Send(KeyInput(vk, true));
            Thread.Sleep(KeyDelayMs);
        }

        public void Chord(ushort modifier, ushort vk)
        {
            Send(KeyInput(modifier, false));
            Thread.Sleep(25);
            Send(KeyInput(vk, false));
            Thread.Sleep(25);
            Send(KeyInput(vk, true));
            Thread.Sleep(25);
            Send(KeyInput(modifier, true));
            Thread.Sleep(KeyDelayMs);
        }

        public void TypeText(string text)
        {
            foreach (char c in text)
            {
                var down = new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE } } };
                var up = down; up.u.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
                Send(down, up);
                Thread.Sleep(6);
            }
            Thread.Sleep(KeyDelayMs);
        }

        /// <summary>Clears the console input line (End + backspaces).</summary>
        public void ClearLine(int chars = 120)
        {
            Tap(0x23);
            var list = new List<Native.INPUT>();
            for (int i = 0; i < chars; i++) { list.Add(KeyInput(0x08, false)); list.Add(KeyInput(0x08, true)); }
            Send(list.ToArray());
            Thread.Sleep(KeyDelayMs);
        }

        /// <summary>
        /// Virtual key for an Unreal key name ("F10", "Tilde", ...) on the game's keyboard layout.
        /// Returns 0 when the layout has no unshifted key for it.
        /// </summary>
        public ushort VirtualKeyFor(string ueKey)
        {
            if (string.IsNullOrEmpty(ueKey)) return 0;
            if (ueKey.Length >= 2 && (ueKey[0] == 'F' || ueKey[0] == 'f') && int.TryParse(ueKey.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                return (ushort)(0x6F + fn);
            switch (ueKey)
            {
                case "Insert": return 0x2D;
                case "Delete": return 0x2E;
                case "Home": return 0x24;
                case "End": return 0x23;
                case "PageUp": return 0x21;
                case "PageDown": return 0x22;
                case "ScrollLock": return 0x91;
                case "Pause": return 0x13;
                case "NumPadZero": return 0x60;
                case "Tab": return 0x09;
            }
            char ch = ueKey switch
            {
                "Tilde" => '`', "Backslash" => '\\', "Caret" => '^', "Section" => '§', "Apostrophe" => '\'', "Semicolon" => ';',
                "Equals" => '=', "Hyphen" => '-', "LeftBracket" => '[', "RightBracket" => ']', "Period" => '.', "Comma" => ',',
                "Slash" => '/', "Ampersand" => '&', "Asterix" => '*', "Colon" => ':', "Dollar" => '$', "Exclamation" => '!',
                "Quote" => '"', "Underscore" => '_', "A_AccentGrave" => 'à', "E_AccentGrave" => 'è', "E_AccentAigu" => 'é', "C_Cedille" => 'ç',
                _ => '\0'
            };
            if (ch == '\0') return 0;
            short scan = Native.VkKeyScanEx(ch, layout);
            if (scan == -1 || ((scan >> 8) & 0xff) != 0) return 0;
            return (ushort)(scan & 0xff);
        }
    }

    public sealed class FocusLostException : Exception
    {
        public FocusLostException() : base("The game window lost focus, so typing was stopped.") { }
    }
}
