using System;
using System.Collections.Generic;
using System.IO;

namespace vTerm.Core
{
    /// <summary>A terminal profile: a name and the command line it starts (and optionally the folder it starts in).</summary>
    public sealed class TermProfile
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Folder { get; set; } = "";

        /// <summary>The profiles this PC has: Windows PowerShell, PowerShell 7 and WSL when installed, Command Prompt.</summary>
        public static List<TermProfile> Defaults()
        {
            var l = new List<TermProfile>();
            l.Add(new TermProfile { Name = "Windows PowerShell", Command = "powershell.exe -NoLogo" });
            if (FindPwsh() != null) l.Add(new TermProfile { Name = "PowerShell 7", Command = "pwsh.exe -NoLogo" });
            l.Add(new TermProfile { Name = "Command Prompt", Command = "cmd.exe" });
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe")))
                l.Add(new TermProfile { Name = "WSL", Command = "wsl.exe ~" });
            return l;
        }

        static string FindPwsh()
        {
            try
            {
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (dir.Trim().Length == 0) continue;
                    string p = Path.Combine(dir.Trim(), "pwsh.exe");
                    if (File.Exists(p)) return p;
                }
                string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
                if (File.Exists(pf)) return pf;
            }
            catch { }
            return null;
        }

        /// <summary>The list as text, one per line: "Name | command line | start folder" (the folder is optional).</summary>
        public static string ToText(List<TermProfile> list)
        {
            var sb = new System.Text.StringBuilder();
            foreach (TermProfile p in list)
                sb.Append(p.Name).Append(" | ").Append(p.Command).Append(string.IsNullOrWhiteSpace(p.Folder) ? "" : " | " + p.Folder).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>Read the text form back; lines without a command are skipped.</summary>
        public static List<TermProfile> FromText(string text)
        {
            var l = new List<TermProfile>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split('|');
                string name = p[0].Trim(), cmd = p.Length >= 2 ? p[1].Trim() : p[0].Trim(), folder = p.Length >= 3 ? p[2].Trim() : "";
                if (cmd.Length == 0) continue;
                l.Add(new TermProfile { Name = name.Length > 0 ? name : cmd, Command = cmd, Folder = folder });
            }
            return l;
        }
    }
}
