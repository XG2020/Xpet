using System;

public enum PetBehaviorState { Idle, Walk, Interact, Sleep, Special, Hidden }
public enum PetSleepPhase { None, Entering, Sleeping, Waking }
public enum PetActionOrigin { AiReply, LocalInteraction, ManualPreview }

/// <summary>Deterministic behavior policy; no Unity objects, timers or global input reads.</summary>
public sealed class PetBehaviorStateMachine
{
    public PetSleepPhase SleepPhase { get; private set; }
    public double PhaseStartedAt { get; private set; }
    public double LastActivityAt { get; private set; }
    public PetBehaviorStateMachine(double now) { LastActivityAt = now; }
    public void Activity(double now) { LastActivityAt = now; }
    public void BeginSleep(double now) { SleepPhase = PetSleepPhase.Entering; PhaseStartedAt = now; }
    public void Wake(double now, bool immediate)
    {
        Activity(now);
        if (immediate) { SleepPhase = PetSleepPhase.None; PhaseStartedAt = now; }
        else if (SleepPhase == PetSleepPhase.Entering || SleepPhase == PetSleepPhase.Sleeping) {
            SleepPhase = PetSleepPhase.Waking; PhaseStartedAt = now;
        }
    }
    public void Tick(double now, double enterDuration, double wakeDuration)
    {
        if (SleepPhase == PetSleepPhase.Entering && now - PhaseStartedAt >= enterDuration) {
            SleepPhase = PetSleepPhase.Sleeping; PhaseStartedAt = now;
        } else if (SleepPhase == PetSleepPhase.Waking && now - PhaseStartedAt >= wakeDuration) {
            SleepPhase = PetSleepPhase.None; PhaseStartedAt = now; Activity(now);
        }
    }
    public bool ShouldSleep(double now, double delay, bool enabled, bool idleAndAvailable) => enabled
        && idleAndAvailable && SleepPhase == PetSleepPhase.None && now - LastActivityAt >= Math.Max(1, delay);
    public static bool MustDeferReply(bool dragging, bool handLeading, bool falling, bool guarding, bool localInteraction)
        => dragging || handLeading || falling || guarding || localInteraction;
    public PetBehaviorState Resolve(bool visible, bool directInteraction, bool localInteraction, bool walking, bool actionPlaying)
        => !visible ? PetBehaviorState.Hidden : directInteraction || localInteraction ? PetBehaviorState.Interact
            : SleepPhase != PetSleepPhase.None ? PetBehaviorState.Sleep : walking ? PetBehaviorState.Walk
            : actionPlaying ? PetBehaviorState.Special : PetBehaviorState.Idle;
}
