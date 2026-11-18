using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace vTerm.Ui
{
    /// <summary>WPF's keys as libghostty-vt's (GhosttyKey: W3C key codes), and the text a key types on the keyboard layout in use.</summary>
    static class KeyMap
    {
        /// <summary>The GhosttyKey for a WPF key (0 = none we pass on).</summary>
        public static int Ghostty(Key k)
        {
            if (k >= Key.A && k <= Key.Z) return 20 + (k - Key.A);
            if (k >= Key.D0 && k <= Key.D9) return 6 + (k - Key.D0);
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return 80 + (k - Key.NumPad0);
            if (k >= Key.F1 && k <= Key.F24) return 121 + (k - Key.F1);
            switch (k)
            {
                case Key.Oem3: return 1;            // `
                case Key.Oem5: return 2;            // \
                case Key.Oem4: return 3;            // [
                case Key.Oem6: return 4;            // ]
                case Key.OemComma: return 5;
                case Key.OemPlus: return 16;        // =
                case Key.Oem102: return 17;         // the extra \ key on some keyboards
                case Key.OemMinus: return 46;
                case Key.OemPeriod: return 47;
                case Key.Oem7: return 48;           // '
                case Key.Oem1: return 49;           // ;
                case Key.Oem2: return 50;           // /
                case Key.Back: return 53;
                case Key.Apps: return 55;
                case Key.Enter: return 58;
                case Key.Space: return 63;
                case Key.Tab: return 64;
                case Key.Delete: return 68;
                case Key.End: return 69;
                case Key.Help: return 70;
                case Key.Home: return 71;
                case Key.Insert: return 72;
                case Key.PageDown: return 73;
                case Key.PageUp: return 74;
                case Key.Down: return 75;
                case Key.Left: return 76;
                case Key.Right: return 77;
                case Key.Up: return 78;
                case Key.Add: return 90;
                case Key.Decimal: return 95;
                case Key.Divide: return 96;
                case Key.Multiply: return 104;
                case Key.Subtract: return 107;
                case Key.Separator: return 108;
                case Key.Escape: return 120;
                case Key.PrintScreen: return 148;
                case Key.Scroll: return 149;
                case Key.Pause: return 150;
            }
            return 0;
        }

        /// <summary>Keys that are never text: they're always passed on as keys (with or without modifiers).</summary>
        public static bool Special(Key k)
        {
            if (k >= Key.F1 && k <= Key.F24) return true;
            switch (k)
            {
                case Key.Back: case Key.Enter: case Key.Tab: case Key.Escape: case Key.Delete: case Key.Insert: case Key.Home: case Key.End:
                case Key.PageUp: case Key.PageDown: case Key.Up: case Key.Down: case Key.Left: case Key.Right: case Key.Space:
                    return true;
            }
            return false;
        }

        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int bufSize, uint flags, IntPtr hkl);

        /// <summary>
        /// The text the key types on this keyboard layout with Shift (or not) and AltGr (or not) - no Ctrl / Alt. "" = none (a dead key, or
        /// a key that types nothing). Doesn't disturb a dead key waiting to combine.
        /// </summary>
        public static string Text(Key k, bool shift, bool altGr)
        {
            try
            {
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                if (vk == 0) return "";
                IntPtr hkl = GetKeyboardLayout(0);
                uint scan = MapVirtualKeyEx(vk, 0, hkl);
                var state = new byte[256];
                if (shift) state[0x10] = 0x80;
                if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
                if (Keyboard.IsKeyToggled(Key.CapsLock)) state[0x14] = 0x01;
                var sb = new StringBuilder(8);
                int n = ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 0x4, hkl);     // 0x4: leave the keyboard's state alone
                if (n <= 0) return "";
                string s = sb.ToString(0, Math.Min(n, sb.Length));
                return s.Length > 0 && s[0] < ' ' ? "" : s;
            }
            catch { return ""; }
        }
    }
}
