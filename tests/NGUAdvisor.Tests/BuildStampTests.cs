using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // WHICH BUILD IS RUNNING, and whether the two halves agree.
    //
    // The advisor DLL and the companion UI ship from TWO Release-only deploy targets in TWO projects,
    // and only one of those projects is in NGUAdvisor.sln. Building the advisor deploys nothing to
    // wwwroot; hot reload swaps the payload and never touches it either. Found the hard way
    // 2026-08-06: a new setting was wired end to end, deployed, reloaded — and the toggle was simply
    // absent, because the deployed UI was SIX DAYS and SEVEN COMMITS stale. Every one of those
    // commits was correct; none had ever reached the running app.
    public class BuildStampTests
    {
        // NGUAdvisor.csproj: AssemblyName = "NGUAdvisor.r" + yyMMddHHmmss, already baked in per build
        // so Mono's Assembly.Load(byte[]) cannot dedupe a hot reload.
        [Fact]
        public void A_stamped_assembly_name_parses_to_its_build_time()
        {
            var t = BuildStamp.Parse("NGUAdvisor.r260806164329");
            Assert.NotNull(t);
            Assert.Equal(new DateTime(2026, 8, 6, 16, 43, 29), t.Value);
        }

        [Theory]
        [InlineData("NGUAdvisor")]                 // unstamped — the pre-scheme name
        [InlineData("NGUAdvisor.dll")]
        [InlineData("NGUAdvisor.r")]               // truncated
        [InlineData("NGUAdvisor.r2608061643")]     // too short
        [InlineData("NGUAdvisor.r26080616432900")] // too long
        [InlineData("NGUAdvisor.rZZZZZZZZZZZZ")]   // not a date
        [InlineData("")]
        [InlineData(null)]
        public void An_unstamped_or_malformed_name_is_unknown_not_a_guess(string name)
            => Assert.Null(BuildStamp.Parse(name));

        // ── the gate ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_ui_older_than_the_tolerance_is_stale()
        {
            var advisor = new DateTime(2026, 8, 6, 16, 43, 0);
            var ui = advisor - TimeSpan.FromDays(6);          // the case that actually happened
            Assert.True(BuildStamp.IsUiStale(advisor, ui));
            var msg = BuildStamp.StaleMessage(advisor, ui);
            Assert.Contains("STALE", msg);
            // Names the fix, not just the fault — and the fix is the ONE command that ships both halves.
            // It used to name `dotnet build NGUAdvisorCompanion.csproj -c Release`, which FAILS with
            // MSB3021 whenever this message can be seen: the companion is running and holds a write lock
            // on its own exe, and the copy dies after wwwroot has already been written (a partial deploy).
            // Remedy advice that does not work is worse than none, so this pins the working one.
            Assert.Contains("deploy.ps1", msg);
            Assert.DoesNotContain("dotnet build", msg);
        }

        // ⚠ TOLERANCE, NOT EQUALITY. The two are built by separate commands and the companion stamp is
        // a file mtime while the advisor's is a build stamp — they never match exactly. Firing on any
        // difference would make this noise, and noise is what gets ignored.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(23)]
        public void Ordinary_same_session_drift_is_not_stale(int hoursOlder)
        {
            var advisor = new DateTime(2026, 8, 6, 16, 43, 0);
            Assert.False(BuildStamp.IsUiStale(advisor, advisor - TimeSpan.FromHours(hoursOlder)));
            Assert.Null(BuildStamp.StaleMessage(advisor, advisor - TimeSpan.FromHours(hoursOlder)));
        }

        // A UI NEWER than the advisor is not this gate's business — that is a stale DLL, which hot
        // reload already makes obvious, and flagging it here would misname the fix.
        [Fact]
        public void A_newer_ui_is_not_reported_as_stale()
        {
            var advisor = new DateTime(2026, 8, 6, 16, 43, 0);
            Assert.False(BuildStamp.IsUiStale(advisor, advisor + TimeSpan.FromDays(3)));
        }

        // ⚠ UNKNOWN IS NOT STALE. Guessing from a missing value would cry wolf on every hand-renamed
        // DLL and on developer builds that never deploy the companion at all.
        [Fact]
        public void Unknown_on_either_side_is_never_stale()
        {
            var t = new DateTime(2026, 8, 6, 16, 43, 0);
            Assert.False(BuildStamp.IsUiStale(null, t));
            Assert.False(BuildStamp.IsUiStale(t, null));
            Assert.False(BuildStamp.IsUiStale(null, null));
            Assert.Null(BuildStamp.StaleMessage(null, t));
        }

        [Fact]
        public void Format_is_stable_and_says_unknown_rather_than_blank()
        {
            Assert.Equal("2026-08-06 16:43", BuildStamp.Format(new DateTime(2026, 8, 6, 16, 43, 29)));
            Assert.Equal("unknown", BuildStamp.Format(null));
        }

        // The stamp round-trips through its own formats, so a future change to one has to change both.
        [Fact]
        public void The_prefix_matches_what_the_csproj_bakes_in()
        {
            Assert.Equal("NGUAdvisor.r", BuildStamp.Prefix);
            Assert.NotNull(BuildStamp.Parse(BuildStamp.Prefix + "260806164329"));
        }

        // ── the bootstrap ─────────────────────────────────────────────────────────────────────────
        //
        // A DIFFERENT question from the pair above. NGUAdvisorBootstrap.dll had no deploy target at
        // all until 2026-08-06 (audit/42 §1 artifact 4, §9 rank 2) and was in sync only because the
        // project has one commit in its history. The deploy gap is now closed by DeployBootstrap plus
        // build/deploy.ps1's hash check. What NEITHER of those can see is that the running game keeps
        // the bootstrap it injected: F5 calls Boot.Reload() on the already-injected copy, so a newly
        // deployed bootstrap sits on disk doing nothing until NGU Idle restarts, and the symptom is
        // hot reload behaving like the old build — the hardest thing here to attribute, because the
        // reload path is how every other change is tested.

        // Exactly the line Boot.Init writes, once per injection.
        private const string RealLog =
            "2026-08-06 16:39:27 payload loaded and started: NGUAdvisor.r260806163918 v1.1.9714.29979 (990208 bytes)\r\n" +
            "2026-08-06 16:43:38 reload requested\r\n" +
            "2026-08-06 16:43:38 old payload unloaded\r\n" +
            "2026-08-06 16:56:04 bootstrap up; payload path: C:\\Users\\Admin\\NGUInjector Updates\\NGU\\injector\\NGUAdvisor.dll\r\n" +
            "2026-08-06 16:56:04 payload loaded and started: NGUAdvisor.r260806164329 v1.1.9714.30104 (990720 bytes)\r\n" +
            "2026-08-06 18:42:43 reload requested\r\n" +
            "2026-08-06 18:42:43 old payload unloaded\r\n" +
            "2026-08-06 18:42:43 payload loaded and started: NGUAdvisor.r260806184213 v1.1.9714.33666 (1005056 bytes)\r\n";

        // The last INJECTION, not the last line and not the last reload. Reloads are the noise this
        // has to see past: three of them bracket the one line that matters in the sample above.
        [Fact]
        public void The_injection_time_is_the_last_bootstrap_up_line_not_the_last_activity()
        {
            var t = BuildStamp.ParseInjectionTime(RealLog);
            Assert.NotNull(t);
            Assert.Equal(new DateTime(2026, 8, 6, 16, 56, 4), t.Value);
        }

        // The log spans every session since the machine was set up, so "last" has to mean last.
        [Fact]
        public void A_later_session_supersedes_an_earlier_one()
        {
            var log = "2026-08-01 09:00:00 bootstrap up; payload path: x\r\n" +
                      "2026-08-06 16:56:04 bootstrap up; payload path: x\r\n";
            Assert.Equal(new DateTime(2026, 8, 6, 16, 56, 4), BuildStamp.ParseInjectionTime(log).Value);
        }

        // UiBridge reads only the TAIL of the file, so the first line can be a fragment. It must be
        // rejected rather than mis-parsed — and a fragment of a "bootstrap up" line is the case.
        [Fact]
        public void A_truncated_leading_line_is_rejected_not_guessed()
        {
            var log = "ap; payload path: C:\\somewhere\\NGUAdvisor.dll\r\n" +
                      "2026-08-06 16:56:04 bootstrap up; payload path: x\r\n";
            Assert.Equal(new DateTime(2026, 8, 6, 16, 56, 4), BuildStamp.ParseInjectionTime(log).Value);

            // ...and with nothing else in the tail, the answer is unknown, which is NOT stale.
            Assert.Null(BuildStamp.ParseInjectionTime("ap; payload path: C:\\x\\NGUAdvisor.dll\r\n"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("2026-08-06 16:43:38 reload requested\r\n")]           // reloads are not injections
        [InlineData("2026-08-06 16:56:04 payload loaded and started: x\r\n")]
        [InlineData("not a log at all")]
        [InlineData("ZZZZ-ZZ-ZZ ZZ:ZZ:ZZ bootstrap up; payload path: x")]  // marker, unparseable time
        public void No_injection_line_means_unknown(string log)
            => Assert.Null(BuildStamp.ParseInjectionTime(log));

        // The case this exists for: someone ran build\deploy.ps1, the bootstrap changed, and the game
        // has been up the whole time. Nothing else in the system reports this.
        [Fact]
        public void A_bootstrap_built_after_this_session_injected_is_stale()
        {
            var injected = new DateTime(2026, 8, 6, 16, 56, 4);
            var onDisk = new DateTime(2026, 8, 6, 19, 30, 0);
            Assert.True(BuildStamp.IsBootstrapStale(injected, onDisk));

            var msg = BuildStamp.BootstrapStaleMessage(injected, onDisk);
            Assert.Contains("STALE", msg);
            Assert.Contains("NGUAdvisorBootstrap.dll", msg);
            // ⚠ THE FIX IS A RESTART AND EXPLICITLY NOT A RELOAD. Naming the reload would send someone
            // round the exact loop that produced the unattributable symptom in the first place.
            Assert.Contains("RESTART", msg);
            Assert.DoesNotContain("deploy.ps1", msg);
        }

        // The ordinary case, and the live one at the time of writing: the deployed bootstrap dates
        // from 2026-07-07 and every session since has injected it.
        [Fact]
        public void A_bootstrap_older_than_the_session_is_the_healthy_case()
        {
            var injected = new DateTime(2026, 8, 6, 16, 56, 4);
            var onDisk = new DateTime(2026, 7, 7, 14, 29, 7);
            Assert.False(BuildStamp.IsBootstrapStale(injected, onDisk));
            Assert.Null(BuildStamp.BootstrapStaleMessage(injected, onDisk));
        }

        // Boot.Log writes whole seconds while a file mtime is exact, so a bootstrap deployed and
        // injected in the same act can read as a few hundred ms "newer" than its own injection.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(59)]
        public void Second_resolution_slop_is_not_stale(int secondsNewer)
        {
            var injected = new DateTime(2026, 8, 6, 16, 56, 4);
            Assert.False(BuildStamp.IsBootstrapStale(injected, injected.AddSeconds(secondsNewer)));
        }

        // ⚠ UNKNOWN IS NOT STALE — same law as IsUiStale. A hand-injected session writes no
        // "bootstrap up" line at all, and a machine with no injector-path.txt has no file to stat.
        [Fact]
        public void Unknown_on_either_bootstrap_side_is_never_stale()
        {
            var t = new DateTime(2026, 8, 6, 16, 56, 4);
            Assert.False(BuildStamp.IsBootstrapStale(null, t));
            Assert.False(BuildStamp.IsBootstrapStale(t, null));
            Assert.False(BuildStamp.IsBootstrapStale(null, null));
            Assert.Null(BuildStamp.BootstrapStaleMessage(null, t));
        }

        // The two gates are independent and must not be confused: the UI gate has a 24h tolerance
        // because it compares a build stamp against a file mtime produced by a different command; the
        // bootstrap gate compares two things that are meant to line up, so its grace is a minute.
        [Fact]
        public void The_two_gates_do_not_share_a_tolerance()
        {
            Assert.Equal(TimeSpan.FromHours(24), BuildStamp.Tolerance);
            Assert.Equal(TimeSpan.FromMinutes(1), BuildStamp.BootstrapGrace);
            Assert.True(BuildStamp.BootstrapGrace < BuildStamp.Tolerance);
        }

        // The file name is hardcoded by everything that injects it (NGUAdvisorLauncher/Program.cs and
        // both Run NGU Advisor*.bat pass "-a .\injector\NGUAdvisorBootstrap.dll"), which is exactly
        // why the bootstrap CANNOT carry a per-build stamped name the way the advisor does.
        [Fact]
        public void The_bootstrap_file_name_is_the_one_smi_is_told_to_inject()
            => Assert.Equal("NGUAdvisorBootstrap.dll", BuildStamp.BootstrapFile);

        // ── §3.2: THE GATE'S OWN BLIND SPOT — the wrong copy ──────────────────────────────────────
        // The stat used to be hard-coded to <injectorDir>\companion\wwwroot\index.html on the
        // assumption that the companion is always the auto-launched one. NGUAdvisorCompanion.csproj
        // tells a developer to run it from its own bin\ instead, and MainForm maps
        // AppContext.BaseDirectory\wwwroot — a DIFFERENT FILE. Measured 2026-08-06: the bin\Debug copy
        // was 565,706 bytes / 2026-07-31 against the deployed 607,179 / 2026-08-06, and the gate read
        // the deployed one and reported the pair healthy while six days sat on screen.

        const string Deployed = @"C:\NGU\injector\companion";
        const string DevBin = @"C:\repo\NGUAdvisorCompanion\bin\Debug\net8.0-windows";

        [Fact]
        public void The_wwwroot_measured_is_the_one_beside_the_running_exe()
        {
            Assert.Equal(System.IO.Path.Combine(DevBin, "wwwroot", "index.html"),
                         BuildStamp.IndexHtmlIn(DevBin));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_unresolvable_companion_directory_stays_unknown(string dir)
            => Assert.Null(BuildStamp.IndexHtmlIn(dir));

        [Fact]
        public void A_companion_run_from_its_own_bin_is_a_dev_copy()
        {
            Assert.True(BuildStamp.IsDevCopy(BuildStamp.IndexHtmlIn(DevBin),
                                             BuildStamp.IndexHtmlIn(Deployed)));
        }

        // The auto-launched case must behave EXACTLY as before: same path, so nothing is decorated,
        // nothing new is logged, and the operator sees no change at all.
        [Fact]
        public void The_auto_launched_companion_is_not_a_dev_copy()
        {
            Assert.False(BuildStamp.IsDevCopy(BuildStamp.IndexHtmlIn(Deployed),
                                              BuildStamp.IndexHtmlIn(Deployed)));
            // Same directory, spelled differently — a path comparison, not a string comparison.
            // Spelled in the HOST's path syntax: a backslash is an ordinary filename character on
            // Linux, so the Windows spelling is not two spellings of one directory there and the
            // trailing "\." is never resolved. The rule under test is the same on both.
            bool windows = System.IO.Path.DirectorySeparatorChar == '\\';
            string dotted = windows ? @"C:\NGU\injector\companion\." : "/NGU/injector/companion/.";
            string recased = windows ? @"C:\NGU\INJECTOR\companion" : "/NGU/INJECTOR/companion";
            Assert.False(BuildStamp.IsDevCopy(BuildStamp.IndexHtmlIn(dotted),
                                              BuildStamp.IndexHtmlIn(recased)));
        }

        // ⚠ SAME RULE AS THE STALE GATE: unknown is not a finding. A companion whose path could not be
        // read (Mono can refuse MainModule across a bitness boundary) must not become a warning.
        [Fact]
        public void An_unknown_path_on_either_side_is_never_a_dev_copy()
        {
            Assert.False(BuildStamp.IsDevCopy(null, BuildStamp.IndexHtmlIn(Deployed)));
            Assert.False(BuildStamp.IsDevCopy(BuildStamp.IndexHtmlIn(DevBin), null));
            Assert.False(BuildStamp.IsDevCopy(null, null));
        }

        [Fact]
        public void The_footer_says_which_copy_the_timestamp_belongs_to()
        {
            var t = new DateTime(2026, 7, 31, 12, 4, 0);
            Assert.Equal("2026-07-31 12:04", BuildStamp.FormatCompanion(t, false));
            Assert.Equal("2026-07-31 12:04 (dev)", BuildStamp.FormatCompanion(t, true));
            Assert.Equal("unknown", BuildStamp.FormatCompanion(null, false));
        }

        // The dev copy's fix is NOT deploy.ps1 — that ships to <injectorDir>\companion\ and would not
        // touch a bin\ copy at all. Remedy advice that cannot work is worse than none; the deployed
        // message's own test pins the same rule from the other side.
        [Fact]
        public void A_stale_dev_copy_names_a_fix_that_can_actually_work()
        {
            var advisor = new DateTime(2026, 8, 6, 16, 43, 0);
            var ui = new DateTime(2026, 7, 31, 12, 4, 0);          // the copy measured on 2026-08-06
            var msg = BuildStamp.DevCopyMessage(advisor, ui, DevBin, Deployed);
            Assert.Contains("STALE", msg);
            Assert.Contains(DevBin, msg);
            Assert.Contains(Deployed, msg);
            Assert.DoesNotContain("deploy.ps1", msg);
        }

        // A dev copy that is CURRENT is still worth one line: it is invisible otherwise, and it changes
        // what the fix would be if it later went stale. It just must not claim to be stale.
        [Fact]
        public void A_current_dev_copy_is_reported_but_not_called_stale()
        {
            var advisor = new DateTime(2026, 8, 6, 16, 43, 0);
            var msg = BuildStamp.DevCopyMessage(advisor, advisor - TimeSpan.FromHours(2), DevBin, Deployed);
            Assert.NotNull(msg);
            Assert.DoesNotContain("STALE", msg);
        }

        [Fact]
        public void No_served_directory_means_nothing_to_say()
            => Assert.Null(BuildStamp.DevCopyMessage(DateTime.Now, DateTime.Now, null, Deployed));

        [Fact]
        public void The_companion_process_name_matches_what_the_csproj_builds()
            => Assert.Equal("NGUAdvisorCompanion.exe", BuildStamp.CompanionExe);
    }
}
