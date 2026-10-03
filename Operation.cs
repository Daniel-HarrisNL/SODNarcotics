using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // The single active drug operation, modelled as a once-per-day pipeline the player can tail hop by hop:
    //
    //   COOK  --(daily)-->  goes to the lab, cooks a batch, stashes it INSIDE the lab, leaves
    //   DISTRIBUTOR  --(daily, after the cook)-->  collects the lab stash, then runs to a DEAD DROP near each
    //                                              dealer and leaves a couple of doses
    //   DEALERS  -->  collect from their nearby dead drop
    //
    // The cook is NOT pinned at the lab: they only commute to/from the lab to cook (the tell), then resume their
    // normal routine. Doses are physical, searchable Interactables (Dose.cs); the lab stash uses the game's
    // PlaceObject pipeline (persistent), dead drops reuse the carried item (SetAsNotInventory, so fingerprints
    // travel with it), and pickups are scripted (herd the collector to the spot, then SetInInventory).
    //
    // Exactly ONE actor is driven at a time (a single "job"), so movement is sane and tailable.
    internal static class Operation
    {
        internal sealed class Dist { public Human H; public bool Collected; }
        internal sealed class Dealer { public Human H; public int DistIndex; public FurnitureLocation Container; public NewNode Drop; public NewGameLocation DropLoc; public readonly List<Interactable> Stash = new List<Interactable>(); public int BinDoses; public bool Stocked; public float NextSell; }

        internal static bool Active;
        internal static Human CookHuman;
        internal static NewAddress Lab;
        internal static readonly List<Dist> Distributors = new List<Dist>();
        internal static readonly List<Dealer> Dealers = new List<Dealer>();
        internal static readonly List<Human> Buyers = new List<Human>();      // passers-by who bought a dose (for the consume/arrest phase)
        internal static readonly List<Interactable> LabStash = new List<Interactable>();

        private enum Job { None, CookSession, DistCollect, DistDrop, DealerCollect, SellToBuyer }
        private static Job _job;
        private static Human _mover;
        private static Dist _jobDist;
        private static Dealer _jobDealer;
        private static Human _jobBuyer;         // the passer-by walking over to buy (SellToBuyer)
        private static NewNode _meetNode;       // the public spot the dealer waits at during a sale
        private static float _dwellUntil;       // real-time seconds; the visible pause while the deal happens
        private static bool _cookProduced;      // produced this cook session yet?
        private static float _deadline;         // real-time seconds; abort a stuck job
        private static float _nextCookTime;     // game hours; when the cook next cooks
        private static float _nextSellScan;     // real-time seconds; throttle the passer-by scan
        private static bool _forceSale;         // F4: make a dealer sell now (bypass the interval + scan throttle)
        private static NewNode _targetNode;     // where the active mover is headed (for the distance readout)
        private static NewNode _labNode;        // cached stable node inside the lab (see LabNode)

        // Daily overdose: once per in-game day, a random buyer overdoses a few minutes after their purchase, which
        // triggers the enforcer arrest + opens the player's case (the payoff end of the chain).
        private static int _odBuyerId = -1;      // humanID of the buyer scheduled to overdose; -1 = none pending
        private static float _odAtHours;         // game hours at which the scheduled overdose fires
        private static int _odScheduledDay = -1; // day index an overdose has already been scheduled for (one/day)

        // Each buyer's dealer, so we can keep the buyer's memory of that dealer alive. SoD evicts old sightings
        // (Human.sightingMemoryLimit) as the buyer sees new people, so a one-time record is forgotten over time -
        // we re-assert it periodically so interrogation ("seen anything unusual") keeps surfacing the dealer.
        private static readonly Dictionary<int, Human> _buyerDealer = new Dictionary<int, Human>();

        // The dealer who sold to a given buyer (humanID). Used to wire the resolution form's "who is dealing" answer.
        internal static Human DealerFor(int buyerId)
        {
            try { if (_buyerDealer.TryGetValue(buyerId, out var h)) return h; } catch { }
            return null;
        }
        private static float _nextSightingReinforce;   // real-time seconds; throttle the re-assert

        private static readonly System.Random _rng = new System.Random();

        private const float RangeM = 2.0f;
        private const float JobTimeoutSec = 200f;

        // ------------------------------------------------------------------ lifecycle

        internal static bool Start()
        {
            try
            {
                if (Active) { Log("an operation is already active; stop it first."); return false; }
                Human cook = PickCook();
                if (cook == null) { Log("could not find a suitable cook."); return false; }
                NewAddress lab = LabSiting.PickLab(cook, out _);
                if (lab == null) { Log($"could not site a lab for {NarcoticsPlugin.Name(cook)} (no vacant unit?)."); return false; }
                if (DrugItem.Current == null) DrugItem.CreateDrug("NarcMysteryDrugA", "Mystery Drug A");

                CookHuman = cook; Lab = lab; Active = true;
                ResetJob();
                _nextCookTime = Herd.NowHours();   // cook heads to the lab right away on the first day
                LabStash.Clear();

                var used = new HashSet<int>(); try { used.Add(cook.humanID); } catch { }
                Distributors.Clear();
                foreach (var h in SelectCitizens(1, used)) Distributors.Add(new Dist { H = h });   // always exactly one
                Dealers.Clear();
                int deal = Math.Max(1, NarcoticsConfig.DealerCount);
                var containers = Containers.SiteContainersNearLab(lab, deal);
                var picks = Containers.PickNearestHomeless(lab, deal, used);
                if (picks.Count == 0) picks = SelectHomeless(deal, used);   // fallback if proximity pick found none
                for (int k = 0; k < picks.Count; k++)
                {
                    var dl = new Dealer { H = picks[k], DistIndex = 0 };
                    if (k < containers.Count && containers[k] != null)
                    {
                        dl.Container = containers[k];
                        dl.Drop = Containers.ContainerNode(containers[k]);
                        try { dl.DropLoc = dl.Drop != null ? dl.Drop.gameLocation : null; } catch { }
                    }
                    Dealers.Add(dl);
                }

                int withContainers = 0; for (int k = 0; k < Dealers.Count; k++) if (Dealers[k].Container != null) withContainers++;
                Log($"STARTED: cook={NarcoticsPlugin.Name(cook)} job='{JobName(cook)}' lab={Cook.LName(lab)}. Chain: cook -> 1 distributor -> {Dealers.Count} dealer(s) ({withContainers} with a real container drop). Cook cooks {NarcoticsConfig.CookStashCap}/session, {NarcoticsConfig.CarryTarget} per dealer.");
                return true;
            }
            catch (Exception e) { Log($"Start error: {e}"); return false; }
        }

        internal static void Stop()
        {
            try
            {
                try { Arrest.Stop(); } catch { }   // release any cuffed suspect tied to this operation
                if (!Active) { Log("no active operation."); return; }
                if (_mover != null) Herd.Release(_mover);
                Herd.ReleaseAll();
                Dose.DestroyAll();
                DestroyWorldDoses();
                Log($"STOPPED (cook {NarcoticsPlugin.Name(CookHuman)} + crew released, doses cleared).");
            }
            catch (Exception e) { Log($"Stop error: {e.Message}"); }
            finally { ClearState(); }
        }

        internal static void ResetOnLoad() { ClearState(); }

        private static void ClearState()
        {
            Active = false; CookHuman = null; Lab = null; _labNode = null;
            Distributors.Clear(); Dealers.Clear(); Buyers.Clear(); LabStash.Clear();
            _forceSale = false; _nextSellScan = 0f;
            _odBuyerId = -1; _odAtHours = 0f; _odScheduledDay = -1;
            _buyerDealer.Clear(); _nextSightingReinforce = 0f;
            ResetJob();
            try { Herd.ReleaseAll(); } catch { }
        }

        private static void ResetJob() { _job = Job.None; _mover = null; _jobDist = null; _jobDealer = null; _jobBuyer = null; _meetNode = null; _dwellUntil = 0f; _cookProduced = false; }

        // ------------------------------------------------------------------ tick

        internal static void Tick()
        {
            if (!Active || CookHuman == null || Lab == null) return;
            try
            {
                TickOverdose();                        // fire a scheduled buyer overdose -> arrest -> case
                TickReinforceSightings();              // keep buyers' dealer sightings fresh so interrogation works
                if (TickCaseResolution()) return;      // player solved the case -> stand down + wipe this tick
                if (_job == Job.None) PickJob();
                if (_job != Job.None) RunJob();
            }
            catch (Exception e) { Log($"Tick error: {e.Message}"); }
        }

        private static void PickJob()
        {
            int carry = Math.Max(1, NarcoticsConfig.CarryTarget);

            // 1) the cook's daily cook + lab stash.
            if (Herd.NowHours() >= _nextCookTime) { Begin(Job.CookSession, CookHuman); return; }

            // 2) distributors collect the lab stash (once per cook cycle).
            if (LabStashCount() > 0)
                for (int i = 0; i < Distributors.Count; i++)
                {
                    var d = Distributors[i];
                    if (d.H != null && Alive(d.H) && !d.Collected) { BeginDist(Job.DistCollect, d); return; }
                }

            // 3) distributors run doses out to each dealer's dead drop.
            for (int i = 0; i < Distributors.Count; i++)
            {
                var d = Distributors[i];
                if (d.H == null || !Alive(d.H) || Dose.CountIn(d.H) <= 0) continue;
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.DistIndex != i || dl.H == null || !Alive(dl.H) || dl.Stocked) continue;
                    BeginDrop(d, dl); return;
                }
            }

            // 4) dealers automatically collect from their dead drop when they're low and the drop has doses.
            for (int k = 0; k < Dealers.Count; k++)
            {
                var dl = Dealers[k];
                if (dl.H == null || !Alive(dl.H)) continue;
                if (DealerStashCount(dl) > 0 && Dose.CountIn(dl.H) < carry) { BeginDealer(dl); return; }
            }

            // 5) dealers sell a dose to a random passer-by (the deal happens in public, with a visible pause).
            if (_forceSale || Time.time >= _nextSellScan)
            {
                _nextSellScan = Time.time + 2f;   // throttle the citizen scan
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.H == null || !Alive(dl.H) || Dose.CountIn(dl.H) <= 0) continue;
                    if (!_forceSale && Herd.NowHours() < dl.NextSell) continue;
                    var buyer = PickPasserBy(dl.H);
                    if (buyer == null) continue;
                    BeginSell(dl, buyer);
                    _forceSale = false;
                    return;
                }
                _forceSale = false;
            }
        }

        private static void RunJob()
        {
            try
            {
                if (Time.time > _deadline) { Log($"job {_job} timed out; will retry."); EndJob(); return; }
                int carry = Math.Max(1, NarcoticsConfig.CarryTarget);

                switch (_job)
                {
                    case Job.CookSession:
                    {
                        var node = LabNode(CookHuman);
                        if (node == null) { EndJob(); break; }
                        _targetNode = node;
                        if (!Herd.ToNode(CookHuman, node, Lab, RangeM)) break;

                        if (!_cookProduced)
                        {
                            int make = Math.Max(0, NarcoticsConfig.CookStashCap - Dose.CountIn(CookHuman));
                            int made = Dose.CreateMany(CookHuman, make);
                            _cookProduced = true;
                            Log($"cook reached the lab and cooked {made} dose(s) (holding {Dose.CountIn(CookHuman)}).");
                        }
                        if (Dose.CountIn(CookHuman) > 0)
                        {
                            var h = Dose.Place(Lab, node, CookHuman);   // into the lab's furniture (persistent stash)
                            if (h != null) { LabStash.Add(h); Dose.Destroy(CookHuman); }
                            // if Place failed (lab not streamed), keep the doses on the cook and retry next tick
                        }
                        else
                        {
                            _nextCookTime = Herd.NowHours() + Math.Max(1f, NarcoticsConfig.CookIntervalHours);
                            for (int i = 0; i < Distributors.Count; i++) Distributors[i].Collected = false;
                            for (int k = 0; k < Dealers.Count; k++) Dealers[k].Stocked = false;
                            Log($"cook stashed {LabStashCount()} dose(s) in the lab and is leaving (next cook in {Math.Max(1f, NarcoticsConfig.CookIntervalHours):0}h).");
                            EndJob();
                        }
                        break;
                    }

                    case Job.DistCollect:
                    {
                        if (_jobDist == null || _jobDist.H == null) { EndJob(); break; }
                        var node = LabNode(_jobDist.H);
                        if (node == null) { EndJob(); break; }
                        _targetNode = node;
                        if (!Herd.ToNode(_jobDist.H, node, Lab, RangeM)) break;
                        PruneLabStash();
                        if (LabStash.Count == 0) { _jobDist.Collected = true; Log($"{NarcoticsPlugin.Name(_jobDist.H)} collected the lab stash (holding {Dose.CountIn(_jobDist.H)}); heading out on the drops."); EndJob(); break; }
                        var it = LabStash[LabStash.Count - 1];
                        LabStash.RemoveAt(LabStash.Count - 1);
                        Dose.Pickup(it, _jobDist.H);
                        break;
                    }

                    case Job.DistDrop:
                    {
                        if (_jobDist == null || _jobDist.H == null || _jobDealer == null || _jobDealer.H == null) { EndJob(); break; }
                        EnsureDrop(_jobDealer);
                        if (_jobDealer.Drop == null) { EndJob(); break; }
                        _targetNode = _jobDealer.Drop;
                        if (!Herd.ToNode(_jobDist.H, _jobDealer.Drop, _jobDealer.DropLoc, RangeM)) break;
                        int placed = 0;
                        while (placed < carry && Dose.CountIn(_jobDist.H) > 0)
                        {
                            if (_jobDealer.Container != null)
                            {
                                // Put the dose into the bin as searchable contained trash (data-side, persistent).
                                if (!Dose.DepositToBin(_jobDealer.Container, _jobDist.H)) break;
                                Dose.Destroy(_jobDist.H);            // the carried dose "moved" into the bin
                                _jobDealer.BinDoses++;
                            }
                            else
                            {
                                var h = Dose.Drop(_jobDist.H, _jobDealer.Drop);   // fallback: loose at the node
                                if (h == null) break;
                                _jobDealer.Stash.Add(h);
                            }
                            placed++;
                        }
                        _jobDealer.Stocked = true;
                        Log($"DEAD DROP: {NarcoticsPlugin.Name(_jobDist.H)} left x{placed} in {NarcoticsPlugin.Name(_jobDealer.H)}'s {(_jobDealer.Container != null ? "bin" : "spot")} (drop holds {DealerStashCount(_jobDealer)}).");
                        EndJob();
                        break;
                    }

                    case Job.DealerCollect:
                    {
                        if (_jobDealer == null || _jobDealer.H == null) { EndJob(); break; }
                        EnsureDrop(_jobDealer);
                        if (_jobDealer.Drop == null) { EndJob(); break; }
                        _targetNode = _jobDealer.Drop;
                        if (!Herd.ToNode(_jobDealer.H, _jobDealer.Drop, _jobDealer.DropLoc, RangeM)) break;
                        int got;
                        if (_jobDealer.Container != null)
                        {
                            got = Dose.CollectFromBin(_jobDealer.Container, _jobDealer.H, carry);
                            _jobDealer.BinDoses = Math.Max(0, _jobDealer.BinDoses - got);
                        }
                        else
                        {
                            got = 0;
                            PruneDealerStash(_jobDealer);
                            for (int i = _jobDealer.Stash.Count - 1; i >= 0; i--)
                            {
                                var it = _jobDealer.Stash[i]; _jobDealer.Stash.RemoveAt(i);
                                if (Dose.Pickup(it, _jobDealer.H)) got++;
                            }
                        }
                        Log($"COLLECT: {NarcoticsPlugin.Name(_jobDealer.H)} picked up x{got} from their dead drop (holding {Dose.CountIn(_jobDealer.H)}).");
                        EndJob();
                        break;
                    }

                    case Job.SellToBuyer:
                    {
                        if (_jobDealer == null || _jobDealer.H == null || !Alive(_jobDealer.H) || _jobBuyer == null || !Alive(_jobBuyer)) { EndJob(); break; }
                        if (Dose.CountIn(_jobDealer.H) <= 0) { EndJob(); break; }       // nothing left to sell
                        if (_meetNode == null) { try { _meetNode = _jobDealer.H.currentNode; } catch { } }
                        if (_meetNode == null) { EndJob(); break; }

                        Herd.ToNode(_jobDealer.H, _meetNode, _jobDealer.DropLoc, RangeM);   // dealer waits at the public spot
                        _targetNode = _meetNode;
                        bool buyerHere = Herd.ToNode(_jobBuyer, _meetNode, null, RangeM);   // passer-by walks over
                        if (!buyerHere) break;

                        if (_dwellUntil <= 0f) { _dwellUntil = Time.time + Math.Max(0.5f, NarcoticsConfig.DealDwellSeconds); Log($"DEALING: {NarcoticsPlugin.Name(_jobDealer.H)} meeting buyer {NarcoticsPlugin.Name(_jobBuyer)}..."); break; }
                        if (Time.time < _dwellUntil) break;   // the visible pause so the deal is obvious

                        bool sold = Dose.Transfer(_jobDealer.H, _jobBuyer);   // move the real item -> carries dealer + buyer prints
                        if (sold && !HasBuyer(SafeId(_jobBuyer))) Buyers.Add(_jobBuyer);
                        if (sold) MaybeScheduleOverdose(_jobBuyer);           // one buyer/day ODs a few minutes later
                        // Feed the vanilla witness system: the buyer genuinely "saw" the dealer, so interrogating
                        // the arrested buyer ("seen anything unusual?") can surface the dealer + location. A calm
                        // sale records a MUNDANE sighting that is lost among dozens of normal ones, so we also mark
                        // that sighting as notable (point-of-interest / odd behaviour) to stand out as "unusual".
                        if (sold) { try { _jobBuyer.UpdateLastSighting(_jobDealer.H, false, 0); FlagSightingNotable(_jobBuyer, _jobDealer.H); _buyerDealer[SafeId(_jobBuyer)] = _jobDealer.H; } catch { } }
                        _jobDealer.NextSell = Herd.NowHours() + Math.Max(0.05f, NarcoticsConfig.DealerSellIntervalHours);
                        Log($"SOLD: {NarcoticsPlugin.Name(_jobDealer.H)} -> buyer {NarcoticsPlugin.Name(_jobBuyer)} x{(sold ? 1 : 0)} (buyer holds {Dose.CountIn(_jobBuyer)}; {Buyers.Count} buyer(s) total).");
                        EndJob();
                        break;
                    }

                    default: EndJob(); break;
                }
            }
            catch (Exception e) { Log($"RunJob error: {e.Message}"); EndJob(); }
        }

        private static void Begin(Job j, Human mover) { _job = j; _mover = mover; _jobDist = null; _jobDealer = null; _jobBuyer = null; _cookProduced = false; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginDist(Job j, Dist d) { _job = j; _mover = d.H; _jobDist = d; _jobDealer = null; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginDrop(Dist d, Dealer dl) { _job = Job.DistDrop; _mover = d.H; _jobDist = d; _jobDealer = dl; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginDealer(Dealer dl) { _job = Job.DealerCollect; _mover = dl.H; _jobDist = null; _jobDealer = dl; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginSell(Dealer dl, Human buyer) { _job = Job.SellToBuyer; _mover = buyer; _jobDist = null; _jobDealer = dl; _jobBuyer = buyer; _meetNode = null; _dwellUntil = 0f; _deadline = Time.time + JobTimeoutSec; try { _meetNode = dl.H.currentNode; } catch { } }
        private static void EndJob()
        {
            try { if (_mover != null) Herd.Release(_mover); } catch { }
            try { if (_job == Job.SellToBuyer && _jobDealer != null && _jobDealer.H != null) Herd.Release(_jobDealer.H); } catch { }
            _job = Job.None; _mover = null; _jobDist = null; _jobDealer = null; _jobBuyer = null; _meetNode = null; _dwellUntil = 0f; _targetNode = null;
        }

        private static bool HasBuyer(int id) { try { for (int i = 0; i < Buyers.Count; i++) { var b = Buyers[i]; if (b != null && b.humanID == id) return true; } } catch { } return false; }

        private static bool IsOperationMember(int id)
        {
            try
            {
                if (CookHuman != null && CookHuman.humanID == id) return true;
                for (int i = 0; i < Distributors.Count; i++) { var d = Distributors[i]; if (d.H != null && d.H.humanID == id) return true; }
                for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && dl.H.humanID == id) return true; }
            }
            catch { }
            return false;
        }

        // Nearest citizen within range of the dealer who isn't the player or part of the operation - a random
        // passer-by to sell to.
        private static Human PickPasserBy(Human dealer)
        {
            try
            {
                NewNode dn = null; try { dn = dealer.currentNode; } catch { }
                if (dn == null) return null;
                var pos = dn.position;
                float range = Math.Max(2f, NarcoticsConfig.BuyerSearchRangeM);
                var cd = CityData.Instance; var cits = cd != null ? cd.citizenDirectory : null; if (cits == null) return null;
                int playerId = PlayerId();
                Human best = null; float bestD = float.MaxValue;
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (id == playerId || IsOperationMember(id)) continue;
                    try { if (Arrest.Active && Arrest.Subject != null && Arrest.Subject.humanID == id) continue; } catch { }   // don't deal to someone being arrested
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    float d; try { d = Vector3.Distance(pos, hn.position); } catch { continue; }
                    if (d <= range && d < bestD) { bestD = d; best = h; }
                }
                return best;
            }
            catch { return null; }
        }

        // Debug (F4): force a dealer who is holding doses to sell to the nearest passer-by right now (bypasses the
        // sell interval + scan throttle), so the dealer->buyer deal can be watched on demand.
        internal static void ForceSaleNow()
        {
            if (!Active) { Log("no active operation."); return; }
            _forceSale = true; _nextSellScan = 0f;
            int ready = 0; for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && Dose.CountIn(dl.H) > 0) ready++; }
            Log($"F4: forcing a sale now ({ready} dealer(s) currently holding doses). The dealer will deal to the nearest passer-by.");
        }

        // ------------------------------------------------------------------ daily overdose -> arrest -> case

        private static int DayIndex() { return (int)(Herd.NowHours() / 24f); }

        // On the day's first sale, schedule that buyer to overdose a few in-game minutes later (one per day).
        private static void MaybeScheduleOverdose(Human buyer)
        {
            try
            {
                if (buyer == null) return;
                int day = DayIndex();
                if (_odScheduledDay == day) return;         // already have today's overdose lined up
                _odScheduledDay = day;
                _odBuyerId = SafeId(buyer);
                _odAtHours = Herd.NowHours() + Math.Max(0.1f, NarcoticsConfig.OverdoseDelayMinutes) / 60f;
                Log($"OVERDOSE SCHEDULED: {NarcoticsPlugin.Name(buyer)} will overdose in ~{NarcoticsConfig.OverdoseDelayMinutes:0} in-game min, triggering an arrest + the case.");
            }
            catch { }
        }

        // Fire the scheduled overdose once its time arrives: hand the buyer to the synthetic arrest (which summons
        // an enforcer, restrains them, and opens the player's case). Skipped if an arrest is already running.
        private static void TickOverdose()
        {
            try
            {
                if (_odBuyerId == -1) return;
                if (Herd.NowHours() < _odAtHours) return;
                int id = _odBuyerId; _odBuyerId = -1;          // consume the schedule regardless of outcome
                if (Arrest.Active) { Log("scheduled overdose skipped: an arrest is already in progress."); return; }
                var buyer = FindBuyerById(id);
                if (buyer == null || !Alive(buyer)) { Log("scheduled overdose buyer is gone; skipping."); return; }
                Log($"OVERDOSE: {NarcoticsPlugin.Name(buyer)} overdoses in public.");
                Arrest.Begin(buyer, true);
            }
            catch (Exception e) { Log($"TickOverdose error: {e.Message}"); }
        }

        private static Human FindBuyerById(int id)
        {
            try { for (int i = 0; i < Buyers.Count; i++) { var b = Buyers[i]; if (b != null && SafeId(b) == id) return b; } } catch { }
            return null;
        }

        // Periodically re-assert each buyer's sighting of their dealer. SoD evicts old sightings as the buyer keeps
        // seeing new people, so a one-time record is forgotten (the buyer "reverted" to seeing nothing unusual) -
        // this keeps the dealer in memory + notable so "seen anything unusual" reliably names them, incl. post-arrest.
        private static void TickReinforceSightings()
        {
            try
            {
                if (_buyerDealer.Count == 0) return;
                if (NarcCase.IsSolved()) return;   // case solved -> stop reinforcing; the investigation is over
                if (Time.time < _nextSightingReinforce) return;
                _nextSightingReinforce = Time.time + 20f;
                var ids = new List<int>(_buyerDealer.Keys);
                for (int i = 0; i < ids.Count; i++)
                {
                    int bid = ids[i];
                    Human dealer = null; try { dealer = _buyerDealer[bid]; } catch { }
                    if (dealer == null || !Alive(dealer)) continue;
                    var buyer = FindBuyerById(bid);
                    if (buyer == null || !Alive(buyer)) continue;
                    try { buyer.UpdateLastSighting(dealer, false, 0); } catch { }
                    FlagSightingNotable(buyer, dealer);
                }
            }
            catch (Exception e) { Log($"TickReinforceSightings error: {e.Message}"); }
        }

        // Mark `witness`'s sighting of `seen` as notable (point-of-interest + odd behaviour), so the vanilla "seen
        // anything unusual?" dialog treats it as worth describing rather than one of dozens of mundane sightings.
        // Matches the entry by humanID (robust against wrapper identity). Experimental: if this surfaces the dealer
        // we have the trail; if not, the inspector dump shows the recorded state to iterate from.
        private static void FlagSightingNotable(Human witness, Human seen)
        {
            try
            {
                var ls = witness != null ? witness.lastSightings : null;
                if (ls == null || seen == null) return;
                int sid = SafeId(seen);
                foreach (var key in ls.Keys)
                {
                    if (key == null || SafeId(key) != sid) continue;
                    try { var s = ls[key]; if (s != null) { s.poi = true; s.run = true; } } catch { }
                    return;
                }
            }
            catch { }
        }

        // When the player solves/closes the case (vanilla resolution), stand the operation down: release the cuffed
        // buyer and wipe every drug object (lab stash, bins, all inventories). "Wipe them all once solved." Stop()
        // does the dose wipe (DestroyAll + DestroyWorldDoses). Returns true if it stood down this tick.
        private static bool TickCaseResolution()
        {
            try
            {
                if (!NarcCase.IsOpen || !NarcCase.IsSolved()) return false;
                Log("case solved/closed - standing the operation down and wiping all drug objects.");
                try { Arrest.Stop(); } catch { }
                NarcCase.MarkClosed();
                Stop();
                return true;
            }
            catch (Exception e) { Log($"TickCaseResolution error: {e.Message}"); return false; }
        }

        // ------------------------------------------------------------------ helpers

        // A single STABLE node inside the lab, cached and reused for everyone who walks there. Critical: computing
        // a fresh FindSafeTeleport each tick returned a shifting node, so Herd kept recreating the goal every frame
        // and the NPC never finished opening a door (the "stuck leaving their apartment" jam). Motives pinned to a
        // node computed ONCE - same idea here.
        private static NewNode LabNode(Human mover)
        {
            try { if (_labNode != null && _labNode.Pointer != IntPtr.Zero) return _labNode; } catch { _labNode = null; }
            NewNode n = null;
            try { n = mover.FindSafeTeleport(Lab, false, true); } catch { }
            if (n != null) { _labNode = n; return n; }
            try { return Lab.anchorNode; } catch { }   // transient fallback until the lab streams; not cached
            return null;
        }

        private static void EnsureDrop(Dealer dl)
        {
            try
            {
                if (dl == null || dl.Drop != null) return;
                NewGameLocation loc = null; NewNode node = null;
                try { loc = dl.H.currentGameLocation; } catch { }
                try { if (loc != null) node = dl.H.FindSafeTeleport(loc, false, true); } catch { }
                if (node == null) { try { node = dl.H.currentNode; } catch { } }
                dl.Drop = node; dl.DropLoc = loc;
            }
            catch { }
        }

        private static int LabStashCount() { PruneLabStash(); return LabStash.Count; }
        private static void PruneLabStash() { try { for (int i = LabStash.Count - 1; i >= 0; i--) if (!Dose.Alive(LabStash[i])) LabStash.RemoveAt(i); } catch { } }
        private static int DealerStashCount(Dealer dl) { if (dl == null) return 0; if (dl.Container != null) return dl.BinDoses; PruneDealerStash(dl); return dl.Stash.Count; }

        // Authoritative drop count (reads the bin page for container drops); for the F3 teleport verification.
        internal static int DropCount(Dealer dl) { if (dl == null) return 0; if (dl.Container != null) return Dose.CountInBin(dl.Container); PruneDealerStash(dl); return dl.Stash.Count; }
        private static void PruneDealerStash(Dealer dl) { try { for (int i = dl.Stash.Count - 1; i >= 0; i--) if (!Dose.Alive(dl.Stash[i])) dl.Stash.RemoveAt(i); } catch { } }

        private static void DestroyWorldDoses()
        {
            try { for (int i = 0; i < LabStash.Count; i++) { try { var it = LabStash[i]; if (it != null) it.SafeDelete(true); } catch { } } } catch { }
            LabStash.Clear();
            try
            {
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.Container != null) { try { Dose.ClearBin(dl.Container); } catch { } dl.BinDoses = 0; }
                    for (int i = 0; i < dl.Stash.Count; i++) { try { var it = dl.Stash[i]; if (it != null) it.SafeDelete(true); } catch { } }
                    dl.Stash.Clear();
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ queries / display

        internal static string RoleOf(Human h)
        {
            try
            {
                if (!Active || h == null) return "-";
                if (CookHuman != null && h.humanID == CookHuman.humanID) return "COOK";
                for (int i = 0; i < Distributors.Count; i++) { var d = Distributors[i]; if (d.H != null && d.H.humanID == h.humanID) return "DISTRIBUTOR"; }
                for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && dl.H.humanID == h.humanID) return "DEALER"; }
                for (int b = 0; b < Buyers.Count; b++) { var by = Buyers[b]; if (by != null && by.humanID == h.humanID) return "BUYER"; }
            }
            catch { }
            return "-";
        }

        internal static string StateLabel
        {
            get
            {
                try
                {
                    if (!Active) return "inactive";
                    if (_job != Job.None && _mover != null) return $"{_job} {NarcoticsPlugin.Name(_mover)}";
                    return "waiting";
                }
                catch { return "?"; }
            }
        }

        internal static List<string> RosterLines()
        {
            var lines = new List<string>();
            try
            {
                var labNode = LabAnchor();
                lines.Add($"COOK        {NarcoticsPlugin.Name(CookHuman)}   x{Dose.CountIn(CookHuman)}{LineDist(CookHuman, labNode)}   [{StateLabel}]");
                lines.Add($"LAB STASH   x{LabStashCount()}");
                for (int i = 0; i < Distributors.Count; i++)
                {
                    var d = Distributors[i]; if (d.H == null) continue;
                    lines.Add($"DISTRIBUTOR {NarcoticsPlugin.Name(d.H)}   x{Dose.CountIn(d.H)}{(d.Collected ? " (done)" : "")}{LineDist(d.H, null)}{Mark(d.H)}");
                }
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k]; if (dl.H == null) continue;
                    lines.Add($"DEALER      {NarcoticsPlugin.Name(dl.H)}   x{Dose.CountIn(dl.H)}   drop:{DealerStashCount(dl)}{LineDist(dl.H, dl.Drop)}{Mark(dl.H)}");
                }
                if (Buyers.Count > 0)
                {
                    lines.Add($"BUYERS      x{Buyers.Count} (hold doses)");
                    for (int b = 0; b < Buyers.Count && b < 6; b++) { var by = Buyers[b]; if (by == null) continue; lines.Add($"  buyer     {NarcoticsPlugin.Name(by)}   x{Dose.CountIn(by)}{Mark(by)}"); }
                }
            }
            catch { }
            return lines;
        }

        private static string Mark(Human h) { try { if (_mover != null && h != null && _mover.humanID == h.humanID) return "  <= " + _job; } catch { } return ""; }

        // Distance readout for a roster line: distance to the mover's live target if this is the active mover,
        // else to a role-relevant fallback node (cook->lab, dealer->their drop). Shown so the player can gauge
        // progress from the apartment without tailing.
        private static string LineDist(Human h, NewNode fallback)
        {
            try
            {
                NewNode n = (_mover != null && h != null && _mover.humanID == h.humanID) ? _targetNode : fallback;
                float d = DistM(h, n);
                return (n != null && d >= 0f) ? $"   {d:0}m" : "";
            }
            catch { return ""; }
        }

        private static float DistM(Human h, NewNode node)
        {
            try { var cn = h != null ? h.currentNode : null; if (cn != null && node != null) return Vector3.Distance(cn.position, node.position); } catch { }
            return -1f;
        }

        private static NewNode LabAnchor() { try { return Lab != null ? Lab.anchorNode : null; } catch { return null; } }

        // Everyone involved in the operation, for the debug "teleport to next NPC" cycle.
        internal static List<Human> InvolvedNPCs()
        {
            var l = new List<Human>();
            try
            {
                if (CookHuman != null) l.Add(CookHuman);
                for (int i = 0; i < Distributors.Count; i++) if (Distributors[i].H != null) l.Add(Distributors[i].H);
                for (int k = 0; k < Dealers.Count; k++) if (Dealers[k].H != null) l.Add(Dealers[k].H);
            }
            catch { }
            return l;
        }

        internal static void Status()
        {
            if (!Active) { Log("STATUS: no active operation."); return; }
            Log($"STATUS: cook={NarcoticsPlugin.Name(CookHuman)} lab={Cook.LName(Lab)} cookDoses={Dose.CountIn(CookHuman)} labStash={LabStashCount()} job={_job} dists={Distributors.Count} dealers={Dealers.Count}");
        }

        internal static NewGameLocation LabLoc => Lab;

        // ------------------------------------------------------------------ staffing

        private static Human PickCook()
        {
            try
            {
                var cd = CityData.Instance;
                var cits = cd != null ? cd.citizenDirectory : null;
                if (cits == null) return null;
                int playerId = PlayerId();
                var pool = new List<Human>();
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null) continue;
                    try { if (h.humanID == playerId) continue; } catch { }
                    try { if (h.isHomeless) continue; } catch { }
                    try { if (h.home == null) continue; } catch { continue; }
                    try { if (h.job == null) continue; } catch { }
                    if (!Alive(h)) continue;
                    pool.Add(h);
                }
                Log($"cook pool: {pool.Count} housed, employed citizens.");
                if (pool.Count == 0) return null;
                return pool[_rng.Next(pool.Count)];
            }
            catch (Exception e) { Log($"PickCook error: {e.Message}"); return null; }
        }

        private static List<Human> SelectHomeless(int n, HashSet<int> used)
        {
            var result = new List<Human>();
            try
            {
                var cd = CityData.Instance;
                var homeless = cd != null ? cd.homelessDirectory : null;
                if (homeless == null) { Log("no homelessDirectory; can't pick dealers."); return result; }
                var pool = new List<Human>();
                for (int i = 0; i < homeless.Count; i++)
                {
                    Human h = null; try { h = homeless[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    if (used.Contains(SafeId(h))) continue;
                    pool.Add(h);
                }
                Log($"homeless pool: {pool.Count} available.");
                Draw(pool, n, used, result);
            }
            catch (Exception e) { Log($"SelectHomeless error: {e.Message}"); }
            return result;
        }

        private static List<Human> SelectCitizens(int n, HashSet<int> used)
        {
            var result = new List<Human>();
            try
            {
                var cd = CityData.Instance;
                var cits = cd != null ? cd.citizenDirectory : null;
                if (cits == null) return result;
                int playerId = PlayerId();
                var pool = new List<Human>();
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id = SafeId(h);
                    if (id == playerId || used.Contains(id)) continue;
                    try { if (h.isHomeless) continue; } catch { }
                    pool.Add(h);
                }
                Log($"citizen pool: {pool.Count} available.");
                Draw(pool, n, used, result);
            }
            catch (Exception e) { Log($"SelectCitizens error: {e.Message}"); }
            return result;
        }

        private static void Draw(List<Human> pool, int n, HashSet<int> used, List<Human> result)
        {
            for (int k = 0; k < n && pool.Count > 0; k++)
            {
                int idx = _rng.Next(pool.Count);
                var h = pool[idx]; pool.RemoveAt(idx);
                result.Add(h);
                try { used.Add(h.humanID); } catch { }
            }
        }

        // ------------------------------------------------------------------ misc

        private static int PlayerId() { try { if (Player.Instance != null) return Player.Instance.humanID; } catch { } return -1; }
        private static int SafeId(Human h) { try { return h.humanID; } catch { return -1; } }
        private static bool Alive(Human h) { if (h == null) return false; try { return !h.isDead; } catch { return true; } }
        private static string JobName(Human h) { try { return h.job != null ? h.job.name : "-"; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][op] " + m);
    }
}
