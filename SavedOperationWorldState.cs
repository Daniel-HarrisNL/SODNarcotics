using System;
using System.Collections.Generic;

namespace Narcotics
{
    // Data contract only: NOT hooked up to save/load yet. Integrate against the live branch.
    // Store durable state IDs/game-clock timestamps; never Unity pointers or Time.time values.
    // Snapshot alongside SavedInvestigation and validate its identity BEFORE replay/rebind.
    internal sealed class SavedOperationWorldState
    {
        public int SchemaVersion { get; set; } = 1;
        public string DrugKey { get; set; }
        public string CaseTitle { get; set; }
        public int CookHumanId { get; set; }
        public int LabDirectoryIndex { get; set; }
        public float CapturedAtGameHours { get; set; }

        public float StartedAtGameHours { get; set; }
        public float NextCookAtGameHours { get; set; }
        public bool CookSessionActive { get; set; }
        public float CookSessionStartGameHours { get; set; }
        public int CookBatchesRemaining { get; set; }

        // Supply-chain stock references the ORIGINAL saved world interactables.
        // Never recreate these items merely because an ID doesn't resolve.
        public List<int> LabSupplyInteractableIds { get; set; } = new List<int>();
        public List<int> LabDiscoveryInteractableIds { get; set; } = new List<int>();
        public bool LabDiscoveryDosesPlaced { get; set; }
        public bool LabDressed { get; set; }
        public List<SavedDistributorWorldState> Distributors { get; set; } = new List<SavedDistributorWorldState>();
        public List<SavedDealerWorldState> Dealers { get; set; } = new List<SavedDealerWorldState>();

        public List<SavedBuyerWorldState> Buyers { get; set; } = new List<SavedBuyerWorldState>();
        public int PendingOverdoseBuyerId { get; set; } = -1;
        public float PendingOverdoseAtGameHours { get; set; }
        public float NextOverdoseEligibleAtGameHours { get; set; }
        public List<int> QueuedOverdoseBuyerIds { get; set; } = new List<int>();
        public SavedArrestWorldState Arrest { get; set; } = new SavedArrestWorldState();

        // The active movement/animation job deliberately isn't serialized verbatim.
        // After rehydrating authoritative inventory and durable timers, PickJob() may
        // re-plan from stable states; completed transfers/sales must never replay.
    }

    internal sealed class SavedDistributorWorldState
    {
        public int HumanId { get; set; }
        public bool Collected { get; set; }
    }

    internal sealed class SavedDealerWorldState
    {
        public int HumanId { get; set; }
        public bool Stocked { get; set; }
        public float NextSellAtGameHours { get; set; }
        public List<int> LooseDropInteractableIds { get; set; } = new List<int>();
        // Bin contents are ALREADY serialized by SoD as MetaObjects. Re-count the
        // actual live container page on load, not an old cached BinDoses integer.
    }

    internal sealed class SavedBuyerWorldState
    {
        public int HumanId { get; set; }
        public int DealerHumanId { get; set; } = -1;
        // Optional world-node/position identity for stable witness sighting locations.
        // Must be resolved against the loaded city before use.
        public float SaleX { get; set; }
        public float SaleY { get; set; }
        public float SaleZ { get; set; }
        public bool HasSalePosition { get; set; }
    }

    internal sealed class SavedArrestWorldState
    {
        public bool ApproachActive { get; set; }
        public int ApproachingSubjectId { get; set; } = -1;
        public int ApproachingEnforcerId { get; set; } = -1;
        public float ApproachStartedAtGameHours { get; set; }
        public bool OpenCaseOnDetain { get; set; }
        public bool SubjectSearched { get; set; }
        public List<SavedPostedArrestWorldState> Posted { get; set; } = new List<SavedPostedArrestWorldState>();
    }

    internal sealed class SavedPostedArrestWorldState
    {
        public int SubjectId { get; set; }
        public int EnforcerId { get; set; } = -1;
        public float HoldUntilGameHours { get; set; }
        public bool OpenCase { get; set; }
        public bool Searched { get; set; }
    }
}
