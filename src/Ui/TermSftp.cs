using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Renci.SshNet;
using Renci.SshNet.Common;
using vTerm.Core;

namespace vTerm.Ui
{
    /// <summary>
    /// SSH.NET for the terminal's file panel (SFTP) and for copying your key to a server. Signs in the way the saved connection says:
    /// password (the saved one, or asked), key file (+ passphrase), or for "SSH agent" your default key files. The server's key is
    /// checked against the one trusted before (asked the first time; a change is warned about).
    /// </summary>
    public static class TermSftp
    {
        /// <summary>Asks on the UI thread (the caller is a background thread): (title, question) -> yes.</summary>
        public static Func<string, string, bool> AskYesNo;
        /// <summary>Asks for a password on the UI thread: (title, label) -> the password, or null.</summary>
        public static Func<string, string, string> AskPassword;
        /// <summary>A connection's trusted key was saved (so the settings get saved).</summary>
        public static Action<TermConnection> Trusted;

        /// <summary>How to sign in, for SSH.NET. Throws with a readable message when it can't.</summary>
        public static ConnectionInfo Info(TermConnection c)
        {
            string user = string.IsNullOrWhiteSpace(c.User) ? Environment.UserName : c.User.Trim();
            var methods = new List<AuthenticationMethod>();
            string secret = vSuite.Secret.Unprotect(c.Secret ?? "");
            if (c.Auth == "Password")
            {
                if (secret.Length == 0 && AskPassword != null) secret = AskPassword("SFTP - " + c.Target, "Password for " + c.Target) ?? "";
                if (secret.Length == 0) throw new InvalidOperationException("No password - save one in the connection (Edit) or type it when asked.");
                string pw = secret;
                methods.Add(new PasswordAuthenticationMethod(user, pw));
                var ki = new KeyboardInteractiveAuthenticationMethod(user);
                ki.AuthenticationPrompt += delegate (object s, AuthenticationPromptEventArgs e) { foreach (AuthenticationPrompt p in e.Prompts) p.Response = pw; };
                methods.Add(ki);
            }
            else
            {
                var keys = new List<IPrivateKeySource>();
                var files = new List<string>();
                if (c.Auth == "Key" && !string.IsNullOrWhiteSpace(c.KeyFile)) files.Add(Environment.ExpandEnvironmentVariables(c.KeyFile));
                else foreach (string n in new[] { "id_ed25519", "id_ecdsa", "id_rsa" }) files.Add(Path.Combine(TermSsh.SshFolder, n));
                string firstErr = null;
                foreach (string f in files)
                {
                    if (!File.Exists(f)) continue;
                    try { keys.Add(secret.Length > 0 ? new PrivateKeyFile(f, secret) : new PrivateKeyFile(f)); }
                    catch (Exception ex) { if (firstErr == null) firstErr = Path.GetFileName(f) + ": " + ex.Message; }
                }
                if (keys.Count == 0)
                    throw new InvalidOperationException(firstErr != null ? "The key couldn't be read (" + firstErr + ") - is its passphrase saved?" :
                                                        c.Auth == "Key" ? "The key file isn't there: " + c.KeyFile : "No key in your .ssh folder - the file panel needs a password or a key file (SSH agent keys can't be used here).");
                methods.Add(new PrivateKeyAuthenticationMethod(user, keys.ToArray()));
            }
            var info = new ConnectionInfo(c.Host.Trim(), c.Port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(12) };
            return info;
        }

        /// <summary>Check the server's key: the trusted one, or ask (first time / changed).</summary>
        static void CheckHostKey(TermConnection c, HostKeyEventArgs e)
        {
            string fp = "";
            try { fp = e.FingerPrintSHA256 ?? ""; } catch { }
            if (fp.Length == 0) { e.CanTrust = true; return; }
            if (string.Equals(c.HostKey, fp, StringComparison.Ordinal)) { e.CanTrust = true; return; }
            bool first = string.IsNullOrEmpty(c.HostKey);
            string q = first
                ? "First connection to " + c.Target + ".\n\nIts key fingerprint is SHA256:" + fp + "\n\nTrust this server?"
                : "WARNING: the key of " + c.Target + " has CHANGED since you last connected.\n\nWas: SHA256:" + c.HostKey + "\nNow: SHA256:" + fp +
                  "\n\nThis can mean the server was reinstalled - or that someone is in between. Trust the new key?";
            bool yes = AskYesNo != null && AskYesNo("vTerm - " + c.Target, q);
            e.CanTrust = yes;
            if (yes) { c.HostKey = fp; if (Trusted != null) Trusted(c); }
        }

        public static SftpClient OpenSftp(TermConnection c)
        {
            if (!string.IsNullOrWhiteSpace(c.Jump)) throw new InvalidOperationException("The file panel can't go through a jump host (" + c.Jump + ").");
            var client = new SftpClient(Info(c));
            client.HostKeyReceived += delegate (object s, HostKeyEventArgs e) { CheckHostKey(c, e); };
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.Connect();
            return client;
        }

        /// <summary>Add your public key to the server's ~/.ssh/authorized_keys (signing in the way the connection says). Returns "" or the problem.</summary>
        public static string CopyKey(TermConnection c, string publicKey)
        {
            publicKey = (publicKey ?? "").Trim().Replace("\r", "").Replace("\n", "");
            if (publicKey.Length == 0) return "There's no public key to copy.";
            if (publicKey.Contains("'")) return "That public key has a quote in it - not copying it.";
            if (!string.IsNullOrWhiteSpace(c.Jump)) return "Copying through a jump host isn't supported - copy it from a terminal tab instead.";
            try
            {
                using (var ssh = new SshClient(Info(c)))
                {
                    ssh.HostKeyReceived += delegate (object s, HostKeyEventArgs e) { CheckHostKey(c, e); };
                    ssh.Connect();
                    string cmd = "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
                                 "(grep -qxF '" + publicKey + "' ~/.ssh/authorized_keys || echo '" + publicKey + "' >> ~/.ssh/authorized_keys)";
                    SshCommand r = ssh.RunCommand(cmd);
                    ssh.Disconnect();
                    if (r.ExitStatus != 0) return "The server said: " + (string.IsNullOrWhiteSpace(r.Error) ? "exit code " + r.ExitStatus : r.Error.Trim());
                }
                return "";
            }
            catch (SshAuthenticationException) { return "The server didn't accept the sign-in (password / key)."; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
