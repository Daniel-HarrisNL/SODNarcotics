using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppSystem.Threading.Tasks;

namespace Narcotics
{
    // Optional versioned runtime data. All persisted values are game time / stable identity;
    // the temporary Herd goal and Unity/IL2CPP pointers are rebuilt after loading.
    internal sealed class SavedCookSession
    {
        public int SchemaVersion { get; set; } = 1;
        public string SaveIdentity { get; set; }
        public long SaveFileLastWriteUtcTicks { get; set; }
        public string DrugKey { get; set; }
        public string CaseTitle { get; set; }
        public int CookHumanId { get; set; }
        public int LabDirectoryIndex { get; set; }
        public float CapturedAtGameHours { get; set; }
        public float OperationStartedAtGameHours { get; set; }
        public bool CookActive { get; set; }
        public float CookSessionStartGameHours { get; set; }
        public float CookShiftHours { get; set; }
        public int CookBatchesLeft { get; set; }
        public int CookBatchLimit { get; set; }
        public float NextCookAtGameHours { get; set; }
        public float CookIntervalHours { get; set; }
        // Optional, backwards-compatible supply tracking; absent on older cook snapshots.
        // IDs identify objects that the game itself saved, never replacement doses.
        public bool HasWorldTracking { get; set; }
        public List<int> BuyerHumanIds { get; set; } = new List<int>();
        public List<int> LabStashInteractableIds { get; set; } = new List<int>();
    }

    internal sealed class PendingCookSave
    {
        internal SavedCookSession Snapshot;
        internal string SaveIdentity;
        internal Task Task;
    }

    internal static class CookSessionPersistence
    {
        private static readonly List<PendingCookSave> Pending = new List<PendingCookSave>();

        internal static string GetSelectedSaveIdentity()
        {
            try
            {
                var menu = MainMenuController.Instance;
                var selected = menu != null ? menu.selectedSave : null;
                var info = selected != null ? selected.info : null;
                return NormalizePath(info != null ? info.FullName : null);
            }
            catch (Exception e)
            {
                NarcoticsPlugin.Log.LogWarning($"[Narcotics][cook-resume] could not read selected save path: {e.Message}");
                return null;
            }
        }

        // Capture BEFORE native serialization begins, not after the async method returns:
        // otherwise time and remaining batches can diverge from the game's saved state.
        internal static PendingCookSave PrepareSaveIdentity(string path)
        {
            try
            {
                string identity = NormalizeCaptureIdentity(path);
                if (string.IsNullOrEmpty(identity))
                {
                    LogSaveSkip("active=unknown", Herd.NowHours(), "native save path is empty");
                    return null;
                }
                if (!Operation.Active || !NarcCase.IsOpen)
                {
                    LogSaveSkip("active=false", Herd.NowHours(), "no active narcotics case");
                    return null;
                }
                SavedCookSession snapshot = Operation.CaptureCookSession();
                if (snapshot == null)
                {
                    LogSaveSkip("active=true", Herd.NowHours(), "operation identity or cook state unavailable");
                    return null;
                }
                snapshot.SaveIdentity = identity;
                float remaining = snapshot.CookActive
                    ? (snapshot.CookSessionStartGameHours >= 0f ? snapshot.CookSessionStartGameHours + snapshot.CookShiftHours - snapshot.CapturedAtGameHours : -1f)
                    : snapshot.NextCookAtGameHours - snapshot.CapturedAtGameHours;
                string reason = snapshot.CookActive && snapshot.CookSessionStartGameHours < 0f
                    ? "reason=traveling-to-lab" : string.Empty;
                Log($"[cook-save] captured active={snapshot.CookActive} started={snapshot.CookSessionStartGameHours:0.###} " +
                    $"batchesLeft={snapshot.CookBatchesLeft} savedGT={snapshot.CapturedAtGameHours:0.###} remaining={remaining:0.###} {reason} path={Path.GetFileName(identity)}.");
                return new PendingCookSave { SaveIdentity = identity, Snapshot = snapshot };
            }
            catch (Exception e)
            {
                Log($"[cook-save] skipped reason=capture failed: {e.Message}");
                return null;
            }
        }

        // The postfix associates the already-captured snapshot with the native save task.
        // The snapshot is committed only after that task reports success.
        internal static void CaptureAndAttachSaveTask(PendingCookSave pending, Task task)
        {
            if (pending == null || pending.Snapshot == null) return;
            if (task == null)
            {
                Log($"[cook-save] skipped reason=native save returned no task, path={Path.GetFileName(pending.SaveIdentity)}.");
                return;
            }
            pending.Task = task;
            Pending.Add(pending);
        }

        // Polled from the existing game Update hook. This only commits a snapshot once, after
        // the game's asynchronous save task has completed successfully; it never writes per tick.
        internal static void PollCompletedSaves()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                var pending = Pending[i];
                if (pending == null || pending.Task == null)
                {
                    Pending.RemoveAt(i);
                    continue;
                }
                bool completed;
                try { completed = pending.Task.IsCompleted; }
                catch { completed = true; }
                if (!completed) continue;

                Pending.RemoveAt(i);
                try
                {
                    if (!pending.Task.IsCompletedSuccessfully)
                    {
                        Log($"[cook-save] skipped active={pending.Snapshot.CookActive} started={pending.Snapshot.CookSessionStartGameHours:0.###} " +
                            $"batchesLeft={pending.Snapshot.CookBatchesLeft} savedGT={pending.Snapshot.CapturedAtGameHours:0.###} remaining=unknown reason=native save task did not succeed.");
                        continue;
                    }
                    if (!File.Exists(pending.SaveIdentity))
                    {
                        Log($"[cook-save] skipped active={pending.Snapshot.CookActive} started={pending.Snapshot.CookSessionStartGameHours:0.###} " +
                            $"batchesLeft={pending.Snapshot.CookBatchesLeft} savedGT={pending.Snapshot.CapturedAtGameHours:0.###} remaining=unknown reason=saved file not found at native path.");
                        continue;
                    }
                    pending.Snapshot.SaveFileLastWriteUtcTicks = File.GetLastWriteTimeUtc(pending.SaveIdentity).Ticks;
                    InvestigationPersistence.CommitCookSession(pending.Snapshot);
                }
                catch (Exception e)
                {
                    Log($"[cook-save] skipped active={pending.Snapshot.CookActive} started={pending.Snapshot.CookSessionStartGameHours:0.###} " +
                        $"batchesLeft={pending.Snapshot.CookBatchesLeft} savedGT={pending.Snapshot.CapturedAtGameHours:0.###} remaining=unknown reason=commit failed: {e.Message}");
                }
            }
        }

        private static string NormalizeCaptureIdentity(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized)) return null;
            // Some game builds pass a relative filename to the native serializer. If the selected
            // slot has the same filename, use its authoritative full path for save/load matching.
            string selected = GetSelectedSaveIdentity();
            if (!string.IsNullOrEmpty(selected) &&
                string.Equals(Path.GetFileName(normalized), Path.GetFileName(selected), StringComparison.OrdinalIgnoreCase))
                return selected;
            return normalized;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return null; }
        }

        private static void LogSaveSkip(string active, float gameHours, string reason) =>
            Log($"[cook-save] skipped {active} started=-1 batchesLeft=0 savedGT={gameHours:0.###} remaining=unknown reason={reason}.");

        private static void Log(string message) => NarcoticsPlugin.Log.LogInfo("[Narcotics]" + message);
    }

    // Capture at the beginning of the native save. The returned task's successful completion
    // is the boundary for committing this optional sidecar snapshot.
    [HarmonyPatch(typeof(SaveStateController), nameof(SaveStateController.CaptureSaveStateAsync), new Type[] { typeof(string), typeof(bool) })]
    internal static class Patch_CaptureCookStateForGameSave
    {
        [HarmonyPrefix]
        private static void Prefix(string __0, out PendingCookSave __state)
        {
            __state = CookSessionPersistence.PrepareSaveIdentity(__0);
        }

        [HarmonyPostfix]
        private static void Postfix(Task __result, PendingCookSave __state)
        {
            CookSessionPersistence.CaptureAndAttachSaveTask(__state, __result);
        }
    }
}
