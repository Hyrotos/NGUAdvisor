// Verification for the UI-audit fixes: P9 (binding-constraint label), P10 (activity feed restored)
// and P6 (per-section live/parked badges, including the gear gap).
// Same harness shape as tests/companion/test-loadouts.js: real index.html, faked WebView2 host,
// snapshots pushed down the MainForm path.
const fs = require("fs");
const path = require("path");
const { JSDOM, VirtualConsole } = require("jsdom");

const FILE = process.argv[2] || path.resolve(__dirname, "../../NGUAdvisorCompanion/wwwroot/index.html");
let pass = 0, fail = 0; const failures = [];
function ok(n, c, d) { if (c) pass++; else { fail++; failures.push(n + (d ? "  --> " + d : "")); } }

const html = fs.readFileSync(FILE, "utf8");
const vc = new VirtualConsole();
vc.on("jsdomError", e => console.log("JSDOM ERROR: " + e.message));
let hostListener = null;
const dom = new JSDOM(html, {
  runScripts: "dangerously", pretendToBeVisual: true, virtualConsole: vc,
  beforeParse(w) {
    w.chrome = { webview: {
      postMessage() {},
      addEventListener(t, fn) { if (t === "message") hostListener = fn; },
      removeEventListener() {}
    } };
    w.requestAnimationFrame = cb => setTimeout(() => cb(Date.now()), 0);
  }
});
const window = dom.window, document = window.document;
const $ = id => document.getElementById(id);
function send(s) { if (!hostListener) throw new Error("no host listener"); hostListener({ data: s }); }

setTimeout(() => {
  // ---------- P9: the binding side is named ----------
  send({ instruments: { titan: { known: true, name: "T7 v1", atk: 137, def: 12 } }, goal: {} });
  ok("P9 defence binds -> 'defence-gated'", /12%.*defence-gated/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T7 v1", atk: 9, def: 140 } }, goal: {} });
  ok("P9 attack binds -> 'attack-gated'", /9%.*attack-gated/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T7 v1", atk: 50, def: 50 } }, goal: {} });
  ok("P9 tie is silent (no gated label)", !/gated/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T4 v2", atk: 90, def: 95, regen: 40 } }, goal: {} });
  ok("P9 regen counted as a gate", /40%.*regen-gated/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T2 v1", atk: 30 } }, goal: {} });
  ok("P9 single gate is silent", /30%/.test($("g1val").textContent) && !/gated/.test($("g1val").textContent), $("g1val").textContent);

  // The chase version and the spawn version are different questions. They legitimately differ for
  // hours while a gold bank is pending, and the card used to publish one while the titan chip row
  // published the other, from the same snapshot, with neither admitting the other existed.
  send({ instruments: { titan: { known: true, name: "T7 v2", atk: 137, def: 12, objV: 2, spawnV: 1 } }, goal: {} });
  ok("divergence: card names the parked spawn", /spawn parked on v1/.test($("g1val").textContent), $("g1val").textContent);
  ok("divergence: still names the binding side", /defence-gated/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T7 v2", atk: 137, def: 12, objV: 2, spawnV: 2 } }, goal: {} });
  ok("agreement is silent", !/parked/.test($("g1val").textContent), $("g1val").textContent);

  send({ instruments: { titan: { known: true, name: "T7 v2", atk: 137, def: 12 } }, goal: {} });
  ok("older advisor without the keys says nothing", !/parked/.test($("g1val").textContent), $("g1val").textContent);

  // A notice that carries something to copy: the toast says so, takes the click, copies, and says
  // what happened. An ordinary notice still ignores the pointer. jsdom has no async clipboard, so
  // this exercises the execCommand fallback, which is synchronous.
  {
    const toast = $("toast");
    let copied = null, refuse = false;
    window.document.execCommand = cmd => {
      if (cmd !== "copy" || refuse) return false;
      const ta = window.document.querySelector("textarea[readonly]");
      copied = ta ? ta.value : null;
      return true;
    };
    const click = () => toast.dispatchEvent(new window.Event("click", { bubbles: true }));

    send({ notice: "Re-optimized.", goal: {} });
    ok("an ordinary notice is not clickable", !toast.classList.contains("copyable") && !/Click to copy/.test(toast.textContent), toast.textContent);
    click();
    ok("clicking an ordinary notice copies nothing", copied === null && toast.textContent === "Re-optimized.", toast.textContent);

    const path = "/home/u/pfx/drive_c/x/state-export.txt";
    send({ notice: "Game state exported to " + path, noticeCopy: path, goal: {} });
    ok("a notice with a path offers the copy", toast.classList.contains("copyable") && /Click to copy the path/.test(toast.textContent), toast.textContent);
    ok("and still shows the message", toast.textContent.indexOf("Game state exported to " + path) === 0, toast.textContent);
    click();
    ok("clicking it copies exactly the path", copied === path, String(copied));
    ok("and says so", toast.textContent === "Path copied to the clipboard." && !toast.classList.contains("copyable"), toast.textContent);
    ok("the helper textarea is cleaned up", !window.document.querySelector("textarea[readonly]"));
    copied = null; click();
    ok("the confirmation is not itself clickable", copied === null);

    refuse = true;
    send({ notice: "Game state exported to /p/state-export.txt", noticeCopy: "/p/state-export.txt", goal: {} });
    click();
    ok("a refused clipboard is reported with the path, not swallowed",
       toast.textContent === "Couldn't reach the clipboard — the path is /p/state-export.txt", toast.textContent);

    // The copy offer belongs to ITS notice: the next plain one must not inherit it.
    send({ notice: "Game state exported to /p/a.txt", noticeCopy: "/p/a.txt", goal: {} });
    send({ notice: "Something else.", goal: {} });
    ok("a later plain notice does not inherit the copy offer", !toast.classList.contains("copyable"));
  }

  // AP page: the tier list as a plan. It only advises, so it carries no control of its own; what it
  // must get right is which row is next, what it costs and how far away it is.
  {
    const list = () => $("apPlanList");
    const rows = () => Array.from(list().querySelectorAll(".planrow"));
    const tab = name => window.document.querySelector('.ptab[data-aptab="' + name + '"]')
      .dispatchEvent(new window.Event("click", { bubbles: true }));
    ok("AP sits under Resources next to EXP",
       window.document.querySelector('.navitem[data-view="exp"]').nextElementSibling.getAttribute("data-view") === "ap");
    ok("the AP page has no setting to switch", !window.document.querySelector("#view-ap .manual input"));

    send({ goal: {}, ap: { balance: "1,500", rows: [
      { name: "ILF", tier: 0, note: "", state: "done" },
      { name: "Red Heart", tier: 1, note: "If you have an open Daycare slot", state: "current", costKnown: true, cost: "25,000", affordable: false, missing: "23,500" },
      { name: "AP Beard Slots", tier: 1, note: "", state: "skip" },
      { name: "Acc slot 1", tier: 1, note: "", state: "queued", costKnown: true, cost: "1,000", affordable: true, count: "Bought: 1 / 2" },
      { name: "Digger slots", tier: 2, note: "", state: "queued", costKnown: false } ] } });

    ok("balance chip", $("apBalance").textContent === "Balance: 1,500 AP", $("apBalance").textContent);
    ok("next chip names the row and the gap", $("apNext").textContent === "Next: Red Heart · 23,500 AP short", $("apNext").textContent);
    ok("upcoming leaves the bought rows out", rows().length === 4 && !/ILF/.test(list().textContent), list().textContent);
    ok("the next row is marked and priced", rows()[0].classList.contains("current") && /25,000 AP/.test(rows()[0].textContent) && /save up/.test(rows()[0].textContent), rows()[0].textContent);
    ok("its guide note is shown under it", rows()[0].nextElementSibling.className === "plannote" && rows()[0].nextElementSibling.textContent === "If you have an open Daycare slot");
    ok("a skipped row says so and shows no price", /not needed/.test(rows()[1].textContent) && !/AP$|cost unknown/.test(rows()[1].querySelector(".pt").textContent), rows()[1].textContent);
    ok("an affordable later row is flagged, with its count", rows()[2].classList.contains("afford") && /affordable/.test(rows()[2].textContent) && /Bought: 1 \/ 2/.test(rows()[2].textContent), rows()[2].textContent);
    ok("an unreadable price is never printed as zero", rows()[3].querySelector(".pt").textContent === "cost unknown", rows()[3].textContent);
    ok("rows are grouped by tier", Array.from(list().querySelectorAll(".plantier")).map(e => e.textContent).join("|") === "TIER 1|TIER 2", list().textContent);

    tab("purchased");
    ok("purchased shows only the bought rows", rows().length === 1 && /ILF/.test(rows()[0].textContent) && /owned/.test(rows()[0].textContent), list().textContent);
    tab("upcoming");

    send({ goal: {}, ap: { balance: "30,000", rows: [
      { name: "Red Heart", tier: 1, note: "", state: "current", costKnown: true, cost: "25,000", affordable: true } ] } });
    ok("an affordable next row reads buy now", /buy now/.test(rows()[0].textContent) && $("apNext").textContent === "Next: Red Heart · affordable now" && $("apNext").classList.contains("act"), $("apNext").textContent);

    send({ goal: {}, ap: { balance: "9", rows: [ { name: "ILF", tier: 0, note: "", state: "done" } ] } });
    ok("a finished list says so", $("apNext").textContent === "Tier list complete" && /Everything on the tier list is bought/.test(list().textContent), list().textContent);
  }

  // The Titans view's heading and verdict name the rung the bars are measured against. They used to
  // say "Autokill unlocks at 100%" under bars scaled to the manual first-kill stats.
  send({ instruments: { titan: { known: true, name: "T2 v1", stage: "first kill", atk: 72, def: 34 } }, goal: {} });
  ok("first kill: heading names the rung", $("titanStageLabel").textContent === "First-kill readiness", $("titanStageLabel").textContent);
  ok("first kill: verdict does not promise autokill", /manual first kill/.test($("titanVerdict").textContent) && !/Autokill/.test($("titanVerdict").textContent), $("titanVerdict").textContent);

  send({ instruments: { titan: { known: true, name: "T2 v1", stage: "idle", atk: 72, def: 34 } }, goal: {} });
  ok("idle: heading names the rung", $("titanStageLabel").textContent === "Idle-kill readiness", $("titanStageLabel").textContent);
  ok("idle: verdict names idle kills", /^Idle kills/.test($("titanVerdict").textContent), $("titanVerdict").textContent);

  send({ instruments: { titan: { known: true, name: "T2 v1", stage: "auto-kill", atk: 72, def: 34 } }, goal: {} });
  ok("auto-kill: heading is the autokill one", $("titanStageLabel").textContent === "Autokill readiness", $("titanStageLabel").textContent);
  ok("auto-kill: verdict is the autokill one", $("titanVerdict").textContent === "Autokill unlocks at 100% of every gate shown.", $("titanVerdict").textContent);

  send({ instruments: { titan: { known: false } }, goal: {} });
  ok("no objective falls back to the autokill wording", $("titanStageLabel").textContent === "Autokill readiness", $("titanStageLabel").textContent);

  // ---------- P10: the feed renders ----------
  const feed = [
    { t: "16:46", who: "ACTION", msg: "Gear switched to Gold loadout", detail: "gold snipe" },
    { t: "16:52", who: "WARN",   msg: "Iron Pill cast held", detail: "under the 10% power floor" },
    { t: "16:55", who: "QUEUED", msg: "Titan 7 v1 queued for the next gold bank" },
    { t: "16:57", who: "FAIL",   msg: "Gear optimize failed" }
  ];
  send({ feed: feed, instruments: {}, goal: {} });

  const flist = $("flist");
  ok("P10 #flist exists", !!flist);
  const rows = () => Array.from(flist.querySelectorAll(".fline"));
  ok("P10 renders default kinds (ACTION+WARN+FAIL, not QUEUED)", rows().length === 3, "rows=" + rows().length);
  ok("P10 newest first", /Gear optimize failed/.test(rows()[0].textContent), rows()[0] && rows()[0].textContent);
  ok("P10 severity is a word, not colour alone", rows().some(r => /WARN/.test(r.textContent)));
  ok("P10 severity rail class applied", rows().some(r => r.className.indexOf("k-FAIL") >= 0));
  ok("P10 detail line rendered", /under the 10% power floor/.test(flist.textContent));

  // toggle QUEUED on
  const qchip = document.querySelector('.fch[data-k="QUEUED"]');
  ok("P10 QUEUED chip starts off", qchip.getAttribute("aria-pressed") === "false");
  qchip.dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("P10 toggling QUEUED repaints to 4 rows", rows().length === 4, "rows=" + rows().length);

  // turn everything off
  ["ACTION", "WARN", "FAIL", "QUEUED"].forEach(k => {
    const c = document.querySelector('.fch[data-k="' + k + '"]');
    if (c.getAttribute("aria-pressed") === "true") c.dispatchEvent(new window.Event("click", { bubbles: true }));
  });
  ok("P10 all-off shows the filter-empty message", /Nothing matches these filters/.test(flist.textContent), flist.textContent.slice(0, 80));

  // ---------- P10: tabs ----------
  ok("P10 Activity tab selected by default", $("tabAct").getAttribute("aria-selected") === "true");
  ok("P10 log pane hidden by default", $("logtext").hidden === true);
  $("tabLog").dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("P10 switching to Log file shows the pre", $("logtext").hidden === false && $("flist").hidden === true);
  ok("P10 log-file select revealed", $("dsubLog").hidden === false && $("dsubAct").hidden === true);
  $("tabAct").dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("P10 switching back restores the feed", $("flist").hidden === false && $("logtext").hidden === true);

  // ---------- P6: per-section live/parked badges ----------
  // renderProfiles builds #secList once, so the profiles node has to arrive before the sections node
  // can be drawn into it.
  send({
    profiles: { list: ["24hr-Evil"], active: "24hr-Evil", dir: "C:\\p", autoProfile: true },
    sections: [
      { key: "energy",  label: "Energy",  driver: "advisor", why: "Auto Profile is generating this segment's energy list instead of using yours." },
      { key: "gear",    label: "Gear",    driver: "nobody",  why: "Nothing is driving this." },
      { key: "diggers", label: "Diggers", driver: "profile", why: "Your profile's digger breakpoints are running — Auto Profile does not affect this one." },
      { key: "beards",  label: "Beards",  driver: "off",     why: "This system is not being managed." }
    ],
    instruments: {}, goal: {}
  });

  const secList = $("secList");
  ok("P6 #secList exists", !!secList);
  const secRows = () => Array.from(secList.querySelectorAll(".secrow"));
  ok("P6 one row per section", secRows().length === 4, "rows=" + secRows().length);
  ok("P6 advisor badge", /Advisor/.test(secRows()[0].textContent));
  ok("P6 profile badge", /Profile/.test(secRows()[2].textContent));
  ok("P6 off badge", /Off/.test(secRows()[3].textContent));
  ok("P6 the reason is shown, not just the state", /generating this segment/.test(secList.textContent));

  // The gap gets the loudest treatment on the page — it is a defect, not a resting state.
  const gearRow = secRows()[1];
  ok("P6 gap row badged Nobody", /Nobody/.test(gearRow.textContent));
  ok("P6 gap row visually escalated", gearRow.className.indexOf("is-nobody") >= 0, gearRow.className);
  ok("P6 gap row says nothing is driving it", /Nothing is driving this/.test(gearRow.textContent));

  // Repainting on change, and not lying when the node is absent.
  send({ profiles: { list: ["24hr-Evil"], active: "24hr-Evil", dir: "C:\\p", autoProfile: false },
         sections: [ { key: "gear", label: "Gear", driver: "profile", why: "Your profile's gear breakpoints are running." } ],
         instruments: {}, goal: {} });
  ok("P6 repaints when the verdicts change", secRows().length === 1 && /Profile/.test(secRows()[0].textContent),
     secRows().length + " / " + (secRows()[0] && secRows()[0].textContent));
  ok("P6 gap styling cleared once resolved", secList.querySelectorAll(".is-nobody").length === 0);

  send({ profiles: { list: ["24hr-Evil"], active: "24hr-Evil", dir: "C:\\p" }, instruments: {}, goal: {} });
  ok("P6 absent node shows a waiting state, not a stale verdict", /Waiting for the advisor/.test(secList.textContent),
     secList.textContent.slice(0, 60));

  // ---------- the pool board ----------
  // Real numbers from the operator's log: TimeMachine took 67.8% of energy and 32.1% went nowhere.
  send({ pools: { seq: 10, energy: {
      pool: 2185000000000, unallocated: 701452565876,
      idleWhy: "beyond the sink's per-tick absorptive capacity", sinkSeated: true,
      lanes: [
        { name:"CAPTimeMachine-0", seated:true, sink:false, took:1482103415355, offered:1482242767712 },
        { name:"Wandoos-0", seated:true, sink:true, took:803893353, offered:702256459229 },
        { name:"CAPAdvancedTraining-0", seated:true, sink:false, took:208766670, offered:546194351981 }
      ] } }, instruments: {}, goal: {} });

  const pb = $("poolBoard");
  ok("pool board renders", !!pb && /Energy/.test(pb.textContent));
  ok("lanes are direct-labelled, no separate legend", /CAPTimeMachine-0/.test(pb.textContent) && /Wandoos-0/.test(pb.textContent));
  ok("the sink is tagged as one", /sink/.test(pb.textContent));
  ok("dominant lane's share is shown", /67\.8%/.test(pb.textContent), pb.textContent.slice(0,140));

  // The whole point of the screen: the remainder is a first-class line, not a rounding gap.
  ok("idle remainder is a line", /idle remainder/.test(pb.textContent));
  ok("idle is escalated past the threshold", pb.querySelectorAll(".pb-loss").length === 1);
  ok("and it says how much and why", /32\.1% of the energy pool went nowhere/.test(pb.textContent)
     && /absorptive capacity/.test(pb.textContent), pb.textContent.slice(0,240));

  // Below the threshold the same figure must go quiet — a permanent red band stops being read.
  send({ pools: { seq: 11, energy: { pool: 1000, unallocated: 20, sinkSeated: true,
      lanes: [ { name:"CAPTimeMachine-0", seated:true, sink:false, took:980, offered:980 } ] } },
      instruments: {}, goal: {} });
  ok("a small remainder is not alarmed", pb.querySelectorAll(".pb-loss").length === 0, pb.textContent.slice(0,120));
  ok("but it is still stated", /2\.0% left idle/.test(pb.textContent), pb.textContent.slice(0,120));

  // A liveness-only tick carries no lanes. The board must keep what it has, not blank.
  send({ pools: { seq: 12 }, instruments: {}, goal: {} });
  ok("liveness tick does not blank the board", /CAPTimeMachine-0/.test(pb.textContent), pb.textContent.slice(0,120));

  // THE POOLS ARE GATED INDEPENDENTLY. A tick where only one moved carries only that one, so caching
  // the payload wholesale deletes the other — which is what made the board look Energy-only.
  send({ pools: { seq: 13,
      energy: { pool: 100, unallocated: 0, sinkSeated: true, lanes: [ { name:"E-lane", seated:true, sink:false, took:100, offered:100 } ] },
      magic:  { pool: 200, unallocated: 0, sinkSeated: true, lanes: [ { name:"M-lane", seated:true, sink:false, took:200, offered:200 } ] } },
      instruments: {}, goal: {} });
  ok("both pools render when both are sent", /E-lane/.test(pb.textContent) && /M-lane/.test(pb.textContent));
  ok("and both are titled", /Energy/.test(pb.textContent) && /Magic/.test(pb.textContent));

  send({ pools: { seq: 14,
      energy: { pool: 100, unallocated: 50, sinkSeated: true, lanes: [ { name:"E-lane2", seated:true, sink:false, took:50, offered:100 } ] } },
      instruments: {}, goal: {} });
  ok("an energy-only tick updates energy", /E-lane2/.test(pb.textContent), pb.textContent.slice(0,140));
  ok("an energy-only tick does NOT delete magic", /M-lane/.test(pb.textContent), pb.textContent.slice(0,200));

  send({ pools: { seq: 15,
      magic: { pool: 200, unallocated: 0, sinkSeated: true, lanes: [ { name:"M-lane2", seated:true, sink:false, took:200, offered:200 } ] } },
      instruments: {}, goal: {} });
  ok("and the same holds the other way round", /M-lane2/.test(pb.textContent) && /E-lane2/.test(pb.textContent), pb.textContent.slice(0,200));

  // Focus hides reference material and keeps what you act on. A third of the pool going nowhere is
  // the second kind, so the board must survive the mode people leave running.
  ok("pool board is not hidden by Focus", pb.className.indexOf("density-full") < 0, pb.className);
  ok("nor is its section label",
     !/density-full/.test(Array.from(document.querySelectorAll(".seclabel"))
        .find(function(e){ return /Where the pool went/.test(e.textContent); }).className));

  // ---------- the advisor ledger ----------
  send({
    ledger: {
      declared: 18, live: 4, pending: 14,
      rows: [
        { id:"at.block", system:"Advanced Training", game:"Advanced Training · Block target",
          field:"advancedTraining.levelTarget[2]", rule:"LevelPlanner.ApplyPurposeFloor",
          authority:"operator ruling", value:"100,000", why:"Block hard cap", segment:"NGU MARATHON",
          state:"active", t:"04:56:22", chain:["written once at engage","held as a FLOOR"] },
        { id:"at.wandoos.reclaim", system:"Advanced Training", game:"Advanced Training · Energy Dump & Magic Dump targets",
          field:"advancedTraining.levelTarget[3..4]", rule:"LevelPlanner reclaim",
          authority:"operator ruling", value:"0 (unset)", why:"withdrew a stranded target", segment:"AUGMENTATION",
          state:"stale", t:"16:32:10", chain:["slots held 2,847,391 / 1,204,558 at engage"] },
        { id:"ngu.track.planner", system:"NGU", game:"NGU difficulty — Normal / Evil / Sadistic",
          field:"settings.nguLevelTrack", rule:"LevelPlanner.TickNguTrack",
          authority:"guide ch.5", value:"Normal", why:"Evil tail", segment:"NGU+AT",
          state:"contested", t:"16:55:03", chain:["your profile's timeline writes this field too"] }
      ]
    }, instruments: {}, goal: {}
  });

  const lgRows = $("lgRows"), lgScope = $("lgScope");
  ok("ledger view exists", !!lgRows && !!lgScope);
  ok("coverage claim is derived from the counts", /4 of 18 state writers instrumented/.test(lgScope.textContent), lgScope.textContent.slice(0,60));
  ok("pending is stated, not hidden", /14 declared, not yet wired/.test(lgScope.textContent));
  ok("scope names what it excludes", /Irreversible actions are in the Activity drawer/.test(lgScope.textContent));

  const lg = () => Array.from(lgRows.querySelectorAll(".lgrow"));
  ok("all three rows render", lg().length === 3, "rows=" + lg().length);
  ok("newest first", /NGU difficulty/.test(lg()[0].textContent), lg()[0].textContent.slice(0,50));

  // The row must name what the operator can go and look at, not where the value lives in the code.
  ok("rows lead with the in-game name", /Advanced Training · Block target/.test(lgRows.textContent), lgRows.textContent.slice(0,120));
  ok("code paths are not in the row headline", !/levelTarget\[2\]/.test(lg().find(r => /Block target/.test(r.textContent)).querySelector(".lgfield").textContent));
  ok("stale row carries its severity class", lg().some(r => r.className.indexOf("s-stale") >= 0));
  ok("contested row carries its severity class", lg().some(r => r.className.indexOf("s-contested") >= 0));
  ok("state is a word, not colour alone", /Contested/.test(lgRows.textContent) && /Stale/.test(lgRows.textContent));

  // filter to the state that matters
  document.querySelector('#lgFilters .fch[data-s="stale"]').dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("filtering to stale leaves one row", lg().length === 1, "rows=" + lg().length);
  ok("and it is the right one", /levelTarget\[3\.\.4\]/.test(lg()[0].textContent));
  document.querySelector('#lgFilters .fch[data-s="reverted"]').dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("an empty state says so without claiming there is nothing at all",
     /Nothing in this state right now/.test(lgRows.textContent), lgRows.textContent.slice(0,60));
  document.querySelector('#lgFilters .fch[data-s="all"]').dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("back to all", lg().length === 3);

  // expanding shows the causal chain
  lg()[0].querySelector(".lgh").dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("row expands", lg()[0].className.indexOf("open") >= 0);
  ok("chain is rendered", /your profile's timeline writes this field too/.test(lg()[0].textContent));
  ok("writer and authority shown", /LevelPlanner.TickNguTrack/.test(lg()[0].textContent) && /guide ch.5/.test(lg()[0].textContent));

  send({ ledger: { declared: 18, live: 4, pending: 14, rows: [] }, instruments: {}, goal: {} });
  ok("empty ledger says nothing has been set yet",
     /has not set anything yet/.test(lgRows.textContent), lgRows.textContent.slice(0,60));

  // ---------- regression: the hidden pane must actually collapse ----------
  // Operator-reported after the first deploy: switching to the Log file tab left the empty activity
  // list holding the top half of the drawer. Cause was specificity — `[hidden]{display:none}` is a UA
  // rule and ANY author rule setting `display` outranks it, which `.flist{display:flex}` does.
  // jsdom does not lay out, so this asserts the rule EXISTS rather than that the pixels moved; that is
  // the honest limit of this harness, and it is still enough to catch the rule being deleted.
  const css = html;
  // Two spans inside ONE grid cell are not grid items — only their wrapper is blockified. Left inline
  // they flow onto a single line and their margin-top is dropped, which is how the field name and its
  // reason ended up running together. jsdom does not lay out, so assert the rule exists.
  ok("ledger stacks the field name above its reason",
     /\.lgh\s+\.lgfield\s*\{[^}]*display:\s*block/.test(html) && /\.lgh\s+\.lgwhy\s*\{[^}]*display:\s*block/.test(html));
  ok("ledger reason has breathing room above it",
     /\.lgh\s+\.lgwhy\s*\{[^}]*margin-top:\s*[4-9]px/.test(html));

  ok("hidden panes have an explicit collapse rule",
     /\.flist\[hidden\][^{]*\{[^}]*display:\s*none/.test(css) && /\.logtext\[hidden\]/.test(css));
  ok("drawer is wider than the original 400px",
     /\.drawer\s*\{[^}]*width:\s*min\(\s*8\d\dpx/.test(css));

  // ---------- hot reload: a new advisor build reloads the page, once ----------
  // jsdom cannot navigate, so the reload itself is not observable here; the latch that guards it is.
  send({ buildTag: "261010-1000", instruments: {}, goal: {} });
  ok("the first build tag is remembered, not acted on", window._buildTag === "261010-1000" && !window._reloading);
  send({ buildTag: "261010-1000", instruments: {}, goal: {} });
  ok("the same build does not reload", !window._reloading);
  send({ instruments: {}, goal: {} });
  ok("a snapshot without a tag (older advisor) does not reload", !window._reloading);
  send({ buildTag: "261010-1030", instruments: {}, goal: {} });
  ok("a different build tag triggers the reload", window._reloading === true);

  // ---------- regression: drawer still opens ----------
  $("logbtn").dispatchEvent(new window.Event("click", { bubbles: true }));
  ok("drawer opens", $("drawer").classList.contains("open"));
  ok("drawer focuses the Activity tab", document.activeElement && document.activeElement.id === "tabAct",
     document.activeElement && document.activeElement.id);

  console.log("\n" + "=".repeat(56));
  console.log("UI-audit verification — P6 / P9 / P10 / Ledger / Pools");
  console.log("PASS " + pass + "   FAIL " + fail);
  if (failures.length) { console.log("-".repeat(56)); failures.forEach(f => console.log("  " + f)); }
  console.log("=".repeat(56));
  process.exit(fail ? 1 : 0);
}, 120);
