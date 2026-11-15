using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// The window the terminal's text is drawn in: a borderless window kept exactly over the terminal area of the vTerm window that owns
    /// it, holding the regular WebView2 control (from vShell, where its windows are layered and a browser can't draw inside them; here it
    /// also keeps the text clear of the window's glass layer). It is not layered: it is made see-through with Windows' "blur behind" with
    /// an empty region (no blur - just the window's own transparency), so the window under it (glass, theme colour) shows around the text.
    /// With "see-through" off it is the theme colour, solid.
    /// </summary>
    public sealed class TermSurface : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_BLURBEHIND { public uint dwFlags; public bool fEnable; public IntPtr hRgnBlur; public bool fTransitionOnMaximized; }

        [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        public readonly WebView2 Web;
        IntPtr _hwnd;
        bool _seeThrough;
        Color _solid;

        public IntPtr Handle { get { return _hwnd; } }

        public TermSurface(Window owner, bool seeThrough, Color solid)
        {
            _seeThrough = seeThrough;
            _solid = solid;
            Title = "vTerm text";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            Owner = owner;
            Topmost = owner != null && owner.Topmost;
            Background = seeThrough ? Brushes.Transparent : new SolidColorBrush(solid);
            Web = new WebView2 { DefaultBackgroundColor = seeThrough ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(255, solid.R, solid.G, solid.B) };
            Content = Web;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            try
            {
                // a tool window: never in Alt+Tab or on a taskbar (vShell's included)
                const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
                SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
                // no Windows 11 border / rounding of its own: the window around it has those
                int none = 1;          // DWMWCP_DONOTROUND
                Native.DwmSetWindowAttribute(_hwnd, 33, ref none, 4);
                int noBorder = unchecked((int)0xFFFFFFFE);     // DWMWA_COLOR_NONE
                Native.DwmSetWindowAttribute(_hwnd, 34, ref noBorder, 4);
            }
            catch { }
            ApplyLook(_seeThrough, _solid);
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        /// <summary>See-through (the window shows around the text) or the theme colour, solid.</summary>
        public void ApplyLook(bool seeThrough, Color solid)
        {
            _seeThrough = seeThrough;
            _solid = solid;
            Background = seeThrough ? Brushes.Transparent : new SolidColorBrush(solid);
            try { Web.DefaultBackgroundColor = seeThrough ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(255, solid.R, solid.G, solid.B); } catch { }
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                var src = HwndSource.FromHwnd(_hwnd);
                if (src != null && src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = seeThrough ? Colors.Transparent : solid;
                IntPtr rgn = CreateRectRgn(0, 0, -1, -1);        // an empty region: no blur, only the window's transparency
                var bb = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2, fEnable = seeThrough, hRgnBlur = rgn };
                DwmEnableBlurBehindWindow(_hwnd, ref bb);
                DeleteObject(rgn);
            }
            catch (Exception ex) { Log.Write("Text window look: " + ex.Message); }
        }

        /// <summary>Exactly over this rectangle (device pixels), shown without taking the keyboard.</summary>
        public void PlaceAt(int x, int y, int w, int h)
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void HideSurface()
        {
            if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_HIDE);
        }
    }
}
