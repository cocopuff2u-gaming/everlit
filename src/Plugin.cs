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
        public const string VERSION = "1.0.0";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> PlayerBuiltOnly;
        internal static ConfigEntry<bool> Campfires, Bonfires, Hearths, Torches, Braziers, Candles, OtherFires;
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
            Campfires = Config.Bind("Fires", "Campfires", true, "Campfires and the iron fire pit");
            Bonfires = Config.Bind("Fires", "Bonfires", true, "Bonfires");
            Hearths = Config.Bind("Fires", "Hearths", true, "Hearths");
            Torches = Config.Bind("Fires", "Torches", true, "Standing and wall torches, jack-o-turnips and lanterns");
            Braziers = Config.Bind("Fires", "Braziers", true, "Standing and hanging braziers");
            Candles = Config.Bind("Fires", "Candles", true, "Resin candles");
            OtherFires = Config.Bind("Fires", "OtherFires", true, "Any other fire that burns fuel, including ones added by other mods");
            ExcludeFires = Config.Bind("Fires", "ExcludeFires", "",
                "Fires to leave alone, by prefab name, comma separated (e.g. \"piece_walltorch, bonfire\"). Wins over the switches above");
            LogEachFire = Config.Bind("Log", "LogEachFire", false, "A log line for every fire lit or put back to normal: what it is and where");
            new Harmony(GUID).PatchAll(Assembly.GetExecutingAssembly());
            WatchConfig();
            Logger.LogInfo($"{NAME} {VERSION} loaded");
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
