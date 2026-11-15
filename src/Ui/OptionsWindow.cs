using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// vTerm's Options (the menu button right of the tabs): the suite's Options window (vSuite's OptionsWindowBase) with vTerm's pages.
    /// Every change is saved and shown in all open vTerm windows at once - there's no OK button. One Options window at a time.
    /// </summary>
    public sealed class OptionsWindow : OptionsWindowBase
    {
        public static OptionsWindow Current { get; private set; }

        const string PgGeneral = "General", PgLook = "Look", PgConnections = "Connections", PgAbout = "About";

        readonly AppSettings _s;
        readonly VShellThemeSource _themeSource;

        /// <summary>Show Options (or bring the open one forward), on a page (null = the one shown last).</summary>
        public static void Open(Window owner, AppSettings s, VShellThemeSource themeSource, Theme theme, bool blur, string page)
        {
            if (Current == null)
            {
                Current = new OptionsWindow(s, themeSource, theme, blur) { Owner = owner };
                Current.Show();
            }
            else Current.Activate();
            if (page != null) Current.ShowPage(page);
        }

        OptionsWindow(AppSettings s, VShellThemeSource themeSource, Theme theme, bool blur) : base(theme, blur)
        {
            _s = s;
            _themeSource = themeSource;
            Closed += (o, e) => { if (Current == this) Current = null; };
            Finish();
        }

        protected override void BuildPages()
        {
            // ---- General ----
            StackPanel general = Page(PgGeneral, "", "New tabs, profiles and closing");
            StackPanel c = Card(general, "New tabs");
            List<TermProfile> profiles = _s.ProfilesInUse();
            string[] names = profiles.ConvertAll(p => p.Name).ToArray();
            string current = _s.StartProfile().Name;
            Row(c, "A new tab opens", "Ctrl+Shift+T and the + button", "The other profiles are on the button beside +, with your saved connections.",
                Combo(names, current, v => _s.DefaultProfile = v));

            c = Card(general, "Profiles");
            var box = new TextBox
            {
                Text = TermProfile.ToText(profiles), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 110, MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
            };
            Place(c, Dim("One per line: Name | command line | start folder (the folder can be left off).", 12, new Thickness(0, 0, 0, 6)));
            Place(c, box);
            var save = TextButton("Save the profiles", () =>
            {
                List<TermProfile> l = TermProfile.FromText(box.Text);
                _s.Profiles = l.Count > 0 ? l : null;
                box.Text = TermProfile.ToText(_s.ProfilesInUse());
                Saved();
            });
            var reset = TextButton("Back to this PC's shells", () =>
            {
                _s.Profiles = null;
                box.Text = TermProfile.ToText(_s.ProfilesInUse());
                Saved();
            });
            Place(c, Buttons(save, reset));

            c = Card(general, "Closing");
            Switch(c, "Ask before closing a window with shells running", "They end when the window closes", null, _s.AskBeforeClosing, v => _s.AskBeforeClosing = v);

            // ---- Look ----
            StackPanel look = Page(PgLook, "", "Text, colours and the window");
            c = Card(look, "Text");
            Row(c, "Font", "Empty = Cascadia Mono, then Consolas", null, TextField(_s.Font, v => _s.Font = (v ?? "").Trim(), 220));
            Row(c, "Text size", null, null, SliderBox(8, 32, _s.FontSize, 1, " pt", v => _s.FontSize = (int)Math.Round(v)));
            c = Card(look, "Colours");
            Row(c, "Colours", "Theme = from your vShell theme", "Theme builds the 16 terminal colours from vShell's theme: blue is the Main Accent, magenta the " +
                "Secondary Accent, the greys come from the background and text. The others are the well-known schemes (the background and text stay the theme's).",
                Combo(TerminalView.Schemes, _s.Colors, v => _s.Colors = v));
            c = Card(look, "The window");
            Switch(c, "Use the theme's opacity", "See-through like vShell's bars - off: solid",
                   "On: the window shows your wallpaper through its colour at the opacity set in vShell (blurred with Blur or Frosted), like the bars and widgets. Off: solid colours.",
                   _s.ThemeOpacity, v => _s.ThemeOpacity = v);
            Switch(c, "See-through text area", "Off: the text sits on the theme colour, solid", null, _s.SeeThrough, v => _s.SeeThrough = v);
            Switch(c, "Keep on top of other windows", "Also the pin button beside the tabs", null, _s.Topmost, v => _s.Topmost = v);
            Note(look, "Colours, opacity, blur and corners come from your vShell theme - change them in vShell's settings.");

            // ---- Connections ----
            StackPanel conns = Page(PgConnections, "", "Saved SSH connections");
            c = Card(conns, "Saved connections");
            _connList = new StackPanel();
            Place(c, _connList);
            var add = TextButton("Add a connection…", () =>
            {
                var made = new TermConnection();
                if (!TermDialogs.Edit(made, true, f => f(this))) return;
                _s.Connections.Add(made);
                ConnectionsSaved();
            });
            var import = TextButton("Add the hosts in ~/.ssh/config", ImportSshConfig);
            Place(c, Buttons(add, import));
            RefreshConnections();
            Action changed = () => Dispatcher.BeginInvoke(new Action(RefreshConnections));
            TermSsh.Changed += changed;
            Closed += (o, e) => TermSsh.Changed -= changed;
            Note(conns, "The same list is in each window's side panel (the button left of the tabs) - click one there to connect.");

            // ---- Updates ----
            UpdatesPage(_s);

            // ---- About ----
            StackPanel about = Page(PgAbout, "", null);
            c = Card(about, "vTerm");
            Version ver = Assembly.GetExecutingAssembly().GetName().Version;
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "vTerm " + ver.Major + "." + ver.Minor + "." + ver.Build, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            info.Children.Add(Dim("A terminal in your vShell theme: PowerShell, cmd, WSL and SSH in tabs, with your saved connections and their files.", 12, new Thickness(0, 2, 0, 0)));
            Place(c, info);
            Row(c, "Colours", _themeSource.Found ? "Your vShell theme - vTerm follows it as you change it" : "vShell's default theme (vShell's settings weren't found)", null, null);
            var folder = TextButton("Open", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Folder) { UseShellExecute = true }); } catch { }
            });
            Row(c, "Settings folder", Log.Folder, null, folder);
            Row(c, "Terminal display", "xterm.js (MIT licence) in Microsoft Edge WebView2", null, null);
        }

        protected override void Saved()
        {
            _s.Normalize();
            _s.Save();
            MainWindow.SettingsChanged();
        }

        // =================================================================
        // Connections
        // =================================================================
        StackPanel _connList;

        void ConnectionsSaved()
        {
            _s.Normalize();
            _s.Save();
            TermSsh.RaiseChanged();     // the side panels and this list show them again
        }

        /// <summary>One row per saved connection, by folder: its name and address, Edit and Delete.</summary>
        void RefreshConnections()
        {
            if (_connList == null) return;
            _connList.Children.Clear();
            if (_s.Connections.Count == 0)
            {
                _connList.Children.Add(Dim("No saved connections yet.", 12.5, new Thickness(0, 0, 0, 8)));
                return;
            }
            string folder = null;
            foreach (TermConnection x in _s.Connections.OrderBy(k => k.Folder ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Shown, StringComparer.OrdinalIgnoreCase))
            {
                TermConnection conn = x;
                if ((x.Folder ?? "") != folder)
                {
                    folder = x.Folder ?? "";
                    if (folder.Length > 0)
                    {
                        var head = Dim(folder, 11.5, new Thickness(0, _connList.Children.Count == 0 ? 0 : 10, 0, 2));
                        head.FontWeight = FontWeights.SemiBold;
                        _connList.Children.Add(head);
                    }
                }
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                // docked right, so added right to left: Edit, Delete on screen
                row.Children.Add(Tool("", "Delete", () =>
                {
                    if (!Looks.YesNo(this, "Delete the saved connection \"" + conn.Shown + "\" (" + conn.Target + ")?", "vTerm")) return;
                    _s.Connections.RemoveAll(k => k.Id == conn.Id);
                    ConnectionsSaved();
                }));
                row.Children.Add(Tool("", "Edit", () => { if (TermDialogs.Edit(conn, false, f => f(this))) ConnectionsSaved(); }));
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = x.Shown, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                TextBlock where = Dim(x.Target + (x.Port != 22 ? ":" + x.Port : "") + "  ·  " +
                                      (x.Auth == "Key" ? "key" : x.Auth == "Agent" ? "agent" : x.Secret.Length > 0 ? "password saved" : "password"), 11, new Thickness(0));
                where.TextWrapping = TextWrapping.NoWrap;
                where.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Children.Add(where);
                row.Children.Add(label);
                _connList.Children.Add(row);
            }
        }

        Button Tool(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Width = 30, Height = 28, FontSize = 12 };
            b.SetResourceReference(StyleProperty, "vx.IconButton");
            b.Click += (o, e) => click();
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }

        void ImportSshConfig()
        {
            List<TermConnection> found;
            try { found = TermSsh.ReadSshConfig(); }
            catch (Exception ex) { Looks.Tell(this, "Couldn't read ~/.ssh/config: " + ex.Message, "vTerm"); return; }
            int added = 0;
            foreach (TermConnection x in found)
            {
                if (_s.Connections.Any(k => string.Equals(k.Host, x.Host, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(k.User, x.User, StringComparison.OrdinalIgnoreCase) && k.Port == x.Port)) continue;
                x.Normalize();
                _s.Connections.Add(x);
                added++;
            }
            ConnectionsSaved();
            Looks.Tell(this, found.Count == 0 ? "There are no named hosts in " + System.IO.Path.Combine(TermSsh.SshFolder, "config") + "." :
                             added == 0 ? "All " + found.Count + " hosts in your ssh config are already saved." :
                             "Added " + added + " connection" + (added == 1 ? "" : "s") + " (folder \"SSH config\").", "vTerm");
        }
    }
}
