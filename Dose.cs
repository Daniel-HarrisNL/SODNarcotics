using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // A PHYSICAL, searchable dose: a real Interactable sitting in an NPC's inventory, so it shows up when the
    // player searches/frisks them (unlike a currentConsumable, which does not appear on a body search - that
    // was why searching the cook found nothing). Built from the operation's cloned drug preset.
    //
    // We keep our own per-human handle list so we can count, transfer and destroy doses precisely, and we
    // validate each handle against the item's live `inInventory` owner so the counts self-heal if the game
    // moves or deletes an item under us.
    internal static class Dose
    {
        // humanID -> the physical dose Interactables we believe are in that human's inventory.
        private static readonly Dictionary<int, List<Interactable>> _held = new Dictionary<int, List<Interactable>>();

        internal static void ResetOnLoad() { _held.Clear(); }

        // How many physical doses `who` is currently holding (validated + self-healing).
        internal static int CountIn(Human who)
        {
            int c = 0;
            try
            {
                if (who == null) return 0;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) return 0;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    if (HeldBy(l[i], id)) c++; else l.RemoveAt(i);
                }
            }
            catch { }
            return c;
        }

        // Create a brand-new physical dose directly in `holder`'s inventory. Returns the Interactable or null.
        //
        // Uses the game's OWN NPC-inventory primitive: CreateWorldInteractable assigns a UNIQUE world id, so each
        // dose gets its own distinct Evidence ("I{id}") and is a properly registered, PICKABLE item; SetInInventory
        // then moves it into the holder and stamps their fingerprint. (The old CreateCitizenInteractable path never
        // assigned an id -> every dose collided on evidence "I0" = whatever already owned that key, e.g. a bullet
        // wound; doses shared one case pin; and the unregistered item vanished when the player tried to take it.)
        internal static Interactable Create(Human holder)
        {
            try
            {
                var preset = DrugItem.Current;
                if (preset == null || holder == null) return null;
                NewNode node = null; try { node = holder.currentNode; } catch { }
                if (node == null) { Log("Create: holder has no currentNode (unstreamed); skipping."); return null; }

                InteractableCreator creator = null;
                try { creator = InteractableCreator.Instance; } catch { }
                if (creator == null) { Log("Create: InteractableCreator.Instance null"); return null; }

                Interactable it = null;
                try { it = creator.CreateWorldInteractable(preset, holder, null, null, node.position, Vector3.zero, null, null); }
                catch (Exception e) { Log($"CreateWorldInteractable threw: {e.Message}"); }
                if (it == null) return null;

                try { it.SetInInventory(holder); } catch (Exception e) { Log($"SetInInventory threw: {e.Message}"); }
                Track(holder, it);
                return it;
            }
            catch (Exception e) { Log($"Create error: {e.Message}"); return null; }
        }

        // Place a physical dose into a game location's furniture (a persistent, searchable STASH - e.g. inside the
        // lab). Uses the game's own PlaceObject pipeline (survives streaming + save/load). Not tracked as inventory;
        // the caller keeps the returned handle. Stamps the owner's fingerprint. Returns null if placement failed
        // (e.g. the location's geometry is not streamed yet - retry when the player is near).
        internal static Interactable Place(NewGameLocation loc, NewNode near, Human owner)
        {
            try
            {
                var preset = DrugItem.Current;
                if (preset == null || loc == null) return null;
                FurnitureLocation furn;
                Interactable it = loc.PlaceObject(preset, owner, null, null, out furn,
                    passVariable: false, forceSecuritySettings: true, forcedSecurity: 0,
                    forcedOwnership: InteractablePreset.OwnedPlacementRule.both, forcedPriority: 5,
                    placeClosestTo: near, ignoreLimits: true);
                if (it != null && owner != null) { try { it.AddNewDynamicFingerprint(owner, Interactable.PrintLife.timed); } catch { } }
                return it;
            }
            catch (Exception e) { Log($"Place error: {e.Message}"); return null; }
        }

        // ---- searchable trash-bin dead drops ------------------------------------------------------------------
        // A bin's SEARCHABLE contents are contained MetaObjects on the bin container's EvidenceMultiPage - that is
        // the ONLY list the player's search reads. MetaObjects self-register in CityData and live in the save, so
        // this is fully data-side + persistent: it works with the player nowhere near (same pipeline PlaceObject
        // uses, which is why the lab stash works). World items in the furniture's spawned list, loose litter
        // (isTrash), and ActionController.Dispose all do NOT show up on a bin search.

        // The bin's searchable page, building the container interactable data-side if needed. Null = this furniture
        // is not a searchable container (pick a different bin).
        internal static EvidenceMultiPage FindBinPage(FurnitureLocation fl)
        {
            try
            {
                if (fl == null) return null;
                try { if (!fl.createdInteractables) fl.CreateInteractables(); } catch (Exception e) { Log($"CreateInteractables threw: {e.Message}"); }
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = pass == 0 ? fl.integratedInteractables : fl.spawnedInteractables;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var it = list[i]; if (it == null) continue;
                        Evidence ev = null; try { ev = it.evidence; } catch { }
                        if (ev == null) continue;
                        EvidenceMultiPage mp = null; try { mp = ev.TryCast<EvidenceMultiPage>(); } catch { }
                        if (mp != null) return mp;
                    }
                }
            }
            catch (Exception e) { Log($"FindBinPage error: {e.Message}"); }
            return null;
        }

        // Deposit one dose as searchable trash into the bin (a contained MetaObject on its page), carrying the
        // depositor's authorship. Returns true on success.
        internal static bool DepositToBin(FurnitureLocation fl, Human depositor)
        {
            try
            {
                var preset = DrugItem.Current; if (preset == null) return false;
                var page = FindBinPage(fl); if (page == null) { Log("DepositToBin: furniture is not a searchable container."); return false; }
                MetaObject mo = null;
                try { mo = new MetaObject(preset, depositor, depositor, null, null); }   // ctor auto-registers in CityData
                catch (Exception e) { Log($"new MetaObject threw: {e.Message}"); return false; }
                if (mo == null) return false;
                try { page.AddContainedMetaObjectToNewPage(mo); }
                catch (Exception e) { Log($"AddContainedMetaObjectToNewPage threw: {e.Message}"); return false; }
                return true;
            }
            catch (Exception e) { Log($"DepositToBin error: {e.Message}"); return false; }
        }

        // How many of OUR doses are currently in the bin (authoritative; reads the page).
        internal static int CountInBin(FurnitureLocation fl)
        {
            int c = 0;
            try
            {
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = 0; i < pc.Count; i++)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (IsOurMeta(id, key)) c++;
                }
            }
            catch { }
            return c;
        }

        // Remove up to `max` of our doses from the bin, minting a fresh physical dose in the collector's inventory
        // for each. Returns how many were collected.
        internal static int CollectFromBin(FurnitureLocation fl, Human collector, int max)
        {
            int got = 0;
            try
            {
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = pc.Count - 1; i >= 0 && got < max; i--)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (!IsOurMeta(id, key)) continue;
                    try { var m = CityData.Instance.FindMetaObject(id); if (m != null) m.Remove(); } catch { }
                    try { pc.RemoveAt(i); } catch { }
                    if (Create(collector) != null) got++;
                }
            }
            catch (Exception e) { Log($"CollectFromBin error: {e.Message}"); }
            return got;
        }

        // Remove all our doses from a bin (operation teardown).
        internal static int ClearBin(FurnitureLocation fl)
        {
            int n = 0;
            try
            {
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = pc.Count - 1; i >= 0; i--)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (!IsOurMeta(id, key)) continue;
                    try { var m = CityData.Instance.FindMetaObject(id); if (m != null) m.Remove(); } catch { }
                    try { pc.RemoveAt(i); } catch { }
                    n++;
                }
            }
            catch { }
            return n;
        }

        private static bool IsOurMeta(int id, string key)
        {
            if (id == 0 || string.IsNullOrEmpty(key)) return false;
            try { var m = CityData.Instance.FindMetaObject(id); return m != null && m.preset == key; } catch { return false; }
        }

        // Move one dose from a holder's inventory to a loose spot at a node (a DEAD DROP). Reuses the same item, so
        // its accumulated fingerprints are preserved. Returns the handle now sitting at the node, or null.
        internal static Interactable Drop(Human from, NewNode node)
        {
            try
            {
                if (from == null || node == null) return null;
                var it = TakeHandle(from);
                if (it == null) return null;
                try { it.SetAsNotInventory(node); } catch (Exception e) { Log($"Drop SetAsNotInventory threw: {e.Message}"); }
                return it;
            }
            catch (Exception e) { Log($"Drop error: {e.Message}"); return null; }
        }

        // Move a world/furniture dose (from a stash or dead drop) into a collector's inventory. Adds it to inventory
        // tracking so CountIn(to) includes it, and stamps the collector's fingerprint (via SetInInventory). Returns
        // true on success.
        internal static bool Pickup(Interactable it, Human to)
        {
            try
            {
                if (to == null || !Alive(it)) return false;
                try { it.SetInInventory(to); } catch (Exception e) { Log($"Pickup SetInInventory threw: {e.Message}"); return false; }
                bool ok = false; try { var inv = it.inInventory; ok = inv != null && inv.humanID == to.humanID; } catch { }
                if (ok) Track(to, it);
                return ok;
            }
            catch { return false; }
        }

        // Is a world/dropped handle still valid (not destroyed)?
        internal static bool Alive(Interactable it)
        {
            try { return it != null && it.Pointer != IntPtr.Zero; } catch { return false; }
        }

        // Create n physical doses in a holder's inventory. Returns how many were created.
        internal static int CreateMany(Human holder, int n)
        {
            int made = 0;
            for (int i = 0; i < n; i++) { if (Create(holder) != null) made++; }
            return made;
        }

        // Move one existing dose from `from` to `to` (stays a physical, friskable item on the new holder).
        internal static bool Transfer(Human from, Human to)
        {
            try
            {
                if (from == null || to == null) return false;
                var it = TakeHandle(from);
                if (it == null) return false;
                try { it.SetInInventory(to); } catch (Exception e) { Log($"Transfer SetInInventory threw: {e.Message}"); }
                Track(to, it);
                return true;
            }
            catch (Exception e) { Log($"Transfer error: {e.Message}"); return false; }
        }

        // Move up to n doses from -> to; returns how many moved.
        internal static int TransferMany(Human from, Human to, int n)
        {
            int moved = 0;
            for (int i = 0; i < n; i++) { if (!Transfer(from, to)) break; moved++; }
            return moved;
        }

        // Destroy one dose held by `who` (e.g. consumed / deposited into an abstract stash). True if one went.
        internal static bool Destroy(Human who)
        {
            try
            {
                var it = TakeHandle(who);
                if (it == null) return false;
                try { it.SafeDelete(true); } catch (Exception e) { Log($"Destroy SafeDelete threw: {e.Message}"); }
                return true;
            }
            catch { return false; }
        }

        internal static int DestroyMany(Human who, int n)
        {
            int gone = 0;
            for (int i = 0; i < n; i++) { if (!Destroy(who)) break; gone++; }
            return gone;
        }

        // Destroy every tracked dose (operation teardown). Best-effort.
        internal static void DestroyAll()
        {
            try
            {
                foreach (var kv in _held)
                {
                    var l = kv.Value; if (l == null) continue;
                    for (int i = 0; i < l.Count; i++) { try { var it = l[i]; if (it != null) it.SafeDelete(true); } catch { } }
                }
            }
            catch { }
            _held.Clear();
        }

        // Pop and return a valid dose handle currently held by `who`, or null.
        private static Interactable TakeHandle(Human who)
        {
            try
            {
                if (who == null) return null;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) return null;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var it = l[i];
                    l.RemoveAt(i);
                    if (HeldBy(it, id)) return it;   // else it was stale; keep popping
                }
            }
            catch { }
            return null;
        }

        private static void Track(Human who, Interactable it)
        {
            try
            {
                if (who == null || it == null) return;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) { l = new List<Interactable>(); _held[id] = l; }
                l.Add(it);
            }
            catch { }
        }

        // Is this interactable alive and in the given human's inventory?
        private static bool HeldBy(Interactable it, int humanID)
        {
            try
            {
                if (it == null || it.Pointer == IntPtr.Zero) return false;
                var inv = it.inInventory;
                return inv != null && inv.humanID == humanID;
            }
            catch { return false; }
        }

        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][dose] " + m);
    }
}
