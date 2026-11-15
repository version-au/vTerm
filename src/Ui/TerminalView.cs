using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// A terminal with tabs: each tab a shell (PowerShell, cmd, WSL ...) in a Windows pseudo console (ConPty), drawn by xterm.js in WebView2.
    /// The tab strip looks like vShell's widget tabs; the terminal's colours come from the vShell theme. It has no background of its own,
    /// so the window holding it shows through - glass and all. The text itself is drawn in a TermSurface: a see-through window kept over
    /// the terminal area. Each vTerm window (MainWindow) holds one; a tab can move from one window to another with its shell running.
    /// </summary>
    public sealed partial class TerminalView : Grid
    {
        sealed class Session
        {
            public string Id;
            public TermProfile Profile;
            public ConPty Pty;
            public string Title;
            /// <summary>Its tab in the strip (vSuite's StripTab, as vXplorer's).</summary>
            public StripTab Tab;
            public readonly StringBuilder Pending = new StringBuilder();
            public bool Created;
            /// <summary>An SSH tab: its connection (and a command run there instead of the saved one), else null.</summary>
            public TermConnection Conn;
            public string Remote;
            /// <summary>The shell / connection has ended (an SSH tab reconnects on Enter).</summary>
            public bool Over;
            public int Cols = 120, Rows = 30;
            /// <summary>The terminal showing it now (a tab can move: popped out into a window, and back).</summary>
            public TerminalView Owner;
            /// <summary>On its way to another terminal: its output waits in Pending for the new one.</summary>
            public bool Moving;
        }

        /// <summary>A tab on its way from one terminal to another (popped out / moved back): its shell, still running, and its screen.</summary>
        public sealed class Handoff
        {
            internal object S;
            /// <summary>The screen and scrollback, as text that draws them again ("" = start blank).</summary>
            internal string Screen = "";
            /// <summary>The terminal it came from.</summary>
            public TerminalView From;
            public string Title = "";
        }

        readonly AppSettings _s;
        readonly List<Session> _sessions = new List<Session>();
        Session _active;
        static int _nextId = 1;
        TermLook _t;
        bool _webReady, _webFailed;
        readonly List<string> _queue = new List<string>();
        readonly DispatcherTimer _flush;
        readonly Border _strip = new Border();
        readonly TabStrip _tabs = new TabStrip { TabHeight = 32, Reserve = 40, VerticalAlignment = VerticalAlignment.Bottom };
        readonly Border _body = new Border { Background = Brushes.Transparent };
        readonly Border _empty = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        readonly TextBlock _emptyText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        readonly StackPanel _right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        WebView2 _web;
        TermSurface _surface;
        bool _pendingWeb, _suspended;
        readonly DispatcherTimer _track;
        RECT _placed;
        int _still;

        /// <summary>The last tab was closed (or moved away).</summary>
        public event Action Emptied;
        /// <summary>"Options…" from the new-tab menu.</summary>
        public Action OpenSettings;
        /// <summary>Shift+Esc (the drop-down hides; null = nothing).</summary>
        public Action HideRequested;
        /// <summary>The text window lost the keyboard to something else (the drop-down hides when it's gone elsewhere).</summary>
        public event Action SurfaceDeactivated;
        /// <summary>A dialog of the side panel is open (the drop-down mustn't hide for it).</summary>
        public bool InDialog { get { return Busy > 0; } }
        /// <summary>The text window has the keyboard.</summary>
        public bool SurfaceActive { get { return _surface != null && _surface.Handle != IntPtr.Zero && Native.GetForegroundWindow() == _surface.Handle; } }
        public IntPtr SurfaceHandle { get { return _surface != null ? _surface.Handle : IntPtr.Zero; } }
        public bool HasTabs { get { return _sessions.Count > 0; } }
        public int TabCount { get { return _sessions.Count; } }
        /// <summary>Tabs whose shell / connection is still running.</summary>
        public int RunningCount { get { return _sessions.FindAll(delegate (Session x) { return !x.Over; }).Count; } }
        /// <summary>A tab moving into a window of its own: the window makes a new one and adopts it.</summary>
        public Action<Handoff> NewWindowFor;
        /// <summary>The other windows a tab can move to (what the menu calls each one, and its terminal).</summary>
        public Func<List<KeyValuePair<string, TerminalView>>> OtherWindows;
        /// <summary>Show (and bring forward) the window this terminal is in - a tab just moved into it.</summary>
        public Action BringUp;
        /// <summary>The tab in front changed, or its title did (the window shows it on the taskbar).</summary>
        public event Action TitleChanged;
        public string ActiveTitle { get { return _active == null ? "" : _active.Tab != null ? _active.Tab.Title : _active.Title; } }
        /// <summary>A press on the tab strip's empty part (a popped-out window is dragged by it).</summary>
        public event Action<MouseButtonEventArgs> StripPressed;

        /// <summary>Every terminal that's open (a dragged tab looks for the one it's dropped on).</summary>
        static readonly List<TerminalView> Live = new List<TerminalView>();

        public TerminalView(AppSettings s)
        {
            _s = s;
            Live.Add(this);
            TermDialogs.Init(s);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // the side panel (connections / files)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _strip.Child = BuildStrip();
            _strip.Background = Brushes.Transparent;       // its empty part takes presses (tabs and buttons handle their own)
            _strip.MouseLeftButtonDown += delegate (object o, MouseButtonEventArgs e) { if (StripPressed != null) StripPressed(e); };
            Grid.SetRow(_strip, 0);
            Grid.SetRow(_body, 1);
            Grid.SetRow(_empty, 1);
            Grid.SetColumn(_strip, 1);
            Grid.SetColumn(_body, 1);
            Grid.SetColumn(_empty, 1);
            Children.Add(_strip);
            Children.Add(_body);
            Children.Add(_empty);
            UIElement side = BuildPanel();
            Grid.SetRowSpan(side, 2);
            Children.Add(side);
            TermSsh.Changed += ConnectionsChanged;
            _empty.Child = _emptyText;
            _empty.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; NewTab(null); };
            _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            _flush.Tick += delegate { FlushOutput(); };
            // started by output arriving (OutputArrived), stopped once there's nothing left to send - not ticking all the time
            PreviewKeyDown += OnKey;
            // the text window follows the terminal area (moves, resizes, shows / hides with it)
            _track = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            _track.Tick += delegate { Track(); };
            IsVisibleChanged += delegate { Track(); if (IsVisible) _track.Start(); else _track.Stop(); };
            LayoutUpdated += delegate { if (_surface != null) Track(); };
            Loaded += delegate { if (_pendingWeb) { _pendingWeb = false; EnsureWeb(); } Track(); };
            ShowEmpty();
        }

        /// <summary>Hold the text window hidden (the drop-down is sliding: it would be left behind).</summary>
        public void Suspend(bool on) { _suspended = on; Track(); }

        /// <summary>Place the text window again now (its window was restored, or changed Keep on top).</summary>
        public void Retrack() { _placed = new RECT(); _still = 0; Track(); }

        /// <summary>Put the text window over the terminal area, or hide it.</summary>
        void Track()
        {
            if (_surface == null || _surface.Handle == IntPtr.Zero) return;
            Window owner = Window.GetWindow(this);
            bool show = !_suspended && IsVisible && owner != null && owner.IsVisible && _sessions.Count > 0 && _body.ActualWidth > 4 && _body.ActualHeight > 4
                        && PresentationSource.FromVisual(_body) != null;
            if (!show)
            {
                if (_placed.Width != 0) { _surface.HideSurface(); _placed = new RECT(); }
                return;
            }
            try
            {
                Point a = _body.PointToScreen(new Point(0, 0)), b = _body.PointToScreen(new Point(_body.ActualWidth, _body.ActualHeight));
                var r = new RECT { Left = (int)Math.Round(Math.Min(a.X, b.X)), Top = (int)Math.Round(Math.Min(a.Y, b.Y)), Right = (int)Math.Round(Math.Max(a.X, b.X)), Bottom = (int)Math.Round(Math.Max(a.Y, b.Y)) };
                // only the part that's really shown: whatever holds it may cut it off (a row shorter than the terminal, a widget that scrolls)
                ClipToShown(ref r);
                // a window resized from its edges: the text window (WebView2's own window takes the mouse) stays clear of them
                if (KeepOffEdges && owner.WindowState == WindowState.Normal)
                {
                    IntPtr oh = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
                    RECT wr;
                    if (oh != IntPtr.Zero && Native.GetWindowRect(oh, out wr))
                    {
                        int band = (int)Math.Round(7 * Math.Max(0.5, Native.GetDpiForWindow(oh) / 96.0));
                        r.Left = Math.Max(r.Left, wr.Left + band);
                        r.Right = Math.Min(r.Right, wr.Right - band);
                        r.Bottom = Math.Min(r.Bottom, wr.Bottom - band);
                    }
                }
                if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8)
                {
                    if (_placed.Width != 0) { _surface.HideSurface(); _placed = new RECT(); }
                    return;
                }
                if (r.Left == _placed.Left && r.Top == _placed.Top && r.Right == _placed.Right && r.Bottom == _placed.Bottom)
                {
                    // still: check less often (4 a second) until something moves again
                    if (++_still == 12 && _track.Interval.TotalMilliseconds < 200) _track.Interval = TimeSpan.FromMilliseconds(250);
                    return;
                }
                _still = 0;
                if (_track.Interval.TotalMilliseconds > 50) _track.Interval = TimeSpan.FromMilliseconds(40);
                _placed = r;
                _surface.Topmost = owner.Topmost;
                _surface.PlaceAt(r.Left, r.Top, r.Width, r.Height);
            }
            catch (Exception ex) { Log.Write("Terminal track: " + ex.Message); }
        }

        /// <summary>Cut r (screen pixels) to what the terminal's holders show: any of them that clips (its own bounds, a layout slot smaller
        /// than it, a scrolling view).</summary>
        void ClipToShown(ref RECT r)
        {
            DependencyObject d = VisualTreeHelper.GetParent(_body);
            while (d != null)
            {
                var fe = d as FrameworkElement;
                if (fe != null && fe.ActualWidth > 0 && fe.ActualHeight > 0 &&
                    (fe.ClipToBounds || fe.Clip != null || System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe) != null || fe is System.Windows.Controls.ScrollContentPresenter || fe is Window))
                {
                    Point a = fe.PointToScreen(new Point(0, 0)), b = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
                    r.Left = Math.Max(r.Left, (int)Math.Round(Math.Min(a.X, b.X)));
                    r.Top = Math.Max(r.Top, (int)Math.Round(Math.Min(a.Y, b.Y)));
                    r.Right = Math.Min(r.Right, (int)Math.Round(Math.Max(a.X, b.X)));
                    r.Bottom = Math.Min(r.Bottom, (int)Math.Round(Math.Max(a.Y, b.Y)));
                }
                d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
            }
        }

        // =================================================================
        // Look
        // =================================================================
        /// <summary>The theme's colours on the tab strip and the terminal (its 16 colours too, unless a named scheme is picked).</summary>
        public void ApplyTheme(TermLook t)
        {
            _t = t;
            _strip.BorderBrush = TermLook.Faded(t.Fg, 14);
            _strip.BorderThickness = new Thickness(0, 0, 0, 1);
            TextElement.SetForeground(this, t.Fg);
            TextElement.SetFontFamily(this, t.Font);
            _emptyText.Foreground = t.FgDim;
            _emptyText.FontSize = t.FontSize;
            foreach (UIElement b in _right.Children) { var bb = b as Border; var tb = bb != null ? bb.Child as TextBlock : null; if (tb != null) tb.Foreground = t.FgDim; }
            foreach (Session x in _sessions) PaintTab(x);
            ShowEmpty();
            if (_surface != null) _surface.ApplyLook(_s.SeeThrough, SolidBg());
            PanelTheme();
            PostTheme();
        }

        /// <summary>The terminal's background when it isn't see-through: the theme colour, solid.</summary>
        Color SolidBg() { return _t == null ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Solid(_t.BgColor); }

        bool Dark()
        {
            if (_t == null) return true;
            Color bg = _t.BgColor;
            return (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 < 140;
        }

        void PostTheme()
        {
            if (_t == null) return;
            AppSettings s = _s;
            Color fg = _t.Fg.Color, bg = _t.BgColor, ac = _t.MainAccent.Color, sec = _t.SecondaryAccent.Color;
            var th = new Dictionary<string, string>
            {
                { "background", "rgba(0,0,0,0)" }, { "foreground", Css(fg) }, { "cursor", Css(ac) }, { "cursorAccent", Css(Solid(bg)) },
                { "selectionBackground", "rgba(" + ac.R + "," + ac.G + "," + ac.B + ",0.38)" }
            };
            string[] names = { "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
                               "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite" };
            string[] pal = Palette(s.Colors, Dark(), Solid(bg), fg, ac, sec);
            for (int i = 0; i < names.Length; i++) th[names[i]] = pal[i];
            string font = string.IsNullOrWhiteSpace(s.Font) ? "'Cascadia Mono', Consolas, 'Courier New', monospace"
                                                             : "'" + s.Font.Replace("'", "") + "', 'Cascadia Mono', Consolas, monospace";
            Post(new { t = "theme", theme = th, font = font, size = s.FontSize, thumb = "rgba(" + fg.R + "," + fg.G + "," + fg.B + ",0.35)" });
        }

        /// <summary>
        /// The 16 terminal colours. "Theme": built from the vShell theme - blue is the Main Accent, magenta the Secondary Accent, the greys
        /// are tinted from the background and text, red / green / yellow are vShell's status colours, cyan sits between the accent and green.
        /// The named schemes are the well-known ones (the background and text stay the theme's).
        /// </summary>
        public static string[] Palette(string scheme, bool dark, Color bg, Color fg, Color ac, Color sec)
        {
            switch (scheme)
            {
                case "One Dark": return new[] { "#3b4048", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#dcdfe4", "#6b7385", "#ff7b86", "#b5e890", "#ffd68a", "#7cc4ff", "#dd8cf0", "#6fd3df", "#ffffff" };
                case "Dracula": return new[] { "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2", "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff" };
                case "Nord": return new[] { "#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0", "#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4" };
                case "Solarized": return new[] { "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5", "#586e75", "#cb4b16", "#93a1a1", "#c9a227", "#839496", "#6c71c4", "#56c2b8", "#fdf6e3" };
                case "Campbell": return new[] { "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc", "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2" };
            }
            Color red = TermLook.Bad.Color, green = TermLook.Good.Color, yellow = TermLook.Warn.Color;
            if (!dark) { red = Mix(red, Colors.Black, 0.18); green = Mix(green, Colors.Black, 0.35); yellow = Mix(yellow, Colors.Black, 0.38); }
            Color blue = ac, magenta = sec, cyan = Mix(ac, green, 0.5);
            // bright = a little towards the text colour's side (lighter on dark themes, darker on light ones)
            Color toward = dark ? Colors.White : Colors.Black;
            double k = 0.25;
            Color black = dark ? Mix(bg, fg, 0.18) : Mix(fg, bg, 0.1);
            Color white = dark ? Mix(fg, bg, 0.18) : Mix(bg, fg, 0.35);
            Color brBlack = Mix(bg, fg, 0.48), brWhite = dark ? fg : Mix(fg, Colors.Black, 0.2);
            return new[]
            {
                Css(black), Css(red), Css(green), Css(yellow), Css(blue), Css(magenta), Css(cyan), Css(white),
                Css(brBlack), Css(Mix(red, toward, k)), Css(Mix(green, toward, k)), Css(Mix(yellow, toward, k)),
                Css(Mix(blue, toward, k)), Css(Mix(magenta, toward, k)), Css(Mix(cyan, toward, k)), Css(brWhite)
            };
        }

        public static readonly string[] Schemes = { "Theme", "One Dark", "Dracula", "Nord", "Solarized", "Campbell" };

        static Color Mix(Color a, Color b, double f)
        {
            f = Math.Max(0, Math.Min(1, f));
            return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * f), (byte)Math.Round(a.G + (b.G - a.G) * f), (byte)Math.Round(a.B + (b.B - a.B) * f));
        }
        static Color Solid(Color c) { return Color.FromRgb(c.R, c.G, c.B); }
        static string Css(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        // =================================================================
        // Tab strip (like a widget's tabs)
        // =================================================================
        UIElement BuildStrip()
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 4, 4, 0) };
            DockPanel.SetDock(_right, Dock.Right);
            dock.Children.Add(_right);
            // the app's icon and its menu, first on the left (set by the window: SetLead)
            DockPanel.SetDock(_lead, Dock.Left);
            dock.Children.Add(_lead);
            // the panel button, then the tabs (vSuite's strip, as vXplorer's: they share the room, arrows when they don't fit) and the new-tab buttons
            var panelBtn = StripButton("\uE700", "Connections and files", delegate { TogglePanel(); });
            DockPanel.SetDock(panelBtn, Dock.Left);
            dock.Children.Add(panelBtn);
            TextElement.SetFontFamily(_tabs, new FontFamily("Segoe UI"));
            TextElement.SetFontSize(_tabs, 12.5);
            _tabs.AddButton(StripButton("", "New tab (Ctrl+Shift+T)", delegate { NewTab(null); }));
            Border pick = null;
            pick = StripButton("", "New tab with…", delegate { ProfileMenu(pick); });
            _tabs.AddButton(pick);
            HookTabs();
            dock.Children.Add(_tabs);
            return dock;
        }

        readonly Border _lead = new Border { VerticalAlignment = VerticalAlignment.Center };

        /// <summary>What goes first on the left of the strip (the window's icon and menu).</summary>
        public void SetLead(UIElement e) { _lead.Child = e; }

        /// <summary>The buttons on the right of the strip: glyph, tooltip, click (pin / minimise / close …).</summary>
        public void SetButtons(IList<Tuple<string, string, Action>> buttons)
        {
            _right.Children.Clear();
            if (buttons != null) foreach (var b in buttons) _right.Children.Add(StripButton(b.Item1, b.Item2, b.Item3));
            if (_t != null) ApplyTheme(_t);
        }

        /// <summary>The hide button on the right of the strip (the drop-down has one).</summary>
        public void ShowHideButton(bool on)
        {
            _right.Children.Clear();
            if (on) _right.Children.Add(StripButton("", "Hide (the shells keep running)", delegate { if (HideRequested != null) HideRequested(); }));
            if (_t != null) ApplyTheme(_t);
        }

        /// <summary>
        /// Open a menu of the terminal's at the mouse pointer, placed on the screen itself: WPF's own placement (under the button, or at the
        /// pointer) put it in the top-left corner of the screen in the drop-down and in widgets - their windows are placed in device pixels
        /// outside WPF, and the text window beside them has the keyboard.
        /// </summary>
        static void OpenAtPointer(ContextMenu m)
        {
            POINT p = Native.CursorPos();
            double sx = 1, sy = 1;
            var target = m.PlacementTarget as Visual;
            try { if (target != null) { DpiScale d = VisualTreeHelper.GetDpi(target); sx = d.DpiScaleX; sy = d.DpiScaleY; } } catch { }
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            m.HorizontalOffset = p.X / sx;
            m.VerticalOffset = p.Y / sy;
            m.IsOpen = true;
        }

        /// <summary>Every tab (the overflow button): the chosen one ticked.</summary>
        void TabsMenu(FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            foreach (Session s in _sessions)
            {
                Session me = s;
                var mi = new MenuItem { Header = s.Tab != null ? s.Tab.Title : s.Title, IsChecked = s == _active };
                mi.Click += delegate { Select(me); };
                m.Items.Add(mi);
            }
            OpenAtPointer(m);
        }

        Border StripButton(string glyph, string tip, Action click)
        {
            var tb = new TextBlock { Text = glyph, FontFamily = TermLook.Glyphs, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (_t != null) tb.Foreground = _t.FgDim;
            var b = new Border
            {
                Width = 28, Height = 26, CornerRadius = new CornerRadius(6, 6, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip,
                Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom, Child = tb
            };
            b.MouseEnter += delegate { if (_t != null) { b.Background = _t.Hover; tb.Foreground = _t.Fg; } };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; if (_t != null) tb.Foreground = _t.FgDim; };
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return b;
        }

        List<TermProfile> Profiles() { return _s.ProfilesInUse(); }

        void ProfileMenu(FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            foreach (TermProfile p in Profiles())
            {
                TermProfile pp = p;
                var mi = new MenuItem { Header = p.Name };
                mi.Click += delegate { NewTab(pp); };
                m.Items.Add(mi);
            }
            AddConnectionItems(m);
            if (OpenSettings != null)
            {
                m.Items.Add(new Separator());
                var set = new MenuItem { Header = "Options…" };
                set.Click += delegate { OpenSettings(); };
                m.Items.Add(set);
            }
            OpenAtPointer(m);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            // Ctrl+Shift+T: new tab, Ctrl+Shift+W: close tab, Ctrl+Tab / Ctrl+Shift+Tab: next / previous tab
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ctrl && shift && k == Key.T) { e.Handled = true; NewTab(null); }
            else if (ctrl && shift && k == Key.O) { e.Handled = true; PopOutActive(); }
            else if (ctrl && shift && k == Key.W) { e.Handled = true; if (_active != null) CloseTab(_active); }
            else if (ctrl && k == Key.Tab && _sessions.Count > 1)
            {
                e.Handled = true;
                int i = _sessions.IndexOf(_active);
                Select(_sessions[(i + (shift ? -1 : 1) + _sessions.Count) % _sessions.Count]);
            }
        }

        // =================================================================
        // Tabs
        // =================================================================
        /// <summary>A new tab (null = the profile new tabs start with).</summary>
        public void NewTab(TermProfile p)
        {
            if (p == null) p = _s.StartProfile();
            EnsureWeb();
            var s = new Session { Id = "t" + (_nextId++), Profile = p, Title = p.Name, Owner = this };
            BuildTab(s);
            _sessions.Add(s);
            StartPty(s);
            Post(new { t = "new", id = s.Id });
            s.Created = true;
            Select(s);
            ShowEmpty();
        }

        /// <summary>A new tab connected to a saved (or quick) SSH connection; remote = a command to run there instead of its own.</summary>
        public void Connect(TermConnection c, string remote)
        {
            if (c == null) return;
            EnsureWeb();
            var p = new TermProfile { Name = c.Shown, Command = TermSsh.CommandLine(c, remote) };
            var s = new Session { Id = "t" + (_nextId++), Profile = p, Title = c.Shown, Conn = c, Remote = remote, Owner = this };
            BuildTab(s);
            _sessions.Add(s);
            StartPty(s);
            Post(new { t = "new", id = s.Id });
            s.Created = true;
            Select(s);
            ShowEmpty();
            ConnectionsChanged();
        }

        /// <summary>Start (or start again) the tab's shell / ssh.</summary>
        void StartPty(Session s)
        {
            var pty = new ConPty();
            s.Pty = pty;
            s.Over = false;
            Session me = s;
            // through the tab's owner at the time: the tab may have moved to another terminal since
            pty.Output += delegate (string text) { if (me.Pty == pty) { lock (me.Pending) me.Pending.Append(text); TerminalView o = me.Owner; if (o != null) o.OutputArrived(); } };
            pty.Exited += delegate (int code) { Dispatcher.BeginInvoke(new Action(delegate { if (me.Pty == pty && me.Owner != null) me.Owner.Ended(me, code); })); };
            string cmd = s.Conn != null ? TermSsh.CommandLine(s.Conn, s.Remote) : s.Profile.Command;
            try
            {
                if (s.Conn != null && !TermSsh.HaveSsh)
                    { lock (s.Pending) s.Pending.Append("\x1b[33mWindows' OpenSSH client isn't installed: Settings > System > Optional features > Add \"OpenSSH Client\".\x1b[0m\r\n"); OutputArrived(); }
                pty.Start(cmd, s.Conn != null ? null : s.Profile.Folder, s.Cols, s.Rows, s.Conn != null ? TermSsh.Env(s.Conn) : null);
            }
            catch (Exception ex)
            {
                Log.Write("Terminal start '" + (s.Conn != null ? "ssh " + s.Conn.Target : cmd) + "': " + ex.Message);
                lock (s.Pending) s.Pending.Append("\x1b[31mCouldn't start \"" + (s.Conn != null ? "ssh " + s.Conn.Target : cmd) + "\": " + ex.Message + "\x1b[0m\r\n");
                OutputArrived();
            }
            PaintTab(s);
        }

        void BuildTab(Session s)
        {
            s.Tab = new StripTab(s, s.Title) { ToolTip = s.Profile.Name + "\n" + s.Profile.Command };
            _tabs.Insert(s.Tab);
            PaintTab(s);
        }

        /// <summary>The tab's look is the strip's (vXplorer's); an SSH tab with its own colour has that colour as its line.</summary>
        void PaintTab(Session s)
        {
            if (s.Tab == null || _t == null) return;
            SolidColorBrush own = null;
            if (s.Conn != null && !string.IsNullOrWhiteSpace(s.Conn.Color)) try { own = _t.Resolve(s.Conn.Color, _t.MainAccent); } catch { }
            s.Tab.LineBrush = own;
        }

        void Select(Session s, bool focus = true)
        {
            _active = s;
            _tabs.Select(s.Tab);
            ActiveChanged();
            foreach (Session x in _sessions) PaintTab(x);
            Post(new { t = "show", id = s.Id });
            if (TitleChanged != null) TitleChanged();
            if (focus) FocusTerm();
        }

        /// <summary>A tab's right-click menu: into a window of its own / to another window, close.</summary>
        void TabMenu(Session s)
        {
            var m = new ContextMenu { PlacementTarget = _tabs, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            if ((_sessions.Count > 1 || LastTabCanLeave) && NewWindowFor != null)
            {
                var pop = new MenuItem { Header = "Move to a new window", InputGestureText = "Ctrl+Shift+O" };
                pop.Click += delegate { PopOut(s); };
                m.Items.Add(pop);
            }
            List<KeyValuePair<string, TerminalView>> others = OtherWindows != null ? OtherWindows() : null;
            if (others != null)
                foreach (var o in others)
                {
                    if (o.Value == null || o.Value == this) continue;
                    TerminalView to = o.Value;
                    var mb = new MenuItem { Header = "Move to " + o.Key };
                    mb.Click += delegate { MoveTo(s, to); };
                    m.Items.Add(mb);
                }
            if (m.Items.Count > 0) m.Items.Add(new Separator());
            var close = new MenuItem { Header = "Close tab", InputGestureText = "Ctrl+Shift+W" };
            close.Click += delegate { CloseTab(s); };
            m.Items.Add(close);
            OpenAtPointer(m);
        }

        // =================================================================
        // Moving a tab to another terminal (pop out / move back) - the shell keeps running
        // =================================================================
        readonly Dictionary<string, Action<string>> _serWait = new Dictionary<string, Action<string>>();

        /// <summary>The tab in front, into a window of its own (Ctrl+Shift+O). A window's only tab stays (the drop-down's can go).</summary>
        void PopOutActive()
        {
            if (_active != null && (_sessions.Count > 1 || LastTabCanLeave)) PopOut(_active);
        }

        /// <summary>The window is resized from its edges: the text window keeps clear of its left, right and bottom edges (not maximised).</summary>
        public bool KeepOffEdges;

        /// <summary>Its last tab may move into a window of its own (the drop-down: it hides then) - a window's last tab would just leave it empty.</summary>
        public bool LastTabCanLeave { get { return _tabs.CanTearLast; } set { _tabs.CanTearLast = value; } }

        /// <summary>A tab dragged out to here (screen pixels) gets its new window there.</summary>
        public static POINT? DropPoint;

        /// <summary>Move a tab into a new window of its own; its shell keeps running and its screen comes along.</summary>
        void PopOut(Session s)
        {
            if (NewWindowFor == null) return;
            MoveOut(s, NewWindowFor);
        }

        /// <summary>Move a tab into another window's terminal.</summary>
        void MoveTo(Session s, TerminalView to)
        {
            if (to == null || to == this) return;
            MoveOut(s, delegate (Handoff h) { to.Adopt(h); if (to.BringUp != null) to.BringUp(); });
        }

        /// <summary>
        /// Take a tab out of this terminal and hand it on: the page is asked for the tab's screen first; while it answers the tab's output
        /// waits (so nothing is drawn into the old screen after the copy), then the tab leaves and `to` gets it.
        /// </summary>
        void MoveOut(Session s, Action<Handoff> to)
        {
            if (s == null || s.Moving || !_sessions.Contains(s)) return;
            FlushOutput();          // the old screen up to date before it's copied
            s.Moving = true;
            bool done = false;
            Action<string> go = delegate (string screen)
            {
                if (done) return;
                done = true;
                _serWait.Remove(s.Id);
                Handoff h = Detach(s, screen);
                if (h == null) { s.Moving = false; return; }
                try { to(h); }
                catch (Exception ex)
                {
                    Log.Write("Terminal move: " + ex);
                    Adopt(h);       // couldn't go: back where it was
                }
            };
            if (!_webReady || _web == null) { go(""); return; }
            _serWait[s.Id] = go;
            Post(new { t = "ser", id = s.Id });
            // the page should answer at once; if it doesn't, go without the screen
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += delegate { t.Stop(); if (!done) { Log.Write("Terminal move: the page didn't send the screen - moving without it"); go(""); } };
            t.Start();
        }

        /// <summary>Take the tab out (its shell keeps running), without giving the keyboard to the tab left in front.</summary>
        Handoff Detach(Session s, string screen)
        {
            int i = _sessions.IndexOf(s);
            if (i < 0) return null;
            _sessions.RemoveAt(i);
            _tabs.Remove(s.Tab);
            Post(new { t = "close", id = s.Id });
            var h = new Handoff { S = s, Screen = screen ?? "", From = this, Title = s.Title };
            if (_sessions.Count == 0)
            {
                _active = null;
                ShowEmpty();
                Track();
                ConnectionsChanged();
                if (Emptied != null) Emptied();
                return h;
            }
            if (_active == s) Select(_sessions[Math.Min(i, _sessions.Count - 1)], false);
            ConnectionsChanged();
            return h;
        }

        /// <summary>Take in a tab from another terminal: its screen is drawn again, then whatever the shell wrote on the way.</summary>
        public void Adopt(Handoff h)
        {
            var s = h != null ? h.S as Session : null;
            if (s == null) return;
            EnsureWeb();
            s.Owner = this;
            s.Created = false;
            BuildTab(s);
            if (s.Over) s.Tab.Title = s.Title + (s.Conn != null ? " (disconnected)" : " (ended)");
            _sessions.Add(s);
            Post(new { t = "new", id = s.Id });
            if (!string.IsNullOrEmpty(h.Screen)) Post(new { t = "o", id = s.Id, d = h.Screen });
            s.Created = true;
            s.Moving = false;
            Select(s);
            ShowEmpty();
            ConnectionsChanged();
            OutputArrived();
        }

        void CloseTab(Session s)
        {
            int i = _sessions.IndexOf(s);
            if (i < 0) return;
            _sessions.RemoveAt(i);
            _tabs.Remove(s.Tab);
            try { s.Pty.Dispose(); } catch { }
            Post(new { t = "close", id = s.Id });
            if (_sessions.Count == 0) { _active = null; ShowEmpty(); Track(); if (Emptied != null) Emptied(); return; }
            if (_active == s) Select(_sessions[Math.Min(i, _sessions.Count - 1)]);
        }

        void Ended(Session s, int code)
        {
            if (!_sessions.Contains(s)) return;
            FlushOutput();
            s.Over = true;
            try { s.Pty.Dispose(); } catch { }
            if (s.Conn != null)
            {
                // an SSH tab stays: Enter connects again
                Post(new { t = "exited", id = s.Id, text = "Disconnected from " + s.Conn.Target + (code != 0 ? " (code " + code + ")" : "") + " - press Enter to connect again, or close the tab" });
                s.Tab.Title = s.Title + " (disconnected)";
                PaintTab(s);
                ConnectionsChanged();
                return;
            }
            if (code == 0) { CloseTab(s); return; }
            Post(new { t = "exited", id = s.Id, text = "The shell ended (exit code " + code + ") - close this tab or open a new one" });
            s.Tab.Title = s.Title + " (ended)";
        }

        /// <summary>No tabs: a line to click for one (the first profile).</summary>
        void ShowEmpty()
        {
            bool none = _sessions.Count == 0;
            _empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            _body.Visibility = none ? Visibility.Hidden : Visibility.Visible;
            if (none) _emptyText.Text = "Click to open " + _s.StartProfile().Name;
        }

        /// <summary>Put the keyboard in the terminal.</summary>
        public void FocusTerm()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                Track();
                if (TabPressed) return;     // a tab is being pressed / dragged: the keyboard comes after (the click selects, the drop focuses)
                if (_surface != null && _surface.Handle != IntPtr.Zero && _placed.Width > 0)
                {
                    try
                    {
                        uint pid;
                        IntPtr fg = Native.GetForegroundWindow();
                        uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out pid);
                        uint me = Native.GetCurrentThreadId();
                        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                        try { Native.SetForegroundWindow(_surface.Handle); }
                        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
                        _surface.Activate();
                    }
                    catch (Exception ex) { Log.Write("Terminal focus: " + ex.Message); }
                }
                try { if (_web != null) { _web.Focus(); Keyboard.Focus(_web); } } catch { }
                Post(new { t = "focus" });
            }));
        }

        /// <summary>End every shell (the view is going away for good).</summary>
        public void EndAll()
        {
            Live.Remove(this);
            _flush.Stop();
            _track.Stop();
            TermSsh.Changed -= ConnectionsChanged;
            CloseSftp();
            foreach (Session s in _sessions) try { s.Pty.Dispose(); } catch { }
            _sessions.Clear();
            try { if (_web != null) _web.Dispose(); } catch { }
            _web = null;
            try { if (_surface != null) _surface.Close(); } catch { }
            _surface = null;
        }

        // =================================================================
        // The page
        // =================================================================
        async void EnsureWeb()
        {
            if (_web != null || _webFailed) return;
            Window owner = Window.GetWindow(this);
            if (owner == null || !owner.IsLoaded) { _pendingWeb = true; return; }       // when the view is in its window
            try
            {
                string dir = ExtractAssets();
                _surface = new TermSurface(owner, _s.SeeThrough, SolidBg());
                // its window moving: follow straight away, even while checking slowly
                owner.LocationChanged += delegate { _still = 0; Track(); };
                _surface.Deactivated += delegate { if (SurfaceDeactivated != null) SurfaceDeactivated(); };
                _surface.Show();
                _web = _surface.Web;
                Log.Write("Terminal: display window made, starting WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataFolder, "WebView2"));
                await _web.EnsureCoreWebView2Async(env);
                CoreWebView2 core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;     // F5 / Ctrl+R / Ctrl+F go to the shell, not the page
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping("vterm.local", dir, CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += OnMessage;
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; OpenLink(e.Uri); };
                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (!e.Uri.StartsWith("https://vterm.local/", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenLink(e.Uri); }
                };
                core.Navigate("https://vterm.local/term.html");
                Log.Write("Terminal: WebView2 " + env.BrowserVersionString + " ready, loading the page");
                _placed = new RECT();
                Track();
            }
            catch (Exception ex)
            {
                _webFailed = true;
                Log.Write("Terminal WebView2: " + ex);
                bool missing = ex is WebView2RuntimeNotFoundException;
                try { if (_surface != null) _surface.Close(); } catch { }
                _surface = null;
                _body.Child = new TextBlock
                {
                    Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Foreground = _t != null ? _t.Fg : Brushes.White,
                    Text = missing ? "The terminal needs the Microsoft Edge WebView2 Runtime (part of Windows 11; for Windows 10 get it from " +
                                     "https://developer.microsoft.com/microsoft-edge/webview2/), then start vTerm again."
                                   : "The terminal couldn't start its display: " + ex.Message
                };
            }
        }

        static string _assets;
        static string ExtractAssets()
        {
            if (_assets != null) return _assets;
            string dir = Path.Combine(App.DataFolder, "terminal");
            Directory.CreateDirectory(dir);
            Assembly asm = typeof(TerminalView).Assembly;
            const string prefix = "vTerm.Terminal.";
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string file = Path.Combine(dir, name.Substring(prefix.Length));
                using (Stream src = asm.GetManifestResourceStream(name))
                using (var ms = new MemoryStream())
                {
                    src.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    // the same bytes already there (a new vTerm's page replaces an older one even when it's the same size)
                    bool same = File.Exists(file) && new FileInfo(file).Length == bytes.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes);
                    if (!same) File.WriteAllBytes(file, bytes);
                }
            }
            _assets = dir;
            return dir;
        }

        void Post(object msg)
        {
            string json = JsonSerializer.Serialize(msg);
            if (!_webReady || _web == null || _web.CoreWebView2 == null) { _queue.Add(json); return; }
            try { _web.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write("Terminal post: " + ex.Message); }
        }

        int _flushOn;

        /// <summary>A shell wrote something (any thread): make sure the flush timer runs.</summary>
        void OutputArrived()
        {
            if (System.Threading.Interlocked.Exchange(ref _flushOn, 1) == 1) return;
            Dispatcher.BeginInvoke(new Action(delegate { if (!_flush.IsEnabled) _flush.Start(); }));
        }

        void FlushOutput()
        {
            // nothing waiting in any tab: stop until more comes
            // (a tab on its way to another terminal keeps its output for that one)
            bool any = false;
            foreach (Session x in _sessions.ToArray()) if (!x.Moving) lock (x.Pending) if (x.Pending.Length > 0) { any = true; break; }
            if (!any)
            {
                _flush.Stop();
                System.Threading.Interlocked.Exchange(ref _flushOn, 0);
                // a write that slipped in between: go again
                foreach (Session x in _sessions.ToArray()) if (!x.Moving) lock (x.Pending) if (x.Pending.Length > 0) { OutputArrived(); break; }
                return;
            }
            if (!_webReady) return;
            foreach (Session s in _sessions.ToArray())
            {
                if (!s.Created || s.Moving) continue;
                string text;
                lock (s.Pending)
                {
                    if (s.Pending.Length == 0) continue;
                    text = s.Pending.ToString();
                    s.Pending.Clear();
                }
                Post(new { t = "o", id = s.Id, d = text });
            }
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson))
                {
                    JsonElement r = doc.RootElement;
                    string kind = Str(r, "t");
                    string id = Str(r, "id");
                    Session s = id.Length > 0 ? _sessions.Find(delegate (Session x) { return x.Id == id; }) : null;
                    switch (kind)
                    {
                        case "ready":
                            Log.Write("Terminal: page ready");
                            _webReady = true;
                            PostTheme();
                            foreach (string q in _queue) try { _web.CoreWebView2.PostWebMessageAsJson(q); } catch { }
                            _queue.Clear();
                            FlushOutput();
                            FocusTerm();
                            break;
                        case "i":
                            if (s == null) break;
                            if (s.Over)
                            {
                                // a disconnected SSH tab: Enter connects again
                                if (s.Conn != null && Str(r, "d").Contains("\r")) { s.Tab.Title = s.Title; Post(new { t = "o", id = s.Id, d = "\r\n" }); StartPty(s); }
                                break;
                            }
                            s.Pty.Write(Str(r, "d"));
                            break;
                        case "r":
                            if (s != null)
                            {
                                JsonElement c, rr;
                                if (r.TryGetProperty("c", out c) && r.TryGetProperty("r", out rr))
                                {
                                    s.Cols = c.GetInt32(); s.Rows = rr.GetInt32();
                                    if (!s.Over) s.Pty.Resize(s.Cols, s.Rows);
                                }
                            }
                            break;
                        case "title":
                            if (s != null)
                            {
                                // an SSH tab keeps its connection's name (ssh sets the title to its own path); a shell's title only
                                // when it's more than the program's path (cmd / PowerShell start with C:\Windows\...\x.exe)
                                if (s.Conn != null) break;
                                string v = Str(r, "v").Trim();
                                if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = "";
                                s.Title = v.Length > 0 ? v : s.Profile.Name;
                                s.Tab.Title = s.Title;
                                if (s == _active && TitleChanged != null) TitleChanged();
                            }
                            break;
                        case "copy":
                            try { Clipboard.SetText(Str(r, "d")); } catch { }
                            break;
                        case "paste":
                            if (s != null)
                            {
                                string text = "";
                                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                                if (text.Length > 0) Post(new { t = "paste", id = s.Id, d = text });
                            }
                            break;
                        case "link": OpenLink(Str(r, "u")); break;
                        case "ser":
                            {
                                // the screen of a tab that's moving to another terminal
                                Action<string> go;
                                if (_serWait.TryGetValue(id, out go)) go(Str(r, "d"));
                            }
                            break;
                        case "key":
                            // the keys the page passes on (the keyboard is in the text window, not in vTerm's)
                            switch (Str(r, "k"))
                            {
                                case "new": NewTab(null); break;
                                case "close": if (_active != null) CloseTab(_active); break;
                                case "next":
                                case "prev":
                                    if (_sessions.Count > 1)
                                    {
                                        int i = _sessions.IndexOf(_active);
                                        Select(_sessions[(i + (Str(r, "k") == "prev" ? -1 : 1) + _sessions.Count) % _sessions.Count]);
                                    }
                                    break;
                                case "hide": if (HideRequested != null) HideRequested(); break;
                                case "popout": PopOutActive(); break;
                            }
                            break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Terminal message: " + ex.Message); }
        }

        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        static void OpenLink(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;
            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Terminal link: " + ex.Message); }
        }
    }
}