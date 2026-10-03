using System;

namespace Narcotics
{
    // God mode for testing: NPCs and cameras ignore the player, no trespassing, invincible, echelon-zone access,
    // and a cash top-up. Ported from the author's Motives mod (its F7 "ghost" toggle) so you can freely tail the
    // cook and dealers without getting spotted, stopped or killed. Toggle with the GodMode key (default F7).
    internal static class GodMode
    {
        internal static bool On;

        internal static void Toggle()
        {
            On = !On;
            if (On) { GiveCash(); Apply(); }
            else Clear();
            NarcoticsPlugin.Log.LogInfo($"[Narcotics] GOD MODE {(On ? "ON" : "OFF")} (NPCs/cameras ignore you, no trespass, invincible, echelon access).");
        }

        // Called every frame while On: keep the player unseen, un-trespassing, healed and unstunned.
        internal static void Apply()
        {
            try
            {
                var p = Player.Instance;
                if (p == null) return;
                try { p.heat = 0f; } catch { }
                try { p.isTrespassing = false; } catch { }
                try { p.unreportable = true; } catch { }
                try { p.spottedState = 0f; } catch { }
                try { p.currentHealth = p.GetCurrentMaxHealth(); } catch { }
                try { p.isStunned = false; } catch { }
                EnsureEchelonAccess(true);
            }
            catch { }
        }

        internal static void Clear()
        {
            try { var p = Player.Instance; if (p != null) { try { p.unreportable = false; } catch { } } } catch { }
            EnsureEchelonAccess(false);
        }

        private static void GiveCash(int amount = 10000)
        {
            try { var gc = GameplayController.Instance; if (gc != null) gc.SetMoney(amount); }
            catch (Exception e) { NarcoticsPlugin.Log.LogWarning($"[Narcotics] god: SetMoney error: {e.Message}"); }
        }

        // Grant/revoke echelon (high-security) zone access by injecting the same passive effect the vanilla sync
        // disk uses, so locked residential zones are walkable while following NPCs. Self-heals if the list rebuilds.
        private static UpgradeEffectController.AppliedEffect _echelonGrant;
        private static void EnsureEchelonAccess(bool on)
        {
            try
            {
                var uec = UpgradeEffectController.Instance;
                if (uec == null) return;
                var list = uec.appliedEffects;
                if (list == null) return;
                if (on)
                {
                    bool present;
                    try { present = _echelonGrant != null && list.Contains(_echelonGrant); }
                    catch { present = _echelonGrant != null; }
                    if (present) return;
                    var eff = new UpgradeEffectController.AppliedEffect();
                    try { eff.effect = SyncDiskPreset.Effect.allowedInEchelons; } catch { }
                    try { eff.value = 1f; } catch { }
                    list.Add(eff);
                    _echelonGrant = eff;
                }
                else if (_echelonGrant != null)
                {
                    try { list.Remove(_echelonGrant); } catch { }
                    _echelonGrant = null;
                }
            }
            catch { }
        }
    }
}
