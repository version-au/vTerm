using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace vTerm.Core
{
    /// <summary>
    /// vTerm's settings (%APPDATA%\vTerm\settings.json, or VTERM_DATA_DIR): the terminal (profiles, text, colours, saved connections),
    /// the window, and updates. The first time vTerm starts it brings vShell's terminal settings and connections over (VShellImport).
    /// </summary>
    public sealed class AppSettings : IUpdateSettings
    {
        // ---- the terminal ----
        /// <summary>Profiles (null = the ones this PC has: PowerShell, Command Prompt, WSL ...).</summary>
        public List<TermProfile> Profiles { get; set; }
        /// <summary>The profile a new tab starts with (its name; empty = the first).</summary>
        public string DefaultProfile { get; set; } = "";
        /// <summary>Text: font (empty = Cascadia Mono, then Consolas) and size.</summary>
        public string Font { get; set; } = "";
        public int FontSize { get; set; } = 13;
        /// <summary>Colours: "Theme" (from the vShell theme) or a named scheme (TerminalView.Schemes).</summary>
        public string Colors { get; set; } = "Theme";
        /// <summary>The text area is see-through (the window's glass / theme colour shows); off = the theme colour, solid.</summary>
        public bool SeeThrough { get; set; } = true;
        /// <summary>Saved SSH connections (shared by every window).</summary>
        public List<TermConnection> Connections { get; set; } = new List<TermConnection>();
        /// <summary>The side panel (connections / files) is open.</summary>
        public bool PanelOpen { get; set; } = true;
        /// <summary>A window with shells still running asks before it closes.</summary>
        public bool AskBeforeClosing { get; set; } = true;

        // ---- the window ----
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        /// <summary>Kept on top of other windows.</summary>
        public bool Topmost { get; set; }
        /// <summary>See-through at vShell's opacity, like its bars (the wallpaper behind); off = solid.</summary>
        public bool ThemeOpacity { get; set; } = true;

        // ---- vShell ----
        /// <summary>vShell's terminal settings have been brought over (once, at the first start).</summary>
        public bool ImportedFromVShell { get; set; }

        // ---- updates (Options > Updates; see Updater / UpdateCenter) ----
        public bool UpdateAuto { get; set; } = true;
        public bool UpdatePre { get; set; }
        public DateTime UpdateLastCheck { get; set; } = DateTime.MinValue;
        public string UpdateSkip { get; set; } = "";

        static string FilePath { get { return Path.Combine(Log.Folder, "settings.json"); } }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch (Exception ex) { Log.Write("Settings: " + ex.Message); }
            var fresh = new AppSettings();
            fresh.Normalize();
            return fresh;
        }

        /// <summary>Values from an old or hand-edited file put right.</summary>
        public void Normalize()
        {
            if (Profiles != null) Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Command));
            if (Profiles != null && Profiles.Count == 0) Profiles = null;
            if (DefaultProfile == null) DefaultProfile = "";
            if (Font == null) Font = "";
            FontSize = Math.Max(8, Math.Min(32, FontSize));
            if (string.IsNullOrWhiteSpace(Colors)) Colors = "Theme";
            if (Connections == null) Connections = new List<TermConnection>();
            Connections.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Host));
            foreach (TermConnection c in Connections) c.Normalize();
            if (UpdateSkip == null) UpdateSkip = "";
        }

        public void Save()
        {
            if (_soon != null) _soon.Stop();
            try
            {
                Directory.CreateDirectory(Log.Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Write("Settings save: " + ex.Message); }
        }

        DispatcherTimer _soon;

        /// <summary>Save in a moment (several changes in a row are saved once). On the UI thread.</summary>
        public void SaveSoon()
        {
            if (_soon == null)
            {
                _soon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _soon.Tick += (o, e) => Save();
            }
            _soon.Stop();
            _soon.Start();
        }

        /// <summary>The profiles in use (the saved ones, or this PC's).</summary>
        public List<TermProfile> ProfilesInUse()
        {
            if (Profiles != null && Profiles.Count > 0) return Profiles;
            if (_defaults == null) _defaults = TermProfile.Defaults();
            return _defaults;
        }
        static List<TermProfile> _defaults;

        /// <summary>The profile a new tab starts with.</summary>
        public TermProfile StartProfile()
        {
            List<TermProfile> all = ProfilesInUse();
            return all.Find(x => string.Equals(x.Name, DefaultProfile, StringComparison.OrdinalIgnoreCase))
                   ?? (all.Count > 0 ? all[0] : new TermProfile { Name = "PowerShell", Command = "powershell.exe -NoLogo" });
        }
    }
}
