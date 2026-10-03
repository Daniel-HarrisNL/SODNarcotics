using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // Generic, MULTI-actor version of the cook's pin/herd machinery. Cook.cs drives a single actor with static
    // pointer fields; the tiered distribution needs to drive many different NPCs (distributors, dealers, buyers)
    // toward nodes or toward each other, so this keeps per-human goal state in a dictionary.
    //
    // Same proven pattern: a max-priority (100000) toGoGoal re-asserted each tick. The AI sorts goals by
    // priority, so 100000 out-prioritises the NPC's normal routine and they walk to the target.
    internal static class Herd
    {
        private class Pin { public IntPtr GoalPtr; public IntPtr NodePtr; public IntPtr PresetPtr; }
        private static readonly Dictionary<int, Pin> _pins = new Dictionary<int, Pin>();

        internal static void ReleaseAll()
        {
            try
            {
                var ids = new List<int>(_pins.Keys);
                foreach (var id in ids)
                {
                    var h = FindHuman(id);
                    if (h != null) Release(h);
                }
            }
            catch { }
            _pins.Clear();
        }

        // Drive `mover` toward a fixed node. Returns true once within rangeM of it. `preset` picks the goal preset
        // (null = the normal walk-to goal); pass e.g. RoutineControls.enforcerResponse for an urgent response.
        internal static bool ToNode(Human mover, NewNode node, NewGameLocation loc, float rangeM, AIGoalPreset preset = null)
        {
            try
            {
                if (mover == null || node == null) return false;
                Assert(mover, node, loc, preset);
                return Near(mover, node, rangeM);
            }
            catch (Exception e) { Log($"ToNode error: {e.Message}"); return false; }
        }

        // Drive `mover` toward a moving actor's live node. Returns true once within rangeM of the target.
        internal static bool ToActor(Human mover, Actor target, float rangeM, AIGoalPreset preset = null)
        {
            try
            {
                if (mover == null || target == null) return false;
                NewNode tnode = null; try { tnode = target.currentNode; } catch { }
                if (tnode == null) return false;
                NewGameLocation loc = null; try { loc = target.currentGameLocation; } catch { }
                Assert(mover, tnode, loc, preset);
                return Near(mover, tnode, rangeM);
            }
            catch (Exception e) { Log($"ToActor error: {e.Message}"); return false; }
        }

        // Re-assert (or create) the max-priority goal for `mover` toward `node`, using `preset` (default = toGoGoal).
        private static void Assert(Human mover, NewNode node, NewGameLocation loc, AIGoalPreset preset)
        {
            NewAIController ai = null; try { ai = mover.ai; } catch { }
            if (ai == null) return;

            if (preset == null) { try { var rc = RoutineControls.Instance; if (rc != null) preset = rc.toGoGoal; } catch { } }
            if (preset == null) return;
            IntPtr presetPtr = IntPtr.Zero; try { presetPtr = preset.Pointer; } catch { }

            int id; try { id = mover.humanID; } catch { return; }
            if (!_pins.TryGetValue(id, out var pin) || pin == null) { pin = new Pin(); _pins[id] = pin; }

            NewAIGoal goal = FindGoalByPtr(ai, pin.GoalPtr);
            bool sameNode = false; try { sameNode = pin.NodePtr != IntPtr.Zero && node.Pointer == pin.NodePtr; } catch { }
            bool samePreset = presetPtr != IntPtr.Zero && presetPtr == pin.PresetPtr;
            if (goal == null || !sameNode || !samePreset)
            {
                // (Re)create only when there is no goal or the TARGET NODE / PRESET changed. The node must be stable
                // across ticks (callers cache it) - a node that shifts each frame recreates the goal every frame and
                // the NPC never finishes opening a door. With a stable node the AI walks + opens its own doors
                // normally (same as the Motives herd), so no anti-stuck hack is needed.
                if (pin.GoalPtr != IntPtr.Zero) { RemoveGoalByPtr(ai, pin.GoalPtr); pin.GoalPtr = IntPtr.Zero; }
                NewAIGoal ng = null;
                try { ng = ai.CreateNewGoal(preset, NowHours(), 9999f, node, null, loc, null, null, -2); }
                catch (Exception e) { Log($"CreateNewGoal threw: {e.Message}"); return; }
                if (ng == null) return;
                goal = ng;
                try { pin.NodePtr = node.Pointer; } catch { }
                try { pin.PresetPtr = presetPtr; } catch { }
                try { pin.GoalPtr = goal.Pointer; } catch { }
            }

            try { goal.basePriority = 100000f; } catch { }
            try { goal.priority = 100000f; } catch { }
            try { goal.isActive = true; } catch { }
            try { ai.currentGoal = goal; } catch { }
        }

        // Drop `mover`'s herd goal and let its normal routine resume.
        internal static void Release(Human mover)
        {
            try
            {
                if (mover == null) return;
                int id; try { id = mover.humanID; } catch { return; }
                if (!_pins.TryGetValue(id, out var pin) || pin == null) { _pins.Remove(id); return; }
                NewAIController ai = null; try { ai = mover.ai; } catch { }
                if (ai != null && pin.GoalPtr != IntPtr.Zero)
                {
                    RemoveGoalByPtr(ai, pin.GoalPtr);
                    try { var cg = ai.currentGoal; if (cg != null && cg.Pointer == pin.GoalPtr) ai.currentGoal = null; } catch { }
                }
                _pins.Remove(id);
            }
            catch { }
        }

        internal static bool IsPinned(Human mover)
        {
            try { return mover != null && _pins.ContainsKey(mover.humanID); } catch { return false; }
        }

        private static bool Near(Human mover, NewNode node, float rangeM)
        {
            try
            {
                var cn = mover.currentNode;
                if (cn == null) return false;
                if (cn.Pointer == node.Pointer) return true;
                return Vector3.Distance(cn.position, node.position) <= rangeM;
            }
            catch { return false; }
        }

        internal static float NowHours() { try { var s = SessionData.Instance; if (s != null) return s.gameTime; } catch { } return 0f; }

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

        private static Human FindHuman(int id)
        {
            try
            {
                var cd = CityData.Instance;
                var cits = cd != null ? cd.citizenDirectory : null;
                if (cits != null)
                    for (int i = 0; i < cits.Count; i++) { var h = cits[i]; if (h != null && h.humanID == id) return h; }
            }
            catch { }
            return null;
        }

        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][herd] " + m);
    }
}
