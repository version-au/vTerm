using System.Collections.Generic;
using vTerm.Core;
using vTerm.Ui;
using Xunit;

namespace vTerm.Tests
{
    public class StartRequestTests
    {
        [Fact]
        public void NothingAsked()
        {
            Assert.Null(StartRequest.Parse(new string[0]));
            Assert.Null(StartRequest.Parse(null));
        }

        [Fact]
        public void AProfileInAFolder()
        {
            StartRequest r = StartRequest.Parse(new[] { "--profile", "PowerShell 7", "--dir", @"C:\Projects" });
            Assert.Equal(StartRequest.Kinds.Tab, r.Kind);
            Assert.Equal("PowerShell 7", r.Profile);
            Assert.Equal(@"C:\Projects", r.Folder);
            Assert.Null(r.Remote);
        }

        [Fact]
        public void ACommandWithATitle()
        {
            StartRequest r = StartRequest.Parse(new[] { "--run", "ping 127.0.0.1", "--title", "Ping" });
            Assert.Equal("ping 127.0.0.1", r.Command);
            Assert.Equal("Ping", r.Title);
        }

        [Fact]
        public void OpenShellFromVShell()
        {
            // Komodo / Proxmox's Open shell: --run is the command on the server, not a local one
            StartRequest r = StartRequest.Parse(new[] { "--host", "10.0.0.5", "--name", "pve", "--user", "root", "--run", "docker exec -it x sh" });
            Assert.Equal(StartRequest.Kinds.Ssh, r.Kind);
            Assert.Equal("10.0.0.5", r.Host);
            Assert.Equal("pve", r.Name);
            Assert.Equal("root", r.User);
            Assert.Equal("docker exec -it x sh", r.Remote);
            Assert.Null(r.Command);
        }

        [Fact]
        public void ANameWithNoAddress()
        {
            StartRequest r = StartRequest.Parse(new[] { "--host", "", "--name", "Unknown" });
            Assert.Equal(StartRequest.Kinds.Ssh, r.Kind);
            Assert.Equal("", r.Host);
        }

        [Fact]
        public void HandOffRoundTrips()
        {
            string[] args = { "--ssh", "root@pve:2222", "--new-window" };
            StartRequest r = StartRequest.FromHandOff(StartRequest.ToHandOff(args));
            Assert.Equal("root@pve:2222", r.Ssh);
            Assert.True(r.NewWindow);
        }

        [Fact]
        public void AStartWithNothingIsANewWindow()
        {
            Assert.True(StartRequest.FromHandOff(StartRequest.ToHandOff(new string[0])).NewWindow);
        }
    }

    public class ConnectionTests
    {
        [Theory]
        [InlineData("root@pve", "root", "pve", 22)]
        [InlineData("pve:2222", "", "pve", 2222)]
        [InlineData("ssh admin@10.0.0.5:2200", "admin", "10.0.0.5", 2200)]
        public void QuickAddresses(string text, string user, string host, int port)
        {
            TermConnection c = TermConnection.Parse(text);
            Assert.Equal(user, c.User);
            Assert.Equal(host, c.Host);
            Assert.Equal(port, c.Port);
        }

        [Theory]
        [InlineData("")]
        [InlineData("two words")]
        public void NotAnAddress(string text)
        {
            Assert.Null(TermConnection.Parse(text));
        }

        [Fact]
        public void FindByHostThenName()
        {
            var all = new List<TermConnection>
            {
                new TermConnection { Name = "nuc01", Host = "192.168.2.11" },
                new TermConnection { Name = "pve", Host = "192.168.2.10" }
            };
            Assert.Equal("pve", TermSsh.Find(all, "192.168.2.10").Name);
            Assert.Equal("nuc01", TermSsh.Find(all, "NUC01").Name);
            Assert.Null(TermSsh.Find(all, "nas"));
        }
    }

    public class ProfileTests
    {
        [Fact]
        public void TextRoundTrips()
        {
            var l = new List<TermProfile>
            {
                new TermProfile { Name = "PowerShell", Command = "powershell.exe -NoLogo" },
                new TermProfile { Name = "Projects", Command = "cmd.exe", Folder = @"C:\Projects" }
            };
            List<TermProfile> back = TermProfile.FromText(TermProfile.ToText(l));
            Assert.Equal(2, back.Count);
            Assert.Equal("powershell.exe -NoLogo", back[0].Command);
            Assert.Equal(@"C:\Projects", back[1].Folder);
        }

        [Fact]
        public void CommentsBlankLinesAndNamelessLines()
        {
            List<TermProfile> l = TermProfile.FromText("# mine\r\n\r\nwsl.exe ~\r\nTop | htop\r\n");
            Assert.Equal(2, l.Count);
            Assert.Equal("wsl.exe ~", l[0].Name);       // no name: the command is its name
            Assert.Equal("htop", l[1].Command);
        }
    }
}
