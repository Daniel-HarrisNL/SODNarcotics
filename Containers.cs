using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // Sites the dead drops as REAL containers (street bins, mailboxes, lockers) closest to the OUTSIDE of the
    // lab's building, and picks the nearest homeless as dealers. Depositing into a container uses the game's own
    // furniture-spawn / trash pipeline, so the drop is persistent + searchable even when the player is nowhere
    // near (unlike loose-on-the-ground items, which only exist while the area is streamed). This is what makes
    // the supply chain run reliably while the player is elsewhere.
    internal static class Containers
    {
        private sealed class Cand { public FurnitureLocation Fl; public float Dist; public bool Outside; public string Name; public int Subs; }

        // A reference point for "just outside the lab's building": the building main-entrance's outside node,
        // else the building centre, else the lab's own entrance / anchor.
        internal static Vector3 ExteriorAnchor(NewAddress lab, out NewBuilding building)
        {
            building = null;
            try { building = lab != null ? lab.building : null; } catch { }
            if (building != null)
            {
                try
                {
                    var w = building.mainEntrance;
                    if (w != null)
                    {
                        NewNode a = null, b = null;
                        try { a = w.node; } catch { }
                        try { var ow = w.otherWall; if (ow != null) b = ow.node; } catch { }
                        NewNode outside = null;
                        try { if (a != null && a.isOutside) outside = a; } catch { }
                        if (outside == null) { try { if (b != null && b.isOutside) outside = b; } catch { } }
                        if (outside == null) outside = a ?? b;
                        if (outside != null) { try { return outside.position; } catch { } }
                    }
                }
                catch { }
                try { return building.worldPosition; } catch { }
            }
            try { var me = lab.GetMainEntrance(); if (me != null) return me.worldAccessPoint; } catch { }
            try { var an = lab.anchorNode; if (an != null) return an.position; } catch { }
            return Vector3.zero;
        }

        // A pool of usable containers near the lab building exterior, NEAREST FIRST. Prefers OUTSIDE street bins
        // (the design's dead drops); only if there are none does it fall back to any container near the lab, so the
        // caller always has a real searchable bin. The caller then picks the nearest one that a CCTV camera films.
        internal static List<FurnitureLocation> SiteContainersNearLab(NewAddress lab, int n)
        {
            var result = new List<FurnitureLocation>();
            try
            {
                NewBuilding bld; Vector3 anchor = ExteriorAnchor(lab, out bld);

                var locs = new List<NewGameLocation>();
                if (bld != null) { try { var st = bld.street; if (st != null) locs.Add(st); } catch { } }
                AddNearbyStreets(anchor, locs);                 // widen the search to streets around the lab
                if (locs.Count == 0 && lab != null) locs.Add(lab);

                var found = new List<Cand>();
                var seen = new HashSet<IntPtr>();
                for (int i = 0; i < locs.Count; i++) CollectContainers(locs[i], anchor, found, seen);

                // Prefer OUTSIDE containers (street bins/dumpsters); fall back to ANY container near the lab only when
                // there are no outside ones, so the supply chain always has a real searchable bin. Sort NEAREST FIRST
                // (the caller wants the closest bin to the lab, then prefers one on camera).
                var outside = new List<Cand>();
                for (int i = 0; i < found.Count; i++) if (found[i].Outside) outside.Add(found[i]);
                var pool = outside.Count > 0 ? outside : found;
                pool.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                for (int i = 0; i < pool.Count && result.Count < n; i++) result.Add(pool[i].Fl);

                Log($"sited {result.Count}/{n} container(s) near the lab ({outside.Count} outside / {found.Count} total, {locs.Count} location(s) scanned; using {(outside.Count > 0 ? "outside" : "any")}).");
                for (int i = 0; i < pool.Count && i < 12; i++) { var c = pool[i]; Log($"  cand: '{c.Name}' outside={c.Outside} subs={c.Subs} {c.Dist:0}m{(i < result.Count ? "  <= in pool" : "")}"); }
                if (result.Count == 0) DumpNearbyFurniture(locs, anchor);
            }
            catch (Exception e) { Log($"SiteContainersNearLab error: {e.Message}"); }
            return result;
        }

        private static void AddNearbyStreets(Vector3 anchor, List<NewGameLocation> locs)
        {
            try
            {
                var cd = CityData.Instance;
                var sd = cd != null ? cd.streetDirectory : null;
                if (sd == null) return;
                var have = new HashSet<IntPtr>();
                foreach (var l in locs) { try { have.Add(l.Pointer); } catch { } }
                for (int i = 0; i < sd.Count; i++)
                {
                    var st = sd[i]; if (st == null) continue;
                    IntPtr p; try { p = st.Pointer; } catch { continue; }
                    if (have.Contains(p)) continue;
                    try { var an = st.anchorNode; if (an != null && Vector3.Distance(anchor, an.position) <= 140f) { locs.Add(st); have.Add(p); } } catch { }
                }
            }
            catch { }
        }

        private static void CollectContainers(NewGameLocation loc, Vector3 anchor, List<Cand> found, HashSet<IntPtr> seen)
        {
            try
            {
                if (loc == null) return;
                var rooms = loc.rooms;
                if (rooms != null) for (int i = 0; i < rooms.Count; i++) { var r = rooms[i]; if (r == null) continue; var list = r.individualFurniture; if (list != null) for (int j = 0; j < list.Count; j++) Consider(list[j], anchor, found, seen); }
                var nodes = loc.nodes;
                if (nodes != null) for (int i = 0; i < nodes.Count; i++) { var nd = nodes[i]; if (nd == null) continue; var list = nd.individualFurniture; if (list != null) for (int j = 0; j < list.Count; j++) Consider(list[j], anchor, found, seen); }
            }
            catch { }
        }

        private static void Consider(FurnitureLocation fl, Vector3 anchor, List<Cand> found, HashSet<IntPtr> seen)
        {
            try
            {
                if (fl == null) return;
                IntPtr p; try { p = fl.Pointer; } catch { return; }
                if (!seen.Add(p)) return;
                if (!IsContainer(fl)) return;
                var c = new Cand { Fl = fl, Dist = DistTo(anchor, fl) };
                try { var an = fl.anchorNode; c.Outside = an != null && an.isOutside; } catch { }
                try { c.Name = fl.furniture != null ? fl.furniture.name : "?"; } catch { c.Name = "?"; }
                try { c.Subs = (fl.furniture != null && fl.furniture.subObjects != null) ? fl.furniture.subObjects.Count : 0; } catch { }
                found.Add(c);
            }
            catch { }
        }

        // A real TRASH BIN by name (dumpster/skip/bin/trash/garbage/waste/recycling) - what the dead-drop design wants.
        // Excludes 'poster' (the broad 'post' keyword used to catch it) and 'cabinet' (which contains the substring
        // "bin"), so only genuine street trash containers qualify. The caller still validates with Dose.CanDeposit.
        internal static bool IsTrashBin(FurnitureLocation fl)
        {
            try
            {
                string nm = fl != null && fl.furniture != null ? fl.furniture.name : null;
                if (string.IsNullOrEmpty(nm)) return false;
                var l = nm.ToLowerInvariant();
                if (l.Contains("poster")) return false;
                if (l.Contains("dumpster") || l.Contains("skip") || l.Contains("recycl")
                 || l.Contains("trash") || l.Contains("garbage") || l.Contains("waste")) return true;
                if (l.Contains("bin") && !l.Contains("cabinet")) return true;
            }
            catch { }
            return false;
        }

        // A container = a bin/mailbox/locker by NAME (accepted even with no sub-object slot - public bins use the
        // trash system, not a slot), OR storage-class furniture (mailboxes/locker/drawers/safe) that has a slot.
        internal static bool IsContainer(FurnitureLocation fl)
        {
            try
            {
                var preset = fl.furniture;
                if (preset == null) return false;

                string nm = null; try { nm = preset.name; } catch { }
                if (!string.IsNullOrEmpty(nm))
                {
                    var low = nm.ToLowerInvariant();
                    if (low.Contains("poster")) { /* decorative, not a container - fall through to slot check */ }
                    else if (low.Contains("bin") || low.Contains("trash") || low.Contains("waste") || low.Contains("garbage")
                     || low.Contains("recycl") || low.Contains("dumpster") || low.Contains("mail") || low.Contains("postbox") || low.Contains("post box")
                     || low.Contains("locker") || low.Contains("cabinet") || low.Contains("drawer")) return true;
                }

                var subs = preset.subObjects;
                bool hasSlot = subs != null && subs.Count > 0;
                if (!hasSlot) return false;
                var classes = fl.furnitureClasses;
                if (classes != null)
                    for (int i = 0; i < classes.Count; i++)
                    {
                        var c = classes[i]; if (c == null) continue;
                        FurnitureClass.OwnershipClass oc; try { oc = c.ownershipClass; } catch { continue; }
                        if (oc == FurnitureClass.OwnershipClass.mailboxes || oc == FurnitureClass.OwnershipClass.locker
                         || oc == FurnitureClass.OwnershipClass.drawers || oc == FurnitureClass.OwnershipClass.safe) return true;
                    }
            }
            catch { }
            return false;
        }

        // A slot to spawn an item into: prefer an unsecured one; else the first. Null if the furniture has no slots.
        internal static FurniturePreset.SubObject PickSlot(FurnitureLocation fl)
        {
            try
            {
                var subs = fl.furniture.subObjects;
                if (subs == null || subs.Count == 0) return null;
                FurniturePreset.SubObject best = null;
                for (int i = 0; i < subs.Count; i++)
                {
                    var s = subs[i]; if (s == null) continue;
                    if (best == null) best = s;
                    int sec = 0; try { sec = s.security; } catch { }
                    if (sec == 0) { best = s; break; }
                }
                return best;
            }
            catch { return null; }
        }

        internal static NewNode ContainerNode(FurnitureLocation fl)
        {
            try { var n = fl.anchorNode; if (n != null) return n; } catch { }
            try { var cv = fl.coversNodes; if (cv != null && cv.Count > 0) return cv[0]; } catch { }
            return null;
        }

        // The N homeless nearest to the lab building exterior (stable measure: their sleep spot, else current node).
        internal static List<Human> PickNearestHomeless(NewAddress lab, int n, HashSet<int> used)
        {
            var result = new List<Human>();
            try
            {
                NewBuilding bld; Vector3 anchor = ExteriorAnchor(lab, out bld);
                var cd = CityData.Instance;
                var homeless = cd != null ? cd.homelessDirectory : null;
                if (homeless == null) { Log("no homelessDirectory; can't pick dealers."); return result; }

                var cand = new List<KeyValuePair<Human, float>>();
                for (int i = 0; i < homeless.Count; i++)
                {
                    Human h = null; try { h = homeless[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (used.Contains(id)) continue;
                    Vector3 p; if (!Measure(h, out p)) continue;
                    cand.Add(new KeyValuePair<Human, float>(h, Vector3.Distance(anchor, p)));
                }
                cand.Sort((x, y) => x.Value.CompareTo(y.Value));
                for (int i = 0; i < cand.Count && result.Count < n; i++)
                {
                    result.Add(cand[i].Key);
                    try { used.Add(cand[i].Key.humanID); } catch { }
                }
                Log($"picked {result.Count}/{n} nearest homeless dealer(s) to the lab.");
            }
            catch (Exception e) { Log($"PickNearestHomeless error: {e.Message}"); }
            return result;
        }

        // One-time diagnostic: list nearby furniture (name / outside / slots / distance) so we can identify the
        // real trash-bin furniture when the container search comes up short.
        private static void DumpNearbyFurniture(List<NewGameLocation> locs, Vector3 anchor)
        {
            try
            {
                Log("  (few containers found - dumping nearby furniture within 30m to identify bins:)");
                int shown = 0; var seen = new HashSet<IntPtr>();
                for (int L = 0; L < locs.Count && shown < 30; L++)
                {
                    var loc = locs[L]; if (loc == null) continue;
                    var nodes = loc.nodes; if (nodes == null) continue;
                    for (int i = 0; i < nodes.Count && shown < 30; i++)
                    {
                        var nd = nodes[i]; if (nd == null) continue;
                        var list = nd.individualFurniture; if (list == null) continue;
                        for (int j = 0; j < list.Count && shown < 30; j++)
                        {
                            var fl = list[j]; if (fl == null) continue;
                            IntPtr p; try { p = fl.Pointer; } catch { continue; }
                            if (!seen.Add(p)) continue;
                            float d = DistTo(anchor, fl); if (d > 30f) continue;
                            string nm = "?"; try { nm = fl.furniture != null ? fl.furniture.name : "?"; } catch { }
                            int subs = 0; try { subs = (fl.furniture != null && fl.furniture.subObjects != null) ? fl.furniture.subObjects.Count : 0; } catch { }
                            bool outside = false; try { var an = fl.anchorNode; if (an != null) outside = an.isOutside; } catch { }
                            Log($"    furn: '{nm}' outside={outside} subs={subs} {d:0}m"); shown++;
                        }
                    }
                }
            }
            catch { }
        }

        private static bool Measure(Human h, out Vector3 p)
        {
            p = Vector3.zero;
            try { var sp = h.sleepPosition; if (sp != null) { var n = sp.node; if (n != null) { p = n.position; return true; } } } catch { }
            try { var cn = h.currentNode; if (cn != null) { p = cn.position; return true; } } catch { }
            return false;
        }

        private static float DistTo(Vector3 anchor, FurnitureLocation fl)
        {
            try { return Vector3.Distance(anchor, fl.GetWorldAveragePosition()); } catch { }
            try { var an = fl.anchorNode; if (an != null) return Vector3.Distance(anchor, an.position); } catch { }
            return float.MaxValue;
        }

        private static bool Alive(Human h) { if (h == null) return false; try { return !h.isDead; } catch { return true; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][sites] " + m);
    }
}
