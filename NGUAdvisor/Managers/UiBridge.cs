using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    /// <summary>
    /// Out-of-process modern-UI bridge — snapshots out (M1), commands in (M2).
    ///
    /// The injector stays headless: it publishes a JSON snapshot of advisor state over a named pipe
    /// ("NGUAdvisorUI") that the WebView2 companion host renders, and applies command JSON the UI sends back.
    ///
    /// Threading contract (mirrors the codebase's FileSystemWatcher -> volatile-flag pattern):
    ///   * <see cref="Publish"/> is called ONLY from the Unity main thread (Main's InvokeRepeating tick).
    ///     ALL game/Unity reads happen there, while building the JSON string.
    ///   * Two UNIDIRECTIONAL pipes (each synchronous): <see cref="PumpLoop"/> WRITES snapshots on
    ///     "NGUAdvisorUI"; <see cref="CmdLoop"/> READS command lines on "NGUAdvisorUICmd" and only
    ///     ENQUEUES them. Separate handles, so a parked read never serializes the writer (a single
    ///     synchronous duplex handle deadlocks; async pipes are unreliable under Mono). Neither pipe
    ///     thread ever touches Unity objects.
    ///   * <see cref="DrainCommands"/> applies queued commands on the Unity main thread (Main.Update),
    ///     each in its own try/catch — the AdvisorApply.RunStep containment pattern.
    ///
    /// Mono teardown lesson (from the WAMIAdvisor pipe bridge): Dispose cannot interrupt a thread parked
    /// in the native WaitForConnection(); we must ALSO "poke" the pipe with a throwaway local client so
    /// the accept thread unblocks — a thread stuck in native pipe code during runtime teardown crashes
    /// the process on exit.
    /// </summary>
    internal sealed class UiBridge : IDisposable
    {
        // snapshots: injector -> UI (write-only) / commands: UI -> injector (read-only).
        // Both carry the instance suffix from NGUADVISOR_INSTANCE, so a bench game's advisor cannot
        // serve the live companion (or be served by it). Unset — every normal install — leaves these
        // as the literal "NGUAdvisorUI" / "NGUAdvisorUICmd" they have always been. See AdvisorInstance.
        private static readonly string PipeName = AdvisorInstance.SnapshotPipe;
        private static readonly string CmdPipeName = AdvisorInstance.CommandPipe;
        private const int ProtocolVersion = 1;
        private const int SparkPoints = 24;
        private const int FeedMax = 20;
        // M7 (audit): a command is small JSON — cap the buffered line so a client that never sends a
        // newline can't grow memory without limit (DoS). Over-long lines are dropped.
        private const int MaxCommandLineChars = 65536;
        // M7: cap ids accepted in one setSettingList so a crafted command can't build an oversized array.
        private const int MaxListIds = 4096;

        private Thread _pumpThread;
        private volatile bool _stopping;
        private volatile string _latest;                 // newest snapshot JSON; atomic ref swap
        private volatile string _pumpError;              // error stashed by the pipe thread; logged on the main thread
        private volatile bool _pipeSecUnavailable;       // M7: set on the pipe thread if the ACL overload isn't supported (Mono)
        private volatile bool _secNoteLogged;            // M7: guards the one-time main-thread "ACL unavailable" log
        private volatile NamedPipeServerStream _server;     // snapshot (out) accept/serve stream
        private volatile NamedPipeServerStream _cmdServer;  // command (in) accept/serve stream
        private Thread _cmdThread;                          // command accept/read loop
        private long _seq;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();  // command lines from the UI

        // Bridge-owned feed ring: the advisor has no multi-entry feed (Activity is a single slot),
        // so we accumulate distinct Activity.Current outcomes here, newest last.
        private readonly LinkedList<JSONObject> _feed = new LinkedList<JSONObject>();
        private int _lastActivitySeq = int.MinValue;
        private string[] _profilesCache;                 // profile names, refreshed ~every 5s (avoid per-tick disk)
        // Last published allocation signature per pool. An unchanged tick then costs one string compare
        // rather than a lane array — see the `pools` node for why that gate is measured, not preferred.
        // _poolsEverSent forces a full first payload, so a page that connects mid-run is never empty.
        private readonly Dictionary<string, string> _poolSig = new Dictionary<string, string>();
        private bool _poolsEverSent;
        // One-shot "show this view" request from an in-game hotkey (F9 -> Profile Editor). Snapshots repeat
        // every second, so the page acts on a CHANGE of `seq`; the node also expires (NavTtlSnaps) so a
        // companion that opens minutes later doesn't jump to a view the user asked for long ago.
        private string _navView;
        private long _navSeq;
        private long _navUntilSeq;
        private const int NavTtlSnaps = 15;              // ~15 s at one snapshot/second
        private JSONObject _macguffinsCache;             // static macguffin id->name map (FavoredMacguffin dropdown); built once
        private JSONObject _zonesCache;                  // static non-titan zone id->name map (SnipeZone / GearHuntZone dropdowns); built once
        private JSONObject _advEnemiesCache;             // static adventure enemy spriteId->name map (blacklist picker); built once
        private JSONObject _editorCatalogCache;          // static: what the profile editor's pickers offer (EditorCatalog)
        private JSONArray _gearChainNamesCache;          // the named chains among them: selectable, but not usable as a chain STEP
        private JSONArray _gearObjectivesCache;          // static gear-objective name list (loadout Advisor dropdowns); built once
        private JSONArray _transformMetaCache;           // transform-chain {name, step} descriptors (grid labels); refreshed ~every 5s
        private JSONArray _titanAkCache;                 // per-titan autokill readiness chips; refreshed ~every 5s (reflection-heavy)

        // The respawn-clock reader stamped onto that cache EVERY tick (see the `titanAk` node). Cached so the
        // per-tick call doesn't allocate a delegate for nothing, but bound LAZILY inside Publish rather than
        // in a static initializer: ZoneHelpers captures `Main.Character` into a static readonly field of its
        // own, so nothing here may risk forcing that class's initializer to run at an earlier moment than it
        // already does. By the time Publish runs, the game is up.
        private Func<int, float?> _titanSpawnReader;
        private JSONObject _cardMetaCache;               // static card meta (bonus types / rarities / costs / sort vocab); built once
        private int[] _gearNowIds;                       // main-gear best set published this tick, so itemNames can name it too
        private int[] _lastSwapIds;                      // kept + missed ids from the last swap, so itemNames can name them
        private int[] _boostCurrentId;                   // the item currently receiving boosts; may be OUTSIDE the priority list, so name it explicitly
        private JSONObject _itemNamesCache;              // gear id->name map for the ids the snapshot references; rebuilt only when that id SET changes
        private string _itemNamesSig;                    // the id set _itemNamesCache was built from ("94,110,116,...")
        private bool _itemNamesComplete;                 // false while any id failed to resolve (item table not up yet) -> periodic retry
        private JSONObject _invVerdictCache;             // keep/trash verdict node; rebuilt only when a new Compute() lands
        private DateTime _invVerdictAt;                  // InventoryAdvisor.Last.At the cache was built from
        private JSONObject _campaignCache;               // CBlock campaign node; rebuilt ~every 5s (parses profile JSON)
        private List<CampaignTables.Supply> _campaignSupplyCache;  // parsed campaign profiles; re-read only when the files change
        private string _campaignFileSig;                 // "<path>|<ticks>|<len>;..." the supply cache was parsed from

        // Bridge-sampled sparkline rings: one growth-rate value per publish, per metric.
        private readonly Dictionary<string, Queue<double>> _spark = new Dictionary<string, Queue<double>>();

        // Per-system two-layer toggles: AUTOMATION (act gate = Manage*/Auto*/Cast*/Combat*) and DECISIONS
        // (advisor strategy = Advisor*). Both are change-guarded auto-save setters; all ANDed with the
        // GlobalEnabled master. One table drives both the snapshot (read state) and commands (apply).
        private sealed class SysToggle
        {
            public readonly string Id;
            public readonly Func<SavedSettings, bool> GetAuto;
            public readonly Action<SavedSettings, bool> SetAuto;
            public readonly Func<SavedSettings, bool> GetAdvisor;
            public readonly Action<SavedSettings, bool> SetAdvisor;
            public SysToggle(string id, Func<SavedSettings, bool> ga, Action<SavedSettings, bool> sa,
                             Func<SavedSettings, bool> gd, Action<SavedSettings, bool> sd)
            { Id = id; GetAuto = ga; SetAuto = sa; GetAdvisor = gd; SetAdvisor = sd; }
        }

        private static readonly SysToggle[] Toggles =
        {
            new SysToggle("titans",    s => s.ManageTitans,       (s, v) => s.ManageTitans = v,       s => s.AdvisorTitans,      (s, v) => s.AdvisorTitans = v),
            new SysToggle("adventure", s => s.CombatEnabled,      (s, v) => s.CombatEnabled = v,      s => s.AdvisorZones,       (s, v) => s.AdvisorZones = v),
            new SysToggle("gear",      s => s.ManageGear,         (s, v) => s.ManageGear = v,         s => s.AdvisorGearRefresh, (s, v) => s.AdvisorGearRefresh = v),
            new SysToggle("wandoos",   s => s.ManageWandoos,      (s, v) => s.ManageWandoos = v,      s => s.AdvisorWandoosOS,   (s, v) => s.AdvisorWandoosOS = v),
            new SysToggle("diggers",   s => s.ManageDiggers,      (s, v) => s.ManageDiggers = v,      s => s.AdvisorDiggers,     (s, v) => s.AdvisorDiggers = v),
            new SysToggle("beards",    s => s.ManageBeards,       (s, v) => s.ManageBeards = v,       s => s.AdvisorBeards,      (s, v) => s.AdvisorBeards = v),
            new SysToggle("yggdrasil", s => s.ManageYggdrasil,    (s, v) => s.ManageYggdrasil = v,    null,                      null),
            new SysToggle("blood",     s => s.CastBloodSpells,    (s, v) => s.CastBloodSpells = v,    s => s.AdvisorBlood,       (s, v) => s.AdvisorBlood = v),
            new SysToggle("gold",      s => s.ManageGoldLoadouts, (s, v) => s.ManageGoldLoadouts = v, s => s.AdvisorGold,        (s, v) => s.AdvisorGold = v),
            new SysToggle("boosts",    s => s.ManageInventory,    (s, v) => s.ManageInventory = v,    s => s.AutoBoostPriority,  (s, v) => s.AutoBoostPriority = v),
            new SysToggle("inventory", s => s.ManageInventory,    (s, v) => s.ManageInventory = v,    null,                      null),
            new SysToggle("quests",    s => s.AutoQuest,          (s, v) => s.AutoQuest = v,          s => s.AdvisorQuests,      (s, v) => s.AdvisorQuests = v),
            new SysToggle("moneypit",  s => s.AutoMoneyPit,       (s, v) => s.AutoMoneyPit = v,       s => s.AdvisorPit,         (s, v) => s.AdvisorPit = v),
            new SysToggle("exp",       null,                      null,                               s => s.AdvisorExpBuys,     (s, v) => s.AdvisorExpBuys = v),
            // Resources is 3 independent gates (Energy/Magic/R3): report ON only when all three are on,
            // and toggle all three together so "resources automation off" actually stops all allocation.
            new SysToggle("resources", s => s.ManageEnergy && s.ManageMagic && s.ManageR3,
                                       (s, v) => { s.ManageEnergy = v; s.ManageMagic = v; s.ManageR3 = v; }, null, null),
        };

        private static SysToggle FindToggle(string id)
        {
            foreach (var t in Toggles) if (t.Id == id) return t;
            return null;
        }

        // ---- scalar setting bindings (W1): bool / int / double over SavedSettings ----
        // One registry drives BOTH the snapshot's `settings` node (read current value) and the
        // setSetting command (coerce + clamp + write). Every SavedSettings setter is change-guarded
        // and auto-saves, so we only assign — never SaveSettings ourselves.
        private sealed class Binding
        {
            public readonly string Key;
            public readonly Action<JSONObject, SavedSettings> Write;   // settings[Key] = current value
            public readonly Action<SavedSettings, JSONNode> Apply;     // coerce + clamp + set
            private Binding(string key, Action<JSONObject, SavedSettings> w, Action<SavedSettings, JSONNode> a)
            { Key = key; Write = w; Apply = a; }

            public static Binding Bool(string key, Func<SavedSettings, bool> g, Action<SavedSettings, bool> s)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null && v.IsBoolean) s(st, v.AsBool); });
            public static Binding Int(string key, Func<SavedSettings, int> g, Action<SavedSettings, int> s, int min, int max)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null) s(st, (int)Math.Round(Math.Min(Math.Max(v.AsDouble, min), max))); });
            public static Binding Dbl(string key, Func<SavedSettings, double> g, Action<SavedSettings, double> s, double min, double max)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null) s(st, Math.Min(Math.Max(v.AsDouble, min), max)); });
            public static Binding Str(string key, Func<SavedSettings, string> g, Action<SavedSettings, string> s)
                => new Binding(key, (o, st) => o[key] = g(st) ?? "",
                                    (st, v) => { if (v != null && v.IsString) s(st, v.Value); });
        }

        // W2: every simple scalar control across all systems (bool / int / double / enum-as-int).
        // The two-layer per-system gates also live in Toggles (setAutomation/setLayer) — binding them
        // here too lets the flat Settings grid write them by key; both hit the same change-guarded setter.
        // List/grid editors (blacklists, kill grid, priority lists, loadout IDs, card grids) are W3;
        // the 12 internal-state fields are intentionally NOT bound.
        private static readonly Binding[] BindingList =
        {
            // Global / Settings
            Binding.Bool("GlobalEnabled",       s => s.GlobalEnabled,       (s, v) => s.GlobalEnabled = v),
            Binding.Bool("AutoProfile",         s => s.AutoProfile,         (s, v) => s.AutoProfile = v),
            Binding.Bool("Autosave",            s => s.Autosave,            (s, v) => s.Autosave = v),
            Binding.Bool("DisableOverlay",      s => s.DisableOverlay,      (s, v) => s.DisableOverlay = v),
            Binding.Bool("LaunchCompanion",     s => s.LaunchCompanion,     (s, v) => s.LaunchCompanion = v),
            Binding.Bool("AdvisorShowOptimal",  s => s.AdvisorShowOptimal,  (s, v) => s.AdvisorShowOptimal = v),
            Binding.Dbl ("DiggerCap",           s => s.DiggerCap,           (s, v) => s.DiggerCap = v, 0, 100),
            // Automation gates
            Binding.Bool("ManageEnergy",        s => s.ManageEnergy,        (s, v) => s.ManageEnergy = v),
            Binding.Bool("ManageMagic",         s => s.ManageMagic,         (s, v) => s.ManageMagic = v),
            Binding.Bool("ManageR3",            s => s.ManageR3,            (s, v) => s.ManageR3 = v),
            Binding.Bool("ManageWandoos",       s => s.ManageWandoos,       (s, v) => s.ManageWandoos = v),
            Binding.Bool("ManageNGUDiff",       s => s.ManageNGUDiff,       (s, v) => s.ManageNGUDiff = v),
            Binding.Bool("ManageDiggers",       s => s.ManageDiggers,       (s, v) => s.ManageDiggers = v),
            Binding.Bool("ManageBeards",        s => s.ManageBeards,        (s, v) => s.ManageBeards = v),
            Binding.Bool("ManageGear",          s => s.ManageGear,          (s, v) => s.ManageGear = v),
            Binding.Bool("ManageYggdrasil",     s => s.ManageYggdrasil,     (s, v) => s.ManageYggdrasil = v),
            Binding.Bool("ManageTitans",        s => s.ManageTitans,        (s, v) => s.ManageTitans = v),
            Binding.Bool("ManageInventory",     s => s.ManageInventory,     (s, v) => s.ManageInventory = v),
            Binding.Bool("ManageConsumables",   s => s.ManageConsumables,   (s, v) => s.ManageConsumables = v),
            Binding.Bool("ManageWishes",        s => s.ManageWishes,        (s, v) => s.ManageWishes = v),
            Binding.Bool("WishSinkMode",        s => s.WishSinkMode,        (s, v) => s.WishSinkMode = v),
            Binding.Bool("ManageMayo",          s => s.ManageMayo,          (s, v) => s.ManageMayo = v),
            Binding.Bool("ManageCooking",       s => s.ManageCooking,       (s, v) => s.ManageCooking = v),
            Binding.Bool("ManageCookingLoadouts", s => s.ManageCookingLoadouts, (s, v) => s.ManageCookingLoadouts = v),
            Binding.Bool("ManageGoldLoadouts",  s => s.ManageGoldLoadouts,  (s, v) => s.ManageGoldLoadouts = v),
            Binding.Bool("ManageQuestLoadouts", s => s.ManageQuestLoadouts, (s, v) => s.ManageQuestLoadouts = v),
            Binding.Bool("CastBloodSpells",     s => s.CastBloodSpells,     (s, v) => s.CastBloodSpells = v),
            Binding.Bool("CombatEnabled",       s => s.CombatEnabled,       (s, v) => s.CombatEnabled = v),
            Binding.Bool("AutoQuest",           s => s.AutoQuest,           (s, v) => s.AutoQuest = v),
            // AUTO
            Binding.Bool("AutoFight",           s => s.AutoFight,           (s, v) => s.AutoFight = v),
            Binding.Bool("AutoRebirth",         s => s.AutoRebirth,         (s, v) => s.AutoRebirth = v),
            Binding.Bool("AutoConvertBoosts",   s => s.AutoConvertBoosts,   (s, v) => s.AutoConvertBoosts = v),
            Binding.Bool("AutoTitanGold",       s => s.AutoTitanGold,       (s, v) => s.AutoTitanGold = v),
            Binding.Bool("UpgradeDiggers",      s => s.UpgradeDiggers,      (s, v) => s.UpgradeDiggers = v),
            Binding.Bool("AutoBuyEM",           s => s.AutoBuyEM,           (s, v) => s.AutoBuyEM = v),
            Binding.Bool("AutoBuyAdventure",    s => s.AutoBuyAdventure,    (s, v) => s.AutoBuyAdventure = v),
            Binding.Bool("AutoBuyConsumables",  s => s.AutoBuyConsumables,  (s, v) => s.AutoBuyConsumables = v),
            Binding.Bool("ConsumeIfAlreadyRunning", s => s.ConsumeIfAlreadyRunning, (s, v) => s.ConsumeIfAlreadyRunning = v),
            Binding.Bool("AutoSpin",            s => s.AutoSpin,            (s, v) => s.AutoSpin = v),
            // Swap gear/diggers/beards
            Binding.Bool("SwapTitanLoadouts",   s => s.SwapTitanLoadouts,   (s, v) => s.SwapTitanLoadouts = v),
            Binding.Bool("SwapTitanDiggers",    s => s.SwapTitanDiggers,    (s, v) => s.SwapTitanDiggers = v),
            Binding.Bool("SwapTitanBeards",     s => s.SwapTitanBeards,     (s, v) => s.SwapTitanBeards = v),
            Binding.Bool("SwapYggdrasilLoadouts", s => s.SwapYggdrasilLoadouts, (s, v) => s.SwapYggdrasilLoadouts = v),
            Binding.Bool("SwapYggdrasilDiggers", s => s.SwapYggdrasilDiggers, (s, v) => s.SwapYggdrasilDiggers = v),
            Binding.Bool("SwapYggdrasilBeards", s => s.SwapYggdrasilBeards, (s, v) => s.SwapYggdrasilBeards = v),
            Binding.Bool("SwapPitDiggers",      s => s.SwapPitDiggers,      (s, v) => s.SwapPitDiggers = v),
            // Adventure / ITOPOD
            Binding.Bool("BeastMode",           s => s.BeastMode,           (s, v) => s.BeastMode = v),
            Binding.Bool("SnipeBossOnly",       s => s.SnipeBossOnly,       (s, v) => s.SnipeBossOnly = v),
            Binding.Bool("AllowZoneFallback",   s => s.AllowZoneFallback,   (s, v) => s.AllowZoneFallback = v),
            Binding.Int ("CombatMode",          s => s.CombatMode,          (s, v) => s.CombatMode = v, 0, 4),
            Binding.Bool("AdvisorFarmGear",     s => s.AdvisorFarmGear,     (s, v) => s.AdvisorFarmGear = v),
            Binding.Bool("AdvisorFarmBoost",    s => s.AdvisorFarmBoost,    (s, v) => s.AdvisorFarmBoost = v),
            Binding.Bool("AdvisorFarmRares",    s => s.AdvisorFarmRares,    (s, v) => s.AdvisorFarmRares = v),
            Binding.Bool("GearHuntEnabled",     s => s.GearHuntEnabled,     (s, v) => s.GearHuntEnabled = v),
            Binding.Bool("AdventureTargetITOPOD", s => s.AdventureTargetITOPOD, (s, v) => s.AdventureTargetITOPOD = v),
            Binding.Bool("ITOPODAutoPush",      s => s.ITOPODAutoPush,      (s, v) => s.ITOPODAutoPush = v),
            Binding.Bool("ITOPODBeastMode",     s => s.ITOPODBeastMode,     (s, v) => s.ITOPODBeastMode = v),
            Binding.Int ("ITOPODOptimizeMode",  s => s.ITOPODOptimizeMode,  (s, v) => s.ITOPODOptimizeMode = v, 0, 3),
            Binding.Int ("ITOPODCombatMode",    s => s.ITOPODCombatMode,    (s, v) => s.ITOPODCombatMode = v, 0, 1),  // binary: Idle/Snipe only (ITOPODManager uses Convert.ToBoolean; SavedSettings validates 0..1) — the UI dropdown must offer 2, not 4
            // Zone pickers: the companion sends only valid ids from the `zones` dropdown; -1 = auto/none,
            // 1000 = ITOPOD (SnipeZone only). Range is permissive; the dropdown constrains the actual value.
            Binding.Int ("SnipeZone",           s => s.SnipeZone,           (s, v) => s.SnipeZone = v, -1, 1000),
            Binding.Int ("GearHuntZone",        s => s.GearHuntZone,        (s, v) => s.GearHuntZone = v, -1, 1000),
            // Titans
            Binding.Int ("TitanCombatMode",     s => s.TitanCombatMode,     (s, v) => s.TitanCombatMode = v, 0, 4),
            Binding.Bool("TitanBeastMode",      s => s.TitanBeastMode,      (s, v) => s.TitanBeastMode = v),
            // Loadouts — per-mode optimizer objective (string; "" = manual item list) + Keep-Respawn; the
            // item-id lists ride setSettingList, and Loot Hunter's pool quotas are ints.
            Binding.Str ("TitanObjective",      s => s.TitanObjective,      (s, v) => s.TitanObjective = v),
            Binding.Bool("TitanObjectiveRespawn", s => s.TitanObjectiveRespawn, (s, v) => s.TitanObjectiveRespawn = v),
            Binding.Str ("GoldObjective",       s => s.GoldObjective,       (s, v) => s.GoldObjective = v),
            Binding.Bool("GoldObjectiveRespawn", s => s.GoldObjectiveRespawn, (s, v) => s.GoldObjectiveRespawn = v),
            Binding.Str ("QuestObjective",      s => s.QuestObjective,      (s, v) => s.QuestObjective = v),
            Binding.Bool("QuestObjectiveRespawn", s => s.QuestObjectiveRespawn, (s, v) => s.QuestObjectiveRespawn = v),
            Binding.Str ("YggdrasilObjective",  s => s.YggdrasilObjective,  (s, v) => s.YggdrasilObjective = v),
            Binding.Bool("YggdrasilObjectiveRespawn", s => s.YggdrasilObjectiveRespawn, (s, v) => s.YggdrasilObjectiveRespawn = v),
            Binding.Str ("CookingObjective",    s => s.CookingObjective,    (s, v) => s.CookingObjective = v),
            Binding.Bool("CookingObjectiveRespawn", s => s.CookingObjectiveRespawn, (s, v) => s.CookingObjectiveRespawn = v),
            // Main/idle gear: the standing objective + its respawn pin, and the drop trigger.
            // AdvisorGearRefresh also lives in Toggles (the Gear view's ADVISOR/MANUAL segment) — binding
            // it here too lets the Loadouts page arm it in place, and both write the same setter.
            Binding.Str ("GearObjective",       s => s.GearObjective,       (s, v) => s.GearObjective = v),
            Binding.Bool("GearObjectiveRespawn", s => s.GearObjectiveRespawn, (s, v) => s.GearObjectiveRespawn = v),
            Binding.Bool("AdvisorGearRefresh",  s => s.AdvisorGearRefresh,  (s, v) => s.AdvisorGearRefresh = v),
            Binding.Bool("AdvisorGearOnDrop",   s => s.AdvisorGearOnDrop,   (s, v) => s.AdvisorGearOnDrop = v),
            Binding.Int ("LootHunterRespawnCount", s => s.LootHunterRespawnCount, (s, v) => s.LootHunterRespawnCount = v, 0, 20),
            Binding.Int ("LootHunterDropCount", s => s.LootHunterDropCount,  (s, v) => s.LootHunterDropCount = v, 0, 20),
            // Challenges
            Binding.Bool("AdvisorChallenges",   s => s.AdvisorChallenges,   (s, v) => s.AdvisorChallenges = v),
            // Quests
            Binding.Bool("AllowMajorQuests",    s => s.AllowMajorQuests,    (s, v) => s.AllowMajorQuests = v),
            Binding.Bool("QuestsFullBank",      s => s.QuestsFullBank,      (s, v) => s.QuestsFullBank = v),
            Binding.Bool("ManualMinors",        s => s.ManualMinors,        (s, v) => s.ManualMinors = v),
            Binding.Bool("AbandonMinors",       s => s.AbandonMinors,       (s, v) => s.AbandonMinors = v),
            Binding.Int ("MinorAbandonThreshold", s => s.MinorAbandonThreshold, (s, v) => s.MinorAbandonThreshold = v, 0, 100),
            Binding.Bool("FiftyItemMinors",     s => s.FiftyItemMinors,     (s, v) => s.FiftyItemMinors = v),
            Binding.Bool("UseButterMinor",      s => s.UseButterMinor,      (s, v) => s.UseButterMinor = v),
            Binding.Bool("UseButterMajor",      s => s.UseButterMajor,      (s, v) => s.UseButterMajor = v),
            Binding.Int ("QuestCombatMode",     s => s.QuestCombatMode,     (s, v) => s.QuestCombatMode = v, 0, 4),
            Binding.Bool("QuestBeastMode",      s => s.QuestBeastMode,      (s, v) => s.QuestBeastMode = v),
            Binding.Bool("PoolMajorQuests",     s => s.PoolMajorQuests,     (s, v) => s.PoolMajorQuests = v),
            Binding.Bool("QuestHoldForGear",    s => s.QuestHoldForGear,    (s, v) => s.QuestHoldForGear = v),
            // Gold
            Binding.Bool("SnipeOnNewZone",      s => s.SnipeOnNewZone,      (s, v) => s.SnipeOnNewZone = v),
            Binding.Bool("SnipeOnRebirth",      s => s.SnipeOnRebirth,      (s, v) => s.SnipeOnRebirth = v),
            Binding.Bool("SnipeOnGoldStarved",  s => s.SnipeOnGoldStarved,  (s, v) => s.SnipeOnGoldStarved = v),
            Binding.Bool("SnipeOnTimer",        s => s.SnipeOnTimer,        (s, v) => s.SnipeOnTimer = v),
            Binding.Int ("ResnipeTime",         s => s.ResnipeTime,         (s, v) => s.ResnipeTime = v, 0, 86400),
            Binding.Bool("GoldCBlockMode",      s => s.GoldCBlockMode,      (s, v) => s.GoldCBlockMode = v),
            // Money pit
            Binding.Bool("AutoMoneyPit",        s => s.AutoMoneyPit,        (s, v) => s.AutoMoneyPit = v),
            Binding.Dbl ("MoneyPitThreshold",   s => s.MoneyPitThreshold,   (s, v) => s.MoneyPitThreshold = v, 0, 1e300),
            Binding.Int ("DaycareThreshold",    s => s.DaycareThreshold,    (s, v) => s.DaycareThreshold = v, 0, 100),
            Binding.Bool("MoneyPitRunMode",     s => s.MoneyPitRunMode,     (s, v) => s.MoneyPitRunMode = v),
            Binding.Bool("PredictMoneyPit",     s => s.PredictMoneyPit,     (s, v) => s.PredictMoneyPit = v),
            Binding.Bool("MoneyPitDaycare",     s => s.MoneyPitDaycare,     (s, v) => s.MoneyPitDaycare = v),
            // Boosts
            Binding.Int ("CubePriority",        s => s.CubePriority,        (s, v) => s.CubePriority = v, 0, 4),
            // NOT a numeric range. Macguffin ids are 198-300 (InventoryManager.macguffinList), so the
            // old -1..64 clamp rewrote EVERY pick to 64: the page then found no <option value="64">,
            // blanked the select once the 4s optimistic hold expired, and the run logged "Failed to
            // find a macguffin with id 64" — which read as "the selection disappears". Validate against
            // the same table SavedSettings.MassUpdate validates against, so the two cannot disagree;
            // an id outside it is dropped rather than clamped into a different, wrong id.
            // The range is deliberately WIDER than the real ids (which top out at 300). Binding.Int
            // clamps before this lambda runs, so a max of exactly 300 would fold a bogus 500 INTO the
            // valid id 300 and accept it; a loose ceiling lets the table lookup below reject it instead.
            Binding.Int ("FavoredMacguffin",    s => s.FavoredMacguffin,
                         (s, v) => { if (v == -1 || InventoryManager.macguffinList.ContainsKey(v)) s.FavoredMacguffin = v; }, -1, 100000),
            // Wishes
            Binding.Int ("WishLimit",           s => s.WishLimit,           (s, v) => s.WishLimit = v, 1, 4),
            Binding.Int ("WishMode",            s => s.WishMode,            (s, v) => s.WishMode = v, 0, 4),
            Binding.Dbl ("WishEnergy",          s => s.WishEnergy,          (s, v) => s.WishEnergy = v, 0, 100),
            Binding.Dbl ("WishMagic",           s => s.WishMagic,           (s, v) => s.WishMagic = v, 0, 100),
            Binding.Dbl ("WishR3",              s => s.WishR3,              (s, v) => s.WishR3 = v, 0, 100),
            Binding.Bool("WeakPriorities",      s => s.WeakPriorities,      (s, v) => s.WeakPriorities = v),
            // Cards
            Binding.Bool("AutoCastCards",       s => s.AutoCastCards,       (s, v) => s.AutoCastCards = v),
            Binding.Bool("TrashCards",          s => s.TrashCards,          (s, v) => s.TrashCards = v),
            Binding.Bool("CardSortEnabled",     s => s.CardSortEnabled,     (s, v) => s.CardSortEnabled = v),
            Binding.Bool("CastProtectedCards",  s => s.CastProtectedCards,  (s, v) => s.CastProtectedCards = v),
            Binding.Bool("TrashProtectedCards", s => s.TrashProtectedCards, (s, v) => s.TrashProtectedCards = v),
            // Yggdrasil
            Binding.Bool("ActivateFruits",      s => s.ActivateFruits,      (s, v) => s.ActivateFruits = v),
            Binding.Int ("YggSwapThreshold",    s => s.YggSwapThreshold,    (s, v) => s.YggSwapThreshold = v, 1, 24),
            // Perks & Quirks
            Binding.Bool("AdvisorPerks",        s => s.AdvisorPerks,        (s, v) => s.AdvisorPerks = v),
            Binding.Bool("AdvisorQuirks",       s => s.AdvisorQuirks,       (s, v) => s.AdvisorQuirks = v),

            // --- Blood (W1.1) ---
            Binding.Bool("AutoSpellSwap",            s => s.AutoSpellSwap,            (s, v) => s.AutoSpellSwap = v),
            Binding.Bool("IronPillOnRebirth",        s => s.IronPillOnRebirth,        (s, v) => s.IronPillOnRebirth = v),
            Binding.Bool("BloodMacGuffinAOnRebirth", s => s.BloodMacGuffinAOnRebirth, (s, v) => s.BloodMacGuffinAOnRebirth = v),
            Binding.Bool("BloodMacGuffinBOnRebirth", s => s.BloodMacGuffinBOnRebirth, (s, v) => s.BloodMacGuffinBOnRebirth = v),
            Binding.Int ("SpaghettiThreshold",       s => s.SpaghettiThreshold,       (s, v) => s.SpaghettiThreshold = v,       0, 100),
            Binding.Int ("CounterfeitThreshold",     s => s.CounterfeitThreshold,     (s, v) => s.CounterfeitThreshold = v,     0, 100000),
            Binding.Int ("BloodMacGuffinAThreshold", s => s.BloodMacGuffinAThreshold, (s, v) => s.BloodMacGuffinAThreshold = v, 0, 100000),
            Binding.Int ("BloodMacGuffinBThreshold", s => s.BloodMacGuffinBThreshold, (s, v) => s.BloodMacGuffinBThreshold = v, 0, 100000),
            Binding.Dbl ("BloodNumberThreshold",     s => s.BloodNumberThreshold,     (s, v) => s.BloodNumberThreshold = v,     0, 1e18),
        };

        private static readonly Dictionary<string, Binding> Bindings = BuildBindings();
        private static Dictionary<string, Binding> BuildBindings()
        {
            var d = new Dictionary<string, Binding>();
            foreach (var b in BindingList) d[b.Key] = b;
            return d;
        }

        // One-shot outbound notice (e.g. the result of a button action): set on the main thread by a
        // command, emitted in the NEXT snapshot as root["notice"], then cleared. Companion shows a toast.
        private static string _notice;
        // Text the notice offers to COPY when it is clicked -- a file path, so far. Rides with the
        // notice it belongs to and is cleared with it, so it can never attach to a later one.
        private static string _noticeCopy;

        // ---- doAction registry: fire-and-forget UI actions (buttons), not settings. Each maps to a
        //      safe, existing main-thread entry point — mirrors what the WinForms buttons already do.
        //      Drained via DrainCommands on the Unity main thread with a per-command try/catch.
        private static readonly Dictionary<string, Action> Actions = BuildActions();
        private static Dictionary<string, Action> BuildActions()
        {
            var d = new Dictionary<string, Action>(StringComparer.Ordinal);
            // "Refresh" — identical to the Reload button. Re-reads SETTINGS from disk (not the DLL).
            d["reloadAdvisor"] = () => Main.RequestSettingsReload();
            // "Hot Reload Advisor" — swaps the payload DLL itself (the old F5 / "Reload Advisor" button).
            // Deliberately separate from reloadAdvisor above. Only requests: Main.Update performs the
            // teardown at the top of the next frame, because doing it here would dispose this very
            // UiBridge while DrainCommands is still iterating its queue.
            d["hotReloadAdvisor"] = () => Main.RequestHotReload();
            // "Snipe Now" — arm a gold snipe by clearing the completion latch (mirrors GoldSnipeNow_Click).
            d["snipeNow"] = () => { if (Main.Settings != null) Main.Settings.GoldSnipeComplete = false; };
            // "Re-optimize gear now" — force an immediate optimize+equip of the active objective's best set
            // (main thread) and stash the human-readable outcome for the next snapshot's `notice` toast.
            d["refreshGear"] = () => { _notice = AdvisorApply.ForceGearReoptimize(); };
            // "Recompute keep/trash" (Gear > Inventory) — the full objective sweep (30+ optimizations, one
            // frame's hitch), same work the 10-minute ApplyBoostPriority pass does. Read-only: it changes
            // no gear and trashes nothing; the verdict rides the next snapshot's `invVerdict` node.
            d["computeInventory"] = () => { _notice = InventoryAdvisor.ComputeForUi(); };
            // "Export game state" (Log drawer) — one readable text file of what the advisor sees right
            // now, names included. Read-only. Commands drain on the main thread, so it runs here.
            d["exportState"] = () =>
            {
                string path = StateExport.Write();
                _notice = path != null ? "Game state exported to " + path
                                       : "The state export failed — check the Debug log.";
                _noticeCopy = path;
            };
            // "Locate" on the Walderp chip — jumps the GAME to the menu he is hiding in. Strictly
            // player-initiated: never fired on a timer, and it does NOT click him. He relocates every 180s,
            // so acting on our own would risk yanking the screen toward a target that has already moved.
            d["locateWalderp"] = () => { _notice = LocateWalderp(); };
            return d;
        }

        // Jump the game to the menu Walderp is currently hiding in. Runs on the Unity main thread (every
        // Action does), so touching the menu swapper is safe. Every failure path returns a sentence for the
        // toast rather than doing nothing -- a button that silently no-ops is the worst outcome here, because
        // the player cannot tell "he moved" from "the advisor is broken".
        private static string LocateWalderp()
        {
            try
            {
                var c = Main.Character;
                if (c == null) return "Can't reach the game right now.";
                var adv = c.adventure;
                if (adv == null) return "Can't read your adventure state right now.";

                // Same condition the game uses to freeze boss5Spawn (AdventureController.cs:724).
                if (adv.waldoDefeats <= adv.waldoFinds)
                    return "Walderp isn't hiding right now — nothing to locate.";

                var u = c.waldoUnlocker;
                if (u == null) return "Walderp is hiding, but the game isn't exposing where.";

                int menu = u.currentMenu;
                // -1 is ROUTINE, not an error: the game clears it during his 180s fade, and again for the
                // first 180s after any launch. Say so, rather than implying something is wrong.
                if (menu < 0)
                    return "Walderp is hiding, but he hasn't settled on a menu yet. Try again shortly.";

                var swapper = u.menuSwapper;
                if (swapper == null) return "Walderp is in a menu, but the game won't let us switch to it.";

                bool troll = false;
                try { troll = c.challenges.trollMenuSwap; } catch { }

                swapper.swapMenu(menu);

                // Under TROLL CHALLENGE, swapMenu re-randomises the destination 75% of the time, so the jump
                // usually lands somewhere else. Tell the player that instead of letting it look like a bug.
                if (troll)
                    return "Tried to jump to Walderp — the troll challenge scrambles menu swaps, so you may have landed elsewhere.";
                return "Jumped to where Walderp is hiding. Look for a faint image and click it.";
            }
            catch (Exception e)
            {
                Main.LogDebug("LocateWalderp: " + e.Message);
                return "Couldn't jump to Walderp's menu.";
            }
        }

        // ---------------------------------------------------------------- lifecycle

        public void Start()
        {
            // Two UNIDIRECTIONAL pipes, each synchronous (PipeOptions.None — the M1-proven pattern on Mono).
            // Splitting the directions avoids concurrent read+write on a single handle, which a synchronous
            // pipe serializes (deadlock) and which the async-pipe workaround can't fix reliably under Mono
            // (sync WaitForConnection on an Asynchronous pipe fails with ERROR_PIPE_LISTENING there).
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "NGUAdvisorUiBridgeTx" };
            _pumpThread.Start();
            _cmdThread = new Thread(CmdLoop) { IsBackground = true, Name = "NGUAdvisorUiBridgeRx" };
            _cmdThread.Start();
            try { Main.Log("UiBridge started (snapshots '" + PipeName + "', commands '" + CmdPipeName + "', v" + ProtocolVersion + ")."); }
            catch { /* logging must never break startup */ }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _signal.Set(); } catch { }
            // Disconnect (DisconnectNamedPipe on Windows-Mono fails any pending I/O) then dispose both
            // servers; poke each to release a thread parked in WaitForConnection; join both threads.
            DisconnectAndDispose(_server);
            DisconnectAndDispose(_cmdServer);
            Poke(PipeName);
            Poke(CmdPipeName);
            try { if (_pumpThread != null) _pumpThread.Join(1000); } catch { }
            try { if (_cmdThread != null) _cmdThread.Join(1000); } catch { }
            try { _signal.Close(); } catch { }
        }

        private static void DisconnectAndDispose(NamedPipeServerStream s)
        {
            if (s == null) return;
            try { if (s.IsConnected) s.Disconnect(); } catch { }
            try { s.Dispose(); } catch { }
        }

        private static void Poke(string name)
        {
            try { using (var p = new NamedPipeClientStream(".", name, PipeDirection.InOut)) p.Connect(200); }
            catch { /* nobody parked / already gone — fine */ }
        }

        // ------------------------------------------------------------- main-thread publish

        /// <summary>Build a snapshot from live advisor state and hand it to the pipe thread. MAIN THREAD ONLY.</summary>
        public void Publish(float nextLoopSeconds)
        {
            // Drain any error the background pipe thread stashed. Logging must happen HERE (main thread):
            // Main.Log* reads the game Character object, which hard-crashes if touched off-thread.
            var pumpErr = _pumpError;
            if (pumpErr != null) { _pumpError = null; try { Main.LogDebug("UiBridge pump: " + pumpErr); } catch { } }

            string json;
            try { json = BuildSnapshotJson(nextLoopSeconds); }
            catch (Exception e) { try { Main.LogDebug("UiBridge snapshot build failed: " + e); } catch { } return; }
            _latest = json;
            try { _signal.Set(); } catch { }
        }

        /// <summary>
        /// Ask the companion page to show a view (the F9 hotkey opens the Profile Editor). Main thread only —
        /// it is called from Main.Update and read while the snapshot is built on the same thread.
        /// The request rides the normal snapshot; it expires after <see cref="NavTtlSnaps"/> snapshots so a
        /// companion launched by the same keypress still catches it, but a later one is not yanked around.
        /// </summary>
        public void RequestView(string view)
        {
            if (string.IsNullOrEmpty(view)) return;
            _navView = view;
            _navSeq++;
            _navUntilSeq = _seq + NavTtlSnaps;
        }

        // ------------------------------------------------------------- background pipe pump

        // M7 (audit): create a hardened pipe server. Best-effort restrict the DACL to the current user,
        // shrinking the surface from "any user / remote / lower-integrity" to this user's processes.
        // Mono's System.IO.Pipes may not implement the PipeSecurity overload, and breaking the bridge
        // over hardening would be worse than the risk — so on any failure we fall back to the default
        // constructor and note it once (from the main thread; the pipe thread must not touch the log).
        private NamedPipeServerStream CreateServer(string name)
        {
            if (!_pipeSecUnavailable)
            {
                try
                {
                    var sec = new PipeSecurity();
                    var self = WindowsIdentity.GetCurrent().User;
                    if (self != null)
                        sec.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
                    return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                }
                catch { _pipeSecUnavailable = true; }
            }
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        }

        // Bounded replacement for StreamReader.ReadLine: never buffer an unbounded line. An over-long
        // line is drained to the next newline and dropped (returned as "" — DrainCommands ignores empty
        // lines). Silent by design: this runs on the pipe thread, which must not touch the log.
        private static string ReadBoundedLine(TextReader reader)
        {
            var sb = new StringBuilder();
            int ci;
            while ((ci = reader.Read()) >= 0)
            {
                char ch = (char)ci;
                if (ch == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length >= MaxCommandLineChars)
                {
                    while ((ci = reader.Read()) >= 0 && (char)ci != '\n') { }   // discard rest of the over-long line
                    return string.Empty;
                }
                sb.Append(ch);
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd('\r');           // EOF
        }

        // Snapshot pipe: WRITE-only. This handle never reads, so nothing serializes its WriteLine.
        private void PumpLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServer(PipeName);
                    _server = server;
                    if (_stopping) return;                      // stop raced in before we park (finally disposes)
                    server.WaitForConnection();                 // blocks until the companion connects (or the poke)
                    if (_stopping) return;

                    var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                    string lastSent = null;
                    while (!_stopping && server.IsConnected)
                    {
                        string snap = _latest;
                        if (snap != null && !ReferenceEquals(snap, lastSent))
                        {
                            writer.WriteLine(snap);             // throws IOException when the client drops
                            lastSent = snap;
                        }
                        _signal.WaitOne(500);                   // wake on new snapshot; 500ms heartbeat otherwise
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException) { /* client dropped or shutting down — re-accept below */ }
                catch (Exception e) { _pumpError = e.Message; }   // NEVER log here — this is the pipe thread
                finally
                {
                    _server = null;
                    DisconnectAndDispose(server);
                }
                if (!_stopping) Thread.Sleep(200);              // brief backoff before re-accepting
            }
        }

        // ------------------------------------------------------------- inbound commands (M2)

        // Command pipe: READ-only. A separate handle from the snapshot pipe, so its parked ReadLine can
        // never serialize the snapshot writer. The companion connects per-command, sends one line, closes;
        // we read to EOF then re-accept. This method ONLY enqueues raw lines — no game/Unity access here.
        private void CmdLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream cmd = null;
                try
                {
                    cmd = CreateServer(CmdPipeName);
                    _cmdServer = cmd;
                    if (_stopping) return;
                    cmd.WaitForConnection();
                    if (_stopping) return;

                    var reader = new StreamReader(cmd, new UTF8Encoding(false));
                    string line;
                    while (!_stopping && cmd.IsConnected && (line = ReadBoundedLine(reader)) != null)
                    {
                        if (line.Length > 0 && _inbound.Count < 256) _inbound.Enqueue(line);  // bound vs flood
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException) { /* client closed after sending — re-accept below */ }
                catch (Exception e) { _pumpError = e.Message; }   // NEVER log here — this is the pipe thread
                finally
                {
                    _cmdServer = null;
                    DisconnectAndDispose(cmd);
                }
                if (!_stopping) Thread.Sleep(100);              // short backoff so the next command connects fast
            }
        }

        /// <summary>Apply queued UI commands. MAIN THREAD ONLY (called from Main.Update); per-command guarded.</summary>
        public void DrainCommands()
        {
            if (_pipeSecUnavailable && !_secNoteLogged)
            {
                _secNoteLogged = true;
                try { Main.LogDebug("UiBridge: pipe ACL unavailable on this runtime; using default pipe security (M7)."); } catch { }
            }
            string line;
            int budget = 64;                                    // bound work per frame
            while (budget-- > 0 && _inbound.TryDequeue(out line))
            {
                try { Dispatch(line); }
                catch (Exception e) { try { Main.LogDebug("UiBridge command failed: " + e.Message); } catch { } }
            }
        }

        private void Dispatch(string line)
        {
            JSONObject obj;
            try { obj = JSON.Parse(line) as JSONObject; }
            catch { try { Main.LogDebug("UiBridge: unparseable command"); } catch { } return; }
            if (obj == null) return;

            string cmd = obj["cmd"].Value;
            if (string.IsNullOrEmpty(cmd)) return;

            var settings = Main.Settings;
            switch (cmd)
            {
                case "setAutomation":
                {
                    // Require a real boolean so a missing/garbled 'on' can't silently pause a system.
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean) { Main.LogDebug("UiBridge: setAutomation missing/invalid 'on'"); break; }
                    bool on = onNode.AsBool;
                    string sys = obj["system"].Value;
                    if (string.IsNullOrEmpty(sys))
                    {
                        // Master. The GlobalEnabled setter persists + refreshes the form + logs and is
                        // change-guarded, so we only assign it — never SaveSettings ourselves.
                        if (settings != null) settings.GlobalEnabled = on;
                        Main.Log("UI command: automation " + (on ? "ON" : "OFF"));
                    }
                    else
                    {
                        var t = FindToggle(sys);
                        if (t != null && t.SetAuto != null && settings != null)
                        { t.SetAuto(settings, on); Main.Log("UI command: " + sys + " automation " + (on ? "ON" : "OFF")); }
                        else Main.LogDebug("UiBridge: setAutomation unsupported system '" + sys + "'");
                    }
                    break;
                }

                case "setLayer":
                {
                    var t = FindToggle(obj["system"].Value);
                    if (t == null || t.SetAdvisor == null || settings == null) { Main.LogDebug("UiBridge: setLayer unsupported system '" + obj["system"].Value + "'"); break; }
                    string layer = obj["layer"].Value;
                    if (layer != "advisor" && layer != "manual") { Main.LogDebug("UiBridge: setLayer invalid layer '" + layer + "'"); break; }
                    bool advisor = layer == "advisor";       // ADVISOR vs MANUAL
                    t.SetAdvisor(settings, advisor);
                    Main.Log("UI command: " + t.Id + " decisions -> " + (advisor ? "ADVISOR" : "MANUAL"));
                    break;
                }

                case "setSetting":
                {
                    string key = obj["key"].Value;
                    var val = obj["value"];
                    if (string.IsNullOrEmpty(key) || settings == null) { Main.LogDebug("UiBridge: setSetting missing key"); break; }
                    if (val == null || !(val.IsBoolean || val.IsNumber || val.IsString)) { Main.LogDebug("UiBridge: setSetting invalid value for '" + key + "'"); break; }
                    Binding b;
                    if (!Bindings.TryGetValue(key, out b)) { Main.LogDebug("UiBridge: setSetting unknown key '" + key + "'"); break; }
                    b.Apply(settings, val);
                    Main.Log("UI command: set " + key);
                    break;
                }

                case "switchProfile":
                {
                    string name = obj["name"].Value;
                    if (string.IsNullOrEmpty(name) || settings == null) { Main.LogDebug("UiBridge: switchProfile missing name"); break; }
                    // Only switch to an EXISTING profile; reject path separators (the UI sends names from
                    // profiles.list). Prevents a typo creating an empty profile / any path traversal.
                    if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains(".."))
                    { Main.LogDebug("UiBridge: switchProfile rejected name '" + name + "'"); break; }
                    try
                    {
                        var dir = Main.GetProfilesDir();
                        if (dir == null || !File.Exists(Path.Combine(dir, name + ".json")))
                        { Main.LogDebug("UiBridge: switchProfile no such profile '" + name + "'"); break; }
                    }
                    catch { break; }
                    settings.AllocationFile = name;                        // change-guarded setter persists
                    Main.RequestAllocationReload();                        // main-thread allocation reload next frame
                    Main.Log("UI command: switch profile -> " + name);
                    break;
                }

                case "setAutoProfile":
                {
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean || settings == null) { Main.LogDebug("UiBridge: setAutoProfile invalid"); break; }
                    settings.AutoProfile = onNode.AsBool;
                    Main.Log("UI command: auto-profile " + (onNode.AsBool ? "ON" : "OFF"));
                    break;
                }

                case "setLaunchCompanion":
                {
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean || settings == null) { Main.LogDebug("UiBridge: setLaunchCompanion invalid"); break; }
                    settings.LaunchCompanion = onNode.AsBool;
                    Main.Log("UI command: auto-launch companion " + (onNode.AsBool ? "ON" : "OFF"));
                    break;
                }

                case "reloadAdvisor":
                    Main.RequestSettingsReload();               // drained next frame: settings + form + allocation
                    Main.Log("UI command: reload requested");
                    break;

                // Payload hot reload — NOT the same thing as reloadAdvisor above (that one re-reads
                // settings from disk). This tears the running advisor down and byte-loads the DLL again,
                // exactly like F5. Accepted both as a top-level cmd and as a doAction (see the Actions
                // table) so either shape the companion sends works.
                case "hotReloadAdvisor":
                    Main.RequestHotReload();                    // performed at the top of the next Update, not here
                    Main.Log("UI command: hot reload requested (payload DLL)");
                    break;

                case "doAction":
                {
                    string action = obj["action"].Value;
                    if (string.IsNullOrEmpty(action)) { Main.LogDebug("UiBridge: doAction missing action"); break; }
                    Action fn;
                    if (!Actions.TryGetValue(action, out fn)) { Main.LogDebug("UiBridge: doAction unknown action '" + action + "'"); break; }
                    fn();
                    Main.Log("UI command: action " + action);
                    break;
                }

                case "setSettingFlag":
                {
                    // Set one element of a bool[] setting (e.g. the titan kill grid). CLONE before mutating:
                    // the setter's SequenceEqual guard would treat an in-place edit of the same array as no-change.
                    string key = obj["key"].Value;
                    var idxNode = obj["index"]; var valNode = obj["value"];
                    if (settings == null || idxNode == null || !idxNode.IsNumber || valNode == null || !valNode.IsBoolean)
                    { Main.LogDebug("UiBridge: setSettingFlag invalid"); break; }
                    int index = idxNode.AsInt; bool value = valNode.AsBool;
                    if (key == "TitanSwapTargets")
                    {
                        var arr = settings.TitanSwapTargets;
                        if (arr == null || index < 0 || index >= arr.Length) { Main.LogDebug("UiBridge: setSettingFlag index oob"); break; }
                        var copy = (bool[])arr.Clone(); copy[index] = value; settings.TitanSwapTargets = copy;
                        Main.Log("UI command: TitanSwapTargets[" + index + "] = " + value);
                    }
                    else Main.LogDebug("UiBridge: setSettingFlag unknown key '" + key + "'");
                    break;
                }

                case "setSettingList":
                {
                    // Replace an int[] setting (boost priority / blacklist). The setters here don't validate,
                    // so range-check every id (mass-update validates on load; we mirror the safe floor).
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setSettingList invalid"); break; }
                    var raw = new List<int>();
                    for (int i = 0; i < valsNode.Count && raw.Count < MaxListIds; i++)
                    {
                        var n = valsNode[i];
                        if (n != null && n.IsNumber) raw.Add(n.AsInt);
                    }
                    // Per-key validation: the enemy blacklist accepts only real adventure sprite ids (mirrors
                    // SavedSettings.IsAdvEnemy); every other list is gear-ids clamped to the gear range.
                    if (key == "BlacklistedBosses")
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (IsAdvEnemyId(id)) f.Add(id);
                        settings.BlacklistedBosses = f.ToArray();   // setter re-applies CombatManager.UpdateBlacklists()
                        Main.Log("UI command: BlacklistedBosses (" + f.Count + " ids)");
                    }
                    else if (key == "WishPriorities" || key == "WishBlacklist")
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (id >= 0 && id <= Consts.MAX_WISH_ID) f.Add(id);
                        var arr = f.ToArray();
                        if (key == "WishPriorities") settings.WishPriorities = arr; else settings.WishBlacklist = arr;
                        Main.Log("UI command: " + key + " (" + arr.Length + " ids)");
                    }
                    else
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (id >= 0 && id <= Consts.MAX_GEAR_ID) f.Add(id);
                        var arr = f.ToArray();
                        if (AssignGearList(settings, key, arr)) Main.Log("UI command: " + key + " (" + arr.Length + " ids)");
                        else Main.LogDebug("UiBridge: setSettingList unknown key '" + key + "'");
                    }
                    break;
                }

                case "setLoadoutFromGear":
                {
                    // Fill a loadout from the currently-equipped gear (the "Use current gear" button).
                    string key = obj["key"].Value;
                    if (settings == null || string.IsNullOrEmpty(key)) { Main.LogDebug("UiBridge: setLoadoutFromGear missing key"); break; }
                    int[] ids;
                    try { ids = LoadoutManager.CurrentGearIds() ?? new int[0]; } catch { ids = new int[0]; }
                    var f = new List<int>();
                    foreach (var id in ids) if (id >= 0 && id <= Consts.MAX_GEAR_ID) f.Add(id);
                    if (AssignGearList(settings, key, f.ToArray())) Main.Log("UI command: setLoadoutFromGear " + key + " (" + f.Count + " ids)");
                    else Main.LogDebug("UiBridge: setLoadoutFromGear unknown key '" + key + "'");
                    break;
                }

                case "applyObjective":
                {
                    // Fill a mode's item-id list with the optimizer's best set for an objective — the
                    // answer to "how do I find an item ID". It writes a SETTING ONLY: no ChangeGear, no
                    // assignCurrentEquipToLoadout. The result comes back through the existing `loadouts`
                    // snapshot node and repaints in the existing list editor, which already renders
                    // [188] "Edgy Helmet" with a remove button per row — so the list IS the preview,
                    // and it is reversible without touching the game.
                    string key = obj["key"].Value;
                    var objNode = obj["objective"];
                    var respNode = obj["respawn"];
                    if (settings == null || string.IsNullOrEmpty(key) || objNode == null || !objNode.IsString)
                    { Main.LogDebug("UiBridge: applyObjective invalid"); break; }
                    bool respawn = respNode != null && respNode.IsBoolean && respNode.AsBool;
                    string objective = objNode.Value;
                    if (string.IsNullOrEmpty(objective))
                    { Main.LogDebug("UiBridge: applyObjective with no objective"); break; }

                    // The Quest card's own entry is not a gear objective: it is solved for the quest's
                    // zone. The fill still shows what it would wear right now.
                    bool questRate = key == "QuestLoadout"
                        && string.Equals(objective, QuestGearSet.ObjectiveName, StringComparison.OrdinalIgnoreCase);
                    var gobj = questRate
                        ? new GearObjectives.Objective(QuestGearSet.ObjectiveName, new string[0])
                        : GearOptimizer.FindObjective(objective);
                    if (gobj == null)
                    { _notice = $"Couldn't find the '{objective}' objective."; break; }

                    int[] optimized;
                    try { optimized = questRate ? GearOptimizer.ResolveQuestGear() : GearOptimizer.OptimizeIds(gobj, respawn); }
                    catch (Exception oe)
                    {
                        Main.LogDebug($"UiBridge: applyObjective optimize failed: {oe.Message}");
                        _notice = "The optimizer hit an error — check the Debug log.";
                        break;
                    }
                    if (optimized == null || optimized.Length == 0)
                    { _notice = $"The optimizer returned no set for '{gobj.Name}'."; break; }

                    // Same range floor setSettingList applies; the setters themselves don't validate.
                    var okIds = new List<int>();
                    foreach (var id in optimized) if (id >= 0 && id <= Consts.MAX_GEAR_ID) okIds.Add(id);
                    if (AssignGearList(settings, key, okIds.ToArray()))
                    {
                        Main.Log($"UI command: applyObjective {key} <- '{gobj.Name}'{(respawn ? " (+top respawn)" : "")} ({okIds.Count} ids)");
                        _notice = $"Filled the {LoadoutLabel(key)} list with the best '{gobj.Name}' set ({okIds.Count} items).";
                    }
                    else Main.LogDebug("UiBridge: applyObjective unknown key '" + key + "'");
                    break;
                }

                case "setIntArray":
                {
                    // Whole int[] for fixed-index arrays (transform per-chain flags, card rarity/cost thresholds).
                    // The companion edits the array it received in the snapshot (correct length) and sends it back.
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setIntArray invalid"); break; }
                    var arr = new int[Math.Min(valsNode.Count, MaxListIds)];
                    for (int i = 0; i < arr.Length; i++) { var n = valsNode[i]; arr[i] = (n != null && n.IsNumber) ? n.AsInt : 0; }
                    if (AssignIntArray(settings, key, arr)) Main.Log("UI command: " + key + " (" + arr.Length + " values)");
                    else Main.LogDebug("UiBridge: setIntArray unknown key '" + key + "'");
                    break;
                }

                case "setSettingStrList":
                {
                    // Whole string[] for fixed-vocabulary lists (BoostPriority types, CardSortOrder criteria).
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setSettingStrList invalid"); break; }
                    var raw = new List<string>();
                    for (int i = 0; i < valsNode.Count && raw.Count < MaxListIds; i++) { var n = valsNode[i]; if (n != null && n.IsString) raw.Add(n.Value); }
                    var arr = FilterStrList(key, raw);
                    if (arr == null) { Main.LogDebug("UiBridge: setSettingStrList unknown key '" + key + "'"); break; }
                    if (key == "BoostPriority") settings.BoostPriority = arr;
                    else if (key == "CardSortOrder") settings.CardSortOrder = arr;
                    Main.Log("UI command: " + key + " (" + arr.Length + " items)");
                    break;
                }

                default:
                    // The page and the injector version independently: the companion is reloaded by
                    // restarting it, the advisor only by F5 / re-inject. So a NEWER page talking to an
                    // OLDER advisor is a normal state after an update — and it used to be completely
                    // silent, because the command simply fell in here. The user pressed a button, the
                    // control looked like it worked, and nothing happened (reported in testing: "Fill
                    // from objective" did nothing, with no message). Say so instead of only logging it.
                    Main.LogDebug("UiBridge: unknown command '" + cmd + "'");
                    _notice = "This control needs a newer advisor than the one running — press F5 in the "
                            + "game to reload it, then try again.";
                    break;
            }
        }

        private static JSONArray IntArr(int[] a)
        {
            var arr = new JSONArray();
            if (a != null) foreach (var id in a) arr.Add(id);
            return arr;
        }

        // Append a gear-id list into the accumulator behind the `itemNames` map. Null-tolerant; the caller
        // sorts + dedupes. Ids are NOT range-filtered here — SavedSettings already validates on assign, and
        // an out-of-range straggler resolves to a graceful "Item {id}" rather than being silently dropped.
        private static void CollectIds(List<int> into, int[] src)
        {
            if (src == null) return;
            foreach (var id in src) into.Add(id);
        }

        // Poop priority by fruit, ported verbatim from the retired WinForms YggPanel (deleted in
        // 4a9beab). Lower ranks first. Matched on the fruit's NAME rather than its index because the
        // orchard's indices shift as fruits unlock, and the guide talks about fruits by name.
        private static int PoopRank(string shortName)
        {
            string n = (shortName ?? "").ToLowerInvariant();
            if (n.Contains("pomegranate")) return 0;
            if (n.Contains("macguffin") && n.Contains("beta")) return 1;
            if (n.Contains("macguffin")) return 2;
            if (n.Contains("knowledge")) return 3;
            if (n.Contains("quirk")) return 4;
            if (n.Contains("luck")) return 5;
            if (n.Contains("gold")) return 6;
            if (n.Contains("adventure")) return 7;
            return 9;
        }

        // The mode name a loadout key belongs to, for user-facing sentences ("Filled the Titan list...").
        // Only the five objective-backed modes are listed: Loot Hunter's set is a hybrid the optimizer
        // assembles itself and Shockwave has no objective, so neither can reach applyObjective — the
        // page doesn't render a fill button for them. The key is a readable fallback either way.
        private static string LoadoutLabel(string key)
        {
            switch (key)
            {
                case "TitanLoadout": return "Titan";
                case "GoldDropLoadout": return "Gold";
                case "QuestLoadout": return "Quest";
                case "YggdrasilLoadout": return "Yggdrasil";
                case "CookingLoadout": return "Cooking";
                default: return key;
            }
        }

        // Assign a gear-id int[] to a named list setting (boost lists + the 7 loadout modes). Returns false
        // for an unknown key. Used by both setSettingList and setLoadoutFromGear.
        private static bool AssignGearList(SavedSettings s, string key, int[] arr)
        {
            switch (key)
            {
                case "PriorityBoosts": s.PriorityBoosts = arr; return true;
                case "BoostBlacklist": s.BoostBlacklist = arr; return true;
                case "TitanLoadout": s.TitanLoadout = arr; return true;
                case "GoldDropLoadout": s.GoldDropLoadout = arr; return true;
                case "QuestLoadout": s.QuestLoadout = arr; return true;
                case "YggdrasilLoadout": s.YggdrasilLoadout = arr; return true;
                case "CookingLoadout": s.CookingLoadout = arr; return true;
                case "LootHunterAccessories": s.LootHunterAccessories = arr; return true;
                case "Shockwave": s.Shockwave = arr; return true;
                default: return false;
            }
        }

        // Assign a whole int[] to a fixed-index array setting (transform per-chain flags, card thresholds).
        private static bool AssignIntArray(SavedSettings s, string key, int[] arr)
        {
            switch (key)
            {
                case "TransformAutoClimb": s.TransformAutoClimb = arr; return true;
                case "TransformKeepMax": s.TransformKeepMax = arr; return true;
                case "TransformFilter": s.TransformFilter = arr; return true;
                case "CardRarities": s.CardRarities = arr; return true;
                case "CardCosts": s.CardCosts = arr; return true;
                default: return false;
            }
        }

        // Validate + dedupe a fixed-vocabulary string list; null for an unknown key.
        private static string[] FilterStrList(string key, List<string> raw)
        {
            var seen = new HashSet<string>();
            var outL = new List<string>();
            if (key == "BoostPriority")
            {
                foreach (var s in raw) { var t = s == null ? "" : s.Trim(); if ((t == "Power" || t == "Toughness" || t == "Special") && seen.Add(t)) outL.Add(t); }
                return outL.ToArray();
            }
            if (key == "CardSortOrder")
            {
                var sl = CardManager.sortList;
                if (sl != null) foreach (var s in raw) { if (s != null && System.Array.IndexOf(sl, s) >= 0 && seen.Add(s)) outL.Add(s); }
                return outL.ToArray();
            }
            return null;
        }

        // Whether id is a real adventure enemy sprite (mirrors SavedSettings.IsAdvEnemy). Runs on the main
        // thread (command drain), so reading the live enemy list is safe.
        private static bool IsAdvEnemyId(int id)
        {
            try
            {
                var el = Main.Character?.adventureController?.enemyList;
                if (el == null) return false;
                for (int z = 0; z < el.Count; z++)
                {
                    var zl = el[z];
                    if (zl == null) continue;
                    foreach (var en in zl) if (en != null && en.spriteID == id) return true;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------- snapshot builder (MAIN THREAD)

        // ── BUILD IDENTITY ────────────────────────────────────────────────────────────────────────
        // Cached: the answers cannot change while this payload is loaded (a hot reload constructs a
        // NEW UiBridge from a NEW assembly), and the companion read is a file stat that has no
        // business running once a second.
        private static DateTime? _advisorBuilt, _companionBuilt;
        // The bootstrap answers a DIFFERENT question than the other two — not "was it deployed" but
        // "is this session running the copy that is on disk". See BuildStamp's bootstrap section.
        private static DateTime? _bootstrapOnDisk, _bootstrapInjected;
        private static bool _buildsResolved;
        // True only when the wwwroot on screen is established to be a copy OTHER than the deployed one
        // (audit/42 §3.2). Unknown stays false — the same rule the stale gate uses.
        private static bool _companionDevCopy;

        // The advisor's own build time, from the stamped assembly identity NGUAdvisor.csproj bakes in
        // (AssemblyName = "NGUAdvisor.r" + yyMMddHHmmss) — already there for hot reload, reused here.
        private static DateTime? AdvisorBuiltAt()
        {
            ResolveBuilds();
            return _advisorBuilt;
        }

        // The companion's build time, taken from the mtime of the index.html it actually serves.
        // wwwroot is copied with PreserveNewest by DeployCompanion, so the deployed file keeps the
        // source timestamp — which is exactly the number that reveals a UI that was never shipped.
        private static DateTime? CompanionBuiltAt()
        {
            ResolveBuilds();
            return _companionBuilt;
        }

        // Whether that timestamp belongs to a copy other than the deployed one (audit/42 §3.2).
        private static bool CompanionIsDevCopy()
        {
            ResolveBuilds();
            return _companionDevCopy;
        }

        // Where the RUNNING companion serves its wwwroot from, or null when that cannot be established.
        //
        // ⚠ ADVISOR-SIDE ON PURPOSE, and this keeps it that way. The page must not report its own build
        // — a constant baked into a stale page would keep claiming to be current, which is the exact
        // failure the gate exists to catch. So the advisor LOCATES the copy instead of asking for it:
        // MainForm maps AppContext.BaseDirectory\wwwroot, and AppContext.BaseDirectory is the directory
        // of the running exe. One process enumeration, once per payload load, off the cached path.
        //
        // Every failure mode here answers null (unknown), never a guess: Mono can refuse MainModule
        // across a bitness boundary, the process can exit mid-enumeration, and neither is evidence
        // about the UI.
        private static string RunningCompanionDir()
        {
            string dir = null;
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(BuildStamp.CompanionExe));
                foreach (var p in procs)
                {
                    try
                    {
                        if (dir == null)
                        {
                            var mod = p.MainModule;
                            var exe = mod == null ? null : mod.FileName;
                            // Belt and braces: a process NAME match is not a path match, and Mono has
                            // historically handed back the caller's own module for a foreign process.
                            if (!string.IsNullOrEmpty(exe) &&
                                string.Equals(Path.GetFileName(exe), BuildStamp.CompanionExe,
                                              StringComparison.OrdinalIgnoreCase))
                                dir = Path.GetDirectoryName(exe);
                        }
                    }
                    catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
            return dir;
        }

        private static void ResolveBuilds()
        {
            if (_buildsResolved) return;
            _buildsResolved = true;

            try { _advisorBuilt = BuildStamp.Parse(Assembly.GetExecutingAssembly().GetName().Name); }
            catch { _advisorBuilt = null; }

            // Same locator the auto-launch uses: <injectorDir>, taken from injector-path.txt. Missing
            // file or missing path = unknown, which is NOT stale. Hoisted out of the companion read
            // because the bootstrap lives in that same directory.
            string injectorDir = null;
            try
            {
                var pathFile = Path.Combine(Main.GetSettingsDir(), "injector-path.txt");
                if (File.Exists(pathFile))
                {
                    var dir = (File.ReadAllText(pathFile) ?? "").Trim();
                    if (dir.Length > 0) injectorDir = dir;
                }
            }
            catch { injectorDir = null; }

            // The DEPLOYED copy: <injectorDir>\companion\, the folder the auto-launch starts the
            // companion from. An unknown injectorDir leaves it unknown, which is NOT stale.
            string deployedDir = null, deployedHtml = null;
            try
            {
                if (injectorDir != null)
                {
                    deployedDir = Path.Combine(injectorDir, "companion");
                    deployedHtml = BuildStamp.IndexHtmlIn(deployedDir);
                }
            }
            catch { deployedDir = null; deployedHtml = null; }

            // The copy ON SCREEN. audit/42 §3.2: these are the same file when the companion is the
            // auto-launched one, and a DIFFERENT file whenever a developer runs it out of its own bin\,
            // which is what NGUAdvisorCompanion.csproj tells them to do. Reading the deployed copy in
            // that case reports a healthy pair while a six-day-old UI is on screen.
            string servedDir = null, servedHtml = null;
            try
            {
                servedDir = RunningCompanionDir();
                servedHtml = BuildStamp.IndexHtmlIn(servedDir);
            }
            catch { servedDir = null; servedHtml = null; }

            // Prefer what is on screen; fall back to the deployed copy (which is what the NEXT launch
            // will serve, and the only answer available when the companion is not running).
            string html = null;
            try
            {
                if (servedHtml != null && File.Exists(servedHtml)) html = servedHtml;
                else { html = (deployedHtml != null && File.Exists(deployedHtml)) ? deployedHtml : null; servedDir = null; }
            }
            catch { html = null; }

            try { if (html != null) _companionBuilt = File.GetLastWriteTime(html); }
            catch { _companionBuilt = null; }

            try { _companionDevCopy = BuildStamp.IsDevCopy(html, deployedHtml); }
            catch { _companionDevCopy = false; }

            // The bootstrap. Both reads are cheap, happen once per payload load, and are independent
            // of the pair above — a stale bootstrap says nothing about the UI and vice versa.
            try
            {
                if (injectorDir != null)
                {
                    var boot = Path.Combine(injectorDir, BuildStamp.BootstrapFile);
                    // Copy preserves the source timestamp, so this is the bootstrap's BUILD time.
                    if (File.Exists(boot)) _bootstrapOnDisk = File.GetLastWriteTime(boot);
                }
                var bootLog = Path.Combine(Main.GetSettingsDir(), "logs", "bootstrap.log");
                if (File.Exists(bootLog))
                    _bootstrapInjected = BuildStamp.ParseInjectionTime(ReadTail(bootLog, 64 * 1024));
            }
            catch { _bootstrapOnDisk = null; _bootstrapInjected = null; }

            // Say it once per payload load, and only when there is something to say. SEPARATE messages
            // rather than one combined line: the three cases name three different fixes (re-deploy the
            // UI, rebuild the dev companion, restart the game), and a gate that names the wrong fix is
            // worse than no gate. A dev copy is always worth one line — it is invisible otherwise, and
            // it changes what the fix is.
            try
            {
                var msg = _companionDevCopy
                    ? BuildStamp.DevCopyMessage(_advisorBuilt, _companionBuilt, HostPath.For(servedDir), HostPath.For(deployedDir))
                    : BuildStamp.StaleMessage(_advisorBuilt, _companionBuilt);
                if (msg != null) Main.Log("Advisor: " + msg);
            }
            catch { }

            try
            {
                var msg = BuildStamp.BootstrapStaleMessage(_bootstrapInjected, _bootstrapOnDisk);
                if (msg != null) Main.Log("Advisor: " + msg);
            }
            catch { }
        }

        // Last <maxBytes> of a file, opened SHARED — bootstrap.log is append-open by the bootstrap
        // itself and a plain ReadAllText would contend with it. Bounded because the log spans every
        // session since the machine was set up and only its tail is ever of interest; a partial first
        // line is rejected by BuildStamp.ParseInjectionTime's exact-format parse.
        private static string ReadTail(string path, int maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var take = (int)Math.Min(fs.Length, maxBytes);
                if (take <= 0) return "";
                fs.Seek(-take, SeekOrigin.End);
                var buf = new byte[take];
                int read = 0;
                while (read < take)
                {
                    int n = fs.Read(buf, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }
                return System.Text.Encoding.UTF8.GetString(buf, 0, read);
            }
        }

        private string BuildSnapshotJson(float nextLoopSeconds)
        {
            var c = Main.Character;
            var settings = Main.Settings;
            var root = new JSONObject();
            root["v"] = ProtocolVersion;
            root["seq"] = (double)(++_seq);
            root["ts"] = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // --- which builds are actually running (nav footer + the stale-UI gate) ---
            // Both sides are published so the footer never has to guess, and so a mismatch is visible
            // in the panel and not only in the log. See BuildStamp for why this exists.
            // The product version is what a public build shows INSTEAD of the two build stamps: it is
            // the thing a player can quote in a bug report and compare against the releases page.
            // Emitted on both channels — the page decides what to render, so the HTML stays identical
            // across the two repos alongside the C#.
            root["version"] = Main.DisplayVersion;
            root["channel"] = Main.Channel;
            root["advisorBuild"] = BuildStamp.Format(AdvisorBuiltAt());
            // "(dev)" when the copy on screen is not the deployed one (audit/42 §3.2). Carried in the
            // VALUE rather than a new field on purpose: renderBuildFoot writes s.companionBuild as text,
            // so this reaches every page version — including the stale ones this gate exists to expose.
            root["companionBuild"] = BuildStamp.FormatCompanion(CompanionBuiltAt(), CompanionIsDevCopy());
            root["uiStale"] = BuildStamp.IsUiStale(AdvisorBuiltAt(), CompanionBuiltAt());

            // --- header ---
            bool automation = settings != null && settings.GlobalEnabled;
            root["automation"] = automation;
            root["profile"] = settings == null ? "-"
                : (settings.AutoProfile ? "AUTO (advisor)" : (settings.AllocationFile ?? "-"));
            root["nextLoopSec"] = Num(Math.Round(nextLoopSeconds, 1));
            Safe("action", () => root["action"] = LockManager.GetLockTypeName());

            // --- stage / progression ---
            // Captured for the campaign node below: challenge completion counters are PER DIFFICULTY and the
            // game only exposes the one being played, so the campaign can only verify blocks on this
            // difficulty. Taken from the same ProgressionAnalyzer read rather than a second Detect() call.
            string liveDifficulty = null;
            Safe("stage", () =>
            {
                var prog = ProgressionAnalyzer.Detect();
                root["difficulty"] = prog.Difficulty ?? "Normal";
                liveDifficulty = prog.Difficulty;
                var stage = new JSONObject();
                stage["known"] = prog.Known;
                stage["label"] = prog.Label ?? "-";
                stage["activity"] = prog.Activity ?? "";
                stage["chapter"] = prog.Chapter;
                double elapsed = c != null ? c.rebirthTime.totalseconds : 0;
                stage["rebirthElapsed"] = FormatDuration(elapsed);
                double target = -1;
                try { if (Main.Profile != null) target = Main.Profile.NextRebirthTargetSeconds(); } catch { }
                if (target > 0 && target > elapsed) stage["rebirthRemaining"] = FormatDuration(target - elapsed);
                root["stage"] = stage;

                // goal card
                var goal = new JSONObject();
                goal["text"] = prog.NextGoal ?? "";
                Safe("bossNow", () => { if (c != null) goal["bossNow"] = ZoneHelpers.CurrentHighestBoss(c); });
                root["goal"] = goal;
            });

            // --- exp balance (feeds the goal card's second gauge) ---
            Safe("exp", () =>
            {
                var xb = ExpBalancer.Analyze();
                if (!xb.Known) return;
                var g = root["goal"].AsObject;
                g["expBalance"] = Num(Math.Round(xb.BalancePct, 0));
                g["expBalanced"] = xb.Balanced;
                g["expNext"] = xb.NextShort ?? xb.NextNames ?? "";
            });

            // --- actions (OptimizationAdvisor recommendations) ---
            Safe("actions", () =>
            {
                var recs = OptimizationAdvisor.Analyze();
                var arr = new JSONArray();
                if (recs != null)
                {
                    foreach (var r in recs)
                    {
                        var o = new JSONObject();
                        o["system"] = r.System ?? "";
                        o["text"] = r.Text ?? "";
                        o["severity"] = r.Severity;
                        o["optimal"] = r.Optimal;
                        if (!string.IsNullOrEmpty(r.AutoKey)) o["auto"] = r.AutoKey;
                        arr.Add(o);
                    }
                }
                root["actions"] = arr;
            });

            // --- instruments ---
            var inst = new JSONObject();
            Safe("resources", () =>
            {
                if (c == null) return;
                var res = new JSONObject();
                res["e"] = Pct(c.curEnergy, c.totalCapEnergy());
                res["m"] = Pct(c.magic.curMagic, c.totalCapMagic());
                if (c.res3 != null && c.res3.res3On) res["r3"] = Pct(c.res3.curRes3, c.totalCapRes3());
                inst["resources"] = res;
            });
            Safe("titan", () =>
            {
                var obj = OptimizationAdvisor.NextObjective();
                var t = new JSONObject();
                t["known"] = obj.Known;
                if (obj.Known)
                {
                    // Index is a 0-based titan-ladder index (i==5 -> T6/Beast); the displayed titan number is Index+1.
                    t["name"] = "T" + (obj.Index + 1) + (obj.Version > 0 ? " v" + obj.Version : "");
                    t["stage"] = obj.Stage ?? "";
                    if (c != null && obj.ReqAttack > 0) t["atk"] = ClampPct(c.totalAdvAttack() / obj.ReqAttack * 100.0);
                    if (c != null && obj.ReqDefense > 0) t["def"] = ClampPct(c.totalAdvDefense() / obj.ReqDefense * 100.0);
                    // Third bar: HP regen, the game's other autokill gate from T4 up. ReqRegen is non-zero
                    // ONLY at the "auto-kill" stage of the kill ladder (the guide's first-kill / idle stages
                    // set it to 0), so the key is present exactly when a regen gate is the thing being
                    // chased — its ABSENCE means "no regen requirement here", not "0% of the way there".
                    if (c != null && obj.ReqRegen > 0) t["regen"] = ClampPct(c.totalAdvHPRegen() / obj.ReqRegen * 100.0);

                    // ⚠ THE OBJECTIVE VERSION AND THE SPAWN VERSION ARE DIFFERENT NUMBERS, and this node
                    // used to publish only the first while the titan chip row published only the second —
                    // so the same snapshot line said "T7 v2" in one place and "v1" in another, with nothing
                    // anywhere admitting they were different questions.
                    //
                    //   objV   what the advisor is CHASING (the first not-yet-autokilled version)
                    //   spawnV what will actually spawn — adventure.titan{n}Version, the difficulty
                    //          SELECTOR, which the advisor parks on a lower version whenever a gold bank
                    //          is pending or the chase is abandoned
                    //
                    // They legitimately differ, often for hours. Publishing both lets the goal card say
                    // "chasing v2 — spawn parked on v1" instead of quietly contradicting its own chip row.
                    t["objV"] = obj.Version;
                    try { t["spawnV"] = ZoneHelpers.TitanVersion(obj.Index); } catch { }
                }
                inst["titan"] = t;
            });
            Safe("boostFarm", () =>
            {
                var v = BoostFarmAdvisor.Analyze();
                var b = new JSONObject();
                b["known"] = v.Known;
                if (v.Known)
                {
                    b["best"] = v.BestName ?? "";
                    b["bestZone"] = v.BestZone;
                    b["bestRate"] = Num(Math.Round(v.BestRate, 0));
                    b["itopodRate"] = Num(Math.Round(v.ItopodRate, 0));
                    b["currentZone"] = v.CurrentZone;
                    b["compliant"] = v.Compliant;
                    if (!string.IsNullOrEmpty(v.Text)) b["text"] = v.Text;
                }
                inst["boostFarm"] = b;
            });
            Safe("cube", () =>
            {
                // Infinity Cube power/toughness vs the game's softcaps (boosts stop helping a capped cube).
                var cu = new JSONObject();
                var ch = Main.Character; var ic = Main.InventoryController;
                if (ch != null && ic != null)
                {
                    double cp = ch.inventory.cubePower, ct = ch.inventory.cubeToughness;
                    double cps = ic.cubePowerSoftcap(), cts = ic.cubeToughnessSoftcap();
                    cu["known"] = true;
                    cu["power"] = Num(cp); cu["toughness"] = Num(ct);
                    cu["powerSoftcap"] = Num(cps); cu["toughSoftcap"] = Num(cts);
                    cu["powerPct"] = ClampPct(cps > 0 ? cp / cps * 100.0 : 0);
                    cu["toughPct"] = ClampPct(cts > 0 ? ct / cts * 100.0 : 0);
                }
                else cu["known"] = false;
                inst["cube"] = cu;
            });
            root["instruments"] = inst;

            // --- growth rates (+ bridge-sampled sparklines) ---
            Safe("growth", () =>
            {
                var arr = new JSONArray();
                arr.Add(GrowthNode("EXP", s => s.GExp));
                arr.Add(GrowthNode("NGU", s => s.GNgu));
                arr.Add(GrowthNode("PP", s => s.GPp));
                arr.Add(GrowthNode("AP", s => s.GAp));
                arr.Add(GrowthNode("Cube", s => s.GCubeP));
                root["growth"] = arr;
            });

            // --- feed (bridge-owned ring, fed from Activity.Current) ---
            Safe("feed", () =>
            {
                PumpFeed();
                var arr = new JSONArray();
                // newest first
                for (var n = _feed.Last; n != null; n = n.Previous) arr.Add(n.Value);
                root["feed"] = arr;
            });

            // --- per-system two-layer toggle states (M3) ---
            Safe("systems", () =>
            {
                if (settings == null) return;
                var sys = new JSONObject();
                foreach (var t in Toggles)
                {
                    var o = new JSONObject();
                    if (t.GetAuto != null) { o["hasAuto"] = true; o["auto"] = t.GetAuto(settings); }
                    if (t.GetAdvisor != null) { o["hasAdvisor"] = true; o["advisor"] = t.GetAdvisor(settings); }
                    sys[t.Id] = o;
                }
                root["systems"] = sys;
            });

            // --- scalar setting values for the wired controls (W1) ---
            Safe("settings", () =>
            {
                if (settings == null) return;
                var so = new JSONObject();
                foreach (var b in BindingList) b.Write(so, settings);
                root["settings"] = so;
            });

            // --- macguffin id->name map (static; drives the FavoredMacguffin dropdown). Built once the game
            //     list is populated, then written from cache each snapshot so a reconnecting UI always gets it.
            Safe("macguffins", () =>
            {
                if (_macguffinsCache == null)
                {
                    var list = InventoryManager.macguffinList;
                    if (list != null && list.Count > 0)
                    {
                        var mo = new JSONObject();
                        foreach (var kv in list) mo[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                        _macguffinsCache = mo;
                    }
                }
                if (_macguffinsCache != null) root["macguffins"] = _macguffinsCache;
            });

            // --- non-titan zone id->name map (static; drives the SnipeZone / GearHuntZone dropdowns). Built
            //     once from ZoneHelpers.ZoneList, then written from cache each snapshot for reconnecting UIs. ---
            Safe("zones", () =>
            {
                if (_zonesCache == null)
                {
                    var zl = ZoneHelpers.ZoneList;
                    if (zl != null && zl.Count > 0)
                    {
                        var zo = new JSONObject();
                        foreach (var kv in zl)
                            if (kv.Key >= 0 && !ZoneHelpers.ZoneIsTitan(kv.Key))
                                zo[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                        _zonesCache = zo;
                    }
                }
                if (_zonesCache != null) root["zones"] = _zonesCache;
            });

            // --- adventure enemy spriteId->name map (static; drives the never-snipe blacklist picker). Flat +
            //     deduped across zones (a sprite repeats across zones); built once, then written from cache. ---
            Safe("advEnemies", () =>
            {
                if (_advEnemiesCache == null)
                {
                    var el = Main.Character?.adventureController?.enemyList;
                    if (el != null && el.Count > 0)
                    {
                        var eo = new JSONObject();
                        var seen = new HashSet<int>();
                        for (int z = 0; z < el.Count; z++)
                        {
                            var zl = el[z];
                            if (zl == null) continue;
                            foreach (var en in zl)
                                if (en != null && seen.Add(en.spriteID))
                                    eo[en.spriteID.ToString(CultureInfo.InvariantCulture)] = en.name;
                        }
                        if (eo.Count > 0) _advEnemiesCache = eo;
                    }
                }
                if (_advEnemiesCache != null) root["advEnemies"] = _advEnemiesCache;
            });

            // --- current enemy blacklist (BlacklistedBosses int[]); written each snapshot like boostLists. ---
            Safe("advBlacklist", () =>
            {
                if (settings == null) return;
                root["advBlacklist"] = IntArr(settings.BlacklistedBosses);
            });

            // --- loadout id-lists (the 7 modes); written each snapshot so the list editors reconcile. ---
            Safe("loadouts", () =>
            {
                if (settings == null) return;
                var lo = new JSONObject();
                lo["TitanLoadout"] = IntArr(settings.TitanLoadout);
                lo["GoldDropLoadout"] = IntArr(settings.GoldDropLoadout);
                lo["QuestLoadout"] = IntArr(settings.QuestLoadout);
                lo["YggdrasilLoadout"] = IntArr(settings.YggdrasilLoadout);
                lo["CookingLoadout"] = IntArr(settings.CookingLoadout);
                lo["LootHunterAccessories"] = IntArr(settings.LootHunterAccessories);
                lo["Shockwave"] = IntArr(settings.Shockwave);
                root["loadouts"] = lo;
            });

            // --- what is actually steering the run: the auto profile's segment plan, or the manual
            //     profile's name. The segment chain used to be timeline chips on the WinForms autopilot
            //     panel; that went in the 2.0.0 port and nothing replaced it, so since then the only
            //     way to know which segment was running was to read inject.log. Cheap: three statics
            //     ChallengeOverlay already maintains on its own 30s tick.
            Safe("autoProfile", () =>
            {
                if (settings == null) return;
                var a = new JSONObject();
                bool on = settings.AutoProfile;
                a["on"] = on;
                a["profile"] = settings.AllocationFile ?? "";
                if (on)
                {
                    a["segment"] = ChallengeOverlay.Segment ?? "";
                    a["phase"] = ChallengeOverlay.Phase ?? "";
                    a["index"] = ChallengeOverlay.SegmentIndex;
                    a["runHours"] = Num(Math.Round(ChallengeOverlay.SegmentRunHours, 1));
                    var chain = new JSONArray();
                    var src = ChallengeOverlay.SegmentChain;
                    if (src != null) foreach (var seg in src) chain.Add(seg);
                    a["chain"] = chain;
                }
                root["autoProfile"] = a;
            });

            // --- the last gear swap, so a result that LOOKS wrong can explain itself. Three outcomes,
            //     and conflating them is what makes a swap look broken when it isn't:
            //       swapped — went on as asked
            //       kept    — a slot this objective scores nothing for, still holding what it had.
            //                 BY DESIGN (ChangeGear only swaps in) and the reason Power/Toughness
            //                 survives a Gold Drops swap. NOT a failure.
            //       missed  — asked for and didn't go on. The only one that is a problem.
            //     Static fields written once per swap; nothing is computed here.
            Safe("lastSwap", () =>
            {
                var sw = LoadoutManager.LastSwap;
                if (sw == null) return;
                var o = new JSONObject();
                o["mode"] = sw.Mode ?? "";
                o["swapped"] = sw.Requested.Length - sw.Missed.Length;
                o["requested"] = sw.Requested.Length;
                o["kept"] = IntArr(sw.Kept);
                o["missed"] = IntArr(sw.Missed);
                o["agoSec"] = Num(Math.Round((DateTime.UtcNow - sw.At).TotalSeconds));
                // What the worn set is actually worth for fighting, so "did this cost me the kill?" is
                // answerable rather than inferred. CurrentScore is one scoring pass over the equipped
                // items — no optimizer search — and both objectives are cached for the tick.
                try
                {
                    var pow = GearOptimizer.FindObjective("Power");
                    var tou = GearOptimizer.FindObjective("Toughness");
                    if (pow != null) o["power"] = Num(GearOptimizer.CurrentScore(pow));
                    if (tou != null) o["toughness"] = Num(GearOptimizer.CurrentScore(tou));
                }
                catch { }
                var named = new List<int>(sw.Kept.Length + sw.Missed.Length);
                named.AddRange(sw.Kept);
                named.AddRange(sw.Missed);
                _lastSwapIds = named.ToArray();
                root["lastSwap"] = o;
            });

            // --- which gear objective is in force, and why (Loadouts › Main readout). The advisor's own
            //     resolution, so the page can never disagree with what the equip path will do. Cheap:
            //     a handful of field reads and one string — it NEVER runs the optimizer. ---
            Safe("gearNow", () =>
            {
                var r = GearObjectiveApply.Current();
                var g = new JSONObject();
                g["objective"] = r.Name ?? "";
                g["source"] = r.Source ?? "";
                g["forceRespawn"] = r.ForceRespawn;
                // MANUAL MODE has no objective NAME by construction — the profile row lists item ids.
                // Without this the panel reads "nothing running" on a row that is very much running,
                // which is the failure the whole gear readout exists to prevent.
                g["manual"] = r.IsManual;
                g["manualCount"] = r.IsManual ? r.ManualIds.Length : 0;
                // An objective name can be any string: profiles are hand-editable files, and the pin is
                // a Binding.Str. If it doesn't resolve, the equip path silently does nothing — so the
                // readout must not keep asserting "Running X". FindObjective is a lookup over a static
                // list; it never runs the optimizer.
                string sentence = r.Sentence ?? "";
                if (r.Resolved && r.Name != GearObjectiveResolver.LootHunter
                    && GearOptimizer.FindObjective(r.Name) == null)
                    sentence = "\"" + r.Name + "\" isn't a known objective, so no gear is being chosen. "
                             + "Pick one from the list.";
                g["sentence"] = sentence;
                // The set the optimizer would equip for the objective in force. Comes from the Optimize()
                // ProgressionAnalyzer already runs on a 10s cache — no extra optimizer work — and answers
                // "what does 'optimal' actually mean?", which a profile gear breakpoint otherwise never
                // tells you: it computes its ids, equips them and discards them without writing the
                // profile or any loadout list.
                try
                {
                    if (ProgressionAnalyzer.BestGearFor == r.Name && ProgressionAnalyzer.BestGearIds.Length > 0)
                    {
                        g["bestIds"] = IntArr(ProgressionAnalyzer.BestGearIds);
                        _gearNowIds = ProgressionAnalyzer.BestGearIds;
                    }
                    else _gearNowIds = null;
                }
                catch { _gearNowIds = null; }
                root["gearNow"] = g;
            });

            // --- gear-objective names (static; drives the loadout Advisor-objective dropdowns). Built once. ---
            Safe("gearObjectives", () =>
            {
                if (_gearObjectivesCache == null)
                {
                    var objs = GearObjectives.Objectives;
                    if (objs != null && objs.Count > 0)
                    {
                        var arr = new JSONArray();
                        foreach (var o in objs) arr.Add(o.Name);
                        // Named chains are picked exactly like an objective, so they share the list.
                        var chains = new JSONArray();
                        foreach (var p in GearChain.Presets) { arr.Add(p.Name); chains.Add(p.Name); }
                        _gearChainNamesCache = chains;
                        _gearObjectivesCache = arr;
                    }
                }
                if (_gearObjectivesCache != null) root["gearObjectives"] = _gearObjectivesCache;
                if (_gearChainNamesCache != null) root["gearChainNames"] = _gearChainNamesCache;
            });

            // --- profile-editor pickers (static; built once). The token types each resource has, how
            //     far their index runs and what each index is called, plus the three small vocabularies
            //     the single-value rows use. Published rather than mirrored in the page, so the editor
            //     can never offer a token this build does not understand. ---
            Safe("editorCatalog", () =>
            {
                if (_editorCatalogCache == null)
                {
                    var cat = new JSONObject();
                    var prio = new JSONObject();
                    foreach (ResourceKind kind in Enum.GetValues(typeof(ResourceKind)))
                    {
                        var list = new JSONArray();
                        foreach (var bt in PriorityCatalog.For(kind))
                        {
                            var t = new JSONObject();
                            t["code"] = bt.Code;
                            t["label"] = bt.Label;
                            t["hasIndex"] = bt.HasIndex;
                            t["max"] = bt.IndexMax;
                            var names = EditorCatalog.IndexNames(kind, bt.Code);
                            if (names != null)
                            {
                                var n = new JSONArray();
                                foreach (var name in names) n.Add(name);
                                t["names"] = n;
                            }
                            list.Add(t);
                        }
                        prio[kind == ResourceKind.Energy ? "energy" : kind == ResourceKind.Magic ? "magic" : "r3"] = list;
                    }
                    cat["priority"] = prio;

                    var wandoos = new JSONArray();
                    foreach (var kv in SystemCatalog.WandoosOS) wandoos.Add(kv.Value);
                    cat["wandoos"] = wandoos;
                    var diff = new JSONArray();
                    foreach (var kv in SystemCatalog.Difficulty) diff.Add(kv.Value);
                    cat["ngudiff"] = diff;
                    var cons = new JSONArray();
                    foreach (var kv in SystemCatalog.Consumables)
                    {
                        var c2 = new JSONArray();
                        c2.Add(kv.Key); c2.Add(kv.Value);
                        cons.Add(c2);
                    }
                    cat["consumables"] = cons;
                    _editorCatalogCache = cat;
                }
                root["editorCatalog"] = _editorCatalogCache;
            });

            // --- wish priority + blacklist (int[] wish ids); written each snapshot like boostLists. ---
            Safe("wishLists", () =>
            {
                if (settings == null) return;
                var w = new JSONObject();
                w["priorities"] = IntArr(settings.WishPriorities);
                w["blacklist"] = IntArr(settings.WishBlacklist);
                root["wishLists"] = w;
            });

            // --- boost-type priority order (string[] of Power/Toughness/Special) for AutoBoostPriority. ---
            Safe("boostPriority", () =>
            {
                if (settings == null) return;
                var arr = new JSONArray();
                var bp = settings.BoostPriority;
                if (bp != null) foreach (var s in bp) arr.Add(s);
                root["boostPriority"] = arr;
            });

            // --- keep/trash verdict (Gear > Inventory readout). Serialization only — Compute() runs on
            //     the 10-minute boost-priority cadence or the computeInventory action, never here. The
            //     arrays are rebuilt only when a new verdict lands (At changes); agoSec is re-stamped
            //     every snapshot so the page can show staleness and decide when to auto-recompute. ---
            Safe("invVerdict", () =>
            {
                var v = InventoryAdvisor.Last;
                if (v == null) return;
                if (_invVerdictCache == null || _invVerdictAt != v.At)
                {
                    var o = new JSONObject();
                    var k = new JSONArray();
                    foreach (var kv in v.Keep) { var e = new JSONObject(); e["id"] = kv.Key; e["n"] = Main.CollapseAscended(kv.Value); k.Add(e); }
                    var t = new JSONArray();
                    foreach (var kv in v.Trash) { var e = new JSONObject(); e["id"] = kv.Key; e["n"] = Main.CollapseAscended(kv.Value); t.Add(e); }
                    o["keep"] = k;
                    o["trash"] = t;
                    o["chapter"] = v.GuideChapter ?? "";
                    // Change signature for the page: it rebuilds its lists only when this moves.
                    o["at"] = v.At.Ticks.ToString(CultureInfo.InvariantCulture);
                    _invVerdictCache = o;
                    _invVerdictAt = v.At;
                }
                _invVerdictCache["agoSec"] = Num(Math.Round((DateTime.UtcNow - v.At).TotalSeconds));
                root["invVerdict"] = _invVerdictCache;
            });

            // --- transform chains: per-chain {name, step} descriptors + the 3 per-chain flag arrays. The
            //     generic "Chain #n" names resolve to the base item's name; step = the current owned tier
            //     ("up to current unlocked level"). Descriptors are throttled (a live inventory scan each);
            //     the flags ride every snapshot for reconciliation. ---
            Safe("transform", () =>
            {
                if (settings == null) return;
                var t = new JSONObject();
                if (_transformMetaCache == null || (_seq % 5) == 1)
                {
                    var meta = new JSONArray();
                    var chains = TransformManager.Chains;
                    if (chains != null)
                        for (int i = 0; i < chains.Length; i++)
                        {
                            var ch = chains[i];
                            var o = new JSONObject();
                            string disp = ch.Name;
                            if (disp != null && disp.StartsWith("Chain #") && ch.Tiers != null && ch.Tiers.Length > 0)
                                disp = Main.ItemNameNice(ch.Tiers[0]);
                            o["name"] = disp;
                            var stepsArr = new JSONArray();
                            bool done = false;
                            try
                            {
                                var view = TransformManager.ViewChain(i);
                                done = view.Done;
                                foreach (var st in view.Steps)
                                {
                                    var so = new JSONObject();
                                    so["name"] = st.Name;
                                    so["level"] = (int)Math.Min(st.Level, int.MaxValue);
                                    so["owned"] = st.Owned;
                                    so["tier"] = st.Tier;
                                    stepsArr.Add(so);
                                }
                            }
                            catch { }
                            o["done"] = done;
                            o["steps"] = stepsArr;
                            meta.Add(o);
                        }
                    if (meta.Count > 0) _transformMetaCache = meta;
                }
                if (_transformMetaCache != null) t["chains"] = _transformMetaCache;
                t["autoClimb"] = IntArr(settings.TransformAutoClimb);
                t["keepMax"] = IntArr(settings.TransformKeepMax);
                t["filter"] = IntArr(settings.TransformFilter);
                root["transform"] = t;
            });

            // --- currently-equipped gear names (Loadouts readout; preserves the WinForms "equipped snapshot"). ---
            Safe("equipped", () =>
            {
                var arr = new JSONArray();
                try { foreach (var id in LoadoutManager.CurrentGearIds() ?? new int[0]) if (id > 0) arr.Add(Main.ItemNameNice(id)); }
                catch { }
                root["equipped"] = arr;
            });

            // --- challenge state (Challenges readout: completed / current / queued, from the overlay tracker). ---
            Safe("challengeState", () =>
            {
                var cs = new JSONObject();
                try
                {
                    var block = ChallengeOverlay.Block();
                    string active = null;
                    try { active = ChallengeDetector.Current(); } catch { }
                    var done = new JSONArray();
                    var queued = new JSONArray();
                    JSONObject cur = null;
                    if (block != null)
                        foreach (var e in block)
                        {
                            if (e.Max <= 0) continue;
                            var o = new JSONObject();
                            o["code"] = e.Code; o["cur"] = e.Cur; o["max"] = e.Max;
                            if (e.Code == active) cur = o;
                            else if (e.Cur >= e.Max) done.Add(o);
                            else queued.Add(o);
                        }
                    cs["done"] = done;
                    cs["queued"] = queued;
                    if (cur != null) cs["current"] = cur;
                    cs["active"] = active ?? "";
                }
                catch { }
                root["challengeState"] = cs;
            });

            // --- log directory: the companion reads the advisor's log files directly for the log drawer. ---
            Safe("logDir", () => { root["logDir"] = Main.GetLogDir(); });

            // --- one-shot notice: outcome of the last button action (e.g. Re-optimize gear now). Emit once
            //     then clear so the companion toasts it exactly once. Same thread as the command drain. ---
            Safe("notice", () =>
            {
                if (string.IsNullOrEmpty(_notice)) { _noticeCopy = null; return; }
                root["notice"] = _notice;
                if (!string.IsNullOrEmpty(_noticeCopy)) root["noticeCopy"] = _noticeCopy;
                _notice = null;
                _noticeCopy = null;
            });

            // --- EXP page: base Energy:Magic value ratio + the balancer's on-ratio verdict. The on/off
            //     status is the balancer's 6-stat waterfill (NOT the E:M ratio) so it matches the advisor. ---
            Safe("expRatio", () =>
            {
                var er = new JSONObject();
                var xb = ExpBalancer.Analyze();
                er["known"] = xb.Known;
                if (xb.Known && c != null)
                {
                    er["onRatio"] = xb.Balanced;
                    er["pct"] = Num(Math.Round(xb.BalancePct, 0));
                    double eE = Math.Max(0, c.energyPower) * 150.0 + c.capEnergy / 250.0 + c.energyBars * 80.0;
                    bool mu = c.highestBoss >= 37;
                    double mE = mu ? Math.Max(0, c.magic.magicPower) * 450.0 + c.magic.capMagic * 3.0 / 250.0 + c.magic.magicPerBar * 240.0 : 0;
                    er["energy"] = FormatBig(eE);
                    er["magic"] = FormatBig(mE);
                    double tot = eE + mE;
                    er["energyPct"] = Num(tot > 0 ? eE / tot * 100.0 : 0);
                    er["magicPct"] = Num(tot > 0 ? mE / tot * 100.0 : 0);
                    er["ratioText"] = mE > 0 ? ((eE / mE) * 3.0).ToString("0.0", CultureInfo.InvariantCulture) + " : 1" : "Energy only";
                }
                root["expRatio"] = er;
            });

            // --- Beards page: per-beard current level + permanent levels banked on the next rebirth.
            //     Semantics (confirmed — do not re-derive):
            //       level = b.beardLevel                     — the beard's CURRENT level.
            //       gain  = c.allBeards.addedTrimmings(id)   — the levels this beard would ADD on rebirth.
            //               Only computed when the beard is `active`; an inactive beard legitimately reports
            //               0, meaning "not applicable", NOT "no gain".
            //       perm  = b.permLevel                      — levels ALREADY banked (permanent).
            //     Each ships twice: the legacy pre-formatted string (level/gain/perm) for compatibility, and
            //     a raw numeric sibling (levelRaw/gainRaw/permRaw) so number-formatting policy can live in
            //     the UI instead of requiring an injector rebuild + re-inject. Same both-ways contract the
            //     `growth` node uses (rate + fmt). The strings go away once nothing reads them.
            Safe("beards", () =>
            {
                var bd = new JSONObject();
                if (c != null && c.beards != null && c.beards.beards != null)
                {
                    try { bd["cap"] = c.allBeards.capBeards(); } catch { }
                    var arr = new JSONArray();
                    var names = OptimizationAdvisor.BeardNames;
                    int n = Math.Min(c.beards.beards.Count, 7);
                    for (int id = 0; id < n; id++)
                    {
                        var b = c.beards.beards[id];
                        var o = new JSONObject();
                        o["id"] = id;
                        o["name"] = id < names.Length ? names[id] : ("Beard " + id);
                        bool active = false; try { active = b.active; } catch { }
                        o["active"] = active;
                        long lvl = 0; try { lvl = b.beardLevel; } catch { }
                        o["level"] = FormatBig(lvl);
                        o["levelRaw"] = Num(lvl);
                        long gain = 0; try { if (active) gain = c.allBeards.addedTrimmings(id); } catch { }
                        o["gain"] = FormatBig(gain);
                        o["gainRaw"] = Num(gain);
                        long perm = 0; try { perm = b.permLevel; } catch { }
                        o["perm"] = FormatBig(perm);
                        o["permRaw"] = Num(perm);
                        arr.Add(o);
                    }
                    bd["list"] = arr;
                }
                root["beards"] = bd;
            });

            // --- Quests page: idle-minors policy + banked/cap ("N of N") + current quest progress. ---
            Safe("quests", () =>
            {
                var q = new JSONObject();
                var st = Main.Settings;
                // `managed` (AutoQuest) is DELIBERATELY NOT PUBLISHED. The SystemControlBar already owns
                // the AUTOMATION display for quests, and the quest page states the live plan when one is
                // running, so a third carrier of the same fact is a thing to keep in sync for nothing.
                q["allowMajors"] = st != null && st.AllowMajorQuests;
                q["idleMinors"] = st != null && !st.ManualMinors;
                q["pooling"] = st != null && st.PoolMajorQuests && !st.QuestBurstActive;
                q["bursting"] = st != null && st.QuestBurstActive;
                if (c != null && c.beastQuest != null)
                {
                    var bq = c.beastQuest;
                    q["banked"] = Num(bq.curBankedQuests);
                    try { q["bankCap"] = Num(c.beastQuestController.maxBankedQuests()); } catch { }
                    bool inq = false; try { inq = bq.inQuest; } catch { }
                    q["inQuest"] = inq;
                    if (inq)
                    {
                        bool minor = false; try { minor = bq.reducedRewards; } catch { }
                        q["type"] = minor ? "minor" : "major";
                        long cd = 0, td = 0; try { cd = bq.curDrops; td = bq.targetDrops; } catch { }
                        q["drops"] = Num(cd); q["targetDrops"] = Num(td);
                        if (td > 0) q["progressPct"] = Num(cd / (double)td * 100.0);
                        try { int z = c.beastQuestController.curQuestZone(); q["zone"] = z; if (ZoneHelpers.ZoneList.TryGetValue(z, out var zn)) q["zoneName"] = zn; } catch { }
                        try { q["readyToHandIn"] = c.beastQuestController.readyToHandIn(); } catch { }
                    }
                    else q["type"] = "none";
                }
                root["quests"] = q;
            });

            // --- Money-pit: next-throw prediction (outcome CATEGORY only; the game exposes no gold value),
            //     ready/ETA, and the advisor's throw plan. ---
            Safe("moneypit", () =>
            {
                var mp = new JSONObject();
                try { mp["ready"] = MoneyPitManager.MoneyPitReady(); } catch { }
                try { mp["etaSec"] = Num(MoneyPitManager.TimeUntilReady()); } catch { }
                try { mp["predicted"] = MoneyPitManager.PredictNext().ToString(); } catch { }
                // `throw` and `runMode` are DELIBERATELY NOT PUBLISHED. The pit chip already carries what
                // the operator acts on, and verdict/detail below say the same thing in words a person can
                // read; the raw decision bool and the run-mode flag were extra state to keep in sync for
                // no display. (`throw` is also a JS reserved word — legal as a property, but a name worth
                // not having.) The pit's UI is unfinished on purpose, pending the AdvisorPit /
                // AutoMoneyPit rival-paths decision — this shrinks its surface rather than growing it.
                try { var plan = MoneyPitManager.AdvisorPlan(); mp["verdict"] = plan.Verdict ?? ""; mp["detail"] = plan.Detail ?? ""; } catch { }
                try { var t = MoneyPitManager.ShockwaveTier(); if (t.HasValue) mp["targetTier"] = MoneyPitManager.TierName(t.Value); } catch { }
                root["moneypit"] = mp;
            });

            // --- Yggdrasil page: per-fruit chips (active / maxxed / inactive / purchasable / locked). Rebuilds
            //     the orchard grid the retired WinForms YggPanel used to show (lost in the companion cutover). ---
            Safe("fruits", () =>
            {
                var arr = new JSONArray();
                var yc = c != null ? c.yggdrasilController : null;
                var fruits = c != null && c.yggdrasil != null ? c.yggdrasil.fruits : null;
                if (c != null && yc != null && fruits != null)
                {
                    long seeds = 0; try { seeds = c.yggdrasil.seeds; } catch { }
                    root["yggSeeds"] = FormatBig(seeds);
                    int cap = 10; try { cap = yc.capTier(); } catch { }
                    root["yggCap"] = cap;
                    var fc = (yc.fruits != null && yc.fruits.Length > 0) ? yc.fruits[0] : null;
                    double thr = 0; try { thr = fc != null ? fc.tierThreshold() : 0; } catch { }
                    bool cardsOn = false; try { cardsOn = c.cards.cardsOn; } catch { }
                    int count = fruits.Count;

                    // WHERE THE POOP SHOULD GO. Restored from the retired WinForms YggPanel, which was
                    // deleted wholesale in the 2.0.0 companion cutover (4a9beab) — the ranking lived
                    // INSIDE that file, so unlike the other casualties of that commit there was no
                    // orphaned caller to find, just an advisor opinion that stopped existing. The
                    // Yggdrasil view has claimed to "advise poop placement" ever since without doing it.
                    //
                    // Two passes on purpose: the recommendation is a ranking ACROSS fruits, so it cannot
                    // be decided inside the per-fruit loop below. Take(max(3, currently-pooped)) mirrors
                    // the original — never advise fewer targets than the player already has poop on, or
                    // the advice would read as "remove poop" when it only means "these three are best".
                    var poopBest = new HashSet<int>();
                    try
                    {
                        int poopCount = 0;
                        var cand = new List<KeyValuePair<int, long>>();   // fruit index -> maxTier, for the tiebreak
                        for (int i = 0; i < count; i++)
                        {
                            long mt = 0; try { mt = fruits[i].maxTier; } catch { }
                            if (mt == 0) continue;                                   // not owned
                            try { if (fruits[i].usePoop) poopCount++; } catch { }
                            bool growing = false; try { growing = fruits[i].growing(); } catch { }
                            if (growing) cand.Add(new KeyValuePair<int, long>(i, mt));
                        }
                        string NameOf(int i) { try { return yc.fruitName != null && i < yc.fruitName.Count ? yc.fruitName[i] : ""; } catch { return ""; } }
                        cand.Sort((x, y) =>
                        {
                            int r = PoopRank(NameOf(x.Key)).CompareTo(PoopRank(NameOf(y.Key)));
                            if (r != 0) return r;
                            return y.Value.CompareTo(x.Value);                        // then the deeper fruit
                        });
                        int take = Math.Max(3, poopCount);
                        for (int k = 0; k < cand.Count && k < take; k++) poopBest.Add(cand[k].Key);
                    }
                    catch { }
                    for (int i = 0; i < count; i++)
                    {
                        long maxTier = 0; try { maxTier = fruits[i].maxTier; } catch { }
                        if (i >= 15 && !cardsOn && maxTier == 0) continue;   // hide deep card-fruits until cards unlock
                        var o = new JSONObject();
                        o["i"] = i;
                        string name = "?"; try { if (yc.fruitName != null && i < yc.fruitName.Count) name = yc.fruitName[i]; } catch { }
                        o["name"] = name;
                        string state;
                        if (maxTier == 0)
                        {
                            long cost = 0; try { if (yc.baseSeedCost != null && i < yc.baseSeedCost.Count) cost = yc.baseSeedCost[i]; } catch { }
                            bool gateOpen = true;
                            try { if (i == 8) gateOpen = false; else if (i == 9) gateOpen = c.settings.itopodOn; else if (i == 14) gateOpen = c.settings.beastOn; } catch { }
                            bool affordable = cost > 0 && seeds >= cost && gateOpen;
                            state = affordable ? "purchasable" : "locked";
                            if (cost > 0 && gateOpen) o["cost"] = FormatBig(cost);   // seeds required to unlock
                            if (affordable) o["affordable"] = true;
                        }
                        else
                        {
                            bool maxxed = false; try { maxxed = fc != null && fc.fruitMaxxed(i); } catch { }
                            bool active = false; try { active = fruits[i].growing(); } catch { }
                            int tier = 0; try { tier = fc != null ? fc.harvestTier(i) : 0; } catch { }
                            double frac = 0; try { double sec = fruits[i].seconds; if (thr > 0) frac = (sec % thr) / thr; } catch { }
                            state = maxxed ? "maxxed" : active ? "active" : "inactive";
                            // Where poop IS, and where the advisor thinks it should be. Two separate
                            // facts: a fruit can be pooped and not recommended (move it), or
                            // recommended and not pooped (put some here).
                            try { if (fruits[i].usePoop) o["poop"] = true; } catch { }
                            if (poopBest.Contains(i)) o["poopRec"] = true;
                            o["tier"] = tier;
                            o["maxTier"] = Num(maxTier);
                            o["frac"] = Num(maxxed ? 100 : active ? Math.Round(frac * 100, 0) : 0);   // growth % through the current tier
                        }
                        o["state"] = state;
                        arr.Add(o);
                    }
                }
                root["fruits"] = arr;
            });

            // --- Perks & Quirks page: the full guide-ordered plan with per-step status, keyed to the
            //     user's current chapter (was: only the single "next buy" reached the UI). ---
            Safe("perkPlan", () => { root["perkPlan"] = PlanArr(SpendPlanner.PerkPlanView()); });
            Safe("quirkPlan", () => { root["quirkPlan"] = PlanArr(SpendPlanner.QuirkPlanView()); });

            // --- cards: static meta (bonus-type rows / rarity map / cost list / sort vocab) + live filter arrays. ---
            Safe("cards", () =>
            {
                if (settings == null) return;
                if (_cardMetaCache == null)
                {
                    var meta = new JSONObject();
                    var types = new JSONArray();
                    var tn = CardManager.bonusTypeNames;
                    if (tn != null) foreach (var n in tn) types.Add(n);
                    meta["types"] = types;
                    var rar = new JSONObject();
                    if (CardManager.rarityList != null) foreach (var kv in CardManager.rarityList) rar[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                    meta["rarities"] = rar;
                    var costs = new JSONArray();
                    if (CardManager.costList != null) foreach (var x in CardManager.costList) costs.Add(x);
                    meta["costs"] = costs;
                    var sorts = new JSONArray();
                    if (CardManager.sortList != null) foreach (var srt in CardManager.sortList) sorts.Add(srt);
                    meta["sorts"] = sorts;
                    if (types.Count > 0) _cardMetaCache = meta;   // only cache once the game enum is populated
                }
                var cardsObj = new JSONObject();
                if (_cardMetaCache != null) cardsObj["meta"] = _cardMetaCache;
                cardsObj["rarities"] = IntArr(settings.CardRarities);
                cardsObj["costs"] = IntArr(settings.CardCosts);
                var so = new JSONArray();
                if (settings.CardSortOrder != null) foreach (var srt in settings.CardSortOrder) so.Add(srt);
                cardsObj["sortOrder"] = so;
                root["cards"] = cardsObj;
            });

            // --- titan kill grid (W3): TitanSwapTargets bool[14] + static abbrev labels. ---
            Safe("titans", () =>
            {
                if (settings == null) return;
                var t = new JSONObject();
                var names = new JSONArray();
                var ab = TitanTables.Abbrev;
                if (ab != null) foreach (var n in ab) names.Add(n);
                t["names"] = names;
                var targets = new JSONArray();
                var tst = settings.TitanSwapTargets;
                if (tst != null) foreach (var b in tst) targets.Add(b);
                t["targets"] = targets;
                root["titans"] = t;
            });

            // --- per-titan autokill readiness (`titans.ak`): the chip row on the Titans view. ---
            // TWELVE entries, not fourteen. ZoneHelpers.AutokillAvailable returns false OUTRIGHT for
            // titanIndex >= 12, so Tippi and Traitor have no autokill path at all and a chip for them could
            // only ever read "short" forever — a permanently red promise the game will never keep. They are
            // omitted; `titans.names` still carries all 14 for the manual kill grid, so the UI must index
            // this array by its own `i` field and NOT by position in `names`.
            //
            // Three states (a fourth, "unknown", appears only on a failed read — see below):
            //   ready — ZoneHelpers.AutokillAvailable is true. The game will fire.
            //   gated — every stat threshold is met but the game still refuses. Pushing stats will NOT help.
            //           Reachable at T4 (needs item 135 maxxed) and T5 (needs boss5Kills >= 3); for T6-T12
            //           the game's method IS the stat check, so stats-met implies ready there.
            //   short — at least one stat threshold unmet.
            // The classification itself lives in TitanTables.AkState so it is unit-testable without the game.
            //
            // NONE OF THOSE FOUR MEAN "KILLABLE RIGHT NOW". The game gates the kill on a respawn clock the
            // autokill methods never consult, and resets that clock on every kill, so `ready` + "80 minutes of
            // cooldown left" is an ordinary steady state — the chip used to claim availability the game would
            // refuse. `respawnSec` is that clock, in whole seconds, 0 = available now. It rides a DIFFERENT
            // cadence to everything else here (see below) and, being independent of the gate reads, it is
            // published for every state including "unknown". ABSENT MEANS UNREADABLE — never assume 0.
            //
            // NOR DOES ANY OF THEM MEAN "THE GAME WILL SPAWN IT". Titans 6-9 carry a per-titan unlock flag
            // (`character.adventure.titan{N}Unlocked`) that gates both the autokill branch and the spawn table
            // and that no autokill method consults, so a locked Beast reads `ready` with a zero clock forever.
            // `unlocked` is that flag, published for all twelve (vacuously true for the eight with no such
            // gate) so ABSENCE STILL MEANS UNREADABLE. Render LOCKED on `unlocked === false` regardless of
            // `state`; never on its absence. See TitanTables' `unlocked` header for why this is a separate key
            // rather than a narrower AutokillAvailable.
            //
            // ...and Walderp (i == 4) gets one more: `waldo`. His respawn clock is the only one the game
            // FREEZES — it advances only while `waldoDefeats <= waldoFinds || waldoFinds >= 4` — so while he
            // is hiding in a menu his `respawnSec` is a stalled number, not a countdown. `waldo.hiding` says
            // so, `waldo.finds`/`defeats` say how far through the hunt he is, and `waldo.menu`/`menuName` say
            // where he currently is when that is readable. The game's own zone-16 label replaces the timer
            // outright in this state ("WALDERP IS HIDING IN THE MENUS! FIND HIM!") — do the same.
            //
            // REFLECTION BUDGET: AutokillAvailable calls a game method by name for titanIndex >= 5 and
            // TitanVersion reflects a save field, i.e. up to 14 reflective calls per rebuild. So the whole
            // array is rebuilt on the same ~5 s cadence as _profilesCache/_transformMetaCache and the SAME
            // JSONArray instance is republished on every other tick (one dictionary write). Consequence the
            // UI should know: these chips and their bars step every ~5 s. The CURRENT target's bars in
            // instruments.titan are still recomputed every tick.
            //
            // ...AND `respawnSec` IS DELIBERATELY NOT ON THAT CADENCE. A gate answer that is 5 s old is still
            // the right answer; a countdown that is 5 s old is a wrong number, on screen, ticking. The two
            // alternatives were both worse. Publishing it on the cache with an as-of sequence pushes a
            // client-side countdown back into the page, and this project has already deleted one of those for
            // continuing to tick after the data stopped arriving — and the game gives it a way to be wrong
            // even when data IS arriving: boss5Spawn (Walderp) only advances while
            // `waldoDefeats <= waldoFinds || waldoFinds >= 4`, so his clock can sit FROZEN at a positive value
            // and any extrapolation from it counts down to an availability that never comes. Publishing only
            // for `ready` chips saves nothing that matters and costs the key its meaning: absence would then
            // mean either "unreadable" or "we chose not to look", and the UI could not tell a broken read from
            // a deliberate omission. So: all twelve, every tick, absence reserved for "unknown".
            //
            // THE COST, MEASURED rather than assumed (net9 microbenchmark of the exact two reflective reads
            // TimeTillTitanSpawn performs, through a cache identical to ReflectionExtensions'): 0.31 us for
            // one titan, 4.2 us and 1.6 KB for all twelve. The same tick already spends ~215 us and ~314 KB
            // just serializing this snapshot to its JSON line — one step of many — so the whole sweep is ~2%
            // of that step and 0.0004% of the 1 s budget. Mono's reflection is several times slower than
            // net9's, but so is its string building; the ratio is what survives the port, and the ratio is
            // not close. For further scale: OptimizationAdvisor.NextObjective() already makes a comparable
            // number of reflective autokill calls and Publish() calls it TWICE per tick.
            Safe("titanAk", () =>
            {
                if (_titanAkCache == null || (_seq % 5) == 1) _titanAkCache = BuildTitanAk();
                // The titans node above bails out when settings are missing; don't let that take the chip
                // row with it. Reading a missing key yields a JSONLazyCreator (never a JSONObject) and does
                // not insert anything, so this is a safe presence test.
                var host = root["titans"] as JSONObject;
                if (host == null) { host = new JSONObject(); root["titans"] = host; }
                host["ak"] = _titanAkCache;
                // Highest titan index this difficulty offers at all (Normal ends at T6). Lets the UI dim the
                // chips for content that does not exist yet instead of showing a wall of red. Clamped to the
                // table: DifficultyMaxTitanIndex reports 13 on Sadistic, but only 12 titans have an AK path.
                try { host["akMaxIndex"] = Math.Min(OptimizationAdvisor.DifficultyMaxTitanIndex(), TitanTables.Ak.Length - 1); } catch { }
                // LAST, and in its own guard: the chips are already attached above, so even a total failure
                // of the respawn overlay costs the row nothing but its timers. Mutates the cached array in
                // place — the same instance `host["ak"]` now points at — so the stamp lands on this tick's
                // output whether or not the row was rebuilt.
                if (_titanSpawnReader == null) _titanSpawnReader = ZoneHelpers.TimeTillTitanSpawn;
                try { TitanTables.StampRespawn(_titanAkCache, _titanSpawnReader); } catch { }
            });

            // --- boost "time to max": how many boosts the targets still need, how fast they are being
            //     applied, and therefore when each one finishes. Every number here was ALREADY computed
            //     by InventoryManager.ShowBoostProgress on its 60s pump and written only to inject.log
            //     ("ETA: N minutes."); this just puts it on the wire. Nothing is recomputed per tick.
            //
            //     The per-item ETA is CUMULATIVE down the boost order — boosts are applied to the
            //     priority list front-first, so item N is only finished after everything ahead of it is.
            //     That is what makes reordering the list a real decision instead of a guess.
            Safe("boostEta", () =>
            {
                var needed = InventoryManager.LastNeeded;
                if (needed == null) return;                       // no sample yet (first 60s after load)
                var o = new JSONObject();
                float total = needed.Total();
                float perMin = InventoryManager.LastBoostsPerMinute;
                // UNITS: these are STAT POINTS to cap, not a count of boost items. GetNeededBoosts sums
                // (level-scaled cap - current) across attack/defence/specs. How many points one dropped
                // boost delivers depends on that boost item's own level, which nothing here models — so
                // never label this "boosts".
                o["power"] = Num(needed.power);
                o["toughness"] = Num(needed.toughness);
                o["special"] = Num(needed.special);
                o["total"] = Num(total);
                o["perMinute"] = Num(perMin);
                // Seconds, and -1 for "not measurable yet" — mirrors the money pit's etaSec, which the
                // page already renders with fmtSec. The rate is 0 until the rolling average has samples.
                o["etaSec"] = Num(perMin > 0 ? total / perMin * 60.0 : -1);
                var per = new JSONObject();
                var order = InventoryManager.LastBoostOrder;
                var map = InventoryManager.LastPerItem;
                if (order != null && map != null && perMin > 0)
                {
                    float run = 0;
                    foreach (var id in order)
                    {
                        if (!map.TryGetValue(id, out var n)) continue;
                        run += n;
                        per[id.ToString(CultureInfo.InvariantCulture)] = Num(run / perMin * 60.0);
                    }
                }
                o["etaByItem"] = per;
                // WHICH ITEM IS ACTUALLY GETTING THE BOOSTS. GetBoostSlots filters to items that still
                // need some, and BoostInventory walks that array in order calling applyAllBoosts until
                // the stock runs out — so element 0 has first call on every boost that drops. It is not
                // necessarily from the priority list: the order is priority, then equipped gear, then
                // locked inventory, so with an empty (or fully-capped) priority list this is whatever
                // the advisor fell through to, which is exactly the case worth naming.
                if (order != null && order.Length > 0)
                {
                    o["current"] = order[0];
                    bool inList = false;
                    var pri = settings.PriorityBoosts;
                    if (pri != null) foreach (var id in pri) if (id == order[0]) { inList = true; break; }
                    o["currentInList"] = inList;
                    _boostCurrentId = new[] { order[0] };
                }
                root["boostEta"] = o;
            });

            // --- boost list editors (W3): PriorityBoosts + BoostBlacklist int[] gear-id arrays. ---
            Safe("boostLists", () =>
            {
                if (settings == null) return;
                var bl = new JSONObject();
                var pri = new JSONArray(); var pb = settings.PriorityBoosts; if (pb != null) foreach (var id in pb) pri.Add(id);
                var blk = new JSONArray(); var bb = settings.BoostBlacklist; if (bb != null) foreach (var id in bb) blk.Add(id);
                bl["priority"] = pri; bl["blacklist"] = blk;
                root["boostLists"] = bl;
            });

            // --- gear id->name map, so the UI can render `[188] "Ascended Edgy Helmet"` instead of a bare
            //     integer. DELIBERATELY NOT the game's whole item table (~500 entries / several KB on a
            //     ~30 KB line published at 1 Hz): only the ids the snapshot itself already ships, i.e. the
            //     7 loadout lists + the 2 boost lists. Every other id-bearing node is out of scope on
            //     purpose: wishLists carries WISH ids (Consts.MAX_WISH_ID, a different table), transform's
            //     autoClimb/keepMax/filter are per-chain flags (and its steps already carry names),
            //     advBlacklist carries adventure sprite ids (see advEnemies), boostPriority is a string
            //     vocabulary, gearObjectives is a name list, and equipped already ships names.
            //
            //     Cache policy mirrors _zonesCache/_macguffinsCache: build once, then reuse the SAME
            //     JSONObject every snapshot. It is rebuilt only when the referenced id SET changes (a
            //     loadout edit), so the steady-state per-tick cost is one dictionary write. Names are
            //     resolved through Main.ItemNameNice, which is total (it returns "" for id <= 0 and "?"
            //     if the game's itemInfo table isn't up yet) — so this can never throw into the build.
            //     A build that hit an unresolved id is marked incomplete and retried every 5th snapshot,
            //     which lets the map heal once the item table populates without a per-tick rebuild.
            Safe("itemNames", () =>
            {
                if (settings == null) return;
                var ids = new List<int>();
                CollectIds(ids, settings.TitanLoadout);
                CollectIds(ids, settings.GoldDropLoadout);
                CollectIds(ids, settings.QuestLoadout);
                CollectIds(ids, settings.YggdrasilLoadout);
                CollectIds(ids, settings.CookingLoadout);
                CollectIds(ids, settings.LootHunterAccessories);
                CollectIds(ids, settings.Shockwave);
                CollectIds(ids, settings.PriorityBoosts);
                CollectIds(ids, settings.BoostBlacklist);
                // ...and the main-gear best set, so its chips render named rather than as bare integers.
                // It changes only when the optimizer's answer changes (~10s cache upstream), so it folds
                // into the same signature-keyed rebuild as everything else here.
                CollectIds(ids, _gearNowIds);
                CollectIds(ids, _lastSwapIds);
                CollectIds(ids, _boostCurrentId);
                ids.Sort();

                // Sorted -> dedupe and build the change signature in one pass.
                var sb = new StringBuilder();
                var uniq = new List<int>();
                int prev = int.MinValue;
                foreach (var id in ids)
                {
                    if (id == prev) continue;
                    prev = id;
                    uniq.Add(id);
                    sb.Append(id.ToString(CultureInfo.InvariantCulture)).Append(',');
                }
                string sig = sb.ToString();

                bool stale = _itemNamesCache == null || _itemNamesSig != sig;
                bool retry = !_itemNamesComplete && (_seq % 5) == 1;
                if (stale || retry)
                {
                    var o = new JSONObject();
                    bool complete = true;
                    foreach (var id in uniq)
                    {
                        string nm = Main.ItemNameNice(id);
                        if (string.IsNullOrEmpty(nm) || nm == "?")
                        {
                            nm = "Item " + id.ToString(CultureInfo.InvariantCulture);
                            if (id > 0) complete = false;      // id <= 0 has no name by definition, not a failure
                        }
                        o[id.ToString(CultureInfo.InvariantCulture)] = nm;
                    }
                    _itemNamesCache = o;
                    _itemNamesSig = sig;
                    _itemNamesComplete = complete;
                }
                root["itemNames"] = _itemNamesCache;
            });

            // --- profiles: list (throttled disk read) + active + auto-profile (M3) ---
            // WHERE THE POOL ACTUALLY WENT. Per-lane seat, cap, offered, took and the unallocated
            // remainder — the numbers that until now existed only as prose in debug.log.
            //
            // ⚠ SIGNATURE-GATED, AND THAT IS A MEASURED DECISION, NOT A PREFERENCE. This snapshot is
            // already ~30KB on the wire every second and costs ~314KB of allocation per tick to build,
            // on the Unity main thread, on a runtime this project has repeatedly flagged as
            // GC-sensitive. Lane data is the largest thing anyone has proposed adding to it. But the
            // allocator ALREADY computes a signature of its own result for the log throttle, so
            // "did anything actually move" is free to ask — the heavy payload rides only when it did.
            //
            // `seq` rides every tick regardless, and cheaply. Without it a quiet allocator and a dead
            // pipe look identical on the page, which was the one real objection to gating at all.
            Safe("pools", () =>
            {
                var pools = new JSONObject();
                pools["seq"] = _seq;
                bool anyChanged = false;

                Action<string, ConstraintLayer.Plan> emit = (key, plan) =>
                {
                    if (plan == null || plan.Lanes == null) return;

                    // The wish take is read BEFORE the signature, because it is PART of the signature.
                    // AllocTelemetry.Signature describes the plan's own lanes, and the wish lane is not
                    // one of them — so a tick where only the wish share moved would compare equal, the
                    // node would be withheld as "nothing moved", and the page would keep repainting a
                    // stale wish bar from its cache. That is the same class of bug as the per-pool merge
                    // this board already carries a comment about.
                    var wishShare = key == "energy" ? WishShareView.Energy
                                  : key == "magic" ? WishShareView.Magic
                                  : default(WishShareView.Share);

                    string sig;
                    try { sig = AllocTelemetry.Signature(plan, null); } catch { sig = null; }
                    if (sig != null && wishShare.Recorded)
                        sig = sig + "|w" + wishShare.Taken.ToString(CultureInfo.InvariantCulture)
                                  + "/" + wishShare.Offered.ToString(CultureInfo.InvariantCulture);
                    string prev;
                    _poolSig.TryGetValue(key, out prev);
                    if (prev != null && prev == sig) return;   // nothing moved — do not pay for it
                    _poolSig[key] = sig;
                    anyChanged = true;

                    var p = new JSONObject();
                    p["pool"] = Num(plan.Pool);
                    p["unallocated"] = Num(plan.Unallocated);
                    if (!string.IsNullOrEmpty(plan.UnallocatedReason)) p["idleWhy"] = plan.UnallocatedReason;
                    p["sinkSeated"] = plan.SinkSeated;
                    if (!string.IsNullOrEmpty(plan.SinkRefusalReason)) p["sinkRefused"] = plan.SinkRefusalReason;
                    if (!string.IsNullOrEmpty(plan.BudgetMessage)) p["budget"] = plan.BudgetMessage;

                    var lanes = new JSONArray();
                    for (int i = 0; i < plan.Lanes.Length; i++)
                    {
                        var d = plan.Lanes[i];
                        var o = new JSONObject();
                        o["name"] = d.Label ?? d.Name ?? "?";
                        o["seated"] = d.Seated;
                        o["sink"] = i == plan.SinkIndex;
                        o["took"] = Num(d.Allocation);
                        o["offered"] = Num(d.Offered);
                        // ⚠ NOT `!d.Seated &&` ANY MORE. A SEATED lane carries a Reason in exactly the
                        // cases the operator most needs one — a rate lane the fill skipped for pool
                        // shortness on E/M, and on R3 a hack holding a real share of the pool at a
                        // progress-per-tick below the 2^-25 float stall floor, which converts nothing
                        // ever (HackMath.StallFloor). Both rendered as a healthy coloured segment with
                        // the sentence explaining them dropped on the floor at this line.
                        if (!string.IsNullOrEmpty(d.Reason)) o["why"] = d.Reason;
                        lanes.Add(o);
                    }
                    // THE WISH LANE ON ENERGY AND MAGIC. Appended here, in the snapshot, rather than in
                    // the plan: WishManager runs AFTER the swap that produced this plan, so the layer
                    // cannot know the take — it only knows what it left on the table. Appending at
                    // publish time reads both at the freshest instant either exists, which is the same
                    // thing BuildR3Plan does for R3.
                    //
                    // Consequence stated rather than hidden: the plan's residue and the wish take are
                    // measured one step apart, so `unallocated` is reduced by the take and floored at
                    // zero instead of being asserted to close exactly. A board that invented precision
                    // here would be lying in a subtler way than the one this fixes.
                    if (wishShare.Recorded)
                    {
                        var w = new JSONObject();
                        w["name"] = "Wishes";
                        w["seated"] = true;
                        w["sink"] = false;
                        w["took"] = Num(wishShare.Taken);
                        w["offered"] = Num(wishShare.Offered);
                        w["why"] = WishShareView.LaneWhy(wishShare.Taken > 0);
                        lanes.Add(w);

                        // WIDEN THE DENOMINATOR — do not scale the bar.
                        //
                        // plan.Pool was measured during the swap, while the wish slots still HELD last
                        // tick's resources, so it excludes them. The take is a share of the pool AFTER
                        // those holdings were released. Drawing one against the other reported ~300% on
                        // an end-game save (lanes working from ~1e16, wish slots sitting on ~8e18) —
                        // observed live, and the reason this block exists.
                        //
                        // The honest pool is everything the lanes committed plus everything that was
                        // idle when the wish pass ran, so lanes + wishes + idle closes. Every existing
                        // lane's share shrinks accordingly, and that is the POINT: on this save the NGU
                        // lanes really are a sliver beside the wish holdings, and the old denominator
                        // was hiding it.
                        long laneAllocated = plan.Pool - plan.Unallocated;
                        p["pool"] = Num(wishShare.BoardPool(laneAllocated, plan.Pool));
                        p["unallocated"] = Num(wishShare.Untaken);
                    }

                    p["lanes"] = lanes;
                    pools[key] = p;
                };

                emit("energy", ConstraintLayerBridge.LastEnergyPlan);
                emit("magic", ConstraintLayerBridge.LastMagicPlan);
                // THE THIRD POOL, through the identical contract. R3 is deliberately not routed
                // through the constraint layer (ConstraintLayerBridge.cs:19-23), so there is no
                // LastR3Plan to hand over — R3PoolView composes one from what the R3 allocator
                // records plus the live holdings. `emit` cannot tell the difference, which is the
                // point: if R3 is ever routed for real, only the argument to this line changes.
                emit("r3", BuildR3Plan());
                if (anyChanged || _poolsEverSent == false)
                {
                    _poolsEverSent = true;
                    root["pools"] = pools;
                }
                else
                {
                    // Liveness only. The page keeps the lanes it already has and advances its "as of".
                    var tick = new JSONObject();
                    tick["seq"] = _seq;
                    root["pools"] = tick;
                }
            });

            // WHAT THE ADVISOR HAS SET ON YOUR BEHALF. Small by construction — WriteLedger keeps one row
            // per FIELD, not per assignment, so a writer re-asserting the same value every tick costs one
            // comparison and contributes one row. No cache and no _seq residue needed at this size.
            Safe("ledger", () =>
            {
                var lg = new JSONObject();
                lg["declared"] = WriteLedger.DeclaredCount;
                lg["live"] = WriteLedger.LiveCount;
                lg["pending"] = WriteLedger.DeclaredCount - WriteLedger.LiveCount;

                var rows = new JSONArray();
                foreach (var e in WriteLedger.Snapshot())
                {
                    var spec = WriteLedger.Spec(e.WriterId);
                    if (spec == null) continue;
                    var o = new JSONObject();
                    o["id"] = e.WriterId;
                    o["system"] = spec.System;
                    o["game"] = spec.Game;      // what the operator sees in the game — the row's headline
                    o["field"] = spec.Field;    // where it lives in the save — detail pane only
                    o["rule"] = spec.Rule;
                    o["authority"] = spec.Authority;
                    o["value"] = e.Value ?? "";
                    o["why"] = e.Why ?? "";
                    o["segment"] = e.Segment ?? "";
                    o["state"] = WriteLedger.StateName(e.State);
                    o["t"] = e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    var chain = new JSONArray();
                    if (e.Chain != null) foreach (var step in e.Chain) chain.Add(step);
                    o["chain"] = chain;
                    rows.Add(o);
                }
                lg["rows"] = rows;
                root["ledger"] = lg;
            });

            // WHICH SECTIONS OF THE LOADED PROFILE ARE ACTUALLY DRIVING THE GAME. The profiles node
            // below carries the file list and the active name; this carries whether any of it is
            // RUNNING. Cheap by construction — ProfileSections.Evaluate is pure boolean logic over
            // settings already in memory, no disk, no reflection, no Character read — so it needs no
            // cache and no _seq residue, unlike its neighbours.
            Safe("sections", () =>
            {
                var st = Main.Settings; if (st == null) return;
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
                    NguTrackOverrideActive = LevelPlanner.NguTrackOwned,
                    // The challenge rotation's three facts. Each is guarded on its own: a throw in any
                    // one of them must not cost the whole sections node, and FALSE is the safe answer
                    // for all three — it reads as "cannot run", never as "running fine".
                    RebirthArmed = SafeRebirthArmed(),
                    ProfileHasChallenges = SafeChallengeCount() > 0,
                    AdvisorChallenges = st.AdvisorChallenges
                };
                var arr = new JSONArray();
                foreach (var v in ProfileSections.Evaluate(inputs))
                {
                    var o = new JSONObject();
                    o["key"] = v.Key;
                    o["label"] = v.Label;
                    o["driver"] = ProfileSections.DriverName(v.Driver);
                    o["why"] = v.Reason;
                    arr.Add(o);
                }
                root["sections"] = arr;
            });

            Safe("profiles", () =>
            {
                if (_profilesCache == null || (_seq % 5) == 1)   // refresh ~every 5th snapshot
                {
                    try
                    {
                        var dir = Main.GetProfilesDir();
                        if (dir != null && Directory.Exists(dir))
                        {
                            var files = Directory.GetFiles(dir, "*.json");
                            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                            var names = new string[files.Length];
                            for (int i = 0; i < files.Length; i++) names[i] = Path.GetFileNameWithoutExtension(files[i]);
                            _profilesCache = names;
                        }
                    }
                    catch { }
                }
                var p = new JSONObject();
                var list = new JSONArray();
                if (_profilesCache != null) foreach (var n in _profilesCache) list.Add(n);
                p["list"] = list;
                try { var d = Main.GetProfilesDir(); if (d != null) p["dir"] = d; } catch { }  // companion reads/writes here
                if (settings != null)
                {
                    p["active"] = settings.AllocationFile ?? "default";
                    p["autoProfile"] = settings.AutoProfile;
                    p["launchCompanion"] = settings.LaunchCompanion;
                }
                root["profiles"] = p;
            });

            // --- campaign: the guide's nine-block CBlock spine joined against live state (CampaignTables). ---
            // COST + CADENCE. This is the only snapshot node that PARSES profile JSON, so it is throttled
            // twice over. The node object is rebuilt on the ~5 s cadence (_seq % 5 == 2 — deliberately a
            // different residue from the _profilesCache / _transformMetaCache / _titanAkCache refreshes at
            // == 1, so the disk work is spread across ticks instead of piling onto one). Inside that, the
            // profile FILES are re-parsed only when a (path, mtime, length) signature changes, the same
            // policy as _itemNamesSig. Steady state per tick is therefore one dictionary write; steady state
            // per 5 s is ~24 File.GetAttributes-class stats and no parsing at all.
            Safe("campaign", () =>
            {
                if (_campaignCache == null || (_seq % 5) == 2)
                    _campaignCache = BuildCampaign(settings, liveDifficulty);
                if (_campaignCache != null) root["campaign"] = _campaignCache;
            });

            // --- lsc: the Laser Sword verdict, published WHATEVER IT SAYS. ---
            //
            // WHY THIS EXISTS. LscAdvisor.Compute() is already called every snapshot by
            // OptimizationAdvisor.Analyze (the `actions` node), but that caller keeps the answer only when it
            // is YES:
            //     var lsc = LscAdvisor.Compute();
            //     if (lsc.Known && lsc.Recommended) list.Add(...);
            // A negative verdict — computed, correct, and carrying its own copy ("LSC needs ~Nm for lv T —
            // not yet an Augs-hour freebie") — is thrown away. So a player sitting at 0/20 LSC sees nothing,
            // which is indistinguishable from "the advisor never looked". Same silent-failure class as a
            // frozen dashboard: the software knows the answer and declines to say it.
            //
            // NOT GATED ON AdvisorChallenges, deliberately. The verdict is information, not automation, and
            // its whole value is telling a user who has the automation OFF whether turning it on would buy
            // anything. `enabled` reports the switch alongside so the UI can say both things at once.
            //
            // NOT A REPLACEMENT for the recommendation card — OptimizationAdvisor's positive-only behaviour is
            // correct AS A RECOMMENDATION. This is a parallel readout.
            //
            // COST / CADENCE: per snapshot, uncached here. Compute() self-caches the expensive estimate for
            // 120 s, and every gate in front of that cache is a plain field read (Character.challenges
            // booleans; ChallengeDetector.Current short-circuits on cc.inChallenge) — no reflection. Since
            // Analyze() already calls it on the same tick, this second call is a cache hit. It is published
            // per tick rather than on the ~5 s campaign cadence because `known` flips the instant a challenge
            // starts, and a stale "yes" would invite a click into a challenge that is already running.
            //
            // The verdict is correct per difficulty by construction: LaserSwordChallengeController's
            // currentCompletions() and laserSwordTarget() both dispatch on settings.rebirthDifficulty
            // (target = that difficulty's completions + 2), and LscAdvisor reads both through the controller.
            Safe("lsc", () =>
            {
                var o = new JSONObject();
                var v = LscAdvisor.Compute();
                o["known"] = v.Known;
                o["difficulty"] = liveDifficulty ?? "";   // the counter the verdict was computed against
                o["enabled"] = settings != null && settings.AdvisorChallenges;
                try
                {
                    var ctl = c?.allChallenges?.laserSwordChallenge;
                    if (ctl != null) { o["cur"] = ctl.currentCompletions(); o["max"] = ctl.maxCompletions; }
                }
                catch { }
                if (v.Known)
                {
                    o["recommended"] = v.Recommended;
                    o["target"] = v.Target;                                    // laser sword level to reach
                    o["estMinutes"] = Num(Math.Round(v.EstMinutes, 0));
                    o["text"] = v.Text ?? "";
                }
                else
                {
                    // Compute() returns Known=false from several distinct early-outs and keeps no reason.
                    // Re-deriving the cheap ones (all plain field reads) turns a bare `known:false` into
                    // something the user can act on. Order matches Compute()'s own gate order.
                    string why = "no aug rate available yet — the estimate needs energy power on the sword";
                    try
                    {
                        if (c == null) why = "game not attached";
                        else if (!c.challenges.laserSwordChallengeUnlocked)
                            why = "not unlocked yet — make a 1/1 Laser Sword first";
                        else if (ChallengeDetector.Current() != null)
                            why = "a challenge is already running";
                        else
                        {
                            var ctl = c.allChallenges?.laserSwordChallenge;
                            if (ctl != null && ctl.maxCompletions > 0 && ctl.currentCompletions() >= ctl.maxCompletions)
                                why = "all " + ctl.maxCompletions + " completions are done on this difficulty";
                        }
                    }
                    catch { }
                    o["why"] = why;
                    o["text"] = "Laser Sword: " + why;
                }
                root["lsc"] = o;
            });

            // --- nav: pending in-game view request (F9), emitted only while fresh ---
            Safe("nav", () =>
            {
                if (_navView == null || _seq > _navUntilSeq) return;
                var n = new JSONObject();
                n["view"] = _navView;
                n["seq"] = (double)_navSeq;
                root["nav"] = n;
            });

            return root.ToString();   // compact — no newlines, so line-framing stays intact
        }

        // ------------------------------------------------------------- helpers

        private JSONObject GrowthNode(string key, Func<GrowthTracker.Sample, double> sel)
        {
            double perHr;
            bool ok = false;
            try { ok = GrowthTracker.Rate(sel, 60, false, out perHr); }
            catch { perHr = 0; }
            if (!ok) perHr = 0;
            perHr = Num(perHr);

            Queue<double> ring;
            if (!_spark.TryGetValue(key, out ring)) { ring = new Queue<double>(SparkPoints); _spark[key] = ring; }
            ring.Enqueue(perHr);
            while (ring.Count > SparkPoints) ring.Dequeue();

            var o = new JSONObject();
            o["k"] = key;
            o["rate"] = perHr;
            o["fmt"] = FormatBig(perHr);
            o["ready"] = ok;
            var spark = new JSONArray();
            foreach (var v in ring) spark.Add(v);
            o["spark"] = spark;
            return o;
        }

        private void PumpFeed()
        {
            ActivityItem a;
            try { a = Activity.Current; } catch { return; }
            if (a == null) return;
            try { if (Activity.Expired(a, DateTime.UtcNow)) return; } catch { }
            if (a.Seq == _lastActivitySeq) return;
            _lastActivitySeq = a.Seq;

            var o = new JSONObject();
            o["t"] = a.ReportedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            o["who"] = WhoOf(a.Kind);
            o["msg"] = a.Message ?? "";
            if (!string.IsNullOrEmpty(a.Detail)) o["detail"] = a.Detail;
            _feed.AddLast(o);
            while (_feed.Count > FeedMax) _feed.RemoveFirst();
        }

        private static string WhoOf(ActivityKind k)
        {
            switch (k)
            {
                case ActivityKind.Completed: return "ACTION";
                case ActivityKind.Queued: return "QUEUED";
                case ActivityKind.Warning: return "WARN";
                case ActivityKind.Failure: return "FAIL";
                default: return "INFO";
            }
        }

        // Per-titan autokill readiness, 12 entries (see the `titanAk` node for why 12 and not 14). Called on
        // the ~5 s cache cadence, never per tick.
        //
        // DEGRADE, NEVER VANISH: every one of the 12 slots is emitted on every rebuild, so the chip row keeps
        // a stable shape and the UI can key on a fixed index. Whatever can't be read is simply not claimed —
        // a titan whose version or gate read throws reports state "unknown" and omits the percentages rather
        // than falling back to "short", which would be an invented claim about the player's stats. Publishing
        // the thresholds is still safe there: they come from the static table, which cannot fail.
        //
        // `respawnSec` is NOT built here — it is stamped onto the finished array every tick by
        // TitanTables.StampRespawn, because a countdown cannot ride a 5 s cache. That is also why it survives
        // the `continue`s below untouched: the overlay walks entries by their `i`, not by how far this builder
        // got through them, so an entry that degraded to "unknown" still carries its clock.
        private static JSONArray BuildTitanAk()
        {
            var arr = new JSONArray();

            // One stat read for all 12 titans (these are plain method calls, not reflection).
            double atk = 0, def = 0, regen = 0;
            bool statsKnown = false;
            try
            {
                var ch = Main.Character;
                if (ch != null)
                {
                    atk = ch.totalAdvAttack();
                    def = ch.totalAdvDefense();
                    regen = ch.totalAdvHPRegen();
                    statsKnown = true;
                }
            }
            catch { }

            int count = Math.Min(TitanTables.Ak.Length, TitanTables.Abbrev.Length);   // 12: Ak stops at Amalg
            for (int i = 0; i < count; i++)
            {
                var o = new JSONObject();
                o["i"] = i;
                o["ab"] = TitanTables.Abbrev[i] ?? ("T" + (i + 1).ToString(CultureInfo.InvariantCulture));
                // How many versions this titan HAS, so `v` reads as "v3 of 4" instead of a bare number and the
                // UI can show whether there is harder content above the one being autokilled. Pure table
                // lookup — no game read, and it cannot fail — so unlike `v` it is emitted BEFORE the version
                // read and therefore survives into the "unknown" branches below. vmax == 1 means unversioned.
                o["vmax"] = TitanTables.VersionCount(i);

                // The unlock gate, and — for Walderp only — why his clock may be standing still. BOTH are
                // emitted here, ahead of the version and gate reads, for the same reason as `vmax`: they must
                // survive every `continue` below. A locked titan is still locked when its version read
                // throws, and a frozen Walderp clock still needs its explanation attached to the chip that
                // shows it. Neither read can throw into the build (both are wrapped and degrade to null).
                var unlocked = TitanTables.UnlockState(i, TitanUnlockFlag(i));
                if (unlocked.HasValue) o[TitanTables.KeyUnlocked] = unlocked.Value;

                if (i == TitanTables.WaldoTitanIndex)
                {
                    var waldo = BuildWaldo();
                    if (waldo != null) o[TitanTables.KeyWaldo] = waldo;
                }

                // Version in play. Unversioned titans (0-4) short-circuit to 1 inside TitanVersion; 5-11
                // reflect the save field. A throw here means we don't know WHICH requirement row applies, so
                // there is nothing honest to publish beyond the identity of the chip.
                int version;
                try { version = ZoneHelpers.TitanVersion(i); }
                catch { o["state"] = TitanTables.StateUnknown; arr.Add(o); continue; }

                var req = TitanTables.AkRow(i, version);
                o["v"] = version;
                if (req == null) { o["state"] = TitanTables.StateUnknown; arr.Add(o); continue; }

                // Raw thresholds always ship: static data, and the UI wants the absolute numbers next to the
                // bars. reqRegen == 0 is the "no regen gate" sentinel (T1-T3) — see the percentage below.
                o["reqAtk"] = Exact(req[0]);
                o["reqDef"] = Exact(req[1]);
                o["reqRegen"] = Exact(req[2]);

                bool ak;
                try { ak = ZoneHelpers.AutokillAvailable(i, version); }
                catch { o["state"] = TitanTables.StateUnknown; arr.Add(o); continue; }

                if (!statsKnown)
                {
                    // The gate is readable but the stats aren't: report it and stop. "ready" is still a fact
                    // (the game said so); anything else would need stats we don't have.
                    o["state"] = ak ? TitanTables.StateReady : TitanTables.StateUnknown;
                    arr.Add(o);
                    continue;
                }

                o["atk"] = TitanTables.StatPct(atk, req[0]);
                o["def"] = TitanTables.StatPct(def, req[1]);
                // ONLY published when a regen gate exists. An absent `regen` key means "this titan has no
                // regen requirement" — distinct from `regen: 0`, which would mean "0% of the way to one".
                if (req[2] > 0) o["regen"] = TitanTables.StatPct(regen, req[2]);
                o["state"] = TitanTables.AkState(atk, def, regen, req, ak);
                arr.Add(o);
            }
            return arr;
        }

        // `character.adventure.titan{N}Unlocked` for the four titans that have one (indices 5-8), or null for
        // everything else INCLUDING a failed read — TitanTables.UnlockState is what turns "this titan has no
        // such gate" into the vacuous true, so this method only ever reports what it actually read.
        //
        // DIRECT FIELD ACCESS, NOT REFLECTION. The four names are known at compile time, so this is four
        // branches and one field load — no name lookup, no invoke, nothing added to the reflection budget the
        // `titanAk` header accounts for. AdvisorApply reads the same three fields the same way.
        private static bool? TitanUnlockFlag(int titanIndex)
        {
            if (!TitanTables.HasUnlockFlag(titanIndex)) return null;
            try
            {
                var ch = Main.Character;
                if (ch == null) return null;
                var adv = ch.adventure;
                if (adv == null) return null;
                switch (titanIndex)
                {
                    case 5: return adv.titan6Unlocked;
                    case 6: return adv.titan7Unlocked;
                    case 7: return adv.titan8Unlocked;
                    case 8: return adv.titan9Unlocked;
                    default: return null;
                }
            }
            catch { return null; }
        }

        // Walderp's hide-and-seek state (see TitanTables' `waldo` header for the mechanic). Null when even
        // the save fields can't be read, so the key is omitted rather than claiming "not hiding".
        //
        // NO SCENE LOOKUP NEEDED. WaldoSaysUnlocker is a MonoBehaviour, but the game hands it to us on a
        // plate: `Character.waldoUnlocker` (Character.cs:128) is a direct reference, so this is two field
        // loads, not a FindObjectOfType sweep — which is why it can ride the ordinary ~5 s row rebuild with
        // no cache of its own. 5 s staleness is nothing against a 180 s relocation cycle.
        //
        // TWO INDEPENDENT READS, ON PURPOSE. defeats/finds come from the SAVE (Character.adventure) and are
        // what `hiding` is decided by; the menu comes from the SCENE component and is decoration. A null
        // waldoUnlocker (scene not built yet, or a game update that drops the field) must cost us the
        // location, not the explanation.
        private static JSONObject BuildWaldo()
        {
            int? defeats, finds;
            try
            {
                var ch = Main.Character;
                if (ch == null) return null;
                var adv = ch.adventure;
                if (adv == null) return null;
                defeats = adv.waldoDefeats;
                finds = adv.waldoFinds;
            }
            catch { return null; }

            int? menuId = null;
            string menuName = null;
            try
            {
                var u = Main.Character.waldoUnlocker;
                // currentMenu is -1 both before his first relocation and briefly after each fade-out; that is
                // "hiding, location unknown", which WaldoNode publishes by omitting these two keys.
                if (u != null && u.currentMenu >= 0)
                {
                    menuId = u.currentMenu;
                    var go = u.menu;                 // menuSwapper.allMenus[currentMenu]
                    if (go != null) menuName = go.name;
                }
            }
            catch { }

            return TitanTables.WaldoNode(defeats, finds, menuId, menuName);
        }

        // ------------------------------------------------------------- campaign (CBlock spine)

        // Resolve a campaign profile name to a LOADABLE file. Flat directory only, because that is the only
        // place the runtime looks: the profile list is Directory.GetFiles(dir, "*.json") with no
        // AllDirectories, and CustomAllocation opens Path.Combine(profilesDir, name + ".json").
        //
        // The sample tree ships as {Normal,Evil,Sadistic}\*.json and the readme tells the player to copy it
        // in, so folder-preserved copies exist in the wild. This USED to fall back to them, which was wrong
        // in both directions: it reported a supplier the advisor can never load -- inventing a duplicate
        // against the flat file that genuinely supplies those ordinals (a real save showed 26 of them from
        // one shadowed CBlock2.json) -- and for a block whose only copy is nested it would have reported the
        // chain healthy while nothing loadable supplied it. A nested copy is now reported by
        // NestedCampaignFolder as a missing file WITH its location, which is both true and actionable.
        private static string ResolveCampaignFile(string dir, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
                var flat = Path.Combine(dir, name + ".json");
                if (File.Exists(flat)) return flat;
            }
            catch { }
            return null;
        }

        // The difficulty folder holding an unloadable copy, or null. Only consulted when the flat probe
        // missed, so the common path is still one File.Exists per profile.
        private static string NestedCampaignFolder(string dir, string name, string difficulty)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(difficulty))
                    return null;
                if (File.Exists(Path.Combine(Path.Combine(dir, difficulty), name + ".json"))) return difficulty;
            }
            catch { }
            return null;
        }

        // Read every profile the campaign names. Re-parses only when a (path, mtime, length) signature
        // changes; otherwise hands back the cached list. Called on the ~5 s cadence only.
        private List<CampaignTables.Supply> CampaignSupply()
        {
            var dir = Main.GetProfilesDir();
            var refs = CampaignTables.AllProfiles(true);   // legacy included: they are REPORTED, never counted

            // Cheap signature pass first — stat only, no reads.
            var sig = new StringBuilder();
            var paths = new string[refs.Count];
            var nested = new string[refs.Count];
            for (int i = 0; i < refs.Count; i++)
            {
                var path = ResolveCampaignFile(dir, refs[i].Name);
                paths[i] = path;
                // Only probe the difficulty folder when the loadable one is absent, and fold the answer into
                // the signature so moving a file up a level re-parses instead of serving a stale verdict.
                nested[i] = path == null ? NestedCampaignFolder(dir, refs[i].Name, refs[i].Difficulty) : null;
                sig.Append(refs[i].Name).Append('|').Append(nested[i] ?? "").Append('|');
                if (path != null)
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        sig.Append(fi.LastWriteTimeUtc.Ticks).Append('|').Append(fi.Length);
                    }
                    catch { sig.Append('?'); }
                }
                sig.Append(';');
            }
            var s = sig.ToString();
            if (_campaignSupplyCache != null && s == _campaignFileSig) return _campaignSupplyCache;

            var list = new List<CampaignTables.Supply>(refs.Count);
            for (int i = 0; i < refs.Count; i++)
            {
                var r = refs[i];
                // File IO here; the parse + malformed classification lives in CampaignTables.ReadSupply so it
                // can be unit-tested. A file that exists but cannot be READ is reported as malformed, not
                // missing — the campaign knows it is there.
                string text = null;
                bool readFailed = false;
                if (paths[i] != null)
                {
                    try { text = File.ReadAllText(paths[i]); }
                    catch { readFailed = true; }
                }
                var sup = CampaignTables.ReadSupply(r.Name, r.Difficulty, r.BlockId, r.IsLegacy,
                                                    paths[i] == null ? null : (text ?? ""));
                sup.NestedIn = nested[i];
                if (readFailed) sup.Malformed = CampaignTables.MalformedParse;
                list.Add(sup);
            }

            _campaignSupplyCache = list;
            _campaignFileSig = s;
            return list;
        }

        // The whole `campaign` node. Rebuilt on the ~5 s cadence; the caller republishes the same instance in
        // between. THIS METHOD ONLY DOES THE LIVE READS — the join and the JSON emission are
        // CampaignTables.ToJson, so the exact wire shape the companion consumes is unit-tested (nothing in
        // UiBridge can be, it pulls in Unity and Assembly-CSharp).
        private JSONObject BuildCampaign(SavedSettings settings, string liveDifficulty)
        {
            List<CampaignTables.Supply> supply;
            try { supply = CampaignSupply(); }
            catch (Exception e) { Main.LogDebug("UiBridge campaign supply: " + e.Message); return _campaignCache; }

            // Completion counters for the CURRENT difficulty only — the game exposes no others, which is why
            // every block on another difficulty reports counted=false rather than a number.
            Dictionary<string, int> live = null;
            try
            {
                var block = ChallengeOverlay.Block();
                if (block != null && block.Count > 0)
                {
                    live = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in block) live[e.Code] = e.Cur;
                }
            }
            catch { }

            return CampaignTables.ToJson(
                supply,
                liveDifficulty,
                live,
                settings == null ? null : settings.AllocationFile,
                settings != null && settings.AutoProfile,
                settings != null && settings.AdvisorChallenges);
        }

        // THE LIVE HALF OF THE R3 BOARD. R3PoolView owns every decision and every sentence; this
        // method only reads, and it reads all three summands — what the hacks hold, what the wishes
        // hold, what is idle — at ONE instant, so `hacks + wishes + idle` closes exactly against the
        // pool the board draws. Mixing a recorded allocation with a live idle cannot close: the R3 bar
        // regenerates into idle continuously ([DECOMP] Resource3Display.cs:62-74).
        //
        // progressPerTick is asked only of hacks that actually hold R3. It is a live multiplier-stack
        // read on the Unity main thread at 1 Hz, and for a hack holding nothing the answer changes
        // nothing on the board — on a save whose hacks are all hard-capped and empty (the state that
        // silences the R3 lane entirely) that is fifteen calls avoided per second.
        private ConstraintLayer.Plan BuildR3Plan()
        {
            try
            {
                var c = Main.Character;
                if (c == null || c.res3 == null || !c.res3.res3On) return null;

                var holdings = new List<R3PoolView.HackHolding>(15);
                var hc = c.hacksController;
                if (hc != null && c.hacks != null && c.hacks.hacks != null)
                {
                    int n = Math.Min(c.hacks.hacks.Count, 15);
                    for (int id = 0; id < n; id++)
                    {
                        var h = c.hacks.hacks[id];
                        if (h == null) continue;
                        holdings.Add(new R3PoolView.HackHolding
                        {
                            Id = id,
                            Held = h.res3,
                            ProgressPerTick = h.res3 > 0 ? hc.progressPerTick(id) : 0,
                        });
                    }
                }

                long wishHeld = 0;
                var wishes = c.wishes != null ? c.wishes.wishes : null;
                if (wishes != null)
                    for (int i = 0; i < wishes.Count; i++)
                        if (wishes[i] != null && wishes[i].res3 > 0) wishHeld += wishes[i].res3;

                var s = Main.Settings;
                return R3PoolView.Compose(new R3PoolView.Inputs
                {
                    Swap = R3PoolView.LastSwap,
                    Wish = R3PoolView.LastWishShare,
                    Hacks = holdings,
                    WishHeld = wishHeld,
                    Idle = c.res3.idleRes3,
                    R3Managed = s != null && s.ManageR3,
                    WishesManaged = s != null && s.ManageWishes,
                });
            }
            catch (Exception e)
            {
                Main.LogDebug("UiBridge R3 pool: " + e.Message);
                return null;
            }
        }

        private void Safe(string name, Action read)
        {
            try { read(); }
            catch (Exception e) { try { Main.LogDebug("UiBridge read '" + name + "': " + e.Message); } catch { } }
        }

        // The two profile-side facts the Challenges verdict needs. Separately guarded rather than folded
        // into the sections node's own Safe(): Main.Profile is null before the first load and
        // RebirthIsArmed walks the parsed rebirth array, so a fault in either would otherwise drop the
        // whole sections readout — which is the one part of the snapshot whose job is to say what is NOT
        // running. False is the fail-safe on both: it renders as "cannot run", never as a false all-clear.
        private static bool SafeRebirthArmed()
        {
            try { return Main.Profile != null && Main.Profile.RebirthIsArmed(); } catch { return false; }
        }

        private static int SafeChallengeCount()
        {
            try { return AllocationProfiles.RebirthStuff.BaseRebirth.ChallengeCount; } catch { return 0; }
        }

        private static double Pct(double cur, double cap)
        {
            if (cap <= 0) return 0;
            return ClampPct(cur / cap * 100.0);
        }

        private static double ClampPct(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p)) return 0;
            if (p < 0) return 0;
            if (p > 100) return 100;
            return Math.Round(p, 0);
        }

        // NaN/Infinity serialize to the literal tokens "NaN"/"Infinity" (invalid JSON) and would break the
        // whole snapshot line, so every raw double added to the DOM passes through here first.
        private static double Num(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v)) ? 0 : v;
        }

        // Same guarantee as Num(), but WITHOUT SimpleJson's "G17" serialization. JSONNumber writes every
        // double it holds as G17, which turns the Ak table's clean constants into float noise —
        // 1e23 ships as "9.9999999999999992E+22": 26 characters, and a threshold the UI would render back to
        // the user in a form the game's own source never uses. JSONNumber's string constructor keeps the
        // literal verbatim (m_RawString), so this emits a real JSON number in round-trip-shortest form
        // ("1E+23"). Only for values whose EXACT decimal shape matters and whose magnitude is large; the
        // per-tick percentages are small integers where G17 is already exact.
        private static JSONNumber Exact(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return new JSONNumber(0);
            // "R" is round-trip-shortest on both net48 and Unity's Mono, and always a valid JSON number
            // token (digits, optional '.', optional 'E+nn').
            return new JSONNumber(v.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string FormatBig(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            double a = Math.Abs(v);
            if (a < 1000) return v.ToString("0.##", CultureInfo.InvariantCulture);
            int exp = (int)Math.Floor(Math.Log10(a));
            double mant = v / Math.Pow(10, exp);
            return mant.ToString("0.0", CultureInfo.InvariantCulture) + "e" + exp;
        }

        // Serialize a SpendPlanner plan (perks/quirks) into the companion's ordered-step array.
        private static JSONArray PlanArr(System.Collections.Generic.List<SpendPlanner.PlanStep> steps)
        {
            var arr = new JSONArray();
            if (steps != null)
                foreach (var s in steps)
                {
                    var o = new JSONObject();
                    o["name"] = s.Name ?? "";
                    o["cur"] = FormatBig(s.CurLevel);
                    o["target"] = s.Target == long.MaxValue ? "max" : s.Target.ToString(CultureInfo.InvariantCulture);
                    o["cost"] = FormatBig(s.Cost);
                    o["chapter"] = s.MinChapter;
                    o["state"] = s.State ?? "";
                    arr.Add(o);
                }
            return arr;
        }

        private static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            int h = (int)ts.TotalHours;
            if (h > 0) return h + "h " + ts.Minutes + "m";
            if (ts.Minutes > 0) return ts.Minutes + "m " + ts.Seconds + "s";
            return ts.Seconds + "s";
        }
    }
}
