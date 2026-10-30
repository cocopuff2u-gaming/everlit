# Changelog

## 1.2.0

* Lit fires show hover text again (`HoverInfo`, on): their name with a note after it (`HoverNote`, "(never runs out)") and their fuel, always full. They now burn their wood so slowly that it lasts for good, rather than using the game's infinite fuel, which shows no text at all; `HoverInfo = false` goes back to that. Asked for in [#2](https://github.com/cocopuff2u-gaming/everlit/issues/2).
* `CharcoalKiln` (off): charcoal kilns make coal without anyone putting wood in. The game has no switch for this, so Everlit puts one piece of wood in a kiln each time it runs empty (tearing it down drops that piece).
* Capacity settings: how much wood a charcoal kiln, ice a frost kiln, and ore and coal a smelter or blast furnace takes. Each defaults to the game's own number, so nothing changes until raised.
* `Furnaces` is split in two: `Smelters` and `BlastFurnaces` (both off). A settings file with `Furnaces = true` keeps both on.
* Setting descriptions corrected: rain only turns a fire off (`PlayersCanTurnOff`) when there's no roof over it, and a shield generator's charged blast still empties it (`ShieldGenerators`).

## 1.1.1

* README and setting descriptions corrected: hover text on switched hot tubs and stations, what `NoSmokeChoke` and `PlayersCanTurnOff` do (rain or strong wind turns such a fire off; there's no on-screen prompt), and how much the frost kiln makes. No change in what the mod does.

## 1.1.0

* Hot tubs stay hot without wood (`HotTubs`, on). Asked for in [#1](https://github.com/cocopuff2u-gaming/everlit/issues/1).
* Stone ovens never run out of wood (`Ovens`, on).
* The eitr refinery refines without sap (`EitrRefinery`, on).
* `FrostKiln` and `FrostFoundry` (both off): the frost kiln runs without ice (so it makes frozen fuel out of nothing) and the frost foundry never runs out of frozen fuel.
* Off by default: `WardedOnly` (only inside players' wards that are switched on), `ShieldGenerators` (a hit uses up no fuel, so the shield never breaks), `NoSmokeChoke` (lit fires don't go out when smoke can't get away) and `PlayersCanTurnOff` (players can turn lit fires off and on).
* Settings changed while a round is running now get a round of their own afterwards.
* None of them is handed fuel a player could get back: what's in one stays, and tearing it down gives back only that. (`FrostKiln`, when on, makes frozen fuel out of nothing.)

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
