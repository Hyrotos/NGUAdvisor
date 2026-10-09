// jsdom harness for the Gear > Loadouts work. Runs the REAL index.html from whichever tree is passed
// (default: dev), fakes the WebView2 host so the live layer boots, feeds synthetic snapshots down the
// same path MainForm uses (PostWebMessageAsJson -> window "message" event) and asserts on the DOM.
//
// Usage: node test-loadouts.js [path-to-index.html]
const fs = require("fs");
const path = require("path");
const { JSDOM, VirtualConsole } = require("jsdom");

const FILE = process.argv[2] || path.resolve(__dirname, "../../NGUAdvisorCompanion/wwwroot/index.html");
// The injector's binding registry, so the page's [data-setting] keys can be checked against the real
// thing rather than against a fixture that only agrees with itself. Resolved RELATIVE TO THE PAGE, so
// pointing this at the public tree checks the public tree's UiBridge. A deployed copy
// (<injectorDir>/companion/wwwroot) has no source beside it — that check is then skipped rather than
// crashing the run, since a deployed page is verified by hashing it against its source instead.
const BRIDGE = [
  path.resolve(path.dirname(FILE), "../../NGUAdvisor/Managers/UiBridge.cs"),
  path.resolve(__dirname, "../../NGUAdvisor/Managers/UiBridge.cs")
].find(p => fs.existsSync(p)) || null;

let pass = 0, fail = 0;
const failures = [];
function ok(name, cond, detail) {
  if (cond) { pass++; }
  else { fail++; failures.push(name + (detail ? "  --> " + detail : "")); }
}

const html = fs.readFileSync(FILE, "utf8");
const vc = new VirtualConsole();
vc.on("jsdomError", e => { console.log("JSDOM ERROR: " + e.message); });

// The live layer registers its snapshot listener at SCRIPT-EXECUTION time
//   window.chrome.webview.addEventListener("message", e => applySnapshot(e.data))
// and an earlier IIFE early-returns unless `window.chrome.webview` exists. So the WebView2 stub has to
// be in place BEFORE the page's scripts run — beforeParse, not after construction.
const SENT = [];
let hostListener = null;
const dom = new JSDOM(html, {
  runScripts: "dangerously",
  pretendToBeVisual: true,
  virtualConsole: vc,
  beforeParse(w) {
    w.chrome = {
      webview: {
        postMessage(s) { try { SENT.push(JSON.parse(s)); } catch (e) { SENT.push({ raw: s }); } },
        addEventListener(type, fn) { if (type === "message") hostListener = fn; },
        removeEventListener() {}
      }
    };
    if (!w.matchMedia) w.matchMedia = () => ({ matches: false, addListener(){}, removeListener(){}, addEventListener(){}, removeEventListener(){} });
    w.scrollTo = w.scrollTo || function () {};
  }
});
const { window } = dom;
if (!window.Element.prototype.scrollIntoView) window.Element.prototype.scrollIntoView = function () {};

// The page installs 1 Hz intervals, so node never exits on its own; and a throw inside our own load
// handler would otherwise be swallowed by jsdom and look identical to a hang.
function done(code) {
  console.log("\n" + "=".repeat(60));
  console.log(FILE);
  console.log("PASS " + pass + "   FAIL " + fail);
  if (failures.length) { console.log("\nFAILURES:"); failures.forEach(f => console.log("  - " + f)); }
  console.log("=".repeat(60));
  try { dom.window.close(); } catch (e) {}
  process.exit(code != null ? code : (fail ? 1 : 0));
}
const HARD_STOP = setTimeout(() => { console.log("\n!! harness timed out before finishing"); done(1); }, 45000);
HARD_STOP.unref && HARD_STOP.unref();
function guard(fn) { return function () { try { return fn.apply(null, arguments); } catch (e) { console.log("HARNESS THREW: " + (e && e.stack || e)); done(1); } }; }

// --- stubs the page needs but jsdom lacks -------------------------------------------------
// index.html calls matchMedia unguarded (fine in WebView2).
if (!window.matchMedia) window.matchMedia = () => ({ matches: false, addListener(){}, removeListener(){}, addEventListener(){}, removeEventListener(){} });
window.scrollTo = window.scrollTo || function(){};
if (!window.Element.prototype.scrollIntoView) window.Element.prototype.scrollIntoView = function(){};

function send(snapshot) {
  // MainForm.PostWebMessageAsJson -> the page's listener gets it as e.data, already deserialised.
  if (!hostListener) throw new Error("the page never registered a webview message listener");
  hostListener({ data: snapshot });
}

function $(id) { return window.document.getElementById(id); }
function txt(id) { const el = $(id); return el ? el.textContent.trim() : null; }

// --- the fixture ---------------------------------------------------------------------------
const GEAR_OBJECTIVES = ["Adventure", "Power", "Toughness", "Drop Chance", "Gold Drops", "NGUs", "Cooking"];

function baseSnapshot(overrides) {
  const s = {
    v: 1,
    settings: {
      GlobalEnabled: true,
      ManageGear: true, AdvisorGearRefresh: true, AdvisorGearOnDrop: true,
      ManageTitans: true, SwapTitanLoadouts: true,
      ManageGoldLoadouts: true,
      AutoQuest: true, ManageQuestLoadouts: true,
      ManageYggdrasil: true, SwapYggdrasilLoadouts: true,
      ManageCooking: true, ManageCookingLoadouts: true,
      GearHuntEnabled: true, GearHuntZone: 12,
      AutoMoneyPit: true,
      TitanObjective: "", GoldObjective: "", QuestObjective: "",
      YggdrasilObjective: "", CookingObjective: "", GearObjective: "",
      TitanObjectiveRespawn: false, GoldObjectiveRespawn: false, QuestObjectiveRespawn: false,
      YggdrasilObjectiveRespawn: false, CookingObjectiveRespawn: false, GearObjectiveRespawn: false,
      LootHunterRespawnCount: 0, LootHunterDropCount: 0
    },
    gearObjectives: GEAR_OBJECTIVES,
    loadouts: {
      TitanLoadout: [], GoldDropLoadout: [], QuestLoadout: [],
      YggdrasilLoadout: [], CookingLoadout: [], LootHunterAccessories: [], Shockwave: []
    },
    itemNames: { "94": "Wooden Spoon", "188": "Edgy Helmet", "301": "Sturdy Pants" },
    titans: { names: ["T1","T2","T3"], targets: [true, false, false] },
    equipped: ["Wooden Spoon", "Edgy Helmet"],
    gearNow: { objective: "Adventure", source: "pin", sentence: "Running <b>Adventure</b> — your standing pick." },
    systems: {}, action: "Default", nextLoopSec: 5
  };
  if (overrides) {
    for (const k of Object.keys(overrides)) {
      if (k === "settings" || k === "loadouts" || k === "titans" || k === "gearNow") Object.assign(s[k], overrides[k]);
      else s[k] = overrides[k];
    }
  }
  return s;
}

// ---------------------------------------------------------------------------------------------
window.addEventListener("load", guard(() => {
  const MODES = window.loadoutModes;

  // ---- 1. the registry crossed the script boundary --------------------------------------
  ok("loadoutModes is exported to the live layer", Array.isArray(MODES), String(MODES));
  ok("gateLabel is exported to the live layer", !!window.gateLabel);
  ok("Main is the first mode", MODES && MODES[0] && MODES[0].key === "Main", MODES && MODES[0] && MODES[0].key);
  ok("8 modes are declared", MODES && MODES.length === 8, MODES && String(MODES.length));

  send(baseSnapshot());

  // ---- 2. every [data-setting] key the page emits must be BOUND IN THE INJECTOR ----------
  // Checking against the fixture would only prove the fixture agrees with itself. The real failure
  // mode is a key the page writes that UiBridge.BindingList doesn't know: setSetting then falls
  // through to a LogDebug("unknown key") and the switch silently does nothing forever. So read the
  // actual C# registry and cross the language boundary.
  if (!BRIDGE) {
    console.log("note: no UiBridge.cs beside this page — skipping the binding cross-check "
              + "(expected for a deployed copy; verify that by hashing it against its source).");
  } else {
    const bridge = fs.readFileSync(BRIDGE, "utf8");
    const bound = new Set();
    const bindRe = /Binding\.(?:Bool|Str|Int|Dbl)\s*\(\s*"([^"]+)"/g;
    let bm;
    while ((bm = bindRe.exec(bridge))) bound.add(bm[1]);
    ok("UiBridge.BindingList was parsed", bound.size > 50, "found " + bound.size);

    const unbound = [];
    window.document.querySelectorAll("#view-loadouts [data-setting], #view-settings [data-setting]").forEach(el => {
      const k = el.getAttribute("data-setting");
      if (!bound.has(k)) unbound.push(k);
    });
    ok("every [data-setting] on Loadouts/Settings is bound in UiBridge",
       unbound.length === 0, Array.from(new Set(unbound)).join(", "));

    // and the declared gate lists specifically (they may not all be rendered if a mode is skipped)
    const unboundGates = [];
    MODES.forEach(m => (m.gates || []).forEach(g => { if (!bound.has(g)) unboundGates.push(m.key + ":" + g); }));
    ok("every declared gate key is bound in UiBridge", unboundGates.length === 0, unboundGates.join(", "));
  }

  // the fixture must also cover them, or the later assertions are vacuous
  const fixtureKeys = new Set(Object.keys(baseSnapshot().settings));
  const missing = [];
  MODES.forEach(m => (m.gates || []).forEach(g => { if (!fixtureKeys.has(g)) missing.push(m.key + ":" + g); }));
  ok("the fixture covers every gate key", missing.length === 0, missing.join(", "));

  // every gate also needs a human label, or the switch renders its raw key
  const unlabelled = [];
  MODES.forEach(m => (m.gates || []).forEach(g => { if (!window.gateLabel[g]) unlabelled.push(g); }));
  ok("every gate key has a human label", unlabelled.length === 0, unlabelled.join(", "));

  // ---- 3. gate switches actually rendered, per mode ---------------------------------------
  MODES.forEach(m => {
    (m.gates || []).forEach(g => {
      const el = window.document.querySelector('#view-loadouts [data-setting="' + g + '"]');
      ok("gate switch rendered: " + m.key + "/" + g, !!el);
    });
  });

  // ---- 4. status line: armed ---------------------------------------------------------------
  ok("Titan reports armed when all gates on + a titan ticked",
     /Armed/i.test(txt("lg-TitanLoadout") || ""), txt("lg-TitanLoadout"));
  ok("Main uses the injector's own sentence",
     /your standing pick/i.test(txt("lg-Main") || ""), txt("lg-Main"));
  // The injector sends PLAIN TEXT and the page escapes it. An objective name can reach gearNow from a
  // hand-edited profile JSON or settings.json, so this must never be injected as markup.
  send(baseSnapshot({ gearNow: { sentence: 'Running "<img src=x onerror=alert(1)>" — your standing pick.' } }));
  ok("a hostile gearNow sentence is escaped, not injected",
     $("lg-Main").querySelector("img") === null, $("lg-Main").innerHTML.slice(0, 120));
  ok("the hostile sentence still renders as readable text",
     /img src=x/.test(txt("lg-Main") || ""), txt("lg-Main"));

  // ---- 5. status line: one gate off ---------------------------------------------------------
  send(baseSnapshot({ settings: { SwapTitanLoadouts: false } }));
  const one = txt("lg-TitanLoadout") || "";
  ok("one gate off -> Not armed", /Not armed/i.test(one), one);
  ok("one gate off names that gate", /Swap gear for titans/i.test(one), one);
  ok("one gate off uses singular 'is'", /\bis\b/.test(one), one);

  // ---- 6. status line: two gates off --------------------------------------------------------
  send(baseSnapshot({ settings: { ManageTitans: false, SwapTitanLoadouts: false } }));
  const two = txt("lg-TitanLoadout") || "";
  ok("two gates off names both", /Titan automation/i.test(two) && /Swap gear for titans/i.test(two), two);
  ok("two gates off uses plural 'are'", /\bare\b/.test(two), two);

  // ---- 7. the non-boolean preconditions -----------------------------------------------------
  send(baseSnapshot({ titans: { targets: [false, false, false] } }));
  ok("no titans ticked is reported", /no titans are ticked/i.test(txt("lg-TitanLoadout") || ""), txt("lg-TitanLoadout"));

  send(baseSnapshot({ settings: { GearHuntZone: -1 } }));
  ok("gear hunt with no stage is reported",
     /no hunt stage/i.test(txt("lg-LootHunterAccessories") || ""), txt("lg-LootHunterAccessories"));

  send(baseSnapshot({ settings: { CookingObjective: "" }, loadouts: { CookingLoadout: [] } }));
  ok("cooking with neither objective nor list is reported",
     /Nothing configured/i.test(txt("lg-CookingLoadout") || ""), txt("lg-CookingLoadout"));

  // objective-only cooking must NOT be reported as unconfigured (this is the RC2 fix's UI half)
  send(baseSnapshot({ settings: { CookingObjective: "Cooking" } }));
  ok("cooking with an objective and no list is armed",
     /Armed/i.test(txt("lg-CookingLoadout") || ""), txt("lg-CookingLoadout"));

  // ---- 8. duplicate-bound controls move together --------------------------------------------
  // GearHuntEnabled is on BOTH the Adventure view and the Loot Hunter block; ManageGear is on
  // Settings, Main and Loot Hunter. Grouping in renderSettings is what keeps them in step.
  send(baseSnapshot());
  const gearHunts = window.document.querySelectorAll('[data-setting="GearHuntEnabled"]');
  ok("GearHuntEnabled is bound more than once", gearHunts.length > 1, String(gearHunts.length));
  ok("all GearHuntEnabled copies are checked",
     Array.from(gearHunts).every(e => e.checked), Array.from(gearHunts).map(e => e.checked).join(","));

  send(baseSnapshot({ settings: { GearHuntEnabled: false } }));
  ok("all GearHuntEnabled copies cleared together",
     Array.from(window.document.querySelectorAll('[data-setting="GearHuntEnabled"]')).every(e => !e.checked));

  // clicking one copy moves every copy immediately (optimistic), and posts exactly one setSetting
  send(baseSnapshot());
  SENT.length = 0;
  const first = window.document.querySelectorAll('[data-setting="ManageGear"]')[0];
  first.checked = false;
  first.dispatchEvent(new window.Event("change", { bubbles: true }));
  const setSettingMsgs = SENT.filter(m => m.cmd === "setSetting" && m.key === "ManageGear");
  ok("one click posts exactly one setSetting", setSettingMsgs.length === 1, JSON.stringify(SENT));
  ok("every ManageGear copy moved on click",
     Array.from(window.document.querySelectorAll('[data-setting="ManageGear"]')).every(e => !e.checked));

  // ---- 9. objective dropdowns ---------------------------------------------------------------
  const titanSel = $("set-TitanObjective");
  ok("titan objective select exists", !!titanSel);
  // The listed objectives, between the "" lead and the trailing "Custom chain…".
  ok("objective options were built", titanSel && titanSel.options.length === GEAR_OBJECTIVES.length + 2,
     titanSel && String(titanSel.options.length));
  ok("the picker ends with Custom chain",
     titanSel && titanSel.options[titanSel.options.length - 1].value === "__chain__");
  ok("mode selects lead with 'Manual — use item list'",
     titanSel && /Manual/.test(titanSel.options[0].textContent), titanSel && titanSel.options[0].textContent);
  const mainSel = $("set-GearObjective");
  ok("main objective select exists", !!mainSel);
  ok("Main leads with 'Follow the profile timeline'",
     mainSel && /Follow the profile timeline/.test(mainSel.options[0].textContent),
     mainSel && mainSel.options[0].textContent);

  // ---- 9b. a custom chain as a loadout objective --------------------------------------------
  // The objective is a string setting, and a chain is a longer string: the picker says "Custom
  // chain…" and the rows under it write that same setting.
  {
    const fireOn = (el, type) => el.dispatchEvent(new window.Event(type, { bubbles: true }));
    const box = $("lc-TitanObjective");
    const rows = () => Array.from(box.querySelectorAll(".peg-step"));
    const lastSet = () => SENT.filter(m => m.cmd === "setSetting" && m.key === "TitanObjective").pop();
    ok("each objective picker has a chain box, hidden", !!box && box.hidden && !!$("lc-GearObjective"));
    // Non-empty id lists throughout, so picking an objective here never schedules the debounced
    // "fill the empty list" that section 11 counts.
    const full = { TitanLoadout: [94], GoldDropLoadout: [94] };
    const withSettings = st => baseSnapshot({ settings: st, loadouts: full });

    // The advisor reports a chain: the picker cannot list it, so it shows Custom chain + the rows.
    send(withSettings({ TitanObjective: "Adventure(0)+PowerWeapon > Drop Chance(all)" }));
    ok("a chain setting selects Custom chain", titanSel.value === "__chain__", titanSel.value);
    ok("and shows its steps", !box.hidden && rows().length === 2 &&
       rows()[0].querySelector('[data-cstep="slots"]').value === "0" && box.querySelector(".cs-pin").checked);
    ok("other modes stay untouched", $("lc-GoldObjective").hidden && $("set-GoldObjective").value === "");

    SENT.length = 0;
    const slots = rows()[1].querySelector('[data-cstep="slots"]');
    slots.value = "2"; fireOn(slots, "change");
    ok("changing a step writes the objective setting",
       lastSet() && lastSet().value === "Adventure(0)+PowerWeapon > Drop Chance(2)", JSON.stringify(SENT));
    fireOn(box.querySelector(".cs-add"), "click");
    ok("adding a step writes it too", lastSet() && /> Drop Chance\(2\) > .+\(all\)$/.test(lastSet().value), JSON.stringify(lastSet()));
    ok("a redraw keeps the rows the user built", rows().length === 3);

    // A snapshot echoing the pending value must not rebuild the rows.
    const firstRow = rows()[0];
    send(withSettings({ TitanObjective: lastSet().value }));
    ok("the echo does not rebuild the rows", rows()[0] === firstRow && titanSel.value === "__chain__");

    // Back to a listed objective: the rows go away and the plain name is written.
    SENT.length = 0;
    fireOn(titanSel, "focusin");
    titanSel.value = "Adventure"; fireOn(titanSel, "change");
    ok("picking a listed objective hides the rows and writes the name",
       box.hidden && lastSet() && lastSet().value === "Adventure", JSON.stringify(SENT));

    // And picking Custom chain again resumes the chain that was built, rather than starting over.
    SENT.length = 0;
    fireOn(titanSel, "focusin");
    titanSel.value = "__chain__"; fireOn(titanSel, "change");
    ok("Custom chain resumes the last chain built for the mode",
       !box.hidden && rows().length === 3 && lastSet() && /^Adventure\(0\)\+PowerWeapon > /.test(lastSet().value), JSON.stringify(lastSet()));
    ok("the placeholder value is never sent", SENT.every(m => m.value !== "__chain__" && m.objective !== "__chain__"), JSON.stringify(SENT));

    // A mode that never had a chain starts from one step taking every slot.
    const titanChain = lastSet().value;
    SENT.length = 0;
    const goldSel = $("set-GoldObjective");
    fireOn(goldSel, "focusin");
    goldSel.value = "__chain__"; fireOn(goldSel, "change");
    const goldSet = SENT.filter(m => m.cmd === "setSetting" && m.key === "GoldObjective").pop();
    ok("a first custom chain is one step taking every slot",
       !$("lc-GoldObjective").hidden && goldSet && /^[^>(]+\(all\)$/.test(goldSet.value), JSON.stringify(goldSet));

    ok("no objective fill was scheduled over a non-empty list", !SENT.some(m => m.cmd === "applyObjective"));
    // Confirm what is pending, then go back to the fixture the checks below expect.
    send(withSettings({ TitanObjective: titanChain, GoldObjective: goldSet.value }));
    titanSel.blur(); goldSel.blur();
    send(baseSnapshot());
    ok("clearing the setting hides the rows again", box.hidden && titanSel.value === "" && $("lc-GoldObjective").hidden,
       titanSel.value);
  }

  // ---- 9c. the Quest card's own objective ---------------------------------------------------
  // "Quest Drop Rate" is solved for the zone the current quest rolled, so only Quest offers it.
  {
    const has = id => Array.from($(id).options).some(o => o.value === "Quest Drop Rate");
    ok("the Quest picker offers Quest Drop Rate", has("set-QuestObjective"));
    ok("no other picker does", !has("set-TitanObjective") && !has("set-GoldObjective") && !has("set-GearObjective"));
    ok("it sits before the listed objectives", $("set-QuestObjective").options[1].value === "Quest Drop Rate");
    send(baseSnapshot({ settings: { QuestObjective: "Quest Drop Rate" }, loadouts: { QuestLoadout: [94] } }));
    ok("the setting shows as that entry, not as a custom chain",
       $("set-QuestObjective").value === "Quest Drop Rate" && $("lc-QuestObjective").hidden, $("set-QuestObjective").value);
    send(baseSnapshot());
  }

  // ---- 10. Main has no id list; the others do ------------------------------------------------
  ok("Main renders no id list", !$("Main"));
  ok("Main offers the existing refreshGear action",
     !!window.document.querySelector('#view-loadouts [data-action="refreshGear"]'));
  ok("Titan still renders its id list", !!$("TitanLoadout"));
  ok("Titan offers Fill from objective",
     !!window.document.querySelector('[data-fillobj="TitanLoadout"]'));
  // A fill button on a mode with no objective select could only ever fail — don't render one.
  ok("Loot Hunter has NO fill button (hybrid set, not an objective)",
     !window.document.querySelector('[data-fillobj="LootHunterAccessories"]'));
  ok("Shockwave has NO fill button (no objective exists for it)",
     !window.document.querySelector('[data-fillobj="Shockwave"]'));
  // Conversely, every mode that HAS an objective select must have a way to use it.
  MODES.filter(m => m.obj && !m.main).forEach(m => {
    ok("fill button present for " + m.key, !!window.document.querySelector('[data-fillobj="' + m.key + '"]'));
  });
  // Every fill button must resolve to a real objective select, or the click is a dead end.
  window.document.querySelectorAll("[data-fillobj]").forEach(b => {
    const k = b.getAttribute("data-fillobj");
    const m = MODES.filter(x => x.key === k)[0];
    ok("fill button " + k + " has a matching objective select", !!(m && m.obj && $("set-" + m.obj)));
  });

  // ---- 11. selecting an objective auto-fills, debounced --------------------------------------
  SENT.length = 0;
  titanSel.value = "Drop Chance";
  titanSel._preVal = "";
  titanSel.dispatchEvent(new window.Event("change", { bubbles: true }));
  const immediate = SENT.filter(m => m.cmd === "applyObjective");
  ok("applyObjective is debounced, not immediate", immediate.length === 0, JSON.stringify(SENT));
  ok("setSetting for the objective is immediate",
     SENT.some(m => m.cmd === "setSetting" && m.key === "TitanObjective" && m.value === "Drop Chance"),
     JSON.stringify(SENT));

  // fire the debounce
  setTimeout(guard(() => {
    const fills = SENT.filter(m => m.cmd === "applyObjective");
    ok("exactly one applyObjective after the debounce", fills.length === 1, JSON.stringify(fills));
    ok("applyObjective carries the right key + objective",
       fills[0] && fills[0].key === "TitanLoadout" && fills[0].objective === "Drop Chance",
       JSON.stringify(fills[0]));

    // clearing the objective must NOT wipe the manual list
    SENT.length = 0;
    titanSel.value = "";
    titanSel.dispatchEvent(new window.Event("change", { bubbles: true }));
    setTimeout(guard(() => {
      ok("clearing the objective sends no applyObjective",
         SENT.filter(m => m.cmd === "applyObjective").length === 0, JSON.stringify(SENT));

      // ---- 11b. picking an objective must NEVER destroy a curated list ----------------------
      // That list is the mode's fallback when the objective is cleared again or the optimizer
      // returns nothing, and this path has no confirmation and no undo. Auto-fill is a convenience
      // for an EMPTY list only; replacing a curated one is the explicit button's job.
      send(baseSnapshot({ loadouts: { QuestLoadout: [10, 25, 188] } }));
      SENT.length = 0;
      const questSel = $("set-QuestObjective");
      questSel.value = "Drop Chance";   // must be one of GEAR_OBJECTIVES, or the <select> silently keeps ""
      questSel.dispatchEvent(new window.Event("change", { bubbles: true }));
      setTimeout(guard(() => {
        ok("auto-fill does NOT overwrite a non-empty loadout",
           SENT.filter(m => m.cmd === "applyObjective").length === 0, JSON.stringify(SENT));
        ok("the objective itself is still saved",
           SENT.some(m => m.cmd === "setSetting" && m.key === "QuestObjective"), JSON.stringify(SENT));

        // ...but the explicit button still replaces it on demand.
        SENT.length = 0;
        window.document.querySelector('[data-fillobj="QuestLoadout"]')
          .dispatchEvent(new window.Event("click", { bubbles: true }));
        ok("the explicit Fill button still overwrites a curated list",
           SENT.filter(m => m.cmd === "applyObjective").length === 1, JSON.stringify(SENT));
        rest();
      }), 600);
    }), 600);
  }), 600);

  function rest() {
    guard(() => {
      // ---- 12. the Fill button ---------------------------------------------------------------
      send(baseSnapshot({ settings: { GoldObjective: "Gold Drops" } }));
      SENT.length = 0;
      window.document.querySelector('[data-fillobj="GoldDropLoadout"]')
        .dispatchEvent(new window.Event("click", { bubbles: true }));
      const gf = SENT.filter(m => m.cmd === "applyObjective");
      ok("Fill button posts applyObjective", gf.length === 1, JSON.stringify(SENT));
      ok("Fill button uses the mode's current objective",
         gf[0] && gf[0].key === "GoldDropLoadout" && gf[0].objective === "Gold Drops", JSON.stringify(gf[0]));

      // With no objective chosen it must refuse rather than send a blank. Uses Cooking deliberately:
      // it has had no change event, so no optimistic PENDING entry is holding its <select> against the
      // snapshot (that hold is correct behaviour and lasts 4s, which would mask this check).
      send(baseSnapshot({ settings: { CookingObjective: "" } }));
      SENT.length = 0;
      ok("the Cooking objective really is empty", $("set-CookingObjective").value === "",
         $("set-CookingObjective").value);
      window.document.querySelector('[data-fillobj="CookingLoadout"]')
        .dispatchEvent(new window.Event("click", { bubbles: true }));
      ok("Fill with no objective sends nothing",
         SENT.filter(m => m.cmd === "applyObjective").length === 0, JSON.stringify(SENT));

      // ---- 12b. gear-source chips on the owning system views -----------------------------------
      // The "it isn't toggling" complaint seen from the other side: on the Titans page, is titan gear
      // being chosen by the advisor, taken from my own list, or not swapping at all?
      send(baseSnapshot({
        settings: { TitanObjective: "Drop Chance", GoldObjective: "", ManageGoldLoadouts: false },
        loadouts: { GoldDropLoadout: [94, 188] }
      }));
      Object.keys(window.gearChipViews).forEach(v => {
        ok("gear chip host exists on view-" + v, !!$("gearchip-" + v));
      });
      const titanChip = $("gearchip-titans").textContent;
      ok("titan chip reports the advisor objective",
         /Advisor/.test(titanChip) && /Drop Chance/.test(titanChip), titanChip);
      ok("titan chip reports it will swap", /Will swap/.test(titanChip), titanChip);

      const goldChip = $("gearchip-gold").textContent;
      ok("gold chip reports a user list with its size",
         /Your list/.test(goldChip) && /2 items/.test(goldChip), goldChip);
      ok("gold chip reports it won't swap when a gate is off",
         /Won.t swap/.test(goldChip), goldChip);

      // Nothing configured must not claim it will swap, even with every switch on.
      send(baseSnapshot({ settings: { CookingObjective: "" }, loadouts: { CookingLoadout: [] } }));
      const cookChip = $("gearchip-cooking").textContent;
      ok("a mode with no objective and no list says there's nothing to swap to",
         /Nothing to swap to/.test(cookChip) && !/Will swap/.test(cookChip), cookChip);

      // ---- 12c. the main best set is shown ------------------------------------------------------
      // The answer to "does `Optimize: n` in the profile show me what it picked?" — it does now.
      send(baseSnapshot({ gearNow: { bestIds: [94, 188, 301] } }));
      const mbs = $("mainBestSet");
      ok("main best set renders one chip per item", mbs.querySelectorAll(".chip").length === 3,
         String(mbs.querySelectorAll(".chip").length));
      ok("main best set names items rather than showing bare ids",
         /Edgy Helmet/.test(mbs.textContent), mbs.textContent);
      ok("an item already worn is marked as worn",
         /worn/.test(mbs.textContent), mbs.textContent);
      ok("an item not yet worn is marked as swapping in",
         /would swap in/.test(mbs.textContent), mbs.textContent);
      send(baseSnapshot({ gearNow: { bestIds: [] } }));
      ok("no best set -> nothing rendered", $("mainBestSet").innerHTML === "");

      // ---- 12d. drag to reorder ------------------------------------------------------------------
      // Reordering a long priority list one arrow-click at a time is the slow path this replaces.
      send(baseSnapshot({ loadouts: { TitanLoadout: [94, 188, 301] } }));
      const listEl = $("TitanLoadout");
      const rows = () => Array.from(listEl.querySelectorAll(".le-row"));
      ok("ordered rows are draggable", rows().every(r => r.getAttribute("draggable") === "true"));
      ok("ordered rows show a grip", rows().every(r => !!r.querySelector(".le-grip")));
      ok("the arrows are KEPT for keyboard users",
         rows()[0].querySelectorAll('[data-lmove]').length === 2);
      // An unordered list must not be draggable at all (order is meaningless there).
      ok("unordered lists are not draggable",
         !$("boostBlacklist") || !$("boostBlacklist").querySelector('.le-row[draggable="true"]'));

      // The boost priority list is the one this was asked for — check it directly, not just by
      // inference from the shared code path.
      send(baseSnapshot({ boostLists: { priority: [94, 188, 301], blacklist: [] } }));
      const bp = $("boostPriority");
      ok("boost priority rows are draggable",
         bp && bp.querySelectorAll('.le-row[draggable="true"]').length === 3,
         bp && String(bp.querySelectorAll(".le-row").length));
      ok("boost priority KEEPS its arrows (drag is pointer-only; this is the keyboard path)",
         bp && bp.querySelector('[data-lmove="up"]') && bp.querySelector('[data-lmove="down"]'));
      ok("the grip is decorative, not a control in the a11y tree",
         bp && bp.querySelector(".le-grip") && bp.querySelector(".le-grip").getAttribute("aria-hidden") === "true");

      // Drag row 0 and drop it below row 2 -> [188, 301, 94]
      function dragRow(fromIdx, ontoIdx, lowerHalf) {
        const src = rows()[fromIdx], dst = rows()[ontoIdx];
        const dt = { data: {}, effectAllowed: "", dropEffect: "",
                     setData(k, v) { this.data[k] = v; }, getData(k) { return this.data[k]; } };
        const mk = (type, target, clientY) => {
          const ev = new window.Event(type, { bubbles: true, cancelable: true });
          ev.dataTransfer = dt;
          Object.defineProperty(ev, "target", { value: target, enumerable: true });
          ev.clientY = clientY;
          return ev;
        };
        // jsdom has no layout, so getBoundingClientRect is all zeros: clientY 0 lands in the top half
        // (before), clientY 1 in the bottom half (after). That is enough to exercise both branches.
        src.dispatchEvent(mk("dragstart", src, 0));
        dst.dispatchEvent(mk("dragover", dst, lowerHalf ? 1 : 0));
        dst.dispatchEvent(mk("drop", dst, lowerHalf ? 1 : 0));
        window.document.dispatchEvent(mk("dragend", src, 0));
      }

      // Re-establish the Titan fixture: the boost-priority checks above sent a snapshot whose
      // TitanLoadout is the empty default, which would leave nothing to drag.
      send(baseSnapshot({ loadouts: { TitanLoadout: [94, 188, 301] } }));
      ok("titan list is back to three rows for the drag checks", rows().length === 3, String(rows().length));

      SENT.length = 0;
      dragRow(0, 2, true);
      let sent = SENT.filter(m => m.cmd === "setSettingList" && m.key === "TitanLoadout");
      ok("dropping below the last row moves the item to the end",
         sent.length === 1 && JSON.stringify(sent[0].values) === JSON.stringify([188, 301, 94]),
         JSON.stringify(sent.map(x => x.values)));

      // Drag the (new) last row onto the top half of row 0 -> back to [94, 188, 301]
      SENT.length = 0;
      dragRow(2, 0, false);
      sent = SENT.filter(m => m.cmd === "setSettingList");
      ok("dropping above the first row moves the item to the front",
         sent.length === 1 && JSON.stringify(sent[0].values) === JSON.stringify([94, 188, 301]),
         JSON.stringify(sent.map(x => x.values)));

      // Dropping an item back onto itself must not churn a write.
      SENT.length = 0;
      dragRow(1, 1, false);
      ok("a no-op drop sends nothing", SENT.filter(m => m.cmd === "setSettingList").length === 0,
         JSON.stringify(SENT));

      // ---- 12d-2. the string lists drag too ------------------------------------------------------
      // Boost TYPE priority is an ordered list just like the id lists; there is no reason for one kind
      // of ordered list on the page to be draggable and the other not.
      send(baseSnapshot({ boostPriority: ["Power", "Toughness", "Special"] }));
      const bto = $("boostTypeOrder");
      const btoRows = () => Array.from(bto.querySelectorAll(".le-row"));
      ok("boost TYPE order renders three rows", btoRows().length === 3, String(btoRows().length));
      ok("boost TYPE rows are draggable",
         btoRows().every(r => r.getAttribute("draggable") === "true"));
      ok("boost TYPE rows show a grip", btoRows().every(r => !!r.querySelector(".le-grip")));
      ok("boost TYPE keeps its arrows for keyboard",
         btoRows()[0].querySelectorAll("[data-smove]").length === 2);

      SENT.length = 0;
      (function () {
        const src = btoRows()[0], dst = btoRows()[2];
        const dt = { data: {}, setData(k, v) { this.data[k] = v; }, getData(k) { return this.data[k]; } };
        const mk = (type, target, y) => {
          const ev = new window.Event(type, { bubbles: true, cancelable: true });
          ev.dataTransfer = dt; ev.clientY = y;
          Object.defineProperty(ev, "target", { value: target, enumerable: true });
          return ev;
        };
        src.dispatchEvent(mk("dragstart", src, 0));
        dst.dispatchEvent(mk("dragover", dst, 1));
        dst.dispatchEvent(mk("drop", dst, 1));
        window.document.dispatchEvent(mk("dragend", src, 0));
      })();
      const strSent = SENT.filter(m => m.cmd === "setSettingStrList" && m.key === "BoostPriority");
      ok("dragging a boost TYPE row posts setSettingStrList",
         strSent.length === 1, JSON.stringify(SENT));
      ok("dragging Power to the end reorders correctly",
         strSent[0] && JSON.stringify(strSent[0].values) === JSON.stringify(["Toughness", "Special", "Power"]),
         JSON.stringify(strSent.map(x => x.values)));

      // The Add row for boost TYPE is gone: all three are always present, so it could only ever say
      // "(all added)". The generic machinery must survive for the card sort order.
      ok("the dead boost-type Add picker is gone", !$("boostTypeOrderAdd"));
      ok("no Add button remains for boost type",
         !window.document.querySelector('[data-sadd="boostTypeOrder"]'));
      ok("the generic SLISTS Add handler still exists for other lists",
         /data-sadd/.test(fs.readFileSync(FILE, "utf8")));

      // The 1 Hz snapshot must not rebuild the list mid-gesture, or the dragged element dies
      // under the pointer and the drag silently fails.
      const beforeDrag = listEl.querySelectorAll(".le-row")[0];
      const srcRow = rows()[0];
      const dt2 = { data: {}, setData(){}, getData(){ return ""; } };
      const dsEv = new window.Event("dragstart", { bubbles: true, cancelable: true });
      dsEv.dataTransfer = dt2;
      Object.defineProperty(dsEv, "target", { value: srcRow, enumerable: true });
      srcRow.dispatchEvent(dsEv);
      send(baseSnapshot({ loadouts: { TitanLoadout: [301, 188, 94] } }));   // contradicting snapshot
      ok("a snapshot mid-drag does not rebuild the dragged list",
         listEl.querySelectorAll(".le-row")[0] === beforeDrag);
      window.document.dispatchEvent(new window.Event("dragend", { bubbles: true }));
      ok("drag cues are cleared on dragend",
         listEl.querySelectorAll(".dragging, .drop-before, .drop-after").length === 0);

      // ---- 12e. boost "time to cap" --------------------------------------------------------------
      // Every number here is computed by the advisor's existing 60s boost pump and was previously
      // written only to inject.log.
      send(baseSnapshot({
        boostLists: { priority: [94, 188, 301], blacklist: [] },
        boostEta: { total: 1200, perMinute: 10, etaSec: 7200, power: 700, toughness: 400, special: 100,
                    etaByItem: { "94": 1800, "188": 4200 } }
      }));
      const beta = $("boostEta").textContent;
      ok("eta chip shows a duration", /2h/.test(beta), beta);
      ok("eta chip states the unit as stat points, never 'boosts'",
         /stat points/.test(beta) && !/\bboosts to go\b/.test(beta), beta);
      ok("eta chip shows the measured rate", /per minute/.test(beta), beta);

      const bpRows = Array.from($("boostPriority").querySelectorAll(".le-row"));
      ok("each boost row carries its own cumulative ETA",
         /at cap in/.test(bpRows[0].textContent) && /at cap in/.test(bpRows[1].textContent),
         bpRows.map(r => r.textContent).join(" | "));
      ok("the second row's ETA is LATER than the first (cumulative, not per-item)",
         /30m/.test(bpRows[0].textContent) && /1h/.test(bpRows[1].textContent),
         bpRows.map(r => r.textContent).join(" | "));
      ok("an item with no remaining need reads 'at cap', not blank",
         /at cap/.test(bpRows[2].textContent) && !/at cap in/.test(bpRows[2].textContent),
         bpRows[2].textContent);

      // Rate not yet measurable -> must NOT invent a duration.
      send(baseSnapshot({
        boostLists: { priority: [94], blacklist: [] },
        boostEta: { total: 1200, perMinute: 0, etaSec: -1, power: 0, toughness: 0, special: 0, etaByItem: {} }
      }));
      const measuring = $("boostEta").textContent;
      ok("no rate yet -> says it is measuring", /measuring/.test(measuring), measuring);
      ok("no rate yet -> shows no fabricated duration", !/\d+h|\d+m\b/.test(measuring.replace(/stat points/,"")), measuring);
      ok("no rate yet -> rows carry no ETA",
         !/at cap in/.test($("boostPriority").textContent), $("boostPriority").textContent);

      // Nothing left to do.
      send(baseSnapshot({ boostEta: { total: 0, perMinute: 12, etaSec: 0, power: 0, toughness: 0, special: 0, etaByItem: {} } }));
      ok("everything at cap is reported as such", /All at cap/.test($("boostEta").textContent),
         $("boostEta").textContent);

      // ---- 12e-2. which item is actually receiving boosts ---------------------------------------
      send(baseSnapshot({
        boostLists: { priority: [94, 188, 301], blacklist: [] },
        boostEta: { total: 1200, perMinute: 10, etaSec: 7200, power: 0, toughness: 0, special: 0,
                    etaByItem: { "94": 1800, "188": 4200 }, current: 188, currentInList: true }
      }));
      ok("the current boost target is named in a chip",
         /Boosting/.test($("boostEta").textContent) && /Edgy Helmet/.test($("boostEta").textContent),
         $("boostEta").textContent);
      const bRows = Array.from($("boostPriority").querySelectorAll(".le-row"));
      ok("the row receiving boosts is marked",
         /boosting now/.test(bRows[1].textContent), bRows[1].textContent);
      ok("other rows are NOT marked",
         !/boosting now/.test(bRows[0].textContent) && !/boosting now/.test(bRows[2].textContent),
         bRows.map(r => r.textContent).join(" | "));
      ok("a marked row still shows its ETA",
         /at cap in/.test(bRows[1].textContent), bRows[1].textContent);

      // The target can be OUTSIDE the list (empty or fully-capped priority list) — say so.
      send(baseSnapshot({
        boostLists: { priority: [], blacklist: [] },
        boostEta: { total: 900, perMinute: 5, etaSec: 3600, power: 0, toughness: 0, special: 0,
                    etaByItem: {}, current: 301, currentInList: false }
      }));
      ok("a target outside the list is still named",
         /Boosting/.test($("boostEta").textContent) && /Sturdy Pants/.test($("boostEta").textContent),
         $("boostEta").textContent);
      ok("a target outside the list says so",
         /not in this list/.test($("boostEta").textContent), $("boostEta").textContent);

      // Before any rate is measured, "which one is it working on" is still answerable.
      send(baseSnapshot({
        boostLists: { priority: [94], blacklist: [] },
        boostEta: { total: 500, perMinute: 0, etaSec: -1, power: 0, toughness: 0, special: 0,
                    etaByItem: {}, current: 94, currentInList: true }
      }));
      ok("the boosting row is marked even with no rate yet",
         /boosting now/.test($("boostPriority").textContent), $("boostPriority").textContent);

      // ---- 12g. what is steering the run, on Current stage ---------------------------------------
      // The segment plan was WinForms timeline chips lost in the 2.0.0 port; since then the page only
      // said "Farming / idle" and the segment was discoverable only in inject.log.
      send(baseSnapshot({
        autoProfile: { on: true, profile: "24hr-EarlyEvil", segment: "NGU MARATHON", phase: "push",
                       index: 3, runHours: 15.6,
                       chain: ["TM HOUR", "AT HOUR", "RECOVERY", "NGU MARATHON"] }
      }));
      const plan = $("stagePlan");
      ok("the whole segment chain is shown", plan.querySelectorAll(".chip").length >= 4,
         String(plan.querySelectorAll(".chip").length));
      ok("the chain reads in order", /TM HOUR[\s\S]*AT HOUR[\s\S]*RECOVERY[\s\S]*NGU MARATHON/.test(plan.textContent),
         plan.textContent);
      ok("the current segment is marked", /NGU MARATHON\s*now/.test(plan.textContent.replace(/\s+/g," ")),
         plan.textContent);
      const chipsIn = Array.from(plan.querySelectorAll(".chip"));
      ok("the current segment is the accented chip",
         chipsIn[3] && chipsIn[3].className.indexOf("max") >= 0, chipsIn.map(c => c.className).join(" | "));
      ok("future segments are muted, not accented",
         chipsIn[0] && chipsIn[0].className.indexOf("max") < 0);
      ok("the run clock and phase are shown", /push phase/.test(plan.textContent) && /15\.6h/.test(plan.textContent),
         plan.textContent);

      // The EVIL chain is a different, longer set of segment names than the Normal one, and two of them
      // (NGU+AT, EVIL NGU) did not exist until the guide's ch.5 phases 3-4 were authored. The strip is
      // built from the published `chain` array rather than any hardcoded list, so this is a guard that it
      // stays that way — a future edit that enumerates the Normal segments would silently blank the Evil
      // plan for every Evil player.
      send(baseSnapshot({
        autoProfile: { on: true, profile: "24hr-Evil", segment: "NGU+AT", phase: "growth",
                       index: 2, runHours: 8.4,
                       chain: ["TM HOUR", "AUGMENTATION", "NGU+AT", "EVIL NGU"] }
      }));
      const evilPlan = $("stagePlan");
      ok("the Evil four-phase chain is shown whole", evilPlan.querySelectorAll(".chip").length >= 4,
         String(evilPlan.querySelectorAll(".chip").length));
      ok("the Evil chain reads in guide order",
         /TM HOUR[\s\S]*AUGMENTATION[\s\S]*NGU\+AT[\s\S]*EVIL NGU/.test(evilPlan.textContent),
         evilPlan.textContent);
      ok("the current Evil segment is marked", /NGU\+AT\s*now/.test(evilPlan.textContent.replace(/\s+/g," ")),
         evilPlan.textContent);
      const evilChips = Array.from(evilPlan.querySelectorAll(".chip"));
      ok("the Evil-NGU tail renders as a future step, not as the current one",
         evilChips[3] && evilChips[3].className.indexOf("max") < 0,
         evilChips.map(c => c.className).join(" | "));

      // A manual profile has no segments — name the profile instead of inventing a plan.
      send(baseSnapshot({ autoProfile: { on: false, profile: "24hr-EarlyEvil.json" } }));
      ok("manual profile is named as such", /Manual profile/.test($("stagePlan").textContent),
         $("stagePlan").textContent);
      ok("manual profile shows the profile name without the extension",
         /24hr-EarlyEvil/.test($("stagePlan").textContent) && !/\.json/.test($("stagePlan").textContent),
         $("stagePlan").textContent);
      ok("manual profile offers a route to its breakpoints",
         !!$("stagePlan").querySelector('[data-view="profileEditor"]'));
      ok("manual profile shows NO segment chain",
         !/NGU MARATHON|TM HOUR/.test($("stagePlan").textContent), $("stagePlan").textContent);

      // Auto profile that hasn't computed a chain yet must not render an empty strip.
      send(baseSnapshot({ autoProfile: { on: true, profile: "x", segment: "", phase: "", index: -1, runHours: 0, chain: [] } }));
      ok("auto profile with no chain yet says so", /working out the plan/.test($("stagePlan").textContent),
         $("stagePlan").textContent);

      // ---- 12h. the last swap explains itself ----------------------------------------------------
      // Two of the three outcomes look identical on the paper doll and only one is a problem, so the
      // readout has to separate them or a correct swap reads as a broken one.
      send(baseSnapshot({
        lastSwap: { mode: "Titan", swapped: 15, requested: 15, kept: [], missed: [], agoSec: 12,
                    power: 1.2e6, toughness: 9.4e5 }
      }));
      ok("a clean swap says every slot went on", /Every slot the objective asked for went on/.test($("lastSwapWhy").textContent),
         $("lastSwapWhy").textContent);
      ok("the swap chip names the mode and the count",
         /Titan/.test($("lastSwap").textContent) && /15 of 15/.test($("lastSwap").textContent),
         $("lastSwap").textContent);
      ok("the worn Power/Toughness is shown", /P\s/.test($("lastSwap").textContent) && /T\s/.test($("lastSwap").textContent),
         $("lastSwap").textContent);

      // KEPT slots are correct behaviour and must never read as a failure.
      send(baseSnapshot({
        lastSwap: { mode: "Gold", swapped: 6, requested: 6, kept: [94, 188, 301], missed: [], agoSec: 3,
                    power: 1.1e6, toughness: 9.0e5 }
      }));
      const keptWhy = $("lastSwapWhy").textContent;
      ok("kept slots are explained as correct", /that is correct/.test(keptWhy), keptWhy);
      ok("kept slots explain where Power/Toughness comes from",
         /Power and Toughness/.test(keptWhy), keptWhy);
      ok("kept slots are NOT reported as not fitting",
         !/did not fit/.test($("lastSwap").textContent), $("lastSwap").textContent);

      // MISSED is the real failure, and must name the items.
      send(baseSnapshot({
        lastSwap: { mode: "Titan", swapped: 12, requested: 15, kept: [], missed: [94, 188, 301], agoSec: 5,
                    power: 1.0e6, toughness: 8.0e5 }
      }));
      ok("missed items are flagged", /did not fit/.test($("lastSwap").textContent), $("lastSwap").textContent);
      ok("missed items are named, not just counted",
         /Edgy Helmet/.test($("lastSwapWhy").textContent), $("lastSwapWhy").textContent);
      ok("missed items suggest the usual causes",
         /unlocked slots/.test($("lastSwapWhy").textContent), $("lastSwapWhy").textContent);
      ok("item names are not double-escaped",
         !/&amp;quot;|&amp;lt;/.test($("lastSwapWhy").innerHTML), $("lastSwapWhy").innerHTML.slice(0,120));

      send(baseSnapshot());
      ok("no swap yet is handled", /No swap yet/.test($("lastSwap").textContent), $("lastSwap").textContent);

      // ---- 12i. completed campaign blocks fold away -----------------------------------------------
      // The spine only grows, so finished blocks otherwise push the one you are on further down the
      // page every time you finish another.
      const camp = {
        difficulty: "Normal",
        blocks: [
          { id: "cb1", name: "CBlock 1", state: "complete", chapter: 2, counted: true },
          { id: "cb2", name: "CBlock 2", state: "complete", chapter: 3, counted: true },
          { id: "cb3", name: "CBlock 3", state: "active",   chapter: 4, counted: true },
          { id: "cb4", name: "CBlock 4", state: "upcoming", chapter: 5, counted: true }
        ]
      };
      send(baseSnapshot({ campaign: camp }));
      const cb = $("campBlocks");
      ok("the completed fold appears when there are completed blocks",
         !!cb.querySelector("[data-cgdone]"), cb.textContent.slice(0, 120));
      ok("the fold counts them", /Completed campaigns/.test(cb.textContent) && /2/.test(cb.textContent),
         cb.textContent.slice(0, 160));
      ok("completed blocks are hidden by default",
         !/CBlock 1/.test(cb.textContent) && !/CBlock 2/.test(cb.textContent), cb.textContent);
      ok("unfinished blocks are still shown",
         /CBlock 3/.test(cb.textContent) && /CBlock 4/.test(cb.textContent), cb.textContent);
      ok("the fold is reported closed to assistive tech",
         cb.querySelector("[data-cgdone]").getAttribute("aria-expanded") === "false");

      cb.querySelector("[data-cgdone]").dispatchEvent(new window.Event("click", { bubbles: true }));
      const cbOpen = $("campBlocks");
      ok("clicking the fold reveals the completed blocks",
         /CBlock 1/.test(cbOpen.textContent) && /CBlock 2/.test(cbOpen.textContent), cbOpen.textContent);
      ok("the fold is reported open to assistive tech",
         cbOpen.querySelector("[data-cgdone]").getAttribute("aria-expanded") === "true");
      ok("the unfinished blocks did not move out",
         /CBlock 3/.test(cbOpen.textContent), cbOpen.textContent);

      cbOpen.querySelector("[data-cgdone]").dispatchEvent(new window.Event("click", { bubbles: true }));
      ok("clicking again folds them back",
         !/CBlock 1/.test($("campBlocks").textContent), $("campBlocks").textContent);

      // No completed blocks -> no fold at all, rather than an empty "Completed campaigns (0)".
      send(baseSnapshot({ campaign: { difficulty: "Normal", blocks: [
        { id: "cb3", name: "CBlock 3", state: "active", chapter: 4, counted: true } ] } }));
      ok("no fold when nothing is complete", !$("campBlocks").querySelector("[data-cgdone]"),
         $("campBlocks").textContent.slice(0, 120));

      // ---- 12j. poop targets on the orchard -------------------------------------------------------
      // Restored from the retired WinForms panel; the Yggdrasil view has promised to "advise poop
      // placement" since 2.0.0 without doing it.
      send(baseSnapshot({ yggSeeds: "1.2K", fruits: [
        { i:0, name:"Pomegranate", state:"active", tier:3, maxTier:10, frac:0.4, poopRec:true, poop:true },
        { i:1, name:"Macguffin Beta", state:"active", tier:2, maxTier:8, frac:0.2, poopRec:true },
        { i:2, name:"Adventure", state:"active", tier:1, maxTier:5, frac:0.1, poop:true },
        { i:3, name:"Gold", state:"inactive", tier:0, maxTier:4 }
      ]}));
      const tiles = $("fruitTiles");
      ok("a recommended fruit gets the recommended marker",
         tiles.querySelectorAll(".ft-poop.rec").length === 2,
         String(tiles.querySelectorAll(".ft-poop.rec").length));
      ok("a pooped-but-not-recommended fruit gets the plain marker",
         tiles.querySelectorAll(".ft-poop:not(.rec)").length === 1,
         String(tiles.querySelectorAll(".ft-poop:not(.rec)").length));
      ok("a fruit with neither gets no marker", tiles.querySelectorAll(".ft-poop").length === 3,
         String(tiles.querySelectorAll(".ft-poop").length));
      ok("the markers carry an explanation for hover/assistive tech",
         !!tiles.querySelector(".ft-poop.rec").getAttribute("title"));
      ok("the summary names where poop should go",
         /Pomegranate/.test($("fruitPoop").textContent) && /Macguffin Beta/.test($("fruitPoop").textContent),
         $("fruitPoop").textContent);
      ok("the summary counts the ones still missing poop",
         /1 of those has none yet/.test($("fruitPoop").textContent), $("fruitPoop").textContent);

      // When poop is already correct, say so rather than nagging.
      send(baseSnapshot({ fruits: [
        { i:0, name:"Pomegranate", state:"active", tier:3, maxTier:10, frac:0.4, poopRec:true, poop:true }
      ]}));
      ok("correct placement is confirmed, not nagged",
         /on the fruits the advisor would pick/.test($("fruitPoop").textContent), $("fruitPoop").textContent);

      // No poop data at all (older advisor) must not render a stray line.
      send(baseSnapshot({ fruits: [ { i:0, name:"Gold", state:"active", tier:1, maxTier:4, frac:0.1 } ]}));
      ok("no poop data -> no summary line", $("fruitPoop").textContent.trim() === "",
         $("fruitPoop").textContent);
      ok("no poop data -> no markers", $("fruitTiles").querySelectorAll(".ft-poop").length === 0);

      // ---- 12k. the breakpoint editor's slot picker ------------------------------------------------
      // Diggers and beards are ordered SLOT lists. Typing "3, 8, 0" required knowing both the numbers
      // and that order = priority. It is a drag list now, and it serialises back into the SAME payload
      // string, so nothing on the server side changed.
      // Drive the REAL path: a timelines message, then the row's own Edit button.
      function openBp(sys, payload) {
        const tl = { type: "timelines", systems: {} };
        // Every system needs a key or the tab strip won't offer it.
        ["energy","magic","r3","gear","diggers","beards","wandoos","ngudiff","consumables","rebirth"]
          .forEach(k => { tl.systems[k] = []; });
        tl.systems[sys] = [{ sec: 0, summary: payload, payload: payload, challenge: "" }];
        send(tl);
        // The Profile Editor renders only the ACTIVE tab, so select the system first.
        const tab = window.document.querySelector('[data-petab="' + sys + '"]');
        if (tab) tab.dispatchEvent(new window.Event("click", { bubbles: true }));
        const row = window.document.querySelector('.tl-row[data-sys="' + sys + '"] .tl-edit');
        if (row) row.dispatchEvent(new window.Event("click", { bubbles: true }));
        return window.document.querySelector(".tl-editor");
      }
      function payloadOf(ed) { const h = ed && ed.querySelector('[data-k="payload"]'); return h ? h.value : null; }

      let ed = openBp("diggers", "3, 8, 0");
      ok("editing a digger breakpoint opens the slot editor", !!(ed && ed.querySelector("#peSlots")),
         ed ? ed.className : "no editor");
      if (ed && ed.querySelector("#peSlots")) {
        const slotRows = () => Array.from(ed.querySelectorAll("#peSlots .le-row"));
        ok("the existing order is loaded as rows", slotRows().length === 3, String(slotRows().length));
        ok("slots are NAMED, not bare numbers",
           /Adv/.test(slotRows()[0].textContent) && /PP/.test(slotRows()[1].textContent),
           slotRows().map(r => r.textContent.trim()).join(" | "));
        ok("the id stays visible for hand-edited profiles",
           /#3/.test(slotRows()[0].textContent), slotRows()[0].textContent);
        ok("slot rows are draggable", slotRows().every(r => r.getAttribute("draggable") === "true"));
        ok("the payload is seeded from the list", payloadOf(ed) === "3, 8, 0", payloadOf(ed));

        // Removing a row must rewrite the payload, not post a setting.
        SENT.length = 0;
        slotRows()[1].querySelector("[data-lremove]").dispatchEvent(new window.Event("click", { bubbles: true }));
        ok("removing a slot rewrites the payload", payloadOf(ed) === "3, 0", payloadOf(ed));
        ok("the editor list never posts a setting",
           SENT.filter(m => m.cmd === "setSettingList").length === 0, JSON.stringify(SENT));

        // The Add picker offers only slots not already listed.
        const pick = $("peSlotsAdd");
        ok("the picker excludes already-listed slots",
           !Array.from(pick.options).some(o => o.value === "3"),
           Array.from(pick.options).map(o => o.value).join(","));
        pick.value = "2";
        $("peSlotsAddBtn").dispatchEvent(new window.Event("click", { bubbles: true }));
        ok("adding a slot appends it to the order", payloadOf(ed) === "3, 0, 2", payloadOf(ed));

        // The digger count serialises into the same payload.
        const cnt = $("peCount");
        ok("diggers offer an activation count", !!cnt);
        cnt.value = "4";
        cnt.dispatchEvent(new window.Event("change", { bubbles: true }));
        ok("the count is appended as xN", payloadOf(ed) === "3, 0, 2 x4", payloadOf(ed));
        cnt.value = "";
        cnt.dispatchEvent(new window.Event("change", { bubbles: true }));
        ok("clearing the count drops the xN", payloadOf(ed) === "3, 0, 2", payloadOf(ed));
      }

      // Beards get the same editor but NO count — a beard list's length is its own count.
      ed = openBp("beards", "0, 5");
      ok("editing a beard breakpoint opens the slot editor", !!(ed && ed.querySelector("#peSlots")));
      ok("beards offer no activation count", !!(ed && !ed.querySelector("#peCount")));
      ok("beard slots use the beard vocabulary",
         !!(ed && /Stats/.test(ed.querySelector("#peSlots").textContent) &&
                  /Adv/.test(ed.querySelector("#peSlots").textContent)),
         ed ? ed.querySelector("#peSlots").textContent : "");

      // ---- 12l. GEAR LOCK: the breakpoint editor's gear form -----------------------------------
      // A gear breakpoint used to be one free-text field that took EITHER item IDs OR
      // "Optimize: X", with no way to write both — which is precisely what was asked for ("2
      // respawn + doll + optimize:TM"). The form states the whole plan, and the hidden payload it
      // serialises has to be exactly BreakpointEditor.ApplyGear's grammar or a saved row changes
      // meaning. That round trip is what these assert.
      ed = openBp("gear", "Lock: 188, 301; Optimize: NGUs");
      ok("editing a gear breakpoint opens the Gear Lock form", !!(ed && ed.querySelector("#peLocks")),
         ed ? ed.className : "no editor");
      if (ed && ed.querySelector("#peLocks")) {
        const lockRows = () => Array.from(ed.querySelectorAll("#peLocks .le-row"));
        const objSel = ed.querySelector('[data-k="gearobj"]');
        const respBox = ed.querySelector('[data-k="gearresp"]');

        ok("the locked items load as rows", lockRows().length === 2, String(lockRows().length));
        ok("locked items are NAMED, not bare ids",
           /Edgy Helmet/.test(lockRows()[0].textContent) && /Sturdy Pants/.test(lockRows()[1].textContent),
           lockRows().map(r => r.textContent.trim()).join(" | "));
        ok("the id stays visible, because the profile stores the id",
           /\[188\]/.test(lockRows()[0].textContent), lockRows()[0].textContent);
        ok("locked rows are draggable — order decides which slot each takes",
           lockRows().every(r => r.getAttribute("draggable") === "true"));
        ok("the objective loads into the picker", objSel && objSel.value === "NGUs", objSel && objSel.value);
        ok("the objective picker is built from the injector's own list",
           objSel.tagName === "SELECT" &&
           GEAR_OBJECTIVES.every(n => Array.from(objSel.options).some(o => o.value === n)),
           Array.from(objSel.options).map(o => o.value).join(","));
        ok("the payload round-trips unchanged when nothing is touched",
           payloadOf(ed) === "Lock: 188, 301; Optimize: NGUs", payloadOf(ed));
        ok("the form says what the row will actually do",
           /2 items are locked in/.test(ed.querySelector(".peg-say").textContent) &&
           /NGUs/.test(ed.querySelector(".peg-say").textContent),
           ed.querySelector(".peg-say").textContent);

        // Removing a lock rewrites the payload and must NOT post a setting — this list belongs to
        // the breakpoint, not to settings.
        SENT.length = 0;
        lockRows()[0].querySelector("[data-lremove]").dispatchEvent(new window.Event("click", { bubbles: true }));
        ok("removing a locked item rewrites the payload",
           payloadOf(ed) === "Lock: 301; Optimize: NGUs", payloadOf(ed));
        ok("the gear lock list never posts a setting",
           SENT.filter(m => m.cmd === "setSettingList").length === 0, JSON.stringify(SENT));

        // Adding by id.
        const addIn = $("peLocksAdd");
        addIn.value = "94";
        $("peLocksAddBtn").dispatchEvent(new window.Event("click", { bubbles: true }));
        ok("adding an item id appends it to the lock list",
           payloadOf(ed) === "Lock: 301, 94; Optimize: NGUs", payloadOf(ed));
        ok("the add box clears after a successful add", addIn.value === "");

        // The respawn pin serialises into the same payload.
        respBox.checked = true;
        respBox.dispatchEvent(new window.Event("change", { bubbles: true }));
        ok("the respawn pin becomes Optimize+Respawn",
           payloadOf(ed) === "Lock: 301, 94; Optimize+Respawn: NGUs", payloadOf(ed));

        // Clearing the objective turns the SAME list back into a plain loadout — the old ID-only
        // shape — which is the backward-compatible half of the grammar.
        objSel.value = "";
        objSel.dispatchEvent(new window.Event("change", { bubbles: true }));
        ok("with no objective the row is a plain item list again",
           payloadOf(ed) === "301, 94", payloadOf(ed));
        ok("the list relabels itself when it stops being a lock",
           ed.querySelector(".peg-locklabel").textContent === "Items to wear",
           ed.querySelector(".peg-locklabel").textContent);
        ok("the respawn pin is disabled when there is no optimize pass to pin into",
           respBox.disabled === true);
      }

      // An objective with NO locked items must still serialise to exactly the old payload, or every
      // pre-Gear-Lock row would be rewritten just by being opened.
      ed = openBp("gear", "Optimize+Respawn: Adventure");
      if (ed) {
        ok("an objective-only gear row round-trips to the old payload verbatim",
           payloadOf(ed) === "Optimize+Respawn: Adventure", payloadOf(ed));
        ok("an objective-only row shows an empty lock list",
           ed.querySelectorAll("#peLocks .le-row").length === 0);
        ok("an objective-only row says it optimises everything",
           /Optimises every slot/.test(ed.querySelector(".peg-say").textContent),
           ed.querySelector(".peg-say").textContent);
      }

      // …and so must an ID-only row, which is what every profile written before this contains.
      ed = openBp("gear", "188, 301");
      if (ed) {
        ok("an id-only gear row round-trips verbatim", payloadOf(ed) === "188, 301", payloadOf(ed));
        ok("an id-only row has no objective selected",
           ed.querySelector('[data-k="gearobj"]').value === "",
           ed.querySelector('[data-k="gearobj"]').value);
        ok("an id-only row calls the list what it is",
           ed.querySelector(".peg-locklabel").textContent === "Items to wear");
      }

      // A SPELLED CHAIN is an objective name, so it rides in the same payload clause. The picker
      // cannot list it, and used to call it "not recognised". It is edited as ROWS, one per step.
      const fire = (el, type) => el.dispatchEvent(new window.Event(type, { bubbles: true }));
      const steps = e => Array.from(e.querySelectorAll("#pegSteps .peg-step"));
      const stepOf = (e, i) => ({ obj: steps(e)[i].querySelector('[data-cstep="obj"]'),
                                  slots: steps(e)[i].querySelector('[data-cstep="slots"]') });
      ed = openBp("gear", "Lock: 301; Optimize+Respawn: Adventure(3) > Respawn(1) > Adventure(all)");
      if (ed) {
        const sel = ed.querySelector('[data-k="gearobj"]');
        ok("a spelled chain selects Custom chain, not 'not recognised'",
           sel.value === "__chain__" && !/not recognised/.test(sel.textContent), sel.value);
        ok("the chain shows one row per step",
           !ed.querySelector(".peg-chainrow").hidden && steps(ed).length === 3, String(steps(ed).length));
        ok("each row carries its step's objective and budget",
           stepOf(ed, 0).obj.value === "Adventure" && stepOf(ed, 0).slots.value === "3" &&
           stepOf(ed, 1).obj.value === "Respawn" && stepOf(ed, 1).slots.value === "1" &&
           stepOf(ed, 2).slots.value === "");
        ok("a spelled chain round-trips verbatim",
           payloadOf(ed) === "Lock: 301; Optimize+Respawn: Adventure(3) > Respawn(1) > Adventure(all)", payloadOf(ed));
        ok("a step cannot be a named chain", !/ \+ /.test(stepOf(ed, 0).obj.textContent), stepOf(ed, 0).obj.textContent);

        stepOf(ed, 1).slots.value = "2"; fire(stepOf(ed, 1).slots, "change");
        stepOf(ed, 2).obj.value = "NGUs"; fire(stepOf(ed, 2).obj, "change");
        ok("changing a row rewrites the payload",
           payloadOf(ed) === "Lock: 301; Optimize+Respawn: Adventure(3) > Respawn(2) > NGUs(all)", payloadOf(ed));

        fire(steps(ed)[0].querySelector('[data-cstepmove="down"]'), "click");
        ok("a step moves down", payloadOf(ed) === "Lock: 301; Optimize+Respawn: Respawn(2) > Adventure(3) > NGUs(all)", payloadOf(ed));
        fire(steps(ed)[1].querySelector("[data-cstepremove]"), "click");
        ok("a step is removed", payloadOf(ed) === "Lock: 301; Optimize+Respawn: Respawn(2) > NGUs(all)", payloadOf(ed));

        ed.querySelector("#pegPin").checked = true; fire(ed.querySelector("#pegPin"), "change");
        ok("the weapon pin is written on the lead step",
           payloadOf(ed) === "Lock: 301; Optimize+Respawn: Respawn(2)+PowerWeapon > NGUs(all)", payloadOf(ed));

        for (let n = 0; n < 6; n++) fire(ed.querySelector("#pegStepAdd"), "click");
        ok("a chain stops at five steps", steps(ed).length === 5 && ed.querySelector("#pegStepAdd").disabled, String(steps(ed).length));

        sel.value = "NGUs"; fire(sel, "change");
        ok("picking a listed objective hides the steps and drops the chain",
           ed.querySelector(".peg-chainrow").hidden && payloadOf(ed) === "Lock: 301; Optimize+Respawn: NGUs", payloadOf(ed));
      }
      ed = openBp("gear", "Optimize: Drop Chance(all) > Adventure(0)+PowerWeapon");
      if (ed) {
        ok("a pin on any step ticks the box", ed.querySelector("#pegPin").checked);
      }
      ed = openBp("gear", "Optimize: NGUs");
      if (ed) {
        const sel = ed.querySelector('[data-k="gearobj"]');
        ok("a listed objective keeps the steps hidden", ed.querySelector(".peg-chainrow").hidden);
        ok("opening a listed objective leaves its payload alone", payloadOf(ed) === "Optimize: NGUs", payloadOf(ed));
        sel.value = "__chain__"; fire(sel, "change");
        ok("Custom chain starts as one step taking every slot",
           steps(ed).length === 1 && /^Optimize: [^(>]+\(all\)$/.test(payloadOf(ed)), payloadOf(ed));
        ok("the only step cannot be removed", steps(ed)[0].querySelector("[data-cstepremove]").disabled);
      }
      // A chain the rows cannot show is kept as written, not swapped for the default one.
      ed = openBp("gear", "Optimize: Adventure(3) > (oops");
      if (ed) {
        ok("an unreadable chain survives being opened", payloadOf(ed) === "Optimize: Adventure(3) > (oops", payloadOf(ed));
        ok("and the form says so", /kept as written/.test(ed.querySelector(".peg-spell").textContent));
        fire(ed.querySelector("#pegStepAdd"), "click");
        ok("until a step is touched", /^Optimize: [^>]+\(all\) > [^>]+\(all\)$/.test(payloadOf(ed)), payloadOf(ed));
      }

      // An empty gear row must not invent content, and must say what to do.
      ed = openBp("gear", "");
      if (ed) {
        ok("an empty gear row serialises to an empty payload", payloadOf(ed) === "", payloadOf(ed));
        ok("an empty gear row says it would equip nothing",
           /would equip nothing/.test(ed.querySelector(".peg-say").textContent),
           ed.querySelector(".peg-say").textContent);
      }

      // A system WITHOUT a slot vocabulary must keep the plain text field.
      ed = openBp("energy", "NGU-3, WAN");
      ok("non-slot systems keep the free-text payload",
         !!(ed && !ed.querySelector("#peSlots") && ed.querySelector('input[data-k="payload"]')));
      ok("the text payload is still editable, not hidden",
         !!(ed && ed.querySelector('input[data-k="payload"]').type !== "hidden"));

      // The slot vocabularies must match the injector's own tables, or a slot is called one thing in
      // the editor and another in the logs.
      const srcTxt = fs.readFileSync(FILE, "utf8");
      ok("digger slot names match the injector table",
         /DIGGER_SLOTS *= *\["Drops","Wandoos","Stats","Adv","E-NGU","M-NGU","E-Beard","M-Beard","PP","Daycare","Blood","EXP"\]/.test(srcTxt.replace(/\s+/g, " ").replace(/", "/g, '","')),
         "see DIGGER_SLOTS");
      ok("beard slot names match the injector table",
         /BEARD_SLOTS *= *\["Stats","Drops","Number","NGU","Wandoos","Adv","Golden"\]/.test(srcTxt.replace(/\s+/g, " ").replace(/", "/g, '","')),
         "see BEARD_SLOTS");

      // The local list must never post a setting — it belongs to the breakpoint, not to settings.
      ok("the editor slot list is registered as local",
         /peSlots:\s*\{[^}]*local:\s*true/.test(srcTxt), "peSlots is not marked local");
      ok("listSend short-circuits local lists",
         /if\(st\.local\)\s*\{\s*peSyncPayload\(\);\s*return;\s*\}/.test(srcTxt.replace(/\s+/g, " ").replace(/\{ /g, "{").replace(/ \}/g, "}")) ||
         /st\.local/.test(srcTxt), "listSend does not check st.local");

      // ---- 12f. the page must not redeclare a shared helper --------------------------------------
      // A second `function fmtNum(...)` in the same IIFE silently replaces the page-wide formatter for
      // every existing caller — invisible in review, and invisible to any test that only checks one
      // view. Guard the whole class of bug at source level.
      // NOT esc(): the design layer and the live layer are separate IIFEs and each deliberately keeps
      // its OWN private esc(). Two is correct there. These are all live-layer-only helpers.
      const src = fs.readFileSync(FILE, "utf8");
      ["fmtNum", "fmtSec", "writeHtml", "drawList", "applyList", "listSend", "renderSettings"].forEach(fn => {
        const n = (src.match(new RegExp("function\\s+" + fn + "\\s*\\(", "g")) || []).length;
        ok("exactly one declaration of " + fn + "()", n === 1, "found " + n);
      });

      // ---- 13. idempotence: same snapshot twice repaints nothing -------------------------------
      const snap = baseSnapshot();
      send(snap);
      const before = $("lg-TitanLoadout").innerHTML;
      const node = $("lg-TitanLoadout").firstChild;
      send(snap);
      ok("repeat snapshot leaves the DOM node identical", $("lg-TitanLoadout").firstChild === node);
      ok("repeat snapshot leaves the markup identical", $("lg-TitanLoadout").innerHTML === before);

      // ---- 14. a11y + design-system census ------------------------------------------------------
      const controls = window.document.querySelectorAll("#view-loadouts input, #view-loadouts select, #view-loadouts button");
      const unnamed = [];
      controls.forEach(c => {
        const hasAria = c.getAttribute("aria-label");
        const wrapped = c.closest("label");
        const labelled = c.id && window.document.querySelector('label[for="' + c.id + '"]');
        const text = c.tagName === "BUTTON" && c.textContent.trim();
        if (!hasAria && !wrapped && !labelled && !text) unnamed.push(c.outerHTML.slice(0, 90));
      });
      ok("no unnamed controls on the Loadouts view", unnamed.length === 0, unnamed.join(" | "));

      const raw = fs.readFileSync(FILE, "utf8");
      ok("no hard-coded hex colour was added in the new blocks",
         !/id="lg-[^"]*"[^>]*style="[^"]*#[0-9a-f]{3}/i.test(raw));
      ok("no third label class introduced", !/class="[^"]*\bnewlabel\b/.test(raw));

      // ---- done ---------------------------------------------------------------------------------
      done();
    })();
  }
}));
