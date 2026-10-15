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
        private static NewNode _subjectNode;   // where the subject collapsed - we pin them here so they don't drift off
        private static NewGameLocation _subjectLoc;
        private static float _approachStartGT; // in-game hours (SessionData.gameTime) when the approach began
        private static float _nextReassert;    // real-time seconds; keep the subject visibly erratic
        internal static bool SubjectSearched;  // set by the OnSearch patch when the player frisks the active arrestee

        // POSTED arrests: once a subject is cuffed, it is handed off from the single approach-machine into this list,
        // which keeps each one restrained (officer leaning nearby) until the hold ends or the case is solved. This frees
        // the approach-machine (Active=false) immediately, so the NEXT overdose can fire instead of the whole queue
        // blocking for the ~12h a subject stays cuffed. Multiple cuffed buyers can coexist for the player to interrogate.
        private sealed class Posted { public Human Subject; public Human Enforcer; public NewGameLocation GuardLoc; public float NextWander; public float HoldUntil; public bool OpenCase; public bool Searched; }
        private static readonly List<Posted> _posted = new List<Posted>();
        private static readonly System.Random _rng = new System.Random();

        private const float ContactM = 1.8f;
        // Backstop measured in IN-GAME time (not real seconds) so fast-forward doesn't stretch it: if the officer hasn't
        // reached the subject by now, teleport them adjacent (or self-arrest if no officer at all) so the case opens.
        private const float ArrestApproachMinutes = 5f;
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
                _approachStartGT = Herd.NowHours(); _nextReassert = 0f; _nextApproachLog = 0f; SubjectSearched = false;
                try { subject.AddDrunk(2f); } catch { }
                try { subject.AddSick(1.5f); } catch { }

                NewGameLocation loc = null; try { loc = subject.currentGameLocation; } catch { }
                string locName = "?"; try { if (loc != null) locName = loc.name; } catch { }
                // PIN the overdosing NPC in place the instant they collapse, so they stop drifting down the street.
                // Otherwise they keep walking their routine and end up far from where the enforcer was dispatched.
                // Capture their spot now and hold them there (Approach re-asserts it) until the officer restrains them.
                _subjectNode = null; _subjectLoc = loc; try { _subjectNode = subject.currentNode; } catch { }
                if (_subjectNode != null) { try { Herd.ToNode(subject, _subjectNode, loc, 0.5f); } catch { } }
                // NOTE: we deliberately do NOT call GameplayController.CallEnforcers anymore. It dispatched the whole
                // enforcer response to the location, which made them try to set up a CRIME SCENE out of the venue (e.g.
                // a tavern) - buggy + unwanted. We drive the ONE nearest uniformed officer ourselves (the Approach
                // machine) to restrain the subject and then potter around them; no crime scene, no venue cordon.
                Log($"OVERDOSE: {NarcoticsPlugin.Name(subject)} is erratic at {locName}; driving the nearest officer to arrest (openCase={openCase}). Teleport (F10) to watch.");
                return true;
            }
            catch (Exception e) { Log($"Begin error: {e.Message}"); return false; }
        }

        internal static void Tick()
        {
            try { TickPosted(); } catch (Exception e) { Log($"TickPosted error: {e.Message}"); }   // keep every cuffed subject held, independent of the approach-machine
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
                    // Drive the officer to the subject's STABLE pinned node - NOT Herd.ToActor on the live node, which
                    // (because the drunk subject stumbles between adjacent nodes) recreated the officer's goal every
                    // tick, so the pathfinder never settled and he never closed the last 1.8m (the "2 in-game days" bug).
                    bool reached = Enforcer != null && _subjectNode != null
                                   && Herd.ToNode(Enforcer, _subjectNode, _subjectLoc, ContactM, ResponseGoal());

                    // Backstop in IN-GAME time (fast-forward-proof): if the officer hasn't reached in time, teleport them
                    // adjacent to the subject (reliable, restrain this tick); if no officer exists at all, self-arrest
                    // the subject in place so the case still opens.
                    bool timedOut = Herd.NowHours() - _approachStartGT >= ArrestApproachMinutes / 60f;
                    if (!reached && timedOut)
                    {
                        if (Enforcer != null)
                        {
                            try { Enforcer.Teleport(AdjacentWalkable(_subjectNode) ?? _subjectNode, null, true, false, true); } catch (Exception e) { Log($"enforcer teleport threw: {e.Message}"); }
                            Log($"arrest: {NarcoticsPlugin.Name(Enforcer)} couldn't path in time - teleported to the scene.");
                            reached = true;
                        }
                        else
                        {
                            Log("arrest: no uniformed officer available - SELF-ARREST fallback so the case still opens.");
                            EnterDetain(true);
                            return;
                        }
                    }
                    if (!reached) return;   // still walking over
                    EnterDetain(false);     // cuff + hand off to the posted list, freeing the machine for the next overdose
                }
            }
            catch (Exception e) { Log($"Tick error: {e.Message}"); Reset(); }
        }

        // Keep every handed-off (cuffed) subject restrained, with its officer posted/leaning nearby, until the case is
        // solved or the hold elapses. Runs every tick regardless of the approach-machine, so arrests accumulate.
        private static void TickPosted()
        {
            if (_posted.Count == 0) return;
            bool solved = false; try { solved = NarcCase.IsSolved(); } catch { }
            for (int i = _posted.Count - 1; i >= 0; i--)
            {
                var p = _posted[i];
                if (p == null || Dead(p.Subject)) { if (p != null) ReleasePosted(p); _posted.RemoveAt(i); continue; }
                try { p.Subject.ai.SetRestrained(true, 2f); } catch { }   // re-assert or they stand up
                if (p.Enforcer != null && !Dead(p.Enforcer))
                {
                    // Hand the officer to its OWN vanilla patrol coroutine around the arrest location (NewAIController.Patrol)
                    // rather than hand-driving it node-to-node and forcing arm/weapon state each tick (which made it walk
                    // with arms stuck out and the gun drawn). Patrol is the cop's normal mill-around-the-area routine:
                    // natural gait, holstered weapon, no door-blocking. Re-assert it every ~20s (and drop any leftover Herd
                    // pin from the approach so the goal can run) in case the officer's shift or another goal interrupts.
                    if (Time.time >= p.NextWander)
                    {
                        try { Herd.Release(p.Enforcer); } catch { }
                        try { if (p.GuardLoc != null) p.Enforcer.ai.Patrol(p.GuardLoc); } catch { }
                        p.NextWander = Time.time + 20f;
                    }
                }
                if ((p.OpenCase && solved) || Herd.NowHours() >= p.HoldUntil)
                {
                    Log($"{NarcoticsPlugin.Name(p.Subject)} released (arrest complete; {_posted.Count - 1} scene(s) still active).");
                    ReleasePosted(p); _posted.RemoveAt(i);
                }
            }
        }

        private static void ReleasePosted(Posted p)
        {
            try { if (p != null && p.Subject != null) { try { Herd.Release(p.Subject); } catch { } try { p.Subject.ai.SetRestrained(false, 0f); } catch { } } } catch { }
            try { if (p != null && p.Enforcer != null) Herd.Release(p.Enforcer); } catch { }
            // Clear this arrestee's "Search <name>" objective + its HUD pointer when they're let go, so a released NPC
            // doesn't leave a dangling POI stuck at the old arrest spot. (On a case RESOLUTION this runs via Arrest.Stop,
            // which fires BEFORE NarcCase.MarkClosed, so the case is still open and ClearLeadFor can find + cancel it.)
            try { if (p != null && p.Subject != null) NarcCase.ClearLeadFor(p.Subject.humanID, "released from arrest"); } catch { }
        }

        private static void ReleaseAllPosted() { try { for (int i = 0; i < _posted.Count; i++) if (_posted[i] != null) ReleasePosted(_posted[i]); } catch { } _posted.Clear(); }

        // Transition Approach -> Detain: cuff the subject in place, open the case, and (unless this was a no-officer
        // self-arrest) cache the guard posts the officer loiters at. Factored out so both the "officer reached /
        // teleported in" path and the "no officer at all" fallback restrain + open identically.
        private static void EnterDetain(bool selfArrest)
        {
            try { Herd.Release(Subject); } catch { }   // drop the hold-in-place pin; SetRestrained keeps them now
            try { Subject.ai.SetRestrained(true, 2f); } catch { }
            if (_openCase) { try { NarcCase.OnSubjectArrested(Subject); } catch (Exception e) { Log($"case open error: {e.Message}"); } }
            try { Operation.OnArrestMade(); } catch { }   // start the "next overdose no sooner than X after THIS arrest" clock

            var p = new Posted
            {
                Subject = Subject,
                Enforcer = selfArrest ? null : Enforcer,
                OpenCase = _openCase,
                HoldUntil = Herd.NowHours() + Math.Max(0.25f, NarcoticsConfig.ArrestHoldHours),
                Searched = false,
            };
            if (p.Enforcer != null)
            {
                // Hand the officer to its OWN vanilla patrol routine around the arrest location (so it mills about the
                // scene with a natural gait + holstered weapon, not standing on the suspect or blocking a door). Release
                // the approach's Herd pin first so the vanilla goal can actually run; TickPosted re-asserts it.
                try { p.GuardLoc = Subject.currentGameLocation; } catch { }
                try { Herd.Release(Enforcer); } catch { }
                try { if (p.GuardLoc != null) Enforcer.ai.Patrol(p.GuardLoc); } catch { }
                Log($"ARREST: {NarcoticsPlugin.Name(Subject)} restrained; officer {NarcoticsPlugin.Name(Enforcer)} set to patrol the scene. Cuffed ~{NarcoticsConfig.ArrestHoldHours:0}h (or until solved). {_posted.Count + 1} scene(s) active.");
            }
            else { Log($"ARREST: {NarcoticsPlugin.Name(Subject)} restrained in place (no officer posted). {_posted.Count + 1} scene(s) active."); }

            _posted.Add(p);
            ClearActive();   // free the approach-machine for the next overdose; the Posted record now owns this subject
        }

        // Null the approach-machine state WITHOUT releasing the subject (a Posted record owns it now). Distinct from
        // Reset(), which releases the subject (used when an in-progress APPROACH is aborted).
        private static void ClearActive()
        {
            Active = false; Enforcer = null; Subject = null; _phase = Phase.Approach; _openCase = false;
            _subjectNode = null; _subjectLoc = null; SubjectSearched = false;
        }

        // Is `id` a subject currently being arrested (approaching) or cuffed (posted)? Used to never sell to an arrestee.
        internal static bool IsArrestee(int id)
        {
            try
            {
                if (Active && Subject != null && Subject.humanID == id) return true;
                for (int i = 0; i < _posted.Count; i++) { var p = _posted[i]; if (p != null && p.Subject != null && p.Subject.humanID == id) return true; }
            }
            catch { }
            return false;
        }

        // Is `id` an officer already tied up at a scene (approaching or posted)? Used so a second arrest picks a different officer.
        private static bool IsPostedEnforcer(int id)
        {
            try
            {
                if (Enforcer != null && Enforcer.humanID == id) return true;
                for (int i = 0; i < _posted.Count; i++) { var p = _posted[i]; if (p != null && p.Enforcer != null && p.Enforcer.humanID == id) return true; }
            }
            catch { }
            return false;
        }

        // A walkable node adjacent to `n`, so a teleported officer lands a step away (not on top of the suspect).
        // Falls back to `n` itself if there is no good neighbour - harmless, the guard post re-places them next tick.
        private static NewNode AdjacentWalkable(NewNode n)
        {
            try
            {
                if (n == null) return n;
                var acc = n.accessToOtherNodes;
                if (acc != null)
                {
                    foreach (var kv in acc)
                    {
                        var nb = kv.Key; var a = kv.Value;
                        if (nb == null || a == null) continue;
                        bool walk = false; try { walk = a.walkingAccess; } catch { }
                        if (!walk) continue;
                        bool bad = false; try { bad = nb.isObstacle || nb.isInaccessable || nb.noAccess || !nb.isConnected; } catch { }
                        if (!bad) return nb;
                    }
                }
            }
            catch { }
            return null;   // NEVER fall back to `n` itself - that put the officer on top of the suspect; no neighbour => leave unposted
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
                if (!Diagnostics.TeleportOnto(Subject)) { Log("subject has no node yet."); return; }   // land right on the subject
                string enf = Enforcer != null ? NarcoticsPlugin.Name(Enforcer) : "(awaiting enforcer)";
                Log($"teleported to the arrest: subject {NarcoticsPlugin.Name(Subject)}, enforcer {enf} (phase {_phase}).");
            }
            catch (Exception e) { Log($"TeleportToPair error: {e.Message}"); }
        }

        internal static void Stop() { try { ReleaseAllPosted(); } catch { } if (Active) Finish(); }

        // Called by the OnSearch Harmony patch when the player frisks an arrestee (approaching OR posted). Matches by
        // humanID so searching one of several cuffed buyers marks the right one. The search doesn't release them (they
        // stay cuffed the full hold); it just confirms the dose is now revealed and nudges the player to pin it.
        internal static void NotifySearched(Human h)
        {
            try
            {
                if (h == null) return;
                int id; try { id = h.humanID; } catch { return; }
                if (Active && Subject != null && Subject.humanID == id)
                {
                    if (!SubjectSearched) { SubjectSearched = true; Log($"player searched {NarcoticsPlugin.Name(h)} - their dose is now known evidence."); }
                    return;
                }
                for (int i = 0; i < _posted.Count; i++)
                {
                    var p = _posted[i];
                    if (p != null && p.Subject != null && p.Subject.humanID == id)
                    {
                        if (!p.Searched) { p.Searched = true; Log($"player searched {NarcoticsPlugin.Name(h)} - their dose is now known evidence. Pin it to the case board (they stay cuffed)."); }
                        return;
                    }
                }
            }
            catch { }
        }

        private static void Reset()
        {
            try { if (Subject != null) { try { Herd.Release(Subject); } catch { } try { Subject.ai.SetRestrained(false, 0f); } catch { } } } catch { }
            try { if (Enforcer != null) Herd.Release(Enforcer); } catch { }
            Active = false; Enforcer = null; Subject = null; _phase = Phase.Approach; _openCase = false; _subjectNode = null; _subjectLoc = null; SubjectSearched = false;
        }

        internal static void ResetOnLoad() { Active = false; Enforcer = null; Subject = null; _phase = Phase.Approach; _openCase = false; _subjectNode = null; _subjectLoc = null; SubjectSearched = false; _posted.Clear(); }

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
                    int id; try { id = h.humanID; } catch { continue; }
                    if (IsPostedEnforcer(id)) continue;   // don't reuse an officer already tied up at another scene
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

        private static bool Dead(Human h) { try { return h == null || h.isDead; } catch { return false; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][arrest] " + m);
    }
}
