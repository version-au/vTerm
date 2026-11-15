using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// What vTerm was started with - on its own, or handed over to the vTerm that's already running (one vTerm at a time):
    ///   vTerm.exe                                    a window with a tab of the start profile (vTerm running: a new window)
    ///   vTerm.exe --profile "PowerShell 7"           a tab with that profile
    ///   vTerm.exe --dir "C:\Projects"                a tab starting in that folder (with --profile, or the start profile)
    ///   vTerm.exe --run "htop" [--title "Top"]       a tab running that command line
    ///   vTerm.exe --ssh root@pve:22                  an SSH tab (a saved connection for that host if there is one, else a quick one)
    ///   vTerm.exe --host 192.168.1.5 --name pve --user root [--run "docker exec -it x sh"] [--why "..."]
    ///                                                an SSH tab to a saved connection (by host, then name); none saved = offers to
    ///                                                save one first (vShell's Open shell from Komodo / Proxmox …)
    ///   --new-window                                 in a new window rather than the one in front
    /// </summary>
    public sealed class StartRequest
    {
        public enum Kinds { Tab, Ssh }
        public Kinds Kind = Kinds.Tab;
        public bool NewWindow;
        public string Profile, Folder, Command, Title;
        public string Ssh, Host, Name, User, Remote, Why;

        const string HandOffPrefix = "vterm:";

        /// <summary>What the command line asks for; null = nothing (just start / a new window).</summary>
        public static StartRequest Parse(string[] args)
        {
            if (args == null || args.Length == 0) return null;
            var r = new StartRequest();
            bool any = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--new-window": r.NewWindow = true; any = true; break;
                    case "--profile": r.Profile = next; i++; any = true; break;
                    case "--dir": r.Folder = next; i++; any = true; break;
                    case "--title": r.Title = next; i++; break;
                    case "--run": r.Command = next; r.Remote = next; i++; any = true; break;
                    case "--ssh": r.Ssh = next; r.Kind = Kinds.Ssh; i++; any = true; break;
                    case "--host": r.Host = next; r.Kind = Kinds.Ssh; i++; any = true; break;
                    case "--name": r.Name = next; i++; break;
                    case "--user": r.User = next; i++; break;
                    case "--why": r.Why = next; i++; break;
                    default:
                        // a folder dropped on vTerm.exe (or "Open in vTerm"): a tab there
                        if (!a.StartsWith("--") && System.IO.Directory.Exists(a)) { r.Folder = a; any = true; }
                        break;
                }
            }
            if (r.Kind == Kinds.Tab) r.Remote = null; else r.Command = null;
            if (r.Kind == Kinds.Ssh && r.Host == null && r.Ssh == null && r.Name != null) r.Host = "";
            return any ? r : null;
        }

        /// <summary>The command line as text for the running vTerm (Instance.HandOff).</summary>
        public static string ToHandOff(string[] args) { return HandOffPrefix + JsonSerializer.Serialize(args ?? new string[0]); }

        /// <summary>A hand-off back into a request. No arguments (vTerm started again): a new window.</summary>
        public static StartRequest FromHandOff(string text)
        {
            string[] args = new string[0];
            if (text != null && text.StartsWith(HandOffPrefix, StringComparison.Ordinal))
                try { args = JsonSerializer.Deserialize<string[]>(text.Substring(HandOffPrefix.Length)) ?? new string[0]; } catch { }
            else if (!string.IsNullOrWhiteSpace(text)) args = new[] { text };
            return Parse(args) ?? new StartRequest { NewWindow = true };
        }

        /// <summary>
        /// The connection an SSH request means: --ssh = a saved one for that host or a quick one; --host = a saved one (by host, then name),
        /// else - after asking - a new saved one, or a quick one. null = cancelled.
        /// </summary>
        public TermConnection ResolveConnection(AppSettings s, Window owner)
        {
            if (!string.IsNullOrWhiteSpace(Ssh))
            {
                TermConnection q = TermConnection.Parse(Ssh);
                if (q == null) { Looks.Tell(owner, "\"" + Ssh + "\" isn't an SSH address (user@host or user@host:port).", "vTerm"); return null; }
                TermConnection saved = s.Connections.Find(x => string.Equals(x.Host, q.Host, StringComparison.OrdinalIgnoreCase) &&
                                                               (q.User.Length == 0 || string.Equals(x.User, q.User, StringComparison.OrdinalIgnoreCase)) && x.Port == q.Port);
                return saved ?? q;
            }
            string host = (Host ?? "").Trim();
            if (host.Length == 0)
            {
                // no address (Komodo's own server, a VM without its guest agent): a saved connection with this name will do
                TermConnection named = string.IsNullOrWhiteSpace(Name) ? null : TermSsh.Find(s.Connections, Name);
                if (named != null) return named;
                string who = string.IsNullOrWhiteSpace(Name) ? "this server" : "\"" + Name + "\"";
                string q = (string.IsNullOrWhiteSpace(Why) ? "vTerm doesn't know the address of " + who + "." : Why) +
                           "\n\nSave a connection" + (string.IsNullOrWhiteSpace(Name) ? "" : " named \"" + Name + "\"") +
                           " with its address and Open shell will use it from now on. Make one now?";
                if (!Looks.YesNo(owner, q, "Open shell")) return null;
                var made = new TermConnection { Name = Name ?? "", User = User ?? "", Auth = "Password" };
                return SaveNew(s, owner, made) ? made : null;
            }
            TermConnection c = TermSsh.Find(s.Connections, host) ?? (string.IsNullOrWhiteSpace(Name) ? null : TermSsh.Find(s.Connections, Name));
            if (c != null) return c;
            int pick = Ask.Show(owner, "Open shell", "There's no saved connection for " + host + (string.IsNullOrWhiteSpace(Name) || Name == host ? "" : " (" + Name + ")") +
                                ".\n\nSave one now (you can add the password or key), or connect without saving?",
                                new[] { "Save one", "Connect", "Cancel" }, 0, 2, false, null, null);
            if (pick == 2) return null;
            c = new TermConnection { Name = string.IsNullOrWhiteSpace(Name) ? host : Name, Host = host, User = User ?? "", Auth = "Password" };
            if (pick == 0) return SaveNew(s, owner, c) ? c : null;
            c.Auth = "Agent";
            return c;
        }

        static bool SaveNew(AppSettings s, Window owner, TermConnection c)
        {
            if (!TermDialogs.Edit(c, true, f => f(owner))) return false;
            s.Connections.Add(c);
            s.SaveSoon();
            TermSsh.RaiseChanged();
            return true;
        }
    }
}
