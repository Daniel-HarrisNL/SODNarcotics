using System;
using UnityEngine;

namespace Narcotics
{
    // Fast-forward time for testing (ported from the author's Motives mod, its End key). Uses the game's OWN
    // efficient fast-sim path: sets the game's fastest built-in speed ('simulation') and, if TimeBoostMultiplier
    // > 1, pushes the game's own currentTimeMultiplier to that multiple of simulation's base each frame. NOT
    // Time.timeScale (that brute-forces full-detail updates and actually runs slower).
    internal static class FastForward
    {
        private static bool _on;
        private static float _simBase;   // currentTimeMultiplier captured at 'simulation' speed

        internal static void Toggle()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var sd = SessionData.Instance;
                if (sd == null) { log.LogInfo("[Narcotics][time] no SessionData (load a game first)."); return; }
                _on = !_on;
                try { Time.timeScale = 1f; } catch { }
                if (_on)
                {
                    try { sd.SetTimeSpeed(SessionData.TimeSpeed.simulation); } catch { }
                    _simBase = 0f; try { _simBase = sd.currentTimeMultiplier; } catch { }
                    log.LogInfo($"[Narcotics][time] FAST-FORWARD ON - simulation speed (base x{_simBase:0.##}), pushing to x{(_simBase * NarcoticsConfig.TimeBoostMultiplier):0.##}. Press End again for normal.");
                }
                else
                {
                    _simBase = 0f;
                    try { sd.SetTimeSpeed(SessionData.TimeSpeed.normal); } catch { }
                    log.LogInfo("[Narcotics][time] FAST-FORWARD OFF - normal speed.");
                }
            }
            catch (Exception e) { log.LogWarning($"[Narcotics][time] error: {e.Message}"); }
        }

        // Re-assert the boosted multiplier each frame (the game may recompute it). No-op when off / multiplier 1.
        internal static void Apply()
        {
            try
            {
                if (!_on || NarcoticsConfig.TimeBoostMultiplier <= 1.01f || _simBase <= 0.01f) return;
                var sd = SessionData.Instance;
                if (sd == null) return;
                float target = _simBase * NarcoticsConfig.TimeBoostMultiplier;
                float cur = 0f; try { cur = sd.currentTimeMultiplier; } catch { return; }
                if (cur > 0.01f && cur < target - 0.01f) { try { sd.currentTimeMultiplier = target; } catch { } }
            }
            catch { }
        }
    }
}
