using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Places the held wrist at the pointer using the visible skeleton's arm lengths.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(300)]
public sealed class PetHandLeadArm : MonoBehaviour
{
    [SerializeField] private PetCharacterAnimation owner;
    [SerializeField] private Camera targetCamera;
    private Transform visualModel;
    private Arm leftArm, rightArm, overriddenArm;
    private Arm overriddenFreeArm;
    private BoneOffset torsoOffset, chestOffset, neckOffset, headOffset;
    private float nextResolveAt;
    private const float DirectionEpsilon = 0.000001f;

    private sealed class Arm
    {
        public Transform upper, elbow, wrist;
        public Quaternion upperBefore, elbowBefore, upperAfter, elbowAfter;
        public Vector3 direction, pole;
        public Vector2 pointer;
        public bool tracking;
        public bool Valid => upper != null && elbow != null && wrist != null;
    }

    private sealed class BoneOffset
    {
        public Transform bone;
        public Quaternion before, after;
        public bool applied;
        public void Apply(Vector3 worldAxis, float degrees)
        {
            if (bone == null) return;
            before = bone.localRotation;
            Vector3 axis = bone.parent != null ? bone.parent.InverseTransformVector(worldAxis) : worldAxis;
            // Rotation axes are pseudovectors: a reflected imported parent
            // needs the opposite local axis to produce the same visible turn.
            if (bone.parent != null && bone.parent.localToWorldMatrix.determinant < 0) axis = -axis;
            bone.localRotation = Quaternion.AngleAxis(degrees, axis.normalized) * before;
            after = bone.localRotation;
            applied = true;
        }
        public void Restore()
        {
            if (applied && bone != null && Quaternion.Angle(bone.localRotation, after) < .05f)
                bone.localRotation = before;
            applied = false;
        }
    }

    /// <summary>Wrist distance from the actual pointer projection, in world units; includes unreachable distance.</summary>
    public float TargetError { get; private set; }
    /// <summary>Interior elbow angle in degrees: 180 is completely straight.</summary>
    public float ElbowAngle { get; private set; }
    public string ActiveSide { get; private set; } = string.Empty;

    private void Awake() { ResolveReferences(); }

    private void Update()
    {
        // Some imported rigs have unanimated arm joints. Remove our last output
        // before animation sampling so an unkeyed joint cannot accumulate IK.
        RestorePreviousPose();
    }

    private void OnDisable()
    {
        RestorePreviousPose();
        ResetTracking();
    }

    private void LateUpdate()
    {
        // A still-valid old skeleton must not survive replacing VisualModel.
        if (owner != null && owner.transform.Find("VisualModel") != visualModel) ResolveReferences();
        if ((owner == null || visualModel == null || targetCamera == null || !targetCamera.isActiveAndEnabled
                || leftArm == null || !leftArm.Valid || rightArm == null || !rightArm.Valid)
                && Time.unscaledTime >= nextResolveAt)
            ResolveReferences();

        if (owner == null || !owner.isActiveAndEnabled || !owner.IsHandLeading || !owner.HandLeadHasPointer
            || targetCamera == null || !targetCamera.isActiveAndEnabled)
        {
            ResetTracking();
            return;
        }

        string side = owner.HandLeadSide;
        Arm arm = side == "L" ? leftArm : side == "R" ? rightArm : null;
        if (arm == null || !arm.Valid)
        {
            ResetTracking();
            return;
        }
        if (ActiveSide != side)
        {
            ResetTracking();
            ActiveSide = side;
        }

        ApplyHeldPose(arm, side, owner.HandLeadPointer, owner.HandFastReactionPose, owner.HandLeadTension,
            Mathf.Clamp(Time.unscaledDeltaTime, 0, 0.1f));
    }

    private bool ApplyHeldPose(Arm arm, string side, Vector2 pointer, PetFastHandReaction.Pose reaction, float tension, float dt)
    {
        // The shoulders may move during the stumble; solve the held arm last so
        // the exact same pointer target still owns that wrist.
        ApplyFastReaction(reaction, side, dt);
        float fullLength=Vector3.Distance(arm.upper.position,arm.elbow.position)+Vector3.Distance(arm.elbow.position,arm.wrist.position);
        tension = Finite(tension) ? Mathf.Clamp01(tension) : 0;
        // Use a stable shoulder-relative plane. The animated wrist can already
        // extend almost an entire arm toward the camera; using that depth made
        // even a nearby pointer unreachable and kept the elbow fully extended.
        float depth = targetCamera.WorldToScreenPoint(arm.upper.position).z-fullLength*Mathf.Lerp(.20f,.025f,tension);
        if (!Finite(pointer.x) || !Finite(pointer.y) || !Finite(depth) || depth <= 0)
        {
            ResetTracking();
            return false;
        }

        Vector3 pointerTarget = targetCamera.ScreenToWorldPoint(new Vector3(pointer.x, pointer.y, depth));
        // The extra torso motion must add no pointer lag during the reaction.
        arm.pointer = reaction.Active || !arm.tracking ? pointer
            : Vector2.Lerp(arm.pointer, pointer, 1 - Mathf.Exp(-Mathf.Lerp(35, 100, tension) * dt));
        Vector3 filteredTarget = targetCamera.ScreenToWorldPoint(new Vector3(arm.pointer.x, arm.pointer.y, depth));
        Solve(arm, pointerTarget, filteredTarget, tension, dt);
        return arm.tracking;
    }

    /// <summary>Offline pose sampling on an inactive QA model; does not change locomotion or actor position.</summary>
    public bool SampleFastReactionForQa(PetFastHandReaction.Pose reaction, string heldSide, Vector2 pointer, Camera camera)
    {
        RestorePreviousPose();
        if (camera != null) targetCamera = camera;
        ResolveReferences();
        Arm arm = heldSide == "L" ? leftArm : heldSide == "R" ? rightArm : null;
        if (targetCamera == null || arm == null || !arm.Valid) return false;
        ResetTracking(); ActiveSide = heldSide;
        return ApplyHeldPose(arm, heldSide, pointer, reaction, 1, 1f / 60f);
    }

    public void RestoreSampledPoseForQa() { RestorePreviousPose(); ResetTracking(); }

    private void ApplyFastReaction(PetFastHandReaction.Pose pose, string heldSide, float dt)
    {
        if (!pose.Active || visualModel == null || targetCamera == null) return;
        Vector3 leanAxis = targetCamera.transform.forward;
        float lean = -pose.direction * pose.leanDegrees;
        bool splitTorso = chestOffset != null;
        torsoOffset?.Apply(leanAxis, lean * (splitTorso ? .6f : 1));
        chestOffset?.Apply(leanAxis, lean * .4f);
        Vector3 up = targetCamera.transform.up;
        float glance = pose.direction * 60f * pose.lookBackWeight;
        neckOffset?.Apply(up, glance * .25f);
        headOffset?.Apply(up, glance * (neckOffset != null ? .75f : 1));

        Arm free = heldSide == "L" ? rightArm : leftArm;
        if (free == null || !free.Valid || pose.braceWeight <= .001f) return;
        float length = Vector3.Distance(free.upper.position, free.elbow.position)
            + Vector3.Distance(free.elbow.position, free.wrist.position);
        if (!Finite(length) || length <= DirectionEpsilon) return;
        Vector3 shoulder = free.upper.position;
        Vector3 wrist = free.wrist.position;
        // A bent balancing arm reaches outward, away from the chest and held arm.
        // Keep its target shoulder-relative; the feet and pelvis remain authored.
        Arm held = heldSide == "L" ? leftArm : rightArm;
        float freeSide = held != null && held.Valid
            ? Mathf.Sign(Vector3.Dot(shoulder - held.upper.position, targetCamera.transform.right)) : 0;
        if (Mathf.Abs(freeSide) < .5f) freeSide = heldSide == "L" ? 1 : -1;
        Vector3 balanced = shoulder + targetCamera.transform.right * (freeSide * length * .38f)
            - up * (length * .16f) - targetCamera.transform.forward * (length * .44f);
        Vector3 target = Vector3.Lerp(wrist, balanced, pose.braceWeight);
        Solve(free, target, target, .25f, dt, false);
    }

    private void Solve(Arm arm, Vector3 pointerTarget, Vector3 filteredTarget, float tension, float dt, bool held = true)
    {
        Vector3 shoulder = arm.upper.position;
        Vector3 animatedElbow = arm.elbow.position;
        Vector3 animatedWrist = arm.wrist.position;
        float upperLength = Vector3.Distance(shoulder, animatedElbow);
        float lowerLength = Vector3.Distance(animatedElbow, animatedWrist);
        float fullLength = upperLength + lowerLength;
        if (!Finite(fullLength) || upperLength <= DirectionEpsilon || lowerLength <= DirectionEpsilon)
        {
            ResetTracking();
            return;
        }

        float maxReach = fullLength * Mathf.Lerp(.985f,.9995f,tension);
        if (maxReach <= Mathf.Abs(upperLength - lowerLength))
        {
            ResetTracking();
            return;
        }
        float minReach = Mathf.Min(maxReach, Mathf.Abs(upperLength - lowerLength) + fullLength * 0.01f);
        Vector3 offset = filteredTarget - shoulder;
        if (!Finite(offset.x) || !Finite(offset.y) || !Finite(offset.z))
        {
            ResetTracking();
            return;
        }

        // The wrist remains on the pointer's current depth plane. Tension makes
        // it respond faster; distance determines extension without stretching or
        // moving a nearby wrist away from the user's pointer merely to straighten.
        Vector3 direction = offset.sqrMagnitude > DirectionEpsilon * DirectionEpsilon
            ? offset.normalized : (animatedWrist - shoulder).normalized;
        if (direction.sqrMagnitude < 0.5f) direction = targetCamera.transform.right;
        float reach = Mathf.Clamp(offset.magnitude, minReach, maxReach);
        Vector3 target = shoulder + direction * reach;
        Vector3 pole = ResolvePole(arm, shoulder, animatedElbow, animatedWrist, direction, fullLength, dt, tension);

        // Law of cosines in the shoulder/target/pole plane. A small reach margin
        // keeps a defined bend even at maximum extension and when crossing over.
        float along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2 * reach);
        float height = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - along * along));
        Vector3 elbowTarget = shoulder + direction * along + pole * height;

        arm.upperBefore = arm.upper.localRotation;
        arm.elbowBefore = arm.elbow.localRotation;
        Swing(arm.upper, arm.elbow.position - shoulder, elbowTarget - shoulder);
        // Upper-arm rotation moves the entire twist-bone chain, so recalculate
        // the real elbow and wrist before applying the forearm's final swing.
        Swing(arm.elbow, arm.wrist.position - arm.elbow.position, target - arm.elbow.position);
        arm.upperAfter = arm.upper.localRotation;
        arm.elbowAfter = arm.elbow.localRotation;
        if (held) overriddenArm = arm;
        else overriddenFreeArm = arm;
        arm.direction = direction;
        arm.pole = pole;
        arm.tracking = true;

        if (held)
        {
            TargetError = Vector3.Distance(arm.wrist.position, pointerTarget);
            ElbowAngle = Vector3.Angle(arm.upper.position - arm.elbow.position, arm.wrist.position - arm.elbow.position);
        }
    }

    private Vector3 ResolvePole(Arm arm, Vector3 shoulder, Vector3 elbow, Vector3 wrist,
        Vector3 direction, float length, float dt, float tension)
    {
        Vector3 animatedDirection = (wrist - shoulder).normalized;
        Vector3 animatedPole = Vector3.ProjectOnPlane(elbow - shoulder, animatedDirection);
        Vector3 preferred;
        if (animatedPole.sqrMagnitude > length * length * 0.0004f && animatedDirection.sqrMagnitude > 0.5f)
            preferred = TransportPole(animatedPole.normalized, animatedDirection, direction);
        else
            preferred = Vector3.ProjectOnPlane(-targetCamera.transform.up - targetCamera.transform.forward * 0.4f, direction);

        if (preferred.sqrMagnitude < DirectionEpsilon)
            preferred = Vector3.ProjectOnPlane(targetCamera.transform.right, direction);
        if (preferred.sqrMagnitude < DirectionEpsilon)
            preferred = Vector3.Cross(direction, targetCamera.transform.up);
        preferred.Normalize();
        if (!arm.tracking) return preferred;

        Vector3 continuous = TransportPole(arm.pole, arm.direction, direction);
        // Animated hands may cross their shoulder axis. Preserve the previous
        // bend hemisphere instead of accepting a 180-degree pole flip.
        if (Vector3.Dot(continuous, preferred) < 0) preferred = -preferred;
        float follow = 1 - Mathf.Exp(-Mathf.Lerp(4, 1.5f, tension) * dt);
        return Vector3.Slerp(continuous, preferred, follow).normalized;
    }

    private static Vector3 TransportPole(Vector3 pole, Vector3 from, Vector3 to)
    {
        // The opposite-direction case has infinitely many swing axes. Using the
        // existing pole avoids the arbitrary axis chosen by FromToRotation.
        Quaternion swing = Vector3.Dot(from, to) < -0.999f
            ? Quaternion.AngleAxis(180, pole) : Quaternion.FromToRotation(from, to);
        Vector3 transported = Vector3.ProjectOnPlane(swing * pole, to);
        return transported.sqrMagnitude > DirectionEpsilon ? transported.normalized : pole;
    }

    private static void Swing(Transform bone, Vector3 currentWorld, Vector3 targetWorld)
    {
        if (currentWorld.sqrMagnitude <= DirectionEpsilon * DirectionEpsilon
            || targetWorld.sqrMagnitude <= DirectionEpsilon * DirectionEpsilon) return;
        if (bone.parent == null)
        {
            bone.rotation = Quaternion.FromToRotation(currentWorld, targetWorld) * bone.rotation;
            return;
        }
        // Solve from world-space directions in the actual parent's basis. This
        // also preserves a reflected FBX/PMX parent scale, which its world
        // quaternion alone cannot represent. No model-specific local axis is used.
        Vector3 current = bone.parent.InverseTransformVector(currentWorld);
        Vector3 target = bone.parent.InverseTransformVector(targetWorld);
        bone.localRotation = Quaternion.FromToRotation(current, target) * bone.localRotation;
    }

    private void RestorePreviousPose()
    {
        RestoreArm(overriddenArm);
        RestoreArm(overriddenFreeArm);
        overriddenArm = null;
        overriddenFreeArm = null;
        // Children first: each local snapshot represents the sampled animation,
        // before this frame's temporary parent rotation was added.
        headOffset?.Restore(); neckOffset?.Restore(); chestOffset?.Restore(); torsoOffset?.Restore();
    }

    private static void RestoreArm(Arm arm)
    {
        if (arm == null) return;
        // Respect an Animator or other owner that has already replaced our output.
        if (arm.upper != null && Quaternion.Angle(arm.upper.localRotation, arm.upperAfter) < 0.05f)
            arm.upper.localRotation = arm.upperBefore;
        if (arm.elbow != null && Quaternion.Angle(arm.elbow.localRotation, arm.elbowAfter) < 0.05f)
            arm.elbow.localRotation = arm.elbowBefore;
    }

    private void ResetTracking()
    {
        if (leftArm != null) leftArm.tracking = false;
        if (rightArm != null) rightArm.tracking = false;
        ActiveSide = string.Empty;
        TargetError = ElbowAngle = 0;
    }

    private void ResolveReferences()
    {
        nextResolveAt = Time.unscaledTime + 0.5f;
        if (owner == null) owner = GetComponent<PetCharacterAnimation>();
        if (targetCamera == null || !targetCamera.isActiveAndEnabled) targetCamera = Camera.main;
        if (owner == null) return;
        Transform model = owner.transform.Find("VisualModel");
        if (model == visualModel && leftArm != null && leftArm.Valid && rightArm != null && rightArm.Valid) return;
        RestorePreviousPose();
        owner.CancelFastHandReaction();
        visualModel = model;
        leftArm = rightArm = null;
        torsoOffset = chestOffset = neckOffset = headOffset = null;
        ResetTracking();
        if (model == null) return;
        var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (Transform bone in model.GetComponentsInChildren<Transform>(true))
        {
            if (InHiddenSource(bone, model)) continue;
            string key = PetRuntimePoseRetargeter.CanonicalName(bone.name);
            if (key != null && !bones.ContainsKey(key)) bones.Add(key, bone);
        }
        leftArm = FindArm(bones, "L");
        rightArm = FindArm(bones, "R");
        Transform Find(string key) => bones.TryGetValue(key, out Transform value) ? value : null;
        Transform torso = Find("上半身") ?? Find("上半身2") ?? Find("胸");
        Transform chest = Find("上半身2") ?? Find("胸");
        Transform neck = Find("首"), head = Find("頭");
        torsoOffset = torso != null ? new BoneOffset { bone = torso } : null;
        chestOffset = chest != null && chest != torso ? new BoneOffset { bone = chest } : null;
        neckOffset = neck != null && neck != torso && neck != chest ? new BoneOffset { bone = neck } : null;
        headOffset = head != null && head != neck && head != torso && head != chest ? new BoneOffset { bone = head } : null;
        ResetTracking();
    }

    private static Arm FindArm(Dictionary<string, Transform> bones, string side)
    {
        if (!bones.TryGetValue("腕." + side, out Transform upper)
            || !bones.TryGetValue("ひじ." + side, out Transform elbow)
            || !bones.TryGetValue("手首." + side, out Transform wrist)
            || !elbow.IsChildOf(upper) || !wrist.IsChildOf(elbow)) return null;
        return new Arm { upper = upper, elbow = elbow, wrist = wrist };
    }

    private static bool InHiddenSource(Transform bone, Transform model)
    {
        for (Transform current = bone; current != null && current != model; current = current.parent)
            if (current.name == "__BundledPoseSource") return true;
        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
