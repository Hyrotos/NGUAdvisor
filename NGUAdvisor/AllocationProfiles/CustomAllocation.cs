using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NGUAdvisor.AllocationProfiles.Breakpoints;
using NGUAdvisor.AllocationProfiles.RebirthStuff;
using NGUAdvisor.Managers;
using SimpleJSON;
using static NGUAdvisor.Main;

namespace NGUAdvisor.AllocationProfiles
{
    public class CustomAllocation
    {
        private static Character _character => Main.Character;
        private BreakpointWrapper _wrapper;
        private readonly string _allocationPath;
        private readonly string _profileName;

        public bool IsAllocationRunning;

        // P0 (M2): refuse absurdly large profile files before parsing (guards the iterative JSON.Parse
        // from building an unbounded DOM out of a crafted/corrupt file). Real profiles are a few KB.
        private const long MaxProfileBytes = 8000000;

        public CustomAllocation(string profilesDir, string profile)
        {
            _allocationPath = Path.Combine(profilesDir, profile + ".json");
            _profileName = profile;
        }

        public void ReloadAllocation()
        {
            if (File.Exists(_allocationPath))
            {
                try
                {
                    var fi = new FileInfo(_allocationPath);
                    if (fi.Length > MaxProfileBytes)
                    {
                        Log($"Allocation profile '{_profileName}' is too large ({fi.Length:N0} bytes; limit {MaxProfileBytes:N0}). Refusing to load.");
                        _wrapper = new BreakpointWrapper();
                        return;
                    }

                    string raw = File.ReadAllText(_allocationPath);
                    _wrapper = new BreakpointWrapper(JSON.Parse(raw)["Breakpoints"]);

                    Log(_wrapper.BuildAllocationString(_profileName));
                    foreach (string warning in Managers.GearChain.ProfileWarnings(raw))
                        Log($"Profile advice: {warning}");

                    DoAllocations();
                }
                catch (Exception e)
                {
                    Log("Failed to load allocation file. Resave to reload");
                    Log(e.Message);
                    Log(e.StackTrace);
                    _wrapper = new BreakpointWrapper();
                }
            }
            else
            {
                var emptyAllocation = @"{
    ""Breakpoints"": {
      ""Magic"": [
        {
          ""Time"": 0,
          ""Priorities"": []
        }
      ],
      ""Energy"": [
        {
          ""Time"": 0,
          ""Priorities"": []
        }
      ],
    ""R3"": [
        {
          ""Time"": 0,
          ""Priorities"": []
        }
      ],
      ""Gear"": [
        {
          ""Time"": 0,
          ""ID"": []
        }
      ],
      ""Wandoos"": [
        {
          ""Time"": 0,
          ""OS"": 0
        }
      ],
      ""Beards"": [
        {
          ""Time"": 0,
          ""List"": []
        }
      ],
      ""Diggers"": [
        {
          ""Time"": 0,
          ""List"": []
        }
      ],
      ""NGUDiff"": [
        {
          ""Time"": 0,
          ""Diff"": 0
        }
      ],
      ""RebirthTime"": -1,
      ""Challenges"": []
    }
  }
        ";

                Log("Created empty allocation profile. Please update allocation.json");
                using (var writer = new StreamWriter(File.Open(_allocationPath, FileMode.CreateNew)))
                {
                    writer.WriteLine(emptyAllocation);
                    writer.Flush();
                }
            }
        }

        // Rebirth watermark: when rebirthTime jumps backwards, a rebirth happened (ours or manual).
        // Breakpoint sets only re-fire when the SELECTED breakpoint changes, so a single time-0
        // breakpoint would never re-apply after rebirth (diggers stayed off — user-reported). Resetting
        // every set forces one re-apply of the active breakpoint on the first pass of the new run.
        // STATIC, because the watermark belongs to the SESSION, not to this CustomAllocation instance.
        // Switching profiles constructs a new CustomAllocation, which reset the field to MaxValue, so the
        // `rt < _lastRebirthSeconds - 1` test below fired on the first tick after every switch and logged
        // "Rebirth detected" on a run that had not rebirthed — three times in 43 seconds in the
        // 2026-08-01 log, on a run the overlay reported as 2.3h old. Static keeps the real rebirth
        // watermark across switches; a genuine first load still fires, since MaxValue is the initial value.
        private static double _lastRebirthSeconds = double.MaxValue;

        public void DoAllocations()
        {
            if (!Settings.GlobalEnabled)
                return;

            if (!CompatibilityGate.ActionsAllowed)   // observe-only on an unrecognized game build (P0-3)
                return;

            if (IsAllocationRunning)
                return;

            try
            {
                double rt = _character.rebirthTime.totalseconds;
                if (rt < _lastRebirthSeconds - 1)
                {
                    _wrapper?.ResetAll();
                    Main.Log("Rebirth detected — re-applying all breakpoint timelines (diggers/beards/gear/OS/diff).");
                }
                _lastRebirthSeconds = rt;
            }
            catch { }

            var preventMagicAllocation = Settings.MoneyPitRunMode && Main.Character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier();

            try
            {
                long originalInput = Main.Character.energyMagicPanel.energyMagicInput;
                IsAllocationRunning = true;

                // P0 (allocation-tick blackout): each step is fault-isolated. A throw in one Swap()/
                // allocate used to abort every LATER step for this tick — energy/magic/gear/diggers
                // silently stopped while the HUD still read "active". Now a failing step is logged
                // (throttled per step) and the rest still run; the input-restore is itself a step, so
                // it happens even when an earlier step faulted.
                // WISHES AS A SINK: hand their holdings back BEFORE anything allocates, so the lanes
                // below see that resource and can cap out on it. The wish pass at the end of this tick
                // then takes only what is left.
                //
                // Gated on the SAME conditions as the wish step itself, deliberately: releasing here and
                // then skipping the allocation (preventMagicAllocation, or the toggle going off between
                // the two) would strand every wish at zero for the tick. The two must agree or the
                // release is a leak.
                //
                // In priority mode this does nothing here — WishManager.Allocate still releases at the
                // end, which is what makes that mode compound. See SavedSettings.WishSinkMode.
                RunStep("Wishes (release holdings to the lanes)", () =>
                {
                    if (Settings.ManageWishes && !preventMagicAllocation && Settings.WishSinkMode)
                        WishManager.ReleaseHoldings();
                });
                RunStep("NGU difficulty", () =>
                {
                    if (Settings.ManageNGUDiff && Main.Character.buttons.ngu.interactable)
                        _wrapper.ngus.Swap();
                });
                // ADVISOR OWNERSHIP (user-reported: every advisor reload re-applied the PROFILE's
                // wandoos breakpoint, and the OS change WIPES wandoos levels — hours of progress
                // gone). Systems the advisor owns are exempt from profile re-application; the
                // advisor's own logic re-establishes them from live state instead.
                RunStep("Wandoos OS", () =>
                {
                    if (Settings.ManageWandoos && !Settings.AdvisorWandoosOS && Main.Character.buttons.wandoos.interactable)
                        _wrapper.wandoos.Swap();
                });
                RunStep("Gear", () =>
                {
                    if (Settings.ManageGear && !Settings.AutoProfile && Main.Character.buttons.inventory.interactable)
                        _wrapper.gear.Swap();
                });
                RunStep("Energy", () =>
                {
                    if (Settings.ManageEnergy)
                        _wrapper.energy.Swap();
                });
                RunStep("Magic", () =>
                {
                    if (Settings.ManageMagic && !preventMagicAllocation)
                        _wrapper.magic.Swap();
                });
                RunStep("R3", () =>
                {
                    if (Settings.ManageR3)
                        _wrapper.r3.Swap();
                });
                RunStep("Wishes (share of remaining idle)", () =>
                {
                    if (Settings.ManageWishes && !preventMagicAllocation)
                    {
                        // Wishes are funded HERE, after the E/M/R3 swaps, and nowhere else. There
                        // used to be a pass BEFORE the swaps that opened with removeMostEnergy/
                        // removeMostMagic/removeAllRes3 and then applied the Wish % sliders — to a
                        // pool the reclaim had just refilled to nearly the whole cap, so "% of
                        // idle" behaved as "% of total, taken off the top" and the swaps divided a
                        // denominator silently pre-shrunk by it (user-reported; audit/38 §E4.1).
                        // Here the sliders mean what the UI label says: a share of what is
                        // genuinely still idle once every other system has taken its fill. They
                        // are also authoritative downward — 0% really allocates nothing, where the
                        // old spare pass drank all residue regardless of the sliders.
                        WishManager.Allocate();
                        WishManager.UpdateWishMenu();
                    }
                });
                RunStep("Consumables", () =>
                {
                    if (Settings.ManageConsumables)
                        _wrapper.consumables.Swap();
                });
                RunStep("Beards", () =>
                {
                    if (Settings.ManageBeards && !Settings.AdvisorBeards && Main.Character.buttons.beards.interactable)
                        _wrapper.beards.Swap();
                });
                RunStep("Diggers", () =>
                {
                    if (Settings.ManageDiggers && !Settings.AdvisorDiggers && Main.Character.buttons.diggers.interactable)
                    {
                        _wrapper.diggers.Swap();
                        DiggerManager.RecapDiggers();
                    }
                });

                RunStep("Restore energy/magic input", () =>
                {
                    Main.Character.energyMagicPanel.energyRequested.text = originalInput.ToString(CultureInfo.InvariantCulture);
                    Main.Character.energyMagicPanel.validateInput();
                });
            }
            catch (Exception e)
            {
                LogDebug($"Unexpected error in allocation loop: {e}");
            }
            finally
            {
                IsAllocationRunning = false;
                ReportReseatAfterGearSwap();
            }
        }

        // THE PRICE OF A GEAR SWAP, MEASURED RATHER THAN ESTIMATED.
        //
        // A swap zeroes committed energy/magic/R3 across eight controllers and then re-allocates via
        // nothing in particular — whichever of the two timers fires first. Until now that window was
        // invisible: the operator saw "Finished equipping gear" and no indication that eight systems had
        // just been producing nothing, or for how long. The advisor swaps several times an hour.
        //
        // ⚠ THIS IS A REPORT, NOT A GATE, AND DELIBERATELY SO. Twelve triggers reach ChangeGear and most
        // are time-critical — a titan window opening, a gold snipe, a quest acquire. A confirm prompt
        // here would trade an invisible half-second for a missed titan, which is a worse trade every
        // time. The swap is almost always right; only the telling was missing.
        //
        // It runs in the `finally` so a throw mid-allocation still closes the window rather than leaving
        // the stamp armed and reporting a false, ever-growing gap on the next pass.
        private static void ReportReseatAfterGearSwap()
        {
            try
            {
                var since = LoadoutManager.AllocationClearedAt;
                if (!since.HasValue) return;
                LoadoutManager.AllocationClearedAt = null;

                var secs = (DateTime.UtcNow - since.Value).TotalSeconds;
                if (secs < 0 || secs > 600) return;   // clock skew or a stamp that outlived its run

                var mode = "gear swap";
                try { if (LoadoutManager.LastSwap != null && !string.IsNullOrEmpty(LoadoutManager.LastSwap.Mode)) mode = LoadoutManager.LastSwap.Mode; } catch { }

                // Eight is Character.removeAllEnergyAndMagic()'s own fixed list, not a count taken here.
                var detail = $"{mode} · allocation cleared on 8 systems, re-seated after {secs:0.0}s";

                // A long window is a genuine warning: it means the re-seat waited on the 10s loop because
                // the 0.5s one is gated on not being mid-fight. A short one is just what a swap costs.
                if (secs >= 3.0) Activity.Warning("Gear swap paused 8 systems", detail);
                else Activity.Completed("Gear swap re-seated", detail);
            }
            catch (Exception e) { LogDebug($"Reseat report: {e.Message}"); }
        }

        // ---- per-step fault containment (audit P0: allocation-tick blackout) ----
        // Runs one allocation step isolated: a throw is caught, logged once, then throttled so a
        // persistently-failing step can't flood the log every tick, and the remaining steps still run.
        // Clears quietly on the first non-throwing run — it does NOT claim recovery (a step whose
        // feature is toggled off is a no-op that also "succeeds"). Mirrors Managers.AdvisorApply's
        // Fault pattern, kept local here to avoid disturbing that file.
        private sealed class StepFault { public int Count; public DateTime LastReport; }
        private static readonly Dictionary<string, StepFault> _stepFaults = new Dictionary<string, StepFault>();
        private static readonly TimeSpan StepReportEvery = TimeSpan.FromMinutes(10);

        private static void RunStep(string name, Action step)
        {
            try
            {
                step();
                _stepFaults.Remove(name);
            }
            catch (Exception e)
            {
                if (!_stepFaults.TryGetValue(name, out var f))
                {
                    _stepFaults[name] = new StepFault { Count = 1, LastReport = DateTime.UtcNow };
                    Log($"Allocation step '{name}' failed (continuing with the rest) - {e.GetType().Name}: {e.Message}");
                    LogDebug($"Allocation step '{name}' failed:\n{e}");
                    return;
                }
                f.Count++;
                if (DateTime.UtcNow - f.LastReport >= StepReportEvery)
                {
                    Log($"Allocation step '{name}' still failing - {f.Count} time(s) - {e.GetType().Name}: {e.Message}");
                    f.LastReport = DateTime.UtcNow;
                }
            }
        }

        // Does the loaded profile SCHEDULE the NGU level track itself?
        //
        // More than one NGUDiff breakpoint means the author wrote a timeline (e.g. 24hr-EarlyEvil's
        // Diff:0 -> Diff:1 at h22), which is a deliberate statement about when Evil NGUs start. A single
        // breakpoint is just a starting track, not a schedule. LevelPlanner.TickNguTrack uses this to
        // decide whether it may drive the track dynamically: both of them write
        // c.settings.nguLevelTrack, and with no arbitration the last writer each tick won.
        public bool ProfileOwnsNguTrack => (_wrapper != null && _wrapper.ngus != null) && _wrapper.ngus.Length > 1;

        // The run's planned length: the smallest positive time-based rebirth target (rebirth triggers
        // when the run crosses it). -1 means NO TIME DEADLINE IS KNOWN — which covers a profile that
        // disarmed rebirth (RebirthTime -1) but ALSO a profile whose rebirth is armed on something
        // other than the clock (a NUMBER/BOSSES entry written without a "Time" key parses to
        // RebirthTime 0, and 0 is skipped below).
        //
        // ⚠ SO -1 HERE DOES NOT MEAN "NO REBIRTH". It means "no seconds figure to give you", and the
        // horizon/deadline/countdown callers all want exactly that. Anything asking whether a rebirth
        // is COMING must ask RebirthIsArmed() + RebirthSchedule instead; BloodPlanner asked it here
        // and read eleven shipped Number profiles as having no rebirth at all.
        //
        // This also never consulted NORB — an older version of this comment claimed it did. NORB is a
        // live challenge state, not a profile field, and it is tested by the callers that care.
        public double NextRebirthTargetSeconds()
        {
            if (_wrapper == null) return -1;
            double best = -1;
            foreach (var rb in _wrapper.rebirth)
                if (rb.RebirthTime > 0 && (best < 0 || rb.RebirthTime < best))
                    best = rb.RebirthTime;
            return best;
        }

        // Does this profile schedule a rebirth AT ALL — on the clock, on the number, on a boss count,
        // or on a muffin cycle? The per-entry rule is RebirthSchedule.EntryArmed, which is the same
        // `>= 0` test DoRebirth just below already filters on and TimeRebirth.RebirthAvailable already
        // opens with, so this cannot drift from what the rebirth path actually does.
        //
        // This is a profile question only. AutoRebirth, NORB and money-pit run mode are live state and
        // belong to RebirthSchedule.Current, which composes them with this.
        public bool RebirthIsArmed()
        {
            if (_wrapper == null) return false;
            foreach (var rb in _wrapper.rebirth)
                if (RebirthSchedule.EntryArmed(rb.RebirthTime))
                    return true;
            return false;
        }

        public bool DoRebirth()
        {
            if (_wrapper == null)
                return false;

            var rbs = _wrapper.rebirth.Where(x => x.RebirthTime >= 0.0);
            if (!rbs.Any())
                return false;

            if (rbs.Any(x => x.RebirthTime <= _character.rebirthTime.totalseconds))
                rbs = rbs.Where(x => x.RebirthTime <= _character.rebirthTime.totalseconds);

            var rb = rbs.AllMaxBy(x => x.RebirthTime).First();

            if (rb.RebirthAvailable(out _))
            {
                if (_character.bossController.isFighting || _character.bossController.nukeBoss)
                {
                    Log("Delaying rebirth while boss fight is in progress");
                    return true;
                }
            }
            else
            {
                return false;
            }

            if (rb.DoRebirth())
            {
                _wrapper.energy.Reset();
                _wrapper.magic.Reset();
                _wrapper.r3.Reset();
                _wrapper.gear.Reset();
                _wrapper.beards.Reset();
                _wrapper.diggers.Reset();
                _wrapper.wandoos.Reset();
                _wrapper.ngus.Reset();
                _wrapper.consumables.Reset();
                // Stats just reset — the gold/CBlock furthest-zone ratchet must not survive the run.
                Main.ResetFurthestZone();
            }

            return true;
        }

        public void CastBloodSpells()
        {
            if (!Settings.CastBloodSpells)
                return;

            var needCast = _wrapper.rebirth.Length == 0;
            foreach (TimeRebirth rb in _wrapper.rebirth)
            {
                if (rb.RebirthTime - _character.rebirthTime.totalseconds >= 30 * 60)
                {
                    needCast = true;
                    break;
                }
            }

            if (!needCast)
                return;

            BloodMagicManager.guffB.Cast();
            BloodMagicManager.guffA.Cast();
            // When the Blood planner auto is on, it owns Iron Pill timing (breakpoint-optimal cast);
            // the threshold path here would fire early and waste the step. Rebirth force-casts are
            // unaffected (blood is lost on rebirth, so casting then is always right).
            if (!Settings.AdvisorBlood)
                BloodMagicManager.ironPill.Cast();
        }

        public static double ParseTime(JSONNode timeNode)
        {
            var time = 0;

            if (timeNode.IsObject)
            {
                foreach (var N in timeNode)
                {
                    if (N.Value.IsNumber)
                    {
                        switch (N.Key.ToLower())
                        {
                            case "h":
                                time += 60 * 60 * N.Value.AsInt;
                                break;
                            case "m":
                                time += 60 * N.Value.AsInt;
                                break;
                            default:
                                time += N.Value.AsInt;
                                break;
                        }
                    }
                }
            }

            if (timeNode.IsNumber)
                time = timeNode.AsInt;

            return time;
        }
    }

    public class BreakpointWrapper
    {
        // Clear every set's "already swapped" memory so the active breakpoint re-applies once.
        public void ResetAll()
        {
            energy?.Reset();
            magic?.Reset();
            r3?.Reset();
            gear?.Reset();
            diggers?.Reset();
            beards?.Reset();
            wandoos?.Reset();
            ngus?.Reset();
            consumables?.Reset();
        }

        public TimeRebirth[] rebirth = new TimeRebirth[0];
        public EnergyBreakpoints energy = new EnergyBreakpoints();
        public MagicBreakpoints magic = new MagicBreakpoints();
        public R3Breakpoints r3 = new R3Breakpoints();
        public GearBreakpoints gear = new GearBreakpoints();
        public DiggerBreakpoints diggers = new DiggerBreakpoints();
        public BeardBreakpoints beards;
        public WandoosBreakpoints wandoos = new WandoosBreakpoints();
        public NGUDiffBreakpoints ngus = new NGUDiffBreakpoints();
        public ConsumablesBreakpoints consumables = new ConsumablesBreakpoints();

        // The Challenges array has no breakpoint class and therefore no Length — which is exactly why it
        // had no line on this banner and no key in ProfileSections until audit/59 §A went looking for
        // one. A profile carried twenty entries and no surface in the product acknowledged them.
        public int challengesAuthored;
        public int challengesRecognised;

        // `:percent` tokens as AUTHORED, per pool, deduplicated. Deliberately not a count: the notice
        // has to quote the operator's own text back or it is just another claim they cannot check.
        public readonly List<string> percentEnergy = new List<string>();
        public readonly List<string> percentMagic = new List<string>();
        public readonly List<string> percentR3 = new List<string>();
        public readonly List<string> percentDropped = new List<string>();

        public BreakpointWrapper(JSONNode parsed)
        {
            var rb = parsed["Rebirth"];
            var rbtime = parsed["RebirthTime"];

            if (rb == null)
            {
                if (rbtime != null)
                {
                    var newRebirth = TimeRebirth.CreateRebirth(CustomAllocation.ParseTime(rbtime), 0.0, "time");
                    Array.Resize(ref rebirth, 1);
                    rebirth[0] = newRebirth;
                }
            }
            else
            {
                var rbs = new List<TimeRebirth>();
                foreach (var bp in rb.Children)
                {
                    if (bp["Type"] == null)
                        continue;

                    var type = bp["Type"].Value.ToUpper();
                    if (type != "TIME" && bp["Target"] == null)
                        continue;

                    var target = type == "TIME" ? 0.0 : bp["Target"].AsDouble;
                    var time = 0.0;
                    if (bp["Time"] != null)
                        time = CustomAllocation.ParseTime(bp["Time"]);

                    var newRebirth = TimeRebirth.CreateRebirth(time, target, type);
                    if (newRebirth != null)
                        rbs.Add(newRebirth);
                }
                rebirth = rbs.ToArray();
            }

            // ⚠ BOTH counts are kept, because they are different facts. ParseChallenges silently drops a
            // token with no '-' and a token whose ordinal will not parse, and logs (once) for a token
            // naming no known challenge — so "20 authored" is what the operator wrote and
            // BaseRebirth.ChallengeCount is what the rotation can engage. Reporting only the first would
            // repeat the class of lie this whole pass exists to remove; reporting only the second hides
            // that entries were discarded.
            var challengeTokens = parsed["Challenges"].AsArray.Children.Select(bp => bp.Value.ToUpper()).ToArray();
            BaseRebirth.ParseChallenges(challengeTokens);
            challengesAuthored = challengeTokens.Length;
            challengesRecognised = BaseRebirth.ChallengeCount;

            // The `:percent` census, taken from the RAW timeline text before parsing folds it away.
            // CapPercent is private and the parsed lane cannot say what token produced it, so a
            // load-time notice that names the operator's own tokens has to read the JSON.
            ScanPercentTokens(parsed["Energy"], percentEnergy);
            ScanPercentTokens(parsed["Magic"], percentMagic);
            ScanPercentTokens(parsed["R3"], percentR3);

            energy = new EnergyBreakpoints(parsed["Energy"]);
            magic = new MagicBreakpoints(parsed["Magic"]);
            r3 = new R3Breakpoints(parsed["R3"]);
            gear = new GearBreakpoints(parsed["Gear"]);
            diggers = new DiggerBreakpoints(parsed["Diggers"]);
            beards = new BeardBreakpoints(parsed["Beards"], diggers);
            wandoos = new WandoosBreakpoints(parsed["Wandoos"]);
            ngus = new NGUDiffBreakpoints(parsed["NGUDiff"]);
            consumables = new ConsumablesBreakpoints(parsed["Consumables"]);
        }

        public BreakpointWrapper()
        {
            beards = new BeardBreakpoints(diggers);
        }

        // THE LOAD BANNER — and what it used to leave out.
        //
        // Operators are documented as reading these counts to confirm a profile actually loaded, so the
        // numbers carry more weight than a count of parsed array entries deserves. Measured on two live
        // saves 2026-08-18 (audit/59 §A): this banner printed "1 Consumable Breakpoints" on a game with
        // ManageConsumables:false and "1 Rebirth Breakpoints" on one with AutoRebirth:false. Both counts
        // were correct and both read as a statement that the section was running. Neither was.
        //
        // The annotation comes from ProfileSections.Evaluate rather than a second set of guards written
        // here, ON PURPOSE: the rules live scattered across CustomAllocation's own per-step gates,
        // ChallengeOverlay's substitution and three independent Advisor* toggles, and the companion's
        // Profiles page already draws that verdict. A private copy in this method would be free to drift
        // from the page — and a banner that disagrees with the panel is worse than one that says nothing.
        //
        // If this is ever reduced back to bare counts, the banner resumes telling an operator that a
        // structurally dead section is live, which is the failure the whole line exists to prevent.
        public string BuildAllocationString(string profileName)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Loaded Custom Allocation from profile '{profileName}'");

            var notes = SectionNotes();
            builder.AppendLine($"{energy.Length} Energy Breakpoints{Note(notes, "energy")}");
            builder.AppendLine($"{magic.Length} Magic Breakpoints{Note(notes, "magic")}");
            builder.AppendLine($"{r3.Length} R3 Breakpoints{Note(notes, "r3")}");
            builder.AppendLine($"{gear.Length} Gear Breakpoints{Note(notes, "gear")}");
            builder.AppendLine($"{beards.Length} Beard Breakpoints{Note(notes, "beards")}");
            builder.AppendLine($"{diggers.Length} Digger Breakpoints{Note(notes, "diggers")}");
            builder.AppendLine($"{wandoos.Length} Wandoos Breakpoints{Note(notes, "wandoos")}");
            builder.AppendLine($"{ngus.Length} NGU Difficulty Breakpoints{Note(notes, "ngudiff")}");
            builder.AppendLine($"{consumables.Length} Consumable Breakpoints{Note(notes, "consumables")}");
            builder.AppendLine($"{ChallengeCountText()}{Note(notes, "challenges")}");
            if (rebirth?.Length > 0)
                builder.AppendLine($"{rebirth.Length} Rebirth Breakpoints{Note(notes, "rebirth")}");
            else
                builder.AppendLine($"Rebirth Disabled.");

            // The master switch annotates every section identically, so it is said ONCE instead of
            // eleven times — SectionNotes returns null in that case for exactly this reason.
            if (notes == null && Main.Settings != null && !Main.Settings.GlobalEnabled)
                builder.AppendLine("⚠ Automation is off — none of the above is running.");

            AppendPercentNotice(builder);

            return builder.ToString();
        }

        // "20 Challenges (18 in the rotation)". The gap is never cosmetic: a token ParseChallenges
        // dropped cannot be engaged, and the operator's only other clue is one "Incorrect challenge
        // name" line buried in the log.
        private string ChallengeCountText()
        {
            if (challengesAuthored == challengesRecognised)
                return $"{challengesAuthored} Challenges";
            return $"{challengesAuthored} Challenges ({challengesRecognised} in the rotation — the rest " +
                   "name no known challenge, carry no '-', or have an ordinal that will not parse)";
        }

        // Per-section verdicts keyed by ProfileSections' stable ids, or NULL when there is nothing
        // useful to say per line — no settings yet (first load can precede them), or the master switch
        // is off, in which case every section would carry the same sentence.
        private Dictionary<string, SectionVerdict> SectionNotes()
        {
            try
            {
                var st = Main.Settings;
                if (st == null || !st.GlobalEnabled) return null;

                bool armed = false;
                if (rebirth != null)
                    foreach (var rb in rebirth)
                        if (rb != null && RebirthSchedule.EntryArmed(rb.RebirthTime)) { armed = true; break; }

                var inputs = new SectionInputs
                {
                    GlobalEnabled = st.GlobalEnabled,
                    AutoProfile = st.AutoProfile,
                    ManageEnergy = st.ManageEnergy, ManageMagic = st.ManageMagic, ManageR3 = st.ManageR3,
                    ManageGear = st.ManageGear, AdvisorGearRefresh = st.AdvisorGearRefresh,
                    ManageWandoos = st.ManageWandoos, AdvisorWandoosOS = st.AdvisorWandoosOS,
                    ManageDiggers = st.ManageDiggers, AdvisorDiggers = st.AdvisorDiggers,
                    ManageBeards = st.ManageBeards, AdvisorBeards = st.AdvisorBeards,
                    ManageNGUDiff = st.ManageNGUDiff, ManageConsumables = st.ManageConsumables,
                    AutoRebirth = st.AutoRebirth,
                    // Read from THIS wrapper's own rebirth array rather than Main.Profile: the banner is
                    // built inside ReloadAllocation, and going back out through Main.Profile would report
                    // on whichever wrapper is currently published, which may not be this one yet.
                    RebirthArmed = armed,
                    ProfileHasChallenges = challengesRecognised > 0,
                    AdvisorChallenges = st.AdvisorChallenges,
                    // Load time, not tick time — the Evil ch.5 window is a running-state fact and the
                    // banner must not claim it is open. False reads as "your timeline owns the track",
                    // which is the state a profile load establishes.
                    NguTrackOverrideActive = false
                };

                var map = new Dictionary<string, SectionVerdict>();
                foreach (var v in ProfileSections.Evaluate(inputs))
                    map[v.Key] = v;
                return map;
            }
            catch (Exception e)
            {
                // A banner is not worth a failed load. Bare counts are what shipped for two years.
                LogDebug($"Profile banner: section verdicts unavailable ({e.Message})");
                return null;
            }
        }

        // Silence when the section runs as authored — an annotation on every line is an annotation
        // nobody reads, and the whole point is that the exceptions stand out.
        private static string Note(Dictionary<string, SectionVerdict> notes, string key)
        {
            SectionVerdict v;
            if (notes == null || !notes.TryGetValue(key, out v)) return "";
            switch (v.Driver)
            {
                case SectionDriver.Profile: return "";
                // Not "NOT YOUR LIST": one advisor-driven section (diggers with Auto Profile off) is
                // still confined to the operator's own list, and a blanket denial would contradict the
                // sentence it introduces. The reason says which it is.
                case SectionDriver.Advisor: return "  ← THE ADVISOR IS DRIVING THIS: " + v.Reason;
                default:                    return "  ← NOT RUNNING: " + v.Reason;
            }
        }

        // `:percent` — WHERE IT IS READ AND WHERE IT IS NOT.
        //
        // Once per load, because that is when the operator is looking at the profile. The WORDING is
        // the product here — the one thing this notice must not do is claim `:percent` is dead
        // everywhere, because it is live on R3 and live on the legacy kill-switch path — so the
        // sentences live in ProfileSections.PercentNotice, which is Unity-free and linked into the
        // test project. This method only supplies the two facts it cannot know: the live state of the
        // constraint allocator, and the operator's own tokens.
        private void AppendPercentNotice(StringBuilder builder)
        {
            // Energy and Magic share a verdict because they share a path — both go through
            // ConstraintLayerBridge when the allocator is on and through the identical prioCount share
            // loop when it is off. Splitting them would imply a difference that does not exist.
            var em = new List<string>(percentEnergy);
            foreach (var t in percentMagic) if (!em.Contains(t)) em.Add(t);

            foreach (var line in ProfileSections.PercentNotice(
                         Managers.ConstraintLayerBridge.NewPathEnabled, em, percentR3, percentDropped))
                builder.AppendLine(line);
        }

        // Mirrors ResourceBreakpoint.ParseBreakpointArray's own reading of a token rather than
        // approximating it (:142-158): split on ':', and if the right half will not int.TryParse the
        // parser skips the entire token. That is why `dropped` is a separate list — same punctuation,
        // completely different outcome, and the second one costs the operator a lane.
        private void ScanPercentTokens(JSONNode timeline, List<string> withPercent)
        {
            try
            {
                if (timeline == null) return;
                foreach (var bp in timeline.AsArray.Children)
                {
                    var prios = bp["Priorities"];
                    if (prios == null) continue;
                    foreach (var t in prios.AsArray.Children)
                    {
                        var tok = t.Value;
                        if (string.IsNullOrEmpty(tok) || tok.IndexOf(':') < 0) continue;
                        var split = tok.Split(':');
                        int pct;
                        if (split.Length < 2 || !int.TryParse(split[1], out pct))
                        {
                            AddOnce(percentDropped, tok);
                            continue;
                        }
                        AddOnce(withPercent, tok);
                    }
                }
            }
            catch { }   // a census is never worth failing a load over
        }

        private static void AddOnce(List<string> list, string token)
        {
            if (!list.Contains(token)) list.Add(token);
        }
    }
}
