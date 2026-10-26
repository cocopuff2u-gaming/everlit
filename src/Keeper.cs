using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Everlit
{
    // Keeps fires lit by switching on the game's own "infinite fuel" for each one, the switch Haldor's,
    // Hildir's and the Bog Witch's fire pits ship with. The game lets a server set a component's fields
    // per object in the object's data ("HasFields", "HasFields<Component>", "<Component>.<field>"), and
    // every player's game applies them when it loads the object (ZNetView.LoadFields). So the fire
    // never burns fuel, in every player's game, with nothing installed on theirs.
    // Fires are found as players' games send them (just built), and by a walk of the whole world at
    // the start, a slice each frame; nothing sweeps after that.
    // A fire is written to once, when it's lit (or put back), and never again after that.
    // A fire a player already has loaded switches when it next loads for them; until then it gets a
    // full load of fuel so it doesn't go out in the meantime.
    // Furnaces (off by default) and hot tubs get the same treatment through the smelter's own "max fuel"
    // (to the game a hot tub is a smelter with nothing to smelt, burning wood to stay on): at 0 the game
    // runs it without fuel, as it does the spinning wheel. They get no fuel top-up: torn down, they drop
    // the coal or wood in them, so a top-up would be free fuel.
    // The eitr refinery (on) and the frost kiln (off) go the same way. Off by default, the frost kiln
    // then makes its goods out of nothing, for as long as it's loaded.
    // Ovens and the frost foundry (off) have no such switch and only cook with fuel in them, so a piece of wood is made to last for
    // good (the oven's own "seconds per fuel" at its highest), and an empty one gets just under one
    // piece: torn down, an oven drops whole pieces only, so that isn't free wood either.
    // Shield generators (off) use up fuel only when hit; at 0 fuel per hit their shield never breaks.
    // Lit fires can also get two of the fire's own switches (both off by default): no going out when
    // smoke can't get away, and players turning them off and on. (Smoke itself can't be switched off
    // from here: the game applies an object's fields only to the parts of it that are active when it
    // loads, and a fire's smoke spawner isn't active then.)
    // WardedOnly (off) keeps it all to what stands inside a player's ward that's switched on. The server
    // has no ward objects running, so wards are kept from their data: found by a walk of the world when
    // it's switched on, then as players' games send them; one changing re-runs a round of known fires.
    internal static class Keeper
    {
        private enum Kind { Campfire, Bonfire, Hearth, Torch, Brazier, Candle, HotTub, Oven, Refinery, Furnace, FrostKiln, FrostFoundry, Shield, Other }
        private enum Via { Fireplace, Smelter, CookingStation, ShieldGenerator }   // the component whose fields are switched

        private sealed class FireType
        {
            public string Name;
            public Kind Kind;
            public float MaxFuel;
            public Via Via;
            public float Normal;   // the prefab's own value of the smelter's, oven's or shield's switched field, to put it back
            public bool CoverOff, CanTurnOff;   // the fire's own smoke and turning-off switches
        }

        private static readonly int HasFields = "HasFields".GetStableHashCode();
        private static readonly int HasFieldsFireplace = ("HasFields" + nameof(Fireplace)).GetStableHashCode();
        private static readonly int InfiniteFuel = (nameof(Fireplace) + "." + nameof(Fireplace.m_infiniteFuel)).GetStableHashCode();
        private static readonly int HasFieldsSmelter = ("HasFields" + nameof(Smelter)).GetStableHashCode();
        private static readonly int SmelterMaxFuel = (nameof(Smelter) + "." + nameof(Smelter.m_maxFuel)).GetStableHashCode();
        private static readonly int HasFieldsCooking = ("HasFields" + nameof(CookingStation)).GetStableHashCode();
        private static readonly int CookingSecPerFuel = (nameof(CookingStation) + "." + nameof(CookingStation.m_secPerFuel)).GetStableHashCode();
        private static readonly int HasFieldsShield = ("HasFields" + nameof(ShieldGenerator)).GetStableHashCode();
        private static readonly int ShieldFuelPerDamage = (nameof(ShieldGenerator) + "." + nameof(ShieldGenerator.m_fuelPerDamage)).GetStableHashCode();
        private static readonly int CoverCheck = (nameof(Fireplace) + "." + nameof(Fireplace.m_disableCoverCheck)).GetStableHashCode();
        private static readonly int TurnOff = (nameof(Fireplace) + "." + nameof(Fireplace.m_canTurnOff)).GetStableHashCode();
        private const int FOREVER = int.MaxValue;       // an oven's seconds per piece of wood: about 68 years
        private const float OVEN_FUEL = 0.99f;          // what an empty oven gets: under one piece, so tearing it down drops none
        private static readonly int Mark = "everlit".GetStableHashCode();   // fires Everlit lit, so it can put them back
        private const int ROUND_BUDGET = 2000;          // objects looked at per frame, at most
        private const double ROUND_MS = 1.0;            // and no more than this much time in a frame
        private const int ROUND_CHANGES = 25;           // fires changed per frame, so lighting a whole world is spread out
        private const float REPORT_MINUTES = 10f;       // between log lines about fires lit as they were built
        private const float WARD_DELAY = 5f;            // after a ward changes, before the fires are looked at again

        private static Dictionary<int, FireType> types;     // every fire that burns fuel
        private static HashSet<int> wanted;                 // the ones the settings keep lit
        private static readonly HashSet<ZDOID> arrived = new HashSet<ZDOID>();
        private static readonly List<ZDOID> work = new List<ZDOID>();
        private static readonly Dictionary<ZDOID, float> fuelSeen = new Dictionary<ZDOID, float>();   // lit fires' fuel when last seen
        private static readonly HashSet<ZDOID> stillBurning = new HashSet<ZDOID>();   // lit, but a player's game still burns it (not reloaded yet)
        private static readonly HashSet<ZDOID> known = new HashSet<ZDOID>();   // every fire seen, lit or not
        private static readonly List<ZDOID> roundList = new List<ZDOID>();
        private static float nextRound, nextReport;
        private static bool running, wholeWorld, forceWorld, firstRound = true;
        private static int liveLit;
        private static int sector, index, slowestFrame, gcs;
        private static bool slowestGc;
        private static bool again;                          // settings changed during a round: one more after it
        private static int rLit, rUnlit, rKept, rWorld, rBurning, rSwitched;
        private static readonly Dictionary<Kind, int> rKinds = new Dictionary<Kind, int>();
        private static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        private static double roundMs, slowestMs;
        private static int roundFrames;

        private struct Ward { public Vector3 Pos; public float Radius; public bool On; }
        private static Dictionary<int, float> wardTypes;    // ward prefabs and their radius
        private static readonly Dictionary<ZDOID, Ward> wards = new Dictionary<ZDOID, Ward>();   // players' wards
        private static readonly HashSet<ZDOID> wardArrived = new HashSet<ZDOID>();
        private static bool warded, wardsKnown, wardRound;  // WardedOnly in effect; wards found; a ward changed
        private static float wardRoundAt;

        private static bool Active => ZNet.instance != null && ZNet.instance.IsServer()
            && ZDOMan.instance != null && ZNetScene.instance != null;

        // every object a player's game sends the server passes through here: catches fires as they're
        // built, so after the first walk of the world nothing needs sweeping
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        private static class Arrived
        {
            private static void Postfix(ZDO __instance)
            {
                if (types == null) return;
                int prefab = __instance.GetPrefab();
                if (types.ContainsKey(prefab)) arrived.Add(__instance.m_uid);
                else if (warded && wardTypes.ContainsKey(prefab)) wardArrived.Add(__instance.m_uid);
            }
        }

        // a ward torn down (by a player's game or the server's)
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        private static class Destroyed
        {
            private static void Prefix(ZDOID uid)
            {
                if (wards.Remove(uid) && wardsKnown) WardChanged();
            }
        }

        public static void SettingsChanged()
        {
            wanted = null;
            if (!running) nextRound = 0f; else again = true;
        }

#if SELFTEST
        public static bool RoundRunning => running || nextRound <= Time.time || wardRound;
        public static void RoundNow() { if (!running) nextRound = 0f; }
        public static void WorldNow() { forceWorld = true; RoundNow(); }
        public static double SlowestMs => slowestMs;
#endif

        public static void Update()
        {
            if (!Active) return;
            if (types == null && !Load()) return;
            if (wanted == null) Want();
            clock.Restart();
            gcs = System.GC.CollectionCount(0);
            if (wardArrived.Count > 0)
            {
                work.Clear(); work.AddRange(wardArrived); wardArrived.Clear();
                foreach (var id in work)
                {
                    var z = ZDOMan.instance.GetZDO(id);
                    if (z != null && wardTypes.TryGetValue(z.GetPrefab(), out float r)) NoteWard(z, r);
                }
            }
            if (wardRound && !running && Time.time >= wardRoundAt) { wardRound = false; nextRound = 0f; }
            if (arrived.Count > 0)
            {
                work.Clear(); work.AddRange(arrived); arrived.Clear();
                foreach (var id in work) if (Look(ZDOMan.instance.GetZDO(id), true)) liveLit++;
            }
            if (Time.time >= nextReport) Report();
            if (!running && Time.time >= nextRound) StartRound();
            if (running) Round();
        }

        private static bool Load()
        {
            if (ZNetScene.instance.m_prefabs == null || ZNetScene.instance.m_prefabs.Count == 0) return false;
            types = new Dictionary<int, FireType>();
            wardTypes = new Dictionary<int, float>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (!go) continue;
                var ward = go.GetComponent<PrivateArea>();
                if (ward != null) { wardTypes[go.name.GetStableHashCode()] = ward.m_radius; continue; }
                var g = go.GetComponent<ShieldGenerator>();
                if (g != null)
                {
                    // uses up fuel only when its shield is hit
                    if (g.m_fuelPerDamage > 0f && g.m_fuelItems.Count > 0)
                        types[go.name.GetStableHashCode()] = new FireType { Name = go.name, Kind = Kind.Shield, Normal = g.m_fuelPerDamage, Via = Via.ShieldGenerator };
                    continue;
                }
                var s = go.GetComponent<Smelter>();
                if (s != null)
                {
                    if (s.m_maxFuel <= 0 || s.m_fuelItem == null) continue;   // needs nothing (spinning wheel, windmill, kiln)
                    string fuel = s.m_fuelItem.gameObject.name;
                    // hot tubs burn fuel just to be on: nothing goes in or comes out. The frost kiln also
                    // takes nothing in, but makes something out of its fuel alone: without fuel that's free
                    Kind? kind = s.m_maxOre == 0 && s.m_conversion.Count == 0 ? Kind.HotTub
                        : fuel == "Coal" ? Kind.Furnace                 // smelters, blast furnaces, modded ones
                        : fuel == "Sap" ? Kind.Refinery                 // the eitr refinery
                        : go.name == "piece_FrostKiln" ? Kind.FrostKiln
                        : (Kind?)null;
                    if (kind.HasValue)
                        types[go.name.GetStableHashCode()] = new FireType { Name = go.name, Kind = kind.Value, Normal = s.m_maxFuel, Via = Via.Smelter };
                    continue;
                }
                var c = go.GetComponent<CookingStation>();
                if (c != null)
                {
                    // ovens, which burn wood, and the frost foundry; not the cooking station, which cooks
                    // over a fire under it
                    if (!c.m_useFuel || c.m_fuelItem == null || c.m_secPerFuel <= 0) continue;
                    Kind? kind = c.m_fuelItem.gameObject.name == "Wood" ? Kind.Oven
                        : go.name == "piece_FrostFoundry" ? Kind.FrostFoundry
                        : (Kind?)null;
                    if (kind.HasValue)
                        types[go.name.GetStableHashCode()] = new FireType { Name = go.name, Kind = kind.Value, Normal = c.m_secPerFuel, Via = Via.CookingStation };
                    continue;
                }
                var f = go.GetComponent<Fireplace>();
                // already infinite (traders' fire pits) or doesn't burn anything: nothing to do
                if (f == null || f.m_infiniteFuel || f.m_secPerFuel <= 0f || f.m_fuelItem == null) continue;
                types[go.name.GetStableHashCode()] = new FireType { Name = go.name, Kind = KindOf(go.name), MaxFuel = f.m_maxFuel,
                    CoverOff = f.m_disableCoverCheck, CanTurnOff = f.m_canTurnOff };
            }
            ZLog.Log($"Everlit: {types.Count} kinds of fire and furnace: " + string.Join(", ", types.Values.OrderBy(t => t.Kind).Select(t => $"{t.Name} ({t.Kind})"))
                + $"; {wardTypes.Count} kinds of ward");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Warm();
            ZLog.Log($"Everlit: ready in {sw.Elapsed.TotalMilliseconds:F1} ms; the world walk starts in a minute");
            nextRound = Time.time + 60f;           // the first walk waits until the server has settled after loading
            return true;
        }

        private static Kind KindOf(string name)
        {
            string n = name.ToLowerInvariant();
            if (n == "fire_pit" || n == "fire_pit_iron") return Kind.Campfire;
            if (n == "bonfire") return Kind.Bonfire;
            if (n == "hearth") return Kind.Hearth;
            if (n.Contains("torch") || n.Contains("jackoturnip") || n.Contains("lantern")) return Kind.Torch;
            if (n.Contains("brazier")) return Kind.Brazier;
            if (n.Contains("candle")) return Kind.Candle;
            return Kind.Other;
        }

        private static void Want()
        {
            warded = Plugin.Enabled.Value && Plugin.WardedOnly.Value;
            if (!warded) { wards.Clear(); wardArrived.Clear(); wardsKnown = wardRound = false; }
            var exclude = new HashSet<string>(Plugin.ExcludeFires.Value.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0));
            wanted = new HashSet<int>();
            if (!Plugin.Enabled.Value) return;
            foreach (var kv in types)
            {
                if (exclude.Contains(kv.Value.Name.ToLowerInvariant())) continue;
                bool on;
                switch (kv.Value.Kind)
                {
                    case Kind.Campfire: on = Plugin.Campfires.Value; break;
                    case Kind.Bonfire: on = Plugin.Bonfires.Value; break;
                    case Kind.Hearth: on = Plugin.Hearths.Value; break;
                    case Kind.Torch: on = Plugin.Torches.Value; break;
                    case Kind.Brazier: on = Plugin.Braziers.Value; break;
                    case Kind.Candle: on = Plugin.Candles.Value; break;
                    case Kind.HotTub: on = Plugin.HotTubs.Value; break;
                    case Kind.Oven: on = Plugin.Ovens.Value; break;
                    case Kind.Refinery: on = Plugin.EitrRefinery.Value; break;
                    case Kind.FrostKiln: on = Plugin.FrostKiln.Value; break;
                    case Kind.FrostFoundry: on = Plugin.FrostFoundry.Value; break;
                    case Kind.Furnace: on = Plugin.Furnaces.Value; break;
                    case Kind.Shield: on = Plugin.ShieldGenerators.Value; break;
                    default: on = Plugin.OtherFires.Value; break;
                }
                if (on) wanted.Add(kv.Key);
            }
        }

        public static bool IsLit(ZDO zdo) => zdo.GetInt(Mark) == 1
            && (zdo.GetBool(InfiniteFuel) || zdo.GetInt(SmelterMaxFuel, -1) == 0 || zdo.GetInt(CookingSecPerFuel) == FOREVER
                || zdo.GetFloat(ShieldFuelPerDamage, -1f) == 0f);

        // returns true when it changed the fire
        private static bool Look(ZDO zdo, bool live)
        {
            if (zdo == null || !zdo.IsValid()) return false;
            int prefab = zdo.GetPrefab();
            if (!types.TryGetValue(prefab, out var type))
            {
                if (warded && wardTypes.TryGetValue(prefab, out float radius)) NoteWard(zdo, radius);
                return false;
            }
            known.Add(zdo.m_uid);
            if (warded && !wardsKnown) return false;     // the wards first; the round after the walk sees to it
            bool built = zdo.GetLong(ZDOVars.s_creator) != 0L;
            bool want = wanted.Contains(prefab) && (built || !Plugin.PlayerBuiltOnly.Value) && (!warded || InWard(zdo.GetPosition()));
            bool marked = zdo.GetInt(Mark) == 1;
            if (want && !IsLit(zdo))
            {
                zdo.Set(HasFields, true);
                zdo.Set(Mark, 1);
                if (type.Via == Via.Smelter)
                {
                    zdo.Set(HasFieldsSmelter, true);
                    zdo.Set(SmelterMaxFuel, 0);
                }
                else if (type.Via == Via.CookingStation)
                {
                    zdo.Set(HasFieldsCooking, true);
                    zdo.Set(CookingSecPerFuel, FOREVER);
                    if (zdo.GetFloat(ZDOVars.s_fuel) < OVEN_FUEL) zdo.Set(ZDOVars.s_fuel, OVEN_FUEL);
                }
                else if (type.Via == Via.ShieldGenerator)
                {
                    zdo.Set(HasFieldsShield, true);
                    zdo.Set(ShieldFuelPerDamage, 0f);
                }
                else
                {
                    zdo.Set(HasFieldsFireplace, true);
                    zdo.Set(InfiniteFuel, true);
                    // the player's game that runs this fire right now only picks the switch up when it
                    // next loads it; a full load keeps it burning until then
                    zdo.Set(ZDOVars.s_fuel, type.MaxFuel);
                }
                Switches(zdo, type, true);
                Win(zdo);
                rLit++;
                rKinds[type.Kind] = rKinds.TryGetValue(type.Kind, out var k) ? k + 1 : 1;
                if (Plugin.LogEachFire.Value) ZLog.Log($"Everlit: lit {type.Name} at {Where(zdo)}{(live ? " (just built)" : "")}");
                return true;
            }
            if (!want && marked)
            {
                // set back rather than removed: a player's copy keeps a value it isn't sent again
                if (type.Via == Via.Smelter) zdo.Set(SmelterMaxFuel, (int)type.Normal);
                else if (type.Via == Via.CookingStation) zdo.Set(CookingSecPerFuel, (int)type.Normal);
                else if (type.Via == Via.ShieldGenerator) zdo.Set(ShieldFuelPerDamage, type.Normal);
                else zdo.Set(InfiniteFuel, false);
                Switches(zdo, type, false);
                zdo.RemoveInt(Mark);
                Win(zdo);
                rUnlit++;
                if (Plugin.LogEachFire.Value) ZLog.Log($"Everlit: put {type.Name} at {Where(zdo)} back to normal");
                return true;
            }
            if (want && Switches(zdo, type, true))
            {
                // already lit, but the smoke or turning-off settings changed
                Win(zdo);
                rSwitched++;
                return true;
            }
            if (want)
            {
                rKept++;
                rKinds[type.Kind] = rKinds.TryGetValue(type.Kind, out var n) ? n + 1 : 1;
                // a player's game that had this fire loaded before it was lit keeps burning it until it
                // loads it again; counted for the log
                float fuel = zdo.GetFloat(ZDOVars.s_fuel);
                if (fuelSeen.TryGetValue(zdo.m_uid, out var was) && fuel < was - 0.0001f) { rBurning++; stillBurning.Add(zdo.m_uid); }
                fuelSeen[zdo.m_uid] = fuel;
            }
            else if (!built) rWorld++;
            return false;
        }

        // A fire's own smoke-check and turning-off switches: what the settings say while it's lit, the
        // fire's own when it's put back. Returns true when it changed one.
        private static bool Switches(ZDO zdo, FireType type, bool lit)
        {
            if (type.Via != Via.Fireplace) return false;
            bool changed = false;
            bool coverOff = (lit && Plugin.NoSmokeChoke.Value) || type.CoverOff;
            if (zdo.GetBool(CoverCheck, type.CoverOff) != coverOff) { zdo.Set(CoverCheck, coverOff); changed = true; }
            bool canTurnOff = (lit && Plugin.PlayersCanTurnOff.Value) || type.CanTurnOff;
            if (zdo.GetBool(TurnOff, type.CanTurnOff) != canTurnOff)
            {
                zdo.Set(TurnOff, canTurnOff);
                // one a player turned off couldn't be turned on again: light it
                if (!canTurnOff && zdo.GetInt(ZDOVars.s_state, 1) != 1) zdo.Set(ZDOVars.s_state, 1);
                changed = true;
            }
            return changed;
        }

        private static void NoteWard(ZDO zdo, float radius)
        {
            if (zdo.GetLong(ZDOVars.s_creator) == 0L) return;     // players' wards only
            var w = new Ward { Pos = zdo.GetPosition(), Radius = radius, On = zdo.GetBool(ZDOVars.s_enabled) };
            if (wards.TryGetValue(zdo.m_uid, out var was) && was.On == w.On && was.Pos == w.Pos) return;
            wards[zdo.m_uid] = w;
            if (wardsKnown) WardChanged();
        }

        private static void WardChanged()
        {
            wardRound = true;
            wardRoundAt = Time.time + WARD_DELAY;
        }

        private static bool InWard(Vector3 p)
        {
            foreach (var w in wards.Values)
                if (w.On && Utils.DistanceXZ(w.Pos, p) < w.Radius) return true;
            return false;
        }

        // The player's game running the fire writes it every 2 s, and only takes an update with a higher
        // revision than its own. A few steps ahead makes sure this one gets taken even if one of its
        // writes is crossing it on the wire. (That write can't undo the change here: an update only
        // sets the values it carries, and it doesn't carry these.)
        private static void Win(ZDO zdo) => zdo.DataRevision += 4;

        // The first round walks the whole world, for fires built before Everlit was installed. After
        // that a round only runs when the settings change, and looks at the fires already found.
        private static void StartRound()
        {
            running = true;
            wholeWorld = firstRound || forceWorld || (warded && !wardsKnown);   // wards are found by a walk
            forceWorld = false;
            sector = 0; index = 0;
            roundList.Clear();
            if (!wholeWorld) roundList.AddRange(known);
            rLit = rUnlit = rKept = rWorld = rBurning = rSwitched = 0; rKinds.Clear();
            roundMs = slowestMs = 0; roundFrames = slowestFrame = 0; slowestGc = false;
        }

        private static void Round()
        {
            int looked = 0, changed = 0, steps = 0;
            bool done;
            if (wholeWorld)
            {
                var sectors = ZDOMan.instance.m_objectsBySector;
                while (sector < sectors.Length && looked < ROUND_BUDGET && changed < ROUND_CHANGES)
                {
                    if ((++steps & 7) == 0 && clock.Elapsed.TotalMilliseconds > ROUND_MS) break;
                    var list = sectors[sector];
                    if (list == null || index >= list.Count) { sector++; index = 0; continue; }
                    if (Look(list[index++], false)) changed++;
                    looked++;
                }
                done = sector >= sectors.Length;
            }
            else
            {
                while (index < roundList.Count && looked < ROUND_BUDGET && changed < ROUND_CHANGES)
                {
                    if ((++steps & 7) == 0 && clock.Elapsed.TotalMilliseconds > ROUND_MS) break;
                    var id = roundList[index++];
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null) { known.Remove(id); fuelSeen.Remove(id); continue; }
                    if (Look(zdo, false)) changed++;
                    looked++;
                }
                done = index >= roundList.Count;
            }
            double ms = clock.Elapsed.TotalMilliseconds;
            roundMs += ms; roundFrames++;
            if (ms > slowestMs) { slowestMs = ms; slowestFrame = roundFrames; slowestGc = System.GC.CollectionCount(0) != gcs; }
            if (!done) return;

            running = false;
            nextRound = again ? 0f : float.MaxValue;
            again = false;
            if (warded && !wardsKnown && wholeWorld)
            {
                // that walk only found the wards and fires; the round right after sees to the fires
                wardsKnown = true;
                nextRound = 0f;
                ZLog.Log($"Everlit: WardedOnly: {wards.Count} wards found, {wards.Values.Count(w => w.On)} switched on; walk took {roundMs:F1} ms over {roundFrames} frames");
                return;
            }
            if (firstRound || rLit > 0 || rUnlit > 0 || rBurning > 0 || rSwitched > 0)
            {
                string kinds = rKinds.Count == 0 ? "" : " (" + string.Join(", ", rKinds.OrderBy(k => k.Key).Select(k => $"{k.Value} {KindName(k.Key)}")) + ")";
                ZLog.Log($"Everlit: {rKept + rLit} fires kept lit{kinds}; {rLit} newly lit, {rUnlit} put back to normal, " + (rSwitched > 0 ? $"{rSwitched} lit fires' switches changed, " : "") +
                    (rBurning > 0 ? $"{rBurning} still using fuel in a player's game (until it next loads there), " : "") +
                    $"{rWorld} of the world's own left alone; {(wholeWorld ? "whole world" : "known fires")} took {roundMs:F1} ms over {roundFrames} frames, slowest frame {slowestMs:F2} ms (#{slowestFrame}{(slowestGc ? ", a garbage collection ran in it" : "")})");
            }
            firstRound = false;
        }

        // a line now and then, only when something happened
        private static void Report()
        {
            nextReport = Time.time + REPORT_MINUTES * 60f;
            if (liveLit > 0 || stillBurning.Count > 0)
                ZLog.Log($"Everlit: last {REPORT_MINUTES:F0} min: {liveLit} fires lit as they were built" +
                    (stillBurning.Count > 0 ? $", {stillBurning.Count} lit fires still used fuel in a player's game (they stop once it next loads there)" : ""));
            liveLit = 0;
            stillBurning.Clear();
        }

        // Compile what a round runs while the world is still loading (a long frame anyway), so the
        // first round's first frame doesn't pay for it later. (Compiling on another thread doesn't
        // carry over to the game's thread here.)
        private static void Warm()
        {
            try
            {
                const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var m in typeof(Keeper).GetMethods(all))
                    if (!m.IsAbstract && !m.ContainsGenericParameters) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
                // the game's own reads and writes a fire goes through
                foreach (var m in typeof(ZDO).GetMethods(all))
                    if ((m.Name.StartsWith("Get") || m.Name.StartsWith("Set") || m.Name == "IsValid") && !m.ContainsGenericParameters)
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
                // and the collections keyed by object id (their code is made per key type, on first use)
                var id = new ZDOID(1L, 1u);
                var set = new HashSet<ZDOID> { id }; set.Contains(id); set.Remove(id);
                var map = new Dictionary<ZDOID, float> { [id] = 1f }; map.TryGetValue(id, out float f0); map.Remove(id);
                var kinds = new Dictionary<Kind, int> { [Kind.Torch] = 1 }; kinds.TryGetValue(Kind.Torch, out int k0);
                var ids = new List<ZDOID> { id }; ids.AddRange(set);
                Look(null, false);
            }
            catch { }
        }

        private static string KindName(Kind k)
        {
            switch (k)
            {
                case Kind.HotTub: return "hot tub";
                case Kind.Refinery: return "eitr refinery";
                case Kind.FrostKiln: return "frost kiln";
                case Kind.FrostFoundry: return "frost foundry";
                case Kind.Shield: return "shield generator";
                default: return k.ToString().ToLowerInvariant();
            }
        }

        private static string Where(ZDO zdo)
        {
            var p = zdo.GetPosition();
            return $"{p.x:F0}, {p.z:F0}";
        }
    }
}
