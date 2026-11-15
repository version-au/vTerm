using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// What all of vTerm shares: the settings, vShell's theme, what comes in on vTerm's pipe (vShell's drop-down and Open shell, and vTerm
    /// started again), and when to end. vTerm runs while a window is open or once the drop-down has been used (its shells live on hidden,
    /// for the next time vShell opens it); a vTerm started in the background for vShell that's never asked anything ends after a minute.
    /// </summary>
    public static class Hub
    {
        public static AppSettings S { get; private set; }
        public static VShellThemeSource Themes { get; private set; }
        static bool _ending;

        public static void Init(AppSettings s, VShellThemeSource themes)
        {
            S = s;
            Themes = themes;
            ApplyTheme();
            themes.Changed += delegate { Application.Current.Dispatcher.BeginInvoke(new Action(ApplyTheme)); };
        }

        /// <summary>vShell's theme into the app's resources and the dialogs (the windows apply it to themselves too).</summary>
        public static void ApplyTheme()
        {
            Theme t = Theme.From(Themes.Current);
            Looks.Theme = t;
            Looks.Blur = S.ThemeOpacity;
            t.Apply(Application.Current.Resources, S.ThemeOpacity);
        }

        /// <summary>A request on the pipe (on the UI thread). Quick: anything that may ask a question runs a moment later.</summary>
        public static string Handle(TermRequest r)
        {
            switch (r.Cmd)
            {
                case "toggle":
                case "show":
                case "hide":
                    return DropDown.Get(S, Themes).Handle(r);
                case "open-host":
                    Later(delegate { OpenHost(r); });
                    return "ok";
                case "args":
                    Later(delegate { Args(r.Args ?? new string[0]); });
                    return "ok";
                case "quit-if-idle":
                    // vShell is closing: end vTerm unless something of it is still in use (a window, or the drop-down's running shells)
                    Later(delegate { if (!MainWindow.All.Any() && (DropDown.Current == null || DropDown.Current.View.RunningCount == 0)) End(); });
                    return "ok";
            }
            return "error: unknown request " + r.Cmd;
        }

        static void Later(Action a) { Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, a); }

        /// <summary>vTerm started again: an update request, else what its command line asks (nothing = a new window).</summary>
        static void Args(string[] args)
        {
            if (args.Length == 1 && UpdateCenter.IsCommand(args[0]))
            {
                // vShell's Update button: a window to show the update notice in (vTerm may be running for the drop-down only, with
                // no window - it updated out of sight), brought to the front. Include pre-releases on / off needs no window.
                if (string.Equals(args[0].Trim(), UpdateCenter.UpdateCommand, StringComparison.OrdinalIgnoreCase))
                {
                    MainWindow w = MainWindow.Front ?? MainWindow.OpenWith(null);
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Activate();
                }
                UpdateCenter.HandleCommand(args[0]);
                return;
            }
            StartRequest req = StartRequest.Parse(args) ?? new StartRequest { NewWindow = true };
            MainWindow front = MainWindow.Front;
            if (front == null)
            {
                req.NewWindow = false;
                front = MainWindow.OpenWith(null, false);
            }
            front.Run(req);
        }

        /// <summary>Open shell from vShell: an SSH tab in the drop-down (when vShell gave its place) or in a vTerm window.</summary>
        static void OpenHost(TermRequest r)
        {
            var req = new StartRequest { Kind = StartRequest.Kinds.Ssh, Host = r.Host ?? "", Name = r.Name, User = r.User, Remote = r.Remote, Why = r.Why };
            if (r.W > 0)
            {
                DropDown dd = DropDown.Get(S, Themes);
                r.Cmd = "show";
                dd.Present(r);
                TermConnection c = req.ResolveConnection(S, dd);
                if (c != null) dd.View.Connect(c, r.Remote);
                else if (!dd.View.HasTabs) dd.HideTerm();
                return;
            }
            MainWindow front = MainWindow.Front ?? MainWindow.OpenWith(null, false);
            front.Run(req);
        }

        /// <summary>A window closed (or the background wait ran out): end vTerm when nothing of it is left.</summary>
        public static void MaybeQuit()
        {
            if (_ending) return;
            if (MainWindow.All.Any() || DropDown.Current != null) return;
            End();
        }

        static void End()
        {
            if (_ending) return;
            _ending = true;
            Log.Write("vTerm ending (nothing open)");
            DropDown.Shutdown();
            Application.Current.Shutdown();
        }
    }
}
