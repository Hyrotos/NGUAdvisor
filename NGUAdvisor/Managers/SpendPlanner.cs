using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // Guide-ordered spend planner for ITOPOD perks (PP), Beast quirks (QP) and Yggdrasil fruit tiers
    // (seeds), following the community guide's chapter recommendations (sayolove.github.io/ngu-guide,
    // chapters 2-5; orders recorded in docs/NGU-KNOWLEDGE.md). Each plan is an ordered list of steps;
    // the next buy is the FIRST step that is unlocked, below target, and allowed at the current
    // difficulty. Names are matched against the game's LIVE name lists (exact first, contains as a
    // fallback) so ID drift between game versions can't mis-buy. Levels/costs/points are all live reads.
    // The Evil-and-beyond plans are intentionally partial: the guide's Evil naming ("Fib 1", "EM 2")
    // needs in-game verification when the user reaches those chapters.
    public static class SpendPlanner
    {
        public struct Buy
        {
            public bool Known;
            public int Id;
            public string Name;
            public long CurLevel;
            public long TargetLevel;   // level/tier the plan wants
            public long Cost;          // cost of the NEXT single level/tier
            public bool Affordable;
        }

        // Full-plan view for the companion Perks & Quirks page (NextPerk/NextQuirk give only the single
        // next buy). Each step carries per-step status so the UI can render the whole ordered plan.
        public struct PlanStep
        {
            public int Id;
            public string Name;
            public long CurLevel;
            public long Target;
            public long Cost;
            public int MinChapter;
            public string State;   // "done" | "current" | "queued" | "chapter" | "diff"
        }

        private struct Step
        {
            public string Match;    // matched against live name list
            public long Target;     // 0 = max level (perks/quirks); tier for fruits
            public int MinChapter;  // step ignored before this chapter
            // Fruits only: tier cap the step needs (0 = ungated). The guide schedules the tier-24
            // push for ch4, but the GAME gate is AllYggdrasil.capTier() -- 10 until Troll Challenge
            // 3x, then 24. Those come apart (cap earned before T6), and gating on the chapter alone
            // stalled the plan with seeds banked and nowhere to spend them.
            public int MinCap;
            public Step(string m, long t, int ch, int minCap = 0) { Match = m; Target = t; MinChapter = ch; MinCap = minCap; }
        }

        // ---- ITOPOD perk order (guide ch2-4; ch5+ partial) ----
        private static readonly Step[] PerkPlan =
        {
            new Step("The Newbie Energy Perk", 0, 2),
            new Step("The Newbie Magic Perk", 0, 2),
            new Step("The Newbie Adventure Perk", 0, 2),
            new Step("The Newbie Drop Chance Perk", 0, 2),
            new Step("The Newbie Stat Perk", 0, 2),
            new Step("Instant Advanced Training Levels", 2, 2),  // guide ch2: 2x after the Newbies
            new Step("Generic Energy Power Perk I", 0, 2),       // guide ch2: alternate with Cap I
            new Step("Generic Energy Cap Perk I", 0, 2),
            new Step("Bonus Titan EXP!", 1, 3),          // 1 level: online-AK EXP bonus
            new Step("What a Crappy Perk", 0, 3),
            new Step("A Digger Slot!", 0, 3),
            new Step("Boosted Boosts I", 10, 3),
            new Step("Faster NGU Energy", 0, 3),
            new Step("I want your seeds ;)", 0, 3),      // post-CBlock1 Yggdrasil block
            new Step("The First Harvest's The Best", 0, 3),
            new Step("\"Fruit of Knowledge sucks 1/5\"", 0, 3),
            new Step("\"Fruit of Knowledge STILL sucks 1/5\"", 0, 3),
            new Step("Generic Magic Power Perk I", 0, 4),
            new Step("Generic Magic Cap Perk I", 0, 4),
            new Step("Faster NGU Magic", 0, 4),
            new Step("Boosted Boosts I", 0, 4),          // finish to max
            new Step("Generic Energy Bar Perk I", 0, 4),
            new Step("Generic Magic Bar Perk I", 0, 4),
            new Step("Adv. Training Level Bank I", 0, 4),
            new Step("Adv. Training Level Bank II", 0, 4),
            new Step("Beard Temp Level Bank I", 0, 4),
            new Step("Beard Temp Level Bank II", 0, 4),
            new Step("Time Machine Level Bank I", 0, 4),
            new Step("Time Machine Level Bank II", 0, 4),
            new Step("More Inventory Space I", 0, 4),
            new Step("More Inventory Space II", 0, 4),
            new Step("Wandoos Lover", 0, 4),
            new Step("Bonus Boss EXP!", 0, 4),
            new Step("Boosted Boosts II", 0, 4),
            new Step("Adv. Training Level Bank III", 0, 5),   // guide ch5: "Beard / AT Banks 3+4"
            new Step("Adv. Training Level Bank IV", 0, 5),
            new Step("Beard Temp Level Bank III", 0, 5),
            new Step("Beard Temp Level Bank IV", 0, 5),
            new Step("Time Machine Level Bank III", 0, 5),
            new Step("Time Machine Level Bank IV", 0, 5),
            // guide ch5 continues (sayolove ch5 → NGU-KNOWLEDGE.md): Fib 1 → EM Pow/Cap 2 (L10) → Fib 3 →
            // Banks 5 → Fib 34 → finish EM → Energy Bars 2 → Magic Bars 2. (Names verified vs Blaze Rkkz.)
            new Step("Fibonacci Perk", 1, 5),                 // "Fib 1"
            new Step("Generic Energy Power Perk II", 10, 5),  // "EM Pow/Cap 2 → L10"
            new Step("Generic Energy Cap Perk II", 10, 5),
            new Step("Generic Magic Power Perk II", 10, 5),
            new Step("Generic Magic Cap Perk II", 10, 5),
            new Step("Fibonacci Perk", 3, 5),                 // "Fib 3"
            new Step("Adv. Training Level Bank V", 0, 5),      // "Beard / AT Banks 5"
            new Step("Beard Temp Level Bank V", 0, 5),
            new Step("Time Machine Level Bank V", 0, 5),
            new Step("Fibonacci Perk", 34, 5),                // "Fib 34"
            new Step("Generic Energy Power Perk II", 0, 5),   // "finish EM" (to max)
            new Step("Generic Energy Cap Perk II", 0, 5),
            new Step("Generic Magic Power Perk II", 0, 5),
            new Step("Generic Magic Cap Perk II", 0, 5),
            new Step("Generic Energy Bar Perk II", 0, 5),     // "Energy Bars 2"
            new Step("Generic Magic Bar Perk II", 0, 5),      // "Magic Bars 2 when cheap"
            // Boss-gated ch5 perks (user-confirmed names) placed LAST so a still-locked one never stalls
            // the earlier plan (there's no perk boss-req field to guard on): unlock at Boss 125 / 150.
            new Step("Welcome to Evil Difficulty", 0, 5),         // guide "Welcome to Evil" (unlocks B125)
            new Step("Adventure Boost for Rich Perks III", 0, 5), // guide "Adventure Perk until T8" (B150)
        };

        // ---- Beast quirk order (guide ch4 + ch5 partial) ----
        private static readonly Step[] QuirkPlan =
        {
            new Step("Baby's First Quirk: Adventure", 0, 4),  // guide ch4: 25% adventure for 300 QP
            new Step("Baby's First Quirk: Energy Power", 0, 5),
            new Step("Baby's First Quirk: Energy Cap", 0, 5),
            new Step("Baby's First Quirk: Energy Bars", 0, 5),
            new Step("Baby's First Quirk: Magic Power", 0, 5),
            new Step("Baby's First Quirk: Magic Cap", 0, 5),
            new Step("Baby's First Quirk: Magic Bars", 0, 5),
            new Step("Adv. Training Level Bank I", 0, 5),     // guide ch5: "Beard / AT Banks 1"
            new Step("Beard Temp Level Bank I", 0, 5),
            new Step("Beasted Boosts I", 0, 5),
            new Step("Beasted Boosts II", 0, 5),              // guide ch5: "Beasted Boosts 2"
        };

        // ---- Yggdrasil fruit tier order (guide ch3 + ch4 sequence, ch5 Evil order, ch7 mayo) ----
        private static readonly Step[] FruitPlan =
        {
            new Step("Gold", 10, 3),
            new Step("Pomegranate", 5, 3),
            new Step("Knowledge", 1, 3),
            new Step("Luck", 1, 3),
            new Step("Pomegranate", 10, 3),
            new Step("Luck", 5, 3),
            new Step("Gold", 24, 3, 24),          // post-TC3 (cap 24): FoG -> Pom -> FoK -> FoL ->
            new Step("Pomegranate", 24, 3, 24),   // FoPa/FoA -> FoAP -> FoPb/FoN -> FoR
            new Step("Knowledge", 24, 3, 24),
            new Step("Luck", 24, 3, 24),
            new Step("Power α", 24, 3, 24),
            new Step("Adventure", 24, 3, 24),
            new Step("Arbitrariness", 24, 3, 24),
            new Step("Power β", 24, 3, 24),
            new Step("Numbers", 24, 3, 24),
            new Step("Rage", 24, 3, 24),
            // guide ch5 "Yggdrasil Order": early Evil, then mid-late Evil.
            new Step("Fruit of MacGuffin α", 12, 5, 24),
            new Step("Fruit of MacGuffin β", 1, 5, 24),
            new Step("Fruit of Quirks", 4, 5, 24),
            new Step("Watermelon", 24, 5, 24),
            new Step("Fruit of Quirks", 12, 5, 24),
            new Step("Fruit of MacGuffin α", 24, 5, 24),
            new Step("Fruit of Quirks", 24, 5, 24),
            new Step("Fruit of Power δ", 24, 5, 24),
            new Step("Fruit of MacGuffin β", 24, 5, 24),
            // guide ch7: "Finish last non-mayo fruits, then level mayo fruits equally" — in rungs of 6.
            new Step("Fruit of Angry Mayo", 6, 7, 24), new Step("Fruit of Sad Mayo", 6, 7, 24),
            new Step("Fruit of Moldy Mayo", 6, 7, 24), new Step("Fruit of Ayy Lmayo", 6, 7, 24),
            new Step("Fruit of Cinco De Mayo", 6, 7, 24), new Step("Fruit of Pretty Mayo", 6, 7, 24),
            new Step("Fruit of Angry Mayo", 12, 7, 24), new Step("Fruit of Sad Mayo", 12, 7, 24),
            new Step("Fruit of Moldy Mayo", 12, 7, 24), new Step("Fruit of Ayy Lmayo", 12, 7, 24),
            new Step("Fruit of Cinco De Mayo", 12, 7, 24), new Step("Fruit of Pretty Mayo", 12, 7, 24),
            new Step("Fruit of Angry Mayo", 18, 7, 24), new Step("Fruit of Sad Mayo", 18, 7, 24),
            new Step("Fruit of Moldy Mayo", 18, 7, 24), new Step("Fruit of Ayy Lmayo", 18, 7, 24),
            new Step("Fruit of Cinco De Mayo", 18, 7, 24), new Step("Fruit of Pretty Mayo", 18, 7, 24),
            new Step("Fruit of Angry Mayo", 24, 7, 24), new Step("Fruit of Sad Mayo", 24, 7, 24),
            new Step("Fruit of Moldy Mayo", 24, 7, 24), new Step("Fruit of Ayy Lmayo", 24, 7, 24),
            new Step("Fruit of Cinco De Mayo", 24, 7, 24), new Step("Fruit of Pretty Mayo", 24, 7, 24),
        };

        // 0 = stage unknown: every chapter-gated step then skips, so NextPerk/NextQuirk go un-Known
        // and NOTHING is bought — the planned-buy fallback still names what's next. (The old
        // "unknown = chapter 1" default made a transient detection failure read as "plan complete".)
        private static int Chapter()
        {
            try { var p = ProgressionAnalyzer.Detect(); return p.Known ? p.Chapter : 0; }
            catch { return 0; }
        }

        // A plan step whose name doesn't resolve is SKIPPED — silently, it looks like "plan
        // complete" (user-reported: P&Q said end-of-guide while the guide's chapter list had buys
        // left). Log each miss once so a name drift is visible in debug.log instead of invisible.
        private static readonly HashSet<string> _reportedMisses = new HashSet<string>();

        private static int FindByName(List<string> names, string match, string kind = null)
        {
            int found = FindByNameCore(names, match);
            if (found < 0 && kind != null && _reportedMisses.Add(kind + "|" + match))
                Main.LogDebug($"SpendPlanner: {kind} step '{match}' not found in the game's name list — step skipped (name drift?)");
            return found;
        }

        private static int FindByNameCore(List<string> names, string match)
        {
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i]?.Trim(), match, StringComparison.OrdinalIgnoreCase)) return i;
            // fallback 1: punctuation-insensitive exact (live names quote/punctuate differently than
            // the community catalogs — debug.log caught the quoted '"Fruit of Knowledge sucks 1/5"'
            // steps never resolving). Unique required, refuse ambiguity rather than mis-buy.
            string normMatch = Normalize(match);
            int found = -1;
            if (normMatch.Length > 0)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (names[i] == null || Normalize(names[i]) != normMatch) continue;
                    if (found >= 0) { found = -1; break; }
                    found = i;
                }
                if (found >= 0) return found;
            }
            // fallback 2: unique contains
            found = -1;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] == null || names[i].IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (found >= 0) return -1;   // ambiguous — refuse rather than mis-buy
                found = i;
            }
            return found;
        }

        // Lowercase letters/digits only — quotes, punctuation and spacing drift can't break a match.
        private static string Normalize(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        // ---------- PERKS ----------

        public static Buy NextPerk()
        {
            var b = new Buy();
            try
            {
                var c = Main.Character;
                if (c == null) return b;
                var ipc = c.adventureController.itopod;
                var levels = c.adventure.itopod.perkLevel;
                int chapter = Chapter();
                var diff = c.settings.rebirthDifficulty;

                foreach (var step in PerkPlan)
                {
                    if (chapter < step.MinChapter) continue;
                    int id = FindByName(ipc.perkName, step.Match, "perk");
                    if (id < 0 || id >= levels.Count || id >= ipc.maxLevel.Count) continue;
                    if (ipc.perkDifficultyReq[id] > diff) continue;
                    long max = ipc.maxLevel[id] > 0 ? ipc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    if (levels[id] >= target) continue;

                    b.Known = true; b.Id = id; b.Name = ipc.perkName[id];
                    b.CurLevel = levels[id]; b.TargetLevel = target;
                    b.Cost = ipc.perkCost(id);
                    b.Affordable = c.adventure.itopod.perkPoints >= b.Cost;
                    return b;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner perks: {e.Message}"); }
            return b;
        }

        // The whole ITOPOD perk plan with per-step status, for the P&Q page. "current" = the step
        // NextPerk() would buy now; "chapter"/"diff" = queued but gated; "done" = at/above target.
        public static List<PlanStep> PerkPlanView()
        {
            var outp = new List<PlanStep>();
            try
            {
                var c = Main.Character;
                if (c == null) return outp;
                var ipc = c.adventureController.itopod;
                var levels = c.adventure.itopod.perkLevel;
                if (ipc == null || levels == null) return outp;
                int chapter = Chapter();
                var diff = c.settings.rebirthDifficulty;
                bool currentSet = false;
                foreach (var step in PerkPlan)
                {
                    int id = FindByName(ipc.perkName, step.Match, "perk");
                    if (id < 0 || id >= levels.Count || id >= ipc.maxLevel.Count) continue;
                    long max = ipc.maxLevel[id] > 0 ? ipc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    var ps = new PlanStep { Id = id, Name = ipc.perkName[id]?.Trim(), CurLevel = levels[id], Target = target, MinChapter = step.MinChapter };
                    try { ps.Cost = ipc.perkCost(id); } catch { }
                    if (levels[id] >= target) ps.State = "done";
                    else if (chapter < step.MinChapter) ps.State = "chapter";
                    else if (id < ipc.perkDifficultyReq.Count && ipc.perkDifficultyReq[id] > diff) ps.State = "diff";
                    else if (!currentSet) { ps.State = "current"; currentSet = true; }
                    else ps.State = "queued";
                    outp.Add(ps);
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner perk view: {e.Message}"); }
            return outp;
        }

        // The first perk buy the guide still has QUEUED but which is gated by chapter or difficulty
        // — what banked PP is FOR (mirrors NextQuirkPlanned; user-reported: NextPerk()=unknown
        // surfaced as "plan complete" while later-chapter steps were still queued).
        public static PlannedBuy NextPerkPlanned()
        {
            var f = new PlannedBuy();
            try
            {
                var c = Main.Character;
                if (c == null) return f;
                var ipc = c.adventureController.itopod;
                var levels = c.adventure.itopod.perkLevel;
                if (levels == null) return f;
                var diff = c.settings.rebirthDifficulty;

                foreach (var step in PerkPlan)
                {
                    int id = FindByName(ipc.perkName, step.Match, "perk");
                    if (id < 0 || id >= levels.Count || id >= ipc.maxLevel.Count) continue;
                    long max = ipc.maxLevel[id] > 0 ? ipc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    if (levels[id] >= target) continue;

                    f.Known = true;
                    f.Name = ipc.perkName[id]?.Trim();
                    f.Cost = ipc.perkCost(id);
                    f.MinChapter = step.MinChapter;
                    f.DifficultyGated = ipc.perkDifficultyReq[id] > diff;
                    return f;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner planned perk: {e.Message}"); }
            return f;
        }

        // Buys toward the current perk step, up to maxBuys levels per call. Mirrors the game's own
        // per-click buy path ItopodPerkController.doLevelUp(id): deduct perkPoints, increment
        // perkLevel[id], then doEffect(id) (the derived-stat recompute). The ONLY things doLevelUp does
        // that we deliberately skip are its three UI-refresh calls -- showTooltip(id), updateText(),
        // changePage(page) -- none of which touch game state; skipping them avoids UI churn/redraw on the
        // advisor loop. There are no achievement or unlock hooks in that path. (Verified vs Assembly-CSharp.)
        public static int BuyPerks(int maxBuys)
        {
            int bought = 0;
            try
            {
                var c = Main.Character;
                if (c == null) return 0;
                var ipc = c.adventureController.itopod;
                for (; bought < maxBuys; )
                {
                    var b = NextPerk();
                    if (!b.Known || !b.Affordable) break;
                    c.adventure.itopod.perkPoints -= ipc.perkCost(b.Id);
                    c.adventure.itopod.perkLevel[b.Id]++;
                    ipc.doEffect(b.Id);
                    bought++;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner buy perks: {e.Message}"); }
            return bought;
        }

        // ---------- QUIRKS ----------

        public static Buy NextQuirk()
        {
            var b = new Buy();
            try
            {
                var c = Main.Character;
                if (c == null) return b;
                var qc = c.beastQuestPerkController;
                var levels = c.beastQuest.quirkLevel;
                if (qc == null || levels == null) return b;
                int chapter = Chapter();
                var diff = c.settings.rebirthDifficulty;

                foreach (var step in QuirkPlan)
                {
                    if (chapter < step.MinChapter) continue;
                    int id = FindByName(qc.quirkName, step.Match, "quirk");
                    if (id < 0 || id >= levels.Count || id >= qc.maxLevel.Count) continue;
                    if (qc.quirkDifficultyReq[id] > diff) continue;
                    long max = qc.maxLevel[id] > 0 ? qc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    if (levels[id] >= target) continue;

                    b.Known = true; b.Id = id; b.Name = qc.quirkName[id];
                    b.CurLevel = levels[id]; b.TargetLevel = target;
                    b.Cost = qc.quirkCost(id);
                    b.Affordable = c.beastQuest.quirkPoints >= b.Cost;
                    return b;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner quirks: {e.Message}"); }
            return b;
        }

        // The whole Beast quirk plan with per-step status, for the P&Q page (mirror of PerkPlanView).
        public static List<PlanStep> QuirkPlanView()
        {
            var outp = new List<PlanStep>();
            try
            {
                var c = Main.Character;
                if (c == null) return outp;
                var qc = c.beastQuestPerkController;
                var levels = c.beastQuest.quirkLevel;
                if (qc == null || levels == null) return outp;
                int chapter = Chapter();
                var diff = c.settings.rebirthDifficulty;
                bool currentSet = false;
                foreach (var step in QuirkPlan)
                {
                    int id = FindByName(qc.quirkName, step.Match, "quirk");
                    if (id < 0 || id >= levels.Count || id >= qc.maxLevel.Count) continue;
                    long max = qc.maxLevel[id] > 0 ? qc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    var ps = new PlanStep { Id = id, Name = qc.quirkName[id]?.Trim(), CurLevel = levels[id], Target = target, MinChapter = step.MinChapter };
                    try { ps.Cost = qc.quirkCost(id); } catch { }
                    if (levels[id] >= target) ps.State = "done";
                    else if (chapter < step.MinChapter) ps.State = "chapter";
                    else if (id < qc.quirkDifficultyReq.Count && qc.quirkDifficultyReq[id] > diff) ps.State = "diff";
                    else if (!currentSet) { ps.State = "current"; currentSet = true; }
                    else ps.State = "queued";
                    outp.Add(ps);
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner quirk view: {e.Message}"); }
            return outp;
        }

        public struct PlannedBuy
        {
            public bool Known;
            public string Name;
            public long Cost;
            public int MinChapter;        // chapter the guide schedules it for
            public bool DifficultyGated;  // also needs a higher rebirth difficulty
        }

        // The first quirk buy the guide still has QUEUED but which is gated by chapter or difficulty
        // — i.e. what banked QP is FOR. User-reported: NextQuirk()=unknown used to surface as "plan
        // complete", which read as the advisor skipping quirks; on Normal the guide's only pre-Evil
        // buy is Baby's First Quirk: Adventure (ch.4), so the plan idles for whole chapters while QP
        // accumulates. This names the next scheduled buy so the advisor can say "bank for X".
        public static PlannedBuy NextQuirkPlanned()
        {
            var f = new PlannedBuy();
            try
            {
                var c = Main.Character;
                if (c == null) return f;
                var qc = c.beastQuestPerkController;
                var levels = c.beastQuest.quirkLevel;
                if (qc == null || levels == null) return f;
                var diff = c.settings.rebirthDifficulty;

                foreach (var step in QuirkPlan)
                {
                    int id = FindByName(qc.quirkName, step.Match, "quirk");
                    if (id < 0 || id >= levels.Count || id >= qc.maxLevel.Count) continue;
                    long max = qc.maxLevel[id] > 0 ? qc.maxLevel[id] : long.MaxValue;
                    long target = step.Target > 0 ? Math.Min(step.Target, max) : max;
                    if (levels[id] >= target) continue;

                    f.Known = true;
                    f.Name = qc.quirkName[id]?.Trim();   // game data has a trailing space on id 6
                    f.Cost = qc.quirkCost(id);
                    f.MinChapter = step.MinChapter;
                    f.DifficultyGated = qc.quirkDifficultyReq[id] > diff;
                    return f;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner planned quirk: {e.Message}"); }
            return f;
        }

        // Same contract as BuyPerks: mirrors BeastQuestPerkController.doLevelUp(id) -- deduct quirkPoints,
        // increment quirkLevel[id], doEffect(id) -- and skips only its UI calls (showTooltip, updateText),
        // which carry no game state.
        public static int BuyQuirks(int maxBuys)
        {
            int bought = 0;
            try
            {
                var c = Main.Character;
                if (c == null) return 0;
                var qc = c.beastQuestPerkController;
                for (; bought < maxBuys; )
                {
                    var b = NextQuirk();
                    if (!b.Known || !b.Affordable) break;
                    c.beastQuest.quirkPoints -= qc.quirkCost(b.Id);
                    c.beastQuest.quirkLevel[b.Id]++;
                    qc.doEffect(b.Id);
                    bought++;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner buy quirks: {e.Message}"); }
            return bought;
        }

        // ---------- YGGDRASIL FRUIT TIERS ----------

        public static Buy NextFruit()
        {
            var b = new Buy();
            try
            {
                var c = Main.Character;
                if (c == null) return b;
                var ycon = c.yggdrasilController;
                var fruits = c.yggdrasil.fruits;
                if (ycon == null || fruits == null) return b;
                int chapter = Chapter();
                int cap = ycon.capTier();

                foreach (var step in FruitPlan)
                {
                    if (chapter < step.MinChapter) continue;
                    if (cap < step.MinCap) continue;
                    int id = FindByName(ycon.fruitName, step.Match, "fruit");
                    if (id < 0 || id >= fruits.Count || id >= ycon.baseSeedCost.Count) continue;
                    long target = Math.Min(step.Target, cap);
                    long tier = fruits[id].maxTier;
                    if (tier >= target) continue;
                    if (tier == 0 && !CanUnlockFruit(c, ycon.fruitName[id])) continue;

                    b.Known = true; b.Id = id; b.Name = ycon.fruitName[id];
                    b.CurLevel = tier; b.TargetLevel = target;
                    b.Cost = ycon.baseSeedCost[id] * (long)Math.Ceiling(Math.Pow(tier + 1, 2));
                    b.Affordable = c.yggdrasil.seeds >= b.Cost;
                    return b;
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner fruits: {e.Message}"); }
            return b;
        }

        // The whole fruit plan with per-step status, for the Yggdrasil page (mirror of PerkPlanView).
        // Nothing is skipped: a step the chapter, an unlock or the tier cap holds back is listed with
        // that reason, so banked seeds can be read against what they are being saved for.
        public static List<PlanStep> FruitPlanView()
        {
            var outp = new List<PlanStep>();
            try
            {
                var c = Main.Character;
                if (c == null) return outp;
                var ycon = c.yggdrasilController;
                var fruits = c.yggdrasil.fruits;
                if (ycon == null || fruits == null) return outp;
                int chapter = Chapter();
                int cap = ycon.capTier();
                bool currentSet = false;
                foreach (var step in FruitPlan)
                {
                    int id = FindByName(ycon.fruitName, step.Match, "fruit");
                    if (id < 0 || id >= fruits.Count || id >= ycon.baseSeedCost.Count) continue;
                    long tier = fruits[id].maxTier;
                    var ps = new PlanStep { Id = id, Name = ycon.fruitName[id]?.Trim(), CurLevel = tier, Target = step.Target, MinChapter = step.MinChapter };
                    ps.Cost = ycon.baseSeedCost[id] * (long)Math.Ceiling(Math.Pow(tier + 1, 2));
                    ps.State = FruitPlanState.Classify(tier, step.Target, chapter, step.MinChapter, cap, step.MinCap,
                        tier > 0 || CanUnlockFruit(c, ycon.fruitName[id]), ref currentSet);
                    outp.Add(ps);
                }
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner fruit view: {e.Message}"); }
            return outp;
        }

        // Unlock gates for the special fruits (from FruitController.upgrade()'s checks; MacGuffin β has none).
        private static bool CanUnlockFruit(Character c, string name)
        {
            try
            {
                // upgrade() refuses Numbers at tier 0 outright: only Troll Challenge #5 grants it.
                if (name.IndexOf("Numbers", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                if (name.IndexOf("Rage", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c.settings.itopodOn;
                if (name.IndexOf("MacGuffin α", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c.achievements.achievementComplete[145];
                if (name.IndexOf("Quirks", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c.settings.beastOn;
                if (name.IndexOf("mayo", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c.cards.cardsOn;
                return true;
            }
            catch { return false; }
        }

        // One tier per call (tiers are chunky purchases). Mirrors FruitController.upgrade()'s state
        // mutation exactly: deduct seeds, increment fruits[id].maxTier. That game method has NO per-tier
        // doEffect; everything else it runs (unlockInfo(), updateFruitDisplay(), tooltips) is UI-only and
        // is deliberately skipped here.
        public static bool BuyFruitTier()
        {
            try
            {
                var c = Main.Character;
                var b = NextFruit();
                if (c == null || !b.Known || !b.Affordable) return false;
                c.yggdrasil.seeds -= b.Cost;
                c.yggdrasil.fruits[b.Id].maxTier++;
                return true;
            }
            catch (Exception e) { Main.LogDebug($"SpendPlanner buy fruit: {e.Message}"); return false; }
        }
    }
}
