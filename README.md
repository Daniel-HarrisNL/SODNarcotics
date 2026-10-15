# Procedural Narcotics Cases

A new drug is on the streets. Catching the dealer will not be enough, you must bring the whole network down. Find the supplier and bring them to justice. 

## Requirements

- **Required:** the BepInEx pack for IL2CPP (mod managers install this for you automatically).
- **Recommended:** BepInExConfigManager, an in-game settings overlay you open with the `` ` `` (backquote) key. (If it does not open, that overlay's own toggle may have defaulted to F5; you can rebind it to backquote in its settings.)

## Installation

Mod manager (r2modman or the Thunderstore Mod Manager): install the mod and launch through the manager. Get it here: [Procedural Narcotics Cases on Thunderstore](https://thunderstore.io/c/shadows-of-doubt/p/opi/Procedural_Narcotics_Cases/)
Manual install works too: put the plugin in `BepInEx/plugins`.

**Works with existing saves.**

STOP HERE - that is everything you need to play.

---

## Beta notes

This is a beta, it has been tested but not extensively for all edge cases. The timings (see Options section below) felt best for me and best for picking up events on CCTV cameras. 

I am very open to improvements so please send me your feedback and suggestions


## How a narcotics operation works (Spoilers)

- **Cook** works shifts in a hidden lab.
- **Distributor** moves the drug between the lab and dead drops.
- **Dealer** collects from the drop and sells to the public.
- **Buyer** overdoses and becomes arrested for possession, and the objective to search them is given to the player.

## Options

Settings editable with BepInExConfigManager:

- **Hijack Chance** - probability to start a narcotics case.
- **Cadence** - time between cook shifts, and shift run duration.
- **Pacing** - chain timings for dead drop, cook time, dealing, time between sales, etc.


## License

MIT. See LICENSE.
