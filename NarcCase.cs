using System;
using UnityEngine;

namespace Narcotics
{
    // The single persistent custom Case the player works. Opened automatically the moment a buyer is restrained
    // after overdosing in public (Arrest.cs). ONE case at a time, like a vanilla murder: a later arrest while a
    // case is still open feeds that case (the player can search the new arrestee too) rather than spawning a
    // duplicate. It is created silently (the direct `new Case(...)` ctor, NOT CreateNewCase, which pops a confirm
    // dialog), selected on the board, then announced exactly like a murder: the big centre banner + the
    // police-scanner blip, so "the player gets the new case notification".
    //
    // Objectives (e.g. an initial "INVESTIGATE THIS NPC") and evidence/fact links (dose prints -> dealer, lab
    // stash -> cook) are Phase 5 and deliberately NOT built here yet - the user wants to test the vanilla
    // resolution/hand-in form against a bare case first.
    internal static class NarcCase
    {
        internal static Case Current;
        internal static int SubjectId = -1;   // humanID of the most recent arrested buyer tied to this case
        internal static string LeadText;   // the exact "Search <name> at <loc>" lead text, so we can find + clear it on search

        internal static bool IsOpen
        {
            get { try { return Current != null && Current.Pointer != IntPtr.Zero; } catch { return false; } }
        }

        // The player has submitted / closed / archived the case via the vanilla resolution loop. Used to release
        // the cuffed buyer early. Returns false when there is simply no case (so the arrest's hold-timer governs).
        internal static bool IsSolved()
        {
            try
            {
                if (!IsOpen) return false;
                try { if (Current.isSolved) return true; } catch { }
                try
                {
                    var st = Current.caseStatus;
                    if (st == Case.CaseStatus.submitted || st == Case.CaseStatus.closable || st == Case.CaseStatus.archived)
                        return true;
                }
                catch { }
                try
                {
                    var cpc = CasePanelController.Instance;
                    if (cpc != null && cpc.activeCases != null && !cpc.activeCases.Contains(Current)) return true;   // off the board
                }
                catch { }
            }
            catch { }
            return false;
        }

        // Open the persistent case for an arrested buyer. Idempotent: if a case is already open, just records the
        // latest subject and returns false (no second case, no second banner). Returns true if a NEW case opened.
        internal static bool Open(Human subject)
        {
            try
            {
                int sid = -1; try { if (subject != null) sid = subject.humanID; } catch { }

                if (IsOpen)
                {
                    if (sid != -1) SubjectId = sid;
                    Log($"arrest of {NarcoticsPlugin.Name(subject)} folds into the open case '{SafeName(Current)}' (one case at a time; no new notification).");
                    return false;
                }

                var cpc = CasePanelController.Instance;
                if (cpc == null) { Log("CasePanelController.Instance null - cannot open a case yet (load a game)."); return false; }

                // CaseType.custom (NOT murder). CaseType.murder is a HARD no: MurderController.OnVictimDiscovery
                // enforces exactly ONE active murder case at a time (it FindIndex-es activeCases for any caseType==
                // murder case whose status is not submitted/closable/archived and, if one exists, SKIPS creating the
                // real murder's case AND its banner, which are both built inside CasePanelController.CreateNewCase).
                // Our persistent case would occupy that slot for its whole ~12h life and silently eat every real
                // murder in that window. Custom avoids the gate entirely (and has no murder-resolve path to NPE). We
                // drive objective text (Patch_ObjectiveSetup), the resolve-question rendering (Patch_InputFieldSetup),
                // and the hand-in ourselves.
                Case c = new Case("New Drug on the Street", Case.CaseType.custom, Case.CaseStatus.handInNotCollected);
                bool present = false; try { present = cpc.activeCases != null && cpc.activeCases.Contains(c); } catch { }
                if (!present) { try { cpc.activeCases.Add(c); } catch (Exception e) { Log($"activeCases.Add failed: {e.Message}"); } }
                try { cpc.SetActiveCase(c); } catch (Exception e) { Log($"SetActiveCase failed: {e.Message}"); }
                try { cpc.UpdateCaseButtonsActive(); } catch { }
                try { cpc.UpdateCaseControls(); } catch { }

                Current = c; SubjectId = sid;

                // CaseType.custom never auto-resolves objective text and SetStatus never adds the hand-in machinery
                // for it, so we drive the whole case ourselves:
                //   1) the resolution-form questions (the engine only generates these for murder cases),
                //   2) the "Search the suspect" lead (Patch_ObjectiveSetup copies our entryRef onto the name),
                //   3) the hand-in destination: Case.handIn must hold City Hall's address id or GetClosestHandIn
                //      returns null and BOTH "Plot Route to Hand-in" and the submit objective silently fail. We fill
                //      it, add the "Collect resolution form from City Hall" lead, and ValidationCheck() to make the
                //      submit objective evaluate now. Patch_GetCaseForm + Patch_HandInCase admit our custom case to the
                //      tray's collect (GetCaseForm) + submit (HandInCase) actions, which vanilla gates to murder/
                //      mainStory/retirement only.
                AddResolveQuestions(c, subject);
                AddInvestigateObjective(c, subject);
                PopulateHandIn(c);
                AddCollectObjective(c);
                try { c.ValidationCheck(); } catch (Exception e) { Log($"ValidationCheck threw: {e.Message}"); }
                try
                {
                    var hi = c.GetClosestHandIn();
                    string hn = "?"; if (hi != null) { try { hn = hi.GetName(); } catch { } }
                    Log($"GetClosestHandIn after populate: {(hi != null ? "resolved a tray ('" + hn + "')" : "NULL - no tray resolved from the handIn ids (Plot Route + submit objective will fail)")}");
                }
                catch (Exception e) { Log($"GetClosestHandIn threw: {e.Message}"); }

                // Notify exactly like a murder arrives: the police-scanner line FIRST so it lands WITH the big banner.
                Scanner("[Scanner] Reported Possession");
                try { Diagnostics.Announce("NEW NARCOTICS CASE"); } catch (Exception e) { Log($"banner error: {e.Message}"); }

                int count = -1; try { count = cpc.activeCases != null ? cpc.activeCases.Count : -1; } catch { }
                Log($"OPENED custom case '{SafeName(c)}' for {NarcoticsPlugin.Name(subject)} (activeCases={count}). Open the case board - it is selected, no popup.");
                return true;
            }
            catch (Exception e) { Log($"Open error: {e}"); return false; }
        }

        // The bottom-left police-scanner feed blip (the per-event notification; distinct from the big banner).
        internal static void Scanner(string message, InterfaceControls.Icon icon = InterfaceControls.Icon.handcuffs)
        {
            try
            {
                var ic = InterfaceController.Instance;
                if (ic == null) return;
                ic.NewGameMessage(InterfaceController.GameMessageType.notification, 0, message, icon);
            }
            catch (Exception e) { Log($"Scanner error: {e.Message}"); }
        }

        // Called by Patch_GetCaseForm the moment the player collects the form at the tray, BEFORE SetStatus spawns the
        // objective banners. Fires the vanilla-style "Collected Form: <case>" scanner blip, then the typewriter
        // day/time/location game-header - the same sequence (scanner -> header -> objective banners) vanilla plays
        // for murder/mainStory/retirement collects but skips for our custom case.
        internal static void OnFormCollected()
        {
            // Vanilla sequence: the "Collected Form" scanner blip + the case-title typewriter header fire together,
            // then a second typewriter header with the time/date/location.
            try { Scanner("Collected Form: " + SafeName(Current), InterfaceControls.Icon.resolve); } catch (Exception e) { Log($"OnFormCollected scanner error: {e.Message}"); }
            Header(SafeName(Current));          // header 1: the case title (with the scanner)
            Header(TimeLocationString());       // header 2: time, date, location
        }

        // Called by Patch_HandInCase when the player submits the completed form. Fires the vanilla "Case submitted
        // for processing" scanner blip (ui.gamemessage key "case submitted") with the resolve icon.
        internal static void OnFormSubmitted()
        {
            string msg = ""; try { msg = Strings.Get("ui.gamemessage", "case submitted"); } catch { }
            if (string.IsNullOrEmpty(msg)) msg = "Case submitted for processing. Please wait for results.";
            try { Scanner(msg, InterfaceControls.Icon.resolve); } catch (Exception e) { Log($"OnFormSubmitted error: {e.Message}"); }
        }

        // Fire one typewriter game-header (InterfaceController GameMessageType.gameHeader - the big centre header that
        // types itself out, distinct from the scanner blip and the case banner). They queue, so two calls play in order.
        private static void Header(string text)
        {
            try { var ic = InterfaceController.Instance; if (ic == null || string.IsNullOrEmpty(text)) return; ic.NewGameMessage(InterfaceController.GameMessageType.gameHeader, 0, text); }
            catch (Exception e) { Log($"Header error: {e.Message}"); }
        }

        // "<time>, <day>, <date>, <location>" for the collect header, matching vanilla (e.g. "4:12AM, Tue, Jan 3rd,
        // City Hall Ground floor lobby"): 12-hour clock + short day + short month + ordinal date + no year + location.
        // The game's own header system hides the date on later same-day headers, so we always build the full form.
        private static string TimeLocationString()
        {
            try
            {
                var sd = SessionData.Instance; if (sd == null) return "";
                float gt = sd.gameTime;
                string time = ""; try { time = sd.GameTimeToClock12String(gt, false); } catch { }
                string date = ""; try { date = sd.LongDateString(gt, true, true, true, true, true, false, false, true); } catch { }   // "Tue, Jan 3rd"
                string loc = null; try { var p = Player.Instance; if (p != null && p.currentGameLocation != null) loc = p.currentGameLocation.name; } catch { }
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(time)) parts.Add(time);
                if (!string.IsNullOrEmpty(date)) parts.Add(date);
                if (!string.IsNullOrEmpty(loc)) parts.Add(loc);
                return string.Join(", ", parts);
            }
            catch { return ""; }
        }

        // Stop tracking the case (e.g. once the player has solved/closed it) without touching the game's Case
        // object, which stays on the board for them.
        internal static void MarkClosed() { Current = null; SubjectId = -1; }

        // Clear the "Explore Reported Crime Scene" lead the moment the player engages the arrested suspect - talking
        // to them or searching them is the whole point of that lead, so it is done on contact. Uses Objective.Cancel()
        // (which only removes the HUD pointer + awareness icon; no murder-state side effects), NOT Complete() whose
        // murder branch could deref the backing murder our synthetic case lacks. Idempotent + pointer-safe.
        internal static void ClearLead(string via)
        {
            try
            {
                if (!IsOpen) return;
                var list = Current.currentActiveObjectives;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    Objective o = null; try { o = list[i]; } catch { }
                    if (o == null) continue;
                    try { if (o.isComplete || o.isCancelled) continue; } catch { }
                    if (!IsLeadObjective(o)) continue;
                    try { o.Cancel(); } catch (Exception e) { Log($"search-lead Cancel threw: {e.Message}"); }
                    try { CasePanelController.Instance.UpdateCaseControls(); } catch { }
                    Log($"cleared the 'search the suspect' lead ({via}).");
                    return;   // Cancel mutates the list; stop iterating
                }
            }
            catch (Exception e) { Log($"ClearLead error: {e.Message}"); }
        }

        // Is this the "Search <name> at <loc>" lead (and not the City Hall collect objective)? Match on the exact lead
        // text we stored, else on the "Search" wording - the collect objective says "Collect a case resolution form
        // from City Hall", so it never matches.
        private static bool IsLeadObjective(Objective o)
        {
            try
            {
                string nm = null; try { nm = o.name; } catch { }
                if (!string.IsNullOrEmpty(LeadText))
                {
                    if (nm == LeadText) return true;
                    try { var qe = o.queueElement; if (qe != null && qe.entryRef == LeadText) return true; } catch { }
                }
                if (!string.IsNullOrEmpty(nm) && nm.IndexOf("Search ", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }
            return false;
        }

        // The starting objectives, each composed with its FULL display text passed as the entryRef (custom cases do
        // not resolve objective text in the engine - Patch_ObjectiveSetup copies our entryRef onto the name once the
        // Objective is built asynchronously; Patch_ObjectiveSetup copies our entryRef onto the name once the Objective
        // is built): a "Search <name> at <location>" lead that POINTS at the arrested suspect but does NOT auto-complete
        // on arrival (trigger type `nothing`) - only the player actually frisking the suspect clears it (ClearLead,
        // called from the OnSearch patch). The "Collect a case resolution form from City Hall" hand-in objective is
        // added separately by AddCollectObjective (custom cases get no hand-in machinery from SetStatus).
        private static void AddInvestigateObjective(Case c, Human subject)
        {
            try
            {
                if (c == null) return;
                NewNode node = null; try { node = subject != null ? subject.currentNode : null; } catch { }
                NewGameLocation loc = null; try { loc = subject != null ? subject.currentGameLocation : null; } catch { }
                Vector3 pos = default; try { if (node != null) pos = node.position; } catch { }
                string where = null; try { if (loc != null) where = loc.name; } catch { }
                string name = null; try { if (subject != null) name = subject.citizenName; } catch { }
                if (string.IsNullOrEmpty(name)) name = "the suspect";

                string leadTxt = string.IsNullOrEmpty(where) ? $"Search {name}" : $"Search {name} at {where}";
                LeadText = leadTxt;   // remember the exact text so searching the suspect can clear this lead
                // Trigger type `nothing` = never auto-completes (goToNode would clear it the moment the player arrives);
                // the pointer still routes the player to the suspect via usePointer + pointerPosition.
                var leadTrig = new Objective.ObjectiveTrigger(Objective.ObjectiveTriggerType.nothing, "", false, 0f, null, null, null, node, null, loc, null, "", false, pos);
                try { c.AddObjective(leadTxt, leadTrig, true, pos, InterfaceControls.Icon.lookingGlass, Objective.OnCompleteAction.nothing, 0f, false, "", false, false, null, false, false, false); }
                catch (Exception e) { Log($"search objective failed: {e.Message}"); }

                Log($"added search objective: '{leadTxt}'.");
            }
            catch (Exception e) { Log($"AddInvestigateObjective error: {e.Message}"); }
        }

        // Fill Case.handIn with the case-tray INTERACTABLE ids. handIn is a List<int> that Case.GetClosestHandIn
        // resolves via CityData.savableInteractableDictionary (Dictionary<int,Interactable>) - NOT addressDictionary,
        // so the entries must be Interactable.ids, not NewAddress.ids (that was the bug: all 17 address ids missed and
        // GetClosestHandIn returned null, which killed Plot Route AND the submit objective). CityData.caseTrays is the
        // canonical list of hand-in trays (vanilla's own reception points); GetClosestHandIn returns the nearest to
        // the player, so we add them all and the City Hall tray wins when the player is there.
        private static void PopulateHandIn(Case c)
        {
            try
            {
                if (c == null) return;
                if (c.handIn == null) c.handIn = new Il2CppSystem.Collections.Generic.List<int>();
                try { c.handIn.Clear(); } catch { }
                int added = 0;
                var cd = CityData.Instance; var trays = cd != null ? cd.caseTrays : null;
                if (trays != null)
                {
                    for (int i = 0; i < trays.Count; i++)
                    {
                        var t = trays[i]; if (t == null) continue;
                        int id; try { id = t.id; } catch { continue; }
                        try { c.handIn.Add(id); added++; } catch { }
                    }
                }
                Log($"populated hand-in with {added} case-tray interactable id(s) (total {c.handIn.Count}).");
            }
            catch (Exception e) { Log($"PopulateHandIn error: {e.Message}"); }
        }

        // The "Collect a case resolution form from City Hall" lead (collectHandIn trigger auto-completes it when the
        // player collects the form at the tray). Text via the vanilla missions.postings key so it reads exactly like
        // a real case; Patch_ObjectiveSetup copies our resolved entryRef onto the objective name for the custom case.
        private static void AddCollectObjective(Case c)
        {
            try
            {
                if (c == null) return;
                string txt = ""; try { txt = Strings.Get("missions.postings", "collect hand in"); } catch { }
                if (string.IsNullOrEmpty(txt)) txt = "Collect a case resolution form from City Hall";
                // Anchor a HUD pointer at the City Hall tray (the nearest hand-in tray) so the collect objective shows
                // its resolve icon floating over the form in-world, like the search lead does over the suspect.
                Vector3 pos = default; bool usePtr = false;
                try { var tray = c.GetClosestHandIn(); if (tray != null) { pos = tray.GetWorldPosition(true); usePtr = true; } } catch { }
                var trig = new Objective.ObjectiveTrigger(Objective.ObjectiveTriggerType.collectHandIn, "", false, 0f, null, null, null, null, null, null, null, "", false, pos);
                try { c.AddObjective(txt, trig, usePtr, pos, InterfaceControls.Icon.hand, Objective.OnCompleteAction.nothing, 0f, false, "", false, false, null, false, false, false); }
                catch (Exception e) { Log($"collect objective failed: {e.Message}"); }
                Log($"added collect objective: '{txt}' (pointer={usePtr}).");
            }
            catch (Exception e) { Log($"AddCollectObjective error: {e.Message}"); }
        }

        // Populate the case's resolution-form questions (the engine only generates these for murder/mainStory cases).
        // IMPORTANT: Case.ResolveQuestion.GetText looks `name` up as a missions.postings LOCALIZATION KEY (it is NOT
        // literal display text), so a free-text name renders BLANK in the form row - Patch_InputFieldSetup forces the
        // row label to our literal `name` instead. `displayObjective=false` stops the engine auto-spawning a (blank)
        // objective from this question when the form is collected. `useAlternateName`/`useName` do NOT affect GetText,
        // so we don't bother with them. The correct answer gets wired to the real dealer once rendering is confirmed.
        private static void AddResolveQuestions(Case c, Human subject)
        {
            try
            {
                if (c == null) return;
                if (c.resolveQuestions == null) c.resolveQuestions = new Il2CppSystem.Collections.Generic.List<Case.ResolveQuestion>();
                // PRIMARY (required): identify the COOK - the lab operator who produces the drug, the real target of
                // the bust. SECONDARY (optional bonus): the street DEALER the interrogation trail points to. The
                // correct answer is the citizen's humanID as a string (ResolveQuestion.UpdateCorrect feeds each
                // correctAnswers entry to CityData.GetHuman). Cook from the active operation; dealer from the trail.
                int cookId = -1; try { if (Operation.CookHuman != null) cookId = Operation.CookHuman.humanID; } catch { }
                int dealerId = -1; try { int bid = subject != null ? subject.humanID : -1; var dl = Operation.DealerFor(bid); if (dl != null) dealerId = dl.humanID; } catch { }
                AddQuestion(c, "Who is producing the drug?", required: true,  reward: new Vector2(600f, 900f), penalty: new Vector2(150f, 300f), correctHumanId: cookId);
                AddQuestion(c, "Who is dealing the drug?",   required: false, reward: new Vector2(200f, 400f), penalty: new Vector2(50f, 150f),  correctHumanId: dealerId);
                Log($"resolve answers: cook#{cookId}, dealer#{dealerId}.");
                Log($"added {c.resolveQuestions.Count} resolve question(s).");
            }
            catch (Exception e) { Log($"AddResolveQuestions error: {e.Message}"); }
        }

        // Add one citizen-identify resolve question. `name` is forced onto the form row by Patch_InputFieldSetup
        // (GetText treats it as a localization key = blank otherwise); displayObjective=false stops the engine
        // auto-spawning a blank objective from it on collect.
        private static void AddQuestion(Case c, string label, bool required, Vector2 reward, Vector2 penalty, int correctHumanId = -1)
        {
            try
            {
                var q = new Case.ResolveQuestion();
                try { q.name = label; } catch { }
                // The correct answer: the citizen's humanID as a string (UpdateCorrect feeds it to CityData.GetHuman).
                if (correctHumanId >= 0)
                {
                    try { q.correctAnswers = new Il2CppSystem.Collections.Generic.List<string>(); q.correctAnswers.Add(correctHumanId.ToString()); } catch { }
                }
                // displayObjective=true mimics vanilla: on collect, Case.SetStatus spawns one objective per question
                // (each with a banner + a submitCase trigger). Those submit-objectives are what make the City Hall
                // tray offer the HAND-IN action, so without them the form can't be submitted. Their text comes out
                // right because Patch_ObjectiveSetup copies our entryRef (the question name) onto the objective name.
                try { q.displayObjective = true; } catch { }
                try { q.inputType = Case.InputType.citizen; } catch { }
                try { q.icon = InterfaceControls.Icon.citizen; } catch { }
                try { q.isOptional = !required; } catch { }
                try { q.rewardRange = reward; } catch { }
                try { q.penaltyRange = penalty; } catch { }
                c.resolveQuestions.Add(q);
            }
            catch (Exception e) { Log($"AddQuestion error: {e.Message}"); }
        }

        // Debug: step the hand-in flow for the open custom case, to TEST the resolution form independently of the
        // City Hall tray (which did nothing in testing). 1st press collects the form (status -> handInCollected,
        // which should make ResolveController render the fillable questions); 2nd press submits (Resolve + submitted).
        internal static void DebugHandInStep()
        {
            try
            {
                if (!IsOpen) { Log("DEBUG hand-in: no open case."); return; }
                var st = Current.caseStatus;
                if (st == Case.CaseStatus.handInNotCollected)
                {
                    try { Current.SetStatus(Case.CaseStatus.handInCollected, false); } catch (Exception e) { Log($"SetStatus(handInCollected) threw: {e.Message}"); }
                    try { var cpc = CasePanelController.Instance; if (cpc != null) { cpc.SetActiveCase(Current); cpc.UpdateCaseControls(); } } catch { }
                    Log("DEBUG hand-in: COLLECTED (status -> handInCollected). Open the case board + Resolve pane - are the questions fillable? Press again to submit.");
                }
                else if (st == Case.CaseStatus.handInCollected)
                {
                    try { Current.Resolve(); } catch (Exception e) { Log($"Resolve threw: {e.Message}"); }
                    try { Current.SetStatus(Case.CaseStatus.submitted, true); } catch (Exception e) { Log($"SetStatus(submitted) threw: {e.Message}"); }
                    Log("DEBUG hand-in: SUBMITTED (Resolve + status -> submitted). Did it solve/score cleanly?");
                }
                else Log($"DEBUG hand-in: case status is {st}; nothing to step.");
            }
            catch (Exception e) { Log($"DebugHandInStep error: {e.Message}"); }
        }

        // True when OUR case is the one currently selected on the case panel. The resolve-form repair patches use this
        // so they only touch OUR render (a real murder's resolve form is left entirely to the game).
        internal static bool IsActiveOnPanel()
        {
            try
            {
                if (!IsOpen) return false;
                var cpc = CasePanelController.Instance;
                var ac = cpc != null ? cpc.activeCase : null;
                return ac != null && ac.Pointer == Current.Pointer;
            }
            catch { return false; }
        }

        internal static void ResetOnLoad() { Current = null; SubjectId = -1; }

        private static string SafeName(Case c) { try { return c.name; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][case] " + m);
    }
}
