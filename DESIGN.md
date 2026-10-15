# Narcotics: design

Status: Phase 0/1 verified in-game; Phase 2 (lab + cook pin) verified; Phase 3 supply chain rebuilt as a scalable tier chain with physical searchable doses (built + compiles + deployed, awaiting in-game test). See "Progress + verified APIs" and "Scalable tier chain + physical doses" below.

A standalone Shadows of Doubt mod (IL2CPP, BepInEx6 + HarmonyX + Il2CppInterop), separate project and Thunderstore package from the author's "Better Leads and Motives" mod, which is only the reference for toolchain and patterns.

## The vision

A new drug appears on the streets. The player uncovers a supply chain and busts the lab behind it.

The simulation, upstream of the player, runs one drug operation at a time:

1. **Cook.** One NPC becomes the cook. They stop showing up to their normal job and set up a secret **lab** in a **vacant address** (or in their own home if they live alone), dressed with lab-style furniture.
2. **Product.** The cook produces a drug: a re-skinned vanilla item (the "beta blockers" object) renamed to a mystery drug (Mystery Drug A, then B, C for successive operations).
3. **Distribution.** The cook periodically walks the streets and hands several doses to individual **dealers**, who are always **homeless** NPCs.
4. **Dealing.** Dealers roam the streets and sell doses to random NPCs they pass.
5. **Use.** A **buyer** periodically consumes a dose. If an **enforcer** with line of sight sees them use it, the enforcer **arrests** the buyer.
6. **Case.** The arrest opens a case on the player's **scanner**, prompting them to investigate the arrest scene.
7. **The trail.** Questioning the arrested buyer does NOT reveal the source. **Physical evidence and CCTV do.** The player traces buyer -> dealer -> cook / lab.
8. **Geography.** Arrests cluster near the lab. As more drug arrests appear, the player can **hone in on the area** of the city the lab is in.
9. **Resolution.** The player finds and busts the lab / arrests the cook. That ends the operation. A new one (next drug) can begin later.

A supporting feature: a new dialog option to **ask an NPC about an item** they are carrying or have equipped.

## Locked decisions (user, 2026-09-28)

- **Resolution = bust the lab.** ONE long-running investigation, not a payable side case per arrest. Individual arrests are leads. Goal and reward is tracing the trail to the lab and taking down the cook.
- **Work-absence clue = subtle.** Coworkers reveal the cook stopped coming to work ONLY if the player specifically asks, phrased vaguely. A lead to dig for, not a giveaway.
- **Scale = one operation at a time.** A single lab / cook / drug active city-wide; the next only starts after the current ends. Keeps arrests clustered for the geographic hone-in.
- **Name = Narcotics.** Package `Narcotics`; in-game name `Narcotics`; GUID `com.opi.narcotics`; folder `workspace\Narcotics`.

## Architecture (from recon)

**Host: a single custom `Case` (`Case.CaseType.custom`).** Created programmatically at the first arrest via `CasePanelController.Instance.CreateNewCase(CaseType.custom, CaseStatus.handInNotCollected, isSilent:false, "New Drug on the Street")`, delivered to the player with an explicit `InterfaceController.Instance.NewGameMessage(GameMessageType.notification, ...)` (the "scanner" pop), and driven with `Case.AddObjective(Objective.ObjectiveTrigger(...))` plus `Case.ResolveQuestion` leads.

- NOT SideJob: it is JobPreset-ScriptableObject-driven and phone-offered with accept/decline, the wrong delivery model for an auto-start-on-arrest case; new job types need a bespoke asset or an injected subclass the game would never spawn itself.
- NOT the murder engine: it is death-shaped (killed victim, body, weapon, cause of death). An arrest is not a death.
- A branching follow-the-trail chain (buyer -> dealer -> cook) IS expressible on a raw Case: `ObjectiveTriggerType` has `exploreCrimeScene`, `surveillanceFlagFootage`, `findSurveillanceWith`, `findFingerprintsOnObject`, `identifyFinerprints`, `goToAddress`; `OnCompleteAction.nextChapterPart` chains phases; `ResolveQuestion.InputType` has `citizen`, `location`, `item`, `arrestPurp`.

**One persistent master case** accumulates a pinned evidence card and a location lead per arrest, so arrest `EvidenceLocation` facts visibly cluster toward the lab. This is the "hone in on the area" mechanic, and it is entirely mod-side.

The engine reuses the author's proven toolchain around this host: the pin/drive-NPC machinery (per-tick goal re-assert at priority 100000, RemoveGoalByPtr cleanup), the vacant-den picker, clue/interactable spawning, the additive dialog-option recipe, and the persistence layer.

## Feasibility (from recon; green = real API, yellow = fake/uncertain, red = fully synthetic)

| Element | Verdict | Approach |
|---|---|---|
| Drug rename (beta blocker -> Mystery Drug A) | green | Clone the preset with `Object.Instantiate` (never mutate the shared SO), set `presetName`, register into `objectPresetDictionary`, `Strings.WriteToDictionary("evidence.names", key, "", "Mystery Drug A")`. |
| Spawn / hand item NPC to NPC | green | `InteractableCreator.CreateWorldInteractable`; transfer via `Interactable.SetInInventory(recv)` + `Human.AddCurrentConsumable(preset)`. |
| Lab in vacant address (or cook home if alone) | green | Reuse the vacant-address picker (inhabitants==0, name has "Vacant", lockable door) + proximity constraint; home if `inhabitants.Count<=1`. |
| Lab furniture / dressing | yellow | Runtime-enumerate `Toolbox.allFurnitureClusters/.allFurniture` for lab-reading assets; place via `GetBestFurnitureClusterLocation` + `NewRoom.AddFurniture`. MVP fallback: item-scatter via `PlaceObject`. |
| Cook skips work + pinned to lab | green | Keep the job (needed for the absence clue); out-prioritise the work goal with a pin re-asserted each tick at 100000 (shipped kidnap-hold pattern). Cooking is animation/flavour. |
| Cook->dealer / dealer->buyer handoff | yellow | No vanilla give-to-passerby verb. Fake convergence by re-targeting a goto goal to the recipient's live node each tick, then transfer on adjacency. Roam within lab radius. |
| Homeless dealers | green | `Actor.isHomeless`; enumerate `CityData.homelessDirectory`. Fallback if pool small: co-opt lowest-class housed citizens (do not spawn). |
| Buyer periodic consume | yellow | Do not trust autonomous consumption; script the cadence with `ActionController.Consume(dose, node, buyer)` on a per-buyer timer. |
| Enforcer-witnessed arrest | RED | No vanilla NPC arrest end-state, no NPC jail, no "use is a crime". Script it: flip illegal flags, gate on a real `enforcer.ActorRaycastCheck(buyer)` LOS, then fake the takedown (`SetRestrained`/`SetKO` + `SetConfineLocation` + relocate/despawn). |
| Case to scanner | green | `CreateNewCase(CaseType.custom, ...)` registers the case + board button; fire `NewGameMessage` explicitly for the pop. |
| Arrest scene as crime scene | green | `NewGameLocation.SetAsCrimeScene(true)` + `evidenceEntry`; fallback: spawn evidence + an `exploreCrimeScene` objective pointing at the node. |
| Physical evidence link | green | Spawn the dropped dose `belongsTo:buyer`; wrap via `Toolbox.GetOrCreateEvidenceForInteractable` + `Evidence.SetBelongsTo` + `AddFactLink` into the case fact graph. |
| Fingerprints on the dose | green | `Interactable.AddNewDynamicFingerprint(dealer, PrintLife.manualRemoval)`; `Print.GetOwner()` resolves to the dealer (SideJobStolenItem precedent). Clean up on close. |
| CCTV trace | yellow | Footage logs presence (`SceneCapture.aCap`, `ActorCapture.id==humanID`), reusable as `EvidenceSurveillance`. BUT cameras are indoor/entrance-mounted (street handoffs unfilmed), no citywide follow, no "met with" fact, ~2-day retention. |
| Geographic clustering | yellow | Mod-side: constrain dealer/buyer roam and arrest-site selection to a radius of the lab so `EvidenceLocation` facts cluster. No vanilla heatmap. |
| Ask-about-item dialog | green | Existing additive recipe; only the item reference is new (scan target's inventory/`currentConsumables`, gate + label + branch). |

## Top risks (ranked)

1. **Enforcer arrest is fully synthetic (red).** No NPC arrest/jail exists. The witness gate is real; everything after is scripted. Highest chance of looking wrong or being unreliable. De-risk with a Phase 0 smoke test and a witness-timeout fallback.
2. **CCTV cannot carry the trace the way first imagined (yellow).** Street handoffs go unfilmed, no citywide follow, no "met with" fact, ~2-day purge. This threatens the "trace on CCTV" fantasy, so physical evidence should likely be the spine. (User decision pending.)
3. **NPC-to-NPC handoff has no vanilla verb (yellow).** Convergence and transfer are faked; needs the per-tick re-target pattern to be robust against pathing.
4. **Furniture placement uncertain (yellow).** Falls back to item-scatter if cluster placement misbehaves.
5. **Save / reload.** All operation state must re-derive and re-assert the cook pin on load. Plan persistence from the start.

## Build order

- **Phase 0 (in-game smoke test):** dump the beta-blocker preset key, enumerate furniture cluster names, confirm homeless population, smoke-test that a `CaseType.custom` case renders on the board and `NewGameMessage` fires, confirm `SetAsCrimeScene` on a street. Behind a debug key. De-risks the reds/yellows before hardcoding.
- **Phase 1 (item + dialog):** clone + rename the drug preset, spawn/transfer primitives, and the additive "ask about this item" dialog option. Self-contained, independently testable.
- **Phase 2 (lab + cook):** vacant-address picker + proximity + cook-home-if-alone; pin the cook (keep the job); dress the lab (item-scatter MVP then cluster).
- **Phase 3 (supply chain):** homeless dealer roster; cook->dealer and dealer->buyer handoffs via per-tick node re-targeting within the lab radius; constrain roam for clustering.
- **Phase 4 (arrest):** per-buyer timed consume; nearest on-duty enforcer LOS gate; faked takedown.
- **Phase 5 (case + evidence):** create/update the master case + notification; crime scene at the arrest site; dropped dose with dealer prints; wrap as evidence + fact links; CCTV per the chosen strategy; objectives + resolve-question leads; cleanup on resolve.
- **Phase 6 (persistence + polish):** re-derive and re-assert on load; tune radius, cadence, reliability, and timing against the ~2-day footage window; strip debug logging, gate keys.

## Decided defaults (not asking; will revisit if wrong)

- **Single persistent master case** (matches the locked resolution and enables clustering).
- **Drug visibly held/equipped** by dealers and buyers, so the player can spot dealers by sight and doses show in any hand-captures.
- **Distinct cloned preset per operation wave** (Mystery Drug A, B, C with their own names/icons).
- **Homeless shortfall fallback:** cap dealers to the homeless count; if too few, co-opt lowest-societal-class housed citizens rather than spawning new NPCs.
- **Dose/arrest pacing** kept within the ~2-day footage retention window so the trail is not purged before the player follows it.

## Open questions being asked now

1. Win condition + reward (arrest the cook vs raid the lab location; pure investigation vs a cash bust bounty).
2. What the faked arrest looks like on screen (KO + removal, escort-off, or discover-after-the-fact).
3. Evidence-trail strategy given the CCTV limits (physical as the spine with CCTV corroborating, inject our own cameras, or stage deals in camera-covered spots).
4. Arrest reliability vs emergence (how many cases the player gets).

## Progress + verified APIs (in-game, 2026-09-29)

Phase 0/1 tested in-game; Phase 2 built and deploys, awaiting test. Verified recipes (these corrected several recon assumptions):

- **Drug item (works):** clone `Toolbox.Instance.objectPresetDictionary`'s `BetaBlockers` preset with `UnityEngine.Object.Instantiate`, set `clone.presetName`, register back into the dictionary, and `Strings.WriteToDictionary("evidence.names", key, "", displayName, "", 0, false, "")`. Spawn a visible dose with `NewGameLocation.PlaceObject(preset, belongsTo, null, null, out furn, passVariable:false, forceSecuritySettings:true, forcedSecurity:0, forcedOwnership:OwnedPlacementRule.both, forcedPriority:5, placeClosestTo:node, ignoreLimits:true)`. Adding to `currentConsumables` alone shows nothing in the player UI.
- **Substrate confirmed:** 60 homeless (ample dealer pool), 368 citizens, 848 addresses. Lab-furniture clusters exist: `Machinery01/02/03`, `EEGMachine`, `WorkbenchAgainstWall`, `SciencePictureLab`, `MedicineCabinet`, `MedicineCart`, plus kitchen counters and `3_Cooker`.
- **Silent case (works):** `CreateNewCase(CaseType.custom, ...)` opens a player-facing confirm popup, so DO NOT use it. Instead construct `new Case(name, CaseType.custom, CaseStatus.handInNotCollected)` directly, then `CasePanelController.Instance.activeCases.Add(c)` + `SetActiveCase(c)` + `UpdateCaseButtonsActive()`/`UpdateCaseControls()`. The murder/side-job engines create silently the same way (they call CreateNewCase with a non-custom type and `isSilent:true`).
- **Announcement (works, verified in-game):** the big centre "NEW ... CASE" banner is NOT `NewGameMessage` (that only drives the location/time arrival header and the bottom-left police-scanner feed - both wrong). It is `InterfaceController.DisplayMissionEndText(ScreenDisplayType, Case)`, fired via the public wrapper `InterfaceController.Instance.NewMurderCaseDisplay()` (`ScreenDisplayType.newMurderCase` = 2), which plays the full-screen banner + its own sting. The main text is a FIXED `Strings.Get("ui.gamemessage", "New Murder Case")` (or "New Kidnapping Case" when the active case is a kidnap), subtitle "Case Opened" - there is NO custom-text parameter. To show our own wording we `Strings.GetENG` the current value, `Strings.WriteToDictionary("ui.gamemessage", "New Murder Case", "", ourText, ...)`, fire the banner, then restore the vanilla text ~3s later (so real murder banners are unaffected). `ScreenDisplayType`: missionComplete=0, missionFailed=1, newMurderCase=2, socialCreditLevelUp=3, unsolved=4, displayResolve=5 (the "SOLVED" banner), apartmentPurchase=6, gameOver=7, coverUpSuccess=8, coverUpFailed=9. The small **police-scanner feed** IS `NewGameMessage(GameMessageType.notification, ...)` (used for e.g. "suspect convicted"); keep it for the per-arrest scanner blip alongside the banner. `AudioControls` has `newMurderCase` + `enforcerScannerMsg`.
- **Cook pin (built):** `cook.ai.CreateNewGoal(RoutineControls.Instance.toGoGoal, SessionData.Instance.gameTime, 9999f, node, null, labLocation, null, null, -2)`, then each frame set `goal.basePriority=goal.priority=100000f`, `isActive=true`, `ai.currentGoal=goal`. Priority 100000 dominates the work/sleep routine (so "cook skips work" is free); the cook keeps their job for the later absence clue. Release via `goal.Remove()`.
- **Lab siting (built):** cook's `home` if `home.inhabitants.Count<=1`; else a vacant `NewAddress` (from `CityData.addressDirectory`, `inhabitants.Count==0`, name contains "Vacant", has a lockable entrance door), preferring one near the cook's home for geographic clustering. A vacant lab is made the cook's PROPERTY via `NewAddress.AddOwner(cook)` (user, 2026-09-29) so the player/other NPCs are trespassing and the cook reacts to intruders; `RemoveOwner` on operation end. Side effect to watch: ownership creates a records link (cook -> this address), which doubles as a legitimate lead once the player suspects the cook.
- **On-screen debug log panel (built):** a BepInEx `ILogListener` (`NarcLogListener`) captures the mod's own log lines into a ring buffer (`LogPanel`), drawn by the hotkey MonoBehaviour's `OnGUI` (top-left, behind EnableDebugKeys, F9 toggles). Lets the user watch activity without alt-tabbing to the console.
- **Arrest primitives confirmed present** (for Phase 4): `NewAIController.SetRestrained/SetKO/SetConfineLocation/SetPersue/InstantPersuitCheck`; `Actor.illegalActionActive/illegalStatus/UpdateIllegalStatus/seesIllegal/AddToSeesIllegal/ActorRaycastCheck/SightingCheck`; `GameplayController.enforcers`. The arrest is still fully synthetic (no NPC arrest end-state), decode in progress.

### Debug keys (behind [Debug] EnableDebugKeys)

- **F2** teleport to the next operation NPC (cook -> distributor -> dealers -> back), logs their inventory
- **F3** teleport to the next dead drop (cycles the dealers' drops), logs the container + dose count
- **F4** force a dealer holding doses to sell to the nearest passer-by now (dealers also auto-collect from drops and auto-sell on a timer)
- **Home** substrate dump (read-only; moved off F3)
- **F5** start a drug operation (pick cook, site lab, pin cook, staff the configured tiers)
- **F6** stop the operation (release the cook + all movers, destroy doses)
- **F7** god mode (NPCs/cameras ignore you, no trespass, invincible, echelon access, test cash) - toggle to tail the cook/dealers freely
- **F8** stage a standalone arrest test between the two NPCs nearest you (enforcer walks up, restrains, holds ~6s, releases)
- **F10** teleport to the staged arrest pair to watch it
- **F9** toggle the on-screen debug log panel (recent mod activity, no console alt-tab needed)
- **F11** teleport to the current lab
- **F12** dress the current lab with furniture (stand in the lab first)
- **End** toggle fast-forward (game 'simulation' speed x `TimeBoostMultiplier`, default 3), ported from the Motives mod

### Daily supply-chain pipeline + physical doses (rebuilt 2026-09-30)

The operation is a once-per-day pipeline, one actor driven at a time (tailable): `COOK -(daily)-> lab stash -> DISTRIBUTOR collects -> CONTAINER DEAD DROPS -> DEALERS collect`. The **cook is NOT pinned at the lab**; on their daily timer they commute to the lab, cook `CookDosesPerSession` (4) doses into inventory, deposit them into the lab as a persistent stash (`Dose.Place` = `PlaceObject`), then leave and resume their normal routine (the commute is the tell). Exactly **1 distributor** (hardcoded) once per cook cycle walks to the lab, collects the stash into inventory (`Dose.Pickup` = `SetInInventory`), then walks to each dealer's assigned **container dead drop** and deposits `DosesPerDealer` (2) via `Dose.DepositInContainer`. **Dealers** (homeless) walk to their container and collect. The **dead drops are REAL CONTAINERS** (street bins / mailboxes / lockers) sited at operation start via `Containers.SiteContainersNearLab` = the N nearest containers to the lab building's exterior; dealers are the N nearest homeless (`Containers.PickNearestHomeless`), so both cluster near the lab. Depositing puts a contained `MetaObject` on the bin container's `EvidenceMultiPage` (`Dose.DepositToBin` = find the bin interactable whose `.evidence` is an `EvidenceMultiPage`, `new MetaObject(drug, depositor, depositor, null, null)`, `page.AddContainedMetaObjectToNewPage(mo)`). That page is the ONLY list the player's bin search reads (`Player.OnSearch` -> `EvidenceMultiPage.pageContent` -> `CityData.FindMetaObject`), and MetaObjects self-register in CityData + live in the save, so it is **persistent + searchable with the player absent** (same pipeline `PlaceObject` uses). Collect = `Dose.CollectFromBin` (remove the MetaObject + mint a fresh physical dose in the dealer). NOT: `CreateFurnitureSpawnedInteractable` (world item, search ignores it), `isTrash` passed-var (that's loose litter), `Human.AddTrash` (per-citizen list), or `ActionController.Dispose` (player-only, needs streamed Garbage-tagged bin). If no container is found for a dealer it falls back to a loose `Dose.Drop` (`SetAsNotInventory`) near them. Files: `Containers.cs` (siting + slot pick + nearest homeless), `Dose.cs` (Place/Drop/Pickup/DepositInContainer), `Herd.cs` (multi-actor drive), `Operation.cs` (Job pipeline: CookSession/DistCollect/DistDrop/DealerCollect, one mover, per-job timeout, live `_targetNode` distance readout). Config section **Distribution**: `CookDosesPerSession` (4), `CookIntervalHours` (24), `DealerCount` (2), `DosesPerDealer` (2) - distributor is always 1. Fingerprint: lab-stash dose = cook prints; container dose = distributor prints (fresh furniture-spawned item) + dealer prints on collect; cook->distributor link lives on the lab stash (found on a lab raid). The distributor still needs the container area streamed to be herded there for show, but the searchable evidence is placed data-side regardless. Buyers/selling/consume/arrest are the next phase (not staffed yet).

- **Physical dose = distinct pickable Evidence (root-caused + fixed 2026-09-30):** create via `InteractableCreator.Instance.CreateWorldInteractable(preset, holder, null, null, node.position, Vector3.zero, null, null)` then `it.SetInInventory(holder)` - EXACTLY how the game's own `Human.SpawnInventoryItems` makes NPC inventory items. The earlier `CreateCitizenInteractable` NEVER assigned a world `id`, so every dose got `id==0` -> evidence key `"I0"` -> `Toolbox.GetOrCreateEvidenceForInteractable` returned whatever already owned `"I0"` (in a murder-heavy save, a bullet-WOUND Evidence): that ONE bug caused all three symptoms - dose rendered as a `.309` entry wound, all doses collapsed to one case-board pin, and the unregistered item vanished instead of entering the player's inventory on pickup. `CreateWorldInteractable` calls `AssignIDWorld` before `OnCreate/MainSetupEnd`, so each dose gets a unique `id` -> unique `"I{id}"` Evidence -> name from `evidence.names[presetName]` = "Mystery Drug A" -> distinct case pins, own fingerprints, proper pickup. The clone already inherits a complete pickable-consumable config from BetaBlockers (retailItem, prefab, fpsItem, isInventoryItem, useEvidence, spawnEvidence), so no extra preset flags are needed; `DrugItem.CreateDrug` stays as-is. `SetInInventory` stamps the holder's dynamic fingerprint automatically; `Interactable.AddNewDynamicFingerprint(Human, PrintLife.timed|manualRemoval)` adds explicit prints; `SafeDelete(true)` consumes/destroys.
- **Dead drops + in-lab stash (recon 2026-09-30, verified via ilspy/probe):** `NewGameLocation.PlaceObject(preset, belongsTo, writer, receiver, out FurnitureLocation pickedFurn, passVariable:false, forceSecuritySettings:true, forcedSecurity:0, forcedOwnership:OwnedPlacementRule.both, forcedPriority:5, placeClosestTo:<node>, ignoreLimits:true)` = the game's own pipeline (picks valid furniture, parents into `FurnitureLocation.spawnedInteractables`, creates evidence + ownership, survives stream-out/in + save/load via `LoadInteractableToWorld`) - used for the persistent lab stash. `Interactable.SetAsNotInventory(node)` drops a carried item loose at a node (reused for dead drops; preserves prints). No vanilla NPC "collect" verb - scripted: herd the collector to the node, then `SetInInventory(collector)`. Containers = furniture whose `FurnitureClass.OwnershipClass` in {locker, drawers, safe, mailboxes, desk}; enumerate via `NewRoom.individualFurniture` / `NewNode.individualFurniture` / `Toolbox.FindFurnitureWithinGameLocation`. `CreateFurnitureSpawnedInteractable` puts an item into a specific container (findable on search - the future searchable-drop upgrade); `CreateFurnitureIntegratedInteractable` is for fixed parts, not doses.

## Phase 3-5 build plan (recon complete 2026-09-29)

Deep-decoded and verified. **Build order = forward / causal (cook -> lab -> dealers -> distribution -> buyers -> consume -> arrest -> case), the order the game simulates it** (user, 2026-09-29). The arrest is the culmination of a real supply chain (a real buyer holding a real dose traceable to a real dealer/cook), NOT an isolated smoke test on a fake buyer; the player only hears about the operation once the arrest happens. (This supersedes the earlier "build the Phase 4 arrest smoke test first" note.)

### Phase 3 - supply chain
- **STEP 0 (extend `DrugItem.CreateDrug`):** also make the clone carriable + fingerprintable + evidence-bearing (set `isInventoryItem`, `requiredCarryAnimation`, copy `aiCarryAnimation`/`aiHeldObjectPosition`/`aiHeldObjectRotation` from a known carriable preset, `fingerprintsEnabled`, `enableDynamicFingerprints`, `useEvidence`). Verify at runtime: without the fingerprint flags `AddNewDynamicFingerprint` silently no-ops; without `useEvidence` `Interactable.evidence` stays null.
- **Dealers.cs:** roster from `CityData.Instance.homelessDirectory` (60 available), filter `isHomeless && !isDead`, pick N near the lab. Fallback: co-opt lowest-class housed citizens (don't spawn).
- **Distribution.cs (from `Operation.Tick`):** cook->dealer supply on a jittered cadence. Needs an `Operation.CookMode { AtLab, Delivering }` state machine because the priority-100000 lab pin fights the delivery walk (in Delivering mode, retarget the cook's goal to the dealer's live node). MVP fallback toggle: hand doses directly (no street walk).
- **Visible carry:** `dose.SetInInventory(supplier)` + `supplier.rightHandInteractable=dose` + `supplier.ai.UpdateHeldItems(onActivation)` + `animationController.SetCarryingItem(true)/SetCarryItemType`. UNVERIFIED (raw x86); fallback: leave the dose `PlaceObject`'d on the supplier's node (still visible + fingerprinted + on CCTV).
- **Buyers.cs:** dealer->buyer convergence via the generalized herd pin (retarget to buyer's live node each tick); handoff on adjacency (`Vector3.Distance` small or shared node): despawn giver's held item, transfer, set recipient's held item.
- **Buyer consume:** per-buyer `nextConsumeTime`; `AddCurrentConsumable` then `RemoveCurrentConsumable(drugPreset)` (prefer over `ActionController.Consume`, which is a near-no-op that empties ALL consumables and applies no flag). Optional `AddDrunk/AddSick` for a visible stagger. Hands off to Phase 4.

### Phase 4 - the arrest (RED, fully synthetic). Recommended sequence
Build as a STANDALONE smoke test first (one buyer + one enforcer, debug key) before wiring to the chain.
1. **Event-driven trigger:** only run the observable consume when a real enforcer is in position. From `GameplayController.Instance.enforcers` take the nearest with `isEnforcer && isOnDuty && !isDead && !isStunned`; require `enforcer.ActorRaycastCheck(buyer, range, out hit) == true` (a genuine Physics.Raycast = the literal "if an enforcer sees it"; also makes arrests cluster near patrols). If none: drop the fingerprinted dose and skip (or `CallEnforcers(immediateTeleport)` behind a toggle).
2. **Synthesize the crime:** `buyer.illegalActionActive = true; buyer.UpdateIllegalStatus()` (never set `illegalStatus` directly; own the flag's lifetime).
3. **Walk up:** `enforcer.ai.SetPersue(buyer, false, 2, true, range)` PLUS a backstop toGoGoal pin to the buyer's live node at priority 100000, re-asserted each frame (reuse `Cook`'s pin machinery). Don't rely on `InstantPersuitCheck` alone.
4. **On contact** (same node, or same room and distance < ~1.5m): KO only if the buyer is fleeing (`SetKO(true, hit.point, dir, forced:true, short)`); else skip straight to cuff (SetKO ragdolls, looks violent). `buyer.ai.SetRestrained(true, holdHours)` and **RE-ASSERT EVERY FRAME** during the hold (it is timer-based; the buyer stands back up otherwise). This is the arms-behind-back pose, the only cuff-like animation (no cuff prop exists).
5. **Close out (let them go):** `enforcer.ai.CancelPersue()` + remove its pin; after a ~3-5s cuffed hold, `buyer.ai.SetRestrained(false, 0)` to RELEASE the buyer, clear `illegalActionActive` + `UpdateIllegalStatus`, so they resume their routine (do NOT `RemoveFromWorld` - possession is minor; the buyer stays tail-able/question-able). Then trigger Phase 5 (drop the fingerprinted dose, open/update the case, fire the scanner blip).

### Phase 5 - case + physical evidence + scanner
- **Master case (first arrest only):** `new Case("New Drug on the Street", CaseType.custom, CaseStatus.handInNotCollected)` + `activeCases.Add` + `SetActiveCase` + `UpdateCaseButtonsActive/UpdateCaseControls`. Store on `Operation.MasterCase`. First arrest also fires the big banner + a scanner notification.
- **Per-arrest scanner blip:** `NewGameMessage(GameMessageType.notification, 0, blip, Icon.handcuffs, AudioControls.Instance.enforcerScannerMsg, newMergeType:-1)`.
- **Dropped dose + dealer prints:** `L.PlaceObject(clone, belongsTo:buyer, ..., placeClosestTo:arrestNode)`; `for i in 0..5: dose.AddNewDynamicFingerprint(dealer, PrintLife.manualRemoval)`. `Print.GetOwner()` resolves dose->dealer automatically.
- **Evidence + lead:** `Toolbox.Instance.GetOrCreateEvidenceForInteractable(...)`; `L.SetAsCrimeScene(true)` (try/catch, non-load-bearing); `c.AddObjective("Investigate the drug arrest", new Objective.ObjectiveTrigger(ObjectiveTriggerType.exploreCrimeScene, ..., arrestNode, L, arrestNode.position), usePointer:true, useIcon:Icon.lookingGlass, ...)`; `PinToCasePanel(c, doseEv, {photo, fingerprints}, forceAutoPin:true)`.
- **Cook leg:** separate stash item at the dealer's location with cook prints (so raiding the dealer traces to the cook).
- **Resolve questions:** `ResolveQuestion { inputType=citizen, correctAnswers=[cook.humanID] }` (VERIFIED) and `{ inputType=location, correctAnswers=[lab id] }` (TEST id vs display name). Objective chaining is mod-side (poll `objective.isComplete`; `nextChapterPart` does NOT work for custom cases).
- **Solve = vanilla hand-in loop** (confirmed; not a side gig): the player fills the Resolve pane (name the cook, locate the lab) and hands in via `Case.Resolve()` for the vanilla rank-scored reward. MUST-VERIFY the Resolve/submit pane renders + submits for `CaseType.custom` early; if not, fixing the hand-in loop for a custom case is a priority. The manual `AddMoney` + `SetStatus(closable)` path is only a last-resort fallback, not the design.
- **Teardown:** `RemoveManuallyCreatedFingerprints()` on planted items; clear pins; reset `Operation` for the next wave (Drug B).

### Top risks (ranked)
1. **Faked arrest (RED)** - no NPC arrest state; `SetRestrained` must be re-asserted every frame. Prove standalone first.
2. **Geometry streaming** gates the LOS raycast + takedown + crime scene; arrests are reliable near the player. Only attempt when the buyer's area is streamed.
3. **Visible in-hand carry UNVERIFIED** - fallback to world-placed dose on the node.
4. **Custom-case resolve/solve UI unverified** - fallback to the manual reward path.
5. **Cook pin fights the delivery walk** - needs the CookMode state machine (MVP: direct-hand toggle).
6. **`illegalActionActive` lifetime** - vanilla never clears it on NPCs; we set-and-clear.
7. **Save/reload** - re-derive + re-assert all operation state on load (Phase 6 persistence, plan from the start).

### Design calls (CONFIRMED by user, 2026-09-29)
- **Consume cadence / arrest witness:** event-driven - a buyer is only caught when a real on-duty enforcer already has line of sight (`ActorRaycastCheck`), so arrests cluster near patrols/the lab. `CallEnforcers` teleport stays behind a config toggle.
- **Arrest disposition = LET THEM GO.** Clean stand-and-cuff (KO only if the buyer bolts), a brief detain at the scene, then RELEASE the buyer back to their routine. Do NOT `RemoveFromWorld` a buyer - possession is minor and should not shrink the citizen pool. The arrest still opens/updates the case and drops the evidence at the scene, and the released buyer remains tail-able and question-able.
- **Solve = the VANILLA case resolution / hand-in loop, like a murder** (NOT auto-solve, NOT a side-gig quick reward - "this isn't a side gig"). The player fills the vanilla Resolve pane (name the cook, locate the lab) and hands the case in for the vanilla rank-scored reward via `Case.Resolve()`. MUST-VERIFY EARLY: that the Resolve/submit pane actually renders and is submittable for `CaseType.custom` (encouraging: `UpdateResolveFields` gates on caseStatus not caseType). If it does not, making the hand-in loop work for a custom case becomes a priority to solve, not a fallback to a manual reward.
- **Location resolve answer:** test both the location id and the display name (recon could not verify which). [must test]
- **Handoff item + cook leg:** fresh fingerprinted dose per stage; cook's prints on a SEPARATE stash item at the dealer, so each hop is a distinct discovery. Now also supports hand OR dead-drop exchange (see the investigation loop below).
- **Cluster style:** permanent scene pin per arrest (strongest "hone in" read); revisit if the board gets cluttered.

### Investigation loop (user, 2026-09-29) - expands Phase 3 + the solve
The player traces the chain by TAILING, not just by reading fingerprints off a dropped dose:
- **Tail a buyer -> find their dealer -> tail / shake down the dealer -> trace to the cook / lab.** The arrested buyer is a starting thread (they are released, so they can be followed and questioned); questioning the buyer still does NOT name the source, but a DEALER can be shaken down for it.
- **Dealers need believable ROUTINES** so tailing works: they move a route (near the lab), collect supply, and sell to passers-by. This replaces the earlier "loiter and sell" sketch - dealers get real, followable schedules.
- **Exchanges happen by HAND (a visible handoff) OR by DEAD DROP** (a supplier leaves the drug at a fixed spot; the other picks it up later). Both leave a trail: a dead-drop location becomes an evidence + tail point, and a hand-off is spot-able and CCTV-visible.
- **"Shake down" a dealer** = a dialog interaction to extract who supplies them (pressure / bribe / threat). Mechanism TBD (could extend the ask-about-item dialog or add a new option). Dealers are a richer info source than buyers.
- Implication: Phase 3 grows a dealer-routine/dead-drop system; Phase 5's trail leans on tailing + shakedowns alongside the physical prints.

**Cook routine = REGULAR within a case, RANDOMLY DECIDED per case (user, 2026-09-29).** Not a hard 24/7 lab pin, and not a moment-to-moment random mix. Instead, when an operation STARTS, roll ONE fixed schedule for the cook (which blocks of the day they cook at the lab vs still show at work / home), then the cook follows that schedule REGULARLY for the whole case. Regular = tailable and gives a consistent absence signal (coworkers/partner notice a repeatable gap). Random-per-case = variety across operations. The absence is partial and believable, and EITHER the partner (home) OR the workplace can surface the subtle "off lately / not around" clue. Refinement to the cook AI when Phase 3 lands (the current Phase 2 hard pin is a testing stand-in). Implementation: replace the single always-on lab goal with a schedule that flips the lab pin on/off by time-of-day, letting the vanilla work/sleep routine run in the off blocks.

## Toolchain / build / packaging (inherited conventions)

- **Build:** `dotnet build -c Release`. Auto-deploys the DLL to `<Steam>\BepInEx\plugins\Narcotics` when the game is CLOSED (fails soft if running).
- **Steam install** (`C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt`) is the DEV target. The r2modman "Default" profile is the user's clean live baseline; do not overwrite without asking.
- **Decode:** reflection probe (net8 MetadataLoadContext) for signatures; Cpp2IL `--output-as isil` for method bodies; ilspycmd for readable C#. Full ISIL dump + IL-recovery DLLs cached in the session scratchpad.
- **Package:** Thunderstore versions are immutable; each release bumps `manifest.json` and rebuilds the zip with System.IO.Compression, forward-slash entries (NOT Compress-Archive). Contents: manifest.json, icon.png (256x256, TODO), README.md, CHANGELOG.md, LICENSE, BepInEx/plugins/Narcotics/Narcotics.dll.
- **Repo:** no Co-Authored-By trailers. No em dashes in any docs or user-facing text.

## Key APIs (targets)

- `CasePanelController.Instance.CreateNewCase(Case.CaseType.custom, Case.CaseStatus.handInNotCollected, false, name)`; `PinToCasePanel`/`SetActiveCase`/`activeCases`.
- `Case.AddObjective(entryRef, Objective.ObjectiveTrigger(ObjectiveTriggerType.*, ...), onCompleteAction:nextChapterPart)`; `Case.ResolveQuestion { InputType.citizen|location|item|arrestPurp }`.
- `InterfaceController.Instance.NewGameMessage(GameMessageType.notification, 0, msg, Icon.agent)`.
- `Toolbox.Instance.objectPresetDictionary` + `Object.Instantiate` + `Strings.WriteToDictionary("evidence.names", key, "", display)`; `EvidenceRetailItem.GenerateName`.
- `InteractableCreator.Instance.CreateWorldInteractable / CreateCitizenInteractable / CreateFingerprintInteractable`.
- `Interactable.SetInInventory(Human)` + `Human.AddCurrentConsumable(preset)`; `Interactable.SetOwner`.
- `CityData.Instance.interactableDirectory / homelessDirectory / citizenDirectory / addressDirectory`; `Actor.isHomeless`.
- `Actor.ai` (`NewAIController`); `CreateNewGoal(RoutineControls.Instance.toGoGoal/toGoWalkGoal, ...)`; goal `basePriority/priority=100000`, `UpdateHeldItems`.
- `ActionController.Instance.Consume(Interactable, NewNode, Actor)`.
- `GameplayController.enforcers`; `NewAIController.InstantPersuitCheck/SetPersue`; `Actor.ActorRaycastCheck/SightingCheck/seesIllegal/illegalActionActive`.
- `NewAIController.SetRestrained/SetKO/SetConfineLocation` (faked arrest).
- `NewGameLocation.SetAsCrimeScene(bool)` + `evidenceEntry`; `NewGameLocation.PlaceObject(...)`; `securityCameras/AddSecurityCamera`.
- `Interactable.AddNewDynamicFingerprint(Human, PrintLife.manualRemoval)` + `RemoveManuallyCreatedFingerprints`; `FingerprintScannerController.Print.GetOwner()`.
- `Toolbox.GetOrCreateEvidenceForInteractable` + `Evidence.SetBelongsTo/AddFactLink`.
- `EvidenceSurveillance`; `SceneRecorder.SceneCapture.aCap`; `Toolbox.GetSceneCaptureFromID`; `SceneRecorder.ExecuteCapture`.
- `GenerationController.Instance.GetBestFurnitureClusterLocation` + `NewRoom.AddFurniture`; `Toolbox.allFurnitureClusters/allFurniture`.
