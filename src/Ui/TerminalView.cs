using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// A terminal with tabs: each tab a shell (PowerShell, cmd, WSL ...) in a Windows pseudo console (ConPty), its screen kept by
    /// libghostty-vt (TermEngine) and drawn by vTerm (TermScreen) right in this window. The tab strip looks like vShell's widget tabs; the
    /// terminal's colours come from the vShell theme. It has no background of its own, so the window holding it shows through - glass and
    /// all. Each vTerm window (MainWindow) holds one; a tab can move from one window to another with its shell and screen as they are.
    /// </summary>
    public sealed partial class TerminalView : Grid
    {
        sealed class Session
        {
            public string Id;
            public TermProfile Profile;
            public ConPty Pty;
            /// <summary>Its screen and scrollback (moves with the tab).</summary>
            public TermEngine Engine;
            public string Title;
            /// <summary>Its tab in the strip (vSuite's StripTab, as vXplorer's).</summary>
            public StripTab Tab;
            /// <summary>An SSH tab: its connection (and a command run there instead of the saved one), else null.</summary>
            public TermConnection Conn;
            public string Remote;
            /// <summary>The shell / connection has ended (an SSH tab reconnects on Enter).</summary>
            public bool Over;
            public int Cols = 120, Rows = 30;
            /// <summary>The terminal showing it now (a tab can move: popped out into a window, and back).</summary>
            public TerminalView Owner;
        }

        /// <summary>A tab on its way from one terminal to another (popped out / moved back): its shell and screen, as they are.</summary>
        public sealed class Handoff
        {
            internal object S;
            /// <summary>The terminal it came from.</summary>
            public TerminalView From;
            public string Title = "";
        }

        readonly AppSettings _s;
        readonly List<Session> _sessions = new List<Session>();
        Session _active;
        static int _nextId = 1;
        TermLook _t;
        readonly Border _strip = new Border();
        readonly TabStrip _tabs = new TabStrip { TabHeight = 32, Reserve = 40, VerticalAlignment = VerticalAlignment.Bottom };
        readonly Border _body = new Border { Background = Brushes.Transparent };
        readonly TermScreen _screen = new TermScreen();
        readonly Border _empty = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        readonly TextBlock _emptyText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        readonly StackPanel _right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        Gt.Rgb[] _ansi;
        Gt.Rgb _fg, _bg, _cursor;

        /// <summary>The last tab was closed (or moved away).</summary>
        public event Action Emptied;
        /// <summary>"Options…" from the new-tab menu.</summary>
        public Action OpenSettings;
        /// <summary>Shift+Esc (the drop-down hides; null = nothing).</summary>
        public Action HideRequested;
        /// <summary>A dialog of the side panel is open (the drop-down mustn't hide for it).</summary>
        public bool InDialog { get { return Busy > 0; } }
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
            _empty.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e) { e.Handled = true; NewTab(null); };
            _body.Child = _screen;
            _screen.Input += delegate (byte[] b) { if (_active != null) SendTo(_active, b); };
            _screen.Resized += OnScreenResized;
            _screen.LinkOpened += OpenLink;
            PreviewKeyDown += OnKey;
            ApplyColors(null);
            ShowEmpty();
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
            PanelTheme();
            ApplyColors(t);
        }

        /// <summary>The terminal's background when it isn't see-through: the theme colour, solid.</summary>
        Color SolidBg() { return _t == null ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Solid(_t.BgColor); }

        bool Dark()
        {
            if (_t == null) return true;
            Color bg = _t.BgColor;
            return (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 < 140;
        }

        /// <summary>The terminal's colours (text, background, cursor, the 16 colours) into every tab's engine, and the screen's look.</summary>
        void ApplyColors(TermLook t)
        {
            Color fg = t != null ? t.Fg.Color : Color.FromRgb(0xE6, 0xE6, 0xE6), bg = SolidBg();
            Color ac = t != null ? t.MainAccent.Color : Color.FromRgb(0x7A, 0xA2, 0xF7), sec = t != null ? t.SecondaryAccent.Color : Color.FromRgb(0xBB, 0x9A, 0xF7);
            string[] pal = Palette(_s.Colors, Dark(), bg, Solid(fg), Solid(ac), Solid(sec));
            _ansi = new Gt.Rgb[16];
            for (int i = 0; i < 16; i++) _ansi[i] = Rgb(FromCss(pal[i]));
            _fg = Rgb(Solid(fg)); _bg = Rgb(bg); _cursor = Rgb(Solid(ac));
            foreach (Session x in _sessions) if (x.Engine != null) x.Engine.SetColors(_fg, _bg, _cursor, _ansi);
            _screen.ApplyLook(_s.Font, _s.FontSize, _s.SeeThrough, bg, Solid(ac), Color.FromArgb(0x61, ac.R, ac.G, ac.B), Color.FromArgb(0x59, fg.R, fg.G, fg.B));
        }

        static Gt.Rgb Rgb(Color c) { return new Gt.Rgb { R = c.R, G = c.G, B = c.B }; }

        static Color FromCss(string css)
        {
            try { return (Color)ColorConverter.ConvertFromString(css); } catch { return Color.FromRgb(0x80, 0x80, 0x80); }
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
            _tabs.AddButton(StripButton("\uE710", "New tab (Ctrl+Shift+T)", delegate { NewTab(null); }));
            Border pick = null;
            pick = StripButton("\uE70D", "New tab with…", delegate { ProfileMenu(pick); });
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
            if (on) _right.Children.Add(StripButton("\uE921", "Hide (the shells keep running)", delegate { if (HideRequested != null) HideRequested(); }));
            if (_t != null) ApplyTheme(_t);
        }

        /// <summary>
        /// Open a menu of the terminal's at the mouse pointer, placed on the screen itself: WPF's own placement (under the button, or at the
        /// pointer) put it in the top-left corner of the screen in the drop-down - its window is placed in device pixels outside WPF.
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
            // Ctrl+Shift+T: new tab, Ctrl+Shift+W: close tab, Ctrl+Tab / Ctrl+Shift+Tab: next / previous tab, Ctrl+Shift+O: into a new
            // window, Shift+Esc: hide (the drop-down)
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ctrl && shift && !alt && k == Key.T) { e.Handled = true; NewTab(null); }
            else if (ctrl && shift && !alt && k == Key.O) { e.Handled = true; PopOutActive(); }
            else if (ctrl && shift && !alt && k == Key.W) { e.Handled = true; if (_active != null) CloseTab(_active); }
            else if (ctrl && !alt && k == Key.Tab && _sessions.Count > 1)
            {
                e.Handled = true;
                int i = _sessions.IndexOf(_active);
                Select(_sessions[(i + (shift ? -1 : 1) + _sessions.Count) % _sessions.Count]);
            }
            else if (shift && !ctrl && !alt && k == Key.Escape && HideRequested != null) { e.Handled = true; HideRequested(); }
        }

        // =================================================================
        // Tabs
        // =================================================================
        /// <summary>A new tab (null = the profile new tabs start with).</summary>
        public void NewTab(TermProfile p)
        {
            if (p == null) p = _s.StartProfile();
            var s = NewSession(p, p.Name);
            if (s == null) return;
            BuildTab(s);
            _sessions.Add(s);
            StartPty(s);
            Select(s);
            ShowEmpty();
        }

        /// <summary>A new tab connected to a saved (or quick) SSH connection; remote = a command to run there instead of its own.</summary>
        public void Connect(TermConnection c, string remote)
        {
            if (c == null) return;
            var p = new TermProfile { Name = c.Shown, Command = TermSsh.CommandLine(c, remote) };
            var s = NewSession(p, c.Shown);
            if (s == null) return;
            s.Conn = c;
            s.Remote = remote;
            BuildTab(s);
            _sessions.Add(s);
            StartPty(s);
            Select(s);
            ShowEmpty();
            ConnectionsChanged();
        }

        /// <summary>A tab's session with its terminal, the size the screen has room for. Null when the terminal engine can't load.</summary>
        Session NewSession(TermProfile p, string title)
        {
            var s = new Session { Id = "t" + (_nextId++), Profile = p, Title = title, Owner = this };
            if (_screen.FitCols > 0) { s.Cols = _screen.FitCols; s.Rows = _screen.FitRows; }
            try { s.Engine = new TermEngine(s.Cols, s.Rows, 10000); }
            catch (Exception ex)
            {
                Log.Write("Terminal engine: " + ex);
                Looks.Tell(Window.GetWindow(this), "The terminal couldn't start (libghostty-vt): " + ex.Message, "vTerm");
                return null;
            }
            s.Engine.SetColors(_fg, _bg, _cursor, _ansi);
            Session me = s;
            s.Engine.Reply += delegate (byte[] b) { ConPty pty = me.Pty; if (pty != null && !me.Over) pty.Write(b); };
            s.Engine.TitleChanged += delegate (string v) { Dispatcher.BeginInvoke(new Action(delegate { TitleFromShell(me, v); })); };
            return s;
        }

        /// <summary>Start (or start again) the tab's shell / ssh.</summary>
        void StartPty(Session s)
        {
            var pty = new ConPty();
            s.Pty = pty;
            s.Over = false;
            Session me = s;
            // straight into the tab's terminal on the reader thread: it keeps up, and the window draws when it's ready
            pty.Data += delegate (byte[] buf, int n) { if (me.Pty == pty && me.Engine != null) me.Engine.Feed(buf, n); };
            pty.Exited += delegate (int code) { Dispatcher.BeginInvoke(new Action(delegate { if (me.Pty == pty && me.Owner != null) me.Owner.Ended(me, code); })); };
            string cmd = s.Conn != null ? TermSsh.CommandLine(s.Conn, s.Remote) : s.Profile.Command;
            try
            {
                if (s.Conn != null && !TermSsh.HaveSsh)
                    s.Engine.Feed("\x1b[33mWindows' OpenSSH client isn't installed: Settings > System > Optional features > Add \"OpenSSH Client\".\x1b[0m\r\n");
                pty.Start(cmd, s.Conn != null ? null : s.Profile.Folder, s.Cols, s.Rows, s.Conn != null ? TermSsh.Env(s.Conn) : null);
            }
            catch (Exception ex)
            {
                Log.Write("Terminal start '" + (s.Conn != null ? "ssh " + s.Conn.Target : cmd) + "': " + ex.Message);
                s.Engine.Feed("\x1b[31mCouldn't start \"" + (s.Conn != null ? "ssh " + s.Conn.Target : cmd) + "\": " + ex.Message + "\x1b[0m\r\n");
            }
            PaintTab(s);
        }

        /// <summary>Typed / clicked input for a tab's shell. A disconnected SSH tab: Enter connects again.</summary>
        void SendTo(Session s, byte[] b)
        {
            if (s == null || b == null || b.Length == 0) return;
            if (s.Over)
            {
                if (s.Conn != null && Array.IndexOf(b, (byte)'\r') >= 0) { s.Tab.Title = s.Title; s.Engine.Feed("\r\n"); StartPty(s); }
                return;
            }
            if (s.Pty != null) s.Pty.Write(b);
        }

        /// <summary>The screen has room for a new number of cells: the tab in front (its terminal and shell) takes that size.</summary>
        void OnScreenResized(int cols, int rows, int cellW, int cellH)
        {
            Session s = _active;
            if (s == null || s.Engine == null) return;
            s.Cols = cols; s.Rows = rows;
            s.Engine.Resize(cols, rows, cellW, cellH);
            if (!s.Over && s.Pty != null) s.Pty.Resize(cols, rows);
        }

        /// <summary>The program set a title: an SSH tab keeps its connection's name (ssh sets the title to its own path); a shell's
        /// title only when it's more than the program's path (cmd / PowerShell start with C:\Windows\...\x.exe).</summary>
        void TitleFromShell(Session s, string v)
        {
            if (s == null || s.Conn != null || s.Tab == null) return;
            v = (v ?? "").Trim();
            if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = "";
            s.Title = v.Length > 0 ? v : s.Profile.Name;
            if (!s.Over) s.Tab.Title = s.Title;
            if (s == _active && TitleChanged != null) TitleChanged();
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
            _screen.Show(s.Engine);
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
        // Moving a tab to another terminal (pop out / move back) - the shell keeps running, the screen goes along as it is
        // =================================================================
        /// <summary>The tab in front, into a window of its own (Ctrl+Shift+O). A window's only tab stays (the drop-down's can go).</summary>
        void PopOutActive()
        {
            if (_active != null && (_sessions.Count > 1 || LastTabCanLeave)) PopOut(_active);
        }

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

        /// <summary>Take a tab out of this terminal and hand it on: its terminal (screen, scrollback) and shell go as they are.</summary>
        void MoveOut(Session s, Action<Handoff> to)
        {
            if (s == null || !_sessions.Contains(s)) return;
            Handoff h = Detach(s);
            if (h == null) return;
            try { to(h); }
            catch (Exception ex)
            {
                Log.Write("Terminal move: " + ex);
                Adopt(h);       // couldn't go: back where it was
            }
        }

        /// <summary>Take the tab out (its shell keeps running), without giving the keyboard to the tab left in front.</summary>
        Handoff Detach(Session s)
        {
            int i = _sessions.IndexOf(s);
            if (i < 0) return null;
            _sessions.RemoveAt(i);
            _tabs.Remove(s.Tab);
            var h = new Handoff { S = s, From = this, Title = s.Title };
            if (_sessions.Count == 0)
            {
                _active = null;
                _screen.Show(null);
                ShowEmpty();
                ConnectionsChanged();
                if (Emptied != null) Emptied();
                return h;
            }
            if (_active == s) Select(_sessions[Math.Min(i, _sessions.Count - 1)], false);
            ConnectionsChanged();
            return h;
        }

        /// <summary>Take in a tab from another terminal: its screen is as it was, and it takes this window's size and colours.</summary>
        public void Adopt(Handoff h)
        {
            var s = h != null ? h.S as Session : null;
            if (s == null) return;
            s.Owner = this;
            BuildTab(s);
            if (s.Over) s.Tab.Title = s.Title + (s.Conn != null ? " (disconnected)" : " (ended)");
            _sessions.Add(s);
            if (s.Engine != null) s.Engine.SetColors(_fg, _bg, _cursor, _ansi);
            Select(s);
            ShowEmpty();
            ConnectionsChanged();
        }

        void CloseTab(Session s)
        {
            int i = _sessions.IndexOf(s);
            if (i < 0) return;
            _sessions.RemoveAt(i);
            _tabs.Remove(s.Tab);
            if (_sessions.Count == 0) { _active = null; _screen.Show(null); }
            else if (_active == s) Select(_sessions[Math.Min(i, _sessions.Count - 1)]);
            try { if (s.Pty != null) s.Pty.Dispose(); } catch { }
            s.Pty = null;
            TermEngine e = s.Engine;
            s.Engine = null;
            // the reader may be inside Feed a moment longer: the engine goes once it's done (Dispose waits for the lock)
            if (e != null) System.Threading.ThreadPool.QueueUserWorkItem(delegate { e.Dispose(); });
            if (_sessions.Count == 0) { ShowEmpty(); if (Emptied != null) Emptied(); }
        }

        void Ended(Session s, int code)
        {
            if (!_sessions.Contains(s)) return;
            s.Over = true;
            try { s.Pty.Dispose(); } catch { }
            if (s.Conn != null)
            {
                // an SSH tab stays: Enter connects again
                s.Engine.Feed("\r\n\x1b[2m[Disconnected from " + s.Conn.Target + (code != 0 ? " (code " + code + ")" : "") + " - press Enter to connect again, or close the tab]\x1b[0m\r\n");
                s.Tab.Title = s.Title + " (disconnected)";
                PaintTab(s);
                ConnectionsChanged();
                return;
            }
            if (code == 0) { CloseTab(s); return; }
            s.Engine.Feed("\r\n\x1b[2m[The shell ended (exit code " + code + ") - close this tab or open a new one]\x1b[0m\r\n");
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
                if (TabPressed || _sessions.Count == 0) return;     // a tab is being pressed / dragged: the keyboard comes after
                if (Busy > 0) return;                               // a dialog of the side panel is open
                try { _screen.Focus(); Keyboard.Focus(_screen); } catch { }
            }));
        }

        /// <summary>End every shell (the view is going away for good).</summary>
        public void EndAll()
        {
            Live.Remove(this);
            TermSsh.Changed -= ConnectionsChanged;
            CloseSftp();
            _screen.Show(null);
            foreach (Session s in _sessions)
            {
                try { if (s.Pty != null) s.Pty.Dispose(); } catch { }
                try { if (s.Engine != null) s.Engine.Dispose(); } catch { }
            }
            _sessions.Clear();
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
