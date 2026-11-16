using System;
using System.IO;
using vTerm.Core;
using Xunit;

namespace vTerm.Tests
{
    public class SettingsFileTests
    {
        [Fact]
        public void An_unreadable_settings_file_is_kept_and_the_save_before_is_used()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vTerm-settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string before = Environment.GetEnvironmentVariable("VTERM_DATA_DIR");
            try
            {
                Environment.SetEnvironmentVariable("VTERM_DATA_DIR", dir);
                vSuite.App.Init("vTerm", "VTERM", "https://example.invalid");
                string file = Path.Combine(dir, "settings.json");
                var s = AppSettings.Load();
                Assert.Null(AppSettings.LoadNote);                       // no file yet: just new settings
                s.ImportedFromVShell = true;
                s.Connections.Add(new TermConnection { Name = "one", Host = "10.0.0.1" }); s.Save();
                s = AppSettings.Load(); s.Connections.Add(new TermConnection { Name = "two", Host = "10.0.0.2" }); s.Save();
                Assert.Equal(2, AppSettings.Load().Connections.Count);

                File.WriteAllText(file, "{ \"Connections\": [ {");       // cut short
                var r = AppSettings.Load();
                Assert.Single(r.Connections);                            // the save before - not "no connections"
                Assert.NotNull(AppSettings.LoadNote);
                Assert.Single(Directory.GetFiles(dir, "settings.broken-*.json"));
                r.Save();                                                // the broken file doesn't go over the good .bak
                Assert.Contains("10.0.0.1", File.ReadAllText(file + ".bak"));

                File.WriteAllText(file, "");                             // empty, and no usable .bak
                File.WriteAllText(file + ".bak", "nonsense");
                var fresh = AppSettings.Load();
                Assert.Empty(fresh.Connections);
                Assert.True(fresh.ImportedFromVShell);                   // not a first start: vShell's old list isn't brought over it
                Assert.NotEmpty(Directory.GetFiles(dir, "settings.broken-*.json"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VTERM_DATA_DIR", before);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
