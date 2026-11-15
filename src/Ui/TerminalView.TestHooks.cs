using System;
using System.Linq;
using System.Text;
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
            return sb.ToString();
        }
    }
}
