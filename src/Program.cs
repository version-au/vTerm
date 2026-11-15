using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using vTerm.Core;
using vTerm.Ui;

namespace vTerm
{
    static class Program
    {
        static Mutex _running;

        [STAThread]
        static int Main(string[] args)
        {
            // the shared library: which app this is (settings folder, log, test variables, releases), and its icon drawing
            vSuite.App.Init("vTerm", "VTERM", "https://vsuite.vrsion.com");
            AppIcon.Drawer = Brand.Draw;

            // ssh asking for a saved password / passphrase, or whether to trust a server (SSH_ASKPASS = vTerm.exe): answer and go
            if (TermSsh.IsAskpass)
                return TermSsh.Askpass(args, delegate (string title, string question)
                {
                    Theme t = null;
                    try { using (var src = new VShellThemeSource()) t = Theme.From(src.Current); } catch { }
                    Ask.FallbackTheme = () => t;
                    return Ask.YesNo(null, title, question, "Yes", "No", false);
                });

            if (args.Length == 2 && args[0] == "--make-icon")   // used once to make vterm.ico
            {
                AppIcon.Save(args[1]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--update-watchdog")   // the old exe after an update: start the new one, go back if it fails
                return Updater.RunWatchdog(args);

            // --background: started by vShell for its drop-down - no window of its own
            bool background = args.Length == 1 && args[0] == "--background";

            // already running (same settings folder): it does what was asked (a new window, a tab, an update), and this one stops here
            string pipe = TermLink.PipeName(App.DataFolder);
            if (TermLink.Send(pipe, new TermRequest { Cmd = background ? "noop" : "args", Args = args }, 4000) != null) return 0;
            // for the installer: "vTerm is running" (AppMutex) - every vTerm holds it, whatever its settings folder
            _running = new Mutex(false, "vTerm.Running");
            // one vTerm per settings folder (and the older way of handing over, should the pipe not answer)
            string handOff = args.Length == 1 && UpdateCenter.IsCommand(args[0]) ? args[0] : StartRequest.ToHandOff(args);
            try { if (Instance.HandOff(handOff)) return 0; }
            catch (Exception ex) { Log.Write("Hand-off: " + ex.Message); }
            Log.Write("vTerm " + Assembly.GetExecutingAssembly().GetName().Version + " starting" + (background ? " in the background (for vShell)" : ""));

            // vTerm ends itself (Hub.MaybeQuit): when its last window closes and the drop-down was never used
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                Log.Flush();
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log.Write("Unhandled (" + (e.IsTerminating ? "ending" : "carrying on") + "): " + e.ExceptionObject); Log.Flush(); };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("Unobserved task error: " + e.Exception); e.SetObserved(); };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Look.ControlsUri });

            var settings = AppSettings.Load();
            if (!settings.ImportedFromVShell)
            {
                // the very first start: vShell's terminal settings and saved connections come over (vShell's own are left as they are)
                VShellImport.Into(settings, true);
                settings.ImportedFromVShell = true;
                settings.Save();
            }
            var theme = new VShellThemeSource();
            Hub.Init(settings, theme);
            Ask.FallbackTheme = () => Looks.Theme;

            // vShell's drop-down / Open shell, and vTerm started again, come in on the pipe
            var stop = new CancellationTokenSource();
            TermLink.Serve(pipe, r => r.Cmd == "noop" ? "ok" : (string)app.Dispatcher.Invoke(new Func<string>(() => Hub.Handle(r))), stop.Token);

            int code;
            if (background)
            {
                // nothing asked within a minute: end again
                var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                wait.Tick += (s, e) => { wait.Stop(); Hub.MaybeQuit(); };
                wait.Start();
                app.Dispatcher.BeginInvoke(new Action(() => UpdateCenter.Start(settings)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                code = app.Run();
            }
            else
            {
                bool update = args.Length == 1 && UpdateCenter.HandleCommand(args[0]);   // vTerm.exe --update (vShell's Settings): open as usual, update now
                StartRequest request = update ? null : StartRequest.Parse(args);
                if (request != null) request.NewWindow = false;
                var window = new MainWindow(settings, theme, null, true);
                app.MainWindow = window;
                window.ContentRendered += (s, e) => UpdateCenter.Start(settings);
                bool started = false;
                window.ContentRendered += (s, e) => { if (started) return; started = true; window.Run(request); };
                window.Show();
                code = app.Run();
            }
            stop.Cancel();
            DropDown.Shutdown();
            settings.Save();
            theme.Dispose();
            return code;
        }
    }
}
