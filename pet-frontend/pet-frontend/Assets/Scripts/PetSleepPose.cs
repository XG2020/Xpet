using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Shared, deterministic sleep poses for existing animated skeletons.
/// Capture a settled Idle pose, then sample after Animator/retargeting. The same
/// sampler can be used by an Editor clip baker without rebuilding model meshes.</summary>
public sealed class PetSleepPose
{
    public const float EnterDuration = 2.8f;
    public const float LoopDuration = 4.5f;
    public const float WakeDuration = 4.5f;

    private sealed class Joint
    {
        public Transform bone;
        public Quaternion rotation;
        public Vector3 position;
    }

    private sealed class Leg
    {
        public Transform upper, knee, ankle;
        public Vector3 ankleInRoot;
        public Quaternion ankleRotationInRoot;
    }

    private sealed class Arm
    {
        public Transform upper, elbow, wrist;
        public Vector3 upperInRoot, forearmInRoot;
        public float side;
    }

    private sealed class Finger
    {
        public Transform bone;
        public Quaternion restRotation;
        public Vector3 curlAxis;
        public float additionalCurl;
        public bool thumb;
    }

    private sealed class Hand
    {
        public string side;
        public Transform wrist, elbow;
        public Vector3 fingerAxis, palmAxis;
        public Transform[] twists;
        public Quaternion[] twistBefore;
    }

    private readonly Transform root;
    private readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
    private readonly List<Leg> legs = new List<Leg>();
    private readonly List<Arm> arms = new List<Arm>();
    private readonly List<Finger> fingers = new List<Finger>();
    private readonly List<Hand> hands = new List<Hand>();
    private int fistHands;
    private Vector3 viewerDirection;
    private bool viewerDirectionAssigned;
    private readonly Joint[] joints;
    private readonly Transform[] drivenBones;
    private readonly Quaternion[] incomingRotations;
    private readonly Vector3[] incomingPositions;
    private readonly Quaternion[] wakeStartRotations, fullSleepRotations;
    private readonly Vector3[] wakeStartPositions, fullSleepPositions;
    private bool wakePrepared;
    private readonly Transform center, torso, chest, neck, head;
    private readonly float bodyHeight;
    private readonly bool plantedLegs;
    private static readonly Vector3 Forward = Vector3.back;

    public bool IsValid => head != null && torso != null;
    public bool HasPlantedLegs => plantedLegs;
    public IReadOnlyList<Transform> DrivenBones => drivenBones;
    public int FingerJointCount => fingers.Count;
    public int FistHandCount => fistHands;
    public int PalmBindingCount => hands.Count;

    /// <summary>World direction from the character toward the viewer, such as
    /// -Camera.main.transform.forward. The default follows the actor's facing basis.</summary>
    public void SetViewerDirection(Vector3 worldDirection)
    {
        if (!Finite(worldDirection.x) || !Finite(worldDirection.y) || !Finite(worldDirection.z) || worldDirection.sqrMagnitude < 0.000001f) return;
        viewerDirection = worldDirection.normalized; viewerDirectionAssigned = true;
    }

    public float PalmViewerDot(string side)
    {
        foreach (Hand hand in hands) if (hand.side == side && hand.wrist != null)
            return Vector3.Dot(hand.wrist.TransformDirection(hand.palmAxis).normalized, ViewerDirection);
        return -1;
    }

    private Vector3 ViewerDirection => viewerDirectionAssigned ? viewerDirection : root.TransformDirection(Forward).normalized;

    /// <summary>Record the visible skeleton only, excluding the hidden retarget source.
    /// Call once per sleep entry after Idle has settled; Capture does not sample or
    /// alter the Animator, face shapes, root transform, hair, clothes, or model assets.</summary>
    public static PetSleepPose Capture(Transform characterRoot)
    {
        return characterRoot != null ? new PetSleepPose(characterRoot) : null;
    }

    private PetSleepPose(Transform characterRoot)
    {
        root = characterRoot;
        Transform visual = root.Find("VisualModel") ?? root;
        foreach (Transform bone in visual.GetComponentsInChildren<Transform>(true))
        {
            if (IsPoseSource(bone)) continue;
            string key = PetRuntimePoseRetargeter.CanonicalName(bone.name);
            if (key != null && !bones.ContainsKey(key)) bones.Add(key, bone);
        }
        // A valid Humanoid Avatar supplies exact limb bindings even when its
        // transform names use another DCC's naming convention.
        Animator animator = root.GetComponent<Animator>() ?? visual.GetComponent<Animator>();
        if (animator != null && animator.isHuman)
        {
            AddHuman(animator, "センター", HumanBodyBones.Hips);
            AddHuman(animator, "下半身", HumanBodyBones.Hips);
            AddHuman(animator, "上半身", HumanBodyBones.Spine);
            AddHuman(animator, "上半身2", HumanBodyBones.Chest);
            AddHuman(animator, "胸", HumanBodyBones.UpperChest);
            AddHuman(animator, "首", HumanBodyBones.Neck);
            AddHuman(animator, "頭", HumanBodyBones.Head);
            AddHuman(animator, "腕.L", HumanBodyBones.LeftUpperArm);
            AddHuman(animator, "ひじ.L", HumanBodyBones.LeftLowerArm);
            AddHuman(animator, "手首.L", HumanBodyBones.LeftHand);
            AddHuman(animator, "腕.R", HumanBodyBones.RightUpperArm);
            AddHuman(animator, "ひじ.R", HumanBodyBones.RightLowerArm);
            AddHuman(animator, "手首.R", HumanBodyBones.RightHand);
            AddHuman(animator, "足.L", HumanBodyBones.LeftUpperLeg);
            AddHuman(animator, "ひざ.L", HumanBodyBones.LeftLowerLeg);
            AddHuman(animator, "足首.L", HumanBodyBones.LeftFoot);
            AddHuman(animator, "足.R", HumanBodyBones.RightUpperLeg);
            AddHuman(animator, "ひざ.R", HumanBodyBones.RightLowerLeg);
            AddHuman(animator, "足首.R", HumanBodyBones.RightFoot);
            AddHumanFingers(animator, "L", new[] {
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
                HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal });
            AddHumanFingers(animator, "R", new[] {
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
                HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal });
        }
        center = Find("センター") ?? Find("下半身");
        torso = Find("上半身") ?? Find("上半身2") ?? Find("胸");
        chest = Find("上半身2") ?? Find("胸");
        if (chest == torso) chest = null;
        neck = Find("首");
        head = Find("頭");
        var selected = new List<Transform>();
        Add(selected, center); Add(selected, Find("腰")); Add(selected, Find("下半身"));
        Add(selected, torso); Add(selected, chest); Add(selected, neck); Add(selected, head);
        bool left = false, right = false;
        foreach (string side in new[] { "L", "R" })
        {
            foreach (string suffix in new[] { "", "D" })
            {
                Transform upper = Find("足" + suffix + "." + side), knee = Find("ひざ" + suffix + "." + side), ankle = Find("足首" + suffix + "." + side);
                if (upper == null || knee == null || ankle == null) continue;
                legs.Add(new Leg { upper = upper, knee = knee, ankle = ankle,
                    ankleInRoot = root.InverseTransformPoint(ankle.position),
                    ankleRotationInRoot = Quaternion.Inverse(root.rotation) * ankle.rotation });
                Add(selected, upper); Add(selected, knee); Add(selected, ankle);
                if (suffix.Length == 0) { if (side == "L") left = true; else right = true; }
            }
            Transform arm = Find("腕." + side), elbow = Find("ひじ." + side), wrist = Find("手首." + side);
            if (arm == null || elbow == null || wrist == null) continue;
            float sign = Mathf.Sign(root.InverseTransformPoint(arm.position).x);
            arms.Add(new Arm { upper = arm, elbow = elbow, wrist = wrist, side = sign == 0 ? (side == "L" ? -1 : 1) : sign,
                upperInRoot = root.InverseTransformDirection(elbow.position - arm.position).normalized,
                forearmInRoot = root.InverseTransformDirection(wrist.position - elbow.position).normalized });
            Add(selected, arm); Add(selected, elbow); Add(selected, wrist);
            BindFingers(side, wrist, selected);
        }
        plantedLegs = center != null && left && right;
        Transform foot = Find("足首.L") ?? Find("足首.R");
        bodyHeight = head != null && foot != null
            ? Mathf.Max(0.1f, Vector3.Distance(root.InverseTransformPoint(head.position), root.InverseTransformPoint(foot.position))) : 1.5f;
        drivenBones = selected.ToArray();
        joints = new Joint[drivenBones.Length];
        incomingRotations = new Quaternion[joints.Length];
        incomingPositions = new Vector3[joints.Length];
        wakeStartRotations = new Quaternion[joints.Length]; fullSleepRotations = new Quaternion[joints.Length];
        wakeStartPositions = new Vector3[joints.Length]; fullSleepPositions = new Vector3[joints.Length];
        for (int i = 0; i < joints.Length; i++) joints[i] = new Joint { bone = drivenBones[i], rotation = drivenBones[i].localRotation, position = drivenBones[i].localPosition };
    }

    public static bool IsSleepState(string state) => state == "SleepEnter" || state == "SleepLoop" || state == "WakeUp";
    public static float Duration(string state) => state == "SleepEnter" ? EnterDuration : state == "SleepLoop" ? LoopDuration : state == "WakeUp" ? WakeDuration : 0;

    /// <summary>Normalized eyelid closure for the face mixer; never writes morphs.</summary>
    public static float ClosedEyeWeight(string state, float normalizedTime)
    {
        float t = Finite(normalizedTime) ? Mathf.Clamp01(normalizedTime) : 0;
        return state == "SleepEnter" ? Smooth(t / 0.72f) : state == "SleepLoop" ? 1 : state == "WakeUp" ? 1 - Smooth((t - 0.26f) / 0.56f) : 0;
    }

    /// <summary>Slow waking stretch with a held peak, shared with face presentation.</summary>
    public static float StretchWeight(string state, float normalizedTime)
    {
        return state == "WakeUp" && Finite(normalizedTime) ? HoldWindow(Mathf.Clamp01(normalizedTime), 0.16f, 0.52f, 0.66f, 0.98f) : 0;
    }

    /// <summary>Normalized fist closure; only the waking stretch closes the hands.</summary>
    public static float FistWeight(string state, float normalizedTime) => StretchWeight(state, normalizedTime);

    /// <summary>Optional normalized yawn amplitude; the face mixer owns its mouth shape.</summary>
    public static float YawnWeight(string state, float normalizedTime)
    {
        return state == "WakeUp" && Finite(normalizedTime) ? HoldWindow(Mathf.Clamp01(normalizedTime), 0.24f, 0.43f, 0.60f, 0.82f) : 0;
    }

    /// <summary>Use the currently visible pose as WakeUp's first frame, including
    /// a partially completed SleepEnter or any phase of the breathing loop.
    /// Sampling the reference pose is temporary and restores every touched joint.
    /// The caller separately retains its current eyelid weight for the face fade.</summary>
    public void PrepareWake()
    {
        if (!IsValid) return;
        for (int i = 0; i < joints.Length; i++) if (joints[i].bone != null)
        {
            wakeStartRotations[i] = joints[i].bone.localRotation;
            wakeStartPositions[i] = joints[i].bone.localPosition;
        }
        Apply("SleepLoop", 0);
        for (int i = 0; i < joints.Length; i++) if (joints[i].bone != null)
        {
            fullSleepRotations[i] = joints[i].bone.localRotation;
            fullSleepPositions[i] = joints[i].bone.localPosition;
            joints[i].bone.localRotation = wakeStartRotations[i];
            joints[i].bone.localPosition = wakeStartPositions[i];
        }
        wakePrepared = true;
    }

    /// <summary>Sample a pose in normalized clip time. SleepLoop repeats; its seam
    /// matches SleepEnter's end and WakeUp's start. blend mixes with the incoming
    /// sampled pose, so callers can fade from a current animation without jumps.
    /// Call Restore before normal animation resumes on rigs with unkeyed joints.</summary>
    public void Apply(string state, float normalizedTime, float blend = 1)
    {
        if (!IsValid || !IsSleepState(state)) return;
        float t = Finite(normalizedTime) ? (state == "SleepLoop" ? Mathf.Repeat(normalizedTime, 1) : Mathf.Clamp01(normalizedTime)) : 0;
        blend = Finite(blend) ? Mathf.Clamp01(blend) : 0;
        if (blend <= 0) return;
        if (state != "WakeUp") wakePrepared = false;
        for (int i = 0; i < joints.Length; i++) if (joints[i].bone != null)
        {
            incomingRotations[i] = joints[i].bone.localRotation;
            incomingPositions[i] = joints[i].bone.localPosition;
        }
        Restore();
        float sleep = state == "SleepEnter" ? Smooth(t) : state == "SleepLoop" ? 1 : 1 - Smooth((t - 0.035f) / 0.785f);
        float breath = state == "SleepLoop" ? 1 - Mathf.Cos(t * Mathf.PI * 2) : 0;
        float stretch = StretchWeight(state, t);
        float fist = FistWeight(state, t);
        Vector3 pitchAxis = root.TransformDirection(Vector3.Cross(Vector3.up, Forward));
        if (plantedLegs)
            center.position += root.TransformVector((Vector3.down * (0.095f * sleep + 0.0018f * breath) - Forward * (0.026f * sleep)) * bodyHeight);
        Rotate(torso, pitchAxis, 5.5f * sleep + 0.45f * breath - 3f * stretch);
        Rotate(chest, pitchAxis, 4f * sleep + 0.45f * breath - 5f * stretch);
        Rotate(neck, pitchAxis, 4f * sleep - 2f * stretch);
        Rotate(head, pitchAxis, 16f * sleep + 0.9f * breath - 8f * stretch);
        Rotate(head, root.TransformDirection(Forward), -5f * sleep);
        foreach (Arm arm in arms)
        {
            // Hands stay below the chest while resting. Waking opens the elbows
            // slowly, keeps them wide and softly bent through a held stretch,
            // then lowers them; the small lag avoids a synchronized cheering V.
            Vector3 upper = Vector3.Slerp(arm.upperInRoot, new Vector3(arm.side * 0.14f, -1, -0.16f).normalized, sleep);
            Vector3 forearm = Vector3.Slerp(arm.forearmInRoot, new Vector3(arm.side * 0.045f, -1, -0.32f).normalized, sleep);
            float armStretch = state == "WakeUp" ? HoldWindow(t, arm.side < 0 ? 0.16f : 0.195f, arm.side < 0 ? 0.52f : 0.555f, 0.69f, 0.98f) : 0;
            upper = Vector3.Slerp(upper, new Vector3(arm.side * 0.94f, 0.28f, 0.11f).normalized, armStretch);
            forearm = Vector3.Slerp(forearm, new Vector3(arm.side * 0.20f, 0.92f, 0.16f).normalized, armStretch);
            Aim(arm.upper, arm.elbow, root.TransformDirection(upper));
            Aim(arm.elbow, arm.wrist, root.TransformDirection(forearm));
        }
        foreach (Finger finger in fingers) if (finger.bone != null)
        {
            // Four fingers close first. The thumb follows gently across the
            // outside knuckles rather than being tucked beneath the fingers.
            float grip = finger.thumb ? Smooth((fist - 0.12f) / 0.88f) : fist;
            finger.bone.localRotation = finger.restRotation * Quaternion.AngleAxis(finger.additionalCurl * grip, finger.curlAxis);
        }
        if (plantedLegs && (sleep > 0 || breath > 0)) foreach (Leg leg in legs)
        {
            SolveLeg(leg.upper, leg.knee, leg.ankle, root.TransformPoint(leg.ankleInRoot), root.TransformDirection(Forward));
            leg.ankle.rotation = root.rotation * leg.ankleRotationInRoot;
        }
        if (state == "WakeUp" && wakePrepared)
        {
            // Remove only the part of the complete sleeping pose that was not
            // present at the interruption. The correction vanishes together
            // with the sleep weight, leaving the authored waking stretch intact.
            for (int i = 0; i < joints.Length; i++) if (joints[i].bone != null)
            {
                Quaternion difference = wakeStartRotations[i] * Quaternion.Inverse(fullSleepRotations[i]);
                joints[i].bone.localRotation = Quaternion.Slerp(Quaternion.identity, difference, sleep) * joints[i].bone.localRotation;
                joints[i].bone.localPosition += (wakeStartPositions[i] - fullSleepPositions[i]) * sleep;
            }
            // The exact first frame retains the captured knee pose. Thereafter
            // keep the shoe anchors planted through the corrected body motion.
            if (plantedLegs && t > 0 && sleep > 0) foreach (Leg leg in legs)
            {
                SolveLeg(leg.upper, leg.knee, leg.ankle, root.TransformPoint(leg.ankleInRoot), root.TransformDirection(Forward));
                leg.ankle.rotation = root.rotation * leg.ankleRotationInRoot;
            }
        }
        if (stretch > 0) foreach (Hand hand in hands) OrientWakingHand(hand, stretch);
        if (blend < 1) for (int i = 0; i < joints.Length; i++) if (joints[i].bone != null)
        {
            joints[i].bone.localRotation = Quaternion.Slerp(incomingRotations[i], joints[i].bone.localRotation, blend);
            joints[i].bone.localPosition = Vector3.Lerp(incomingPositions[i], joints[i].bone.localPosition, blend);
        }
    }

    public void Restore()
    {
        foreach (Joint joint in joints) if (joint.bone != null)
        {
            joint.bone.localRotation = joint.rotation;
            joint.bone.localPosition = joint.position;
        }
    }

    private Transform Find(string key) => bones.TryGetValue(key, out Transform value) ? value : null;
    private void AddHuman(Animator animator, string key, HumanBodyBones humanBone)
    {
        if (bones.ContainsKey(key)) return;
        Transform bone = animator.GetBoneTransform(humanBone);
        if (bone != null) bones.Add(key, bone);
    }
    private void AddHumanFingers(Animator animator, string side, HumanBodyBones[] humanBones)
    {
        string[] digits = { "人指", "中指", "薬指", "小指", "親指" };
        for (int digit = 0; digit < digits.Length; digit++)
            for (int joint = 0; joint < 3; joint++)
                AddHuman(animator, digits[digit] + (char)('０' + joint + (digit == 4 ? 0 : 1)) + "." + side, humanBones[digit * 3 + joint]);
    }
    private void BindFingers(string side, Transform wrist, List<Transform> selected)
    {
        Transform middle = Find("中指１." + side), index = Find("人指１." + side), pinky = Find("小指１." + side);
        if (middle == null || index == null || pinky == null) return;
        Vector3 forward = (middle.position - wrist.position).normalized;
        Vector3 palm = Vector3.Cross(forward, index.position - pinky.position).normalized * (side == "R" ? 1 : -1);
        if (palm.sqrMagnitude < 0.5f) return;
        var twists = new List<Transform>();
        foreach (string name in new[] { "手捩1", "手捩2", "手捩3", "手捩", "ForearmTwist1", "ForearmTwist2", "ForearmTwist3", "ForearmTwist", "LowerArmTwist" })
        {
            Transform twist = Find(name + "." + side);
            if (twist == null || twists.Contains(twist)) continue;
            twists.Add(twist); Add(selected, twist);
        }
        // Parent twists are applied before their children, using saved world
        // rotations so hierarchical twist bones do not multiply each other.
        twists.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
        hands.Add(new Hand { side = side, wrist = wrist, elbow = Find("ひじ." + side),
            fingerAxis = wrist.InverseTransformDirection(forward), palmAxis = wrist.InverseTransformDirection(palm),
            twists = twists.ToArray(), twistBefore = new Quaternion[twists.Count] });
        string[] digits = { "人指", "中指", "薬指", "小指", "親指" };
        int before = fingers.Count;
        for (int digit = 0; digit < digits.Length; digit++)
        {
            bool thumb = digit == 4;
            Transform proximal = Find(digits[digit] + (thumb ? "０." : "１.") + side);
            for (int joint = 0; joint < 3; joint++)
            {
                string key = digits[digit] + (char)('０' + joint + (thumb ? 0 : 1)) + "." + side;
                Transform bone = Find(key);
                if (bone == null) continue;
                Transform child = joint < 2 ? Find(digits[digit] + (char)('０' + joint + (thumb ? 1 : 2)) + "." + side) : Find(key + "_end");
                if (child == null && bone.childCount > 0) child = bone.GetChild(0);
                Vector3 direction = child != null ? child.position - bone.position : bone.parent != null ? bone.position - bone.parent.position : forward;
                if (direction.sqrMagnitude < 0.000001f) continue;
                // Derive the flexion axis from each real segment and palm plane.
                // The thumb's target stays on the dorsal side of the index knuckle.
                float palmWidth = Vector3.Distance(index.position, pinky.position);
                Vector3 knuckleOutside = index.position - palm * palmWidth * 0.16f;
                Vector3 towardPalm = thumb ? (knuckleOutside - bone.position).normalized - palm * 0.20f : palm;
                Vector3 axis = Vector3.Cross(direction.normalized, towardPalm).normalized;
                if (axis.sqrMagnitude < 0.5f) continue;
                Vector3 reference = joint == 0 && proximal != null ? proximal.position - wrist.position
                    : bone.parent != null ? bone.position - bone.parent.position : direction;
                reference = Vector3.ProjectOnPlane(reference, axis).normalized;
                Vector3 projected = Vector3.ProjectOnPlane(direction, axis).normalized;
                float neutralCurl = reference.sqrMagnitude > 0.5f && projected.sqrMagnitude > 0.5f
                    ? Mathf.Clamp(Vector3.SignedAngle(reference, projected, axis), 0, 90) : 0;
                // MCP/PIP/DIP targets are deliberately below a tight grip. A
                // captured relaxed curl is subtracted rather than applied twice.
                float target = thumb ? (joint == 0 ? 18 : joint == 1 ? 24 : 16)
                    : (joint == 0 ? 58 : joint == 1 ? 78 : 46) + (digit - 1.5f) * 2;
                float maximumIncrement = joint == 0 ? 55 : joint == 1 ? 68 : 42;
                float curl = thumb ? Mathf.Min(target, 28) : Mathf.Clamp(target - neutralCurl, 0, maximumIncrement);
                fingers.Add(new Finger { bone = bone, restRotation = bone.localRotation,
                    curlAxis = bone.InverseTransformDirection(axis), additionalCurl = curl, thumb = thumb });
                Add(selected, bone);
            }
        }
        if (fingers.Count > before) fistHands++;
    }
    private void OrientWakingHand(Hand hand, float weight)
    {
        if (hand.wrist == null || hand.elbow == null) return;
        Vector3 fingers = hand.wrist.TransformDirection(hand.fingerAxis).normalized;
        Vector3 palm = hand.wrist.TransformDirection(hand.palmAxis).normalized;
        Vector3 viewer = ViewerDirection;
        Vector3 upright = Vector3.ProjectOnPlane(root.up, viewer).normalized;
        if (upright.sqrMagnitude < 0.5f) upright = Vector3.ProjectOnPlane(root.forward, viewer).normalized;
        if (upright.sqrMagnitude < 0.5f) return;
        // Swing toward upright first, then pronate around the transported finger
        // direction. This avoids using a wrist-local Euler axis or flipping an
        // opposing palm with a single shortest-path 180-degree wrist rotation.
        Quaternion swing = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(fingers, upright), weight);
        fingers = (swing * fingers).normalized;
        palm = Vector3.ProjectOnPlane(swing * palm, fingers).normalized;
        Vector3 targetPalm = Vector3.ProjectOnPlane(viewer, fingers).normalized;
        if (palm.sqrMagnitude < 0.5f || targetPalm.sqrMagnitude < 0.5f) return;
        float roll = Vector3.SignedAngle(palm, targetPalm, fingers);
        palm = Quaternion.AngleAxis(roll * weight, fingers) * palm;
        Quaternion desired = Quaternion.LookRotation(fingers, palm)
            * Quaternion.Inverse(Quaternion.LookRotation(hand.fingerAxis, hand.palmAxis));
        Vector3 forearm = (hand.wrist.position - hand.elbow.position).normalized;
        Vector3 currentPalm = Vector3.ProjectOnPlane(hand.wrist.TransformDirection(hand.palmAxis), forearm).normalized;
        Vector3 rolledPalm = Vector3.ProjectOnPlane(palm, forearm).normalized;
        float twistAngle = Vector3.SignedAngle(currentPalm, rolledPalm, forearm);
        float elbowRoll = Mathf.Clamp(twistAngle * 0.65f, -85, 85);
        for (int i = 0; i < hand.twists.Length; i++) if (hand.twists[i] != null) hand.twistBefore[i] = hand.twists[i].rotation;
        // Elbow rotation around its own forearm axis preserves the wrist target.
        // Existing deformation twists carry the remaining gradient toward the hand.
        hand.elbow.rotation = Quaternion.AngleAxis(elbowRoll, forearm) * hand.elbow.rotation;
        for (int i = 0; i < hand.twists.Length; i++) if (hand.twists[i] != null)
        {
            float fraction = (i + 1f) / (hand.twists.Length + 1f);
            float angle = Mathf.Lerp(elbowRoll, twistAngle, fraction);
            hand.twists[i].rotation = Quaternion.AngleAxis(angle, forearm) * hand.twistBefore[i];
        }
        hand.wrist.rotation = desired;
    }
    private static int Depth(Transform bone) { int depth = 0; for (Transform at = bone; at != null; at = at.parent) depth++; return depth; }
    private static void Add(List<Transform> selected, Transform bone) { if (bone != null && !selected.Contains(bone)) selected.Add(bone); }
    private bool IsPoseSource(Transform bone)
    {
        for (Transform at = bone; at != null && at != root; at = at.parent) if (at.name == "__BundledPoseSource") return true;
        return false;
    }
    private static void Rotate(Transform bone, Vector3 axis, float degrees) { if (bone != null) bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation; }
    private static void Aim(Transform bone, Transform child, Vector3 direction)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude > 0.000001f && direction.sqrMagnitude > 0.000001f) bone.rotation = Quaternion.FromToRotation(current, direction) * bone.rotation;
    }
    private static void SolveLeg(Transform upper, Transform knee, Transform ankle, Vector3 target, Vector3 bendDirection)
    {
        float a = Vector3.Distance(upper.position, knee.position), b = Vector3.Distance(knee.position, ankle.position);
        Vector3 reach = target - upper.position;
        if (a < 0.0001f || b < 0.0001f || reach.sqrMagnitude < 0.000001f) return;
        float distance = Mathf.Clamp(reach.magnitude, Mathf.Abs(a - b) + 0.0001f, a + b - 0.0001f);
        Vector3 direction = reach.normalized, bend = Vector3.ProjectOnPlane(bendDirection, direction).normalized;
        if (bend.sqrMagnitude < 0.001f) return;
        float along = (a * a - b * b + distance * distance) / (2 * distance);
        Vector3 kneeTarget = upper.position + direction * along + bend * Mathf.Sqrt(Mathf.Max(0, a * a - along * along));
        Aim(upper, knee, kneeTarget - upper.position);
        Aim(knee, ankle, target - knee.position);
    }
    private static float Smooth(float value) { float t = Mathf.Clamp01(value); return t * t * t * (t * (t * 6 - 15) + 10); }
    private static float HoldWindow(float t, float enterStart, float enterEnd, float exitStart, float exitEnd)
        => Smooth((t - enterStart) / (enterEnd - enterStart)) * (1 - Smooth((t - exitStart) / (exitEnd - exitStart)));
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
