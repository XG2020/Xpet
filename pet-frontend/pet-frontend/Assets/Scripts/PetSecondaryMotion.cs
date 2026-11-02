using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Small, bounded spring rotations for Lita's existing skirt and hair bones.
/// This is secondary animation, not a reconstruction of the PMX rigid-body simulation.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public sealed class PetSecondaryMotion : MonoBehaviour
{
    [SerializeField] private Transform visualModel;
    [SerializeField] private PetCharacterAnimation character;
    [SerializeField, Range(1, 6)] private float springFrequency = 3.2f;
    [SerializeField, Range(0.5f, 1.2f)] private float dampingRatio = 0.82f;
    [SerializeField, Range(0, 2)] private float motionStrength = 1f;
    private readonly List<SpringBone> springs = new List<SpringBone>();
    private bool initialized;
    private bool desktopDragging;
    private float desktopVelocity;
    private float desktopInputAt;
    private float dragDrive;
    private float walkBlend;
    private float walkPhase;

    private sealed class SpringBone
    {
        public Transform bone;
        public Quaternion restRotation;
        public Vector3 lastAnchor;
        public Vector3 observedVelocity;
        public Quaternion lastAnchorRotation;
        public Vector3 observedAngularVelocity;
        public Vector2 angle;
        public Vector2 velocity;
        public float limit;
        public float strength;
        public float phase;
        public bool hair;
    }

    public int SimulatedBoneCount { get { return springs.Count; } }

    public void Configure(Transform model, PetCharacterAnimation animation)
    {
        visualModel = model;
        character = animation;
    }

    private void Awake()
    {
        if (visualModel == null) visualModel = transform.Find("VisualModel");
        if (character == null) character = GetComponent<PetCharacterAnimation>();
        if (visualModel == null) return;
        var skinnedBones = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer skin in visualModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach (Transform bone in skin.bones) if (bone != null) skinnedBones.Add(bone);

        // Sample three joints per long skirt strip rather than adding the same
        // rotation at all 19 joints. The sum of the limits stays modest, and the
        // unmodified top rows keep the waist seam attached to the body.
        foreach (Transform bone in visualModel.GetComponentsInChildren<Transform>(true))
        {
            if (!skinnedBones.Contains(bone) || bone.childCount == 0) continue;
            bool known = TryGetSettings(bone.name, out bool hair, out float strength, out float phase);
            if (!known && !TryGetImportedSettings(bone, out hair, out strength, out phase)) continue;
            springs.Add(new SpringBone
            {
                bone = bone,
                restRotation = bone.localRotation,
                lastAnchor = bone.position,
                hair = hair,
                strength = strength,
                phase = phase,
                limit = known ? (hair ? 4.5f : 3.2f) : (hair ? 2.5f : 2f)
            });
        }
        initialized = true;
        ResetMotion();
    }

    private void OnEnable() { if (initialized) ResetMotion(); }

    private void OnDisable()
    {
        RestoreRestPose();
        desktopDragging = false;
        desktopVelocity = dragDrive = walkBlend = 0;
    }

    /// <summary>Native window dragging does not translate Unity's scene. Supply its
    /// pointer velocity explicitly so clothes still lag behind a carried character.</summary>
    public void SetDesktopMotion(bool dragging, float horizontalPixelsPerSecond)
    {
        desktopDragging = dragging;
        desktopVelocity = dragging && !float.IsNaN(horizontalPixelsPerSecond) && !float.IsInfinity(horizontalPixelsPerSecond)
            ? Mathf.Clamp(horizontalPixelsPerSecond, -1800, 1800) : 0;
        desktopInputAt = Time.unscaledTime;
    }

    private void ResetMotion()
    {
        RestoreRestPose();
        foreach (SpringBone spring in springs)
        {
            if (spring.bone == null) continue;
            spring.lastAnchor = spring.bone.position;
            spring.lastAnchorRotation = spring.bone.parent.rotation;
            spring.observedVelocity = spring.observedAngularVelocity = Vector3.zero;
            spring.angle = spring.velocity = Vector2.zero;
        }
        dragDrive = walkBlend = walkPhase = 0;
    }

    private void RestoreRestPose()
    {
        foreach (SpringBone spring in springs)
            if (spring.bone != null) spring.bone.localRotation = spring.restRotation;
    }

    private void LateUpdate()
    {
        Simulate(Time.unscaledDeltaTime, Time.unscaledTime);
    }

    private void Simulate(float delta, float now)
    {
        if (!initialized || springs.Count == 0) return;
        if (delta <= 0) return;
        // Avoid accumulated energy after a paused Editor or a long frame.
        if (delta > 0.15f) { ResetMotion(); return; }
        float dt = delta;
        bool walking = character != null && character.IsWalking;
        walkBlend = Mathf.MoveTowards(walkBlend, walking ? 1 : 0, dt * 4f);
        walkPhase = Mathf.Repeat(walkPhase + dt * (Mathf.PI * 2 / 1.05f), Mathf.PI * 2);
        float input = desktopDragging && now - desktopInputAt < 0.15f ? desktopVelocity / 900f : 0;
        dragDrive = Mathf.Lerp(dragDrive, input, 1 - Mathf.Exp(-dt * 10));

        // These bones are intentionally absent from the Animator curves. Reset
        // only this component's joints before observing the newly animated body,
        // so last frame's spring offset cannot feed itself back into the solver.
        RestoreRestPose();
        foreach (SpringBone spring in springs)
        {
            if (spring.bone == null) continue;
            Vector3 anchor = spring.bone.position;
            Vector3 velocity = transform.InverseTransformVector((anchor - spring.lastAnchor) / delta);
            spring.observedVelocity = Vector3.ClampMagnitude(velocity, 1.5f);
            // A torso turn can leave an attachment pivot almost stationary.
            // Its rotation still carries momentum into a skirt or hair strand.
            Quaternion anchorRotation = spring.bone.parent.rotation;
            Quaternion turn = anchorRotation * Quaternion.Inverse(spring.lastAnchorRotation);
            if (turn.w < 0) turn = new Quaternion(-turn.x, -turn.y, -turn.z, -turn.w);
            turn.ToAngleAxis(out float degrees, out Vector3 axis);
            spring.observedAngularVelocity = degrees > 0.001f && !float.IsNaN(axis.x)
                ? Vector3.ClampMagnitude(transform.InverseTransformDirection(axis) * (degrees / delta), 180f)
                : Vector3.zero;
            spring.lastAnchorRotation = anchorRotation;
            spring.lastAnchor = anchor;
        }

        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / (1f / 120f)), 1, 18);
        float step = dt / steps;
        float omega = Mathf.PI * 2 * Mathf.Clamp(springFrequency, 1, 6);
        float damping = 2 * Mathf.Clamp(dampingRatio, 0.5f, 1.2f) * omega;
        foreach (SpringBone spring in springs)
        {
            if (spring.bone == null) continue;
            Vector3 inertia = spring.observedVelocity;
            float flutter = Mathf.Sin(now * 1.5f + spring.phase) * (spring.hair ? 0.28f : 0.35f);
            float stride = Mathf.Sin(walkPhase - spring.phase * 0.35f) * walkBlend;
            Vector2 target = new Vector2(
                -inertia.z * 7f - inertia.y * 2f - spring.observedAngularVelocity.x * 0.025f + stride * 2.1f + flutter,
                inertia.x * 9f - spring.observedAngularVelocity.z * 0.025f
                    - spring.observedAngularVelocity.y * 0.012f + dragDrive * 2.6f + stride * 0.65f);
            target *= spring.strength * Mathf.Clamp(motionStrength, 0, 2);
            target = Vector2.ClampMagnitude(target, spring.limit);
            for (int i = 0; i < steps; i++)
            {
                spring.velocity += ((target - spring.angle) * omega * omega - spring.velocity * damping) * step;
                spring.velocity = Vector2.ClampMagnitude(spring.velocity, 55f);
                spring.angle += spring.velocity * step;
                if (spring.angle.sqrMagnitude > spring.limit * spring.limit)
                {
                    spring.angle = Vector2.ClampMagnitude(spring.angle, spring.limit);
                    float outwardVelocity = Vector2.Dot(spring.velocity, spring.angle.normalized);
                    if (outwardVelocity > 0) spring.velocity -= spring.angle.normalized * outwardVelocity;
                }
            }
            // Top-to-bottom hierarchy order allows child joints to follow their
            // parent. Only rotation is written, so every original bone length and
            // local position remains exactly intact.
            Quaternion baseRotation = spring.bone.parent.rotation * spring.restRotation;
            spring.bone.rotation = Quaternion.AngleAxis(spring.angle.y, transform.forward)
                * Quaternion.AngleAxis(spring.angle.x, transform.right) * baseRotation;
        }
    }

    private static bool ImportedChainKind(string name, out bool hair)
    {
        hair = name.StartsWith("Hair", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("髪", StringComparison.Ordinal) || name.Contains("髮")
            || name.Contains("髪") || name.Contains("刘海") || name.Contains("劉海");
        if (name.StartsWith("_", StringComparison.Ordinal) || name.EndsWith("end", StringComparison.OrdinalIgnoreCase)) return false;
        return hair || name.StartsWith("Skirt", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Cloth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ribbon") || name.StartsWith("裙", StringComparison.Ordinal)
            || name.StartsWith("外套", StringComparison.Ordinal) || name.StartsWith("結", StringComparison.Ordinal);
    }

    private static bool TryGetImportedSettings(Transform bone, out bool hair, out float strength, out float phase)
    {
        strength = phase = 0;
        if (!ImportedChainKind(bone.name, out hair)) return false;
        int depth = 0;
        for (Transform parent = bone.parent; parent != null && ImportedChainKind(parent.name, out bool parentHair)
                && parentHair == hair; parent = parent.parent) depth++;
        // Keep the root seam fixed; sample at most three joints even in long
        // chains. Do not add a full-strength rotation at every imported joint.
        if (depth != 1 && depth != 3 && depth != 5) return false;
        strength = depth == 1 ? 0.65f : depth == 3 ? 0.42f : 0.25f;
        phase = depth * 0.22f + (bone.name.Contains("L") || bone.name.Contains("左") ? 0.4f : 0);
        return true;
    }

    private static bool TryGetSettings(string name, out bool hair, out float strength, out float phase)
    {
        hair = false;
        strength = 1f;
        phase = 0;
        if (name.EndsWith("_end", StringComparison.Ordinal) || name.EndsWith("End", StringComparison.Ordinal)) return false;
        if (name.StartsWith("qz_", StringComparison.Ordinal) || name.StartsWith("qh_", StringComparison.Ordinal))
        {
            string[] parts = name.Split('_');
            if (parts.Length != 3 || !int.TryParse(parts[1], out int row) || !int.TryParse(parts[2], out int panel)) return false;
            bool front = parts[0] == "qz";
            int middle = front ? 7 : 6;
            int lower = front ? 12 : 10;
            if (row != 2 && row != middle && row != lower) return false;
            strength = row == 2 ? 0.8f : row == middle ? 0.65f : 0.45f;
            phase = panel * 0.17f + row * 0.13f;
            return true;
        }
        hair = true;
        if (name.StartsWith("Bone_C7_Head_Hair_", StringComparison.Ordinal))
        {
            strength = name.EndsWith("01", StringComparison.Ordinal) ? 0.7f : 0.5f;
            phase = name.Contains("_L_") || name.Contains("_FL_") ? 0.8f : 0.2f;
            return name.EndsWith("01", StringComparison.Ordinal) || name.EndsWith("02", StringComparison.Ordinal);
        }
        if (name.StartsWith("Bone_C7_Head_Earrings_", StringComparison.Ordinal))
        {
            strength = 0.7f;
            return name.EndsWith("01", StringComparison.Ordinal);
        }
        if (name == "hf_2_1" || name == "hf_7_1" || name == "hf_12_1"
            || name == "zb_2_1" || name == "zb_6_1" || name == "yb__3" || name == "yb__7")
        {
            strength = 0.6f;
            phase = name.StartsWith("hf_", StringComparison.Ordinal) ? 0.5f : 1.1f;
            return true;
        }
        return false;
    }
}
