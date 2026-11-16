using vTerm.Core;
using Xunit;

namespace vTerm.Tests
{
    public class AskpassTests
    {
        [Fact]
        public void A_saved_password_only_answers_this_servers_password_prompt()
        {
            Assert.True(TermSsh.SecretFits("root@pve1's password: ", "root@pve1", "Password"));
            Assert.True(TermSsh.SecretFits("(root@PVE1) Password: ", "root@pve1", "Password"));
            Assert.True(TermSsh.SecretFits("Password: ", "root@pve1", "Password"));                       // names nobody
            Assert.False(TermSsh.SecretFits("jump@bastion's password: ", "root@pve1", "Password"));       // the jump host asking
            Assert.False(TermSsh.SecretFits("Enter passphrase for key 'C:\\Users\\me\\.ssh\\id_ed25519': ", "root@pve1", "Password"));
        }

        [Fact]
        public void A_keys_passphrase_is_never_given_as_a_password()
        {
            Assert.True(TermSsh.SecretFits("Enter passphrase for key 'C:\\Users\\me\\.ssh\\id_ed25519': ", "root@pve1", "Key"));
            Assert.False(TermSsh.SecretFits("root@pve1's password: ", "root@pve1", "Key"));               // the key was turned down
        }

        [Fact]
        public void A_saved_secret_is_tried_once()
        {
            var c = new TermConnection { Host = "pve1", User = "root", Auth = "Password", Secret = "x" };
            Assert.Contains("NumberOfPasswordPrompts=1", TermSsh.CommandLine(c, null));
            c.Secret = "";
            Assert.DoesNotContain("NumberOfPasswordPrompts", TermSsh.CommandLine(c, null));               // typed by hand: ssh's usual three
        }
    }
}
