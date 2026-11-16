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

        /// <summary>Set by Load when settings.json was there but couldn't be read: what was done about it, for the log and a notice.</summary>
        public static string LoadNote;
        bool _noSave, _keepBak;

        static AppSettings Read(string path)
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("the file is empty");
            return JsonSerializer.Deserialize<AppSettings>(text) ?? throw new InvalidDataException("the file holds nothing");
        }

        /// <summary>
        /// The settings; new ones when there's no file yet. A file that's there but can't be read is never taken for "no settings"
        /// (that lost every saved connection): one that's only busy is tried again; a broken one is kept as
        /// settings.broken-&lt;time&gt;.json and the save before it (settings.json.bak) is used; and when not even a copy can be kept,
        /// nothing is saved over it this run.
        /// </summary>
        public static AppSettings Load()
        {
            bool there = false;
            string why = null;
            for (int tries = 0; tries < 5; tries++)
            {
                try
                {
                    there = File.Exists(FilePath);
                    if (!there) break;
                    AppSettings s = Read(FilePath);
                    s.Normalize();
                    return s;
                }
                catch (IOException ex) when (tries < 4 && !(ex is InvalidDataException)) { why = ex.Message; System.Threading.Thread.Sleep(200); }
                catch (Exception ex) { why = ex.Message; break; }
            }
            AppSettings use = null;
            if (there)
            {
                Log.Write("Settings: settings.json couldn't be read - " + why);
                bool kept = false;
                try
                {
                    string aside = Path.Combine(Log.Folder, "settings.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Copy(FilePath, aside, true);
                    kept = true;
                    Log.Write("Settings: it's kept as " + aside);
                }
                catch (Exception ex) { Log.Write("Settings: couldn't keep a copy of it - " + ex.Message); }
                try
                {
                    if (File.Exists(FilePath + ".bak")) { use = Read(FilePath + ".bak"); Log.Write("Settings: using settings.json.bak (the save before the last one)"); }
                }
                catch (Exception ex) { Log.Write("Settings: settings.json.bak can't be read either - " + ex.Message); }
                if (use == null) use = new AppSettings { ImportedFromVShell = true };      // not a first start: nothing is brought over again
                use._noSave = !kept;      // no copy of the unreadable file: leave it alone
                use._keepBak = true;      // the first save mustn't put the broken file over the good .bak
                LoadNote = kept ? "settings.json couldn't be read; a copy is kept beside it (settings.broken-…json)."
                                : "settings.json couldn't be read or copied, so nothing is saved over it until " + App.Name + " starts again.";
            }
            if (use == null) use = new AppSettings();
            use.Normalize();
            return use;
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
            if (_noSave) return;
            try
            {
                Directory.CreateDirectory(Log.Folder);
                // the file as it is (read or written by us, so it's good) stays as settings.json.bak
                if (_keepBak) _keepBak = false;
                else try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true); } catch { }
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
