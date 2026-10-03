using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // The arrest (Phase 4, the synthetic "RED" part), built around the user's "erratic buyer" design:
    //   make the subject behave erratically (drunk + sick), summon enforcers to the scene (CallEnforcers, which
    //   also fires the scanner notify + brings on-duty enforcers over), AND command the nearest REAL enforcer
    //   (GameplayController.enforcers) to walk up and restrain them - because CallEnforcers alone gives the
    //   responders no target, so they just mill about. On contact the subject is SetRestrained (re-asserted every
    //   frame), held briefly, then RELEASED (never removed from the world - they stay tail-able/question-able).
    //
    // We do NOT set illegalActionActive on the subject: that marks them a violent aggressor, so bystanders ATTACK
    // them instead of reporting. Tested via F8 on the nearest NPC; later triggered by a buyer consuming a dose.
    internal static class Arrest
    {
        internal static bool Active;
        internal static Human Enforcer;   // the responding enforcer we drive to the subject
        internal static Human Subject;    // the overdosing NPC being arrested

        private enum Phase { Approach, Detain }
        private static Phase _phase;
        private static bool _openCase;         // open/feed the custom Case when this subject is restrained
        private static float _holdUntilHours;  // game hours: keep them cuffed in place until this time (or case solved)
        private static NewNode _subjectNode;   // where the subject collapsed - we pin them here so they don't drift off
        private static NewGameLocation _subjectLoc;
        private static NewNode _guardNode;     // primary guard post (building entrance / location anchor) - "near the door"
        private static NewNode _guardNode2;    // fallback guard post (where the officer restrained them, ~1.8m from the suspect)
        private static NewGameLocation _guardLoc;
        private static float _deadline;        // real-time seconds (give enforcers time to travel)
        private static float _nextReassert;    // real-time seconds; keep the subject visibly erratic
        internal static bool SubjectSearched;  // set by the OnSearch patch when the player frisks the arrestee

        private const float ContactM = 1.8f;
        private const float ApproachTimeoutSec = 40f;   // if no enforcer reaches the subject by now, self-arrest so the case still opens
        private static float _nextApproachLog;

        // F8 (debug): overdose the NEAREST NPC and arrest them. Also opens the case, so the full chain end can be
        // tested on demand without waiting for a buyer's scheduled overdose.
        internal static void Overdose()
        {
            try
            {
                var player = Player.Instance;
                NewNode pn = null; try { pn = player != null ? player.currentNode : null; } catch { }
                if (pn == null) { Log("no player node (load a game)."); return; }
                Human subj = NearestNPC(pn.position, -1);
                if (subj == null) { Log("no nearby NPC - move somewhere with people."); return; }
                Begin(subj, true);
            }
            catch (Exception e) { Log($"Overdose error: {e.Message}"); }
        }

        // Start an arrest on a SPECIFIC subject: make them erratic (drunk + sick), summon enforcers, and drive the
        // nearest real enforcer over to restrain them. If openCase, the custom Case + new-case notification fire
        // the instant they are restrained. Resets any arrest already in progress (callers that must not interrupt
        // an existing arrest check Arrest.Active first).
        internal static bool Begin(Human subject, bool openCase)
        {
            try
            {
                if (subject == null || Dead(subject)) { Log("Begin: no valid subject."); return false; }
                Reset();
                Subject = subject; Enforcer = null; Active = true; _phase = Phase.Approach; _openCase = openCase;
                _deadline = Time.time + ApproachTimeoutSec; _nextReassert = 0f; _nextApproachLog = 0f; SubjectSearched = false;
                try { subject.AddDrunk(2f); } catch { }
                try { subject.AddSick(1.5f); } catch { }

                NewGameLocation loc = null; try { loc = subject.currentGameLocation; } catch { }
                string locName = "?"; try { if (loc != null) locName = loc.name; } catch { }
                // PIN the overdosing NPC in place the instant they collapse, so they stop drifting down the street.
                // Otherwise they keep walking their routine and end up far from where the enforcer was dispatched.
                // Capture their spot now and hold them there (Approach re-asserts it) until the officer restrains them.
                _subjectNode = null; _subjectLoc = loc; try { _subjectNode = subject.currentNode; } catch { }
                if (_subjectNode != null) { try { Herd.ToNode(subject, _subjectNode, loc, 0.5f); } catch { } }
                try { var gc = GameplayController.Instance; if (gc != null && loc != null) gc.CallEnforcers(loc, false, false, 0f); }
                catch (Exception e) { Log($"CallEnforcers threw: {e.Message}"); }
                Log($"OVERDOSE: {NarcoticsPlugin.Name(subject)} is erratic; enforcers called to {locName} + nearest one dispatched to arrest (openCase={openCase}). Teleport (F10) to watch.");
                return true;
            }
            catch (Exception e) { Log($"Begin error: {e.Message}"); return false; }
        }

        internal static void Tick()
        {
            if (!Active) return;
            try
            {
                if (Dead(Subject)) { Log("arrest subject gone."); Reset(); return; }

                // Keep the subject visibly erratic (drunk wears off).
                if (Time.time >= _nextReassert)
                {
                    _nextReassert = Time.time + 3f;
                    try { Subject.AddDrunk(0.5f); } catch { }
                    try { Subject.AddSick(0.4f); } catch { }
                }

                if (_phase == Phase.Approach)
                {
                    if (_subjectNode != null) { try { Herd.ToNode(Subject, _subjectNode, _subjectLoc, 0.5f); } catch { } }   // keep them put
                    if (Dead(Enforcer)) Enforcer = NearestEnforcer(Subject);   // (re)find a UNIFORMED responder
                    if (Time.time >= _nextApproachLog)
                    {
                        _nextApproachLog = Time.time + 8f;
                        Log($"arrest: approaching - enforcer={(Enforcer != null ? NarcoticsPlugin.Name(Enforcer) : "<none available>")}.");
                    }
                    // Self-arrest fallback: if no enforcer reaches the subject in time (they may all be busy with the
                    // city's other crimes - the freeze you saw), open the case anyway so it is always workable. The
                    // buyer stays cuffed in place; the officer guard is simply skipped (no officer to post).
                    if (Time.time > _deadline)
                    {
                        Log("arrest: no enforcer reached in time - SELF-ARREST fallback so the case still opens.");
                        try { Herd.Release(Subject); } catch { }
                        try { Subject.ai.SetRestrained(true, 2f); } catch { }
                        _phase = Phase.Detain;
                        _holdUntilHours = Herd.NowHours() + Math.Max(0.25f, NarcoticsConfig.ArrestHoldHours);
                        if (_openCase) { try { NarcCase.Open(Subject); } catch (Exception e) { Log($"case open error: {e.Message}"); } }
                        _guardNode = null; _guardNode2 = null; _guardLoc = null;
                        return;
                    }
                    if (Enforcer == null) return;                              // none available yet - wait
                    if (Herd.ToActor(Enforcer, Subject, ContactM, ResponseGoal()))   // urgent alarm-response stance, not a stroll
                    {
                        try { Herd.Release(Subject); } catch { }   // drop the hold-in-place pin; SetRestrained keeps them now
                        try { Subject.ai.SetRestrained(true, 2f); } catch { }
                        _phase = Phase.Detain;
                        _holdUntilHours = Herd.NowHours() + Math.Max(0.25f, NarcoticsConfig.ArrestHoldHours);
                        if (_openCase) { try { NarcCase.Open(Subject); } catch (Exception e) { Log($"case open error: {e.Message}"); } }
                        // Post the officer at the scene's building ENTRANCE to guard it (like a vanilla officer by the
                        // crime-scene door), NOT on top of the suspect (that blocked talking to them), and have them
                        // WALK there, not sprint. We avoid the vanilla enforcerGuardDuty goal: its locationOption is
                        // murderLocation, which with no real murder-location object falls back onto the suspect (that
                        // was the "stood on top" bug). Fallback post: where the officer is standing now (a step from
                        // the suspect) if there is no building entrance (street scenes).
                        _guardNode = null; _guardNode2 = null; _guardLoc = null;
                        try { _guardNode2 = Enforcer.currentNode; } catch { }   // scene side (~1.8m from the suspect)
                        try
                        {
                            var l = Subject.currentGameLocation; _guardLoc = l;
                            NewNode far = null;
                            try { var bld = l != null ? l.building : null; var w = bld != null ? bld.mainEntrance : null; far = w != null ? w.node : null; } catch { }
                            if (far == null) { try { far = l != null ? l.anchorNode : null; } catch { } }   // street scene: the location anchor
                            _guardNode = far;
                        }
                        catch { }
                        if (_guardNode == null) _guardNode = _guardNode2;
                        if (_guardNode2 == null) _guardNode2 = _guardNode;
                        Log($"ARREST: {NarcoticsPlugin.Name(Subject)} restrained; officer {NarcoticsPlugin.Name(Enforcer)} is patrolling the scene. Cuffed ~{NarcoticsConfig.ArrestHoldHours:0}h (or until solved) - talk to / search them.");
                    }
                }
                else // Detain: keep the subject cuffed; the officer posts up NEAR the suspect and LEANS ON A WALL
                     // (walks there, no sprinting, no ping-ponging) like an enforcer loitering at a vanilla scene.
                {
                    try { Subject.ai.SetRestrained(true, 2f); } catch { }   // re-assert or they stand up (keeps them in place)
                    if (Enforcer != null && !Dead(Enforcer))
                    {
                        var target = _guardNode2 ?? _guardNode;   // the spot by the suspect (~1.8m); entrance only as fallback
                        if (target != null) Herd.ToNode(Enforcer, target, _guardLoc, 1.0f, LeanGoal() ?? WalkGoal());
                        Calm(Enforcer);   // clear the urgent alarm-response state left from the approach so they WALK, not run
                    }
                    if (_openCase && NarcCase.IsSolved()) { Log($"case solved - releasing {NarcoticsPlugin.Name(Subject)}."); Finish(); return; }
                    if (Herd.NowHours() >= _holdUntilHours) { Log($"{NarcoticsPlugin.Name(Subject)} held the full {NarcoticsConfig.ArrestHoldHours:0}h - releasing."); Finish(); return; }
                }
            }
            catch (Exception e) { Log($"Tick error: {e.Message}"); Reset(); }
        }

        // Release the subject back to their routine (never RemoveFromWorld - possession is minor, keeps them
        // tail-able/question-able for the investigation).
        private static void Finish()
        {
            try
            {
                if (Subject != null) { try { Subject.ai.SetRestrained(false, 0f); } catch { } }
                if (Enforcer != null) { try { Herd.Release(Enforcer); } catch { } }
                Log($"RELEASED {NarcoticsPlugin.Name(Subject)} - arrest complete.");
            }
            catch (Exception e) { Log($"Finish error: {e.Message}"); }
            finally { Reset(); }
        }

        internal static void TeleportToPair()
        {
            try
            {
                if (!Active || Subject == null) { Log("no active arrest (press the overdose key first)."); return; }
                var player = Player.Instance; if (player == null) return;
                NewNode node = null; try { node = Subject.currentNode; } catch { }
                if (node == null) { Log("subject has no node yet."); return; }
                player.Teleport(node, null, true, false, true);
                string enf = Enforcer != null ? NarcoticsPlugin.Name(Enforcer) : "(awaiting enforcer)";
                Log($"teleported to the arrest: subject {NarcoticsPlugin.Name(Subject)}, enforcer {enf} (phase {_phase}).");
            }
            catch (Exception e) { Log($"TeleportToPair error: {e.Message}"); }
        }

        internal static void Stop() { if (Active) Finish(); }

        // Called by the OnSearch Harmony patch when the player frisks the current arrestee. The search no longer
        // releases them (they stay cuffed the full hold); it just confirms the dose is now revealed and nudges the
        // player to pin it - the whole point of holding them is so a forgotten pin can still be fixed.
        internal static void NotifySearched()
        {
            if (!Active || SubjectSearched) return;
            SubjectSearched = true;
            Log($"player searched {NarcoticsPlugin.Name(Subject)} - their dose is now known evidence. Pin it to the case board (they stay cuffed, so you can come back).");
        }

        private static void Reset()
        {
            try { if (Subject != null) { try { Herd.Release(Subject); } catch { } try { Subject.ai.SetRestrained(false, 0f); } catch { } } } catch { }
            try { if (Enforcer != null) Herd.Release(Enforcer); } catch { }
            Active = false; Enforcer = null; Subject = null; _phase = Phase.Approach; _openCase = false; _guardNode = null; _guardNode2 = null; _guardLoc = null; _subjectNode = null; _subjectLoc = null; SubjectSearched = false;
        }

        internal static void ResetOnLoad() { Active = false; Enforcer = null; Subject = null; _phase = Phase.Approach; _openCase = false; _guardNode = null; _guardNode2 = null; _guardLoc = null; _subjectNode = null; _subjectLoc = null; SubjectSearched = false; }

        // Nearest living citizen to a point (excluding the player, and optionally a humanID to skip).
        private static Human NearestNPC(Vector3 pos, int skipId)
        {
            try
            {
                var cd = CityData.Instance; var cits = cd != null ? cd.citizenDirectory : null;
                if (cits == null) return null;
                int playerId = -1; try { if (Player.Instance != null) playerId = Player.Instance.humanID; } catch { }
                Human best = null; float bestD = float.MaxValue;
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || Dead(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (id == playerId || id == skipId) continue;
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    float d; try { d = Vector3.Distance(pos, hn.position); } catch { continue; }
                    if (d < bestD) { bestD = d; best = h; }
                }
                return best;
            }
            catch { return null; }
        }

        // Nearest living enforcer to the subject, from the game's own enforcer list.
        private static Human NearestEnforcer(Human subject)
        {
            try
            {
                NewNode sn = null; try { sn = subject.currentNode; } catch { }
                if (sn == null) return null;
                var pos = sn.position;
                var gc = GameplayController.Instance;
                var list = gc != null ? gc.enforcers : null;
                if (list == null) return null;
                Human best = null; float bestD = float.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    Human h = null;
                    try { h = list[i]; } catch { }
                    if (h == null || Dead(h)) continue;
                    if (!IsUniformed(h)) continue;   // exclude plainclothes/off-duty enforcers (vanilla sends uniformed responders)
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    float d; try { d = Vector3.Distance(pos, hn.position); } catch { continue; }
                    if (d < bestD) { bestD = d; best = h; }
                }
                return best;
            }
            catch { return null; }
        }

        // A uniformed, on-duty enforcer (currently wearing their work outfit). Vanilla crime-scene responders are
        // uniformed; a plainclothes enforcer standing guard looked wrong (user feedback), so we exclude them.
        private static bool IsUniformed(Human h)
        {
            try
            {
                var oc = h.outfitController;
                if (oc == null) return false;
                var cat = oc.currentOutfit;
                return cat == ClothesPreset.OutfitCategory.work || cat == ClothesPreset.OutfitCategory.outdoorsWork;
            }
            catch { return false; }
        }

        // The vanilla enforcer alarm-response goal (urgent movement + response stance), so the officer hurries over
        // instead of strolling with an umbrella. Null if unavailable -> Herd falls back to the normal walk-to goal.
        private static AIGoalPreset ResponseGoal()
        {
            try { var rc = RoutineControls.Instance; if (rc != null) return rc.enforcerResponse; } catch { }
            return null;
        }

        // The vanilla WALK-to goal (toGoWalkGoal). The default toGoGoal RUNS (its move action forces running), which
        // is what made the guarding officer sprint back and forth; toGoWalkGoal is the same goal tuned to walk. Null
        // -> Herd falls back to the running goal.
        private static AIGoalPreset WalkGoal()
        {
            try { var rc = RoutineControls.Instance; if (rc != null) return rc.toGoWalkGoal; } catch { }
            return null;
        }

        // A one-time clone of toGoWalkGoal tuned to make the posted officer LEAN ON A WALL near the suspect (vanilla's
        // standAgainstWall idle) instead of standing stiffly. We clone (never mutate the shared preset, which every
        // enforcer uses) and enable pottering with standAgainstWall as the only potter action, sourced from this
        // config, located at the node we herd to. Same Instantiate+DontDestroyOnLoad pattern as the drug-item clone.
        private static AIGoalPreset _leanGoal;
        private static AIGoalPreset LeanGoal()
        {
            try
            {
                if (_leanGoal != null) return _leanGoal;
                var rc = RoutineControls.Instance; if (rc == null) return null;
                var baseGoal = rc.toGoWalkGoal; if (baseGoal == null) return null;
                AIGoalPreset clone = null;
                try { clone = UnityEngine.Object.Instantiate(baseGoal).TryCast<AIGoalPreset>(); } catch (Exception e) { Log($"LeanGoal clone threw: {e.Message}"); }
                if (clone == null) return null;
                try { clone.allowPottering = true; } catch { }
                try { clone.potterSource = AIGoalPreset.GoalActionSource.thisConfiguration; } catch { }   // use OUR potterActions list
                try { clone.locationOption = AIGoalPreset.LocationOption.passedGamelocation; } catch { }   // potter at the node we pass
                try { clone.potterFrequency = new Vector2(2f, 6f); } catch { }
                try
                {
                    var acts = new Il2CppSystem.Collections.Generic.List<AIActionPreset>();
                    if (rc.standAgainstWall != null) acts.Add(rc.standAgainstWall);
                    clone.potterActions = acts;
                }
                catch (Exception e) { Log($"LeanGoal potterActions threw: {e.Message}"); }
                try { UnityEngine.Object.DontDestroyOnLoad(clone); } catch { }
                _leanGoal = clone;
                Log("built the lean-on-wall guard goal (standAgainstWall).");
                return _leanGoal;
            }
            catch (Exception e) { Log($"LeanGoal build error: {e.Message}"); return null; }
        }

        // Clear the urgent alarm-response AI state (reaction + investigation urgency) carried over from the approach,
        // so the officer's guard movement is a calm walk, not a pursuit run. isRunning is DERIVED from speed, so we
        // set the urgency INPUTS (reactionState/investigationUrgency), never the output flag.
        private static void Calm(Human enforcer)
        {
            try
            {
                var ai = enforcer != null ? enforcer.ai : null; if (ai == null) return;
                try { ai.SetReactionState(NewAIController.ReactionState.none); } catch { }
                try { ai.SetInvestigationUrgency(NewAIController.InvestigationUrgency.walk); } catch { }
            }
            catch { }
        }

        private static bool Dead(Human h) { try { return h == null || h.isDead; } catch { return false; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][arrest] " + m);
    }
}
