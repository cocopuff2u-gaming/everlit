using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace Everlit
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.cocopuff2u.everlit";
        public const string NAME = "Everlit";
        public const string VERSION = "1.2.0";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> PlayerBuiltOnly, WardedOnly;
        internal static ConfigEntry<bool> Campfires, Bonfires, Hearths, Torches, Braziers, Candles, OtherFires, HotTubs, Ovens, EitrRefinery, Smelters, BlastFurnaces, FrostKiln, CharcoalKiln, FrostFoundry, ShieldGenerators;
        internal static ConfigEntry<bool> NoSmokeChoke, PlayersCanTurnOff, HoverInfo;
        internal static ConfigEntry<string> HoverNote;
        internal static ConfigEntry<int> CharcoalKilnMaxWood, FrostKilnMaxIce, SmelterMaxOre, SmelterMaxCoal, BlastFurnaceMaxOre, BlastFurnaceMaxCoal;
        internal static ConfigEntry<string> ExcludeFires;
        internal static ConfigEntry<bool> LogEachFire;
        private FileSystemWatcher watcher;
        private volatile bool changed;
        private float reloadAt;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true,
                "Keep fires lit. Turning this off puts every fire Everlit lit back to normal (burning fuel) on the next round; do that and let a round run before removing the mod, or those fires stay lit for good");
            PlayerBuiltOnly = Config.Bind("General", "PlayerBuiltOnly", true,
                "Only fires a player built. Off: the world's own fire pits too (abandoned camps, villages), which then light up and stay lit");
            WardedOnly = Config.Bind("General", "WardedOnly", false,
                "Only fires and stations inside a player's ward that's switched on. Ones outside stay normal; building, switching or tearing down a ward changes the fires around it a few seconds later");
            Campfires = Config.Bind("Fires", "Campfires", true, "Campfires and the iron fire pit");
            Bonfires = Config.Bind("Fires", "Bonfires", true, "Bonfires");
            Hearths = Config.Bind("Fires", "Hearths", true, "Hearths");
            Torches = Config.Bind("Fires", "Torches", true, "Standing and wall torches, jack-o-turnips and lanterns");
            Braziers = Config.Bind("Fires", "Braziers", true, "Standing and hanging braziers");
            Candles = Config.Bind("Fires", "Candles", true, "Resin candles");
            OtherFires = Config.Bind("Fires", "OtherFires", true, "Any other fire that burns fuel, including ones added by other mods");
            HotTubs = Config.Bind("Fires", "HotTubs", true,
                "Hot tubs stay hot without wood. Wood already in one stays there, and can't be added while this is on");
            Ovens = Config.Bind("Fires", "Ovens", true,
                "Stone ovens never run out of wood. An empty one is given just under one piece (tearing it down drops none)");
            Smelters = Config.Bind("Fires", "Smelters", false,
                "Smelters (and any modded furnace that burns coal) smelt without coal. Coal already in one stays there, and can't be added while this is on");
            BlastFurnaces = Config.Bind("Fires", "BlastFurnaces", false,
                "Blast furnaces smelt without coal. Coal already in one stays there, and can't be added while this is on");
            Furnaces();
            EitrRefinery = Config.Bind("Fires", "EitrRefinery", true,
                "The eitr refinery refines without sap. Sap already in one stays there, and can't be added while this is on");
            FrostKiln = Config.Bind("Fires", "FrostKiln", false,
                "The frost kiln runs without ice. It takes nothing else in, so it makes frozen fuel out of nothing: a piece every 30 s while a player is near it, and up to an hour's worth at once when someone comes back");
            CharcoalKiln = Config.Bind("Fires", "CharcoalKiln", false,
                "Charcoal kilns make coal without anyone putting wood in. The game has no switch for that, so Everlit puts one piece of wood in a kiln each time it runs empty (a piece of coal every 15 s while a player is near it). Tearing a kiln down drops that one piece");
            FrostFoundry = Config.Bind("Fires", "FrostFoundry", false,
                "The frost foundry never runs out of frozen fuel. An empty one is given just under one piece (tearing it down drops none)");
            ShieldGenerators = Config.Bind("Fires", "ShieldGenerators", false,
                "Shield generators use up no fuel when their shield is hit, so the shield never breaks. It still needs fuel put in once to switch on, its size still goes by how much is in it, and setting off its charged blast still empties it");
            var ore = new AcceptableValueRange<int>(1, 100);
            var fuel = new AcceptableValueRange<int>(1, 500);
            CharcoalKilnMaxWood = Config.Bind("Capacity", "CharcoalKilnMaxWood", 25, new ConfigDescription(
                "How much wood a charcoal kiln takes (the game's own and the default: 25)", ore));
            FrostKilnMaxIce = Config.Bind("Capacity", "FrostKilnMaxIce", 25, new ConfigDescription(
                "How much ice a frost kiln takes (the game's own and the default: 25). Has no effect while FrostKiln is on: it then takes none", fuel));
            SmelterMaxOre = Config.Bind("Capacity", "SmelterMaxOre", 10, new ConfigDescription(
                "How much ore a smelter takes (the game's own and the default: 10)", ore));
            SmelterMaxCoal = Config.Bind("Capacity", "SmelterMaxCoal", 20, new ConfigDescription(
                "How much coal a smelter takes (the game's own and the default: 20). Has no effect while Smelters is on: it then takes none", fuel));
            BlastFurnaceMaxOre = Config.Bind("Capacity", "BlastFurnaceMaxOre", 10, new ConfigDescription(
                "How much ore a blast furnace takes (the game's own and the default: 10)", ore));
            BlastFurnaceMaxCoal = Config.Bind("Capacity", "BlastFurnaceMaxCoal", 20, new ConfigDescription(
                "How much coal a blast furnace takes (the game's own and the default: 20). Has no effect while BlastFurnaces is on: it then takes none", fuel));
            HoverInfo = Config.Bind("Lit fires", "HoverInfo", true,
                "Lit fires show the game's usual text when a player looks at them: their name with HoverNote after it, and their fuel, always full (adding more says so). Off: they show nothing, as the game does for a fire that never runs out");
            HoverNote = Config.Bind("Lit fires", "HoverNote", "(never runs out)",
                "What follows a lit fire's name with HoverInfo on (the same for every player, whatever their game's language). Empty: just the name");
            NoSmokeChoke = Config.Bind("Lit fires", "NoSmokeChoke", false,
                "Lit fires don't go out when their smoke can't get away (indoors, under a roof), with something solid right above them, or partly buried. The smoke still fills the room and still hurts");
            PlayersCanTurnOff = Config.Bind("Lit fires", "PlayersCanTurnOff", false,
                "Players can turn lit fires off and on again by using them (no on-screen prompt for it: with HoverInfo on, the fire shows its usual add-fuel prompt; off, it shows nothing). The game's own rule for such fires: rain with no roof over it, or strong wind with little cover, turns one off, and it stays off until someone lights it again");
            ExcludeFires = Config.Bind("Fires", "ExcludeFires", "",
                "Fires to leave alone, by prefab name, comma separated (e.g. \"piece_walltorch, bonfire\"). Wins over the switches above");
            LogEachFire = Config.Bind("Log", "LogEachFire", false, "A log line for every fire lit or put back to normal: what it is and where");
            new Harmony(GUID).PatchAll(Assembly.GetExecutingAssembly());
            WatchConfig();
            Logger.LogInfo($"{NAME} {VERSION} loaded");
        }

        // 1.1 had one "Furnaces" switch for both; a file that has it switched on keeps both on
        private void Furnaces()
        {
            var old = new ConfigDefinition("Fires", "Furnaces");
            var entry = Config.Bind(old, false);
            if (entry.Value) { Smelters.Value = true; BlastFurnaces.Value = true; }
            Config.Remove(old);
            Config.Save();
        }

        private void Update()
        {
            ReloadIfChanged();
            Keeper.Update();
#if SELFTEST
            Tests.Update(this);
#endif
        }

        // settings changed in the file take effect a moment later, no restart needed
        private void WatchConfig()
        {
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.ConfigFilePath), Path.GetFileName(Config.ConfigFilePath));
                watcher.Changed += (s, e) => changed = true;
                watcher.Created += (s, e) => changed = true;
                watcher.Renamed += (s, e) => changed = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                watcher.EnableRaisingEvents = true;
            }
            catch (System.Exception e) { Logger.LogWarning("Settings won't reload by themselves: " + e.Message); }
        }

        private void ReloadIfChanged()
        {
            if (changed) { changed = false; reloadAt = UnityEngine.Time.time + 1f; }
            if (reloadAt == 0f || UnityEngine.Time.time < reloadAt) return;
            reloadAt = 0f;
            try
            {
                // (without saving the file back, which would only start another reload)
                bool save = Config.SaveOnConfigSet;
                Config.SaveOnConfigSet = false;
                try { Config.Reload(); } finally { Config.SaveOnConfigSet = save; }
                Keeper.SettingsChanged();
                Logger.LogInfo("Settings reloaded");
            }
            catch (System.Exception e) { Logger.LogWarning("Settings not reloaded: " + e.Message); }
        }
    }
}
