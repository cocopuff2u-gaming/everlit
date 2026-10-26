# Changelog

## 1.1.0

* Hot tubs stay hot without wood (`HotTubs`, on). Asked for in [#1](https://github.com/cocopuff2u-gaming/everlit/issues/1).
* Stone ovens never run out of wood (`Ovens`, on).
* The eitr refinery refines without sap (`EitrRefinery`, on).
* `FrostKiln` and `FrostFoundry` (both off): the frost kiln runs without ice (so it makes frozen fuel out of nothing) and the frost foundry never runs out of frozen fuel.
* Off by default: `WardedOnly` (only inside players' wards that are switched on), `ShieldGenerators` (a hit uses up no fuel, so the shield never breaks), `NoSmokeChoke` (lit fires don't go out when smoke can't get away) and `PlayersCanTurnOff` (players can turn lit fires off and on).
* Settings changed while a round is running now get a round of their own afterwards.
* None of them is handed free fuel: what's in one stays, and tearing it down gives back only that.

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
