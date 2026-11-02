using System;

public enum PetFastHandReactionPhase { None, FastRun, Unbalance, Brace, LookBack, Recover }

/// <summary>Deterministic, visual-only reaction shared by native and Editor hand-leading.</summary>
public sealed class PetFastHandReaction
{
    public const float SpeedThreshold = 560f;
    public const float SustainSeconds = .20f;
    public const float FilterSeconds = .08f;
    public const float CooldownSeconds = 6f;
    public const float Duration = 1.92f;

    public struct Pose
    {
        public PetFastHandReactionPhase phase;
        public float leanDegrees, braceWeight, lookBackWeight, direction;
        public bool Active => phase != PetFastHandReactionPhase.None;
    }

    // Nine restrained key poses: quick run, lean, free-hand brace, glance, recover.
    private static readonly float[] Times = { 0, .18f, .38f, .60f, .82f, 1.06f, 1.30f, 1.56f, Duration };
    private static readonly float[] Leans = { 0, 3, 13, 21, 17, 11, 7, 3, 0 };
    private static readonly float[] Braces = { 0, .05f, .18f, .62f, 1, 1, .72f, .38f, 0 };
    private static readonly float[] Glances = { 0, 0, 0, 0, .05f, .55f, 1, .50f, 0 };
    private bool session, reacting;
    private float previousAt, sustained, startedAt, nextAllowedAt = float.NegativeInfinity, reactionDirection;
    public bool TriggeredThisLead { get; private set; }
    public float FilteredSpeed { get; private set; }
    public Pose CurrentPose { get; private set; }

    public void Begin(float now)
    {
        Cancel();
        session = Finite(now);
        previousAt = now;
        TriggeredThisLead = false;
    }

    /// <summary>Cancel the current overlay while retaining its cooldown across hand holds.</summary>
    public void Cancel()
    {
        session = reacting = false;
        sustained = FilteredSpeed = 0;
        CurrentPose = default;
    }

    /// <summary>Cancel on lifting without rearming this hand hold when it returns to the ground.</summary>
    public void CancelReaction()
    {
        reacting = false;
        sustained = FilteredSpeed = 0;
        CurrentPose = default;
    }

    public Pose Step(float speed, float direction, bool running, bool grounded, float now)
    {
        if (!session || !Finite(now) || now < previousAt)
        {
            Cancel();
            return CurrentPose;
        }
        float elapsed = now - previousAt;
        previousAt = now;
        if (!grounded)
        {
            CancelReaction();
            return CurrentPose;
        }
        // A paused process or missing input frame cannot count as sustained fast movement.
        if (elapsed > .25f) { sustained = FilteredSpeed = 0; }
        float dt = Math.Min(elapsed, .1f);
        speed = Finite(speed) ? Math.Max(0, Math.Min(speed, 1400f)) : 0;
        FilteredSpeed += (speed - FilteredSpeed) * (1f - (float)Math.Exp(-dt / FilterSeconds));
        if (!reacting && !TriggeredThisLead && now >= nextAllowedAt)
        {
            sustained = running && FilteredSpeed >= SpeedThreshold ? sustained + dt : 0;
            if (sustained >= SustainSeconds && Finite(direction) && direction != 0)
            {
                reacting = TriggeredThisLead = true;
                startedAt = now;
                nextAllowedAt = now + CooldownSeconds;
                reactionDirection = direction < 0 ? -1 : 1;
            }
        }
        CurrentPose = reacting ? SamplePose(now - startedAt, reactionDirection) : default;
        if (!CurrentPose.Active) reacting = false;
        return CurrentPose;
    }

    /// <summary>Pure nine-key pose sampling for QA and imported rigs; never moves a root or foot.</summary>
    public static Pose SamplePose(float elapsed, float direction)
    {
        if (!Finite(elapsed) || !Finite(direction) || elapsed < 0 || elapsed >= Duration) return default;
        int key = 0;
        while (key < Times.Length - 2 && elapsed > Times[key + 1]) key++;
        float t = (elapsed - Times[key]) / (Times[key + 1] - Times[key]);
        t = t * t * (3f - 2f * t);
        return new Pose
        {
            phase = elapsed < .22f ? PetFastHandReactionPhase.FastRun
                : elapsed < .62f ? PetFastHandReactionPhase.Unbalance
                : elapsed < 1.10f ? PetFastHandReactionPhase.Brace
                : elapsed < 1.50f ? PetFastHandReactionPhase.LookBack : PetFastHandReactionPhase.Recover,
            leanDegrees = Leans[key] + (Leans[key + 1] - Leans[key]) * t,
            braceWeight = Braces[key] + (Braces[key + 1] - Braces[key]) * t,
            lookBackWeight = Glances[key] + (Glances[key + 1] - Glances[key]) * t,
            direction = direction < 0 ? -1 : 1
        };
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
