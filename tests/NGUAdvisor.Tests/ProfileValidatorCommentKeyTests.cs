using System.IO;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class ProfileValidatorCommentKeyTests
    {
        // Comment keys are dropped by the model on load, so two of them in one object lose nothing.
        [Theory]
        [InlineData("{ \"Comment\": \"a\", \"Comment\": \"b\", \"Breakpoints\": {} }")]
        [InlineData("{ \"Breakpoints\": { \"Energy\": [ { \"Time\": 0, \"PriorityPercentExample2\": [], \"PriorityPercentExample2\": [], \"Priorities\": [] } ] } }")]
        public void A_repeated_comment_key_is_not_a_structural_error(string json)
            => Assert.True(ProfileValidator.Validate(json).Ok, ProfileValidator.Validate(json).Message);

        // Only one of two data keys survives the parse, and nothing says which was meant.
        [Theory]
        [InlineData("{ \"Breakpoints\": {}, \"Breakpoints\": {} }", "Breakpoints")]
        [InlineData("{ \"Breakpoints\": { \"Energy\": [ { \"Time\": 0, \"Priorities\": [], \"Priorities\": [] } ] } }", "Priorities")]
        public void A_repeated_data_key_still_is(string json, string key)
        {
            var r = ProfileValidator.Validate(json);
            Assert.False(r.Ok);
            Assert.Contains("Duplicate property name '" + key + "'", r.Message);
        }

        // The profile is now checked when it LOADS, so every profile the product ships has to pass --
        // otherwise a stock profile would announce itself as "not valid JSON" on every load.
        [Fact]
        public void Every_shipped_profile_passes_the_load_time_check()
        {
            string root = Path.GetDirectoryName(typeof(ProfileValidatorCommentKeyTests).Assembly.Location);
            while (root != null && !Directory.Exists(Path.Combine(root, "NGUAdvisor", "SampleProfiles")))
                root = Path.GetDirectoryName(root);
            Assert.NotNull(root);

            var files = new[] { "SampleProfiles", "Presets" }
                .SelectMany(d => Directory.GetFiles(Path.Combine(root, "NGUAdvisor", d), "*.json", SearchOption.AllDirectories))
                .ToList();
            Assert.True(files.Count > 50, files.Count.ToString());

            var bad = files.Select(f => new { f, r = ProfileValidator.Validate(File.ReadAllText(f)) })
                           .Where(x => !x.r.Ok)
                           .Select(x => Path.GetFileName(x.f) + " line " + x.r.Line + ": " + x.r.Message)
                           .ToList();
            Assert.True(bad.Count == 0, string.Join(" | ", bad));
        }
    }
}
