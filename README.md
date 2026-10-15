<p align="center"><img src="https://raw.githubusercontent.com/cocopuff2u-gaming/everlit/main/package/icon.png" alt="Everlit" width="160"></p>

<h1 align="center">Everlit</h1>

<p align="center"><b>Fires and torches your players build never run out of fuel.</b></p>

Torches, campfires, hearths, braziers and candles in Valheim burn fuel, and
sooner or later somebody has to walk around the base with a stack of resin.
Install Everlit on your dedicated server and every fire a player builds stays lit
for good. The fire pits that come with the world, at abandoned camps and villages,
stay as they are.

Your players don't install anything. It runs only on the server.

---

## What you get

| | |
|---|---|
| **Fires that stay lit** | Every fire a player built burns forever, without using fuel. Nobody has to refill a torch again. |
| **Only player-built fires** | The world's own fire pits, at abandoned camps and villages, stay dark the way the game made them. Switch `PlayerBuiltOnly` off if you want those lit too. |
| **Pick which fires** | Switches for campfires, bonfires, hearths, torches, braziers and candles, and a list for single fire types to leave alone. |
| **Furnaces, if you want** | Off by default. Switch `Furnaces` on and smelters and blast furnaces smelt without coal, using the game's own "needs no fuel" setting that the spinning wheel and windmill run on. |
| **New fires too** | A fire is switched over as soon as it's built. |
| **The game's own feature** | Valheim already has fires that never run out (the traders' fire pits). Everlit switches that same feature on for your players' fires, using a per-object setting every player's game applies by itself. Nothing is faked, and no fuel is added over and over. |
| **Easy to undo** | Turn `Enabled` off and every fire Everlit lit goes back to burning fuel. |
| **Light on your server** | One walk of the world a minute after it loads, spread out so no frame does more than a millisecond or two of work. After that Everlit only reacts to fires as players build them; it doesn't sweep the world again. |

---

## Install

You need a Valheim **dedicated server** with **BepInEx** on it
([BepInExPack for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)).
Most server hosts have a one-click option for BepInEx.

1. **Download** `Everlit-<version>.zip` from
   [Releases](https://github.com/cocopuff2u-gaming/everlit/releases/latest).
2. **Unzip it.** Inside is a `plugins/Everlit` folder.
3. **Copy** the `Everlit` folder into your server's `BepInEx/plugins` folder,
   so you end up with:

   ```
   <server>/BepInEx/plugins/Everlit/Everlit.dll
   ```

4. **Start the server.** In the server log you should see:

   ```
   Everlit 1.0.0 loaded
   ```

   and, a minute after the world loads:

   ```
   Everlit: 86 fires kept lit (11 campfire, 1 bonfire, 8 hearth, 62 torch, 4 brazier); 86 newly lit, ...
   ```

### Removing it

The switch Everlit turns on is saved with each fire, so it stays even without the
mod. To go back to normal fires, set `Enabled = false`, wait for the log line saying
the fires were put back (a few seconds), and only then remove the mod.

---

## Settings

Settings live in `BepInEx/config/com.cocopuff2u.everlit.cfg`. The file appears
after the first start. Edit it any time: Everlit picks up the change a second after
you save, and says `Settings reloaded` in the log.

| Setting | Everlit default | Vanilla game | What it does |
|---|---|---|---|
| `Enabled` | `true` | fires burn fuel | Keep fires lit. Off puts every fire Everlit lit back to normal. |
| `PlayerBuiltOnly` | `true` | | Only fires a player built. Off: the world's own fire pits too, which then light up and stay lit. |
| `Campfires` | `true` | | Campfires and the iron fire pit. |
| `Bonfires` | `true` | | Bonfires. |
| `Hearths` | `true` | | Hearths. |
| `Torches` | `true` | | Standing and wall torches, jack-o-turnips and lanterns. |
| `Braziers` | `true` | | Standing and hanging braziers. |
| `Candles` | `true` | | Resin candles. |
| `OtherFires` | `true` | | Any other fire that burns fuel, including ones added by other mods. |
| `Furnaces` | `false` | furnaces burn coal | Smelters and blast furnaces (and modded furnaces that burn coal) smelt without coal. Coal already in one stays there; no more can be added while it's on. |
| `ExcludeFires` | (empty) | | Fire types to leave alone, by prefab name, comma separated, e.g. `piece_walltorch, bonfire` (furnaces: `smelter`, `blastfurnace`). Wins over the switches. |
| `LogEachFire` | `false` | | A log line for every fire lit or put back: what it is and where. |

A blank means the game has nothing like it.

---

## Good to know

* **A fire someone is standing next to** switches over the next time it loads for
  them, after they walk away and come back or log in again. Until then Everlit gives
  it a full load of fuel, so it can't go out in the meantime. The log counts these
  as "still used fuel in a player's game" until they've switched.
* **Fires still go out the way the game puts them out:** under water, or choked by
  their own smoke under a roof with nowhere for it to go. Those don't use fuel.
* **Adding fuel.** A lit fire won't take wood or resin any more, the same as the
  traders' fire pits.
* **Smelters, kilns, furnaces and hot tubs** aren't fires to the game and aren't
  touched.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Nothing in the log about Everlit | BepInEx isn't running. Check that `BepInEx/LogOutput.log` exists and the server starts through BepInEx. |
| A torch near me still burns down | It switches the next time it loads for you: walk out of the area and back, or log out and in. |
| An old fire pit at a camp didn't light | That's on purpose: only fires players built. Set `PlayerBuiltOnly = false` to light those too. |

Still stuck? [Open an issue](https://github.com/cocopuff2u-gaming/everlit/issues)
and attach `BepInEx/LogOutput.log`.

---

## More

* [CHANGELOG.md](CHANGELOG.md): what changed in each version.
* **Building from source**: `./build.sh --managed <server>/valheim_server_Data/Managed`
  (needs the .NET SDK 8+). The zip lands in `dist/`.
* **Self-test**: build with `dotnet build src/Everlit.csproj -c Release -p:SelfTest=true`,
  put that DLL on a test server, create an empty file `BepInEx/config/everlit.selftest`
  and start the server. It builds fires out at sea and near the middle of the world,
  has the server's own game load them to watch them burn (or not) over three days of
  world time, tries every setting, logs `Everlit TEST PASS` or `FAIL` for each, then
  removes what it made. The normal build has none of this in it.

## Credits

Everlit is made by [Cody Keats](https://github.com/cocopuff2u), who also makes
[Munin Map](https://github.com/cocopuff2u-gaming/munin-map),
[Regrove](https://github.com/cocopuff2u-gaming/regrove) and
[Unmarked](https://github.com/cocopuff2u-gaming/unmarked).

## Licence

MIT. See [LICENSE](LICENSE).
