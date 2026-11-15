using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// The drop-down terminal vShell's bars open (TermLink "toggle" / "show" / "open-host"). vShell says where - the card's place against
    /// the bar's edge, in screen pixels - and how it looks (the bar's corners, outline and glass); the colours are the vShell theme's. It
    /// slides out of the bar (or fades in, when it isn't attached), takes the keyboard, and hides again when you click elsewhere (if that's
    /// on), with Shift+Esc, or its hide button. Hidden, its shells keep running. Drawn like vShell's own drop-down was: a layered window
    /// with room for the shadow, the glass and the card; the text is in the TerminalView's own text window, as everywhere.
    /// </summary>
    public sealed class DropDown : Window
    {
        static DropDown _one;

        /// <summary>The drop-down, once it has been opened (it lives on hidden).</summary>
        public static DropDown Current { get { return _one; } }

        readonly AppSettings _s;
        readonly VShellThemeSource _themeSource;
        readonly TerminalView _view;
        TermRequest _req;
        IntPtr _hwnd;
        bool _hiding;
        DateTime _hiddenAt = DateTime.MinValue;
        long _hiddenFrom;

        readonly Grid _stage = new Grid();
        readonly Grid _shadowHost = new Grid { IsHitTestVisible = false };
        readonly Border _shadow = new Border { Background = Brushes.Black, IsHitTestVisible = false };
        readonly Glass _glass = new Glass();
        readonly Border _card = new Border();
        readonly Border _outer = new Border();
        readonly TranslateTransform _slide = new TranslateTransform();
        const double Room = 14;     // around the card for its shadow (not on the side against the bar)

        public TerminalView View { get { return _view; } }

        public static DropDown Get(AppSettings s, VShellThemeSource themeSource)
        {
            if (_one == null) _one = new DropDown(s, themeSource);
            return _one;
        }

        DropDown(AppSettings s, VShellThemeSource themeSource)
        {
            _s = s;
            _themeSource = themeSource;
            Title = "vTerm drop-down";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 900; Height = 400;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12.5;

            _view = new TerminalView(s);
            _view.Emptied += delegate { HideTerm(); };
            _view.SurfaceDeactivated += LeftSoon;
            _view.HideRequested = HideTerm;
            _view.OpenSettings = delegate { HideTerm(); MainWindow.OpenOptionsAnywhere(null); };
            _view.NewWindowFor = delegate (TerminalView.Handoff h) { HideTerm(); MainWindow.OpenWith(h); };
            _view.OtherWindows = delegate { return MainWindow.Targets(null); };
            _view.BringUp = delegate { if (_req != null && (!IsVisible || _hiding)) Present(_req); };   // a tab moved in from a window
            _view.ShowHideButton(true);
            _view.LastTabCanLeave = true;      // its only tab can move into a window of its own (by its menu, Ctrl+Shift+O or dragging it out)
            _card.Child = _view;
            _shadowHost.Children.Add(_shadow);
            _stage.Children.Add(_shadowHost);
            _stage.Children.Add(_glass);
            _stage.Children.Add(_card);
            _stage.RenderTransform = _slide;
            _outer.Child = _stage;
            _outer.ClipToBounds = true;         // sliding out of the bar: nothing shows past the bar's edge
            Content = _outer;
            _card.SizeChanged += delegate { UpdateShadowClip(); };

            Deactivated += delegate { LeftSoon(); };
            PreviewKeyDown += delegate (object o, KeyEventArgs e) { if (e.Key == Key.Escape && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) { e.Handled = true; HideTerm(); } };
            Closing += delegate (object o, System.ComponentModel.CancelEventArgs e)
            {
                if (_quitting) return;
                e.Cancel = true;        // Alt+F4 hides it; the shells keep running
                HideTerm();
            };
            Action themeChanged = delegate { Dispatcher.BeginInvoke(new Action(delegate { if (_req != null) ApplyLook(); })); };
            _themeSource.Changed += themeChanged;
            Closed += delegate { _themeSource.Changed -= themeChanged; if (_one == this) _one = null; };
        }

        bool _quitting;

        /// <summary>vTerm is ending: end the drop-down's shells.</summary>
        public static void Shutdown()
        {
            if (_one == null) return;
            try { _one._quitting = true; _one._view.EndAll(); _one.Close(); } catch { }
            _one = null;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            // a tool window: never in Alt+Tab or on a taskbar
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        // =================================================================
        // What vShell asks
        // =================================================================
        /// <summary>"toggle" / "show" / "hide": the answer is "shown" or "hidden".</summary>
        public string Handle(TermRequest r)
        {
            if (r.Cmd == "hide") { HideTerm(); return "hidden"; }
            if (r.Cmd == "toggle")
            {
                if (_hiding) return "hidden";
                if (IsVisible && (IsActive || _view.SurfaceActive)) { HideTerm(); return "hidden"; }
                // a click on the bar's button just hid it (it lost the keyboard to the bar): that click means hide, not show again
                if (!IsVisible && (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 350 && _hiddenFrom == r.Bar) return "hidden";
            }
            Present(r);
            if (!_view.HasTabs) _view.NewTab(ProfileOf(r));
            return "shown";
        }

        /// <summary>Show (never hide) at the place the request gives, with the keyboard.</summary>
        public void Present(TermRequest r)
        {
            if (_hiding) { _slide.BeginAnimation(TranslateTransform.YProperty, null); BeginAnimation(OpacityProperty, null); Done(); }
            _req = r;
            bool wasVisible = IsVisible;
            if (!wasVisible) { Opacity = 0; Show(); }
            ApplyLook();
            Place();
            ForceForeground();
            if (!wasVisible) Reveal();
            else _view.FocusTerm();
        }

        TermProfile ProfileOf(TermRequest r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Profile)) return null;
            return _s.ProfilesInUse().Find(p => string.Equals(p.Name, r.Profile, StringComparison.OrdinalIgnoreCase));
        }

        // =================================================================
        // Show / hide
        // =================================================================
        void Reveal()
        {
            Opacity = 1;
            _view.Suspend(true);        // the text window waits until the drop-down is in place
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation a;
            if (_req.Attached)
            {
                double from = (_req.FromTop ? -1 : 1) * Math.Max(40, ActualHeight);
                a = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
                a.Completed += delegate { _view.Suspend(false); _view.FocusTerm(); };
                _slide.BeginAnimation(TranslateTransform.YProperty, a);
            }
            else
            {
                _slide.BeginAnimation(TranslateTransform.YProperty, null);
                _slide.Y = 0;
                a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
                a.Completed += delegate { _view.Suspend(false); _view.FocusTerm(); };
                BeginAnimation(OpacityProperty, a);
            }
        }

        public void HideTerm()
        {
            if (!IsVisible || _hiding) return;
            _hiding = true;
            _view.Suspend(true);        // the text window goes first (it can't slide)
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            DoubleAnimation a;
            if (_req != null && _req.Attached)
            {
                a = new DoubleAnimation(0, (_req.FromTop ? -1 : 1) * Math.Max(40, ActualHeight), TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
                a.Completed += delegate { Done(); };
                _slide.BeginAnimation(TranslateTransform.YProperty, a);
            }
            else
            {
                a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
                a.Completed += delegate { Done(); };
                BeginAnimation(OpacityProperty, a);
            }
        }

        void Done()
        {
            Hide();
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _slide.Y = 0;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _hiding = false;
            _hiddenAt = DateTime.UtcNow;
            _hiddenFrom = _req != null ? _req.Bar : 0;
        }

        /// <summary>The drop-down or its text window lost the keyboard: once it's clear where it went, hide (when that's on) - not when it
        /// went from one to the other (clicking the tabs / the text) or to one of its dialogs.</summary>
        void LeftSoon()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (_quitting || !IsVisible || _hiding || _req == null || !_req.HideOnLeave || _view.InDialog) return;
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == _hwnd || (fg != IntPtr.Zero && fg == _view.SurfaceHandle)) return;
                if (fg != IntPtr.Zero && Native.GetWindowRel(fg, 4) == _hwnd) return;      // a window it owns (a dialog)
                uint pid;
                if (fg != IntPtr.Zero && Native.GetWindowThreadProcessId(fg, out pid) != 0 && pid == (uint)Environment.ProcessId)
                {
                    // one of vTerm's own dialogs / questions (asked from here): stay
                    foreach (Window w in Application.Current.Windows)
                        if (!(w is MainWindow) && w != this && w.IsActive) return;
                }
                HideTerm();
            }));
        }

        void ForceForeground()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                uint pid;
                IntPtr fg = Native.GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out pid);
                uint me = Native.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                try { Native.SetForegroundWindow(_hwnd); }
                finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
                Activate();
            }
            catch (Exception ex) { Log.Write("Drop-down foreground: " + ex.Message); }
        }

        // =================================================================
        // Where and how it looks
        // =================================================================
        /// <summary>The card where vShell said, with room for the shadow on the sides away from the bar.</summary>
        void Place()
        {
            if (_hwnd == IntPtr.Zero || _req == null || _req.W <= 0 || _req.H <= 0) return;
            double scale = Math.Max(0.5, Native.GetDpiForWindow(_hwnd) / 96.0);
            int room = (int)Math.Round(Room * scale);
            int x = _req.X - room, w = _req.W + 2 * room, y, h;
            if (!_req.Attached) { y = _req.Y - room; h = _req.H + 2 * room; }
            else if (_req.FromTop) { y = _req.Y; h = _req.H + room; }
            else { y = _req.Y - room; h = _req.H + room; }
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void ApplyLook()
        {
            if (_req == null) return;
            Theme t = Theme.From(_themeSource.Current);
            double r = Math.Max(0, _req.Radius);
            double bw = Math.Max(0, Math.Min(12, _req.BorderThickness));
            Brush outline = Brushes.Transparent;
            try { if (!string.IsNullOrWhiteSpace(_req.BorderColor)) outline = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_req.BorderColor)); } catch { }
            bool attached = _req.Attached, top = _req.FromTop;

            // attached: square on the bar's side (no outline there), rounded on the far side
            CornerRadius cr = !attached ? new CornerRadius(r) : top ? new CornerRadius(0, 0, r, r) : new CornerRadius(r, r, 0, 0);
            Thickness bt = !attached ? new Thickness(bw) : top ? new Thickness(bw, 0, bw, bw) : new Thickness(bw, bw, bw, 0);
            _outer.Padding = !attached ? new Thickness(Room) : top ? new Thickness(Room, 0, Room, Room) : new Thickness(Room, Room, Room, 0);
            _card.CornerRadius = cr;
            _card.BorderThickness = bt;
            _card.BorderBrush = outline;
            _card.Background = Bg(t);
            _card.Padding = new Thickness(2, 0, 2, 2);
            _shadow.CornerRadius = cr;
            _shadowHost.Visibility = _req.Shadow ? Visibility.Visible : Visibility.Collapsed;
            _shadow.Effect = _req.Shadow ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black } : null;
            UpdateShadowClip();
            _glass.SetCorners(cr);
            string blur = _req.Blur == "Blur" || _req.Blur == "Acrylic" || _req.Blur == "Clear" ? _req.Blur : "Off";
            _glass.Refresh(blur);
            _view.ApplyTheme(TermLook.From(t));
        }

        /// <summary>The theme's background at its opacity (its gradient when it has one), as vShell's bars.</summary>
        static Brush Bg(Theme t)
        {
            if (!t.Gradient) { var b = new SolidColorBrush(t.Bg); b.Freeze(); return b; }
            double rad = t.GradientAngle * Math.PI / 180.0, dx = Math.Cos(rad), dy = Math.Sin(rad);
            var g = new LinearGradientBrush(t.Bg, t.Bg2, new Point(0.5 - dx / 2, 0.5 - dy / 2), new Point(0.5 + dx / 2, 0.5 + dy / 2));
            g.Freeze();
            return g;
        }

        /// <summary>Only the part of the shadow outside the card is drawn (a see-through card doesn't show it through).</summary>
        void UpdateShadowClip()
        {
            double w = _card.ActualWidth, h = _card.ActualHeight;
            if (w < 1 || h < 1) return;
            double r = _card.CornerRadius.BottomLeft;
            var clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-40, -40, w + 80, h + 80)),
                                            new RectangleGeometry(new Rect(0, 0, w, h), r, r));
            clip.Freeze();
            _shadowHost.Clip = clip;
        }
    }
}
