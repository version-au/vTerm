using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace vTerm.Ui
{
    /// <summary>
    /// Tests only, as vXplorer's and vPad's: with VTERM_TEST_HOOKS=1 a test drives a vTerm window by WM_COPYDATA (kind TestKind, UTF-16
    /// text) - it needn't be in front, and nothing touches the real mouse or keyboard. Without the variable these messages are ignored.
    /// vSuite.Core's scripts\test-send.ps1 sends one.
    /// </summary>
    static class TestHooks
    {
        public const int TestKind = 0x76580002;
        public static bool On { get { return App.TestHooks; } }

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        public static string Received(IntPtr lParam)
        {
            if (!On || lParam == IntPtr.Zero) return null;
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (cds.dwData.ToInt64() != TestKind || cds.lpData == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(cds.lpData, Math.Max(0, cds.cbData / 2)).TrimEnd('\0');
        }
    }

    public sealed partial class MainWindow
    {
        /// <summary>
        /// The terminal's commands (TerminalView.RunTestCommand: newtab, tab, close, type, moveout), and newwindow | closewindow | options [page]
        /// | dump &lt;file&gt; (every window and the drop-down: their tabs). Questions answer themselves with VTERM_TEST_ASK=yes / no.
        /// </summary>
        void RunTestCommand(string command)
        {
            string verb = command.Split(' ')[0].ToLowerInvariant();
            string arg = command.Length > verb.Length ? command.Substring(verb.Length + 1) : "";
            try
            {
                if (_view.RunTestCommand(verb, arg)) return;
                switch (verb)
                {
                    case "newwindow": OpenWindow(null); break;
                    case "closewindow": Close(); break;
                    case "options": OpenOptions(arg.Length > 0 ? arg : null); break;
                    case "updateclose":
                    {
                        // what an update does once the new version is staged: Installing on, every window closed the normal way
                        typeof(UpdateCenter).GetProperty("Installing").SetValue(null, true);
                        foreach (System.Windows.Window w in System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<System.Windows.Window>(System.Windows.Application.Current.Windows)))
                            try { w.Close(); } catch { }
                        break;
                    }
                    case "shot":
                    {
                        // the window's content as a PNG (the glass behind it isn't in it: it's drawn by Windows)
                        var el = (System.Windows.FrameworkElement)Content;
                        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY),
                                                                                       dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                        rtb.Render(el);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using (var fs = File.Create(arg)) enc.Save(fs);
                        break;
                    }
                    case "dump":
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("windows: " + All.Count);
                        foreach (MainWindow w in All)
                        {
                            sb.AppendLine("window: " + w.Title + (w.IsActive ? " (in front)" : ""));
                            sb.Append(w._view.TestDump());
                        }
                        if (DropDown.Current != null)
                        {
                            sb.AppendLine("drop-down: " + (DropDown.Current.IsVisible ? "shown" : "hidden") + (DropDown.Current.IsActive ? ", active" : "") +
                                          ", keyboard on " + (System.Windows.Input.Keyboard.FocusedElement != null ? System.Windows.Input.Keyboard.FocusedElement.GetType().Name : "nothing") +
                                          ", mouse captured by " + (System.Windows.Input.Mouse.Captured != null ? System.Windows.Input.Mouse.Captured.GetType().Name : "nothing"));
                            sb.Append(DropDown.Current.View.TestDump());
                        }
                        File.WriteAllText(arg, sb.ToString());
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Test command '" + command + "': " + ex); }
        }
    }
}
