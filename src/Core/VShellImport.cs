using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace vTerm.Core
{
    /// <summary>
    /// Brings vShell's terminal over: its profiles, text and colour settings, saved SSH connections (passwords stay encrypted for your
    /// Windows account, so they carry straight over) and the profile its Terminal module started with. Done once, at vTerm's very first
    /// start (AppSettings.ImportedFromVShell) - never again after that. vShell's own settings aren't touched.
    /// </summary>
    public static class VShellImport
    {
        public sealed class Result
        {
            public bool Found;
            public int Connections, ConnectionsAdded;
            public bool Settings;
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Read vShell's settings.json into s. everything = the terminal settings too; else only the connections.</summary>
        public static Result Into(AppSettings s, bool everything)
        {
            var r = new Result();
            string file = VShellThemeSource.SettingsFile;
            if (!File.Exists(file)) return r;
            try
            {
                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();
                using (JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                {
                    JsonElement root = doc.RootElement;
                    r.Found = true;
                    JsonElement v;
                    if (root.TryGetProperty("TermConnections", out v) && v.ValueKind == JsonValueKind.Array)
                    {
                        var list = JsonSerializer.Deserialize<List<TermConnection>>(v.GetRawText(), Json) ?? new List<TermConnection>();
                        foreach (TermConnection c in list)
                        {
                            if (c == null || string.IsNullOrWhiteSpace(c.Host)) continue;
                            c.Normalize();
                            r.Connections++;
                            if (s.Connections.Exists(x => x.Id == c.Id || (string.Equals(x.Host, c.Host, StringComparison.OrdinalIgnoreCase) &&
                                                                            string.Equals(x.User, c.User, StringComparison.OrdinalIgnoreCase) && x.Port == c.Port))) continue;
                            s.Connections.Add(c);
                            r.ConnectionsAdded++;
                        }
                    }
                    if (everything)
                    {
                        if (root.TryGetProperty("TermProfiles", out v) && v.ValueKind == JsonValueKind.Array)
                        {
                            var profiles = JsonSerializer.Deserialize<List<TermProfile>>(v.GetRawText(), Json);
                            if (profiles != null && profiles.Count > 0) s.Profiles = profiles;
                        }
                        if (root.TryGetProperty("TermFont", out v) && v.ValueKind == JsonValueKind.String) s.Font = v.GetString() ?? "";
                        int size;
                        if (root.TryGetProperty("TermFontSize", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out size)) s.FontSize = size;
                        if (root.TryGetProperty("TermColors", out v) && v.ValueKind == JsonValueKind.String) s.Colors = v.GetString() ?? "Theme";
                        if (root.TryGetProperty("TermSeeThrough", out v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) s.SeeThrough = v.GetBoolean();
                        if (root.TryGetProperty("TermPanelOpen", out v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) s.PanelOpen = v.GetBoolean();
                        string profile = FirstTerminalProfile(root);
                        if (!string.IsNullOrEmpty(profile)) s.DefaultProfile = profile;
                        r.Settings = true;
                    }
                }
                s.Normalize();
                Log.Write("vShell's terminal brought over: " + r.ConnectionsAdded + " of " + r.Connections + " connections" + (r.Settings ? ", with its settings" : ""));
            }
            catch (Exception ex) { Log.Write("vShell import: " + ex.Message); }
            return r;
        }

        /// <summary>The profile the first Terminal module (on any bar or widget) starts with - anywhere in the file.</summary>
        static string FirstTerminalProfile(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                JsonElement type, profile;
                if (e.TryGetProperty("Type", out type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Terminal" &&
                    e.TryGetProperty("TermProfile", out profile) && profile.ValueKind == JsonValueKind.String)
                    return profile.GetString();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    string found = FirstTerminalProfile(p.Value);
                    if (found != null) return found;
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    string found = FirstTerminalProfile(x);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
