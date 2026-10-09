using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class HostPathTests
    {
        private const string Prefix = "/home/u/.local/share/Steam/steamapps/compatdata/1147690/pfx";

        [Fact]
        public void Drive_C_is_the_prefixs_drive_c()
            => Assert.Equal(Prefix + "/drive_c/users/steamuser/AppData/LocalLow/NGUAdvisor/state-export.txt",
                            HostPath.For(@"C:\users\steamuser\AppData\LocalLow\NGUAdvisor\state-export.txt", Prefix));

        [Theory]
        [InlineData(@"Z:\home\u\git\x.txt", "/home/u/git/x.txt")]                // Wine maps Z: to the Unix root
        [InlineData(@"z:/home/u/x.txt", "/home/u/x.txt")]
        [InlineData(@"D:\Games\x.txt", Prefix + "/dosdevices/d:/Games/x.txt")]   // any other drive is a dosdevices link
        [InlineData(@"c:\a.txt", Prefix + "/drive_c/a.txt")]
        public void Other_drives_resolve_too(string windows, string expected)
            => Assert.Equal(expected, HostPath.For(windows, Prefix));

        // On real Windows there is no prefix, and the path must come back untouched.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Without_a_prefix_the_path_is_unchanged(string prefix)
            => Assert.Equal(@"C:\users\me\x.txt", HostPath.For(@"C:\users\me\x.txt", prefix));

        [Theory]
        [InlineData("/already/unix")]
        [InlineData(@"relative\path.txt")]
        [InlineData(@"\\server\share\x.txt")]
        [InlineData("C:")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_that_is_not_a_drive_path_is_left_alone(string path)
            => Assert.Equal(path, HostPath.For(path, Prefix));

        [Fact]
        public void The_prefix_comes_from_wine_first_then_from_proton()
        {
            Assert.Equal("/w", HostPath.PrefixFrom("/w/", "/steam/compat/1"));
            Assert.Equal("/steam/compat/1/pfx", HostPath.PrefixFrom(null, "/steam/compat/1/"));
            Assert.Equal("/steam/compat/1/pfx", HostPath.PrefixFrom("", "/steam/compat/1"));
            Assert.Null(HostPath.PrefixFrom(null, null));
        }
    }
}
