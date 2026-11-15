using System;
using System.Linq;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// The tab strip is vSuite's TabStrip (as vXplorer's): the tabs share the room with arrows when they don't fit; dragging one along the
    /// strip slides the others out of its way; pulled out, a copy follows the pointer - let go on another vTerm window (or the drop-down)
    /// and it moves there, in the open and a new window opens there (a window's only tab moves the window instead, joining another window
    /// if it's let go on its tabs). The shell keeps running throughout. This hears what the strip does.
    /// </summary>
    public sealed partial class TerminalView
    {
        /// <summary>A tab is pressed (maybe the start of a drag): the text window doesn't take the keyboard meanwhile (it did when the
        /// press activated the window, and that took the press away).</summary>
        public bool TabPressed { get { return _tabs.Pressing; } }

        Session SessionOf(StripTab t) { return t != null ? t.Tag as Session : null; }

        void HookTabs()
        {
            _tabs.OwnsWindow = h => h != IntPtr.Zero && h == SurfaceHandle;   // a drop on the text window counts as on this terminal
            _tabs.Picked += t => { Session s = SessionOf(t); if (s != null && _sessions.Contains(s)) Select(s, false); };
            _tabs.PressEnded += t => FocusTerm();   // the click (or the drag) is over: the keyboard back to the text
            _tabs.CloseClicked += t => { Session s = SessionOf(t); if (s != null) CloseTab(s); };
            _tabs.MenuRequested += t => { Session s = SessionOf(t); if (s != null) TabMenu(s); };
            _tabs.Reordered += (t, from, to) =>
            {
                Session s = SessionOf(t);
                if (s == null || !_sessions.Remove(s)) return;
                _sessions.Insert(Math.Max(0, Math.Min(to, _sessions.Count)), s);
            };
            _tabs.MovedTo += (t, other, index) =>
            {
                Session s = SessionOf(t);
                TerminalView target = Live.FirstOrDefault(v => v._tabs == other);
                if (s != null && target != null && target != this) MoveTo(s, target);
            };
            _tabs.TornOut += (t, screen) =>
            {
                Session s = SessionOf(t);
                if (s == null) return;
                DropPoint = new POINT { X = (int)screen.X, Y = (int)screen.Y };   // the new window goes where it was let go
                PopOut(s);
            };
        }
    }
}
