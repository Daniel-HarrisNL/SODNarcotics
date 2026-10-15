using System;
using System.Collections.Generic;

namespace Narcotics
{
    // Drives the cook: keeps them pinned at the lab (they walk there and stay), out-prioritising their
    // normal work/sleep routine. This is the proven kidnap/sniper pin pattern from the author's other mod:
    // a toGoGoal re-asserted each tick at priority 100000 (the AI sorts goals by priority, so 100000 wins).
    // The cook keeps their job, so the "hasn't shown up to work" clue still works later.
    internal static class Cook
    {
        private static IntPtr _goalPtr = IntPtr.Zero;
        private static IntPtr _nodePtr = IntPtr.Zero;
        private static bool _nodeReachable;
        private static bool _loggedPin;
        private static bool _loggedUpgrade;
        private static IntPtr _herdGoalPtr = IntPtr.Zero;
        private static IntPtr _herdNodePtr = IntPtr.Zero;

        internal static void ResetState() { _goalPtr = IntPtr.Zero; _nodePtr = IntPtr.Zero; _nodeReachable = false; _loggedPin = false; _loggedUpgrade = false; _herdGoalPtr = IntPtr.Zero; _herdNodePtr = IntPtr.Zero; }

        // Re-assert each tick: keep a max-priority GoTo goal to a node inside the lab. The target node must be
        // a REACHABLE (walkable) one from FindSafeTeleport; the lab's anchor node is only a transient fallback
        // used until the lab geometry streams in. Once we have a reachable node we keep it (no per-tick thrash).
        internal static void Reassert(Human cook, NewGameLocation lab)
        {
            try
            {
                if (cook == null || lab == null) return;
                NewAIController ai = null; try { ai = cook.ai; } catch { }
                if (ai == null) return;

                NewNode node = null; bool reachable = false;
                try { node = cook.FindSafeTeleport(lab, false, true); } catch { }
                if (node != null) reachable = true;
                if (node == null) { try { node = lab.anchorNode; } catch { } }
                if (node == null) return;

                NewAIGoal goal = FindGoalForLocation(ai, lab);
                // (Re)create when there is no goal yet, or when we can upgrade a not-yet-reachable pin to a
                // reachable node for the first time. Do NOT chase a moving reachable node (avoids thrash) and
                // do NOT downgrade a reachable pin back to the anchor fallback.
                bool recreate = goal == null || (reachable && !_nodeReachable);
                if (recreate)
                {
                    RemoveAllSiteGoals(ai, lab);
                    // WALK to the lab, don't run: toGoGoal forces running (looked unnatural). Fall back to it only
                    // if the walk goal is missing, so the cook still gets pinned.
                    AIGoalPreset preset = null; try { var rc = RoutineControls.Instance; if (rc != null) { preset = rc.toGoWalkGoal; if (preset == null) preset = rc.toGoGoal; } } catch { }
                    if (preset == null) return;
                    NewAIGoal ng = null;
                    try { ng = ai.CreateNewGoal(preset, NowHours(), 9999f, node, null, lab, null, null, -2); }
                    catch (Exception e) { Log($"CreateNewGoal threw: {e.Message}"); return; }
                    if (ng == null) return;
                    goal = ng;
                    try { _nodePtr = node.Pointer; } catch { }
                    try { _goalPtr = goal.Pointer; } catch { }
                    _nodeReachable = reachable;
                    if (!_loggedPin) { _loggedPin = true; Log($"pinned {NarcoticsPlugin.Name(cook)} to lab {LName(lab)} (node={(reachable ? "reachable" : "anchor-fallback, will upgrade when the lab streams")}, priority 100000)."); }
                    else if (reachable && !_loggedUpgrade) { _loggedUpgrade = true; Log($"upgraded {NarcoticsPlugin.Name(cook)}'s lab pin to a reachable node; they can path now."); }
                }

                try { goal.basePriority = 100000f; } catch { }
                try { goal.priority = 100000f; } catch { }
                try { goal.isActive = true; } catch { }
                try { ai.currentGoal = goal; } catch { }
            }
            catch (Exception e) { Log($"Reassert error: {e.Message}"); }
        }

        internal static void Release(Human cook, NewGameLocation lab)
        {
            try
            {
                if (cook == null) return;
                NewAIController ai = null; try { ai = cook.ai; } catch { }
                if (ai == null) return;
                int before = GoalCount(ai);
                int removed = 0;
                if (lab != null) removed += RemoveAllSiteGoals(ai, lab);
                if (_goalPtr != IntPtr.Zero && RemoveGoalByPtr(ai, _goalPtr)) removed++;
                // Our removed goal may still be the AI's currentGoal, which leaves the cook idle AT the lab
                // (the reported "F6 did nothing"). Clear it so the AI re-picks its next routine goal and leaves.
                try { var cg = ai.currentGoal; if (cg == null || cg.Pointer == _goalPtr) ai.currentGoal = null; } catch { }
                int after = GoalCount(ai);
                Log($"released {NarcoticsPlugin.Name(cook)}: removed {removed} pin goal(s), goals {before}->{after}, currentGoal cleared; routine resumes.");
            }
            catch (Exception e) { Log($"Release error: {e.Message}"); }
            finally { ResetState(); }
        }

        private static int GoalCount(NewAIController ai) { try { var g = ai.goals; return g != null ? g.Count : -1; } catch { return -1; } }

        // Drop the lab pin goal (used when the cook leaves the lab to go deliver). Keeps other state.
        internal static void RemoveLabGoal(Human cook)
        {
            try
            {
                NewAIController ai = null; try { ai = cook != null ? cook.ai : null; } catch { }
                if (ai == null) return;
                if (_goalPtr != IntPtr.Zero) { RemoveGoalByPtr(ai, _goalPtr); _goalPtr = IntPtr.Zero; }
                _nodePtr = IntPtr.Zero; _nodeReachable = false;
            }
            catch { }
        }

        // Herd the cook toward a MOVING actor (a dealer) by re-issuing a max-priority GoTo goal to the actor's
        // live node each tick. Returns true once the cook is within rangeM (handoff range). Proven herd pattern.
        internal static bool HerdToActor(Human cook, Actor target, float rangeM)
        {
            try
            {
                if (cook == null || target == null) return false;
                NewAIController ai = null; try { ai = cook.ai; } catch { }
                if (ai == null) return false;
                NewNode tnode = null; try { tnode = target.currentNode; } catch { }
                if (tnode == null) return false;

                NewAIGoal goal = FindGoalByPtr(ai, _herdGoalPtr);
                bool sameNode = false; try { sameNode = _herdNodePtr != IntPtr.Zero && tnode.Pointer == _herdNodePtr; } catch { }
                if (goal == null || !sameNode)
                {
                    if (_herdGoalPtr != IntPtr.Zero) { RemoveGoalByPtr(ai, _herdGoalPtr); _herdGoalPtr = IntPtr.Zero; }
                    // WALK toward the dealer, don't run (toGoGoal forces running). Fall back to the run goal only if
                    // the walk goal is unavailable.
                    AIGoalPreset preset = null; try { var rc = RoutineControls.Instance; if (rc != null) { preset = rc.toGoWalkGoal; if (preset == null) preset = rc.toGoGoal; } } catch { }
                    if (preset == null) return false;
                    NewGameLocation loc = null; try { loc = target.currentGameLocation; } catch { }
                    NewAIGoal ng = null;
                    try { ng = ai.CreateNewGoal(preset, NowHours(), 9999f, tnode, null, loc, null, null, -2); }
                    catch (Exception e) { Log($"HerdToActor CreateNewGoal threw: {e.Message}"); return false; }
                    if (ng == null) return false;
                    goal = ng;
                    try { _herdNodePtr = tnode.Pointer; } catch { }
                    try { _herdGoalPtr = goal.Pointer; } catch { }
                }
                try { goal.basePriority = 100000f; } catch { }
                try { goal.priority = 100000f; } catch { }
                try { goal.isActive = true; } catch { }
                try { ai.currentGoal = goal; } catch { }

                try
                {
                    var cn = cook.currentNode;
                    if (cn != null)
                    {
                        if (cn.Pointer == tnode.Pointer) return true;
                        if (UnityEngine.Vector3.Distance(cn.position, tnode.position) <= rangeM) return true;
                    }
                }
                catch { }
                return false;
            }
            catch (Exception e) { Log($"HerdToActor error: {e.Message}"); return false; }
        }

        // Drop the herd goal (used after a handoff, so the lab pin can take over again).
        internal static void ClearHerd(Human cook)
        {
            try
            {
                NewAIController ai = null; try { ai = cook != null ? cook.ai : null; } catch { }
                if (ai == null) { _herdGoalPtr = IntPtr.Zero; _herdNodePtr = IntPtr.Zero; return; }
                if (_herdGoalPtr != IntPtr.Zero) { RemoveGoalByPtr(ai, _herdGoalPtr); _herdGoalPtr = IntPtr.Zero; }
                _herdNodePtr = IntPtr.Zero;
            }
            catch { }
        }

        private static NewAIGoal FindGoalByPtr(NewAIController ai, IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var goals = ai != null ? ai.goals : null;
                if (goals == null) return null;
                for (int i = 0; i < goals.Count; i++)
                {
                    var g = goals[i]; if (g == null) continue;
                    try { if (g.Pointer == ptr) return g; } catch { }
                }
            }
            catch { }
            return null;
        }

        internal static float NowHours() { try { var s = SessionData.Instance; if (s != null) return s.gameTime; } catch { } return 0f; }

        private static NewAIGoal FindGoalForLocation(NewAIController ai, NewGameLocation loc)
        {
            try
            {
                var goals = ai != null ? ai.goals : null;
                if (goals == null || loc == null) return null;
                for (int i = 0; i < goals.Count; i++)
                {
                    var g = goals[i]; if (g == null) continue;
                    NewGameLocation gl = null, pg = null;
                    try { gl = g.gameLocation; } catch { }
                    try { pg = g.passedGameLocation; } catch { }
                    try { if ((gl != null && gl.Pointer == loc.Pointer) || (pg != null && pg.Pointer == loc.Pointer)) return g; } catch { }
                }
            }
            catch { }
            return null;
        }

        private static int RemoveAllSiteGoals(NewAIController ai, NewGameLocation loc)
        {
            int n = 0;
            try
            {
                var seen = new HashSet<IntPtr>();
                for (int iter = 0; iter < 16; iter++)
                {
                    NewAIGoal g = FindGoalForLocation(ai, loc);
                    if (g == null) return n;
                    IntPtr p; try { p = g.Pointer; } catch { return n; }
                    if (!seen.Add(p)) return n;
                    try { g.basePriority = 0f; g.priority = 0f; g.isActive = false; } catch { }
                    try { g.Remove(); n++; } catch { return n; }
                }
            }
            catch { }
            return n;
        }

        private static bool RemoveGoalByPtr(NewAIController ai, IntPtr ptr)
        {
            try
            {
                var goals = ai != null ? ai.goals : null;
                if (goals == null) return false;
                int gc = 0; try { gc = goals.Count; } catch { }
                for (int i = 0; i < gc; i++)
                {
                    var g = goals[i]; if (g == null) continue;
                    IntPtr p; try { p = g.Pointer; } catch { continue; }
                    if (p != ptr) continue;
                    try { g.basePriority = 0f; g.priority = 0f; g.isActive = false; } catch { }
                    try { g.Remove(); } catch { }
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static string LName(NewGameLocation l) { try { return l != null ? l.name : "<null>"; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][cook] " + m);
    }
}
