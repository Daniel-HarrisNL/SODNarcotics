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

        // SOFT drive toward a fixed node: assigns a top-priority goal ONCE and then leaves it alone (does NOT re-slam
        // currentGoal / priority every tick like the hard pin does). The AI still walks there (priority 100000 wins the
        // goal sort), but because we don't clobber currentGoal each frame, the vanilla "being followed" reaction - which
        // inserts a spook/confront action into currentGoal - can attach and run while the NPC is in transit. Use this
        // for travel where we want reactions; use ToNode (hard) where the NPC must be rigidly held. Returns true within rangeM.
        internal static bool ToNodeSoft(Human mover, NewNode node, NewGameLocation loc, float rangeM, AIGoalPreset preset = null)
        {
            try
            {
                if (mover == null || node == null) return false;
                AssertSoft(mover, node, loc, preset);
                return Near(mover, node, rangeM);
            }
            catch (Exception e) { Log($"ToNodeSoft error: {e.Message}"); return false; }
        }

        // Create the goal if missing (or the node/preset changed) and kick it off ONCE; otherwise leave it untouched so
        // the AI can interrupt it with a reaction and then re-elect it (its priority stays 100000 in the goal list).
        private static void AssertSoft(Human mover, NewNode node, NewGameLocation loc, AIGoalPreset preset)
        {
            NewAIController ai = null; try { ai = mover.ai; } catch { }
            if (ai == null) return;
            if (preset == null) { try { var rc = RoutineControls.Instance; if (rc != null) { preset = rc.toGoWalkGoal; if (preset == null) preset = rc.toGoGoal; } } catch { } }
            if (preset == null) return;
            IntPtr presetPtr = IntPtr.Zero; try { presetPtr = preset.Pointer; } catch { }

            int id; try { id = mover.humanID; } catch { return; }
            if (!_pins.TryGetValue(id, out var pin) || pin == null) { pin = new Pin(); _pins[id] = pin; }

            NewAIGoal goal = FindGoalByPtr(ai, pin.GoalPtr);
            bool sameNode = false; try { sameNode = pin.NodePtr != IntPtr.Zero && node.Pointer == pin.NodePtr; } catch { }
            bool samePreset = presetPtr != IntPtr.Zero && presetPtr == pin.PresetPtr;
            if (goal == null || !sameNode || !samePreset)
            {
                if (pin.GoalPtr != IntPtr.Zero) { RemoveGoalByPtr(ai, pin.GoalPtr); pin.GoalPtr = IntPtr.Zero; }
                NewAIGoal ng = null;
                try { ng = ai.CreateNewGoal(preset, NowHours(), 9999f, node, null, loc, null, null, -2); }
                catch (Exception e) { Log($"AssertSoft CreateNewGoal threw: {e.Message}"); return; }
                if (ng == null) return;
                goal = ng;
                try { pin.NodePtr = node.Pointer; } catch { }
                try { pin.PresetPtr = presetPtr; } catch { }
                try { pin.GoalPtr = goal.Pointer; } catch { }
                try { goal.basePriority = 100000f; } catch { }
                try { goal.priority = 100000f; } catch { }
                try { goal.isActive = true; } catch { }
                try { ai.currentGoal = goal; } catch { }   // kick off the walk ONCE; do NOT re-slam each tick
            }
            // else: goal already present for this node/preset -> leave it (no re-slam) so a vanilla reaction can take over.
        }

        // Re-assert (or create) the max-priority goal for `mover` toward `node`, using `preset` (default = toGoGoal).
        private static void Assert(Human mover, NewNode node, NewGameLocation loc, AIGoalPreset preset)
        {
            NewAIController ai = null; try { ai = mover.ai; } catch { }
            if (ai == null) return;

            // Default to the WALK-to goal, not toGoGoal: toGoGoal's move action forces RUNNING, which made every
            // herded NPC (cook, distributor, dealer, buyer, pinned arrest subject) sprint around unnaturally. The
            // arrest enforcer passes its own urgent response goal explicitly, so it is unaffected. Fall back to the
            // running goal only if the walk goal is unavailable, so movement never stalls.
            if (preset == null) { try { var rc = RoutineControls.Instance; if (rc != null) { preset = rc.toGoWalkGoal; if (preset == null) preset = rc.toGoGoal; } } catch { } }
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

        // True when `mover` is actively reacting to something (tail/confront/flee/investigate). The vanilla reaction
        // inserts a spook/confront action into the NPC's currentGoal and sets reactionState/spooked; our priority-100000
        // pin FIGHTS that (re-slamming currentGoal), which traps the NPC in a flailing "under attack" loop. Callers use
        // this to RELEASE the pin and get out of the way until the NPC calms. reactionState is the transient state
        // machine (none when calm); spooked is a decaying fear timer (spookForgetCounter clears it).
        // We key on reactionState ONLY: it is the AI's transient reaction state machine (none = calm; the tail
        // confront/pursuit shows up as investigatingSight/persuing/searching), so it reliably clears when the player
        // stops tailing. We deliberately do NOT also gate on the `spooked` float - its decay semantics are unverified,
        // and if it ever held a nonzero baseline the dealer would never deal or collect. reactionState is enough to
        // stop the pin fighting the reaction (the actual bug); a benign distraction just defers a deal, which is fine.
        internal static bool IsReacting(Human mover)
        {
            try
            {
                var ai = mover != null ? mover.ai : null;
                if (ai == null) return false;
                return ai.reactionState != NewAIController.ReactionState.none;
            }
            catch { return false; }
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
