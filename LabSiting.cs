using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // Sites the lab: the cook's home if they live alone, else a private vacant address
    // (unowned "Vacant address N" units with a lockable door, preferring one near the cook's home
    // so the operation stays geographically localised for the later "hone in on the area" mechanic).
    // The vacant-address selection reuses the proven kidnap-den picker from the author's other mod.
    internal static class LabSiting
    {
        private static readonly System.Random _rng = new System.Random();

        internal static NewAddress PickLab(Human cook, out bool usedHome)
        {
            usedHome = false;   // never use the cook's real home; the lab is always a separate location the cook can walk into
            try
            {
                NewAddress home = null; try { home = cook.home; } catch { }
                return PickVacantAddress(home);   // a walk-in vacant unit, preferably near the cook's home for clustering
            }
            catch (Exception e) { Log($"PickLab error: {e.Message}"); return null; }
        }

        // Walk-in "Vacant address" shells: the cook can path into them freely (they are not private). Making the lab
        // private/trespassable is PARKED for later (a private unit is inaccessible to the cook too - see DESIGN.md).
        private static NewAddress PickVacantAddress(NewAddress near)
        {
            try
            {
                var cd = CityData.Instance;
                var dir = cd != null ? cd.addressDirectory : null;
                if (dir == null) return null;
                var vacant = new List<NewAddress>();
                for (int i = 0; i < dir.Count; i++)
                {
                    var a = dir[i]; if (a == null) continue;
                    int occ = 0; try { var inh = a.inhabitants; occ = inh != null ? inh.Count : 0; } catch { occ = -1; }
                    if (occ != 0) continue;
                    string n = null; try { n = a.name; } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.IndexOf("Basement", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf("Vacant", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!HasLockableDoor(a)) continue;
                    vacant.Add(a);
                }
                Log($"vacant-lab pool: {vacant.Count} candidates.");
                if (vacant.Count == 0) return null;
                if (near != null)
                {
                    bool haveHp = false; Vector3 hp = Vector3.zero;
                    try { var hn = near.anchorNode; if (hn != null) { hp = hn.position; haveHp = true; } } catch { }
                    if (haveHp)
                    {
                        SortByDistance(vacant, hp);
                        return vacant[_rng.Next(Math.Min(5, vacant.Count))];
                    }
                }
                return vacant[_rng.Next(vacant.Count)];
            }
            catch (Exception e) { Log($"PickVacantAddress error: {e.Message}"); return null; }
        }

        private static void SortByDistance(List<NewAddress> list, Vector3 from)
        {
            try { list.Sort((a, b) => Dist(a, from).CompareTo(Dist(b, from))); } catch { }
        }
        private static float Dist(NewAddress a, Vector3 from)
        {
            try { var n = a.anchorNode; if (n != null) return Vector3.Distance(n.position, from); } catch { }
            return float.MaxValue;
        }

        // Close + lock the lab's entrance door(s) so it reads as a private, secured lab and the player/NPCs
        // are trespassing. The owner (cook) keeps access. Returns the number of doors locked (0 if the lab
        // geometry is not streamed yet - the caller retries). Mirrors the Talk-to-Partner close+lock pattern.
        internal static int LockLab(NewGameLocation lab, Human owner)
        {
            int locked = 0;
            try
            {
                if (lab == null) return 0;
                var ents = lab.entrances;
                if (ents == null) return 0;
                for (int i = 0; i < ents.Count; i++)
                {
                    var e = ents[i]; if (e == null) continue;
                    NewDoor d = null; try { d = e.door; } catch { }
                    if (d == null) continue;
                    try { if (!d.isClosed) d.SetOpen(0f, owner, false, 1f); } catch { }
                    try { d.SetLocked(true, owner, false); locked++; } catch (Exception ex) { Log($"lock door failed: {ex.Message}"); }
                }
            }
            catch (Exception e) { Log($"LockLab error: {e.Message}"); }
            return locked;
        }

        // Unlock (and leave closed) the lab's door(s) so a released cook is not trapped behind a locked door
        // after we strip their ownership. Returns the number of doors unlocked.
        internal static int UnlockLab(NewGameLocation lab, Human actor)
        {
            int n = 0;
            try
            {
                if (lab == null) return 0;
                var ents = lab.entrances;
                if (ents == null) return 0;
                for (int i = 0; i < ents.Count; i++)
                {
                    var e = ents[i]; if (e == null) continue;
                    NewDoor d = null; try { d = e.door; } catch { }
                    if (d == null) continue;
                    try { d.SetLocked(false, actor, false); n++; } catch { }
                }
            }
            catch (Exception e) { Log($"UnlockLab error: {e.Message}"); }
            return n;
        }

        private static bool HasLockableDoor(NewAddress a)
        {
            try
            {
                var ents = a.entrances;
                if (ents != null)
                    for (int i = 0; i < ents.Count; i++)
                    {
                        var e = ents[i]; if (e == null) continue;
                        try { if (e.door != null) return true; } catch { }
                    }
                try { var me = a.GetMainEntrance(); if (me != null && me.door != null) return true; } catch { }
            }
            catch { }
            return false;
        }

        // Dress a room in the lab with lab-reading furniture clusters. Best-effort: reliable when the lab
        // geometry is streamed (stand in the lab and call it). Distant addresses may have no rooms loaded yet.
        internal static int DressLab(NewGameLocation lab)
        {
            int placed = 0;
            try
            {
                if (lab == null) return 0;
                var rooms = lab.rooms;
                if (rooms == null || rooms.Count == 0) { Log("DressLab: no rooms streamed (stand in the lab and retry)."); return 0; }
                NewRoom room = null;
                for (int i = 0; i < rooms.Count; i++) { var r = rooms[i]; if (r != null) { room = r; break; } }
                if (room == null) return 0;

                var clusters = FindLabClusters();
                if (clusters.Count == 0) { Log("DressLab: no lab clusters found in Toolbox.allFurnitureClusters."); return 0; }
                var gc = GenerationController.Instance;
                if (gc == null) return 0;
                foreach (var cluster in clusters)
                {
                    try
                    {
                        var loc = gc.GetBestFurnitureClusterLocation(room, cluster, false, true);
                        if (loc == null) continue;
                        room.AddFurniture(loc, true, true, true, true, null);   // generateNew, addPathBlocking, immediateSpawn, ignoreLimitations
                        placed++;
                    }
                    catch (Exception e) { Log($"DressLab: place '{ClName(cluster)}' failed: {e.Message}"); }
                }
                Log($"DressLab: placed {placed} lab cluster(s) in room '{RName(room)}'.");
            }
            catch (Exception e) { Log($"DressLab error: {e.Message}"); }
            return placed;
        }

        private static readonly string[] LabClusterNames = { "Machinery01", "Machinery02", "Machinery03", "EEGMachine", "WorkbenchAgainstWall", "SciencePictureLab", "MedicineCabinet", "MedicineCart" };
        private static List<FurnitureCluster> FindLabClusters()
        {
            var result = new List<FurnitureCluster>();
            try
            {
                var all = Toolbox.Instance != null ? Toolbox.Instance.allFurnitureClusters : null;
                if (all == null) return result;
                var wanted = new HashSet<string>(LabClusterNames, StringComparer.OrdinalIgnoreCase);
                foreach (var c in all)
                {
                    if (c == null) continue;
                    string nm = ClName(c);
                    if (!string.IsNullOrEmpty(nm) && wanted.Contains(nm)) result.Add(c);
                }
            }
            catch { }
            return result;
        }

        private static string ClName(FurnitureCluster c) { try { return c.name; } catch { return null; } }
        private static string RName(NewRoom r) { try { return r.name; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][lab] " + m);
    }
}
