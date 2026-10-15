using System;

namespace Narcotics
{
    // Thin, defensive wrappers over the vanilla CitizenAnimationController (Actor.animationController) so our network
    // NPCs can play brief interaction gestures (dead-drop stash/collect) and hold a lean/sit idle (the dealer's fixed
    // dealing spot). Purely cosmetic: these set the citizen's desired animation pose and never move the NPC or touch
    // any object - the mod keeps owning routing and the real stash/collect logic. Members reflection-verified
    // (ArmsBoolSate / IdleAnimationState enums, SetArmsBoolState / SetIdleAnimationState / ForceUpdateAnimationSate).
    internal static class Anim
    {
        private static CitizenAnimationController AC(Human h)
        {
            try { return h != null ? h.animationController : null; } catch { return null; }
        }

        // Hold the lock/unlock arm gesture (the "working a lock" pose) - the dead-drop interaction the user asked for.
        // It is a HELD pose, so call Rest() when the interaction finishes.
        internal static void Locking(Human h) { SetArms(h, CitizenAnimationController.ArmsBoolSate.armsLocking); }
        internal static void Rest(Human h) { SetArms(h, CitizenAnimationController.ArmsBoolSate.armsResting); }

        private static void SetArms(Human h, CitizenAnimationController.ArmsBoolSate s)
        {
            var ac = AC(h); if (ac == null) return;
            try { ac.SetArmsBoolState(s); } catch { }
            try { ac.ForceUpdateAnimationSate(true); } catch { }   // apply even if the NPC is currently culled/off-screen
        }

        // Set a body idle pose (lean against a wall at the dealing spot), or clear it back to the default.
        internal static void Lean(Human h) { Idle(h, CitizenAnimationController.IdleAnimationState.leanAgainstWall); }
        internal static void IdleNone(Human h) { Idle(h, CitizenAnimationController.IdleAnimationState.none); }

        private static void Idle(Human h, CitizenAnimationController.IdleAnimationState s)
        {
            var ac = AC(h); if (ac == null) return;
            try { ac.SetIdleAnimationState(s); } catch { }
            try { ac.ForceUpdateAnimationSate(true); } catch { }
        }
    }
}
