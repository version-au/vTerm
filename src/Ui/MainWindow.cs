using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// A vTerm window: the suite's borderless window (the vShell theme, glass at the theme's opacity, Windows 11 corners and outline) holding
    /// one TerminalView. Moved by the tab strip's empty part (double-click: maximise), resized from its edges. Tabs move between windows with
    /// their shells running (a tab's menu, Ctrl+Shift+O); a window whose last tab leaves closes. vTerm runs until its last window closes.
    /// </summary>
    public sealed partial class MainWindow : Window, IThemedWindow
    {
        readonly AppSettings _s;
        readonly VShellThemeSource _themeSource;
        readonly TerminalView _view;
        readonly Grid _root = new Grid();
        readonly Action _themeChanged;
        readonly Action _updateChanged;
        IntPtr _hwnd;
        bool _quiet;
        Theme _theme;
        bool _blur;
        WindowIcons _icons;
        NoticeWindow _notice;
        readonly Image _appMark = new Image { Width = 18, Height = 18 };
        Button _appBtn;

        public Theme CurrentTheme { get { return _theme; } }
        public bool Blurred { get { return _blur; } }
        public TerminalView View { get { return _view; } }

        static int _count;
        readonly int _number;

        /// <summary>A window. adopt = a tab moving in from another window (no tab of its own then); first = vTerm's first window (where it was).</summary>
        public MainWindow(AppSettings s, VShellThemeSource themeSource, TerminalView.Handoff adopt, bool first)
        {
            _s = s;
            _themeSource = themeSource;
            _number = ++_count;
            Title = "vTerm";
            Width = 960; Height = 580; MinWidth = 420; MinHeight = 220;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12.5;
            UseLayoutRounding = true;
            Background = Brushes.Transparent;
            Topmost = s.Topmost;
            SetResourceReference(ForegroundProperty, "vx.Fg");
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,     // moved by the tab strip (StripPressed)
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            _view = new TerminalView(s);
            _view.Emptied += delegate { if (!_closing) { _quiet = true; Dispatcher.BeginInvoke(new Action(Close)); } };
            _view.StripPressed += OnStripPressed;
            _view.TitleChanged += delegate { string a = _view.ActiveTitle; Title = a.Length > 0 ? a + " - vTerm" : "vTerm"; };
            _view.OpenSettings = delegate { OpenOptions(null); };
            _view.NewWindowFor = delegate (TerminalView.Handoff h) { OpenWindow(h); };
            _view.OtherWindows = delegate { return Targets(this); };
            _view.BringUp = delegate { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); };
            // vTerm's icon at the top left, as in vXplorer and vView: its menu (new tab / window, Options, updates, About, close)
            _appBtn = new Button { Width = 34, Height = 28, Margin = new Thickness(0, 0, 2, 0), ToolTip = "vTerm menu", Content = _appMark, Focusable = false };
            _appBtn.SetResourceReference(StyleProperty, "vx.IconButton");
            _appBtn.Click += delegate { ShowMenu(); };
            _view.SetLead(_appBtn);
            _root.SetResourceReference(Panel.BackgroundProperty, "vx.Bg");
            _root.Children.Add(_view);
            Content = _root;
            Buttons();

            // a tab dragged out into the open: its new window goes where it was dropped (the tabs under the pointer)
            POINT? dropAt = adopt != null ? TerminalView.DropPoint : null;
            TerminalView.DropPoint = null;
            if (first) RestorePlacement();
            if (dropAt != null) { WindowState = WindowState.Normal; WindowStartupLocation = WindowStartupLocation.Manual; }
            else if (!first) WindowStartupLocation = WindowStartupLocation.CenterScreen;

            SourceInitialized += delegate
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                if (dropAt != null)
                {
                    double scale = Math.Max(0.5, Native.GetDpiForWindow(_hwnd) / 96.0);
                    POINT p = dropAt.Value;
                    Native.SetWindowPos(_hwnd, IntPtr.Zero, p.X - (int)(120 * scale), p.Y - (int)(16 * scale), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
                ApplyTheme();
            };
            StateChanged += delegate { UpdateMaximized(); };
            // clicked / Alt+Tabbed to: the keyboard goes to the text
            Activated += delegate { _view.FocusTerm(); };
            Loaded += delegate
            {
                if (adopt != null) _view.Adopt(adopt);
                if (_notice == null) { _notice = new NoticeWindow(this, _root); _notice.ApplyLook(_theme, _blur); _notice.Sync(); }
            };
            Closing += OnClosing;
            Closed += delegate { Dispatcher.BeginInvoke(new Action(Hub.MaybeQuit)); };
            _themeChanged = delegate { Dispatcher.BeginInvoke(new Action(ApplyTheme)); };
            _themeSource.Changed += _themeChanged;
            _updateChanged = delegate { Dispatcher.BeginInvoke(new Action(Buttons)); };
            UpdateCenter.Changed += _updateChanged;
        }

        /// <summary>Every open vTerm window.</summary>
        public static List<MainWindow> All { get { return Application.Current.Windows.OfType<MainWindow>().Where(w => w.IsLoaded && !w._closing).ToList(); } }

        /// <summary>The window most recently used (for things handed over from another start).</summary>
        public static MainWindow Front
        {
            get
            {
                List<MainWindow> all = All;
                return all.FirstOrDefault(w => w.IsActive) ?? all.LastOrDefault();
            }
        }

        // =================================================================
        // Theme
        // =================================================================
        void ApplyTheme()
        {
            Theme t = Theme.From(_themeSource.Current);
            bool blur = _s.ThemeOpacity;
            _theme = t;
            _blur = blur;
            Looks.Theme = t;
            Looks.Blur = blur;
            t.Apply(Application.Current.Resources, blur);
            WindowEffects.Look(this, t, blur);
            if (_icons == null) _icons = new WindowIcons(this);
            _icons.Apply(t);
            try { RenderOptions.SetBitmapScalingMode(_appMark, BitmapScalingMode.HighQuality); _appMark.Source = AppIcon.Image(t, true); }
            catch (Exception ex) { Log.Write("Icon: " + ex.Message); }
            var look = TermLook.From(t);
            _view.ApplyTheme(look);
            if (_notice != null) _notice.ApplyLook(t, blur);
            if (OptionsWindow.Current != null && OptionsWindow.Current.Owner == this) OptionsWindow.Current.ApplyTheme(t, blur);
        }

        /// <summary>Options changed something (text, colours, opacity, keep on top): every window shows it.</summary>
        public static void SettingsChanged()
        {
            foreach (MainWindow w in All)
            {
                w.Topmost = w._s.Topmost;
                w.ApplyTheme();
                w.Buttons();
            }
        }

        // =================================================================
        // Placement, moving, maximise
        // =================================================================
        void RestorePlacement()
        {
            if (_s.Width >= MinWidth && _s.Height >= MinHeight)
            {
                var r = new Rect(_s.Left, _s.Top, _s.Width, _s.Height);
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (screen.IntersectsWith(r))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
                    if (_s.Maximized) WindowState = WindowState.Maximized;
                    return;
                }
            }
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void Remember()
        {
            Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (r.Width < MinWidth || r.Height < MinHeight || double.IsInfinity(r.Width)) return;
            _s.Left = r.Left; _s.Top = r.Top; _s.Width = r.Width; _s.Height = r.Height;
            _s.Maximized = WindowState == WindowState.Maximized;
        }

        void OnStripPressed(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { e.Handled = true; ToggleMax(); return; }
            e.Handled = true;
            try { DragMove(); } catch { }
        }

        void ToggleMax() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

        void UpdateMaximized()
        {
            // a borderless window maximised hangs off the screen by its frame: keep the content on screen
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(WindowEffects.MaximizedOverhang(_hwnd)) : new Thickness(0);
            Buttons();
        }

        // =================================================================
        // The buttons right of the tabs
        // =================================================================
        void Buttons()
        {
            var b = new List<Tuple<string, string, Action>>();
            if (UpdateCenter.Available != null && !UpdateCenter.IsSkipped(UpdateCenter.Available) && !UpdateCenter.NoticeVisible)
                b.Add(Tuple.Create("", "vTerm " + UpdateCenter.VersionText(UpdateCenter.Available) + " is out - see Options > Updates", (Action)delegate { OpenOptions("Updates"); }));
            b.Add(Tuple.Create(Topmost ? "" : "", Topmost ? "Kept on top - click to let other windows cover it" : "Keep on top of other windows", (Action)TogglePin));
            b.Add(Tuple.Create("", "Minimise", (Action)delegate { WindowState = WindowState.Minimized; }));
            bool max = WindowState == WindowState.Maximized;
            b.Add(Tuple.Create(max ? "" : "", max ? "Restore" : "Maximise", (Action)ToggleMax));
            b.Add(Tuple.Create("", "Close", (Action)Close));
            _view.SetButtons(b);
        }

        void TogglePin()
        {
            _s.Topmost = !_s.Topmost;
            _s.SaveSoon();
            foreach (MainWindow w in All) { w.Topmost = _s.Topmost; w.Buttons(); }
        }

        void ShowMenu()
        {
            var m = new ContextMenu { PlacementTarget = _appBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            Add(m, "", "New tab", "Ctrl+Shift+T", true, delegate { _view.NewTab(null); });
            Add(m, "", "New window", "Ctrl+Shift+N", true, delegate { OpenWindow(null); });
            m.Items.Add(new Separator());
            Add(m, "", "Options", null, true, delegate { OpenOptions(null); });
            Add(m, "", "Check for updates", null, !UpdateCenter.Busy && !UpdateCenter.Installing, delegate { OpenOptions("Updates"); UpdateCenter.CheckNow(true); });
            Add(m, "", "About vTerm", null, true, delegate { OpenOptions("About"); });
            m.Items.Add(new Separator());
            Add(m, "", "Close window", "Alt+F4", true, Close);
            m.IsOpen = true;
        }

        static void Add(ItemsControl m, string glyph, string text, string keys, bool enabled, Action a)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            var g = new TextBlock { Text = glyph, FontFamily = TermLook.Glyphs, FontSize = 13 };
            mi.Icon = g;
            if (keys != null) mi.InputGestureText = keys;
            mi.Click += delegate { a(); };
            m.Items.Add(mi);
        }

        public void OpenOptions(string page)
        {
            OptionsWindow.Open(this, _s, _themeSource, _theme, _blur, page);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && shift && e.Key == Key.N) { e.Handled = true; OpenWindow(null); return; }
            base.OnPreviewKeyDown(e);
        }

        // =================================================================
        // Windows and tabs
        // =================================================================
        /// <summary>A new window: with a tab moving in, or with a new tab of the start profile.</summary>
        MainWindow OpenWindow(TerminalView.Handoff adopt) { return OpenWith(adopt, true); }

        /// <summary>A new window (from anywhere - the drop-down, a request on the pipe): with a tab moving in, else (newTab) a new tab.</summary>
        public static MainWindow OpenWith(TerminalView.Handoff adopt, bool newTab = true)
        {
            var w = new MainWindow(Hub.S, Hub.Themes, adopt, !All.Any());
            w.Show();
            if (adopt == null && newTab) w.View.NewTab(null);
            w.Activate();
            return w;
        }

        /// <summary>Where a tab can move to (except the terminal of `except`): every window, and the drop-down.</summary>
        public static List<KeyValuePair<string, TerminalView>> Targets(MainWindow except)
        {
            var l = new List<KeyValuePair<string, TerminalView>>();
            foreach (MainWindow w in All)
            {
                if (w == except) continue;
                string t = w._view.ActiveTitle;
                l.Add(new KeyValuePair<string, TerminalView>("window " + w._number + (t.Length > 0 ? " (" + t + ")" : ""), w._view));
            }
            if (DropDown.Current != null) l.Add(new KeyValuePair<string, TerminalView>("the drop-down", DropDown.Current.View));
            return l;
        }

        /// <summary>Options, from a window if one is open (else on its own - the drop-down's menu).</summary>
        public static void OpenOptionsAnywhere(string page)
        {
            MainWindow front = Front;
            if (front != null) { front.OpenOptions(page); return; }
            OptionsWindow.Open(null, Hub.S, Hub.Themes, Looks.Theme ?? Theme.From(Hub.Themes.Current), true, page);
        }

        /// <summary>What a start of vTerm asked for (here, or handed over from another start): a tab with a profile / an SSH host.</summary>
        public void Run(StartRequest r)
        {
            if (r == null) { _view.NewTab(null); return; }
            if (r.NewWindow)
            {
                var w = new MainWindow(_s, _themeSource, null, false);
                w.Show();
                r.NewWindow = false;
                w.Run(r);
                return;
            }
            switch (r.Kind)
            {
                case StartRequest.Kinds.Ssh:
                    {
                        TermConnection c = r.ResolveConnection(_s, this);
                        if (c != null) _view.Connect(c, r.Remote);
                        else if (!_view.HasTabs) _view.NewTab(null);
                        break;
                    }
                default:
                    {
                        TermProfile p = null;
                        if (!string.IsNullOrEmpty(r.Profile))
                            p = _s.ProfilesInUse().Find(x => string.Equals(x.Name, r.Profile, StringComparison.OrdinalIgnoreCase));
                        if (p == null && !string.IsNullOrEmpty(r.Command)) p = new TermProfile { Name = r.Title ?? r.Command, Command = r.Command, Folder = r.Folder ?? "" };
                        // only --dir ("Open in vTerm" on a folder): the start profile, in that folder (it opened in the profile's own folder)
                        if (p == null && !string.IsNullOrEmpty(r.Folder)) p = _s.StartProfile();
                        if (p != null && !string.IsNullOrEmpty(r.Folder)) p = new TermProfile { Name = p.Name, Command = p.Command, Folder = r.Folder };
                        _view.NewTab(p);
                        break;
                    }
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // =================================================================
        // Closing
        // =================================================================
        bool _closing;

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_quiet && _s.AskBeforeClosing && !UpdateCenter.Installing)
            {
                int running = _view.RunningCount;
                if (running > 0)
                {
                    string q = running == 1 ? "Close this window? Its terminal is still running and will end."
                                            : "Close this window? Its " + running + " terminals are still running and will end.";
                    if (!Looks.YesNo(this, q, "vTerm")) { e.Cancel = true; return; }
                }
            }
            _closing = true;
            Remember();
            _s.Save();
            _themeSource.Changed -= _themeChanged;
            UpdateCenter.Changed -= _updateChanged;
            try { _view.EndAll(); } catch { }
            if (_notice != null) try { _notice.Close(); } catch { }
        }

        // =================================================================
        // Another start of vTerm handing over what it was asked (Instance.HandOff)
        // =================================================================
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x4A /* WM_COPYDATA */)
            {
                string test = TestHooks.Received(lParam);   // VTERM_TEST_HOOKS=1 only
                if (test != null)
                {
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(() => RunTestCommand(test)));
                    return new IntPtr(1);
                }
                string text = null;
                try { text = Instance.Received(lParam); } catch { }
                if (text == null) return IntPtr.Zero;
                handled = true;
                string t = text;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (UpdateCenter.HandleCommand(t)) return;
                    StartRequest r = StartRequest.FromHandOff(t);
                    MainWindow target = Front ?? this;
                    target.Run(r);
                }));
                return new IntPtr(1);
            }
            return IntPtr.Zero;
        }
    }
}
