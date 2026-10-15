# Narcotics

*A vanilla+ investigation mod for Shadows of Doubt*

A new drug has appeared on the streets. Somewhere in the city an ordinary citizen has quietly turned to cooking it in a hidden lab, handing it off through dead drops to dealers who sell it hand to hand. When a user overdoses and is picked up, the case lands on your scanner. Work the arrests, the cameras and the physical evidence back up the chain, through the dealers and the courier to the lab, and bring in the cook.

Narcotics takes over the slot the base game would have used for a murder (how often is up to you, from never to always), so it slots into the normal flow of cases rather than piling on top of it.

## How it plays

A narcotics operation runs as a living supply chain:

- **The cook** works a shift in a concealed lab, producing a uniquely named designer drug.
- **A courier** ferries each dose to a dead drop, a real searchable bin out on a camera-covered street.
- **A dealer** collects from the drop and sells hand to hand to buyers in public.
- **A buyer** overdoses a while later and is arrested, and the case opens on your scanner.

From there it is an ordinary Shadows of Doubt investigation, worked with the tools you already know:

- **Dust the drugs** for fingerprints to put names to the people who handled them.
- **Pull CCTV** to catch a sale or a dead-drop exchange on camera.
- **Tail** a dealer to their drop, and the courier back toward the lab.
- **Hone in** on the lab, identify the cook, and make the arrest.

You close the case at City Hall by naming the people and places in the chain: who was producing, that they have been arrested, where the lab is, who was transporting, and who was dealing.

## Requirements

- **Required:** the BepInEx pack for IL2CPP (mod managers install this for you automatically).
- **Recommended:** BepInExConfigManager, an in-game settings overlay you open with the `` ` `` (backquote) key, so you can tune the options below without restarting.

## Installation

Easiest is a mod manager (r2modman or the Thunderstore Mod Manager): install the mod and launch through the manager. Manual install works too: drop `Narcotics.dll` into `BepInEx/plugins`.

## Options

All settings live in the config (`com.benhirsh.narcotics.cfg`), editable live with BepInExConfigManager. The ones most worth knowing:

- **Hijack Chance** - how often a would-be murder becomes a narcotics case instead, from `0` (never, pure vanilla) to `1` (every time). A slider.
- **Cadence** - how long between cook shifts, and how long a shift runs.
- **Pacing** - the dwell times along the chain (dead-drop stash and collect, the deal itself) and how long after an arrest the next sale happens, so you can make tailing faster or slower to taste.

## Beta notes

This is a beta. The full investigation loop is in and playable end to end, but it is still being tuned and polished, so expect the occasional rough edge. Feedback is welcome.

A few drug items (the thinner props) carry their fingerprint data but can't be developed with the dusting kit, so for those the trail is worked through cameras, tailing and interrogation rather than prints.

## License

MIT. See LICENSE.
