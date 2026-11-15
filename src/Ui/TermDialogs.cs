using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>The terminal's dialogs: editing a saved connection, and the questions the SSH side asks (trust a server, a password).</summary>
    public static class TermDialogs
    {
        public static readonly string[] AuthNames = { "Password", "Key file", "SSH agent / my default keys" };
        static readonly string[] AuthKeys = { "Password", "Key", "Agent" };

        static AppSettings _s;

        /// <summary>Hook the SSH side's questions up to dialogs on the UI thread (once).</summary>
        public static void Init(AppSettings s)
        {
            if (_s != null) return;
            _s = s;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            TermSftp.AskYesNo = delegate (string title, string q)
            {
                return (bool)ui.Invoke(new Func<bool>(delegate { return Looks.YesNo(Front(), q, title); }));
            };
            TermSftp.AskPassword = delegate (string title, string label)
            {
                return (string)ui.Invoke(new Func<string>(delegate
                {
                    var f = new List<FormField> { FormField.Password(label, "") };
                    return FormWindow.Ask(Front(), title, f) ? f[0].Value : null;
                }));
            };
            TermSftp.Trusted = delegate (TermConnection c)
            {
                ui.BeginInvoke(new Action(delegate
                {
                    // the saved copy (a quick connection isn't saved)
                    TermConnection saved = s.Connections.Find(delegate (TermConnection x) { return x.Id == c.Id; });
                    if (saved != null && saved != c) saved.HostKey = c.HostKey;
                    s.SaveSoon();
                }));
            };
        }

        /// <summary>The vTerm window in front (dialogs from the SSH side go over it).</summary>
        static Window Front()
        {
            foreach (Window w in Application.Current.Windows) if (w is MainWindow && w.IsActive) return w;
            return Application.Current.MainWindow;
        }

        /// <summary>Edit (or make) a connection. run = how the dialog is shown. true = OK.</summary>
        public static bool Edit(TermConnection c, bool isNew, Func<Func<Window, bool>, bool> run)
        {
            int ai = Math.Max(0, Array.IndexOf(AuthKeys, c.Auth));
            var name = new FormField("Name", c.Name) { Hint = "What the list and the tab show (empty = user@host)." };
            var folder = new FormField("Folder", c.Folder) { Hint = "Group it under a folder (e.g. Proxmox, Home Assistant) - empty = none." };
            var host = new FormField("Host", c.Host) { Hint = "Address or name, e.g. 192.168.1.10 or pve.local" };
            var port = new FormField("Port", c.Port.ToString());
            var user = new FormField("User", c.User) { Hint = "e.g. root" };
            var auth = FormField.Combo("Sign in with", AuthNames[ai], AuthNames);
            var secret = FormField.Password("Password / key passphrase",
                (c.Secret.Length > 0 ? "Saved (encrypted for your Windows account). Leave it empty to keep it" : "Empty = typed in the terminal when asked") +
                "; for a key without a passphrase leave it empty. Tick below to forget a saved one.");
            var forget = FormField.Check("Forget the saved password / passphrase", false);
            var key = new FormField("Key file", c.KeyFile) { Hint = "For \"Key file\": e.g. " + TermSsh.DefaultKeyFile + " (My key in the panel makes one)." };
            var jump = new FormField("Jump host", c.Jump) { Hint = "Optional: go through another server first (ssh -J), e.g. user@bastion" };
            var cmd = new FormField("Run on connect", c.Command) { Hint = "Optional: a command instead of the login shell (e.g. htop, or tmux attach)." };
            var color = new FormField("Tab colour", c.Color) { Hint = "Optional: #RRGGBB, or @main / @secondary for the theme's accents." };
            var sftp = new FormField("Files start in", c.SftpPath) { Hint = "Optional: the folder the file panel opens (empty = home)." };
            var fields = new List<FormField> { name, folder, host, port, user, auth, secret, forget, key, jump, cmd, color, sftp };
            while (true)
            {
                bool ok = run(delegate (Window o) { return FormWindow.Ask(o, isNew ? "New SSH connection" : "Edit " + c.Shown, fields); });
                if (!ok) return false;
                if (host.Value.Trim().Length == 0) { Looks.Tell(Front(), "The connection needs a host.", "vTerm"); continue; }
                break;
            }
            c.Name = name.Value.Trim();
            c.Folder = folder.Value.Trim();
            c.Host = host.Value.Trim();
            int p;
            c.Port = int.TryParse(port.Value.Trim(), out p) && p > 0 && p < 65536 ? p : 22;
            c.User = user.Value.Trim();
            c.Auth = AuthKeys[Math.Max(0, Array.IndexOf(AuthNames, auth.Value))];
            if (forget.Checked) c.Secret = "";
            if (secret.Value.Length > 0) c.Secret = vSuite.Secret.Protect(secret.Value);
            c.KeyFile = key.Value.Trim().Trim('"');
            if (c.Auth == "Key" && c.KeyFile.Length == 0) c.KeyFile = TermSsh.DefaultKeyFile;
            c.Jump = jump.Value.Trim();
            c.Command = cmd.Value.Trim();
            c.Color = color.Value.Trim();
            c.SftpPath = sftp.Value.Trim();
            c.Normalize();
            return true;
        }
    }
}
