using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Shows verified, separately skinned weapons only during a held-item gesture.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)]
public sealed class PetWeaponAttachment : MonoBehaviour
{
    [SerializeField] private string[] visibleStates = { "Cheer" };
    [SerializeField, Range(0, 1)] private float appearAt = 0.30f;
    [SerializeField, Range(0, 1)] private float disappearAt = 0.67f;
    private Animator animator;
    private Transform wrist, middle, index, little;
    private readonly List<Binding> bindings = new List<Binding>();
    private bool initialized;
    private sealed class Binding
    {
        public SkinnedMeshRenderer renderer;
        public Transform root;
        public Vector3 restPosition, gripLocal;
        public Quaternion restRotation, frameLocal;
        public bool originalEnabled, visible, fitted;
        public bool sword;
        public string evidence;
    }

    public int WeaponCount => bindings.Count;
    public bool HasSword => bindings.Any(binding => binding.sword && binding.fitted);
    public int VisibleWeaponCount => bindings.Count(b => b.visible);
    public string[] Evidence => bindings.Select(b => b.evidence).ToArray();
    public float MaximumGripError => bindings.Where(b => b.visible && b.fitted)
        .Select(b => Vector3.Distance(b.root.TransformPoint(b.gripLocal), HandGrip())).DefaultIfEmpty(0).Max();

    private void Awake() { Initialize(); }
    public void Initialize()
    {
        if (initialized) return;
        initialized = true; animator = GetComponent<Animator>();
        var transforms = GetComponentsInChildren<Transform>(true);
        wrist = transforms.FirstOrDefault(t => t.name == "手首.R");
        middle = transforms.FirstOrDefault(t => t.name == "中指１.R");
        index = transforms.FirstOrDefault(t => t.name == "人指１.R");
        little = transforms.FirstOrDefault(t => t.name == "小指１.R");
        var renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            // Exact renderer plus material identities deliberately exclude bows,
            // ribbons, glasses, sleeves and similarly named clothing bones.
            if (!string.Equals(renderer.name, "Weapon", StringComparison.OrdinalIgnoreCase)) continue;
            bool sparxie = renderer.sharedMaterials.Any(m => m != null && m.name.EndsWith("Sparxie_01_Mat_Weapon", StringComparison.Ordinal));
            bool evanescia = renderer.sharedMaterials.Any(m => m != null && m.name.EndsWith("Evanescia_01_Mat_Weapon", StringComparison.Ordinal));
            if (!sparxie && !evanescia) continue;
            var binding = new Binding { renderer = renderer, originalEnabled = renderer.enabled, sword = evanescia };
            bindings.Add(binding);
            binding.root = transforms.FirstOrDefault(t => t.name == (sparxie ? "weapon_JNT" : "Weapon_00_JNT"));
            if (binding.root != null)
            {
                binding.restPosition = binding.root.localPosition; binding.restRotation = binding.root.localRotation;
                bool dedicated = renderer.bones.All(b => b != null && (b == binding.root || b.IsChildOf(binding.root)))
                    && !renderers.Any(r => r != renderer && r.bones.Any(b => b != null && (b == binding.root || b.IsChildOf(binding.root))));
                Transform end = transforms.FirstOrDefault(t => t.name == (sparxie ? "WeaponStretch_00_JNT" : "Ruler_03_JNT"));
                if (dedicated && end != null && wrist != null && middle != null && index != null && little != null)
                {
                    // Source-FBX inspection: Sparxie's straight narrow shaft runs
                    // between the lower accessory and the stretch joint; 45% is
                    // inside that shaft. Evanescia's authored root is at its hilt.
                    Vector3 grip = sparxie ? Vector3.Lerp(binding.root.position, end.position, 0.45f) : binding.root.position;
                    binding.gripLocal = binding.root.InverseTransformPoint(grip);
                    Vector3 axis = (end.position - binding.root.position).normalized;
                    Vector3 front = sparxie ? binding.root.forward : binding.root.right;
                    front = Vector3.ProjectOnPlane(front, axis).normalized;
                    if (front.sqrMagnitude > 0.5f)
                    {
                        binding.frameLocal = Quaternion.Inverse(binding.root.rotation) * Quaternion.LookRotation(front, axis);
                        binding.fitted = true;
                    }
                }
            }
            binding.evidence = (sparxie ? "Sparxie: separate Weapon mesh/material, dedicated weapon_JNT chain; measured lower straight shaft grip."
                : "Evanescia katana: separate Weapon mesh/material, dedicated Weapon_00_JNT chain; source hilt pivot grip. The source rig retains its Ruler joint names.")
                + (binding.fitted ? " Hand mount fitted." : " Hand mount not verified; hidden.");
            renderer.enabled = false;
        }
    }

    private Vector3 HandGrip()
    {
        Vector3 finger = (middle.position - wrist.position).normalized;
        Vector3 spread = index.position - little.position;
        Vector3 palm = Vector3.Cross(finger, spread).normalized;
        return Vector3.Lerp(wrist.position, middle.position, 0.72f) + palm * spread.magnitude * 0.24f;
    }

    private void LateUpdate()
    {
        if (animator == null || animator.runtimeAnimatorController == null) { EvaluateForPose("Idle", 0); return; }
        var state = animator.GetCurrentAnimatorStateInfo(0);
        string name = state.IsName("Base Layer.HoldSword") ? "HoldSword" : state.IsName("Base Layer.DrawSlash") ? "DrawSlash"
            : visibleStates.FirstOrDefault(s => state.IsName("Base Layer." + s)) ?? "";
        EvaluateForPose(name, state.normalizedTime, animator.IsInTransition(0));
    }

    /// <summary>The same deterministic pose evaluation is used by preview regression.</summary>
    public void EvaluateForPose(string state, float normalizedTime, bool inTransition = false, bool respectPreferences = true)
    {
        Initialize();
        bool show = !inTransition && visibleStates.Contains(state) && normalizedTime >= appearAt && normalizedTime <= disappearAt;
        foreach (Binding binding in bindings)
        {
            bool swordPhase = !inTransition && (state == "HoldSword" ? normalizedTime >= .24f && normalizedTime <= .76f
                : state == "DrawSlash" && normalizedTime >= .18f && normalizedTime <= .80f);
            string action = state == "HoldSword" ? "hold_sword" : state == "DrawSlash" ? "draw_slash" : state.ToLowerInvariant();
            bool visible = (binding.sword ? swordPhase : show) && binding.fitted && (!respectPreferences || PetActionLibrary.IsEnabled(action));
            if (visible)
            {
                Vector3 finger = (middle.position - wrist.position).normalized;
                Vector3 spread = (index.position - little.position).normalized;
                Vector3 palm = Vector3.Cross(finger, spread).normalized;
                // Both verified rigs hold the right fist near the centerline
                // during Cheer. A small turn from the knuckle row can still
                // leave the shaft vertical across the face, because that row
                // starts tilted inward. Give its long end a definite outward
                // direction relative to the character instead. The palm still
                // controls roll and the measured grip stays inside the hand.
                float outwardTilt = binding.sword ? (state == "DrawSlash" ? Mathf.Lerp(35, 120, Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.32f, .72f, normalizedTime))) : 35) : 38;
                float radians = outwardTilt * Mathf.Deg2Rad;
                Vector3 shaft = -transform.right * Mathf.Sin(radians) + transform.up * Mathf.Cos(radians);
                Vector3 weaponFront = Vector3.ProjectOnPlane(palm, shaft).normalized;
                if (weaponFront.sqrMagnitude < .5f) weaponFront = Vector3.ProjectOnPlane(transform.forward, shaft).normalized;
                binding.root.rotation = Quaternion.LookRotation(weaponFront, shaft)
                    * Quaternion.Inverse(binding.frameLocal);
                binding.root.position += HandGrip() - binding.root.TransformPoint(binding.gripLocal);
            }
            else RestoreRoot(binding);
            binding.renderer.enabled = visible; binding.visible = visible;
        }
    }

    private static void RestoreRoot(Binding binding)
    {
        if (binding.root == null) return;
        binding.root.localPosition = binding.restPosition; binding.root.localRotation = binding.restRotation;
    }
    private void OnDisable()
    {
        foreach (Binding binding in bindings) { RestoreRoot(binding); if (binding.renderer != null) binding.renderer.enabled = false; binding.visible = false; }
    }
    private void OnDestroy()
    {
        foreach (Binding binding in bindings) { RestoreRoot(binding); if (binding.renderer != null) binding.renderer.enabled = binding.originalEnabled; }
    }
}
