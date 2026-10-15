#if SELFTEST
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Everlit
{
    // Server-side self-test, only in a build made with -p:SelfTest=true. Started by a file named
    // everlit.selftest in BepInEx/config. Makes player-built and world fires, checks what Everlit does
    // with them, loads them in the server's own game near the world's centre (as a player walking up
    // would) to see the game itself run them with infinite fuel, logs "Everlit TEST PASS/FAIL", then
    // removes everything it made and puts the settings, world clock and reference position back.
    internal static class Tests
    {
        private static bool started;
        private static int pass, fail;
        private static readonly List<ZDO> made = new List<ZDO>();
        private static string Trigger => Path.Combine(Paths.ConfigPath, "everlit.selftest");
        private static Vector3? spot;

        // a dedicated server parks its reference position outside the world every physics tick;
        // while a test needs the game to run objects, hold it at the test spot
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))]
        private static class HoldScene { private static void Prefix() { if (spot.HasValue) ZNet.instance.SetReferencePosition(spot.Value); } }
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Update))]
        private static class HoldZones { private static void Prefix() { if (spot.HasValue) ZNet.instance.SetReferencePosition(spot.Value); } }

        public static void Update(Plugin host)
        {
            if (started || Time.time < 60f || !File.Exists(Trigger) || ZNet.instance == null || ZDOMan.instance == null || ZNetScene.instance == null) return;
            started = true;
            try { File.Delete(Trigger); } catch { }
            host.StartCoroutine(Run(host));
        }

        private static IEnumerator Run(Plugin host)
        {
            ZLog.Log("Everlit TEST starting");
            var cfg = host.Config;
            bool save = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;
            double clockWas = ZNet.instance.GetTimeSeconds();
            Vector3 refWas = ZNet.instance.GetReferencePosition();
            var was = (Plugin.Enabled.Value, Plugin.PlayerBuiltOnly.Value, Plugin.ExcludeFires.Value, Plugin.Torches.Value);

            IEnumerator inner = Body();
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (System.Exception e) { Check("no exception", false, e.ToString()); break; }
                if (!more) break;
                yield return inner.Current;
            }

            spot = null;
            ZNet.instance.SetReferencePosition(refWas);
            ZNet.instance.SetNetTime(clockWas);
            (Plugin.Enabled.Value, Plugin.PlayerBuiltOnly.Value, Plugin.ExcludeFires.Value, Plugin.Torches.Value) = was;
            Keeper.SettingsChanged();
            cfg.SaveOnConfigSet = save;
            foreach (var z in made)
                if (z != null && z.IsValid()) { z.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(z); }
            ZLog.Log($"Everlit TEST done: {pass} pass, {fail} fail (removed {made.Count} test objects)");
        }

        private static IEnumerator Body()
        {
            while (Keeper.RoundRunning) yield return new WaitForSeconds(1f);
            var at = new Vector3(6000f, 30f, -6000f);   // out at sea, nobody near
            const string torchName = "piece_groundtorch_wood";
            float torchMax = ZNetScene.instance.GetPrefab(torchName).GetComponent<Fireplace>().m_maxFuel;

            // 1. a round: a built torch and a built hearth get lit, a world fire pit doesn't
            var torch = Make(torchName, at, built: true);
            torch.Set(ZDOVars.s_fuel, 0.5f);
            var hearth = Make("hearth", at + new Vector3(4f, 0f, 0f), built: true);
            var camp = Make("fire_pit", at + new Vector3(8f, 0f, 0f), built: false);
            camp.Set(ZDOVars.s_fuel, 0f);
            var snapshot = Package(torch);              // the torch as a player's game had it before
            uint rev = torch.DataRevision;
            yield return Round(world: true);            // made here, not sent by a player: only a world walk finds them
            Check("built torch lit by a round", Keeper.IsLit(torch), Fields(torch));
            Check("built torch topped up for the meantime", Mathf.Approximately(torch.GetFloat(ZDOVars.s_fuel), torchMax), $"fuel {torch.GetFloat(ZDOVars.s_fuel)}");
            Check("change sent on to players (revision moved ahead)", torch.DataRevision > rev + 1, $"{rev} -> {torch.DataRevision}");
            Check("built hearth lit", Keeper.IsLit(hearth), Fields(hearth));
            Check("world fire pit left alone", !Keeper.IsLit(camp) && !camp.GetBool("Fireplace.m_infiniteFuel"), Fields(camp));

            // 2. a fire as it's built: lit when the player's game sends it, without waiting for a round
            var fresh = Make(torchName, at + new Vector3(12f, 0f, 0f), built: true);
            Arrive(fresh, Package(fresh));
            yield return new WaitForSeconds(0.5f);
            Check("just-built torch lit right away", Keeper.IsLit(fresh), Fields(fresh));

            // 3. a stale update from a player's game (made before the change) doesn't undo it
            Arrive(torch, snapshot);
            Check("a player's stale update doesn't undo it", Keeper.IsLit(torch), Fields(torch));

            // 4. the game itself runs it with infinite fuel: load both near the centre in the server's game
            var near = new Vector3(14f, 0f, 14f);
            spot = near;
            for (int i = 0; i < 60 && !ZoneSystem.instance.IsZoneLoaded(near); i++) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(2f);
            near.y = ZoneSystem.instance.GetGroundHeight(near);
            long now = ZNet.instance.GetTime().Ticks;
            torch.Set(ZDOVars.s_lastTime, now);
            camp.Set(ZDOVars.s_fuel, 1f);
            camp.Set(ZDOVars.s_lastTime, now);
            var campAt = near + new Vector3(4f, 0f, 0f);
            campAt.y = ZoneSystem.instance.GetGroundHeight(campAt);
            torch.SetPosition(near);
            camp.SetPosition(campAt);
            Fireplace ft = null, fc = null;
            for (int i = 0; i < 30 && ((ft = Live(torch)) == null || (fc = Live(camp)) == null); i++) yield return new WaitForSeconds(0.5f);
            Check("both loaded in the server's game", ft != null && fc != null, "");
            if (ft == null || fc == null) yield break;
            Check("game applied infinite fuel to the built torch", ft.m_infiniteFuel, "");
            Check("game left the world fire pit normal", !fc.m_infiniteFuel, "");
            yield return new WaitForSeconds(2.5f);
            float before = torch.GetFloat(ZDOVars.s_fuel);
            ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + 3 * 24 * 3600);   // three days of world time
            yield return new WaitForSeconds(4.5f);
            Check("built torch burns nothing in three world days", torch.IsValid() && torch.GetFloat(ZDOVars.s_fuel) == before && ft && ft.IsBurning(),
                $"fuel {before} -> {torch.GetFloat(ZDOVars.s_fuel)}, burning {ft && ft.IsBurning()}");
            Check("world fire pit burned out as normal", camp.IsValid() && camp.GetFloat(ZDOVars.s_fuel) == 0f && fc && !fc.IsBurning(), $"valid {camp.IsValid()}, fuel {camp.GetFloat(ZDOVars.s_fuel)}");

            // 5. settings: a fire type switched off is put back; ExcludeFires; Enabled off puts everything back
            Plugin.Torches.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("Torches off: torches put back, hearth stays lit", !Keeper.IsLit(fresh) && !fresh.GetBool("Fireplace.m_infiniteFuel") && Keeper.IsLit(hearth), Fields(fresh));
            Plugin.Torches.Value = true; Plugin.ExcludeFires.Value = " Hearth , bonfire"; Keeper.SettingsChanged();
            yield return Round();
            Check("ExcludeFires (any case, spaces): hearth put back, torches lit again", !Keeper.IsLit(hearth) && Keeper.IsLit(fresh), $"{Fields(hearth)} / {Fields(fresh)}");
            Plugin.ExcludeFires.Value = ""; Plugin.PlayerBuiltOnly.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("PlayerBuiltOnly off: the world fire pit lit too", camp.IsValid() && Keeper.IsLit(camp), $"valid {camp.IsValid()}, {Fields(camp)}");
            Plugin.PlayerBuiltOnly.Value = true; Plugin.Enabled.Value = false; Keeper.SettingsChanged();
            yield return Round();
            bool allBack = true;
            foreach (var z in made) allBack &= !Keeper.IsLit(z) && !z.GetBool("Fireplace.m_infiniteFuel");
            Check("Enabled off: every fire put back to normal", allBack, $"{Fields(torch)} / {Fields(camp)}");

            // 6. reloaded after being put back, the game burns it normally again
            torch.SetPosition(at);
            yield return new WaitForSeconds(3f);
            torch.SetPosition(near + new Vector3(-4f, 0f, 0f));
            for (int i = 0; i < 30 && (ft = Live(torch)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("put-back torch runs without infinite fuel when it loads again", ft != null && !ft.m_infiniteFuel, ft == null ? "not loaded" : "");

            Check("rounds stay light (slowest frame under 5 ms, every round)", slowest < 5.0, $"{slowest:F2} ms");
        }

        private static double slowest;
        private static IEnumerator Round(bool world = false)
        {
            if (world) Keeper.WorldNow(); else Keeper.RoundNow();
            yield return null;
            for (int i = 0; i < 600 && Keeper.RoundRunning; i++) yield return new WaitForSeconds(0.5f);
            if (Keeper.SlowestMs > slowest) slowest = Keeper.SlowestMs;
        }

        private static void Check(string what, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            ZLog.Log($"Everlit TEST {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? " | " + detail : "")}");
        }

        private static string Fields(ZDO z) => $"lit {Keeper.IsLit(z)}, infinite {z.GetBool("Fireplace.m_infiniteFuel")}, HasFields {z.GetBool("HasFields")}";

        private static ZDO Make(string prefab, Vector3 pos, bool built)
        {
            int hash = prefab.GetStableHashCode();
            var view = ZNetScene.instance.GetPrefab(hash).GetComponent<ZNetView>();
            var zdo = ZDOMan.instance.CreateNewZDO(pos, hash);
            zdo.Persistent = view.m_persistent; zdo.Type = view.m_type; zdo.Distant = view.m_distant;
            zdo.SetPrefab(hash);
            if (built) zdo.Set(ZDOVars.s_creator, 1234567L);
            made.Add(zdo);
            return zdo;
        }

        private static Fireplace Live(ZDO z)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return v ? v.GetComponent<Fireplace>() : null;
        }

        private static ZPackage Package(ZDO zdo)
        {
            var pkg = new ZPackage();
            zdo.Serialize(pkg);
            return pkg;
        }

        // what the server does with an update from a player's game: read the object from the wire
        private static void Arrive(ZDO zdo, ZPackage pkg)
        {
            pkg.SetPos(0);
            zdo.Deserialize(pkg);
        }
    }
}
#endif
