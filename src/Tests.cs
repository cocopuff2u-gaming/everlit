#if SELFTEST
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var was = (Plugin.Enabled.Value, Plugin.PlayerBuiltOnly.Value, Plugin.ExcludeFires.Value, Plugin.Torches.Value, Plugin.Smelters.Value, Plugin.BlastFurnaces.Value, Plugin.HotTubs.Value, Plugin.Ovens.Value, Plugin.EitrRefinery.Value, Plugin.FrostKiln.Value, Plugin.CharcoalKiln.Value, Plugin.FrostFoundry.Value,
                Plugin.ShieldGenerators.Value, Plugin.NoSmokeChoke.Value, Plugin.PlayersCanTurnOff.Value, Plugin.WardedOnly.Value, Plugin.HoverInfo.Value, Plugin.HoverNote.Value);

            var capsWere = new[] { Plugin.CharcoalKilnMaxWood, Plugin.FrostKilnMaxIce, Plugin.SmelterMaxOre, Plugin.SmelterMaxCoal, Plugin.BlastFurnaceMaxOre, Plugin.BlastFurnaceMaxCoal }.Select(e => (e, e.Value)).ToArray();

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
            (Plugin.Enabled.Value, Plugin.PlayerBuiltOnly.Value, Plugin.ExcludeFires.Value, Plugin.Torches.Value, Plugin.Smelters.Value, Plugin.BlastFurnaces.Value, Plugin.HotTubs.Value, Plugin.Ovens.Value, Plugin.EitrRefinery.Value, Plugin.FrostKiln.Value, Plugin.CharcoalKiln.Value, Plugin.FrostFoundry.Value,
                Plugin.ShieldGenerators.Value, Plugin.NoSmokeChoke.Value, Plugin.PlayersCanTurnOff.Value, Plugin.WardedOnly.Value, Plugin.HoverInfo.Value, Plugin.HoverNote.Value) = was;
            foreach (var (e, v) in capsWere) e.Value = v;
            Keeper.SettingsChanged();
            cfg.SaveOnConfigSet = save;
            foreach (var z in made)
                if (z != null && z.IsValid()) { z.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(z); }
            ZLog.Log($"Everlit TEST done: {pass} pass, {fail} fail (removed {made.Count} test objects)");
        }

        private static IEnumerator Body()
        {
            while (Keeper.RoundRunning) yield return new WaitForSeconds(1f);
            GameFacts();
            var at = new Vector3(6000f, 30f, -6000f);   // out at sea, nobody near
            const string torchName = "piece_groundtorch_wood";
            var torchPrefab = ZNetScene.instance.GetPrefab(torchName).GetComponent<Fireplace>();
            float torchMax = torchPrefab.m_maxFuel, torchSec = torchPrefab.m_secPerFuel;
            string torchLabel = torchPrefab.m_name;
            Plugin.HoverInfo.Value = true; Plugin.HoverNote.Value = "(never runs out)";

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
            Check("HoverInfo on (default): game runs the built torch on a piece of wood that lasts for good, not infinite fuel", !ft.m_infiniteFuel && ft.m_secPerFuel >= 1e29f,
                $"infinite {ft.m_infiniteFuel}, secPerFuel {ft.m_secPerFuel}");
            Check("HoverInfo on: its name carries the note", ft.m_name == torchLabel + " (never runs out)", ft.m_name);
            string hoverText = "";
            try { hoverText = ft.GetHoverText(); } catch (System.Exception e) { hoverText = "exception " + e.Message; }
            Check("HoverInfo on: the game shows hover text, the note and full fuel", hoverText.Contains("(never runs out)") && hoverText.Contains($"{(int)torchMax}/{(int)torchMax}"), hoverText.Replace("\n", " | "));
            Check("game left the world fire pit normal", !fc.m_infiniteFuel, "");
            yield return new WaitForSeconds(2.5f);
            float before = torch.GetFloat(ZDOVars.s_fuel);
            ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + 3 * 24 * 3600);   // three days of world time
            yield return new WaitForSeconds(4.5f);
            Check("built torch burns nothing in three world days", torch.IsValid() && torch.GetFloat(ZDOVars.s_fuel) == before && ft && ft.IsBurning(),
                $"fuel {before} -> {torch.GetFloat(ZDOVars.s_fuel)}, burning {ft && ft.IsBurning()}");
            Check("world fire pit burned out as normal", camp.IsValid() && camp.GetFloat(ZDOVars.s_fuel) == 0f && fc && !fc.IsBurning(), $"valid {camp.IsValid()}, fuel {camp.GetFloat(ZDOVars.s_fuel)}");

            // 4b. HoverInfo off: lit fires go back to the game's infinite fuel (no hover text), and their
            // own name and burn rate; on again, back to the lasting piece of wood and the note
            Plugin.HoverInfo.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("HoverInfo off: the torch switched to infinite fuel, its own name and burn rate", Keeper.IsLit(torch) && torch.GetBool("Fireplace.m_infiniteFuel")
                && torch.GetFloat("Fireplace.m_secPerFuel") == torchSec && torch.GetString("Fireplace.m_name") == torchLabel, Fields(torch));
            torch.SetPosition(at);
            yield return new WaitForSeconds(3f);
            torch.SetPosition(near);
            for (int i = 0; i < 30 && (ft = Live(torch)) == null; i++) yield return new WaitForSeconds(0.5f);
            string blank = ft == null ? "not loaded" : ft.GetHoverText();
            Check("HoverInfo off: loaded again, it runs on infinite fuel and shows no hover text", ft != null && ft.m_infiniteFuel && blank == "" && ft.m_name == torchLabel, $"hover '{blank}'");
            Plugin.HoverInfo.Value = true; Plugin.HoverNote.Value = "<color=orange>Everlit</color>"; Keeper.SettingsChanged();
            yield return Round();
            Check("HoverInfo on again, with another note: switched back", Keeper.IsLit(torch) && !torch.GetBool("Fireplace.m_infiniteFuel") && torch.GetFloat("Fireplace.m_secPerFuel") >= 1e29f
                && torch.GetString("Fireplace.m_name") == torchLabel + " <color=orange>Everlit</color>", Fields(torch));
            torch.Set(ZDOVars.s_fuel, 1f);
            Arrive(torch, Package(torch));
            yield return new WaitForSeconds(0.5f);
            Check("HoverInfo on: a lit fire found below full is filled up again", torch.GetFloat(ZDOVars.s_fuel) == torchMax, $"fuel {torch.GetFloat(ZDOVars.s_fuel)}");
            Plugin.HoverNote.Value = "(never runs out)"; Keeper.SettingsChanged();
            yield return Round();

            // 5. furnaces: left alone while Smelters and BlastFurnaces are off; Smelters on switches
            // smelters only, and a built smelter smelts with no coal and isn't handed any (torn down, it
            // would drop it); BlastFurnaces on switches blast furnaces
            Plugin.Smelters.Value = false; Plugin.BlastFurnaces.Value = false; Keeper.SettingsChanged();
            yield return Round();
            var smelter = Make("smelter", at + new Vector3(16f, 0f, 0f), built: true);
            smelter.Set(ZDOVars.s_fuel, 2f);
            var blast = Make("blastfurnace", at + new Vector3(16f, 0f, 4f), built: true);
            blast.Set(ZDOVars.s_fuel, 3f);
            Arrive(smelter, Package(smelter));
            Arrive(blast, Package(blast));
            yield return new WaitForSeconds(0.5f);
            Check("Smelters, BlastFurnaces off (default): built smelter and blast furnace left alone", !Keeper.IsLit(smelter) && !Keeper.IsLit(blast), $"{Fields(smelter)} / {Fields(blast)}");
            Plugin.Smelters.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("Smelters on: built smelter switched, its coal left as it was", Keeper.IsLit(smelter) && smelter.GetInt("Smelter.m_maxFuel", -1) == 0
                && smelter.GetFloat(ZDOVars.s_fuel) == 2f, $"{Fields(smelter)}, maxFuel {smelter.GetInt("Smelter.m_maxFuel", -1)}, coal {smelter.GetFloat(ZDOVars.s_fuel)}");
            Check("Smelters on: blast furnace still left alone", !Keeper.IsLit(blast), Fields(blast));
            int blastMax = ZNetScene.instance.GetPrefab("blastfurnace").GetComponent<Smelter>().m_maxFuel;
            Plugin.BlastFurnaces.Value = true; Plugin.Smelters.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("BlastFurnaces on, Smelters off: blast furnace switched, its coal left as it was; smelter put back", Keeper.IsLit(blast) && blast.GetInt("Smelter.m_maxFuel", -1) == 0
                && blast.GetFloat(ZDOVars.s_fuel) == 3f && !Keeper.IsLit(smelter), $"maxFuel {blast.GetInt("Smelter.m_maxFuel", -1)}, coal {blast.GetFloat(ZDOVars.s_fuel)}; smelter {Fields(smelter)}");
            Plugin.BlastFurnaces.Value = false; Plugin.Smelters.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("BlastFurnaces off: blast furnace needs coal again", !Keeper.IsLit(blast) && blast.GetInt("Smelter.m_maxFuel", -1) == blastMax && Keeper.IsLit(smelter),
                $"maxFuel {blast.GetInt("Smelter.m_maxFuel", -1)}, prefab {blastMax}");
            Check("fires stay lit alongside", Keeper.IsLit(torch) && Keeper.IsLit(hearth), "");
            var smelterAt = near + new Vector3(0f, 0f, 6f);
            smelterAt.y = ZoneSystem.instance.GetGroundHeight(smelterAt);
            smelter.Set(ZDOVars.s_fuel, 0f);
            smelter.SetPosition(smelterAt);
            Smelter fs = null;
            for (int i = 0; i < 30 && (fs = LiveSmelter(smelter)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("smelter loaded in the server's game, needing no coal", fs != null && fs.m_maxFuel == 0, fs == null ? "not loaded" : $"maxFuel {fs.m_maxFuel}; {Loaded(smelter, "Smelter.m_maxFuel")}");
            if (fs != null)
            {
                yield return new WaitForSeconds(2f);
                fs.QueueOre("CopperOre", false);
                fs.QueueOre("CopperOre", false);
                ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + fs.m_secPerProduct + 5f);
                yield return new WaitForSeconds(3f);
                Check("smelter smelted ore with no coal in it", fs && fs.GetQueueSize() == 1 && smelter.GetFloat(ZDOVars.s_fuel) == 0f,
                    $"ore left {(fs ? fs.GetQueueSize() : -1)}, coal {smelter.GetFloat(ZDOVars.s_fuel)}");
                // a bar it may have dropped
                foreach (var d in ItemDrop.s_instances.ToArray())
                    if (d && d.name.StartsWith("Copper") && Vector3.Distance(d.transform.position, smelterAt) < 6f && d.m_nview && d.m_nview.IsValid())
                        ZNetScene.instance.Destroy(d.gameObject);
            }

            // 5b. hot tubs (on by default): a built one stays hot with no wood, and keeps the wood it had
            var tub = Make("piece_bathtub", at + new Vector3(20f, 0f, 0f), built: true);
            tub.Set(ZDOVars.s_fuel, 3f);
            Arrive(tub, Package(tub));
            yield return new WaitForSeconds(0.5f);
            Check("built hot tub switched as it's built, its wood left as it was", Keeper.IsLit(tub) && tub.GetInt("Smelter.m_maxFuel", -1) == 0
                && tub.GetFloat(ZDOVars.s_fuel) == 3f, $"maxFuel {tub.GetInt("Smelter.m_maxFuel", -1)}, wood {tub.GetFloat(ZDOVars.s_fuel)}");
            var tubAt = near + new Vector3(-6f, 0f, 6f);
            tubAt.y = ZoneSystem.instance.GetGroundHeight(tubAt);
            tub.Set(ZDOVars.s_fuel, 0f);
            tub.SetPosition(tubAt);
            Smelter fh = null;
            for (int i = 0; i < 30 && (fh = LiveSmelter(tub)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("hot tub loaded in the server's game, needing no wood", fh != null && fh.m_maxFuel == 0, fh == null ? "not loaded" : $"maxFuel {fh.m_maxFuel}; {Loaded(tub, "Smelter.m_maxFuel")}");
            if (fh != null)
            {
                yield return new WaitForSeconds(2f);
                ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + 3 * 24 * 3600);
                yield return new WaitForSeconds(3f);
                Check("hot tub hot with no wood after three world days", fh && fh.IsActive() && fh.m_enabledObject.activeSelf && tub.GetFloat(ZDOVars.s_fuel) == 0f,
                    $"active {(fh ? fh.IsActive() : false)}, wood {tub.GetFloat(ZDOVars.s_fuel)}");
            }
            int tubMax = ZNetScene.instance.GetPrefab("piece_bathtub").GetComponent<Smelter>().m_maxFuel;
            Plugin.HotTubs.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("HotTubs off: hot tub needs wood again", !Keeper.IsLit(tub) && tub.GetInt("Smelter.m_maxFuel", -1) == tubMax, $"maxFuel {tub.GetInt("Smelter.m_maxFuel", -1)}, prefab {tubMax}");
            Plugin.HotTubs.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("HotTubs on again: switched back", Keeper.IsLit(tub), "");

            // 5c. ovens (on by default): wood lasts for good; an empty one gets under one piece, a stocked one keeps its wood
            const int forever = int.MaxValue;
            var oven = Make("piece_oven", at + new Vector3(24f, 0f, 0f), built: true);
            oven.Set(ZDOVars.s_fuel, 0f);
            var stocked = Make("piece_oven", at + new Vector3(28f, 0f, 0f), built: true);
            stocked.Set(ZDOVars.s_fuel, 5f);
            Arrive(oven, Package(oven));
            Arrive(stocked, Package(stocked));
            yield return new WaitForSeconds(0.5f);
            Check("built empty oven switched as it's built, given under one piece of wood", Keeper.IsLit(oven) && oven.GetInt("CookingStation.m_secPerFuel") == forever
                && oven.GetFloat(ZDOVars.s_fuel) > 0f && oven.GetFloat(ZDOVars.s_fuel) < 1f, $"secPerFuel {oven.GetInt("CookingStation.m_secPerFuel")}, wood {oven.GetFloat(ZDOVars.s_fuel)}");
            Check("stocked oven keeps its wood as it was", Keeper.IsLit(stocked) && stocked.GetFloat(ZDOVars.s_fuel) == 5f, $"wood {stocked.GetFloat(ZDOVars.s_fuel)}");
            var ovenAt = near + new Vector3(6f, 0f, -6f);
            ovenAt.y = ZoneSystem.instance.GetGroundHeight(ovenAt);
            oven.SetPosition(ovenAt);
            CookingStation fo = null;
            for (int i = 0; i < 30 && (fo = LiveOven(oven)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("oven loaded in the server's game, its wood lasting for good", fo != null && fo.m_secPerFuel == forever, fo == null ? "not loaded" : $"secPerFuel {fo.m_secPerFuel}; {Loaded(oven, "CookingStation.m_secPerFuel")}");
            if (fo != null)
            {
                yield return new WaitForSeconds(2f);
                float ovenWood = oven.GetFloat(ZDOVars.s_fuel);
                ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + 3 * 24 * 3600);
                yield return new WaitForSeconds(3f);
                Check("oven still has its wood after three world days", fo && oven.GetFloat(ZDOVars.s_fuel) > 0.98f * ovenWood && fo.GetFuel() > 0f,
                    $"wood {ovenWood} -> {oven.GetFloat(ZDOVars.s_fuel)}");
                // torn down, it drops no wood (under one piece in it)
                int woodBefore = 0, woodAfter = 0;
                foreach (var d in ItemDrop.s_instances) if (d && d.name.StartsWith("Wood") && Vector3.Distance(d.transform.position, ovenAt) < 6f) woodBefore++;
                fo.DropAllItems();
                yield return new WaitForSeconds(0.5f);
                foreach (var d in ItemDrop.s_instances.ToArray())
                    if (d && d.name.StartsWith("Wood") && Vector3.Distance(d.transform.position, ovenAt) < 6f) { woodAfter++; if (d.m_nview && d.m_nview.IsValid()) ZNetScene.instance.Destroy(d.gameObject); }
                Check("oven torn down drops no free wood", woodAfter == woodBefore, $"{woodBefore} -> {woodAfter}");
            }
            int ovenSec = ZNetScene.instance.GetPrefab("piece_oven").GetComponent<CookingStation>().m_secPerFuel;
            Plugin.Ovens.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("Ovens off: ovens burn wood again", !Keeper.IsLit(oven) && oven.GetInt("CookingStation.m_secPerFuel") == ovenSec && !Keeper.IsLit(stocked), $"secPerFuel {oven.GetInt("CookingStation.m_secPerFuel")}, prefab {ovenSec}");
            Plugin.Ovens.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("Ovens on again: switched back", Keeper.IsLit(oven) && Keeper.IsLit(stocked), "");

            // 5d. eitr refinery (on by default) refines without sap; frost kiln and frost foundry off by default
            var refinery = Make("eitrrefinery", at + new Vector3(32f, 0f, 0f), built: true);
            refinery.Set(ZDOVars.s_fuel, 4f);
            var kiln = Make("piece_FrostKiln", at + new Vector3(36f, 0f, 0f), built: true);
            var foundry = Make("piece_FrostFoundry", at + new Vector3(40f, 0f, 0f), built: true);
            foreach (var z in new[] { refinery, kiln, foundry }) Arrive(z, Package(z));
            yield return new WaitForSeconds(0.5f);
            Check("built eitr refinery switched, its sap left as it was", Keeper.IsLit(refinery) && refinery.GetInt("Smelter.m_maxFuel", -1) == 0 && refinery.GetFloat(ZDOVars.s_fuel) == 4f,
                $"maxFuel {refinery.GetInt("Smelter.m_maxFuel", -1)}, sap {refinery.GetFloat(ZDOVars.s_fuel)}");
            Check("FrostKiln, FrostFoundry off (default): left alone", !Keeper.IsLit(kiln) && !Keeper.IsLit(foundry), "");
            var refAt = near + new Vector3(-6f, 0f, -6f);
            refAt.y = ZoneSystem.instance.GetGroundHeight(refAt);
            refinery.Set(ZDOVars.s_fuel, 0f);
            refinery.SetPosition(refAt);
            Smelter fr = null;
            for (int i = 0; i < 30 && (fr = LiveSmelter(refinery)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("eitr refinery loaded in the server's game, needing no sap", fr != null && fr.m_maxFuel == 0, fr == null ? "not loaded" : $"maxFuel {fr.m_maxFuel}");
            if (fr != null)
            {
                yield return new WaitForSeconds(2f);
                string from = fr.m_conversion[0].m_from.gameObject.name, to = fr.m_conversion[0].m_to.gameObject.name;
                fr.QueueOre(from, false);
                fr.QueueOre(from, false);
                ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + fr.m_secPerProduct + 5f);
                yield return new WaitForSeconds(3f);
                Check($"eitr refinery refined {from} into {to} with no sap", fr && fr.GetQueueSize() == 1 && refinery.GetFloat(ZDOVars.s_fuel) == 0f,
                    $"left {(fr ? fr.GetQueueSize() : -1)}, sap {refinery.GetFloat(ZDOVars.s_fuel)}");
                foreach (var d in ItemDrop.s_instances.ToArray())
                    if (d && d.name.StartsWith(to) && Vector3.Distance(d.transform.position, refAt) < 6f && d.m_nview && d.m_nview.IsValid())
                        ZNetScene.instance.Destroy(d.gameObject);
            }
            var kilnPrefab = ZNetScene.instance.GetPrefab("piece_FrostKiln").GetComponent<Smelter>();
            ZLog.Log($"Everlit TEST note: the frost kiln makes {(kilnPrefab.m_conversion[0].m_to ? kilnPrefab.m_conversion[0].m_to.gameObject.name : "?")} every {kilnPrefab.m_secPerProduct} s, stacking {kilnPrefab.m_spawnStack}");
            Plugin.FrostKiln.Value = true; Plugin.FrostFoundry.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("FrostKiln on: switched, needs no ice", Keeper.IsLit(kiln) && kiln.GetInt("Smelter.m_maxFuel", -1) == 0, $"maxFuel {kiln.GetInt("Smelter.m_maxFuel", -1)}");
            Check("FrostFoundry on: switched, given under one piece of frozen fuel", Keeper.IsLit(foundry) && foundry.GetInt("CookingStation.m_secPerFuel") == int.MaxValue
                && foundry.GetFloat(ZDOVars.s_fuel) > 0f && foundry.GetFloat(ZDOVars.s_fuel) < 1f, $"secPerFuel {foundry.GetInt("CookingStation.m_secPerFuel")}, fuel {foundry.GetFloat(ZDOVars.s_fuel)}");
            int kilnMax = kilnPrefab.m_maxFuel, foundrySec = ZNetScene.instance.GetPrefab("piece_FrostFoundry").GetComponent<CookingStation>().m_secPerFuel;
            Plugin.FrostKiln.Value = false; Plugin.FrostFoundry.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("FrostKiln, FrostFoundry off again: put back", !Keeper.IsLit(kiln) && kiln.GetInt("Smelter.m_maxFuel", -1) == kilnMax
                && !Keeper.IsLit(foundry) && foundry.GetInt("CookingStation.m_secPerFuel") == foundrySec, $"kiln {kiln.GetInt("Smelter.m_maxFuel", -1)}, foundry {foundry.GetInt("CookingStation.m_secPerFuel")}");

            // 5d2. charcoal kilns (off by default): on, an empty one is given one piece of wood, and again
            // each time it runs dry (seen when the player's game running it sends it on)
            var charcoal = Make("charcoal_kiln", at + new Vector3(48f, 0f, 0f), built: true);
            var full = Make("charcoal_kiln", at + new Vector3(52f, 0f, 0f), built: true);
            full.Set(ZDOVars.s_item0, "RoundLog"); full.Set(ZDOVars.s_queued, 1);
            Arrive(charcoal, Package(charcoal));
            Arrive(full, Package(full));
            yield return new WaitForSeconds(0.5f);
            Check("CharcoalKiln off (default): built charcoal kiln left alone", !Keeper.IsLit(charcoal) && charcoal.GetInt(ZDOVars.s_queued) == 0, $"queued {charcoal.GetInt(ZDOVars.s_queued)}");
            Plugin.CharcoalKiln.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("CharcoalKiln on: empty kiln given one piece of wood", Keeper.IsLit(charcoal) && charcoal.GetInt(ZDOVars.s_queued) == 1 && charcoal.GetString(ZDOVars.s_item0) == "Wood",
                $"queued {charcoal.GetInt(ZDOVars.s_queued)}, item0 {charcoal.GetString(ZDOVars.s_item0)}");
            Check("CharcoalKiln on: a kiln with wood in it gets nothing more", Keeper.IsLit(full) && full.GetInt(ZDOVars.s_queued) == 1 && full.GetString(ZDOVars.s_item0) == "RoundLog",
                $"queued {full.GetInt(ZDOVars.s_queued)}, item0 {full.GetString(ZDOVars.s_item0)}");
            var charAt = near + new Vector3(10f, 0f, 6f);
            charAt.y = ZoneSystem.instance.GetGroundHeight(charAt);
            charcoal.SetPosition(charAt);
            Smelter fk = null;
            for (int i = 0; i < 30 && (fk = LiveSmelter(charcoal)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("charcoal kiln loaded in the server's game", fk != null, "");
            if (fk != null)
            {
                yield return new WaitForSeconds(2f);
                int coalBefore = 0, coalAfter = 0;
                foreach (var d in ItemDrop.s_instances) if (d && d.name.StartsWith("Coal") && Vector3.Distance(d.transform.position, charAt) < 6f) coalBefore++;
                ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + fk.m_secPerProduct + 3f);
                yield return new WaitForSeconds(3f);
                foreach (var d in ItemDrop.s_instances.ToArray())
                    if (d && d.name.StartsWith("Coal") && Vector3.Distance(d.transform.position, charAt) < 6f) { coalAfter++; if (d.m_nview && d.m_nview.IsValid()) ZNetScene.instance.Destroy(d.gameObject); }
                Check("charcoal kiln made coal from the piece it was given, and ran dry", coalAfter > coalBefore && charcoal.GetInt(ZDOVars.s_queued) == 0,
                    $"coal {coalBefore} -> {coalAfter}, queued {charcoal.GetInt(ZDOVars.s_queued)}");
                // the server runs it here; in play it's a player's game, whose update brings it back
                Arrive(charcoal, Package(charcoal));
                yield return new WaitForSeconds(0.5f);
                Check("run dry, it's given another piece as its update comes in", charcoal.GetInt(ZDOVars.s_queued) == 1 && charcoal.GetString(ZDOVars.s_item0) == "Wood",
                    $"queued {charcoal.GetInt(ZDOVars.s_queued)}");
            }
            Plugin.CharcoalKiln.Value = false; Keeper.SettingsChanged();
            yield return Round();
            charcoal.Set(ZDOVars.s_queued, 0);
            Arrive(charcoal, Package(charcoal));
            yield return new WaitForSeconds(0.5f);
            Check("CharcoalKiln off again: no longer fed", !Keeper.IsLit(charcoal) && !Keeper.IsLit(full) && charcoal.GetInt(ZDOVars.s_queued) == 0, $"queued {charcoal.GetInt(ZDOVars.s_queued)}");

            // 5d3. limits: raised on built furnaces and kilns, the world's own left alone, the coal limit
            // left to the fuel switch while that's on, the game's own back at 0
            var capSmelter = Make("smelter", at + new Vector3(56f, 0f, 0f), built: true);
            var capBlast = Make("blastfurnace", at + new Vector3(60f, 0f, 0f), built: true);
            var capKiln = Make("charcoal_kiln", at + new Vector3(64f, 0f, 0f), built: true);
            var capFrost = Make("piece_FrostKiln", at + new Vector3(68f, 0f, 0f), built: true);
            var wildSmelter = Make("smelter", at + new Vector3(72f, 0f, 0f), built: false);
            foreach (var z in new[] { capSmelter, capBlast, capKiln, capFrost, wildSmelter }) Arrive(z, Package(z));
            yield return new WaitForSeconds(0.5f);
            Check("limits at the game's own (default): nothing written", capSmelter.GetInt("Smelter.m_maxOre", -1) == -1 && capKiln.GetInt("Smelter.m_maxOre", -1) == -1 && capFrost.GetInt("Smelter.m_maxFuel", -1) == -1, "");
            Plugin.Smelters.Value = false;
            Plugin.SmelterMaxOre.Value = 30; Plugin.SmelterMaxCoal.Value = 50; Plugin.BlastFurnaceMaxOre.Value = 40; Plugin.BlastFurnaceMaxCoal.Value = 60;
            Plugin.CharcoalKilnMaxWood.Value = 75; Plugin.FrostKilnMaxIce.Value = 100;
            Keeper.SettingsChanged();
            yield return Round();
            string Lim(ZDO z) => $"ore {z.GetInt("Smelter.m_maxOre", -1)}, fuel {z.GetInt("Smelter.m_maxFuel", -1)}";
            Check("limits raised: smelter 30 ore / 50 coal, blast furnace 40 / 60", capSmelter.GetInt("Smelter.m_maxOre", -1) == 30 && capSmelter.GetInt("Smelter.m_maxFuel", -1) == 50
                && capBlast.GetInt("Smelter.m_maxOre", -1) == 40 && capBlast.GetInt("Smelter.m_maxFuel", -1) == 60, $"{Lim(capSmelter)} / {Lim(capBlast)}");
            Check("limits raised: charcoal kiln 75 wood, frost kiln 100 ice (and still takes nothing else)", capKiln.GetInt("Smelter.m_maxOre", -1) == 75 && capKiln.GetInt("Smelter.m_maxFuel", -1) == -1
                && capFrost.GetInt("Smelter.m_maxFuel", -1) == 100 && capFrost.GetInt("Smelter.m_maxOre", -1) == -1, $"{Lim(capKiln)} / {Lim(capFrost)}");
            Check("limits: the world's own smelter left alone", wildSmelter.GetInt("Smelter.m_maxOre", -1) == -1, Lim(wildSmelter));
            Plugin.Smelters.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("Smelters on: coal limit gives way to no coal, ore limit stays", Keeper.IsLit(capSmelter) && capSmelter.GetInt("Smelter.m_maxFuel", -1) == 0 && capSmelter.GetInt("Smelter.m_maxOre", -1) == 30, Lim(capSmelter));
            Plugin.Smelters.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("Smelters off again: back to the raised coal limit", !Keeper.IsLit(capSmelter) && capSmelter.GetInt("Smelter.m_maxFuel", -1) == 50, Lim(capSmelter));
            var capAt = near + new Vector3(12f, 0f, -8f);
            capAt.y = ZoneSystem.instance.GetGroundHeight(capAt);
            capKiln.SetPosition(capAt);
            Smelter fc2 = null;
            for (int i = 0; i < 30 && (fc2 = LiveSmelter(capKiln)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("charcoal kiln loaded in the server's game takes 75", fc2 != null && fc2.m_maxOre == 75, fc2 == null ? "not loaded" : $"maxOre {fc2.m_maxOre}; {Loaded(capKiln, "Smelter.m_maxOre")}");
            if (fc2 != null)
            {
                for (int i = 0; i < 40; i++) fc2.QueueOre("Wood", false);
                Check("it holds more than the game's own 25", fc2.GetQueueSize() == 40, $"queued {fc2.GetQueueSize()}");
            }
            Plugin.SmelterMaxOre.Value = 10; Plugin.SmelterMaxCoal.Value = 20; Plugin.BlastFurnaceMaxOre.Value = 10; Plugin.BlastFurnaceMaxCoal.Value = 20;
            Plugin.CharcoalKilnMaxWood.Value = 25; Plugin.FrostKilnMaxIce.Value = 25;
            Keeper.SettingsChanged();
            yield return Round();
            Check("limits back to the defaults: the game's own set again (10/20, 10/20, 25 wood, 25 ice)", capSmelter.GetInt("Smelter.m_maxOre", -1) == 10 && capSmelter.GetInt("Smelter.m_maxFuel", -1) == 20
                && capBlast.GetInt("Smelter.m_maxOre", -1) == 10 && capBlast.GetInt("Smelter.m_maxFuel", -1) == 20 && capKiln.GetInt("Smelter.m_maxOre", -1) == 25 && capFrost.GetInt("Smelter.m_maxFuel", -1) == 25,
                $"{Lim(capSmelter)} / {Lim(capBlast)} / {Lim(capKiln)} / {Lim(capFrost)}");

            // 5e. shield generators (off by default): on, a hit uses up no fuel
            string shieldName = ZNetScene.instance.m_prefabs.First(p => p && p.GetComponent<ShieldGenerator>() is ShieldGenerator sg && sg.m_fuelItems.Count > 0).name;   // not the charred ones
            float shieldNormal = ZNetScene.instance.GetPrefab(shieldName).GetComponent<ShieldGenerator>().m_fuelPerDamage;
            var shield = Make(shieldName, at + new Vector3(44f, 0f, 0f), built: true);
            shield.Set(ZDOVars.s_fuel, 5f);
            Arrive(shield, Package(shield));
            yield return new WaitForSeconds(0.5f);
            Check($"ShieldGenerators off (default): {shieldName} left alone", !Keeper.IsLit(shield), "");
            Plugin.ShieldGenerators.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("ShieldGenerators on: switched, its fuel left as it was", Keeper.IsLit(shield) && shield.GetFloat("ShieldGenerator.m_fuelPerDamage", -1f) == 0f && shield.GetFloat(ZDOVars.s_fuel) == 5f,
                $"fuelPerDamage {shield.GetFloat("ShieldGenerator.m_fuelPerDamage", -1f)}, fuel {shield.GetFloat(ZDOVars.s_fuel)}");
            var shieldAt = near + new Vector3(-10f, 0f, 0f);
            shieldAt.y = ZoneSystem.instance.GetGroundHeight(shieldAt);
            shield.SetPosition(shieldAt);
            ShieldGenerator fg = null;
            for (int i = 0; i < 30 && (fg = LiveShield(shield)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("shield generator loaded in the server's game, a hit using up nothing", fg != null && fg.m_fuelPerDamage == 0f, fg == null ? "not loaded" : $"fuelPerDamage {fg.m_fuelPerDamage}");
            Plugin.ShieldGenerators.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("ShieldGenerators off again: put back", !Keeper.IsLit(shield) && shield.GetFloat("ShieldGenerator.m_fuelPerDamage", -1f) == shieldNormal,
                $"fuelPerDamage {shield.GetFloat("ShieldGenerator.m_fuelPerDamage", -1f)}, prefab {shieldNormal}");

            // 5f. lit fires' own switches: no going out from smoke, players turning them off
            var lamp = Make(torchName, at + new Vector3(48f, 0f, 0f), built: true);
            Arrive(lamp, Package(lamp));
            yield return new WaitForSeconds(0.5f);
            Check("NoSmokeChoke, PlayersCanTurnOff off (default): lit torch keeps the fire's own", Keeper.IsLit(lamp) && !lamp.GetBool("Fireplace.m_disableCoverCheck") && !lamp.GetBool("Fireplace.m_canTurnOff"), "");
            Plugin.NoSmokeChoke.Value = true; Plugin.PlayersCanTurnOff.Value = true; Keeper.SettingsChanged();
            yield return Round();
            Check("both on: lit fires switched", lamp.GetBool("Fireplace.m_disableCoverCheck") && lamp.GetBool("Fireplace.m_canTurnOff")
                && hearth.GetBool("Fireplace.m_disableCoverCheck") && !camp.GetBool("Fireplace.m_disableCoverCheck"), "");
            var lampAt = near + new Vector3(-4f, 0f, -4f);
            lampAt.y = ZoneSystem.instance.GetGroundHeight(lampAt);
            lamp.SetPosition(lampAt);
            Fireplace fl = null;
            for (int i = 0; i < 30 && (fl = Live(lamp)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("loaded in the server's game: no smoke check, can be turned off", fl != null && fl.m_disableCoverCheck && fl.m_canTurnOff && (fl.m_infiniteFuel || fl.m_secPerFuel >= 1e29f),
                fl == null ? "not loaded" : $"cover off {fl.m_disableCoverCheck}, can turn off {fl.m_canTurnOff}");
            lamp.Set(ZDOVars.s_state, 2);                // as a player turning it off
            Plugin.NoSmokeChoke.Value = false; Plugin.PlayersCanTurnOff.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("both off again: switches back, a fire left turned off is lit again", !lamp.GetBool("Fireplace.m_disableCoverCheck") && !lamp.GetBool("Fireplace.m_canTurnOff")
                && lamp.GetInt(ZDOVars.s_state, 1) == 1 && Keeper.IsLit(lamp), $"state {lamp.GetInt(ZDOVars.s_state, 1)}");

            // 5g. WardedOnly: only what stands inside a player's ward that's switched on
            var wardAt = at + new Vector3(0f, 0f, 60f);
            var ward = Make("guard_stone", wardAt, built: true);
            ward.Set(ZDOVars.s_enabled, true);
            float wardRadius = ZNetScene.instance.GetPrefab("guard_stone").GetComponent<PrivateArea>().m_radius;
            var inside = Make(torchName, wardAt + new Vector3(wardRadius * 0.5f, 0f, 0f), built: true);
            var outside = Make(torchName, wardAt + new Vector3(wardRadius + 10f, 0f, 0f), built: true);
            Plugin.WardedOnly.Value = true; Keeper.SettingsChanged();
            yield return Round();                        // a walk to find the wards, then a round of the fires
            Check("WardedOnly on: fire inside the ward lit, outside not", Keeper.IsLit(inside) && !Keeper.IsLit(outside) && !Keeper.IsLit(hearth),
                $"inside {Keeper.IsLit(inside)}, outside {Keeper.IsLit(outside)}, hearth {Keeper.IsLit(hearth)}");
            ward.Set(ZDOVars.s_enabled, false);
            Arrive(ward, Package(ward));                 // a player switching it off
            for (int i = 0; i < 40 && Keeper.IsLit(inside); i++) yield return new WaitForSeconds(0.5f);
            Check("ward switched off: its fire put back by itself", !Keeper.IsLit(inside), "");
            ward.Set(ZDOVars.s_enabled, true);
            Arrive(ward, Package(ward));
            for (int i = 0; i < 40 && !Keeper.IsLit(inside); i++) yield return new WaitForSeconds(0.5f);
            Check("ward switched on again: its fire lit by itself", Keeper.IsLit(inside), "");
            var fresh2 = Make(torchName, wardAt + new Vector3(-wardRadius * 0.5f, 0f, 0f), built: true);
            Arrive(fresh2, Package(fresh2));
            yield return new WaitForSeconds(0.5f);
            Check("fire built inside the ward lit right away", Keeper.IsLit(fresh2), "");
            made.Remove(ward);                           // gone for good: the cleanup mustn't touch its recycled object
            ward.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(ward);
            for (int i = 0; i < 40 && Keeper.IsLit(inside); i++) yield return new WaitForSeconds(0.5f);
            Check("ward torn down: its fires put back by themselves", !Keeper.IsLit(inside) && !Keeper.IsLit(fresh2), "");
            Plugin.WardedOnly.Value = false; Keeper.SettingsChanged();
            yield return Round();
            Check("WardedOnly off: every fire lit again", Keeper.IsLit(inside) && Keeper.IsLit(outside) && Keeper.IsLit(hearth) && Keeper.IsLit(torch), "");

            // 6. settings: a fire type switched off is put back; ExcludeFires; Enabled off puts everything back
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
            foreach (var z in made) allBack &= !Keeper.IsLit(z) && !z.GetBool("Fireplace.m_infiniteFuel") && z.GetFloat("Fireplace.m_secPerFuel") < 1e29f && !z.GetString("Fireplace.m_name").Contains("never runs out");
            Check("Enabled off: every fire put back to normal", allBack, $"{Fields(torch)} / {Fields(camp)}");
            int smelterMax = ZNetScene.instance.GetPrefab("smelter").GetComponent<Smelter>().m_maxFuel;
            Check("Enabled off: hot tub needs wood again", tub.GetInt("Smelter.m_maxFuel", -1) == tubMax, $"maxFuel {tub.GetInt("Smelter.m_maxFuel", -1)}");
            Check("Enabled off: ovens burn wood again", oven.GetInt("CookingStation.m_secPerFuel") == ovenSec && stocked.GetInt("CookingStation.m_secPerFuel") == ovenSec, $"secPerFuel {oven.GetInt("CookingStation.m_secPerFuel")}");
            int refMax = ZNetScene.instance.GetPrefab("eitrrefinery").GetComponent<Smelter>().m_maxFuel;
            Check("Enabled off: eitr refinery needs sap again", refinery.GetInt("Smelter.m_maxFuel", -1) == refMax, $"maxFuel {refinery.GetInt("Smelter.m_maxFuel", -1)}, prefab {refMax}");
            Check("Enabled off: smelter needs coal again", smelter.GetInt("Smelter.m_maxFuel", -1) == smelterMax, $"maxFuel {smelter.GetInt("Smelter.m_maxFuel", -1)}, prefab {smelterMax}");

            // 7. reloaded after being put back, the game burns it normally again
            torch.SetPosition(at);
            yield return new WaitForSeconds(3f);
            torch.SetPosition(near + new Vector3(-4f, 0f, 0f));
            for (int i = 0; i < 30 && (ft = Live(torch)) == null; i++) yield return new WaitForSeconds(0.5f);
            Check("put-back torch runs as normal when it loads again (no infinite fuel, its own burn rate and name)", ft != null && !ft.m_infiniteFuel && ft.m_secPerFuel == torchSec && ft.m_name == torchLabel,
                ft == null ? "not loaded" : $"infinite {ft.m_infiniteFuel}, secPerFuel {ft.m_secPerFuel}, name {ft.m_name}");

            Check("rounds stay light (slowest frame under 5 ms, every round)", slowest < 5.0, $"{slowest:F2} ms");
        }

        // What the settings' descriptions say about the game's own pieces, checked against this
        // version of the game: a game update that changes one shows up here.
        private static void GameFacts()
        {
            Smelter S(string n) => ZNetScene.instance.GetPrefab(n)?.GetComponent<Smelter>();
            CookingStation C(string n) => ZNetScene.instance.GetPrefab(n)?.GetComponent<CookingStation>();
            string Fuel(ItemDrop i) => i ? i.gameObject.name : "none";
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (!go) continue;
                if (go.GetComponent<Smelter>() is Smelter s)
                    ZLog.Log($"Everlit TEST fact: {go.name}: smelter, fuel {Fuel(s.m_fuelItem)} max {s.m_maxFuel} per product {s.m_fuelPerProduct}, maxOre {s.m_maxOre}, {s.m_secPerProduct} s each, stack {s.m_spawnStack}, " +
                        string.Join(" ", s.m_conversion.Select(c => $"{(c.m_from ? c.m_from.gameObject.name : "nothing")}->{(c.m_to ? c.m_to.gameObject.name : "?")}")));
                else if (go.GetComponent<CookingStation>() is CookingStation c && c.m_useFuel)
                    ZLog.Log($"Everlit TEST fact: {go.name}: oven, fuel {Fuel(c.m_fuelItem)} max {c.m_maxFuel}, {c.m_secPerFuel} s per piece");
                else if (go.GetComponent<Fireplace>() is Fireplace f && !f.m_infiniteFuel && f.m_secPerFuel > 0f && f.m_fuelItem)
                    ZLog.Log($"Everlit TEST fact: {go.name}: fire, fuel {Fuel(f.m_fuelItem)} max {f.m_maxFuel}, {f.m_secPerFuel} s per piece, wet check {(f.m_enabledObjectLow && f.m_enabledObjectHigh)}, coverOff {f.m_disableCoverCheck}, canTurnOff {f.m_canTurnOff}");
                else if (go.GetComponent<ShieldGenerator>() is ShieldGenerator g)
                    ZLog.Log($"Everlit TEST fact: {go.name}: shield, fuel {string.Join("/", g.m_fuelItems.Select(Fuel))} max {g.m_maxFuel}, per damage {g.m_fuelPerDamage}, attack {g.m_enableAttack} after {g.m_attackChargeTime} s");
            }
            var tub = S("piece_bathtub"); var kiln = S("piece_FrostKiln"); var charcoal = S("charcoal_kiln");
            Check("fact: hot tub burns wood, takes nothing in, makes nothing", tub && Fuel(tub.m_fuelItem) == "Wood" && tub.m_maxOre == 0 && tub.m_conversion.Count == 0, "");
            Check("fact: smelter and blast furnace burn coal", Fuel(S("smelter")?.m_fuelItem) == "Coal" && Fuel(S("blastfurnace")?.m_fuelItem) == "Coal", "");
            Check("fact: eitr refinery burns sap", Fuel(S("eitrrefinery")?.m_fuelItem) == "Sap", "");
            Check("fact: frost kiln burns ice, takes nothing else, makes one piece every 30 s, unstacked", kiln && Fuel(kiln.m_fuelItem) == "Ice" && kiln.m_maxOre == 0
                && kiln.m_conversion.Count == 1 && kiln.m_conversion[0].m_from == null && kiln.m_secPerProduct == 30f && !kiln.m_spawnStack,
                kiln ? $"fuel {Fuel(kiln.m_fuelItem)}, maxOre {kiln.m_maxOre}, {kiln.m_secPerProduct} s, stack {kiln.m_spawnStack}" : "missing");
            Check("fact: charcoal kiln, spinning wheel, windmill burn no fuel", charcoal && charcoal.m_maxFuel == 0 && S("piece_spinningwheel")?.m_maxFuel == 0 && S("windmill")?.m_maxFuel == 0, "");
            Check("fact: the limits' defaults are the game's own (kiln 25 wood, frost kiln 25 ice, smelter and blast furnace 10 ore / 20 coal)",
                charcoal && charcoal.m_maxOre == (int)Plugin.CharcoalKilnMaxWood.DefaultValue && kiln && kiln.m_maxFuel == (int)Plugin.FrostKilnMaxIce.DefaultValue
                && S("smelter")?.m_maxOre == (int)Plugin.SmelterMaxOre.DefaultValue && S("smelter")?.m_maxFuel == (int)Plugin.SmelterMaxCoal.DefaultValue
                && S("blastfurnace")?.m_maxOre == (int)Plugin.BlastFurnaceMaxOre.DefaultValue && S("blastfurnace")?.m_maxFuel == (int)Plugin.BlastFurnaceMaxCoal.DefaultValue, "");
            Check("fact: stone oven burns wood, frost foundry frozen fuel", Fuel(C("piece_oven")?.m_fuelItem) == "Wood" && Fuel(C("piece_FrostFoundry")?.m_fuelItem) == "FrozenFuel", "");
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

        private static string Fields(ZDO z) => $"lit {Keeper.IsLit(z)}, infinite {z.GetBool("Fireplace.m_infiniteFuel")}, secPerFuel {z.GetFloat("Fireplace.m_secPerFuel", -1f)}, name {z.GetString("Fireplace.m_name")}, HasFields {z.GetBool("HasFields")}";

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

        // what a loaded object got: the data on it, and whether the game applied it (and how often)
        private static readonly Dictionary<ZDOID, int> loads = new Dictionary<ZDOID, int>();
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.LoadFields))]
        private static class Loads
        {
            private static void Postfix(ZNetView __instance)
            {
                var z = __instance.GetZDO();
                if (z != null && made.Contains(z)) loads[z.m_uid] = loads.TryGetValue(z.m_uid, out int n) ? n + 1 : 1;
            }
        }
        private static string Loaded(ZDO z, string field)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return $"zdo HasFields {z.GetBool("HasFields")}, HasFields{field.Split('.')[0]} {z.GetBool("HasFields" + field.Split('.')[0])}, {field} int {z.GetInt(field, -1)}; " +
                $"instance {(v ? v.name : "none")}, same zdo {(v && v.GetZDO() == z)}, loaded {(loads.TryGetValue(z.m_uid, out int n) ? n : 0)}x, owner {z.GetOwner()}, me {ZDOMan.GetSessionID()}";
        }

        private static Fireplace Live(ZDO z)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return v ? v.GetComponent<Fireplace>() : null;
        }

        private static Smelter LiveSmelter(ZDO z)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return v ? v.GetComponent<Smelter>() : null;
        }

        private static CookingStation LiveOven(ZDO z)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return v ? v.GetComponent<CookingStation>() : null;
        }

        private static ShieldGenerator LiveShield(ZDO z)
        {
            var v = ZNetScene.instance.FindInstance(z);
            return v ? v.GetComponent<ShieldGenerator>() : null;
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
