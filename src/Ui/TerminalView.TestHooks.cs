using System;
using System.Linq;
using System.Text;
using System.Windows.Input;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// Tests only (VTERM_TEST_HOOKS=1, as vXplorer's and vPad's): the terminal's part of the test commands MainWindow receives by WM_COPYDATA
    /// - nothing moves the real mouse or keyboard. vSuite.Core's scripts\test-send.ps1 sends one.
    /// </summary>
    public sealed partial class TerminalView
    {
        /// <summary>
        /// newtab [profile] | tab &lt;index from 1&gt; | close [index] | type &lt;text&gt; (to the shell in front; \n = Enter) | moveout (the tab in
        /// front into a new window). True when it was one of these.
        /// </summary>
        public bool RunTestCommand(string verb, string arg)
        {
            int n;
            switch (verb)
            {
                case "newtab":
                    NewTab(arg.Length > 0 ? _s.ProfilesInUse().FirstOrDefault(p => string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase)) : null);
                    return true;
                case "tab": if (int.TryParse(arg, out n) && n >= 1 && n <= _sessions.Count) Select(_sessions[n - 1]); return true;
                case "close":
                {
                    Session s = int.TryParse(arg, out n) && n >= 1 && n <= _sessions.Count ? _sessions[n - 1] : _active;
                    if (s != null) CloseTab(s);
                    return true;
                }
                case "type": if (_active != null && _active.Pty != null && !_active.Over) _active.Pty.Write(arg.Replace("\\n", "\r")); return true;
                case "text": _screen.TypeText(arg.Replace("\\n", "\r")); return true;      // as typed, through the screen
                case "key":
                {
                    // key Up | key Ctrl+C | key Shift+PageUp …: through the screen's key handling, as a real key press
                    ModifierKeys m = ModifierKeys.None;
                    string name = arg;
                    foreach (string part in arg.Split('+'))
                    {
                        if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) m |= ModifierKeys.Control;
                        else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) m |= ModifierKeys.Shift;
                        else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) m |= ModifierKeys.Alt;
                        else name = part;
                    }
                    Key k;
                    if (Enum.TryParse(name, true, out k)) _screen.PressKey(k, m, false);
                    return true;
                }
                case "wheel": { int d; if (int.TryParse(arg, out d)) _screen.TestWheel(d); return true; }
                case "selectall": if (_active != null && _active.Engine != null) _active.Engine.SelectAll(); return true;
                case "copy": _screen.Copy(); return true;
                case "paste": _screen.Paste(); return true;
                case "moveout": PopOutActive(); return true;
            }
            return false;
        }

        /// <summary>This terminal's tabs as text (the dump command): name, running or ended, SSH, the one in front.</summary>
        public string TestDump()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _sessions.Count; i++)
            {
                Session s = _sessions[i];
                sb.AppendLine("  tab " + (i + 1) + ": " + (s.Tab != null ? s.Tab.Title : s.Title) + (s == _active ? " [front]" : "") +
                              (s.Over ? " [ended]" : " [running]") + (s.Conn != null ? " [ssh " + s.Conn.Shown + "]" : "") +
                              (s.Conn == null && s.Profile != null && !string.IsNullOrEmpty(s.Profile.Folder) ? " [in " + s.Profile.Folder + "]" : ""));
            }
            // the front tab's screen: its last lines as text, its size, what the library holds
            if (_active != null && _active.Engine != null)
            {
                string[] lines = _active.Engine.ScreenText().Replace("\r", "").Split('\n');
                sb.AppendLine("  screen " + _active.Engine.Cols + "x" + _active.Engine.Rows + ", " + (_active.Engine.MemoryBytes() / 1024) + " KB, frames " + _screen.Frames + ":");
                for (int i = Math.Max(0, lines.Length - 12); i < lines.Length; i++) sb.AppendLine("    | " + lines[i]);
            }
            return sb.ToString();
        }
    }
}
