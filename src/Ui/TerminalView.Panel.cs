using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Renci.SshNet;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// The terminal's side panel: Connections (saved SSH connections - quick connect, folders, new / edit / import, your key and copying it
    /// to a server) and Files (SFTP for the SSH tab in front: browse, upload - also by dropping files on it -, download, open, new folder,
    /// rename, delete). The connections are vTerm settings, so every window has them.
    /// </summary>
    public sealed partial class TerminalView
    {
        readonly Border _side = new Border { Width = 280 };
        readonly Border _tabConn = new Border(), _tabFiles = new Border();
        readonly DockPanel _connView = new DockPanel(), _filesView = new DockPanel();
        readonly StackPanel _connList = new StackPanel();
        readonly TextBox _quick = new TextBox();
        readonly StackPanel _fileList = new StackPanel();
        readonly TextBox _path = new TextBox();
        readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 6), FontSize = 11.5 };
        readonly List<Button> _panelButtons = new List<Button>();
        bool _showFiles;
        static readonly HashSet<string> _closedFolders = new HashSet<string>();

        // SFTP: one client per connection (by id), used one job at a time
        readonly Dictionary<string, SftpClient> _sftp = new Dictionary<string, SftpClient>();
        TermConnection _filesConn;
        string _filesPath = "";
        int _job;

        /// <summary>A dialog of the panel is open (the drop-down mustn't hide for it).</summary>
        public int Busy { get; private set; }

        AppSettings S { get { return _s; } }

        // =================================================================
        // Panel
        // =================================================================
        UIElement BuildPanel()
        {
            _side.Visibility = S.PanelOpen ? Visibility.Visible : Visibility.Collapsed;
            _side.BorderThickness = new Thickness(0, 0, 1, 0);
            var dock = new DockPanel();
            // the two views
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 4) };
            PanelTab(_tabConn, "Connections", false);
            PanelTab(_tabFiles, "Files", true);
            head.Children.Add(_tabConn);
            head.Children.Add(_tabFiles);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            BuildConnView();
            BuildFilesView();
            var both = new Grid();
            both.Children.Add(_connView);
            both.Children.Add(_filesView);
            dock.Children.Add(both);
            _side.Child = dock;
            ShowView(false);
            RefreshConnections();
            return _side;
        }

        void PanelTab(Border b, string text, bool files)
        {
            b.Child = new TextBlock { Text = text, FontSize = 12.5 };
            b.Padding = new Thickness(10, 4, 10, 4);
            b.Margin = new Thickness(0, 0, 4, 0);
            b.CornerRadius = new CornerRadius(6, 6, 0, 0);
            b.BorderThickness = new Thickness(0, 0, 0, 2);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; ShowView(files); };
        }

        void ShowView(bool files)
        {
            _showFiles = files;
            _connView.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
            _filesView.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            PanelTheme();
            if (files) LoadFiles(null);
        }

        void TogglePanel()
        {
            S.PanelOpen = !S.PanelOpen;
            _side.Visibility = S.PanelOpen ? Visibility.Visible : Visibility.Collapsed;
            _s.SaveSoon();
        }

        void PanelTheme()
        {
            if (_t == null) return;
            _side.Background = TermLook.Faded(_t.Fg, 4);
            _side.BorderBrush = TermLook.Faded(_t.Fg, 14);
            foreach (var x in new[] { new KeyValuePair<Border, bool>(_tabConn, !_showFiles), new KeyValuePair<Border, bool>(_tabFiles, _showFiles) })
            {
                x.Key.Background = x.Value ? _t.Hover : Brushes.Transparent;
                x.Key.BorderBrush = x.Value ? _t.MainAccent : Brushes.Transparent;
                var tb = x.Key.Child as TextBlock;
                if (tb != null) tb.Foreground = x.Value ? _t.Fg : _t.FgDim;
            }
            foreach (TextBox tb in new[] { _quick, _path })
            {
                tb.Background = TermLook.Faded(_t.Fg, 8);
                tb.Foreground = _t.Fg;
                tb.CaretBrush = _t.Fg;
                tb.BorderBrush = TermLook.Faded(_t.Fg, 18);
                tb.SelectionBrush = _t.MainAccent;
            }
            foreach (Button b in _panelButtons) StyleButton(b);
            _status.Foreground = _t.FgDim;
            RefreshConnections();
            if (_showFiles) RenderFiles(_lastEntries);
        }

        Button PanelButton(string glyph, string text, string tip, Action click)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = TermLook.Glyphs, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            if (text.Length > 0) row.Children.Add(new TextBlock { Text = " " + text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, ToolTip = tip, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, Focusable = false };
            b.Click += delegate { click(); };
            _panelButtons.Add(b);
            StyleButton(b);
            return b;
        }

        void StyleButton(Button b)
        {
            if (_t == null) return;
            b.Background = TermLook.Faded(_t.Fg, 8);
            b.Foreground = _t.Fg;
            b.BorderBrush = TermLook.Faded(_t.Fg, 16);
        }

        Window Owner() { return Window.GetWindow(this); }

        /// <summary>Run a dialog with the drop-down held open.</summary>
        T Dialog<T>(Func<Window, T> show)
        {
            Busy++;
            try { return show(Owner()); }
            finally { Busy--; FocusTermLater(); }
        }

        void FocusTermLater() { Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { if (Busy == 0) FocusTerm(); })); }

        bool Ask(string question, string title)
        {
            return Dialog(delegate (Window o)
            {
                return Looks.YesNo(o, question, title);
            });
        }

        void Tell(string text, string title)
        {
            Dialog(delegate (Window o)
            {
                Looks.Tell(o, text, title);
                return 0;
            });
        }

        // =================================================================
        // Connections
        // =================================================================
        void BuildConnView()
        {
            var top = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            _quick.Padding = new Thickness(6, 4, 6, 4);
            _quick.ToolTip = "Quick connect: user@host or user@host:port, then Enter (not saved)";
            var hint = new TextBlock { Text = "user@host  -  Enter to connect", IsHitTestVisible = false, Margin = new Thickness(9, 5, 0, 0), Opacity = 0.5, FontSize = 12 };
            var q = new Grid();
            q.Children.Add(_quick);
            q.Children.Add(hint);
            _quick.TextChanged += delegate { hint.Visibility = _quick.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            _quick.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                TermConnection c = TermConnection.Parse(_quick.Text);
                if (c == null) return;
                _quick.Text = "";
                Connect(c, null);
            };
            top.Children.Add(q);
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(PanelButton("", "New", "A new saved connection", delegate { NewConnection(null); }));
            buttons.Children.Add(PanelButton("", "Import", "Add the hosts in your ~/.ssh/config", delegate { ImportConfig(); }));
            Button keys = null;
            keys = PanelButton("", "My key", "Your SSH key: make one, copy it", delegate { KeyMenu(keys); });
            buttons.Children.Add(keys);
            top.Children.Add(buttons);
            DockPanel.SetDock(top, Dock.Top);
            _connView.Children.Add(top);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _connList, Focusable = false };
            _connList.Margin = new Thickness(4, 0, 4, 8);
            _connView.Children.Add(sv);
        }

        /// <summary>The saved connections changed (or a tab connected / disconnected): show them again.</summary>
        void ConnectionsChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ConnectionsChanged)); return; }
            RefreshConnections();
        }

        void RefreshConnections()
        {
            if (_t == null) return;
            _connList.Children.Clear();
            List<TermConnection> all = S.Connections ?? new List<TermConnection>();
            if (all.Count == 0)
            {
                _connList.Children.Add(new TextBlock
                {
                    Text = "No saved connections yet. New makes one; Import adds the hosts in your ~/.ssh/config. Saved connections are kept in vTerm's " +
                           "settings, so every window has them.",
                    TextWrapping = TextWrapping.Wrap, Foreground = _t.FgDim, Margin = new Thickness(6, 6, 6, 0), FontSize = 12
                });
                return;
            }
            var folders = all.Select(delegate (TermConnection c) { return c.Folder ?? ""; }).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(delegate (string f) { return f.Length == 0 ? "" : "~" + f; }, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string folder in folders)
            {
                string fk = folder;
                bool closed = folder.Length > 0 && _closedFolders.Contains(folder);
                if (folder.Length > 0)
                {
                    var fh = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 8, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                    fh.Children.Add(new TextBlock { Text = closed ? "" : "", FontFamily = TermLook.Glyphs, FontSize = 9, Foreground = _t.FgDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                    fh.Children.Add(new TextBlock { Text = folder, Foreground = _t.FgDim, FontSize = 11.5, FontWeight = FontWeights.SemiBold });
                    fh.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; if (!_closedFolders.Remove(fk)) _closedFolders.Add(fk); RefreshConnections(); };
                    _connList.Children.Add(fh);
                }
                if (closed) continue;
                foreach (TermConnection c in all.Where(delegate (TermConnection x) { return string.Equals(x.Folder ?? "", fk, StringComparison.OrdinalIgnoreCase); })
                                                .OrderBy(delegate (TermConnection x) { return x.Shown; }, StringComparer.OrdinalIgnoreCase))
                    _connList.Children.Add(ConnRow(c, folder.Length > 0));
            }
        }

        FrameworkElement ConnRow(TermConnection c, bool indent)
        {
            bool open = _sessions.Any(delegate (Session s) { return s.Conn != null && s.Conn.Id == c.Id && !s.Over; });
            Brush dot = !string.IsNullOrWhiteSpace(c.Color) ? (Brush)SafeColor(c.Color) : open ? TermLook.Good : TermLook.Faded(_t.Fg, 30);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var d = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            g.Children.Add(d);
            var col = new StackPanel();
            col.Children.Add(new TextBlock { Text = c.Shown, Foreground = _t.Fg, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            col.Children.Add(new TextBlock
            {
                Text = c.Target + (c.Port != 22 ? ":" + c.Port : "") + "  ·  " + (c.Auth == "Key" ? "key" : c.Auth == "Agent" ? "agent" : c.Secret.Length > 0 ? "password saved" : "password") + (open ? "  ·  connected" : ""),
                Foreground = _t.FgDim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(col, 1);
            g.Children.Add(col);
            var row = new Border { Child = g, Padding = new Thickness(indent ? 18 : 8, 4, 6, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Click to connect - right-click for more" };
            row.MouseEnter += delegate { row.Background = _t.Hover; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            TermConnection cc = c;
            row.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            row.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Connect(cc, null); };
            row.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; ConnMenu(row, cc); };
            return row;
        }

        SolidColorBrush SafeColor(string spec)
        {
            try { return _t.Resolve(spec, _t.MainAccent); } catch { return _t.MainAccent; }
        }

        void ConnMenu(FrameworkElement target, TermConnection c)
        {
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            Item(m, "Connect", delegate { Connect(c, null); });
            Item(m, "Files (SFTP)", delegate { OpenFiles(c); });
            m.Items.Add(new Separator());
            Item(m, "Edit…", delegate { EditConnection(c); });
            Item(m, "Duplicate", delegate
            {
                TermConnection d = c.Copy();
                d.Id = Guid.NewGuid().ToString("N");
                d.Name = c.Shown + " (copy)";
                d.HostKey = c.HostKey;
                S.Connections.Add(d);
                Saved();
            });
            Item(m, "Copy my key to this server…", delegate { CopyKeyTo(c); });
            m.Items.Add(new Separator());
            Item(m, "Delete", delegate
            {
                if (!Ask("Delete the saved connection \"" + c.Shown + "\" (" + c.Target + ")?", "vTerm")) return;
                S.Connections.RemoveAll(delegate (TermConnection x) { return x.Id == c.Id; });
                Saved();
            });
            OpenAtPointer(m);
        }

        static void Item(ItemsControl m, string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += delegate { a(); };
            m.Items.Add(mi);
        }

        /// <summary>The connections in the new-tab menu (by folder).</summary>
        void AddConnectionItems(ContextMenu m)
        {
            List<TermConnection> all = S.Connections ?? new List<TermConnection>();
            m.Items.Add(new Separator());
            if (all.Count == 0) { Item(m, "New SSH connection…", delegate { NewConnection(null); }); return; }
            var byFolder = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
            foreach (TermConnection c in all.OrderBy(delegate (TermConnection x) { return x.Folder ?? ""; }, StringComparer.OrdinalIgnoreCase)
                                            .ThenBy(delegate (TermConnection x) { return x.Shown; }, StringComparer.OrdinalIgnoreCase))
            {
                TermConnection cc = c;
                ItemsControl into = m;
                if (!string.IsNullOrEmpty(c.Folder))
                {
                    MenuItem f;
                    if (!byFolder.TryGetValue(c.Folder, out f)) { f = new MenuItem { Header = c.Folder }; byFolder[c.Folder] = f; m.Items.Add(f); }
                    into = f;
                }
                Item(into, c.Shown + "   (" + c.Target + ")", delegate { Connect(cc, null); });
            }
            Item(m, "New SSH connection…", delegate { NewConnection(null); });
        }

        void Saved()
        {
            _s.SaveSoon();
            TermSsh.RaiseChanged();
        }

        // ---- new / edit ----
        public void NewConnection(TermConnection preset)
        {
            TermConnection c = preset ?? new TermConnection();
            if (TermDialogs.Edit(c, true, delegate (Func<Window, bool> f) { return Dialog(f); }))
            {
                S.Connections.Add(c);
                Saved();
            }
        }

        void EditConnection(TermConnection c)
        {
            if (TermDialogs.Edit(c, false, delegate (Func<Window, bool> f) { return Dialog(f); }))
            {
                // a connection's file panel signs in again with the new details
                SftpClient old;
                if (_sftp.TryGetValue(c.Id, out old)) { _sftp.Remove(c.Id); try { old.Dispose(); } catch { } }
                Saved();
            }
        }

        void ImportConfig()
        {
            List<TermConnection> found;
            try { found = TermSsh.ReadSshConfig(); }
            catch (Exception ex) { Tell("Couldn't read ~/.ssh/config: " + ex.Message, "vTerm"); return; }
            int added = 0;
            foreach (TermConnection c in found)
            {
                bool have = S.Connections.Any(delegate (TermConnection x)
                {
                    return string.Equals(x.Host, c.Host, StringComparison.OrdinalIgnoreCase) && string.Equals(x.User, c.User, StringComparison.OrdinalIgnoreCase) && x.Port == c.Port;
                });
                if (have) continue;
                c.Normalize();
                S.Connections.Add(c);
                added++;
            }
            Saved();
            Tell(found.Count == 0 ? "There are no named hosts in " + Path.Combine(TermSsh.SshFolder, "config") + "." :
                 added == 0 ? "All " + found.Count + " hosts in your ssh config are already saved." :
                 "Added " + added + " connection" + (added == 1 ? "" : "s") + " (folder \"SSH config\").", "vTerm");
        }

        // ---- your key ----
        void KeyMenu(FrameworkElement target)
        {
            string key = TermSsh.DefaultKeyFile;
            bool have = File.Exists(key);
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            if (!have) Item(m, "Make my SSH key (ed25519)…", delegate { MakeKey(); });
            else
            {
                m.Items.Add(new MenuItem { Header = "Your key: " + key, IsEnabled = false });
                Item(m, "Copy my public key", delegate
                {
                    string pub = TermSsh.PublicKeyOf(key);
                    try { Clipboard.SetText(pub); } catch { }
                    Tell(pub.Length > 0 ? "Copied - paste it into a server's ~/.ssh/authorized_keys (or use \"Copy my key to this server\" on a connection)." : "There's no " + key + ".pub.", "vTerm");
                });
            }
            Item(m, "Open my .ssh folder", delegate
            {
                try { Directory.CreateDirectory(TermSsh.SshFolder); Process.Start(new ProcessStartInfo(TermSsh.SshFolder) { UseShellExecute = true }); } catch { }
            });
            OpenAtPointer(m);
        }

        void MakeKey()
        {
            string key = TermSsh.DefaultKeyFile;
            if (!Ask("Make an SSH key at " + key + "?\n\nIt's an ed25519 key without a passphrase (anyone who can use your Windows account can use it). " +
                     "Then use \"Copy my key to this server\" on a connection to sign in with it from then on.", "vTerm")) return;
            try
            {
                Directory.CreateDirectory(TermSsh.SshFolder);
                var psi = new ProcessStartInfo(TermSsh.KeygenExe, "-t ed25519 -f \"" + key + "\" -N \"\" -C \"" + Environment.UserName + "@" + Environment.MachineName + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (!File.Exists(key)) { Tell("ssh-keygen didn't make the key: " + err.Trim(), "vTerm"); return; }
                }
                string pub = TermSsh.PublicKeyOf(key);
                try { Clipboard.SetText(pub); } catch { }
                Tell("Made " + key + " (its public key is copied).\n\nRight-click a connection > \"Copy my key to this server\" to sign in with it.", "vTerm");
            }
            catch (Exception ex) { Tell("Couldn't make the key: " + ex.Message + "\n\n(Windows' OpenSSH Client is needed: Settings > System > Optional features.)", "vTerm"); }
        }

        void CopyKeyTo(TermConnection c)
        {
            string key = c.Auth == "Key" && !string.IsNullOrWhiteSpace(c.KeyFile) ? Environment.ExpandEnvironmentVariables(c.KeyFile) : TermSsh.DefaultKeyFile;
            string pub = TermSsh.PublicKeyOf(key);
            if (pub.Length == 0)
            {
                if (!File.Exists(TermSsh.DefaultKeyFile) && Ask("You don't have an SSH key yet. Make one now?", "vTerm")) MakeKey();
                key = TermSsh.DefaultKeyFile;
                pub = TermSsh.PublicKeyOf(key);
                if (pub.Length == 0) return;
            }
            if (!Ask("Add your public key (" + Path.GetFileName(key) + ".pub) to " + c.Target + "'s ~/.ssh/authorized_keys?\n\n" +
                     "vTerm signs in once the way the connection is set up (password) to add it.", "vTerm")) return;
            SetStatus("Copying your key to " + c.Target + "…");
            TermConnection conn = c;
            Task.Run(delegate
            {
                string err = TermSftp.CopyKey(conn, pub);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetStatus("");
                    if (err.Length > 0) { Tell("Couldn't copy the key: " + err, "vTerm"); return; }
                    if (conn.Auth != "Key" && Ask("Your key is on " + conn.Target + ". Sign in with the key from now on (no password needed)?", "vTerm"))
                    {
                        conn.Auth = "Key";
                        conn.KeyFile = key;
                        conn.Secret = "";
                        Saved();
                    }
                    else if (conn.Auth == "Key") Tell("Your key is on " + conn.Target + ".", "vTerm");
                }));
            });
        }

        // =================================================================
        // Files (SFTP)
        // =================================================================
        void BuildFilesView()
        {
            var top = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
            var up = PanelButton("", "", "Up a folder", delegate { if (_filesPath.Length > 0) LoadFiles(ParentDir(_filesPath)); });
            up.Margin = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(up, Dock.Left);
            top.Children.Add(up);
            var refresh = PanelButton("", "", "Refresh", delegate { LoadFiles(_filesPath); });
            refresh.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(refresh, Dock.Right);
            top.Children.Add(refresh);
            _path.Padding = new Thickness(4, 3, 4, 3);
            _path.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; LoadFiles(_path.Text.Trim()); } };
            top.Children.Add(_path);
            DockPanel.SetDock(top, Dock.Top);
            _filesView.Children.Add(top);

            var bottom = new StackPanel();
            var buttons = new WrapPanel { Margin = new Thickness(8, 2, 8, 0) };
            buttons.Children.Add(PanelButton("", "Upload", "Upload files here (or drop them on the list)", delegate { UploadPick(); }));
            buttons.Children.Add(PanelButton("", "New folder", "Make a folder here", delegate { NewFolder(); }));
            bottom.Children.Add(buttons);
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            _filesView.Children.Add(bottom);

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _fileList, Focusable = false };
            _fileList.Margin = new Thickness(4, 0, 4, 4);
            sv.AllowDrop = true;
            sv.Background = Brushes.Transparent;
            sv.DragOver += delegate (object s, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _listedConn != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            sv.Drop += delegate (object s, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Upload(files);
            };
            _filesView.Children.Add(sv);
        }

        /// <summary>The tab in front changed (or connected / disconnected): the file panel follows its connection.</summary>
        void ActiveChanged()
        {
            ConnectionsChanged();
            if (_showFiles)
            {
                TermConnection c = _active != null ? _active.Conn : null;
                if (c == null || _filesConn == null || c.Id != _filesConn.Id) LoadFiles(null);
            }
        }

        /// <summary>Show a connection's files (from its menu).</summary>
        void OpenFiles(TermConnection c)
        {
            if (!S.PanelOpen) TogglePanel();
            _filesConn = c;
            _filesPath = "";
            _showFiles = true;
            _connView.Visibility = Visibility.Collapsed;
            _filesView.Visibility = Visibility.Visible;
            PanelTheme();
            LoadFiles("");
        }

        void SetStatus(string text) { _status.Text = text ?? ""; _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; }

        static string ParentDir(string p)
        {
            p = (p ?? "").TrimEnd('/');
            int i = p.LastIndexOf('/');
            return i <= 0 ? "/" : p.Substring(0, i);
        }

        static string Join(string dir, string name) { return (dir.EndsWith("/") ? dir : dir + "/") + name; }

        /// <summary>The file panel's client for a connection (signing in when needed). On a background thread.</summary>
        SftpClient Client(TermConnection c)
        {
            SftpClient cl;
            lock (_sftp)
            {
                if (_sftp.TryGetValue(c.Id, out cl) && cl.IsConnected) return cl;
                if (cl != null) { _sftp.Remove(c.Id); try { cl.Dispose(); } catch { } }
            }
            cl = TermSftp.OpenSftp(c);
            lock (_sftp) _sftp[c.Id] = cl;
            return cl;
        }

        void CloseSftp()
        {
            lock (_sftp)
            {
                foreach (SftpClient c in _sftp.Values) try { c.Dispose(); } catch { }
                _sftp.Clear();
            }
        }

        sealed class Entry { public string Name, Full; public bool Dir, Link; public long Size; public DateTime When; public TermConnection Conn; }
        /// <summary>
        /// The server and folder the rows on show were listed from - what every action on them (and Upload, New folder, a drop) works
        /// on. Not _filesConn / _filesPath: those change the moment another tab comes in front, while the old rows are still there.
        /// </summary>
        TermConnection _listedConn;
        string _listedPath = "";
        List<Entry> _lastEntries;

        /// <summary>List a folder (null = the tab in front's connection, where it was / its start folder).</summary>
        void LoadFiles(string path)
        {
            if (path == null)
            {
                TermConnection a = _active != null ? _active.Conn : null;
                if (a != null && (_filesConn == null || _filesConn.Id != a.Id)) { _filesConn = a; _filesPath = ""; }
                path = _filesPath;
            }
            TermConnection c = _filesConn;
            if (c == null)
            {
                _fileList.Children.Clear();
                _path.Text = "";
                _fileList.Children.Add(new TextBlock
                {
                    Text = "Open an SSH tab (or right-click a connection > Files) to see its files here.", TextWrapping = TextWrapping.Wrap,
                    Foreground = _t != null ? _t.FgDim : Brushes.Gray, Margin = new Thickness(6, 6, 6, 0), FontSize = 12
                });
                SetStatus("");
                return;
            }
            int job = ++_job;
            // another server's rows don't stay on show while this one connects (a click on them would be on the wrong server)
            if (_listedConn != null && _listedConn.Id != c.Id) { _fileList.Children.Clear(); _lastEntries = null; _listedConn = null; _listedPath = ""; _path.Text = ""; }
            SetStatus("Connecting to " + c.Target + "…");
            string want = path;
            Task.Run(delegate
            {
                try
                {
                    SftpClient cl = Client(c);
                    var list = new List<Entry>();
                    string dir;
                    lock (cl)
                    {
                        dir = string.IsNullOrEmpty(want) ? (string.IsNullOrWhiteSpace(c.SftpPath) ? cl.WorkingDirectory : c.SftpPath) : want;
                        foreach (var f in cl.ListDirectory(dir))
                        {
                            if (f.Name == "." || f.Name == "..") continue;
                            list.Add(new Entry { Name = f.Name, Full = f.FullName, Dir = f.IsDirectory, Link = f.IsSymbolicLink, Size = f.Length, When = f.LastWriteTime, Conn = c });
                        }
                    }
                    list.Sort(delegate (Entry x, Entry y) { return x.Dir != y.Dir ? (x.Dir ? -1 : 1) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (job != _job) return;
                        _filesPath = dir;
                        _listedConn = c;
                        _listedPath = dir;
                        _path.Text = dir;
                        SetStatus(c.Target + "  ·  " + list.Count + " item" + (list.Count == 1 ? "" : "s"));
                        RenderFiles(list);
                    }));
                }
                catch (Exception ex)
                {
                    Log.Write("Terminal SFTP " + c.Target + ": " + ex.Message);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (job != _job) return;
                        _fileList.Children.Clear();
                        _lastEntries = null; _listedConn = null; _listedPath = "";
                        SetStatus("Couldn't open " + (string.IsNullOrEmpty(want) ? c.Target : want) + ": " + ex.Message);
                    }));
                }
            });
        }

        void RenderFiles(List<Entry> list)
        {
            _lastEntries = list;
            _fileList.Children.Clear();
            if (list == null || _t == null) return;
            foreach (Entry e in list)
            {
                Entry it = e;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock
                {
                    Text = e.Dir ? "" : "", FontFamily = TermLook.Glyphs, FontSize = 12, Foreground = e.Dir ? _t.MainAccent : _t.FgDim,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
                });
                var name = new TextBlock { Text = e.Name + (e.Link ? "  →" : ""), Foreground = _t.Fg, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                g.Children.Add(name);
                var size = new TextBlock { Text = e.Dir ? "" : SizeText(e.Size), Foreground = _t.FgDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(size, 2);
                g.Children.Add(size);
                var row = new Border
                {
                    Child = g, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    ToolTip = e.Full + (e.Dir ? "" : "\n" + SizeText(e.Size)) + "\n" + e.When.ToString("d MMM yyyy h:mm tt") + "\nDouble-click: " + (e.Dir ? "open" : "open with its app") + " - right-click for more"
                };
                row.MouseEnter += delegate { row.Background = _t.Hover; };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                row.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs ev)
                {
                    ev.Handled = true;
                    if (ev.ClickCount == 2) { if (it.Dir) LoadFiles(it.Full); else OpenRemote(it); }
                };
                row.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs ev) { ev.Handled = true; FileMenu(row, it); };
                _fileList.Children.Add(row);
            }
        }

        static string SizeText(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.#") + " MB";
            return (b / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }

        void FileMenu(FrameworkElement target, Entry e)
        {
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            if (e.Dir) Item(m, "Open", delegate { LoadFiles(e.Full); });
            else
            {
                Item(m, "Open with its app", delegate { OpenRemote(e); });
                Item(m, "Download…", delegate { Download(e); });
            }
            Item(m, "Copy path", delegate { try { Clipboard.SetText(e.Full); } catch { } });
            if (e.Dir) Item(m, "Open a terminal here", delegate { if (e.Conn != null) Connect(e.Conn, "cd '" + e.Full.Replace("'", "'\\''") + "' && exec $SHELL -l"); });
            m.Items.Add(new Separator());
            Item(m, "Rename…", delegate { Rename(e); });
            Item(m, "Delete…", delegate { Delete(e); });
            OpenAtPointer(m);
        }

        /// <summary>Do something with the file panel's client on a background thread, then show the folder again.</summary>
        void Job(string doing, Action<SftpClient> work, bool reload = true, TermConnection on = null)
        {
            TermConnection c = on ?? _listedConn;     // the server the rows came from
            if (c == null) { SetStatus("Wait for the folder to show first."); return; }
            SetStatus(doing + "…");
            Task.Run(delegate
            {
                try
                {
                    SftpClient cl = Client(c);
                    lock (cl) work(cl);
                    Dispatcher.BeginInvoke(new Action(delegate { SetStatus(""); if (reload && _listedConn != null && _listedConn.Id == c.Id) LoadFiles(_listedPath); }));
                }
                catch (Exception ex)
                {
                    Log.Write("Terminal SFTP: " + doing + ": " + ex.Message);
                    Dispatcher.BeginInvoke(new Action(delegate { SetStatus(doing + " failed: " + ex.Message); }));
                }
            });
        }

        void Progress(string what, ulong done, long total)
        {
            Dispatcher.BeginInvoke(new Action(delegate { SetStatus(what + (total > 0 ? "  " + Math.Min(100, (long)done * 100 / total) + " %" : "  " + SizeText((long)done))); }));
        }

        void UploadPick()
        {
            TermConnection c = _listedConn;
            string dest = _listedPath;
            if (c == null || string.IsNullOrEmpty(dest)) { SetStatus("Wait for the folder to show first."); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Upload to " + c.Target + ":" + dest };
            bool ok = Dialog(delegate (Window o) { return dlg.ShowDialog(o) == true; });
            if (ok) Upload(dlg.FileNames, c, dest);      // where it was when you chose - not where the panel has gone since
        }

        /// <summary>Upload files / folders (folders with everything in them) into the folder shown.</summary>
        void Upload(string[] paths, TermConnection c = null, string dest = null)
        {
            if (paths == null || paths.Length == 0) return;
            if (c == null) { c = _listedConn; dest = _listedPath; }
            // no folder on show yet (still connecting, or it couldn't be opened): never guess - "" would be the server's root
            if (c == null || string.IsNullOrEmpty(dest)) { SetStatus("Wait for the folder to show first."); return; }
            Job("Uploading " + (paths.Length == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : paths.Length + " items"), delegate (SftpClient cl)
            {
                foreach (string p in paths) UploadOne(cl, p, dest);
            }, true, c);
        }

        void UploadOne(SftpClient cl, string local, string remoteDir)
        {
            string name = Path.GetFileName(local.TrimEnd('\\'));
            string target = Join(remoteDir, name);
            if (Directory.Exists(local))
            {
                if (!cl.Exists(target)) cl.CreateDirectory(target);
                foreach (string sub in Directory.GetFileSystemEntries(local)) UploadOne(cl, sub, target);
                return;
            }
            long total = new FileInfo(local).Length;
            using (FileStream fs = File.OpenRead(local))
                cl.UploadFile(fs, target, true, delegate (ulong done) { Progress("Uploading " + name, done, total); });
        }

        void Download(Entry e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = e.Name, Title = "Download " + e.Name };
            try { dlg.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); } catch { }
            bool ok = Dialog(delegate (Window o) { return dlg.ShowDialog(o) == true; });
            if (!ok) return;
            string local = dlg.FileName;
            Job("Downloading " + e.Name, delegate (SftpClient cl)
            {
                using (FileStream fs = File.Create(local))
                    cl.DownloadFile(e.Full, fs, delegate (ulong done) { Progress("Downloading " + e.Name, done, e.Size); });
            }, false, e.Conn);
        }

        /// <summary>Download to a temporary folder and open it with its app (changes aren't sent back).</summary>
        void OpenRemote(Entry e)
        {
            TermConnection c = e.Conn;
            if (c == null) return;
            string dir = Path.Combine(Path.GetTempPath(), "vTerm-files", MakeSafe(c.Host));
            string local = Path.Combine(dir, MakeSafe(e.Name));
            Job("Opening " + e.Name, delegate (SftpClient cl)
            {
                Directory.CreateDirectory(dir);
                using (FileStream fs = File.Create(local))
                    cl.DownloadFile(e.Full, fs, delegate (ulong done) { Progress("Opening " + e.Name, done, e.Size); });
                Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
            }, false, c);
        }

        static string MakeSafe(string n)
        {
            foreach (char ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
            return n;
        }

        void NewFolder()
        {
            TermConnection c = _listedConn;
            string dir = _listedPath;
            if (c == null || string.IsNullOrEmpty(dir)) { SetStatus("Wait for the folder to show first."); return; }
            string name = Dialog(delegate (Window o) { return FormWindow.Prompt(o, "New folder", "Name of the new folder in " + dir, ""); });
            if (string.IsNullOrWhiteSpace(name)) return;
            string target = Join(dir, name.Trim());
            Job("Making " + name.Trim(), delegate (SftpClient cl) { cl.CreateDirectory(target); }, true, c);
        }

        void Rename(Entry e)
        {
            string name = Dialog(delegate (Window o) { return FormWindow.Prompt(o, "Rename", "New name for " + e.Name, e.Name); });
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == e.Name) return;
            string target = Join(ParentDir(e.Full), name.Trim());
            Job("Renaming " + e.Name, delegate (SftpClient cl) { cl.RenameFile(e.Full, target); }, true, e.Conn);
        }

        void Delete(Entry e)
        {
            if (!Ask("Delete " + (e.Dir ? "the folder " + e.Full + " and everything in it" : e.Full) + " on " + (e.Conn != null ? e.Conn.Target : "the server") + "?\n\nThis can't be undone.", "vTerm")) return;
            Job("Deleting " + e.Name, delegate (SftpClient cl) { DeleteOne(cl, e.Full, e.Dir && !e.Link); }, true, e.Conn);
        }

        static void DeleteOne(SftpClient cl, string path, bool dir)
        {
            if (!dir) { cl.DeleteFile(path); return; }
            foreach (var f in cl.ListDirectory(path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                DeleteOne(cl, f.FullName, f.IsDirectory && !f.IsSymbolicLink);
            }
            cl.DeleteDirectory(path);
        }
    }
}
