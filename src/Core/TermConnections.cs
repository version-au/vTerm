using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace vTerm.Core
{
    /// <summary>
    /// A saved SSH connection. Saved in vTerm's settings, so every vTerm window has them. The password / key passphrase is stored
    /// encrypted for your Windows account. The same fields (and JSON names) as vShell's, so vShell's connections come over as they are.
    /// </summary>
    public sealed class TermConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        /// <summary>The folder it's listed under ("" = none).</summary>
        public string Folder { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "";
        /// <summary>"Password", "Key" (KeyFile) or "Agent" (Windows' ssh-agent / default keys).</summary>
        public string Auth { get; set; } = "Password";
        public string KeyFile { get; set; } = "";
        /// <summary>The password (Password) or the key's passphrase (Key), encrypted (Secret.Protect); "" = asked in the terminal.</summary>
        public string Secret { get; set; } = "";
        /// <summary>Jump host (ssh -J), e.g. "user@bastion:22".</summary>
        public string Jump { get; set; } = "";
        /// <summary>Run on the server after connecting (empty = the login shell).</summary>
        public string Command { get; set; } = "";
        /// <summary>The tab's colour ("#RRGGBB", "" = the theme's).</summary>
        public string Color { get; set; } = "";
        /// <summary>SFTP: the folder the file panel starts in ("" = home).</summary>
        public string SftpPath { get; set; } = "";
        /// <summary>The server's key fingerprint (SHA256) once trusted (the file panel checks it).</summary>
        public string HostKey { get; set; } = "";

        /// <summary>user@host</summary>
        public string Target { get { return (string.IsNullOrWhiteSpace(User) ? "" : User.Trim() + "@") + (Host ?? "").Trim(); } }
        public string Shown { get { return string.IsNullOrWhiteSpace(Name) ? Target : Name; } }

        public TermConnection Copy()
        {
            return new TermConnection
            {
                Id = Id, Name = Name, Folder = Folder, Host = Host, Port = Port, User = User, Auth = Auth, KeyFile = KeyFile, Secret = Secret,
                Jump = Jump, Command = Command, Color = Color, SftpPath = SftpPath, HostKey = HostKey
            };
        }

        public void Normalize()
        {
            if (string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString("N");
            Name = Name ?? ""; Folder = (Folder ?? "").Trim(); Host = (Host ?? "").Trim(); User = (User ?? "").Trim();
            if (Port <= 0 || Port > 65535) Port = 22;
            if (Auth != "Key" && Auth != "Agent") Auth = "Password";
            KeyFile = KeyFile ?? ""; Secret = Secret ?? ""; Jump = (Jump ?? "").Trim(); Command = Command ?? ""; Color = Color ?? "";
            SftpPath = SftpPath ?? ""; HostKey = HostKey ?? "";
        }

        /// <summary>"user@host:port" (quick connect) as a connection that isn't saved.</summary>
        public static TermConnection Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4).Trim();
            if (text.Length == 0 || text.Contains(" ")) return null;
            var c = new TermConnection { Auth = "Agent" };
            int at = text.LastIndexOf('@');
            if (at > 0) { c.User = text.Substring(0, at); text = text.Substring(at + 1); }
            int colon = text.LastIndexOf(':');
            int port;
            if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text.Substring(colon + 1), out port)) { c.Port = port; text = text.Substring(0, colon); }
            c.Host = text;
            c.Name = c.Target;
            return c.Host.Length > 0 ? c : null;
        }
    }

    /// <summary>Running a saved connection with Windows' own OpenSSH, and the bits around it (askpass, ~/.ssh/config, keys).</summary>
    public static class TermSsh
    {
        /// <summary>The saved connections changed (the windows' lists show them again).</summary>
        public static event Action Changed;
        public static void RaiseChanged() { var h = Changed; if (h != null) h(); }

        public static string OpenSshDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH"); } }
        public static string SshExe { get { string p = Path.Combine(OpenSshDir, "ssh.exe"); return File.Exists(p) ? p : "ssh.exe"; } }
        public static string KeygenExe { get { string p = Path.Combine(OpenSshDir, "ssh-keygen.exe"); return File.Exists(p) ? p : "ssh-keygen.exe"; } }
        public static bool HaveSsh { get { return File.Exists(Path.Combine(OpenSshDir, "ssh.exe")); } }
        public static string SshFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"); } }

        static string Q(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            if (a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>The ssh command line for a connection; remote = a command to run there instead of the saved one (null = the saved one).</summary>
        public static string CommandLine(TermConnection c, string remote)
        {
            var sb = new StringBuilder(Q(SshExe));
            sb.Append(" -o ServerAliveInterval=30 -o ServerAliveCountMax=3");
            if (c.Port != 22) sb.Append(" -p ").Append(c.Port);
            if (c.Auth == "Key" && !string.IsNullOrWhiteSpace(c.KeyFile)) sb.Append(" -i ").Append(Q(Environment.ExpandEnvironmentVariables(c.KeyFile))).Append(" -o IdentitiesOnly=yes");
            if (!string.IsNullOrWhiteSpace(c.Jump)) sb.Append(" -J ").Append(Q(c.Jump));
            // a saved password is answered for you, so a wrong one would be tried three times in a blink (and get this PC banned
            // by fail2ban): once is enough
            if (!string.IsNullOrEmpty(c.Secret) && c.Auth != "Agent") sb.Append(" -o NumberOfPasswordPrompts=1");
            string cmd = remote ?? c.Command;
            if (!string.IsNullOrWhiteSpace(cmd)) sb.Append(" -t");
            sb.Append(' ').Append(Q(c.Target));
            if (!string.IsNullOrWhiteSpace(cmd)) sb.Append(' ').Append(Q(cmd));
            return sb.ToString();
        }

        /// <summary>
        /// The environment for ssh: with a saved password / passphrase, ssh asks vTerm for it (SSH_ASKPASS = vTerm.exe, which answers from
        /// the encrypted copy - the password is never typed or shown). Without one, ssh asks in the terminal as usual.
        /// </summary>
        public static Dictionary<string, string> Env(TermConnection c)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (c == null || string.IsNullOrEmpty(c.Secret) || c.Auth == "Agent") return env;
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return env;
            env["SSH_ASKPASS"] = exe;
            env["SSH_ASKPASS_REQUIRE"] = "force";
            env["VTERM_ASKPASS"] = "1";
            env["VTERM_ASKPASS_SECRET"] = c.Secret;          // the encrypted copy (only this Windows account can read it)
            env["VTERM_ASKPASS_HOST"] = c.Target;
            env["VTERM_ASKPASS_AUTH"] = c.Auth ?? "";         // what the saved secret is: a password, or a key's passphrase
            return env;
        }

        /// <summary>vTerm.exe started by ssh to answer a question (SSH_ASKPASS).</summary>
        public static bool IsAskpass { get { return Environment.GetEnvironmentVariable("VTERM_ASKPASS") == "1"; } }

        /// <summary>
        /// Answer ssh: a password / passphrase prompt gets the saved one; "are you sure you want to continue connecting (yes/no)" (a server
        /// seen for the first time, or its key changed) is asked in a question window. Returns the exit code.
        /// </summary>
        public static int Askpass(string[] args, Func<string, string, bool> askYesNo)
        {
            string prompt = string.Join(" ", args ?? new string[0]);
            string host = Environment.GetEnvironmentVariable("VTERM_ASKPASS_HOST") ?? "";
            try
            {
                if (prompt.IndexOf("yes/no", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bool yes = askYesNo != null && askYesNo("vTerm - " + host, prompt.Trim());
                    Console.Out.WriteLine(yes ? "yes" : "no");
                    Console.Out.Flush();
                    return 0;
                }
                if (!SecretFits(prompt, host, Environment.GetEnvironmentVariable("VTERM_ASKPASS_AUTH"))) return 1;
                string secret = vSuite.Secret.Unprotect(Environment.GetEnvironmentVariable("VTERM_ASKPASS_SECRET") ?? "");
                if (secret.Length == 0) return 1;
                Console.Out.WriteLine(secret);
                Console.Out.Flush();
                return 0;
            }
            catch { return 1; }
        }

        /// <summary>
        /// Is the saved secret the answer to this prompt? A key's passphrase only answers "Enter passphrase for key …" - never a
        /// password prompt (the server turned the key down and asks for a password: the passphrase isn't one, and would be sent to
        /// the server). A password never answers a passphrase prompt, nor a password prompt that names another machine (a jump
        /// host: "jump@bastion's password:" would have been given this server's password).
        /// </summary>
        public static bool SecretFits(string prompt, string target, string auth)
        {
            prompt = prompt ?? "";
            bool passphrase = prompt.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0;
            if (auth == "Key") return passphrase;
            if (auth == "Password" && passphrase) return false;
            if (passphrase) return true;
            // "user@host's password:" / "(user@host) Password:" - the machine it names must be this connection's
            var m = System.Text.RegularExpressions.Regex.Match(prompt, @"@([^\s'():]+)");
            if (!m.Success || string.IsNullOrEmpty(target)) return true;
            string mine = target.Substring(target.LastIndexOf('@') + 1).Trim();
            return string.Equals(m.Groups[1].Value.Trim(), mine, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The connections in ~/.ssh/config (named hosts, not patterns), for Import.</summary>
        public static List<TermConnection> ReadSshConfig()
        {
            var list = new List<TermConnection>();
            string file = Path.Combine(SshFolder, "config");
            if (!File.Exists(file)) return list;
            TermConnection cur = null;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t', '=' });
                if (sp < 0) continue;
                string key = line.Substring(0, sp).Trim().ToLowerInvariant(), val = line.Substring(sp + 1).Trim().Trim('=').Trim().Trim('"');
                if (key == "host")
                {
                    cur = null;
                    string first = val.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (first.Contains("*") || first.Contains("?") || first.StartsWith("!")) continue;
                    cur = new TermConnection { Name = first, Host = first, Folder = "SSH config", Auth = "Agent" };
                    list.Add(cur);
                    continue;
                }
                if (key == "match") { cur = null; continue; }
                if (cur == null) continue;
                int port;
                switch (key)
                {
                    case "hostname": cur.Host = val; break;
                    case "user": cur.User = val; break;
                    case "port": if (int.TryParse(val, out port)) cur.Port = port; break;
                    case "identityfile":
                        cur.KeyFile = val.StartsWith("~") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), val.Substring(1).TrimStart('/', '\\')) : val;
                        cur.Auth = "Key";
                        break;
                    case "proxyjump": cur.Jump = val; break;
                }
            }
            return list;
        }

        /// <summary>A saved connection for this host (by host, then by name, case-insensitive), or null.</summary>
        public static TermConnection Find(List<TermConnection> all, string host)
        {
            if (all == null || string.IsNullOrWhiteSpace(host)) return null;
            host = host.Trim();
            foreach (TermConnection c in all) if (string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (TermConnection c in all) if (string.Equals(c.Name, host, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>The public key next to a private key file ("" when there is none).</summary>
        public static string PublicKeyOf(string keyFile)
        {
            try
            {
                string p = Environment.ExpandEnvironmentVariables(keyFile ?? "") + ".pub";
                return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>The key vTerm suggests: ~/.ssh/id_ed25519 (made when it isn't there).</summary>
        public static string DefaultKeyFile { get { return Path.Combine(SshFolder, "id_ed25519"); } }
    }
}
