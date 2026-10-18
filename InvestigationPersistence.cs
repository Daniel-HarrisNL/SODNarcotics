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

        internal static void BeginLoad()
        {
            _loading = true;
            _restoredThisLoad = false;
            _nextRetry = 0f;
            _lastWarning = null;
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
                Saved[record.DrugKey] = record;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk));
                var json = JsonSerializer.Serialize(new List<SavedInvestigation>(Saved.Values),
                    new JsonSerializerOptions { WriteIndented = true });
                string temporary = PathOnDisk + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, PathOnDisk, true);
                Log($"recorded case '{record.CaseTitle}' (key={record.DrugKey}, crew={record.DistributorIds.Count}+{record.Dealers.Count}).");
            }
            catch (Exception e) { Warn("recording investigation identity failed: " + e); }
        }

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
