using System;
using System.Collections.Generic;
using UnityEngine;

public enum PetMouseGazeMode { Off = 0, Occasional = 1, Always = 2 }

/// <summary>Small additive eye/head turns, applied after animation and removed before
/// the next sample. It never writes facial shapes, mouth channels, or resting poses.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(80)]
public sealed class PetMouseGaze : MonoBehaviour
{
    public const string PreferenceKey = "desktop_pet.mouse_gaze_mode";
    public const float MaximumEyeYaw = 14f, MaximumEyePitch = 9f;
    public const float MaximumHeadYaw = 20f, MaximumHeadPitch = 11f;
    public static PetMouseGazeMode Mode => (PetMouseGazeMode)Mathf.Clamp(PlayerPrefs.GetInt(PreferenceKey, 1), 0, 2);
    public static event Action ModeChanged;
    private Animator animator;
    private PetCharacterAnimation character;
    private Camera view;
    private TransparentWindowController desktop;
    private BoneOffset head, leftEye, rightEye;
    private Vector2 eyeAngles, headAngles, eyeVelocity, headVelocity;
    private float attentionWeight, attentionVelocity, nextAttentionAt;
    private bool watching, modeInitialized;
    private PetMouseGazeMode lastMode;
    private System.Random random;
    public Transform HeadBone => head?.bone;
    public int EyeBoneCount => (leftEye?.bone != null ? 1 : 0) + (rightEye?.bone != null ? 1 : 0);
    public Vector2 EyeAngles => eyeAngles;
    public Vector2 HeadAngles => headAngles;
    public float AttentionWeight => attentionWeight;

    private sealed class BoneOffset
    {
        public Transform bone;
        public Quaternion before, after;
        public bool applied;
        public void Remove()
        {
            if (!applied) return;
            // A clip or another explicit pose assignment may already have replaced
            // this joint. Only undo an offset which is still ours.
            if (bone != null && SameRotation(bone.localRotation, after)) bone.localRotation = before;
            applied = false;
        }
        public void Apply(Quaternion delta)
        {
            if (bone == null) return;
            before = bone.localRotation;
            bone.rotation = delta * bone.rotation;
            after = bone.localRotation;
            applied = true;
        }
        private static bool SameRotation(Quaternion a, Quaternion b)
        {
            float difference = (a.x-b.x)*(a.x-b.x) + (a.y-b.y)*(a.y-b.y) + (a.z-b.z)*(a.z-b.z) + (a.w-b.w)*(a.w-b.w);
            float opposite = (a.x+b.x)*(a.x+b.x) + (a.y+b.y)*(a.y+b.y) + (a.z+b.z)*(a.z+b.z) + (a.w+b.w)*(a.w+b.w);
            return Mathf.Min(difference, opposite) < 0.0000000001f;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetEvents() { ModeChanged = null; }

    public static void SetMode(PetMouseGazeMode value)
    {
        PlayerPrefs.SetInt(PreferenceKey, Mathf.Clamp((int)value, 0, 2));
        PlayerPrefs.Save();
        ModeChanged?.Invoke();
    }

    public static PetMouseGaze InstallOn(GameObject model)
    {
        if (model == null || model.GetComponent<PetCharacterAnimation>() == null) return null;
        return model.GetComponent<PetMouseGaze>() ?? model.AddComponent<PetMouseGaze>();
    }

    private void Awake() { BindRig(GetComponent<Animator>()); }
    private void OnDisable() { ResetOffsets(); }
    private void OnDestroy() { RemoveOffsets(); }

    public void BindRig(Animator target, Camera camera = null)
    {
        ResetOffsets();
        animator = target;
        character = GetComponent<PetCharacterAnimation>();
        view = camera != null ? camera : Camera.main;
        desktop = FindFirstObjectByType<TransparentWindowController>();
        random = new System.Random(unchecked(gameObject.GetEntityId().GetHashCode() * 397));
        Transform headBone = null, left = null, right = null;
        if (animator != null && animator.avatar != null && animator.isHuman)
        {
            headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            left = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            right = animator.GetBoneTransform(HumanBodyBones.RightEye);
        }
        var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var joint in transform.GetComponentsInChildren<Transform>(true))
        {
            string key = Normalize(joint.name);
            if (!bones.ContainsKey(key)) bones[key] = joint;
        }
        Transform Find(params string[] names)
        {
            foreach (string name in names) if (bones.TryGetValue(Normalize(name), out var result)) return result;
            return null;
        }
        headBone ??= Find("頭", "Head", "J_Bip_C_Head", "Bip001 Head", "Bip01 Head", "頭部");
        left ??= Find("目.L", "左目", "eye.L", "LeftEye", "EyeLeft", "J_Adj_L_FaceEye", "Bip001 L Eye");
        right ??= Find("目.R", "右目", "eye.R", "RightEye", "EyeRight", "J_Adj_R_FaceEye", "Bip001 R Eye");
        // Exact aliases intentionally exclude eyelids, eye corners and eye-end joints.
        head = headBone != null ? new BoneOffset { bone = headBone } : null;
        leftEye = left != null && left != headBone ? new BoneOffset { bone = left } : null;
        rightEye = right != null && right != headBone && right != left ? new BoneOffset { bone = right } : null;
    }

    private static string Normalize(string name)
    {
        int colon = name.LastIndexOf(':');
        if (colon >= 0) name = name.Substring(colon + 1);
        return name.Normalize(System.Text.NormalizationForm.FormKC).Replace("_", "").Replace(".", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
    }

    private void Update() { RemoveOffsets(); }

    private void LateUpdate()
    {
        if (view == null) view = Camera.main;
        Vector2 pointer = default;
        bool found = view != null && desktop != null && desktop.TryGetGazePointer(out pointer);
        if (desktop == null) found = view != null && TransparentWindowController.TryGetLocalGazePointer(out pointer);
        Simulate(pointer, Time.unscaledDeltaTime, Time.unscaledTime, found, AllowsAnimatedPose(), Mode);
    }

    private bool AllowsAnimatedPose()
    {
        if (character != null && (character.IsSleeping || character.IsDragging || character.IsGuarding || character.IsWalking || character.IsHandLeading)) return false;
        if (animator == null || !animator.enabled || animator.runtimeAnimatorController == null) return false;
        if (animator.IsInTransition(0)) return false;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        return state.IsName("Base Layer.Idle") || state.IsName("Idle") || state.IsName("Base Layer.Speak") || state.IsName("Speak");
    }

    private void Simulate(Vector2 pointer, float delta, float now, bool hasPointer, bool poseAllowed, PetMouseGazeMode mode)
    {
        RemoveOffsets();
        if (head?.bone == null || view == null || !poseAllowed || delta > 0.3f || delta <= 0)
        { ResetAngles(); return; }
        delta = Mathf.Min(delta, 0.1f);
        if (!modeInitialized || mode != lastMode)
        {
            modeInitialized = true; lastMode = mode;
            watching = mode == PetMouseGazeMode.Always;
            nextAttentionAt = now + Range(1.2f, 2.8f);
        }
        if (mode == PetMouseGazeMode.Occasional && now >= nextAttentionAt)
        {
            watching = !watching;
            nextAttentionAt = now + (watching ? Range(2.4f, 4.2f) : Range(3.5f, 7f));
        }
        bool engaged = hasPointer && mode != PetMouseGazeMode.Off && (mode == PetMouseGazeMode.Always || watching);
        attentionWeight = Mathf.SmoothDamp(attentionWeight, engaged ? 1 : 0, ref attentionVelocity,
            engaged ? 0.22f : 0.3f, 3f, delta);
        Vector3 headScreen = view.WorldToScreenPoint(head.bone.position);
        Vector2 target = Vector2.zero;
        if (engaged && headScreen.z > 0 && Finite(pointer.x) && Finite(pointer.y))
        {
            float distance = Mathf.Max(180, view.pixelHeight * 0.45f);
            target = new Vector2(Mathf.Atan((pointer.x - headScreen.x) / distance) * Mathf.Rad2Deg,
                                 Mathf.Atan((pointer.y - headScreen.y) / distance) * Mathf.Rad2Deg);
            target.x = Mathf.Clamp(target.x, -MaximumEyeYaw - MaximumHeadYaw, MaximumEyeYaw + MaximumHeadYaw);
            target.y = Mathf.Clamp(target.y, -MaximumEyePitch - MaximumHeadPitch, MaximumEyePitch + MaximumHeadPitch);
        }
        target *= attentionWeight;
        bool hasEyes = EyeBoneCount > 0;
        Vector2 headTarget = new Vector2(
            HeadShare(target.x, hasEyes ? 7f : 0f, MaximumEyeYaw, MaximumHeadYaw),
            HeadShare(target.y, hasEyes ? 5f : 0f, MaximumEyePitch, MaximumHeadPitch));
        headAngles = Smooth(headAngles, headTarget, ref headVelocity, 0.42f, 30, delta);
        Vector2 eyeTarget = hasEyes ? new Vector2(Mathf.Clamp(target.x - headAngles.x, -MaximumEyeYaw, MaximumEyeYaw),
            Mathf.Clamp(target.y - headAngles.y, -MaximumEyePitch, MaximumEyePitch)) : Vector2.zero;
        eyeAngles = Smooth(eyeAngles, eyeTarget, ref eyeVelocity, 0.11f, 110, delta);
        if (!engaged && headAngles.sqrMagnitude + eyeAngles.sqrMagnitude < 0.00001f)
        { ResetAngles(); return; }
        Quaternion Rotation(Vector2 angles) => Quaternion.AngleAxis(-angles.x, view.transform.up) * Quaternion.AngleAxis(angles.y, view.transform.right);
        head.Apply(Rotation(headAngles));
        leftEye?.Apply(Rotation(eyeAngles));
        rightEye?.Apply(Rotation(eyeAngles));
    }

    private float Range(float min, float max) => min + (max - min) * (float)(random?.NextDouble() ?? 0.5);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float HeadShare(float angle, float eyeLead, float eyeMaximum, float maximum)
    {
        // Nearby offsets stay in the eyes first. Larger offsets recruit the head
        // progressively, then ease into its limit without a sudden turn.
        float amplitude = Mathf.Clamp01((Mathf.Abs(angle) - eyeLead) / (eyeMaximum + maximum - eyeLead));
        float eased = amplitude * amplitude * (3f - 2f * amplitude);
        return Mathf.Sign(angle) * maximum * eased;
    }
    private static Vector2 Smooth(Vector2 current, Vector2 target, ref Vector2 velocity, float smoothTime, float speed, float delta)
    {
        Vector2 smoothed = Vector2.SmoothDamp(current, target, ref velocity, smoothTime, speed, delta);
        return Vector2.MoveTowards(current, smoothed, speed * delta);
    }
    private void RemoveOffsets() { leftEye?.Remove(); rightEye?.Remove(); head?.Remove(); }
    private void ResetAngles() { eyeAngles = headAngles = eyeVelocity = headVelocity = Vector2.zero; attentionWeight = attentionVelocity = 0; }
    public void ResetOffsets() { RemoveOffsets(); ResetAngles(); modeInitialized = false; }
}
