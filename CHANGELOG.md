# Changelog

## 1.0.0

First release.

* Fires players build never run out of fuel: campfires, bonfires, hearths, torches, braziers and candles.
* Uses the game's own infinite-fuel switch, set per fire, which every player's game applies by itself.
* The world's own fire pits are left alone (`PlayerBuiltOnly`).
* `Furnaces` (off by default): smelters and blast furnaces smelt without coal.
* Switches per fire type, and `ExcludeFires` for single types.
* New fires are switched as they're built; one walk of the world after it loads, no sweeping after that.
* `Enabled = false` puts every fire back to normal.
* Settings take effect when the file is saved, no restart.
* Server only: players need nothing.
