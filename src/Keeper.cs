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
    internal static class Keeper
    {
        private enum Kind { Campfire, Bonfire, Hearth, Torch, Brazier, Candle, Other }

        private sealed class FireType
        {
            public string Name;
            public Kind Kind;
            public float MaxFuel;
        }

        private static readonly int HasFields = "HasFields".GetStableHashCode();
        private static readonly int HasFieldsFireplace = ("HasFields" + nameof(Fireplace)).GetStableHashCode();
        private static readonly int InfiniteFuel = (nameof(Fireplace) + "." + nameof(Fireplace.m_infiniteFuel)).GetStableHashCode();
        private static readonly int Mark = "everlit".GetStableHashCode();   // fires Everlit lit, so it can put them back
        private const int ROUND_BUDGET = 2000;          // objects looked at per frame, at most
        private const double ROUND_MS = 1.0;            // and no more than this much time in a frame
        private const int ROUND_CHANGES = 25;           // fires changed per frame, so lighting a whole world is spread out
        private const float REPORT_MINUTES = 10f;       // between log lines about fires lit as they were built

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
        private static int rLit, rUnlit, rKept, rWorld, rBurning;
        private static readonly Dictionary<Kind, int> rKinds = new Dictionary<Kind, int>();
        private static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        private static double roundMs, slowestMs;
        private static int roundFrames;

        private static bool Active => ZNet.instance != null && ZNet.instance.IsServer()
            && ZDOMan.instance != null && ZNetScene.instance != null;

        // every object a player's game sends the server passes through here: catches fires as they're
        // built, so after the first walk of the world nothing needs sweeping
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        private static class Arrived
        {
            private static void Postfix(ZDO __instance)
            {
                if (types != null && types.ContainsKey(__instance.GetPrefab())) arrived.Add(__instance.m_uid);
            }
        }

        public static void SettingsChanged()
        {
            wanted = null;
            if (!running) nextRound = 0f;
        }

#if SELFTEST
        public static bool RoundRunning => running;
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
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                var f = go ? go.GetComponent<Fireplace>() : null;
                // already infinite (traders' fire pits) or doesn't burn anything: nothing to do
                if (f == null || f.m_infiniteFuel || f.m_secPerFuel <= 0f || f.m_fuelItem == null) continue;
                types[go.name.GetStableHashCode()] = new FireType { Name = go.name, Kind = KindOf(go.name), MaxFuel = f.m_maxFuel };
            }
            ZLog.Log($"Everlit: {types.Count} kinds of fire: " + string.Join(", ", types.Values.OrderBy(t => t.Kind).Select(t => $"{t.Name} ({t.Kind})")));
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
                    default: on = Plugin.OtherFires.Value; break;
                }
                if (on) wanted.Add(kv.Key);
            }
        }

        public static bool IsLit(ZDO zdo) => zdo.GetInt(Mark) == 1 && zdo.GetBool(InfiniteFuel);

        // returns true when it changed the fire
        private static bool Look(ZDO zdo, bool live)
        {
            if (zdo == null || !zdo.IsValid() || !types.TryGetValue(zdo.GetPrefab(), out var type)) return false;
            known.Add(zdo.m_uid);
            bool built = zdo.GetLong(ZDOVars.s_creator) != 0L;
            bool want = wanted.Contains(zdo.GetPrefab()) && (built || !Plugin.PlayerBuiltOnly.Value);
            bool marked = zdo.GetInt(Mark) == 1;
            if (want && !IsLit(zdo))
            {
                zdo.Set(HasFields, true);
                zdo.Set(HasFieldsFireplace, true);
                zdo.Set(InfiniteFuel, true);
                zdo.Set(Mark, 1);
                // the player's game that runs this fire right now only picks the switch up when it next
                // loads it; a full load keeps it burning until then
                zdo.Set(ZDOVars.s_fuel, type.MaxFuel);
                Win(zdo);
                rLit++;
                rKinds[type.Kind] = rKinds.TryGetValue(type.Kind, out var k) ? k + 1 : 1;
                if (Plugin.LogEachFire.Value) ZLog.Log($"Everlit: lit {type.Name} at {Where(zdo)}{(live ? " (just built)" : "")}");
                return true;
            }
            if (!want && marked)
            {
                zdo.Set(InfiniteFuel, false);
                zdo.RemoveInt(Mark);
                Win(zdo);
                rUnlit++;
                if (Plugin.LogEachFire.Value) ZLog.Log($"Everlit: put {type.Name} at {Where(zdo)} back to normal");
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
            wholeWorld = firstRound || forceWorld;
            forceWorld = false;
            sector = 0; index = 0;
            roundList.Clear();
            if (!wholeWorld) roundList.AddRange(known);
            rLit = rUnlit = rKept = rWorld = rBurning = 0; rKinds.Clear();
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
            nextRound = float.MaxValue;
            if (firstRound || rLit > 0 || rUnlit > 0 || rBurning > 0)
            {
                string kinds = rKinds.Count == 0 ? "" : " (" + string.Join(", ", rKinds.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key.ToString().ToLowerInvariant()}")) + ")";
                ZLog.Log($"Everlit: {rKept + rLit} fires kept lit{kinds}; {rLit} newly lit, {rUnlit} put back to normal, " +
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

        private static string Where(ZDO zdo)
        {
            var p = zdo.GetPosition();
            return $"{p.x:F0}, {p.z:F0}";
        }
    }
}
