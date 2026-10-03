using System;
using UnityEngine;

namespace Narcotics
{
    // The re-skinned drug item: clone a vanilla consumable (beta blockers, or another small
    // pill/medical item) and rename it to "Mystery Drug A" WITHOUT mutating the shared vanilla
    // preset (that would rename every beta blocker in the city). Discovery is by name scan
    // because preset asset names live in Unity bundles, not the DLL.
    internal static class DrugItem
    {
        internal static InteractablePreset BasePreset;   // the discovered vanilla base
        internal static InteractablePreset Current;      // the active operation's cloned drug preset
        internal static string CurrentKey;
        internal static string CurrentDisplay;

        // On a game load the cached presets reference a previous session's (destroyed) objects; clear them so a
        // fresh drug is created next operation.
        internal static void ResetOnLoad() { BasePreset = null; Current = null; CurrentKey = null; CurrentDisplay = null; }

        // Find a vanilla consumable to base the drug on. Beta blockers preferred; any small
        // pill/medical consumable is an acceptable fallback.
        internal static InteractablePreset FindBasePreset(bool logAll = false)
        {
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null) { Log("FindBasePreset: Toolbox.Instance null"); return null; }
                var dict = tb.objectPresetDictionary;
                if (dict == null) { Log("FindBasePreset: objectPresetDictionary null"); return null; }

                InteractablePreset beta = null, pill = null, med = null;
                int n = 0, matched = 0;
                foreach (InteractablePreset preset in dict.Values)
                {
                    n++;
                    if (preset == null) continue;
                    string nm = SafeName(preset);
                    if (string.IsNullOrEmpty(nm)) continue;
                    string low = nm.ToLowerInvariant();
                    bool drugish = low.Contains("beta") || low.Contains("blocker") || low.Contains("pill")
                                 || low.Contains("medic") || low.Contains("drug") || low.Contains("capsule")
                                 || low.Contains("tablet") || low.Contains("dose") || low.Contains("sedativ")
                                 || low.Contains("painkill") || low.Contains("narcot");
                    if (logAll && drugish) { matched++; Log($"  candidate: '{nm}'  consumableAmount={SafeConsumable(preset)}"); }
                    if (beta == null && (low.Contains("beta") || low.Contains("blocker"))) beta = preset;
                    else if (pill == null && low.Contains("pill")) pill = preset;
                    else if (med == null && (low.Contains("medic") || low.Contains("capsule") || low.Contains("tablet") || low.Contains("sedativ") || low.Contains("painkill"))) med = preset;
                }
                Log($"FindBasePreset: scanned {n} presets, {matched} drug-ish candidates. beta={(beta != null)} pill={(pill != null)} med={(med != null)}");
                return beta ?? pill ?? med;
            }
            catch (Exception e) { Log($"FindBasePreset error: {e.Message}"); return null; }
        }

        // Clone the base into a uniquely named drug preset. Never mutates the shared base.
        internal static InteractablePreset CreateDrug(string presetKey, string displayName)
        {
            try
            {
                if (BasePreset == null) BasePreset = FindBasePreset();
                if (BasePreset == null) { Log("CreateDrug: no base preset found (run the substrate dump to see item names)"); return null; }

                var clone = UnityEngine.Object.Instantiate(BasePreset);
                if (clone == null) { Log("CreateDrug: Instantiate returned null"); return null; }
                UnityEngine.Object.DontDestroyOnLoad(clone);
                try { clone.hideFlags = HideFlags.HideAndDontSave; } catch { }
                try { clone.name = presetKey; } catch { }
                try { clone.presetName = presetKey; } catch (Exception e) { Log($"CreateDrug: set presetName failed: {e.Message}"); }
                // Concealed: NPCs carry the dose in inventory (still searchable + fingerprintable) but do NOT walk
                // around visibly holding it in hand (user decision 2026-10-01). The arrest fires from an enforcer's
                // line-of-sight to a known buyer, not from vanilla contraband detection.
                try { clone.inventoryCarryItem = false; } catch { }
                try { clone.requiredCarryAnimation = false; } catch { }

                // Register so lookups and evidence naming resolve to the clone.
                try { Toolbox.Instance.objectPresetDictionary[presetKey] = clone; } catch (Exception e) { Log($"CreateDrug: register failed: {e.Message}"); }

                // Item names resolve via Strings 'evidence.names' keyed by presetName.
                try { Strings.WriteToDictionary("evidence.names", presetKey, "", displayName, "", 0, false, ""); }
                catch (Exception e) { Log($"CreateDrug: WriteToDictionary failed: {e.Message}"); }

                Current = clone; CurrentKey = presetKey; CurrentDisplay = displayName;
                Log($"CreateDrug: created '{displayName}' (key={presetKey}) from base '{SafeName(BasePreset)}'");
                return clone;
            }
            catch (Exception e) { Log($"CreateDrug error: {e}"); return null; }
        }

        // Put a dose into a citizen's consumables (what they will later 'use' / sell).
        internal static void GiveConsumable(Human who, InteractablePreset drug)
        {
            try { if (who != null && drug != null) who.AddCurrentConsumable(drug); }
            catch (Exception e) { Log($"GiveConsumable error: {e.Message}"); }
        }

        // How many of the CURRENT operation's drug a citizen is holding (in their consumables).
        internal static int CountIn(Human who)
        {
            int c = 0;
            try
            {
                var list = who != null ? who.currentConsumables : null;
                if (list == null || Current == null) return 0;
                for (int i = 0; i < list.Count; i++)
                {
                    try { var p = list[i]; if (p != null && p.Pointer == Current.Pointer) c++; } catch { }
                }
            }
            catch { }
            return c;
        }

        // Remove one dose of the current drug from a citizen (returns true if one was taken).
        internal static bool TakeOne(Human who)
        {
            try { if (who != null && Current != null && CountIn(who) > 0) { who.RemoveCurrentConsumable(Current); return true; } }
            catch { }
            return false;
        }

        // Spawn a physical, visible dose near an actor via the game's own furniture placement.
        // The raw CreateWorldInteractable path needs room/node context and NREs without it; PlaceObject
        // is the proven primitive (the same one the clue system uses).
        internal static Interactable SpawnDoseAtActor(InteractablePreset drug, Human at, Human belongsTo)
        {
            try
            {
                if (drug == null || at == null) return null;
                var loc = at.currentGameLocation;
                if (loc == null) { Log("SpawnDoseAtActor: actor has no currentGameLocation"); return null; }
                FurnitureLocation furn;
                return loc.PlaceObject(drug, belongsTo, null, null, out furn,
                    passVariable: false,
                    forceSecuritySettings: true,
                    forcedSecurity: 0,
                    forcedOwnership: InteractablePreset.OwnedPlacementRule.both,
                    forcedPriority: 5,
                    placeClosestTo: at.currentNode,
                    ignoreLimits: true);
            }
            catch (Exception e) { Log($"SpawnDoseAtActor error: {e.Message}"); return null; }
        }

        static string SafeName(InteractablePreset p)
        {
            try { if (!string.IsNullOrEmpty(p.presetName)) return p.presetName; } catch { }
            try { return p.name; } catch { return null; }
        }
        static string SafeConsumable(InteractablePreset p) { try { return p.consumableAmount.ToString("0.##"); } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][item] " + m);
    }
}
