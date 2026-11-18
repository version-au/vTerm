using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using vTerm.Core;

namespace GhosttyBench
{
    /// <summary>
    /// libghostty-vt + a WPF renderer against vTerm's xterm.js in WebView2, on the same output. Writes results\results-*.md.
    /// Arguments: --mb N (size of each workload, default 16), --fresh (a new WebView2 profile: a first-ever start), --quick (1 run each).
    /// </summary>
    static class Program
    {
        const int Cols = 120, Rows = 40;
        static int _mb = 16, _repeat = 3;
        static bool _fresh;
        static string _out, _data;
        static readonly StringBuilder _md = new StringBuilder();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [STAThread]
        static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mb" && i + 1 < args.Length) _mb = int.Parse(args[++i], Inv);
                else if (args[i] == "--fresh") _fresh = true;
                else if (args[i] == "--quick") _repeat = 1;
            }
            string benchDir = FindBenchDir();
            _out = Path.Combine(benchDir, "results");
            _data = Path.Combine(Path.GetTempPath(), "vterm-ghostty-bench");
            Directory.CreateDirectory(_out); Directory.CreateDirectory(_data);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int code = 0;
            app.Startup += async delegate
            {
                try { await RunAll(benchDir); }
                catch (Exception ex) { Console.WriteLine("FAILED: " + ex); code = 1; }
                app.Shutdown();
            };
            app.Run();
            return code;
        }

        static string FindBenchDir()
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "GhosttyBench.csproj"))) d = Path.GetDirectoryName(d);
            return d ?? Directory.GetCurrentDirectory();
        }

        static void Say(string s) { Console.WriteLine(s); }
        static void Md(string s) { _md.AppendLine(s); }
        static string F(double v, string fmt = "0.0") { return v.ToString(fmt, Inv); }
        static double MBs(long bytes, double ms) { return bytes / 1048576.0 / (ms / 1000.0); }
        static double Median(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        static long Private() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using (var p = Process.GetCurrentProcess()) { p.Refresh(); return p.PrivateMemorySize64; } }
        static string MB(long b) { return F(b / 1048576.0) + " MB"; }

        static Task NextFrame()
        {
            var t = new TaskCompletionSource<bool>();
            EventHandler h = null;
            h = delegate { CompositionTarget.Rendering -= h; t.TrySetResult(true); };
            CompositionTarget.Rendering += h;
            return t.Task;
        }

        static async Task RunAll(string benchDir)
        {
            string vtermAssets = Path.GetFullPath(Path.Combine(benchDir, "..", "src", "Terminal"));
            string webDir = Path.Combine(AppContext.BaseDirectory, "web");
            Say("Making the workloads (" + _mb + " MB each) ...");
            List<Workload> work = Workloads.All(_mb, Cols, Rows);
            foreach (Workload w in work) File.WriteAllText(Path.Combine(_data, w.Name + ".txt"), w.Text, new UTF8Encoding(false));

            Md("# libghostty-vt vs vTerm's xterm.js - test results");
            Md("");
            Md("- When: " + DateTime.Now.ToString("yyyy-MM-dd h:mm tt", Inv) + " on " + Environment.MachineName + " (" + Environment.ProcessorCount + " logical CPUs), Windows " + Environment.OSVersion.Version);
            Md("- Terminal size " + Cols + " x " + Rows + ", Cascadia Mono 13, scrollback 10,000 lines. Each workload " + _mb + " MB of UTF-8; median of " + _repeat + " runs where repeated.");
            Md("- libghostty-vt 1.0.1 (the release DLL) with the bench's WPF renderer (`GhosttyView`); xterm.js = vTerm's own copy (DOM renderer, Unicode 11 add-on) in WebView2" + (_fresh ? " (a new WebView2 profile: first-ever start)" : " (an existing WebView2 profile)") + ".");
            Md("");
            Md("Workloads:");
            foreach (Workload w in work) Md("- **" + w.Name + "** - " + w.About + " (" + MB(w.Bytes.Length) + ")");
            Md("");

            var results = new Dictionary<string, string>();

            // ---------------------------------------------------------------- libghostty-vt
            Say("libghostty-vt: start-up ...");
            long before = Private();
            var swStart = Stopwatch.StartNew();
            object termLock = new object();
            IntPtr term = Gt.NewTerminal(Cols, Rows, 10000);
            var view = new GhosttyView(termLock, "Cascadia Mono", 13);
            view.Attach(term, Cols, Rows);
            double gEngine = swStart.Elapsed.TotalMilliseconds;
            var gwin = new Window
            {
                Title = "libghostty-vt (bench renderer)", Content = view, SizeToContent = SizeToContent.WidthAndHeight, Left = 40, Top = 40,
                WindowStartupLocation = WindowStartupLocation.Manual, Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), ShowActivated = false
            };
            gwin.Show();
            byte[] hello = Encoding.UTF8.GetBytes("PS C:\\Users\\steve> ");
            lock (termLock) Gt.Write(term, hello, 0, hello.Length);
            view.Frame();
            await NextFrame();
            double gStart = swStart.Elapsed.TotalMilliseconds;
            long gIdle = Private() - before;
            Say("  " + F(gStart) + " ms to the first frame (library + renderer ready at " + F(gEngine) + " ms)");

            Say("libghostty-vt: parsing alone ...");
            var gParse = new Dictionary<string, double>();
            foreach (Workload w in work)
            {
                var runs = new List<double>();
                for (int r = 0; r < _repeat; r++)
                {
                    IntPtr t = Gt.NewTerminal(Cols, Rows, 10000);
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < w.Bytes.Length; i += 16384) Gt.Write(t, w.Bytes, i, Math.Min(16384, w.Bytes.Length - i));
                    runs.Add(sw.Elapsed.TotalMilliseconds);
                    Gt.ghostty_terminal_free(t);
                }
                gParse[w.Name] = Median(runs);
                Say("  " + w.Name + ": " + F(MBs(w.Bytes.Length, gParse[w.Name])) + " MB/s");
            }

            Say("libghostty-vt: a full-screen redraw ...");
            var gRedraw = new Dictionary<string, double>();
            foreach (string name in new[] { "colour", "unicode", "fullscreen" })
            {
                Workload w = work.First(x => x.Name == name);
                lock (termLock) { Gt.ghostty_terminal_reset(term); Gt.Write(term, w.Bytes, 0, Math.Min(w.Bytes.Length, 256 * 1024)); }
                view.Frame(true);
                await NextFrame();
                int f0 = view.Frames; double ms0 = view.FrameMs;
                for (int i = 0; i < 200; i++) view.Frame(true);
                gRedraw[name] = (view.FrameMs - ms0) / (view.Frames - f0);
                await NextFrame();
                Snapshot(view, Path.Combine(_out, "ghostty-" + name + ".png"));
                Say("  " + name + ": " + F(gRedraw[name], "0.00") + " ms a frame (all " + Rows + " rows)");
            }

            Say("libghostty-vt: streaming with the screen drawn ...");
            var gStream = new Dictionary<string, Tuple<double, int, double>>();
            foreach (Workload w in work)
            {
                lock (termLock) Gt.ghostty_terminal_reset(term);
                view.Frame(true);
                await NextFrame();
                gStream[w.Name] = await GhosttyStream(view, term, termLock, delegate (Action<byte[], int> add)
                {
                    for (int i = 0; i < w.Bytes.Length; i += 16384)
                    {
                        int n = Math.Min(16384, w.Bytes.Length - i);
                        byte[] piece = new byte[n];
                        Buffer.BlockCopy(w.Bytes, i, piece, 0, n);
                        add(piece, n);
                    }
                });
                Say("  " + w.Name + ": " + F(gStream[w.Name].Item1) + " ms (" + F(MBs(w.Bytes.Length, gStream[w.Name].Item1)) + " MB/s), " + gStream[w.Name].Item2 + " frames, " + F(gStream[w.Name].Item3, "0.00") + " ms a frame");
            }

            Say("ConPTY on its own (nothing drawn) ...");
            var conRaw = new Dictionary<string, Tuple<double, long>>();
            foreach (string name in new[] { "plain", "colour" })
            {
                var runs = new List<Tuple<double, long>>();
                for (int r = 0; r < _repeat; r++) runs.Add(await ConPtyRun(name, delegate (string s) { }));
                conRaw[name] = runs.OrderBy(x => x.Item1).ElementAt(runs.Count / 2);
                Say("  " + name + ": " + F(conRaw[name].Item1) + " ms, " + MB(conRaw[name].Item2 * 1) + " of ConPTY output");
            }

            Say("libghostty-vt: through ConPTY (cmd /c type) ...");
            var gCon = new Dictionary<string, Tuple<double, int, double>>();
            foreach (string name in new[] { "plain", "colour" })
            {
                lock (termLock) Gt.ghostty_terminal_reset(term);
                view.Frame(true);
                await NextFrame();
                gCon[name] = await GhosttyStream(view, term, termLock, delegate (Action<byte[], int> add)
                {
                    ConPtyRun(name, delegate (string s) { byte[] b = Encoding.UTF8.GetBytes(s); add(b, b.Length); }).Wait();
                });
                Say("  " + name + ": " + F(gCon[name].Item1) + " ms, " + gCon[name].Item2 + " frames");
            }

            Say("libghostty-vt: memory, 10 terminals with full scrollback ...");
            Workload colour = work.First(x => x.Name == "colour");
            int fill = Math.Min(colour.Bytes.Length, 3 * 1024 * 1024);
            long m0 = Private();
            var many = new List<IntPtr>();
            ulong resident = 0;
            for (int i = 0; i < 10; i++)
            {
                IntPtr t = Gt.NewTerminal(Cols, Rows, 10000);
                for (int k = 0; k < fill; k += 65536) Gt.Write(t, colour.Bytes, k, Math.Min(65536, fill - k));
                many.Add(t);
                Gt.MemoryUsage mu = Gt.Memory(t);
                resident += mu.PrimaryResident + mu.AltResident;
            }
            long gMany = Private() - m0;
            Say("  process grew " + MB(gMany) + ", libghostty-vt says " + MB((long)resident) + " resident");
            foreach (IntPtr t in many) Gt.ghostty_terminal_free(t);
            gwin.Close();
            view.Free();
            Gt.ghostty_terminal_free(term);

            // ---------------------------------------------------------------- xterm.js in WebView2
            string profile = Path.Combine(_data, "WebView2");
            if (_fresh && Directory.Exists(profile)) { try { Directory.Delete(profile, true); } catch { } }
            Say("xterm.js: start-up ...");
            var x = new XtermHost();
            long xBefore = Private();
            var swX = Stopwatch.StartNew();
            double pageReady = await x.StartAsync(profile, vtermAssets, webDir, _data, Cols * view.CellW + 40, Rows * view.CellH + 60);
            await x.NewTerm("main", Cols, Rows, false);
            double xStart = swX.Elapsed.TotalMilliseconds;
            await Task.Delay(1500);
            long xIdle = await x.ProcessBytes();
            long xInProc = Private() - xBefore;
            Say("  " + F(xStart) + " ms to a terminal on screen (page ready at " + F(pageReady) + " ms); WebView2 processes " + MB(xIdle));

            Say("xterm.js: parsing alone (off screen) and parsing + drawing ...");
            await x.NewTerm("off", Cols, Rows, true);
            var xParse = new Dictionary<string, double>();
            var xVisible = new Dictionary<string, double>();
            foreach (Workload w in work)
            {
                var runs = new List<double>(); var vis = new List<double>();
                for (int r = 0; r < _repeat; r++)
                {
                    await x.Reset("off");
                    runs.Add((await x.Run("off", Path.Combine(_data, w.Name + ".txt"))).Item1);
                    await x.Reset("main");
                    vis.Add((await x.Run("main", Path.Combine(_data, w.Name + ".txt"))).Item2);
                }
                xParse[w.Name] = Median(runs); xVisible[w.Name] = Median(vis);
                if (w.Name != "plain")
                    using (var fs = File.Create(Path.Combine(_out, "xterm-" + w.Name + ".png")))
                        await x.Web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
                Say("  " + w.Name + ": " + F(MBs(w.Bytes.Length, xParse[w.Name])) + " MB/s alone, " + F(MBs(w.Bytes.Length, xVisible[w.Name])) + " MB/s drawn");
            }
            await x.Close("off");

            Say("xterm.js: streaming the way vTerm does (128K characters a 15 ms tick) ...");
            var xStream = new Dictionary<string, double>();
            foreach (Workload w in work)
            {
                await x.Reset("main");
                xStream[w.Name] = await x.PumpLikeVterm("main", delegate (Action<string> add)
                {
                    // as ConPTY hands it over: 16K pieces
                    for (int i = 0; i < w.Text.Length; i += 16384)
                    {
                        int n = Math.Min(16384, w.Text.Length - i);
                        if (n < w.Text.Length - i && char.IsHighSurrogate(w.Text[i + n - 1])) n--;
                        add(w.Text.Substring(i, n));
                        i -= 16384 - n;
                    }
                });
                Say("  " + w.Name + ": " + F(xStream[w.Name]) + " ms (" + F(MBs(w.Bytes.Length, xStream[w.Name])) + " MB/s)");
            }

            Say("xterm.js: through ConPTY (cmd /c type), vTerm's path ...");
            var xCon = new Dictionary<string, double>();
            foreach (string name in new[] { "plain", "colour" })
            {
                await x.Reset("main");
                xCon[name] = await x.PumpLikeVterm("main", delegate (Action<string> add) { ConPtyRun(name, add).Wait(); });
                Say("  " + name + ": " + F(xCon[name]) + " ms");
            }

            Say("xterm.js: memory, 10 terminals with full scrollback ...");
            string fillText = Encoding.UTF8.GetString(colour.Bytes, 0, fill);
            for (int i = 0; i < 10; i++)
            {
                string id = "m" + i;
                await x.NewTerm(id, Cols, Rows, false);
                for (int k = 0; k < fillText.Length; k += 131072) x.Post(new { t = "o", id = id, d = fillText.Substring(k, Math.Min(131072, fillText.Length - k)) });
                await x.Sync(id);
            }
            await Task.Delay(2000);
            long xMany = await x.ProcessBytes() - xIdle;
            Say("  WebView2 processes grew " + MB(xMany));
            x.Close();

            // ---------------------------------------------------------------- the report
            Md("## Start-up (to a terminal on screen)");
            Md("");
            Md("| | libghostty-vt + WPF | xterm.js in WebView2 |");
            Md("|---|---:|---:|");
            Md("| Time to the first frame | " + F(gStart) + " ms (library + renderer " + F(gEngine) + " ms; the rest is WPF's first window, which vTerm pays either way) | " + F(xStart) + " ms (page ready " + F(pageReady) + " ms) |");
            Md("| Memory for one idle terminal | +" + MB(gIdle) + " in the process (mostly WPF's first window, which vTerm has either way) | " + MB(xIdle) + " in WebView2's own processes |");
            Md("");
            Md("## Parsing alone (the terminal engine, nothing drawn)");
            Md("");
            Md("| Workload | libghostty-vt | xterm.js | |");
            Md("|---|---:|---:|---:|");
            foreach (Workload w in work)
                Md("| " + w.Name + " | " + F(MBs(w.Bytes.Length, gParse[w.Name])) + " MB/s | " + F(MBs(w.Bytes.Length, xParse[w.Name])) + " MB/s | " + F(xParse[w.Name] / gParse[w.Name]) + "x |");
            Md("");
            Md("## Parsing and drawing, output as fast as it comes");
            Md("");
            Md("libghostty-vt: a reader thread writes 16 KB pieces, the screen is drawn every display frame (dirty rows only). xterm.js: written inside the page as fast as it takes it (no vTerm in between).");
            Md("");
            Md("| Workload | libghostty-vt + WPF | frames (avg ms of UI thread) | xterm.js in the page | |");
            Md("|---|---:|---:|---:|---:|");
            foreach (Workload w in work)
                Md("| " + w.Name + " | " + F(MBs(w.Bytes.Length, gStream[w.Name].Item1)) + " MB/s | " + gStream[w.Name].Item2 + " (" + F(gStream[w.Name].Item3, "0.00") + ") | " + F(MBs(w.Bytes.Length, xVisible[w.Name])) + " MB/s | " + F(xVisible[w.Name] / gStream[w.Name].Item1) + "x |");
            Md("");
            Md("## vTerm's real path vs libghostty-vt");
            Md("");
            Md("xterm.js fed exactly as vTerm's TerminalView feeds it (output in a StringBuilder, 128K characters posted as JSON each 15 ms Background tick, the 2M-character hold-back).");
            Md("");
            Md("| Workload | libghostty-vt + WPF | vTerm today | |");
            Md("|---|---:|---:|---:|");
            foreach (Workload w in work)
                Md("| " + w.Name + " | " + F(gStream[w.Name].Item1 / 1000, "0.00") + " s | " + F(xStream[w.Name] / 1000, "0.00") + " s | " + F(xStream[w.Name] / gStream[w.Name].Item1) + "x |");
            Md("");
            Md("## Through ConPTY (a real shell: `cmd /c type` of the workload)");
            Md("");
            Md("| Workload | ConPTY alone (nothing drawn) | libghostty-vt + WPF | vTerm today |");
            Md("|---|---:|---:|---:|");
            foreach (string name in new[] { "plain", "colour" })
                Md("| " + name + " (" + MB(conRaw[name].Item2) + " out of ConPTY) | " + F(conRaw[name].Item1 / 1000, "0.00") + " s | " + F(gCon[name].Item1 / 1000, "0.00") + " s | " + F(xCon[name] / 1000, "0.00") + " s |");
            Md("");
            Md("## A full-screen redraw (libghostty-vt + WPF, UI thread)");
            Md("");
            foreach (var kv in gRedraw) Md("- " + kv.Key + ": " + F(kv.Value, "0.00") + " ms for all " + Rows + " rows (render state + GlyphRuns; WPF's own render thread not counted)");
            Md("");
            Md("## Memory: 10 terminals, each with " + MB(fill) + " of colour output (about 10,000 lines of scrollback)");
            Md("");
            Md("| | libghostty-vt | xterm.js in WebView2 |");
            Md("|---|---:|---:|");
            Md("| Grew by | " + MB(gMany) + " (library's own count: " + MB((long)resident) + ") | " + MB(xMany) + " |");
            Md("| Idle cost (see start-up) | ~" + MB(gIdle) + " | " + MB(xIdle) + " |");

            string file = Path.Combine(_out, "results-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv) + (_fresh ? "-fresh" : "") + ".md");
            File.WriteAllText(file, _md.ToString(), new UTF8Encoding(false));
            Say("");
            Say("Written: " + file);
        }

        /// <summary>
        /// libghostty-vt fed on a background thread (feed calls add per piece, as a ConPTY reader would), the screen drawn each display
        /// frame. Returns (ms until all of it is drawn, frames drawn, average UI-thread ms a frame).
        /// </summary>
        static async Task<Tuple<double, int, double>> GhosttyStream(GhosttyView view, IntPtr term, object termLock, Action<Action<byte[], int>> feed)
        {
            int f0 = view.Frames; double ms0 = view.FrameMs;
            var sw = Stopwatch.StartNew();
            EventHandler tick = delegate { view.Frame(); };
            CompositionTarget.Rendering += tick;
            await Task.Run(delegate
            {
                // parsing happens here, in the reader's thread, as it would in vTerm
                feed(delegate (byte[] b, int n) { lock (termLock) Gt.Write(term, b, 0, n); });
            });
            // the frame after the last write
            view.Frame();
            await NextFrame();
            CompositionTarget.Rendering -= tick;
            int frames = view.Frames - f0;
            return Tuple.Create(sw.Elapsed.TotalMilliseconds, frames, frames > 0 ? (view.FrameMs - ms0) / frames : 0);
        }

        /// <summary>The bench renderer's screen as a PNG (to check the drawing).</summary>
        static void Snapshot(FrameworkElement el, string file)
        {
            var dpi = VisualTreeHelper.GetDpi(el);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY),
                                                                           dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (DrawingContext dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg);
            rtb.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        const string EndMark = "@@BENCH-END@@";

        /// <summary>
        /// `cmd /c type` of a workload file in vTerm's ConPty class at the bench's size, then an end mark; returns once the mark has been
        /// handed on: (ms, bytes that came out of ConPTY).
        /// </summary>
        static Task<Tuple<double, long>> ConPtyRun(string name, Action<string> output)
        {
            return Task.Run(delegate
            {
                var pty = new ConPty();
                long bytes = 0;
                string tail = "";
                var seen = new ManualResetEventSlim();
                var sw = Stopwatch.StartNew();
                pty.Output += delegate (string s)
                {
                    bytes += Encoding.UTF8.GetByteCount(s);
                    output(s);
                    tail = tail + s;
                    if (tail.Contains(EndMark)) seen.Set();
                    if (tail.Length > 64) tail = tail.Substring(tail.Length - 64);
                };
                pty.Start("cmd.exe /c \"type \"" + Path.Combine(_data, name + ".txt") + "\" & echo " + EndMark + "\"", _data, Cols, Rows);
                if (!seen.Wait(120000)) throw new TimeoutException("ConPTY never showed the end mark");
                double ms = sw.Elapsed.TotalMilliseconds;
                pty.Dispose();
                return Tuple.Create(ms, bytes);
            });
        }
    }
}
