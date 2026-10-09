using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    // Option B "challenge overlays" — first slice. The user's profile stays the base; while a
    // challenge is active (and Settings.AdvisorChallenges is on) the overlay transforms behavior on
    // top of it. This slice: phase-aware GEAR OBJECTIVE rotation (push = bosses falling -> Adventure
    // gear; growth = boss wall -> NGUs gear) driven through ApplyGearRefresh, NOEC gear suppression,
    // the completions-aware block model, and the ACTION FEED every overlay decision is written to
    // (the Challenges tab renders it; it doubles as the validation log for growing templates toward
    // Option A). Dead-system allocation strips (NONGU/NOTM/NOAUG) are the next slice.
    public static class ChallengeOverlay
    {
        // Newest-first, capped; the Challenges tab renders this verbatim.
        public static readonly List<string> Feed = new List<string>();

        // Consulted by ApplyGearRefresh in place of the profile's objective while set.
        public static string GearObjectiveOverride { get; private set; }

        // WHICH of the two things the field above currently is: the auto profile's per-segment gear
        // (true), or a challenge push/growth rotation (false). One field carries both, and the
        // difference matters to anything explaining itself to the user.
        //
        // It has to be recorded HERE, at the moment of assignment. A reader cannot re-derive it from
        // ChallengeDetector, because this only updates on the 30s advisor tick while the companion
        // snapshot reads it every second — for up to 30s after a challenge ends the field still holds
        // the rotation value while the detector already says "no challenge", and a re-derived answer
        // would confidently mislabel it as segment gear (and claim the auto profile chose it, even
        // with AutoProfile off).
        public static bool GearObjectiveIsSegment { get; private set; }

        private static void SetGearObjective(string objective, bool isSegment)
        {
            GearObjectiveOverride = objective;
            GearObjectiveIsSegment = objective != null && isSegment;
        }
        public static string Phase { get; private set; } = "";

        private static int _lastBoss = -1;
        private static DateTime _lastBossKill = DateTime.MinValue;
        private static string _activeChallenge;

        // Number-growth projection: how many bosses would 30 minutes of number growth cover?
        // Sampled from nextAttackMulti (number-derived, immune to our own gear swaps) over rolling
        // ~10-minute windows; linear projection; boss cost k = 2.0 Normal / 1.5 Evil / ~1.25 Sadistic.
        public static double Bosses30 { get; private set; } = double.NaN;
        private static double _numSample;
        private static DateTime _numSampleAt = DateTime.MinValue;

        private static void UpdateNumberProjection(Character c)
        {
            try
            {
                // First 10 minutes of a run: the multiplier is tiny and volatile — projections
                // whipsawed segments (log-audit: 2.1 -> 0.3 boss/30m within a minute of rebirth).
                // Phase falls back to kill-recency until the run has substance.
                double runSec = 0;
                try { runSec = c.rebirthTime.totalseconds; } catch { }
                if (runSec < 600)
                {
                    Bosses30 = double.NaN;
                    _numSampleAt = DateTime.MinValue;
                    return;
                }

                double cur = c.nextAttackMulti;
                if (cur <= 0) return;

                // First sample, or a rebirth reset (the multiplier collapsed): start a fresh window.
                if (_numSampleAt == DateTime.MinValue || cur < _numSample * 0.5)
                {
                    _numSample = cur;
                    _numSampleAt = DateTime.UtcNow;
                    Bosses30 = double.NaN;   // warming up
                    return;
                }

                double elapsed = (DateTime.UtcNow - _numSampleAt).TotalSeconds;
                if (elapsed < 120) return;   // too short to trust

                double k;
                try
                {
                    k = c.settings.rebirthDifficulty == difficulty.sadistic ? 1.25
                        : c.settings.rebirthDifficulty == difficulty.evil ? 1.5 : 2.0;
                }
                catch { k = 2.0; }

                double rate = (cur - _numSample) / elapsed;
                double projected = cur + rate * 1800.0;
                Bosses30 = projected > cur ? Math.Log(projected / cur) / Math.Log(k) : 0;

                // Roll the window forward every ~10 minutes so the estimate tracks the run.
                if (elapsed >= 600)
                {
                    _numSample = cur;
                    _numSampleAt = DateTime.UtcNow;
                }
            }
            catch (Exception e) { Main.LogDebug($"Number projection: {e.Message}"); }
        }

        public static void Record(string action, string reason) => Record("ALLOC", action, reason);

        // Category-tagged feed entries (Advisors > FEED filters on the [CAT] prefix).
        // Actions are sentence-cased here (capitalization scheme: feed lines are reports).
        public static void Record(string category, string action, string reason)
        {
            try
            {
                if (!string.IsNullOrEmpty(action) && char.IsLower(action[0]))
                    action = char.ToUpperInvariant(action[0]) + action.Substring(1);
                Feed.Insert(0, $"[{category}] {DateTime.Now:HH:mm} {action} — {reason}");
                if (Feed.Count > 50) Feed.RemoveAt(Feed.Count - 1);
                Main.Log($"Overlay: {action} ({reason})");
            }
            catch { }
        }

        // R11: the outer whole-Tick catch was removed so AdvisorApply's RunStep("Challenge overlay", ...)
        // owns the bounded fault report. The narrow boss-read / detector probes keep their own catches.
        public static void Tick()
        {
            var c = Main.Character;
            if (c == null) return;

            var s = Main.Settings;
            bool overlays = s != null && s.AdvisorChallenges;
            bool auto = s != null && s.AutoProfile;
            if (!overlays && !auto)
            {
                SetGearObjective(null, false);
                Phase = "";
                SetSegment("");        // audit/59 §C: this reset used to sit BELOW this return
                _lastGenKey.Clear();   // narrate afresh when the auto profile comes back
                return;
            }

            // Phase (user rule, post-challenge recovery): PUSH while the number is still cheap
            // power — i.e. projected number growth over 30 minutes covers MULTIPLE bosses (each
            // boss costs x2 attack on Normal, x1.5 Evil) — or while bosses are actively falling.
            // GROWTH once 30 minutes of number wouldn't move the wall much: pivot to the
            // long-horizon systems (wandoos/NGU).
            int boss = 0;
            try { boss = c.bossID; } catch { }
            if (boss != _lastBoss)
            {
                _lastBoss = boss;
                _lastBossKill = DateTime.UtcNow;
            }
            bool recentKill = (DateTime.UtcNow - _lastBossKill).TotalMinutes < 5;
            UpdateNumberProjection(c);
            bool numberCheap = !double.IsNaN(Bosses30) && Bosses30 >= 2.0;
            Phase = (recentKill || numberCheap) ? "push" : "growth";

            // D1: run segment (the guide's 24h rebirth shape), auto-profile only.
            if (auto) UpdateSegment(c);
            else SetSegment("");

            if (!overlays)
            {
                // Auto profile only: no challenge transforms; gear follows the segment
                // outside challenges (a challenge with overlays off = profile rules).
                SetGearObjective(ChallengeDetector.Current() == null ? SegmentGear() : null, true);
                return;
            }

            string cur = ChallengeDetector.Current();
            if (cur != _activeChallenge)
            {
                if (cur != null) Record("SEGMENT", $"challenge start: {cur}", "overlay active");
                else if (_activeChallenge != null) Record("SEGMENT", $"challenge complete: {_activeChallenge}", "back to profile rules");
                _activeChallenge = cur;
                SetGearObjective(null, false);
                // Fresh narration state per challenge (else a lingering template flag would
                // suppress the new challenge's "template applied" feed entry). Generation key
                // too: the auto profile re-announces when it resumes after the challenge.
                _lastActive.Clear();
                _templateOn.Clear();
                _fallbackOn.Clear();
                _lastGenKey.Clear();
            }
            if (cur == null)
            {
                SetGearObjective(auto ? SegmentGear() : null, true);
                return;
            }

            // NOEC: no equipment exists — gear rotation stands down entirely.
            if (cur == "NOEC")
            {
                if (GearObjectiveOverride != null)
                {
                    SetGearObjective(null, false);
                    Record("GEAR", "gear rotation off", "NOEC: no equipment in this challenge");
                }
                return;
            }

            bool pushing = Phase == "push";
            string want = pushing ? "Adventure" : "NGUs";
            if (want != GearObjectiveOverride)
            {
                SetGearObjective(want, false);
                Record("GEAR", $"gear → {want}", pushing ? $"push phase: boss {boss} within reach" : "growth phase: at the boss wall");
            }
        }

        // ---- Slice 2: allocation strips. The per-system challenge guards already exist (AugmentBP/
        // BestAug refuse NOAUG via AugmentMath.AugmentMenuUnlocked, TimeMachineBP refuses NOTM at
        // TimeMachineBP.cs:9's Unlocked(), and NGU lanes are refused by the CONSTRAINT LAYER —
        // ConstraintLayerBridge.cs:300 -> FeasibilityPass.NguLane on character.NGU.disabled),
        // and PerformSwap filters IsValid() BEFORE computing shares — so redistribution is free. The
        // hole this closes: a profile breakpoint whose priorities are ALL dead in the active challenge
        // used to bail out and leave the resource idle for the whole challenge. Now the overlay
        // (1) narrates the filter in the feed, and (2) injects a generic fallback priority list in the
        // all-dead case — itself IsValid-filtered, so whatever the challenge kills drops here too. ----
        //
        // ⚠ THIS COMMENT USED TO READ "NGUBP dies with the disabled NGU button". THAT WAS FALSE, and it
        // mattered, because the NONGU template below was designed around it (39 §F1; amendment 32 §4).
        // [DECOMP] ButtonShower.cs:329-346 is the COMPLETE gate on buttons.ngu.interactable and it reads
        // character.settings.nguOn — the player's "On Vacation" toggle — and nothing else; :199 is that
        // file's only inChallenge term and it belongs to No Augs. So NGUBP.Unlocked() (NGUBP.cs:8-14)
        // stays TRUE for the whole NGU Challenge and the lanes survive the IsValid() filter at :99.
        //
        // What actually refuses them is one layer later and keyed on the EFFECT FLAG, not the challenge:
        // [DECOMP] Rebirth.cs:521-523 — engageNGUChallenge() sets nguChallenge.inChallenge = true and,
        // two lines later, character.NGU.disabled = true. That second flag is what the ~30
        // `if (character.NGU.disabled) return 1.0;` sites in AllNGUController.cs read, and it is what
        // FeasibilityPass.NguLane refuses on. ONE predicate therefore covers both the Troll challenge
        // and the NGU Challenge — which is why searching this tree for `nguChallenge` finds no gate and
        // the gate exists anyway. Cleared at NGUChallengeController.cs:146 / :230, NOT at rebirth.
        private static bool _lscSwordOn;   // narration flag for the LSC sword-first injection
        private static readonly Dictionary<ResourceType, int> _lastActive = new Dictionary<ResourceType, int>();
        private static readonly Dictionary<ResourceType, List<ResourceBreakpoint>> _fallbackParsed = new Dictionary<ResourceType, List<ResourceBreakpoint>>();
        private static readonly Dictionary<ResourceType, bool> _templateOn = new Dictionary<ResourceType, bool>();
        private static readonly Dictionary<ResourceType, bool> _fallbackOn = new Dictionary<ResourceType, bool>();  // narrate fallback injection ONCE per state change, not per tick
        private static readonly Dictionary<string, List<ResourceBreakpoint>> _templateParsed = new Dictionary<string, List<ResourceBreakpoint>>();

        // Slice 3: re-weighting templates for the strip challenges. Stripping alone leaves the
        // SURVIVORS with the dead systems' shares — a 70%-NGU profile in NONGU floods basic training,
        // which is survival, not strategy. When a strip challenge has gutted the profile list (half
        // or more entries inactive), the challenge's template takes over the shape instead. Untouched
        // outside these three challenges; a lightly-degraded list stays the user's own.
        //
        // ⚠ THE NONGU TEMPLATE IS NOT DEAD CODE, AND A DELETE DECISION TAKEN ON THAT PREMISE IS VOID.
        // 39 §F1 inferred "the list is never gutted, so the template can never fire" from the fact that
        // NGUBP.IsValid() stays true during the challenge. That inference does not follow. `gutted` is
        // `valid.Count * 2 <= origCount` (below) and `valid` is filtered by IsValid() =
        // correctResourceType && unlocked && !targetMet (LaneTargets.cs:177-178) — so EVERY finished or
        // locked lane shrinks it, whatever the cause. The block comment two lines above the check says
        // so outright: "the count includes finished-target entries, an acceptable over-trigger".
        //
        // Let N = NGU entries (all survive) and K = origCount. Gutting needs valid.Count <= K/2, hence
        // N <= K/2. So the template fires whenever NGU entries are at most HALF the list and enough of
        // the remainder has hit target or locked — e.g. ["NGU-0","CAPTM:10","BESTAUG","ALLBT"] with the
        // TM at its cap and augments under the bossID<17 lock: valid = 2, orig = 4, 2*2 <= 4. TRUE.
        // The second door is independent: `valid.Count == 0` fires the template regardless of `gutted`.
        //
        // The real defect is the INVERSE of the one reported: the template is unreachable exactly in the
        // case it was written for — the 70%-NGU profile named on the line above, where N > K/2 always —
        // and reachable in cases it was not designed for. Reported, not fixed (amendment 32 §7).
        //
        // ⚠ AND IT IS NOT REDUNDANT EITHER — the SECOND delete premise, also false. The reading that
        // "FeasibilityPass.NguLane already refuses the NGU lanes, so the template has no NGU work left
        // to do" fails on two independent grounds:
        //
        //   1. THE NONGU TEMPLATE CONTAINS NO NGU TOKENS AT ALL. Read it: energy is
        //      CAPTM:10 / CAPWAN:60 / BESTAUG / CAPALLAT / ALLBT and magic is CAPTM:10 / CAPWAN:60 /
        //      BR-30. It does not reshape NGU lanes — it is entirely NON-NGU allocation policy, and
        //      specifically it is the answer to "what should the freed pool do INSTEAD", carrying cap
        //      PERCENTAGES (grammar [CAP]BASE[-index][:percent], ResourceBreakpoint.cs:12) and a
        //      priority ORDER that exist nowhere else. Contrast the NOTM and NOAUG templates on the
        //      lines below, which DO carry ALLNGU: the omission here is deliberate, not vestigial.
        //      Deleting it would delete that policy, which is why the delete was refused — the
        //      operator's ruling is about NGU allocation and this is not that.
        //   2. THE TEMPLATE RUNS BEFORE THE REFUSAL, so it cannot be downstream of it.
        //      ConstraintLayerBridge.cs:99 builds `valid`, :100 calls TransformPriorities (here), and
        //      the NguLane refusal is not reached until :316. The template therefore sees the NGU
        //      lanes still live, and when it fires it REPLACES membership wholesale.
        private static readonly Dictionary<string, Dictionary<ResourceType, string[]>> Templates =
            new Dictionary<string, Dictionary<ResourceType, string[]>>
            {
                ["NONGU"] = new Dictionary<ResourceType, string[]>
                {
                    [ResourceType.Energy] = new[] { "CAPTM:10", "CAPWAN:60", "BESTAUG", "CAPALLAT", "ALLBT" },
                    [ResourceType.Magic] = new[] { "CAPTM:10", "CAPWAN:60", "BR-30" },
                },
                ["NOTM"] = new Dictionary<ResourceType, string[]>
                {
                    [ResourceType.Energy] = new[] { "CAPWAN:40", "BESTAUG", "CAPALLAT", "ALLNGU", "ALLBT" },
                    [ResourceType.Magic] = new[] { "CAPWAN:40", "ALLNGU", "BR-30" },
                },
                ["NOAUG"] = new Dictionary<ResourceType, string[]>
                {
                    [ResourceType.Energy] = new[] { "CAPTM:5", "CAPWAN:40", "CAPALLAT", "ALLNGU", "ALLBT" },
                    [ResourceType.Magic] = new[] { "CAPTM:5", "CAPWAN:40", "ALLNGU", "BR-30" },
                },
            };

        // WHICH LIST THIS METHOD LAST HANDED BACK, PER POOL.
        //
        // ⚠ THIS EXISTS BECAUSE THE SUBSTITUTION WAS UNDISCLOSED. Every return below can hand the
        // allocator a list that is not the operator's, and until 2026-08-18 nothing downstream could
        // tell which — so ObjectiveParity's report asserted "THE PROFILE IS STILL THE ONE ALLOCATING"
        // on ticks where the profile's tokens had been replaced wholesale, and numbered the generated
        // lanes "(profile #10)" on a profile holding three tokens (audit/59 §A).
        //
        // Recorded AT THE RETURN, not re-derived by a reader. A reader would have to re-evaluate
        // AdvisorChallenges, AutoProfile, ChallengeDetector and the gutted test against state that has
        // moved on since — the same trap GearObjectiveIsSegment above documents, and the same fix.
        private static readonly Dictionary<ResourceType, ObjectiveParity.MembershipSource> _sourceOf =
            new Dictionary<ResourceType, ObjectiveParity.MembershipSource>();

        public static ObjectiveParity.MembershipSource MembershipSourceOf(ResourceType type)
        {
            ObjectiveParity.MembershipSource s;
            return _sourceOf.TryGetValue(type, out s) ? s : ObjectiveParity.MembershipSource.Unstated;
        }

        // Record-and-return. Every `return` in TransformPriorities goes through this, so a new exit
        // added later without one is visible as the odd branch out rather than as a silent relapse.
        private static List<ResourceBreakpoint> From(ResourceType type,
            ObjectiveParity.MembershipSource src, List<ResourceBreakpoint> list)
        {
            _sourceOf[type] = src;
            return list;
        }

        public static List<ResourceBreakpoint> TransformPriorities(ResourceBreakpoint[] original, List<ResourceBreakpoint> valid, ResourceType type)
        {
            try
            {
                if (Main.Settings == null) return From(type, ObjectiveParity.MembershipSource.Profile, valid);
                string cur = ChallengeDetector.Current();
                if (cur != "LSC") RestoreLscAugTargets();   // undo the LSC aug caps once the challenge is over
                if (cur == null || !Main.Settings.AdvisorChallenges)
                {
                    _lastActive.Remove(type);
                    _templateOn.Remove(type);
                    _fallbackOn.Remove(type);
                    _lscSwordOn = false;
                    // Option A: outside challenges the auto profile generates the list (challenge
                    // active with overlays off = profile rules, the conservative reading).
                    if (cur == null && Main.Settings.AutoProfile)
                    {
                        var gen = AutoGenerated(type);
                        if (gen.Count > 0)
                            return From(type, ObjectiveParity.MembershipSource.AutoProfile, gen);
                    }
                    return From(type, ObjectiveParity.MembershipSource.Profile, valid);
                }

                int origCount = original?.Length ?? 0;
                if (!_lastActive.TryGetValue(type, out var last) || last != valid.Count)
                {
                    _lastActive[type] = valid.Count;
                    if (valid.Count < origCount)
                        Record($"{type} priorities: {valid.Count}/{origCount} active", $"{cur}: dead/finished systems filtered");
                }

                // LSC disables NOTHING, so it should run the normal segment timeline (TM/AT/BT hours) and
                // merely FOCUS the laser sword aug + upgrade to the challenge target. Two fixes vs the old
                // behavior (user-reported after the first LSC): (a) BASE = the segment allocation when
                // AutoProfile — the old static-profile base ignored TM hour, so magic pooled in wandoos/
                // blood instead of the TM; (b) set the GAME's aug-6 targets to the challenge level so the
                // CAP sword priorities STOP at target (via TargetMet → IsValid=false) instead of filling to
                // their energy cap — otherwise Laser Sword raced to lv8 while the upgrade sat at lv1 (never
                // completing) and Basic Training got nothing. Once both hit target their breakpoints drop
                // and energy flows to the rest of the timeline.
                if (cur == "LSC")
                {
                    var baseList = Main.Settings.AutoProfile ? AutoGenerated(type) : valid;
                    // The base under the sword lanes is the auto profile's list whenever it produced
                    // one — the non-energy pools get that base verbatim, so they must report it as
                    // what it is rather than inheriting "profile" from the surrounding branch.
                    var baseSrc = ReferenceEquals(baseList, valid)
                        ? ObjectiveParity.MembershipSource.Profile
                        : ObjectiveParity.MembershipSource.AutoProfile;
                    if (baseList.Count == 0)
                    {
                        baseList = valid;
                        baseSrc = ObjectiveParity.MembershipSource.Profile;
                    }
                    if (type != ResourceType.Energy) return From(type, baseSrc, baseList);

                    SetLscAugTargets();
                    var sword = ParsedList("LSC|sword", new[] { "CAPAUG-12", "CAPAUG-13" }, type);
                    if (sword.Count > 0 && !_lscSwordOn)
                    {
                        _lscSwordOn = true;
                        Record("Energy → laser sword after setup caps", "LSC: BT/TM/Wandoos caps first, then sword + upgrade to target, then the rest");
                    }
                    else if (sword.Count == 0) _lscSwordOn = false;
                    // LSC is a normal run that also needs a sword: the base list's LEADING setup lanes
                    // (Basic Training, Time Machine, Wandoos) fill before it, and only what follows them
                    // -- augs, AT, NGUs -- waits for the sword to reach target. With the sword in front
                    // of everything, the run's cheap caps went unfunded until both sword lanes were done.
                    int setup = 0;
                    while (setup < baseList.Count && IsLscSetupLane(baseList[setup])) setup++;
                    var merged = new List<ResourceBreakpoint>(baseList.Take(setup));
                    foreach (var bp in sword) if (!merged.Contains(bp)) merged.Add(bp);
                    foreach (var bp in baseList.Skip(setup)) if (!merged.Contains(bp)) merged.Add(bp);
                    // Only an overlay once there is actually something in front; with no sword lanes
                    // left (both at target) `merged` IS the base list and reporting an overlay would
                    // be the same kind of unearned claim this pass exists to remove.
                    return From(type, sword.Count > 0
                        ? ObjectiveParity.MembershipSource.ChallengeOverlay
                        : baseSrc, merged);
                }

                // Template takeover when a strip challenge gutted the list (>= half inactive — the
                // count includes finished-target entries, an acceptable over-trigger since the
                // template only exists for challenges whose strips are the dominant cause).
                bool gutted = origCount > 0 && valid.Count * 2 <= origCount;
                if ((gutted || valid.Count == 0) && Templates.TryGetValue(cur, out var perType)
                    && perType.TryGetValue(type, out var tokens))
                {
                    var tpl = ParsedList($"{cur}|{type}", tokens, type);
                    if (tpl.Count > 0)
                    {
                        if (!_templateOn.TryGetValue(type, out var on) || !on)
                        {
                            _templateOn[type] = true;
                            Record($"{type} → {cur} template", $"profile list {valid.Count}/{origCount} active — reshaping allocation");
                        }
                        _fallbackOn[type] = false;
                        return From(type, ObjectiveParity.MembershipSource.ChallengeTemplate, tpl);
                    }
                }
                if (_templateOn.TryGetValue(type, out var wasOn) && wasOn)
                {
                    _templateOn[type] = false;
                    Record($"{type} → profile priorities", $"{cur}: profile list healthy again");
                }

                if (valid.Count > 0 || origCount == 0)
                {
                    _fallbackOn[type] = false;
                    return From(type, ObjectiveParity.MembershipSource.Profile, valid);
                }

                var fb = Fallback(type);
                if (fb.Count > 0)
                {
                    if (!_fallbackOn.TryGetValue(type, out var fbOn) || !fbOn)
                    {
                        _fallbackOn[type] = true;
                        Record($"{type} fallback priorities injected", $"{cur}: profile list is entirely inactive");
                    }
                }
                return From(type, ObjectiveParity.MembershipSource.ChallengeFallback, fb);
            }
            catch (Exception e)
            {
                Main.LogDebug($"TransformPriorities: {e.Message}");
                return From(type, ObjectiveParity.MembershipSource.Profile, valid);
            }
        }

        private static List<ResourceBreakpoint> ParsedList(string key, string[] tokens, ResourceType type)
        {
            if (!_templateParsed.TryGetValue(key, out var parsed))
            {
                var arr = new JSONArray();
                foreach (var t in tokens) arr.Add(t);
                parsed = ResourceBreakpoint.ParseBreakpointArray(arr, type).Where(x => x != null).ToList();
                _templateParsed[key] = parsed;
            }
            return parsed.Where(x => x.IsValid()).ToList();
        }

        // LSC completion = leveling the laser sword aug (augs[6]) AND its upgrade to the CONTROLLER's
        // laserSwordTarget(), which is this difficulty's completions (clamped to max) + 2 — NOT
        // Character.challenges.laserSwordChallenge.curCompletions + 2, which is the normal-difficulty
        // counter and unclamped, and on Evil/Sadistic can sit far above the real requirement. Setting
        // the game's aug-6 "set caps" to that level makes the CAP sword priorities stop there
        // (AugmentBP.TargetMet → IsValid=false), so BOTH reach target instead of Laser Sword racing to
        // its energy cap while the upgrade starves. Snapshot the user's own caps once (persisted, so a
        // save/reload mid-challenge can't strand the override) and restore them when LSC ends.
        private static void SetLscAugTargets()
        {
            try
            {
                var c = Main.Character;
                var a = c.augments.augs[6];
                long target = c.allChallenges.laserSwordChallenge.laserSwordTarget();
                var s = Main.Settings;
                if (s != null && !s.LscTargetsSaved)
                {
                    s.LscSavedAugTarget = a.augmentTarget;
                    s.LscSavedUpgTarget = a.upgradeTarget;
                    s.LscTargetsSaved = true;   // last: a throw above leaves the snapshot un-armed
                }
                a.augmentTarget = target;
                a.upgradeTarget = target;
            }
            catch (Exception e) { Main.LogDebug($"LSC aug targets: {e.Message}"); }
        }

        private static void RestoreLscAugTargets()
        {
            var s = Main.Settings;
            if (s == null || !s.LscTargetsSaved) return;
            try
            {
                var a = Main.Character.augments.augs[6];
                a.augmentTarget = s.LscSavedAugTarget;
                a.upgradeTarget = s.LscSavedUpgTarget;
                s.LscTargetsSaved = false;   // only disarm once the write landed
            }
            catch (Exception e) { Main.LogDebug($"LSC aug restore: {e.Message}"); }
        }

        private static List<ResourceBreakpoint> Fallback(ResourceType type)
        {
            // R3's fallback follows the CBlock-3 phase gate, so it cannot sit in the one-entry-per-
            // type cache below — the token set changes when the block completes. ParsedList's key
            // carries the tokens, so each phase still parses exactly once and IsValid() still runs
            // at call time. This path fires inside challenges when the profile's own R3 list is
            // entirely inactive; a bare ALLHACK here was the same hack-0-forever trap AutoTokens had.
            if (type == ResourceType.R3)
            {
                var toks = R3Tail();
                return ParsedList($"fallback|R3|{string.Join(",", toks)}", toks, type);
            }
            if (!_fallbackParsed.TryGetValue(type, out var parsed))
            {
                string[] tokens;
                switch (type)
                {
                    case ResourceType.Energy: tokens = new[] { "CAPTM:5", "CAPWAN:40", "BESTAUG", "CAPALLAT", "ALLNGU", "ALLBT" }; break;
                    case ResourceType.Magic: tokens = new[] { "CAPTM:5", "CAPWAN:40", "ALLNGU", "BR-30" }; break;
                    default: tokens = new string[0]; break;
                }
                var arr = new JSONArray();
                foreach (var t in tokens) arr.Add(t);
                parsed = ResourceBreakpoint.ParseBreakpointArray(arr, type).Where(x => x != null).ToList();
                _fallbackParsed[type] = parsed;
            }
            // IsValid at call time: unlock state and targets move; the challenge kills its own entries.
            return parsed.Where(x => x.IsValid()).ToList();
        }

        // Challenges tab: one-line allocation status per resource for the current view.
        public static string AllocationStatus(ResourceType type)
        {
            if (_templateOn.TryGetValue(type, out var on) && on) return "template";
            if (_lastActive.TryGetValue(type, out var n)) return $"{n} active";
            return null;
        }

        // ---- Option A: the auto profile. Generates energy/magic/R3 priorities per cycle through
        // the same TransformPriorities hook the challenge transforms use — parameterized by RUN
        // SEGMENT (D1: the guide's 24h shape — RECOVERY while the number is cheap power, then
        // TM HOUR -> AT HOUR -> NGU MARATHON), CHAPTER (D2: targeted NGU lists, guide ch.1-4), and
        // TM state. The profile file is never touched; toggling off resumes the timeline.
        // Rebirth + NGU-diff stay profile-owned. ----
        private static readonly Dictionary<ResourceType, string> _lastGenKey = new Dictionary<ResourceType, string>();

        // The marathon's blood lane. CAP (out of the equal-share divisor) + a percent (bounded to a
        // slice of the surplus, not all of it) + a seconds gate scaled to that percent so the same
        // rituals still qualify. See the reasoning at the marathon branch in AutoTokens — the three
        // parts are load-bearing together and must be retuned together.
        //
        // ⚠ TWO OF THOSE THREE PARTS NO LONGER DECIDE ANYTHING on the default (constraint-layer) path:
        // there is no divisor for CAP to escape, and a CAP-with-percent budget was min(ceil(cur×pct),
        // idle) — a percent of the resource CAP, which a mature account's cap exceeds the pool by, so
        // it never binds. The MARATHON is deliberately NOT stripped here: only the three programs on
        // amendment 01's REPLACE path are in scope (see SegmentRitual below and the AutoTokens
        // branches). Retuning `:10` is not a lever; it never was one.
        private const string MarathonRitual = "CAPBR-300:10";

        // The SAME blood lane and the SAME 300s duration filter, with the share semantics stripped:
        // the three programs on amendment 01's REPLACE path (AUGMENTATION / NGU+AT / EVIL NGU) emit
        // this instead of MarathonRitual. `-300` is BR's Index = secondsToRun, a MEMBERSHIP filter
        // deciding which rituals qualify (RitualMath.RitualDecide) — it is kept, unchanged, because
        // dropping it would change which rituals run. `CAP` and `:10` are the share semantics and
        // they decide nothing under the constraint layer (amendment 28); see AutoTokens.
        //
        // ⚠ THE TAIL GUARD MUST TEST FOR THIS TOKEN. It tests `!list.Contains(...)` by exact string,
        // so a segment emitting "BR-300" while the guard only knew "CAPBR-300:10" would look
        // ritual-less and get a SECOND blood lane appended — the exact duplicate-lane trap the guard
        // was written to stop.
        private const string SegmentRitual = "BR-300";

        // ── TWO LIFETIMES, BECAUSE THE READERS WANT TWO DIFFERENT THINGS (audit/59 §C, decision 4) ──
        //
        // Segment is LIVE: it is what is driving allocation RIGHT NOW, and it is "" when nothing is.
        // Every reader that makes a decision from it must use this one — OptimizationAdvisor's digger
        // order (`Segment == "NGU MARATHON"` picks a completely different digger list), Main's ITOPOD
        // routing (`== "EVIL CLIMB"`), ConstraintLayer's sink-anchor table, NGUDiffBreakpoints and
        // WandoosBreakpoints. A stale value here does not degrade a decision, it makes a DIFFERENT
        // decision, silently and forever.
        //
        // THE BUG THIS PAIR REPLACES: the `Segment = ""` reset sat behind the both-overlays-off early
        // return in Tick(), so it was unreachable in exactly the case it existed for. Observed live:
        // the digger order was still keyed to a segment set two hours earlier. Phase and the gear
        // objective were both cleared on that path; Segment was the one that was missed.
        //
        // LastSegment is PROVENANCE: the last segment that actually ran, kept after it stops, for
        // anything reconstructing why an earlier decision was made. Never used to decide anything.
        public static string Segment { get; private set; } = "";
        public static string LastSegment { get; private set; } = "";
        public static DateTime LastSegmentEndedAt { get; private set; } = DateTime.MinValue;

        // The only writer. Keeps the provenance pair honest without every assignment site having to
        // remember to, and makes "the segment ended" a single observable event.
        private static void SetSegment(string value)
        {
            value = value ?? "";
            if (Segment == value) return;
            if (Segment.Length > 0)
            {
                LastSegment = Segment;
                LastSegmentEndedAt = DateTime.UtcNow;
            }
            Segment = value;

            // audit/59 §C decision 5: a write justified by a segment that has ended is STALE, not
            // Active. This is MarkStale's only production caller and the reason the state exists.
            try
            {
                int staled = WriteLedger.MarkStaleOutsideSegment(Segment);
                if (staled > 0)
                    Record("Ledger", staled + (staled == 1 ? " write is" : " writes are") + " now stale",
                           "the segment that justified " + (staled == 1 ? "it" : "them") + " has ended");
            }
            catch { }
        }

        // The ordered plan for THIS run, and where in it we are. The segment chain used to be visible
        // as timeline chips on the WinForms autopilot panel; that panel went in the 2.0.0 companion
        // port and nothing replaced it, so since then the only way to know which segment was running
        // was to read inject.log. Published so the Overview can say it again.
        public static string[] SegmentChain { get; private set; } = new string[0];
        public static int SegmentIndex { get; private set; } = -1;
        public static double SegmentRunHours { get; private set; }

        private static int _chapter;
        private static DateTime _chapterAt = DateTime.MinValue;

        private static int Chapter()
        {
            if ((DateTime.UtcNow - _chapterAt).TotalSeconds > 60)
            {
                _chapterAt = DateTime.UtcNow;
                try { _chapter = StageDetector.Detect().Chapter; } catch { _chapter = 0; }
            }
            return _chapter;
        }

        private static void UpdateSegment(Character c)
        {
            bool tmEmpty = true;
            try { tmEmpty = c.machine.realBaseGold <= 0.0; } catch { }
            double runSec = 0;
            try { runSec = c.rebirthTime.totalseconds; } catch { }
            bool atUnlocked = false;
            try { atUnlocked = c.buttons.advancedTraining.interactable; } catch { }
            bool tmUnlocked = false;
            try { tmUnlocked = c.buttons.brokenTimeMachine.interactable && !c.challenges.timeMachineChallenge.inChallenge; } catch { }

            // TIME-ANCHORED (user-reported: RECOVERY held for 5 hours because kill-recency kept
            // "push" alive — past the boss ceiling, bosses die continuously and would hold the run
            // hostage forever). The wall clock owns the shape; the number rule gets a bounded window:
            //   TM HOUR   — ONLY while TM is unlocked: first hour, and any time TM gold is zero.
            //               Evil (and Sadistic) re-lock the Time Machine, so early-Evil runs skip
            //               this and open on RECOVERY/NGU MARATHON until TM re-unlocks.
            //   AT HOUR   — second hour (if AT is unlocked); AtHourPlanner may extend it to the 4h
            //               mark when projected AT gains cross a titan stage or unlock a farm zone
            //               (one bounded decision per run — the time anchor stays the law)
            //   RECOVERY  — number still cheap (>=2 boss/30m), but never past hour 4
            //   MARATHON  — everything after (the guide's 22h); its start is never delayed
            bool numberCheap = !double.IsNaN(Bosses30) && Bosses30 >= 2.0;

            // EVIL CLIMB (guide ch.5 sub-mode A) — early Evil is a boss RE-CLIMB, not a mature 24h run:
            // entering Evil re-locks TM/AT/Wandoos and resets the Number, so the TM/AT/RECOVERY/MARATHON
            // shape doesn't fit. The WHOLE pre-T7 climb (to Boss 125) pushes bosses + grows Number + levels
            // NGUs (AT feeds the EV-exploder objective as systems re-unlock). The matching short Number-target
            // rebirths + the first Evil Basic entry are profile-owned (EvilStart: Number/100 + BASIC-1).
            // Chapter-gated so Normal is untouched; hands back to the normal chain at Boss 125 / T7 unlock
            // (later slices add the guide's full 24h Evil shape past T7).
            // GATED ON DIFFICULTY, NOT CHAPTER. These used to require StageDetector.Chapter == 5, which
            // ends at Evil boss 166 — the boss that unlocks Interdimensional Party. So the moment the
            // player reached IDP, every Evil-specific segment switched off and the run silently reverted
            // to the NORMAL 24h shape (TM HOUR -> AT HOUR -> RECOVERY -> NGU MARATHON) on an Evil
            // character, with no warning and no log line. The guide's four-phase shape is the EVIL run
            // shape — it "REPLACES the Normal 1h TM / 1h AT / 22h NGU" for the whole difficulty, not for
            // one chapter of it — so difficulty is the correct gate and the boss thresholds below carry
            // the progression. Chapter() also falls back to 0 on any exception, which used to drop a
            // healthy Evil run out of its own shape for up to 60s at a time.
            bool isEvil = false;
            try { isEvil = c.settings.rebirthDifficulty == difficulty.evil; } catch { }

            bool evilClimb = false;
            try { evilClimb = isEvil && ZoneHelpers.CurrentHighestBoss(c) < 125; } catch { }

            // AUGMENTATION (guide ch.5 sub-mode B, phase 2) — once T7-capable (Boss 125+), the 24h run is
            // TM 0–0.5h → AUGMENTATION 0.5–3h → Normal-NGU+AT → Evil-NGU. This phase runs the best augment
            // (energy) and feeds blood for the Counterfeit-once → Blood Number spells (magic). Chapter+boss-
            // gated; preempts TM HOUR from 0.5h so TM keeps only the guide's first half-hour. Magic→blood
            // routing is tunable (untested until Boss 125+).
            bool evilRun = false;
            try { evilRun = isEvil && ZoneHelpers.CurrentHighestBoss(c) >= 125 && tmUnlocked; } catch { }

            bool augPhase = evilRun && runSec >= 1800 && runSec < 10800;

            // Guide ch.5 sub-mode B phases 3 and 4, which had no implementation at all: after the
            // augment window the run was handed straight back to the NORMAL chain, so the guide's
            // "3:00-23:00 Normal NGU **and** Advanced Training" became a single-system AT HOUR followed
            // by an NGU marathon, and the "23:00-24:00 Evil NGU" tail existed only as a level-track
            // switch in LevelPlanner with no matching allocation.
            //
            // The tail is sized the way the guide sizes it: N hours where N = T7 versions DEFEATED
            // (1h after T7v1, 2h after T7v2...). That is the same rule and the same conversion
            // LevelPlanner.TickNguTrack uses, read from the same places, so the allocation shape and the
            // level track cannot disagree about when Evil NGUs start. With no time-based rebirth target
            // (RebirthTime -1 / number-target profiles) there is no "last N hours" to compute, so the
            // run simply stays in NGU+AT — which is what it did before this existed.
            bool evilNguTail = false;
            if (evilRun && runSec >= 10800)
            {
                try
                {
                    double target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1;
                    // Kills, not the difficulty selector. The selector is pinned to whatever version the
                    // advisor is chasing, so this read 0 on an account with eight confirmed T7 v2 kills —
                    // and the Evil-NGU tail below has therefore never once been scheduled.
                    int t7Defeated = ZoneHelpers.VersionsDefeatedByKills(6);
                    if (target > 0 && t7Defeated >= 1)
                        evilNguTail = runSec >= target - t7Defeated * 3600.0;
                }
                catch { }
            }

            string seg;
            if (evilClimb) seg = "EVIL CLIMB";
            else if (augPhase) seg = "AUGMENTATION";
            else if (evilNguTail) seg = "EVIL NGU";
            else if (evilRun && runSec >= 10800) seg = "NGU+AT";
            else if (tmUnlocked && (tmEmpty || runSec < 3600)) seg = "TM HOUR";
            else if (atUnlocked && runSec < AtHourPlanner.EndSec(c, runSec)) seg = "AT HOUR";
            else if (numberCheap && runSec < 14400) seg = "RECOVERY";
            else seg = "NGU MARATHON";

            // The shape of THIS run, for the companion. Built here, from the same unlock flags the
            // decision above just used, so the displayed plan cannot drift from the plan being run —
            // a separately-derived chain would be a second source of truth for the same question.
            // A segment the run can never reach (TM re-locked on Evil, AT not unlocked yet) is simply
            // absent rather than shown as skippable, because it was never part of this run's plan.
            var chain = new List<string>();
            if (evilClimb) chain.Add("EVIL CLIMB");
            else if (evilRun)
            {
                // The guide's four-phase Evil 24h shape, shown whole so the companion timeline matches
                // what the run will actually do. The Evil-NGU tail only appears when it can be computed
                // (a time-based rebirth target and at least one T7 version down).
                chain.Add("TM HOUR");
                chain.Add("AUGMENTATION");
                chain.Add("NGU+AT");
                if (evilNguTail || CanPlanEvilNguTail()) chain.Add("EVIL NGU");
            }
            else
            {
                if (tmUnlocked) chain.Add("TM HOUR");
                if (atUnlocked) chain.Add("AT HOUR");
                chain.Add("RECOVERY");
                chain.Add("NGU MARATHON");
            }
            // The current segment must always appear, even if a flag flickered between the two blocks.
            if (!chain.Contains(seg)) chain.Insert(0, seg);
            SegmentChain = chain.ToArray();
            SegmentIndex = Array.IndexOf(SegmentChain, seg);
            SegmentRunHours = runSec / 3600.0;

            if (seg != Segment)
            {
                SetSegment(seg);
                if (Main.Settings != null && Main.Settings.AutoProfile)
                    Record("SEGMENT", $"segment → {seg}",
                        $"run {runSec / 3600:0.0}h · {(double.IsNaN(Bosses30) ? Phase : $"≈{Bosses30:0.#} boss/30m")}");
            }
        }

        // Will the Evil-NGU tail be reachable this run? Used only to decide whether to SHOW it in the
        // planned chain; the live decision is made in UpdateSegment from the same two facts.
        private static bool CanPlanEvilNguTail()
        {
            try
            {
                double target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1;
                return target > 0 && ZoneHelpers.VersionsDefeatedByKills(6) >= 1;   // kills, not the selector
            }
            catch { return false; }
        }

        private static string SegmentGear()
        {
            switch (Segment)
            {
                case "EVIL CLIMB": return "Adventure";   // re-climb: adventure stats push the boss wall
                case "AUGMENTATION": return "Augments";  // guide ch5 phase 2: gear for augment speed
                // Phase 3 runs NGUs and AT together; the NGU side is the one that compounds all run, so
                // gear for NGU speed and let the bounded AT share ride on it.
                case "NGU+AT": return "NGUs";
                case "EVIL NGU": return "NGUs";
                case "TM HOUR": return "Time Machine";
                case "RECOVERY": return "Adventure";
                // AT HOUR levels the trainings — gear for AT SPEED, not combat stats (user-compared
                // vs the GO site's "AT gains" loadout: "Adventure" here wore Power/Toughness gear).
                case "AT HOUR": return "Advanced Training";
                case "NGU MARATHON": return "NGUs";
                default: return null;
            }
        }

        public static string AutoStatus()
        {
            bool tmEmpty = true;
            try { tmEmpty = Main.Character.machine.realBaseGold <= 0.0; } catch { }
            string ph = string.IsNullOrEmpty(Phase) ? "growth" : Phase;
            string seg = string.IsNullOrEmpty(Segment) ? "" : $"{Segment} · ";
            string num = double.IsNaN(Bosses30) ? "number: measuring" : $"number ≈ {Bosses30:0.#} boss/30m";
            return $"{seg}{ph} phase · TM {(tmEmpty ? "empty" : "funded")} · {num}";
        }

        // D2: chapter NGU CANDIDATES (guide ch.1-3 — which bonuses matter early). Energy indices:
        // 0 Augs, 1 Wandoos, 4 Adventure-a, 6 Drop Chance. Magic: 0 Ygg, 3 Number, 4 TM.
        // From ch.4 on, EVERY NGU is a candidate (user rule 2026-07-11: the old ch.4 group lists
        // excluded E7 Magic / E8 PP / M5 Energy / M6 Adventure-β outright, so they never ran) —
        // NGUAdvisors's exact-value ranking decides which lanes actually get energy/magic.
        public static int[] ChapterNguIds(ResourceType type)
        {
            int ch = Chapter();
            if (type == ResourceType.Energy)
            {
                if (ch == 1) return new int[0];             // pre-NGU era
                if (ch == 2) return new[] { 0, 1 };
                if (ch == 3) return new[] { 4, 6 };
                return new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            }
            if (type == ResourceType.Magic)
            {
                if (ch == 1) return new int[0];
                if (ch == 2) return new[] { 0, 3 };
                if (ch == 3) return new[] { 0 };
                return new[] { 0, 1, 2, 3, 4, 5, 6 };
            }
            return new int[0];
        }

        // The NGUs to actually run: NGUAdvisors's value-ranked pick from the chapter candidates
        // (>=1.05x/hr keeps a lane open; nothing hot = deepen the top two), chapter list fallback.
        private static string[] ChapterNgus(ResourceType type)
        {
            var ids = ChapterNguIds(type);
            if (ids.Length == 0) return new string[0];
            try
            {
                var plan = NGUAdvisors.Compute(ChapterNguIds(ResourceType.Energy), ChapterNguIds(ResourceType.Magic));
                var targets = type == ResourceType.Magic ? plan.MagicTargets : plan.EnergyTargets;
                if (plan.Known && targets.Length > 0) ids = targets;
            }
            catch { }
            return ids.Select(i => $"NGU-{i}").ToArray();
        }

        // Surplus lanes: positive-value NGUs that didn't make the hot set. They are DISJOINT from the
        // hot set by construction (NguValueMath.Surplus filters `!targets.Contains`), so emitting them
        // with or without the CAP prefix cannot collide with, or de-duplicate away, a hot lane.
        //
        // `cap` exists only because the CAP prefix was share semantics: the absorbers were emitted as
        // CAPNGU to stay OUT of the equal-share divisor so the hot lanes' shares were untouched. The
        // three programs on amendment 01's REPLACE path pass false — under the constraint layer there
        // is no divisor to stay out of and the prefix decides nothing (amendment 28). The segments not
        // on that path (the NGU MARATHON) still pass true, deliberately: stripping them is a separate,
        // separately-scoped decision, not a side effect of this one.
        private static string[] ChapterNgusSurplus(ResourceType type, bool cap = true)
        {
            try
            {
                var plan = NGUAdvisors.Compute(ChapterNguIds(ResourceType.Energy), ChapterNguIds(ResourceType.Magic));
                var ids = type == ResourceType.Magic ? plan.MagicSurplus : plan.EnergySurplus;
                if (plan.Known && ids != null)
                    return ids.Select(i => cap ? $"CAPNGU-{i}" : $"NGU-{i}").ToArray();
            }
            catch { }
            return new string[0];
        }

        // Latch for the ritual-funding transition log below (null = not yet evaluated this load).
        private static bool? _lastBloodMatters;

        // ---- The guide ch.5 hack-phase gate: "Post-T7, run A/D Hack until completing CBlock 3",
        // then Adventure as the default push plus the first-milestone sweep on ids 2-6. The token
        // lists and the index-trap resolution live in HackPhase; the block's requirements come from
        // CampaignTables.LegRequirements (the same rows Status() reads); the live counters from
        // Block() above. This used to be a bare ALLHACK — hacks 0..14 in index order — and the R3
        // waterfill parks the whole pool on the head lane, so hack 0 drank everything FOREVER, at
        // every stage: exactly the "advisor ingesting ch.5 rows without the chapter gate runs hack 0
        // forever" failure audit/10 §A1.2 called out before this generator existed.
        //
        // COMPLETIONS ARE PER-DIFFICULTY AND ONLY THE CURRENT ONE IS READABLE, so the gate is
        // decided where it can be verified: on Evil, against cblock3's Evil-leg ordinals; on
        // Sadistic, always post (Sadistic is entered through Evil's ending — the sweep lanes then
        // self-report done instantly if their milestones are long past); on Normal, always pre —
        // that covers the block's own rebirth-to-Normal return trip, where hacks pay nothing and
        // A/D is the guide's standing answer anyway. A failed read keeps the LAST answer rather
        // than flapping the profile for up to 60s the way Chapter()'s zero-fallback used to.
        private static bool _postCBlock3;
        private static DateTime _postCBlock3At = DateTime.MinValue;

        // ── PRICING THE R3 TAIL FROM LIVE STATE ─────────────────────────────────────────────────
        //
        // Every term comes off the game, so there is no constant table to go stale: b is
        // HacksController.properties[id].baseEffectPerLevel, the cost is the game's own
        // progressPerTickCap folded through HackMath.Saturation, and eligibility is HackBP's own
        // Unlocked() rule copied verbatim so a lane can never be RANKED that would then be REFUSED.
        //
        // Cached on the same 60s cadence as the phase gate above: density only moves when a level
        // moves, and a level is minutes to hours away at every price on a mature board.
        private static DateTime _tailAt = DateTime.MinValue;
        private static string[] _tail;
        private static string _tailShape;

        private static IList<HackPhase.Candidate> HackCandidates()
        {
            var list = new List<HackPhase.Candidate>();
            var c = Main.Character;
            if (c == null) return list;

            long cap;
            bool on;
            try
            {
                cap = c.totalCapRes3();
                on = c.buttons.hacks.interactable;
            }
            catch { return list; }
            if (!on || cap <= 0) return list;

            var hc = c.hacksController;
            for (int id = 0; id <= 14; id++)
            {
                var cand = new HackPhase.Candidate { Id = id, Eligible = false, Density = 0.0 };
                try
                {
                    long level = c.hacks.hacks[id].level;
                    if (level >= hc.hardCapLevel(id)) { list.Add(cand); continue; }

                    long sat = HackMath.Saturation(cap, hc.progressPerTickCap(id));
                    if (sat <= 0) { list.Add(cand); continue; }

                    double b = hc.properties[id].baseEffectPerLevel;
                    double d = HackMath.MarginalDensity(b, level, sat);

                    // The milestone step is included: it is real value the pure rate drops, and
                    // amortising it over the levels still owed is the honest way to price a lane
                    // that is one level from a 10% jump (hack 13) against one that is fifty from a
                    // 0.5% one. It moves the ORDER only when two lanes are already close.
                    d += HackMath.MilestoneStep(hc.properties[id].milestoneEffect,
                                                hc.levelsToNextMilestone(id), sat);

                    cand.Density = d;
                    cand.Eligible = d > 0;
                }
                catch { cand.Eligible = false; }
                list.Add(cand);
            }
            return list;
        }

        private static string[] R3Tail()
        {
            if (_tail != null && (DateTime.UtcNow - _tailAt).TotalSeconds <= 60) return _tail;
            _tailAt = DateTime.UtcNow;
            try
            {
                var cands = HackCandidates();
                _tail = HackPhase.R3Tokens(PostCBlock3(), cands);

                // Announce only when the ORDER changes — this runs every minute forever.
                string shape = string.Join(",", _tail);
                if (shape != _tailShape)
                {
                    _tailShape = shape;
                    var top = new List<string>();
                    var ranked = new List<HackPhase.Candidate>(cands);
                    ranked.RemoveAll(x => !x.Eligible || x.Density <= 0);
                    ranked.Sort((a, b) => b.Density.CompareTo(a.Density) != 0
                        ? b.Density.CompareTo(a.Density) : a.Id.CompareTo(b.Id));
                    for (int i = 0; i < ranked.Count && i < 3; i++)
                        top.Add(SystemCatalog.NameOf(SystemCatalog.Hacks, ranked[i].Id)
                                + " (" + ranked[i].Density.ToString("0.###e+0") + ")");
                    Main.LogDebug("[HackRank] " + shape + (top.Count > 0 ? "  top: " + string.Join(" > ", top.ToArray()) : ""));
                    Record("R3 → hack order", top.Count > 0
                        ? "priced by marginal value density — leading with " + top[0]
                        : "no hack could be priced; falling back to the Adventure default");
                }
            }
            catch (Exception e)
            {
                Main.LogDebug($"R3Tail: {e.Message}");
                _tail = HackPhase.R3Tokens(PostCBlock3());   // today's static list, never worse
            }
            return _tail;
        }

        private static bool PostCBlock3()
        {
            if ((DateTime.UtcNow - _postCBlock3At).TotalSeconds <= 60) return _postCBlock3;
            _postCBlock3At = DateTime.UtcNow;
            bool was = _postCBlock3;
            try
            {
                var diff = Main.Character.settings.rebirthDifficulty;
                if (diff >= difficulty.sadistic) _postCBlock3 = true;
                else if (diff != difficulty.evil) _postCBlock3 = false;
                else
                {
                    var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Evil);
                    var live = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in Block()) live[e.Code] = e.Cur;
                    _postCBlock3 = HackPhase.ChainSatisfied(req, live);
                }
            }
            catch (Exception e) { Main.LogDebug($"PostCBlock3: {e.Message}"); }
            if (_postCBlock3 != was)
                Record("R3 → hack phase", _postCBlock3
                    ? "CBlock 3 complete — first-milestone sweep (TM/Drop/Aug/eNGU/mNGU), then Adventure as the default push"
                    : "pre-CBlock 3 — A/D Hack carries R3 until the block completes");
            return _postCBlock3;
        }

        public static string[] AutoTokens(ResourceType type)
        {
            if (type == ResourceType.R3) return R3Tail();
            if (type != ResourceType.Energy && type != ResourceType.Magic) return new string[0];

            bool e = type == ResourceType.Energy;
            var ngus = ChapterNgus(type);
            var list = new List<string>();
            string seg = string.IsNullOrEmpty(Segment) ? "NGU MARATHON" : Segment;

            // Rituals cost magic that would otherwise be NGU growth, so run them ONLY while blood is
            // ACTUALLY being converted into something: a sink the blood advisor has routed on
            // (number/loot/gold) or an Iron Pill worth pursuing. When the routing is idle AND the pill
            // isn't worth it, drop rituals so that magic feeds the current segment instead of pooling
            // blood that never gets cast.
            //
            // BloodPlanner owns this answer. This used to read the live auto-spell toggles directly,
            // which made the profile depend on an OUTPUT of ApplyBlood (60s-throttled) — the two
            // advisors talking through mutated game state. BloodMatters() returns the routing INTENT
            // when the advisor owns blood, and falls back to the live toggles when it doesn't (manual /
            // AutoSpellSwap), so every quadrant of advisor-on/off answers from one place.
            bool rituals = !e && BloodPlanner.BloodMatters();
            if (!e && _lastBloodMatters != rituals)
            {
                _lastBloodMatters = rituals;
                Main.Log($"Overlay: rituals {(rituals ? "ON — BR-30 in the magic profile (blood has a live sink)" : "OFF — no live blood consumer (magic stays on NGUs)")}");
            }

            switch (seg)
            {
                case "EVIL CLIMB":
                    // Ch.5 re-climb (guide sub-mode A): mirror RECOVERY — push bosses (cheap adventure caps),
                    // a small CAPTM:5 to bootstrap TM GOLD once it re-unlocks (~boss 30) so GOLD DIGGERS can
                    // run (diggers need gold income to cover their drain; user-caught: no TM feed → grossGold
                    // stays 0 → diggers never turn on), best augment → AT (EV-exploder) → NGUs; magic feeds
                    // Wandoos then Number. TM/Wandoos CAP tokens self-skip while still locked.
                    if (e) { list.Add("CAPALLBT"); list.Add("CAPTM:5"); list.Add("CAPWAN:40"); list.Add("BESTAUG"); list.Add("CAPALLAT"); }
                    else { list.Add("CAPWAN:40"); list.Add("NGU-3"); }   // magic: Wandoos (when unlocked) then Number NGU
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    break;
                case "AUGMENTATION":
                    // Guide ch5 phase 2 (0.5–3h): energy → best augment (cheap stat caps first); magic →
                    // blood (fuels the Counterfeit-once → Blood Number spells the BloodPlanner casts).
                    //
                    // ---- SHARE SEMANTICS STRIPPED (amendment 11 §4.4 REPLACE · amendment 28 · 31 §Q4a) ----
                    // This hunk used to emit CAPALLBT + CAPBESTAUG, and its own comment said exactly why
                    // the CAP was there: IsCap is a substring test, so plain BESTAUG was a NON-cap lane
                    // sitting inside the prioCount equal-share divisor, and the segment whose entire
                    // purpose is the augment was handed 1/prioCount of the pool — a share measured falling
                    // 1/3 → 1/6 in ten seconds as the NGU hot set grew (2026-08-01 log). The CAP was a
                    // DIVISOR WORKAROUND by construction.
                    //
                    // THERE IS NO DIVISOR LEFT. Every seated destination is offered
                    // min(capacity, remaining / destinations-not-yet-offered) (ConstraintLayer.FillSession),
                    // so a lane's share no longer depends on how many other lanes are non-CAP, and CAP
                    // buys nothing. The workaround's own stated bound was wrong anyway: "bounded by its
                    // own need" (CalculateAugCap's stair-snap) holds only while ONE augment level costs
                    // less than the pool — late-game it never does, so the effective bound was
                    // min(need, pool) = pool, and audit 31 §Q1b measured 99.93% of the energy pool in
                    // this one lane.
                    //
                    // What is left below is MEMBERSHIP and ORDER, which are still load-bearing: they are
                    // the only thing deciding which lanes seat and in what sequence. The amounts belong
                    // to the constraint layer.
                    //
                    // BR-30 is NOT added here. It used to be appended unconditionally by an `else` on this
                    // line, which contradicted this block's own comment claiming it was the same
                    // live-consumer-gated routing the marathon uses. It was not: the gated append is in
                    // the tail below (`if (rituals && ...)`), and because that guard tests
                    // `!list.Contains("BR-30")` it found the token already present and did nothing. Net
                    // effect: when BloodPlanner.BloodMatters() was false — blood idle, nothing being cast —
                    // every other segment correctly dropped rituals and this one still funded a ritual
                    // with no sink. Falling through to the tail restores the gate; when blood does have a
                    // live consumer the tail adds BR-30 exactly as before.
                    //
                    // ---- ⚠ WIDENING THIS MEMBERSHIP WAS PROPOSED, MEASURED, AND REFUSED ------------
                    // (amendment 36 §3, 2026-08-08. Do not re-open it from the token list alone.)
                    //
                    // THE PROPOSAL was to seat the surplus sink (WAN), ALLAT, TM and the surplus-NGU
                    // tail here, ordered after BESTAUG, because AUGMENTATION is the ONLY energy segment
                    // with no WAN, no ALLAT and no TM, and its ticks logged `sink=absent`. Both halves
                    // of that premise are true. THE CONCLUSION IS NOT, and the reason is arithmetic on
                    // a conserved quantity: this segment's anchor lane is ALREADY TAKING THE WHOLE POOL,
                    // so every lane added to it is subtracted from the augment, not from idle resource.
                    //
                    // MEASURED on the operator's own [AllocDbg] block — energy pool 1,728,134,347,235
                    // at 9270s, replayed through the shipped ConstraintLayer.Waterfill, with every added
                    // lane modelled as a hard CEILING at its measured `took=` from the 17-lane block 450
                    // seconds later and BestAug given INFINITE appetite. Both assumptions favour the
                    // augment, so these are UPPER BOUNDS on what widening could leave it:
                    //
                    //   today (BESTAUG + 6 hot NGUs)      BestAug 1,723,068,828,990   99.707%   idle 0
                    //   + WAN, ALLAT, TM, surplus NGUs    BestAug   231,750,007,913   13.410%   idle 0
                    //   + WAN only (closes sink=absent)   BestAug   861,534,414,495   49.853%   idle 13.7%
                    //
                    // The full widening costs the augment 7.44x. Even seating the sink ALONE halves it —
                    // and makes the remainder WORSE, not better: Wandoos saturates at its per-tick
                    // absorptive capacity (624,350,820,085 measured) and the reserve banked above that
                    // is stranded, so a segment that idles NOTHING today would idle 237,183,594,410.
                    // [OPERATOR]: gains across systems "without degrading the active 'farm' system" —
                    // in this segment the augment IS the active farm system.
                    //
                    // AND THE REMAINDER THAT MOTIVATED IT IS ALREADY GONE. The 85% energy figure was the
                    // pre-waterfill single pass (fixed by 3546670: this block now logs `remainder=0`),
                    // and the 98% magic figure was the BR withdrawal (fixed by the re-offer gate —
                    // ConstraintLayer.ReofferTable; magic returns to `remainder=401`-scale). There is no
                    // idle pool here left for extra lanes to catch.
                    //
                    // ⚠ WHAT WAS STILL OPEN — AND IS NOW CLOSED, BY A CONDITIONAL SINK RATHER THAN BY
                    // A TOKEN. This segment emits no WAN, so if its ANCHOR LANE DROPS OUT the pool had
                    // no destination at all:
                    //   · energy — BestAug refused (every pair at target, or the No Augs challenge) and
                    //     ALLBT's twelve slots all at cap leaves six NGU ceilings, ~5.07 B of 1.728 T;
                    //   · magic  — BloodPlanner.BloodMatters() false drops BR-30 and leaves five NGU
                    //     ceilings, ~16.4 B of 1.026 T.
                    // Both are ~99% idle with `sink=absent`. Neither was observed in the 2026-08-07 logs
                    // (`rituals ON` throughout, BestAug seated every tick), so this was latent.
                    //
                    // ⚠ THE FIX IS NOT ON THIS LINE, AND MUST NOT BE MOVED ONTO IT. Adding "WAN" to the
                    // tokens below is exactly the unconditional widening priced at 2.02x above — the
                    // sink sits INSIDE the fill's divisor (proved from the operator's own 15-lane block
                    // in ConstraintLayer.AnchorAbsentSink's header) and it also banks a share every
                    // waterfill ROUND, so an unconditional WAN takes the augment from 99.707% to
                    // 49.448% of the pool. The seat is instead made in WithAnchorAbsentSink, AFTER
                    // IsValid() has run, and ONLY when the anchor is already gone — so in every tick
                    // the operator has actually logged, the membership below is unchanged to the lane.
                    if (e) { list.Add("ALLBT"); list.Add("BESTAUG"); }
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    break;
                case "NGU+AT":
                    // Guide ch5 phase 3 (3:00-23:00): "BB Normal NGUs *while* raising AT for objectives",
                    // explicitly SIMULTANEOUS. That combination is the thing the old shape could not
                    // express: it ran a single-system AT HOUR and then an NGU marathon, so for most of the
                    // run exactly one of the two systems the guide wants funded together was getting
                    // anything.
                    //
                    // ---- SHARE SEMANTICS STRIPPED (amendment 11 §4.4 REPLACE · amendment 28 · 31 §Q4a) ----
                    // Removed from the tokens below, and from this comment: CAPTM:5, CAPWAN:40,
                    // CAPALLBT, CAPALLAT:15, CAPBR-300:10 and the CAPNGU surplus absorbers. Every one of
                    // those decorations existed to steer the prioCount share model — CAP to leave the
                    // divisor, :percent to bound what leaving it then let a lane take, absorber-ordering
                    // to decide who drank the residue. That model is gone: the fill offers every seated
                    // destination min(capacity, remaining / destinations-not-yet-offered).
                    //
                    // AND THE PERCENTS NEVER BOUND WHAT THEY CLAIMED. A CAP-with-percent budget was
                    // min(ceil(cur × pct), idle) — a percent of the resource CAP, not of the idle pool —
                    // so on a mature account it exceeds the pool and the percent simply never binds.
                    // Live evidence: CAPTM:5 took 867B where the old model gave it 43.3B. The
                    // "CAPALLAT:15 is the bound / tune here" note this comment used to carry was
                    // therefore describing a knob that did not turn, and the guide's own bound ("AT with
                    // <40% of Energy cap") is a TARGET-side statement that no token percent can express.
                    //
                    // ORDER IS KEPT EXACTLY: TM, Wandoos, the cheap BT caps, the five AT slots, the hot
                    // NGU lanes, the ritual, then the surplus NGUs. It is still load-bearing — it is the
                    // sequence the fill walks — even though the reasons the old comment gave for it
                    // (self-limiting caps first, absorbers below the ritual) were share-model reasons.
                    list.Add("TM");
                    list.Add("WAN");
                    if (e) { list.Add("ALLBT"); list.Add("ALLAT"); }
                    // BESTAUG — A DESTINATION FOR THE SURPLUS, AND THE BEST ONE ON THE ATTACK AXIS.
                    //
                    // This segment seated nothing that could hold a large pool. Measured on the live save
                    // 2026-08-18, mid-Evil, boss 159: every lane with a finite ceiling summed to 14.4% of
                    // the energy pool — Wandoos 2.87%, the nine NGUs 3.53%, the four AT slots 7.98%, Basic
                    // Training TWELVE UNITS — and the other 85.6% had exactly two possible homes, neither
                    // of them seated here. BestAug's per-tick ceiling at that state is ~5.4e15, about
                    // 2,470x the whole idle remainder.
                    //
                    // WHY BESTAUG AND NOT TM, WHICH WAS ALREADY SEATED. Both are unbounded relative to the
                    // pool, so the fill's equal share splits the surplus between them — this does not
                    // redirect TM, it adds a second destination. On the attack axis the two are not close:
                    // routing the surplus for the rest of a 24h run moves augment pair 2 from 675/1093 to
                    // 958/1549, i.e. augmentsController.totalBonus() x3.44 straight into totalAttack
                    // ([DECOMP] AugmentController.cs:701-704, Character.cs:1082), against TM's x1.13 on
                    // gross gold — and TM-speed's remaining effect is only speedGoldMultiBonus, whose
                    // elasticity 1/(levelSpeed-48) has been decaying since levelSpeed 49 and never
                    // recovers ([DECOMP] TimeMachineController.cs:252-260).
                    //
                    // ⚠ IT IS A PHASE-3 LANE, NOT A STANDING ONE. Guide ch5 phase 2 is AUGMENTATION and
                    // owns the augment push; this seats the lane so phase 3's surplus has somewhere to go,
                    // not to re-run phase 2. On the Sadistic track the augment branch takes a further
                    // ~5e19 slowdown ([DECOMP] AugmentController.cs:332, :347-350), so on a mature save
                    // this lane self-limits to near nothing rather than competing — which is the correct
                    // behaviour and needs no gate here.
                    if (e) list.Add("BESTAUG");
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    if (rituals) list.Add(SegmentRitual);
                    foreach (var t in ChapterNgusSurplus(type, cap: false)) if (!list.Contains(t)) list.Add(t);
                    break;
                case "EVIL NGU":
                    // Guide ch5 phase 4 (the last N hours, N = T7 versions defeated): "switch to Evil
                    // NGUs ... focus NGU Augments + NGU Ygg/EXP toward T7". Pure NGU growth, so no AT
                    // lane here — AT had phase 3 — and the surplus NGUs are seated in this window
                    // because it is the one the whole run was built to feed.
                    //
                    // The LEVEL TRACK itself is not switched here: LevelPlanner.TickNguTrack (or the
                    // profile's own NGUDiff timeline, which now wins when it has one) owns that, and both
                    // size the window from the same T7-versions-defeated rule this segment does.
                    //
                    // ---- SHARE SEMANTICS STRIPPED (amendment 11 §4.4 REPLACE · amendment 28 · 31 §Q4a) ----
                    // Same strip as NGU+AT above, same reasons: CAPTM:5 / CAPWAN:40 / CAPALLBT /
                    // CAPBR-300:10 / CAPNGU absorbers → the bare lanes. "Absorber" was a share-model
                    // role, not a lane kind: what made the surplus NGUs absorb was being LAST in the
                    // list, and they still are.
                    list.Add("TM");
                    list.Add("WAN");
                    if (e) list.Add("ALLBT");
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    if (rituals) list.Add(SegmentRitual);
                    foreach (var t in ChapterNgusSurplus(type, cap: false)) if (!list.Contains(t)) list.Add(t);
                    break;
                case "TM HOUR":
                    // The guide's hour-0 shape (24hr profiles): cap the cheap BTs, fund TM, wandoos,
                    // rest to augs. NO AT here (user-reported: CAPALLAT was draining the TM hour) —
                    // AT has its own hour.
                    if (e) list.Add("CAPALLBT");
                    list.Add("CAPTM:30");
                    list.Add("CAPWAN:40");
                    if (e) list.Add("BESTAUG");
                    break;
                case "RECOVERY":
                    if (e) list.Add("CAPALLBT");   // cheap caps first (adventure stats for the push)
                    list.Add("CAPTM:5");
                    list.Add("CAPWAN:40");
                    if (e) { list.Add("BESTAUG"); list.Add("CAPALLAT"); }
                    else list.Add("NGU-3");   // D3: the Number NGU IS the number-growth allocation
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    break;
                case "AT HOUR":
                    // CAPALLBT leads here and in the marathon below, as it already does in every
                    // other energy list. BT caps are fixed within a run (they only shrink at rebirth),
                    // but tiers unlock mid-run (previous tier at 5000 x id) and a short hour-0 pool
                    // leaves partial caps -- so a segment without the token never tops either up, and
                    // a run that STARTS in one trains nothing at all (user-reported 2026-10-09).
                    if (e) { list.Add("CAPALLBT"); list.Add("CAPALLAT"); list.Add("CAPWAN:40"); list.Add("BESTAUG"); }
                    else { list.Add("CAPTM:5"); list.Add("CAPWAN:40"); }
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    break;
                default:
                    // NGU MARATHON — hot NGU lanes get their full equal shares (the old plain
                    // BESTAUG/CAPALLAT here stole equal shares from them; augs/AT have their hours).
                    if (e) list.Add("CAPALLBT");   // see AT HOUR: saturated slots drop out, so this costs the NGUs nothing
                    list.Add("CAPTM:5");
                    list.Add("CAPWAN:60");
                    foreach (var t in ngus) if (!list.Contains(t)) list.Add(t);
                    // ⚠ THE "BR IS SELF-LIMITING" PREMISE BELOW WAS FALSE FOR BR, and it cost the
                    // marathon most of its magic (user-reported 2026-07-31: "the blood advisor is
                    // pulling almost all of the magic while starving NGUs").
                    //   BR.CalculateMaxAllocation caps a ritual at capValue ONLY while
                    //   remaining > capValue. Once remaining <= capValue — the normal case for any
                    //   high-index ritual, whose capValue is a whole one-tick completion — it returns
                    //   ≈remaining, i.e. the entire pool handed to one ritual. CastRituals also walks
                    //   ritual.Count-1 downwards, so the most expensive ritual goes first.
                    // Combined with plain "BR-30" being NON-cap, that did two things: it inflated the
                    // equal-share divisor (every hot NGU lane lost a third of its ceiling), and, as the
                    // LAST non-cap token, it hit prioCount == 1 → MaxAllocation = the whole idle pool,
                    // draining the surplus the CAPNGU absorbers below exist to catch. Measured: rituals
                    // funded with 245B magic during an NGU MARATHON.
                    //
                    // Now CAP-with-a-percent, which fixes both at once: CAP keeps it out of the divisor
                    // (PerformSwap counts only !IsCap), and the percent bounds it to a slice of the
                    // surplus instead of all of it.
                    //
                    // THE SECONDS GATE SCALES WITH THE PERCENT, and must. BR's Index is secondsToRun:
                    // CastRituals skips any ritual whose RitualTimeLeft(i, allocationLeft) exceeds it.
                    // RitualTimeLeft is computed against the share, so a 10× smaller share makes every
                    // ritual take ~10× longer — leaving the gate at 30 would skip everything, BR would
                    // allocate nothing, Allocate() would return false and PerformSwap would DROP the
                    // lane, taking blood income to zero. 300 ≈ 30 × (1 / 0.10) keeps the same rituals
                    // eligible. Tripwire if this is ever retuned: BR logs "allocated NOTHING" (BR.cs).
                    //
                    // AT and the aug pair below ARE genuinely self-limiting (CalculateATCap /
                    // CalculateAugCap size by formula/ceil(formula/MaxAllocation), and CAPALLAT
                    // waterfills across its slots), so the original reasoning still stands for them.
                    //
                    // SELF-LIMITING LANES BEFORE THE ABSORBERS (rituals, AT, the aug pair). Each one
                    // takes only what it actually NEEDS, never the pool: CAPALLAT and
                    // CAPBESTAUG both size by the formula/ceil(formula/MaxAllocation) divisor
                    // (CalculateATCap / CalculateAugCap), and CAPALLAT additionally waterfills across
                    // its slots (GroupShare — "a slot never gets more than its need"). So putting them
                    // first costs the absorbers below only the real demand, and they still mop up the
                    // genuine remainder — the 4.7B-idle fix is untouched.
                    //
                    // Ordering them AFTER the absorbers starved all three to exactly zero. An absorber
                    // is CAP-with-no-percent: MaxAllocation = min(cur, idle) = everything left, and
                    // unlike AT/augs an NGU lane's cap is large enough to actually drink it (that is
                    // the whole point of the 2026-07-11 change). Downstream lanes then saw idle 0.
                    // BR and CAPBESTAUG return false when they allocate nothing, so PerformSwap DROPPED
                    // them from the retry list without a word; CAPALLAT returns true and silently
                    // no-opped. Blood income was zero for the whole marathon (rebirthPower frozen) and
                    // AT/augs were fed nothing — while TM/AT/RECOVERY, which emit no absorbers, kept
                    // working, which is what made all of it look intermittent rather than broken.
                    //
                    // The old "value order" comment here (warm NGU lanes > AT > augs) described an
                    // intent the allocator could not deliver: an absorber that takes everything leaves
                    // nothing to order, so those two tokens were dead code.
                    if (rituals) list.Add(MarathonRitual);
                    // BOUNDED SPLIT (user pick 2026-07-16). Unbounded, AT/augs take their full need
                    // here and the warm lanes get only what's left; bounded, both get a defined slice.
                    // Hot lanes are unaffected either way — they allocate above this and CAP tokens are
                    // out of their equal-share divisor (PerformSwap counts only !IsCap) — so the slice
                    // is carved from the SURPLUS the hot lanes physically cannot drink, not from the
                    // growth the marathon is for.
                    //
                    // PERCENT SEMANTICS, because they differ per token and the difference is a trap:
                    //   CAPALLAT:10  -> ALLAT expands to FIVE AdvancedTrainingBPs and each one carries
                    //                   the percent, so this is 10% of curEnergy PER SLOT, not 10% for
                    //                   the group. In practice only Power/Toughness are hungry (the AT
                    //                   purpose caps hold the other three near zero cost), so it lands
                    //                   around 20% total. A flat :20 here would have meant 20% EACH —
                    //                   up to the whole pool.
                    //   CAPBESTAUG:10 -> one BestAug BP, so 10% is a true total.
                    // Percent is of cur, not idle. Both still self-limit to actual need (CalculateATCap
                    // / CalculateAugCap) and to whatever is idle when they run, so these are ceilings on
                    // the tail rather than budgets they always claim. Tune here.
                    if (e) { list.Add("CAPALLAT:10"); list.Add("CAPBESTAUG:10"); }
                    // SURPLUS ABSORBERS (user 2026-07-11: 4.7B of a 5.4B pool sat idle once the
                    // hot lanes took their caps — the game hard-caps each NGU at ONE level per
                    // tick, decomp NGUController.updateNGU resets progress to 0 on level-up, so
                    // hot lanes physically cannot drink more). Every absorber is CAP-type: out of
                    // the equal-share divisor, fed strictly from what everything above leaves behind.
                    foreach (var t in ChapterNgusSurplus(type)) if (!list.Contains(t)) list.Add(t);
                    break;
            }

            // Rituals cost magic that would otherwise be NGU growth, so run them ONLY while blood is
            // ACTUALLY being converted into something: a sink the blood advisor has routed on
            // (number/loot/gold) or an Iron Pill worth pursuing. When the routing is idle AND the pill
            // isn't worth it, drop rituals so that magic feeds the current segment (e.g. the marathon's
            // NGUs) instead of pooling blood that never gets cast. Applies to EVERY segment now — this
            // was NGU-MARATHON-only, which left TM/AT/recovery pooling unconditionally.
            //
            // BloodPlanner owns this answer. This used to read the live auto-spell toggles directly,
            // which made the profile depend on an OUTPUT of ApplyBlood (60s-throttled) — the two
            // advisors talking through mutated game state. BloodMatters() returns the routing INTENT
            // when the advisor owns blood, and falls back to the live toggles when it doesn't (manual /
            // AutoSpellSwap), so every quadrant of advisor-on/off answers from one place.
            // (Caveat: the gold-spell "want" reads live blood/sec, so a cold-start gold-starve can't
            // bootstrap rituals from zero — a pre-existing limit; NUMBER no longer has it, since it is
            // the default sink and needs no income to become eligible.)
            // Segments other than the marathon emit no surplus absorbers, so plain BR-30 is safe there:
            // with nothing below it to starve, taking the remainder is the intent, and those segments
            // are not the ones whose stated goal is NGU growth.
            //
            // The marathon uses MarathonRitual and the three stripped programs use SegmentRitual, so
            // this guard MUST test for all three spellings. Testing only "BR-30" would find the other
            // token absent and append a SECOND blood lane — a duplicate destination that both
            // allocators would then feed, which is worse than the bug this replaced.
            if (rituals && !list.Contains("BR-30") && !list.Contains(MarathonRitual)
                && !list.Contains(SegmentRitual)) list.Add("BR-30");
            return list.ToArray();
        }

        private static bool IsLscSetupLane(ResourceBreakpoint bp) =>
            bp is BasicTrainingBP || bp is TimeMachineBP || bp is WandoosBP;

        private static List<ResourceBreakpoint> AutoGenerated(ResourceType type)
        {
            var tokens = AutoTokens(type);
            if (tokens.Length == 0) return new List<ResourceBreakpoint>();
            // The NGU picks vary with live value math — the token list itself is the cache key.
            string key = $"auto|{Segment}|{Chapter()}|{type}|{string.Join(",", tokens)}";
            if (_templateParsed.Count > 64) _templateParsed.Clear();   // bound the variant cache
            var list = WithAnchorAbsentSink(ParsedList(key, tokens, type), type);
            if (list.Count > 0 && (!_lastGenKey.TryGetValue(type, out var last) || last != key))
            {
                _lastGenKey[type] = key;
                // Print the TOKENS, not just the status. This line fires precisely when the allocation
                // changes, and it used to report only segment/phase/TM/number — telling you THAT the
                // profile changed while never saying what it became. The allocation was unauditable
                // from the log: the same blind spot that let BR-30 sit starved behind the absorbers,
                // and that hid AT/augs getting nothing. The token order IS the allocation.
                Record($"{type} → auto profile", $"{AutoStatus()} · {string.Join(" → ", tokens)}");
            }
            return list;
        }

        // Per-pool latch for the line below — the sink's arrival and departure are STATE CHANGES and
        // get one line each, never a per-tick repeat (spec §3.4, the _templateOn/_fallbackOn shape).
        private static readonly Dictionary<ResourceType, bool> _anchorSinkOn =
            new Dictionary<ResourceType, bool>();

        // THE ANCHOR-ABSENT SURPLUS SINK, wired. The decision itself is
        // ConstraintLayer.AnchorAbsentSink — read its header for why this is conditional, for the
        // 1,758,030,099,891 / 15 arithmetic that proves the sink sits INSIDE the fill's divisor, and
        // for the no-flap proof. This method is only the two membership reads it needs and the seat.
        //
        // ⚠ IT RUNS HERE, AFTER ParsedList, AND THAT IS THE POINT. ParsedList has just applied
        // IsValid() — correctResourceType && Unlocked() && !TargetMet() — so "the anchor is not
        // absorbing" is already decided and is simply the anchor's ABSENCE from `list`. Asking the
        // question in AutoTokens instead would mean re-deriving the No Augs lock and the seven pair
        // targets from live state, a second copy of a predicate the lane already owns.
        //
        // ⚠ AND THE SINK IS SUBJECT TO THE SAME IsValid() AS EVERYTHING ELSE. ParsedList filters the
        // WAN lane too, so a Wandoos that is re-locked (Evil re-locks it) or at target yields NOTHING
        // and the list goes back unchanged — a segment with no valid sink available is left exactly
        // as it was rather than handed an empty lane.
        //
        // ⚠ AN EMPTY LIST IS LEFT EMPTY. AutoGenerated's caller falls back to the operator's own
        // profile on `gen.Count == 0` (TransformPriorities:322); returning a Wandoos-only list would
        // silently take that fallback away, which is a different decision than this one.
        private static List<ResourceBreakpoint> WithAnchorAbsentSink(List<ResourceBreakpoint> list, ResourceType type)
        {
            try
            {
                if (list == null || list.Count == 0) return list;
                bool energy = type == ResourceType.Energy;
                var anchor = ConstraintLayer.SinkAnchorFor(Segment, energy);
                if (anchor == null) return list;      // segment not on the allowlist — no-op

                // Named by GetType().Name, exactly as ConstraintLayerBridge.BuildSpec keys a lane,
                // so the table and the membership cannot drift over what "BestAug" means.
                bool anchorSeated = list.Any(x => x != null &&
                    string.Equals(x.GetType().Name, anchor, StringComparison.Ordinal));
                bool sinkSeated = list.Any(x => x is WandoosBP);

                var d = ConstraintLayer.AnchorAbsentSink(Segment, energy, anchorSeated, sinkSeated);
                if (!d.Seat)
                {
                    if (_anchorSinkOn.TryGetValue(type, out var was) && was)
                    {
                        _anchorSinkOn[type] = false;
                        Record($"{type} → {anchor} back, surplus sink released",
                            $"{Segment}: the anchor is absorbing again — Wandoos dropped");
                    }
                    return list;
                }

                var wan = ParsedList($"anchorsink|{type}", new[] { "WAN" }, type);
                if (wan.Count == 0) return list;      // Wandoos locked or at target — nothing to seat

                if (!_anchorSinkOn.TryGetValue(type, out var on) || !on)
                {
                    _anchorSinkOn[type] = true;
                    Record($"{type} → surplus sink seated", d.Reason);
                }

                // LAST. The sink is never handed to FillSession.Offer in any round, so its list
                // position decides nothing — appending keeps every other lane's order byte-identical.
                var widened = new List<ResourceBreakpoint>(list);
                widened.AddRange(wan);
                return widened;
            }
            catch (Exception e)
            {
                Main.LogDebug($"WithAnchorAbsentSink: {e.Message}");
                return list;
            }
        }

        // ---- Block model: every challenge with live completions, in game-menu order. ----
        public class Entry
        {
            public string Code;
            public int Cur;
            public int Max;
            public string StripNote;
        }

        private static readonly string[][] Defs =
        {
            new[] { "BASIC", "standard rules" },
            // D1 REVERSED (amendment 30): the advisor honours the No Augs challenge, so this note
            // returns to the same form its NONGU/NOTM siblings use. Under D1 it briefly read
            // "augments keep funding (UI-only lock — see log)"; that is no longer what happens.
            new[] { "NOAUG", "will strip augment priorities" },
            new[] { "24HR", "gear rotation: push/growth" },
            new[] { "100LC", "gear rotation: push/growth" },
            new[] { "NOEC", "no gear churn (nothing to wear)" },
            new[] { "TC", "keeps ALLBT guard" },
            new[] { "NORB", "single run — no rebirth spells" },
            new[] { "LSC", "gear rotation: push/growth" },
            new[] { "BLIND", "standard rules (numbers hidden)" },
            new[] { "NONGU", "will strip NGU priorities" },
            new[] { "NOTM", "will strip TM priorities; zone snipe carries gold" },
        };

        // Completions come from the CONTROLLERS (Character.allChallenges), not the serialized
        // Character.challenges objects: there, maxCompletions is [NonSerialized] and nothing ever
        // assigns it, so every Max read 0 — and ChallengesPanel filters its completed chips and its
        // queue on Max > 0, leaving both permanently empty. The controllers also expose
        // currentCompletions(), i.e. the counter for the difficulty actually being played.
        public static List<Entry> Block()
        {
            var list = new List<Entry>();
            try
            {
                var ac = Main.Character?.allChallenges;
                if (ac == null) return list;
                foreach (var d in Defs)
                {
                    int cur, max;
                    switch (d[0])
                    {
                        case "BASIC": cur = ac.basicChallenge.currentCompletions(); max = ac.basicChallenge.maxCompletions; break;
                        case "NOAUG": cur = ac.noAugsChallenge.currentCompletions(); max = ac.noAugsChallenge.maxCompletions; break;
                        case "24HR": cur = ac.hour24Challenge.currentCompletions(); max = ac.hour24Challenge.maxCompletions; break;
                        case "100LC": cur = ac.level100Challenge.currentCompletions(); max = ac.level100Challenge.maxCompletions; break;
                        case "NOEC": cur = ac.noEquipmentChallenge.currentCompletions(); max = ac.noEquipmentChallenge.maxCompletions; break;
                        case "TC": cur = ac.trollChallenge.currentCompletions(); max = ac.trollChallenge.maxCompletions; break;
                        case "NORB": cur = ac.noRebirthChallenge.currentCompletions(); max = ac.noRebirthChallenge.maxCompletions; break;
                        case "LSC": cur = ac.laserSwordChallenge.currentCompletions(); max = ac.laserSwordChallenge.maxCompletions; break;
                        case "BLIND": cur = ac.blindChallenge.currentCompletions(); max = ac.blindChallenge.maxCompletions; break;
                        case "NONGU": cur = ac.NGUChallenge.currentCompletions(); max = ac.NGUChallenge.maxCompletions; break;
                        case "NOTM": cur = ac.timeMachineChallenge.currentCompletions(); max = ac.timeMachineChallenge.maxCompletions; break;
                        default: continue;
                    }
                    list.Add(new Entry { Code = d[0], Cur = cur, Max = max, StripNote = d[1] });
                }
            }
            catch (Exception e) { Main.LogDebug($"ChallengeOverlay block: {e.Message}"); }
            return list;
        }
    }
}
