using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace Narcotics
{
    // Experimental second-stage persistence. Recipes for the case's crew and their fixed dead drops
    // are stored separately from DrugPersistence's global preset registry. Only immutable identity
    // is persisted: the loaded game's own case, item pages and citizen objects remain authoritative.
    internal sealed class SavedDealerSite
    {
        public int HumanId { get; set; }
        public int DistributorIndex { get; set; }
        public string FurnitureName { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    internal sealed class SavedInvestigation
    {
        public string DrugKey { get; set; }
        public string DrugName { get; set; }
        public string BasePresetName { get; set; }
        public string CaseTitle { get; set; }
        public int CookId { get; set; }
        public int LabDirectoryIndex { get; set; }
        public string LabName { get; set; }
        public float StartedAtGameHours { get; set; }
        public List<int> DistributorIds { get; set; } = new List<int>();
        public List<SavedDealerSite> Dealers { get; set; } = new List<SavedDealerSite>();
        // Optional per-save cook snapshots. Missing/null is valid for legacy records.
        public List<SavedCookSession> CookSessions { get; set; } = new List<SavedCookSession>();
    }

    internal static class InvestigationPersistence
    {
        private static readonly string PathOnDisk = Path.Combine(Paths.ConfigPath, "com.opi.narcotics.investigations.json");
        private static readonly Dictionary<string, SavedInvestigation> Saved = new Dictionary<string, SavedInvestigation>();
        private static bool _read;
        private static bool _restoredThisLoad;
        private static bool _loading;
        private static float _nextRetry;
        private static string _lastWarning;
        private static string _loadSaveIdentity;
        private static long _loadSaveLastWriteUtcTicks;
        private static float _loadedGameHours = float.NaN;

        internal static void BeginLoad(string saveIdentity, float loadedGameHours)
        {
            _loading = true;
            _restoredThisLoad = false;
            _nextRetry = 0f;
            _lastWarning = null;
            _loadSaveIdentity = saveIdentity;
            _loadedGameHours = loadedGameHours;
            _loadSaveLastWriteUtcTicks = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(_loadSaveIdentity) && File.Exists(_loadSaveIdentity))
                    _loadSaveLastWriteUtcTicks = File.GetLastWriteTimeUtc(_loadSaveIdentity).Ticks;
            }
            catch { }
        }

        internal static void AfterGameStarted()
        {
            _loading = false;
            _restoredThisLoad = false;
            _nextRetry = 0f;
        }

        private static void ReadOnce()
        {
            if (_read) return;
            _read = true;
            try
            {
                if (!File.Exists(PathOnDisk)) return;
                var records = JsonSerializer.Deserialize<List<SavedInvestigation>>(File.ReadAllText(PathOnDisk));
                if (records == null) return;
                foreach (var r in records)
                {
                    if (r == null || string.IsNullOrEmpty(r.DrugKey) ||
                        !r.DrugKey.StartsWith("NarcDrug_", StringComparison.Ordinal) ||
                        string.IsNullOrEmpty(r.CaseTitle)) continue;
                    Saved[r.DrugKey] = r;
                }
                Log($"loaded {Saved.Count} investigation identity record(s).");
            }
            catch (Exception e) { Warn("reading investigation identities failed: " + e.Message); }
        }

        // Called only once the CASE has actually opened: saving the initial operation when it starts
        // could leave a dangling entry for an investigation that was never created or saved.
        internal static void RecordOpenedCase()
        {
            try
            {
                if (!Operation.Active || !NarcCase.IsOpen) return;
                var record = Operation.CaptureIdentity();
                if (record == null || string.IsNullOrEmpty(record.DrugKey))
                {
                    Warn("unable to capture full crew/lab/dead-drop identity; the case cannot be resumed by this prototype.");
                    return;
                }
                ReadOnce();
                if (Saved.TryGetValue(record.DrugKey, out var previous) && previous != null && previous.CookSessions != null)
                    record.CookSessions = previous.CookSessions;
                Saved[record.DrugKey] = record;
                WriteSavedRecords();
                Log($"recorded case '{record.CaseTitle}' (key={record.DrugKey}, crew={record.DistributorIds.Count}+{record.Dealers.Count}).");
            }
            catch (Exception e) { Warn("recording investigation identity failed: " + e); }
        }

        private static void WriteSavedRecords()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk));
            var json = JsonSerializer.Serialize(new List<SavedInvestigation>(Saved.Values),
                new JsonSerializerOptions { WriteIndented = true });
            string temporary = PathOnDisk + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, PathOnDisk, true);
        }

        internal static void CommitCookSession(SavedCookSession snapshot)
        {
            if (snapshot == null) return;
            ReadOnce();
            if (!Saved.TryGetValue(snapshot.DrugKey ?? string.Empty, out var record) || record == null ||
                record.CaseTitle != snapshot.CaseTitle || record.CookId != snapshot.CookHumanId ||
                record.LabDirectoryIndex != snapshot.LabDirectoryIndex)
            {
                Warn("[cook-save] skipped snapshot commit: active operation identity no longer matches its investigation record.");
                return;
            }
            if (record.CookSessions == null) record.CookSessions = new List<SavedCookSession>();
            for (int i = record.CookSessions.Count - 1; i >= 0; i--)
                if (SamePath(record.CookSessions[i]?.SaveIdentity, snapshot.SaveIdentity)) record.CookSessions.RemoveAt(i);
            record.CookSessions.Add(snapshot);
            WriteSavedRecords();
            float remaining = snapshot.CookActive
                ? (snapshot.CookSessionStartGameHours >= 0f ? snapshot.CookSessionStartGameHours + snapshot.CookShiftHours - snapshot.CapturedAtGameHours : -1f)
                : snapshot.NextCookAtGameHours - snapshot.CapturedAtGameHours;
            Log($"[cook-save] committed active={snapshot.CookActive} started={snapshot.CookSessionStartGameHours:0.###} " +
                $"batchesLeft={snapshot.CookBatchesLeft} savedGT={snapshot.CapturedAtGameHours:0.###} remaining={remaining:0.###} " +
                $"save={System.IO.Path.GetFileName(snapshot.SaveIdentity)}.");
        }

        internal static SavedCookSession FindCookSession(SavedInvestigation saved, out string reason, out SavedCookSession diagnostic)
        {
            reason = null;
            diagnostic = null;
            ReadOnce();
            if (saved == null) { reason = "investigation identity missing"; return null; }
            // During an in-game (hot) load, MainMenuController.selectedSave may be null even
            // though the native LoadSaveState(StateSaveData) hook has the correct game time.
            // Never guess a slot by case title or "most recent": accept only ONE unchanged
            // save file whose recorded snapshot also matches the loaded game's identity/time.
            // Cold loads keep the stricter, existing selected-path validation below.
            if (string.IsNullOrWhiteSpace(_loadSaveIdentity))
                return FindUniqueHotLoadSnapshot(saved, out reason, out diagnostic);
            if (saved.CookSessions == null || saved.CookSessions.Count == 0) { reason = "legacy save has no cook snapshot"; return null; }
            if (_loadSaveLastWriteUtcTicks == 0) { reason = "selected save file timestamp unavailable"; return null; }
            const float HoursTolerance = 0.05f;
            for (int i = saved.CookSessions.Count - 1; i >= 0; i--)
            {
                var candidate = saved.CookSessions[i];
                if (candidate == null || !SamePath(candidate.SaveIdentity, _loadSaveIdentity)) continue;
                diagnostic = candidate;
                if (candidate.SaveFileLastWriteUtcTicks != _loadSaveLastWriteUtcTicks)
                { reason = "selected save file timestamp differs from captured save"; continue; }
                if (candidate.SchemaVersion != 1) { reason = "unsupported cook snapshot version"; return null; }
                if (candidate.DrugKey != saved.DrugKey || candidate.CaseTitle != saved.CaseTitle ||
                    candidate.CookHumanId != saved.CookId || candidate.LabDirectoryIndex != saved.LabDirectoryIndex)
                { reason = "operation identity mismatch"; return null; }
                if (!Finite(candidate.CapturedAtGameHours) || !Finite(_loadedGameHours) ||
                    Math.Abs(candidate.CapturedAtGameHours - _loadedGameHours) > HoursTolerance)
                { reason = $"save game-time mismatch (snapshot={candidate.CapturedAtGameHours:0.###}, loaded={_loadedGameHours:0.###})"; return null; }
                return candidate;
            }
            reason = "no snapshot matches selected save path and file timestamp";
            return null;
        }

        // Fail-closed recovery for a game-initiated hot load that doesn't expose selectedSave.
        // It does NOT repair/recreate items, change a snapshot, or select the newest save.
        // Each candidate must belong to this exact operation, still exist at its original
        // full path with its recorded last-write tick, and match the loaded game's time.
        // If more than one candidate is possible, restore none rather than mix save slots.
        private static SavedCookSession FindUniqueHotLoadSnapshot(
            SavedInvestigation saved, out string reason, out SavedCookSession diagnostic)
        {
            diagnostic = null;
            reason = "selected save identity unavailable; no uniquely matching saved snapshot";
            if (saved.CookSessions == null || saved.CookSessions.Count == 0 || !Finite(_loadedGameHours))
                return null;

            const float HoursTolerance = 0.05f;
            SavedCookSession match = null;
            for (int i = 0; i < saved.CookSessions.Count; i++)
            {
                var candidate = saved.CookSessions[i];
                if (candidate == null || candidate.SchemaVersion != 1 ||
                    candidate.DrugKey != saved.DrugKey || candidate.CaseTitle != saved.CaseTitle ||
                    candidate.CookHumanId != saved.CookId || candidate.LabDirectoryIndex != saved.LabDirectoryIndex ||
                    !Finite(candidate.CapturedAtGameHours) ||
                    Math.Abs(candidate.CapturedAtGameHours - _loadedGameHours) > HoursTolerance ||
                    candidate.SaveFileLastWriteUtcTicks <= 0 || string.IsNullOrWhiteSpace(candidate.SaveIdentity))
                    continue;

                try
                {
                    // Only previously recorded absolute paths are eligible. Never resolve a
                    // relative filename against the working directory during hot loading.
                    if (!System.IO.Path.IsPathRooted(candidate.SaveIdentity) || !File.Exists(candidate.SaveIdentity) ||
                        File.GetLastWriteTimeUtc(candidate.SaveIdentity).Ticks != candidate.SaveFileLastWriteUtcTicks)
                        continue;
                }
                catch { continue; }

                if (match != null)
                {
                    reason = "selected save identity unavailable; multiple snapshots match game time and file timestamps";
                    return null;
                }
                match = candidate;
            }

            if (match == null) return null;
            diagnostic = match;
            reason = null;
            Log($"[cook-resume] hot-load selected path unavailable; uniquely matched {System.IO.Path.GetFileName(match.SaveIdentity)} " +
                $"by operation identity, game time and unchanged save-file timestamp.");
            return match;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Recognize OUR case by its five custom resolution questions, not merely CaseType.custom:
        // other mods can use that case type too. We only bind unsolved cases.
        private static Case FindSavedCase()
        {
            try
            {
                var cpc = CasePanelController.Instance;
                var cases = cpc != null ? cpc.activeCases : null;
                if (cases == null) return null;
                for (int i = 0; i < cases.Count; i++)
                {
                    var c = cases[i]; if (c == null) continue;
                    try
                    {
                        if (c.caseType != Case.CaseType.custom || c.isSolved ||
                            c.caseStatus == Case.CaseStatus.closable || c.caseStatus == Case.CaseStatus.archived) continue;
                        bool producer = false, transporter = false, location = false;
                        var qs = c.resolveQuestions;
                        if (qs == null) continue;
                        for (int j = 0; j < qs.Count; j++)
                        {
                            var q = qs[j]; if (q == null) continue;
                            if (q.name == NarcCase.QProducing) producer = true;
                            if (q.name == NarcCase.QTransporting) transporter = true;
                            if (q.name == NarcCase.QLabLocation) location = true;
                        }
                        if (producer && transporter && location) return c;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string CorrectAnswer(Case c, string label)
        {
            try
            {
                var qs = c.resolveQuestions;
                if (qs == null) return null;
                for (int i = 0; i < qs.Count; i++)
                {
                    var q = qs[i];
                    if (q != null && q.name == label && q.correctAnswers != null && q.correctAnswers.Count > 0)
                        return q.correctAnswers[0];
                }
            }
            catch { }
            return null;
        }

        // Do not select a stale snapshot from a different city or a previous case with the same
        // procedural title. Compare the game's persisted cook/distributor/lab answers before binding.
        private static SavedInvestigation Match(Case c)
        {
            ReadOnce();
            string cook = CorrectAnswer(c, NarcCase.QProducing);
            string distributor = CorrectAnswer(c, NarcCase.QTransporting);
            string lab = CorrectAnswer(c, NarcCase.QLabLocation);
            if (string.IsNullOrEmpty(cook) || string.IsNullOrEmpty(distributor) || string.IsNullOrEmpty(lab)) return null;
            foreach (var s in Saved.Values)
            {
                try
                {
                    if (s.CaseTitle != c.name || cook != s.CookId.ToString() ||
                        lab != s.LabDirectoryIndex.ToString() || s.DistributorIds == null ||
                        s.DistributorIds.Count == 0 || distributor != s.DistributorIds[0].ToString()) continue;
                    // Verify the lab's name as well as directory index (indexes repeat between cities).
                    var cd = CityData.Instance;
                    var dir = cd != null ? cd.gameLocationDirectory : null;
                    if (dir == null || s.LabDirectoryIndex < 0 || s.LabDirectoryIndex >= dir.Count) continue;
                    var loc = dir[s.LabDirectoryIndex];
                    if (loc == null || loc.name != s.LabName) continue;
                    return s;
                }
                catch { }
            }
            return null;
        }

        // Called from the game tick BEFORE TickAutoStart. If a saved case exists, NEVER start a
        // fresh drug operation over it, even when its sidecar identity is missing or unusable.
        // This protects player progress rather than quietly replacing Mirage with Drift.
        internal static bool HasUnresolvedCase()
        {
            return FindSavedCase() != null;
        }

        internal static bool Loading => _loading;

        internal static void Tick()
        {
            CookSessionPersistence.PollCompletedSaves();
            if (_loading || Operation.Active || _restoredThisLoad) return;
            try
            {
                if (UnityEngine.Time.time < _nextRetry) return;
                _nextRetry = UnityEngine.Time.time + 3f;
                var c = FindSavedCase();
                if (c == null) return;
                NarcCase.BindLoadedCase(c);
                var snapshot = Match(c);
                if (snapshot == null)
                {
                    WarnOnce("saved Narcotics case found but no matching operation record; blocking new operations to avoid overwriting this investigation.");
                    return;
                }
                if (!Operation.RestoreIdentity(snapshot))
                {
                    WarnOnce("operation reconstruction is incomplete; not starting a replacement operation.");
                    return;
                }
                _restoredThisLoad = true;
                Log($"RESUMED '{snapshot.CaseTitle}' (key={snapshot.DrugKey}); existing game case preserved; no duplicate operation started.");
            }
            catch (Exception e) { WarnOnce("restoration attempt failed: " + e.Message); }
        }

        private static void WarnOnce(string message)
        {
            if (_lastWarning == message) return;
            _lastWarning = message;
            Warn(message);
        }

        private static void Log(string msg) => NarcoticsPlugin.Log.LogInfo("[Narcotics][investigation-save] " + msg);
        private static void Warn(string msg) => NarcoticsPlugin.Log.LogWarning("[Narcotics][investigation-save] " + msg);
    }
}
