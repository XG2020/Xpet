using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>Plays imported bone motion over the active model with smooth entry/exit and bounded root movement.</summary>
[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public sealed class PetImportedMotionPlayer : MonoBehaviour
{
    private sealed class Binding
    {
        public Transform bone;
        public PetVmdMotion.Key[] keys;
        public Quaternion restLocal, modelBasis;
        public Vector3 restPosition;
        public bool rootTranslation;
        public Quaternion baseRotation;
        public Vector3 basePosition;
        public bool applied;
    }
    private readonly List<Binding> bindings = new List<Binding>();
    private readonly Dictionary<Transform, Matrix4x4> bindWorld = new Dictionary<Transform, Matrix4x4>();
    private PetCharacterAnimation character;
    private PetActionEntry active;
    private PetVmdMotion motion;
    private float startedAt, stoppedAt = -1, positionScale, bodyHeight;
    private int version;
    public bool IsPlaying => active != null;
    public int BoundBoneCount => bindings.Count;
    public static event Action<string> PlaybackError;

    public static string StorageDirectory => PetResourceLibraries.MotionDirectory;
    public static string ResolveFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[a-f0-9]{64}\\.vmd$"))
            throw new InvalidDataException("导入动作文件标识无效。");
        return Path.Combine(StorageDirectory, fileName);
    }

    public sealed class Prepared { public PetVmdMotion motion; public string fileName; }
    public static Task<Prepared> Prepare(string source)
    {
        string directory = StorageDirectory; // Unity path access stays on the main thread.
        return Task.Run(() =>
        {
            var path = new FileInfo(source);
            if (!path.Exists || path.Extension.ToLowerInvariant() != ".vmd" || path.Length > PetVmdMotion.MaximumBytes)
                throw new InvalidDataException("请选择不超过 32 MB 的 VMD 动作文件。");
            byte[] bytes = File.ReadAllBytes(path.FullName);
            PetVmdMotion data = PetVmdMotion.Read(bytes);
            string hash;
            using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
            Directory.CreateDirectory(directory);
            string fileName = hash + ".vmd", target = Path.Combine(directory, fileName);
            if (!File.Exists(target))
            {
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, target); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return new Prepared { motion = data, fileName = fileName };
        });
    }

    public static int CountCompatibleBones(Transform model, PetVmdMotion data)
    {
        if (model == null) return 0;
        Transform visual = model.Find("VisualModel") ?? model;
        return visual.GetComponentsInChildren<Transform>(true).Count(bone => !IsPoseSource(bone, model) && CompatibleTrack(data, PetVmdMotion.CanonicalBone(bone.name)) != null);
    }

    private static bool IsPoseSource(Transform bone, Transform root)
    {
        for (Transform ancestor = bone; ancestor != null && ancestor != root; ancestor = ancestor.parent)
            if (ancestor.name == "__BundledPoseSource") return true;
        return false;
    }

    private static PetVmdMotion.Key[] CompatibleTrack(PetVmdMotion data, string name)
    {
        if (data.tracks.TryGetValue(name, out var keys)) return keys;
        // MMD's deforming D leg chain inherits the corresponding FK motion.
        // The existing models retain both chains and weight the skirt/legs to D.
        foreach (string limb in new[] { "足", "ひざ", "足首" })
            foreach (string side in new[] { ".L", ".R" })
                if (name == limb + "D" + side && data.tracks.TryGetValue(limb + side, out keys)) return keys;
        return null;
    }

    public bool Play(PetActionEntry entry)
    {
        if (!PetActionLibrary.IsImported(entry) || !entry.enabled) return false;
        character = GetComponentInChildren<PetCharacterAnimation>(true);
        if (character != null && (character.IsDragging || character.IsTouchReacting || character.IsHandLeading)) return false;
        StopImmediately(); int generation = version;
        active = entry;
        StartCoroutine(Load(entry, generation));
        return true;
    }

    private IEnumerator Load(PetActionEntry entry, int generation)
    {
        Task<PetVmdMotion> task;
        try { string path = ResolveFile(entry.motion_file); task = Task.Run(() => PetVmdMotion.Read(path)); }
        catch (Exception error) { Fail(error.Message); yield break; }
        while (!task.IsCompleted && generation == version) yield return null;
        if (generation != version) yield break;
        if (task.IsFaulted) { Fail(task.Exception.GetBaseException().Message); yield break; }
        character?.StopWalking(); character?.PreviewAction("idle");
        // Non-deforming root bones may have no mesh bind-pose entry. Settle those
        // into the neutral animation before recording their coordinate baseline.
        yield return new WaitForSecondsRealtime(0.36f);
        if (generation != version) yield break;
        try
        {
            motion = task.Result; Bind(motion);
            if (bindings.Count < 2) throw new InvalidDataException("动作骨骼与当前角色不匹配，请选择使用同类 MMD 骨架的动作。");
            startedAt = Time.unscaledTime; stoppedAt = -1;
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private void Bind(PetVmdMotion data)
    {
        bindings.Clear(); bindWorld.Clear();
        Transform coordinates = transform.Find("VisualModel") ?? transform;
        foreach (SkinnedMeshRenderer skin in coordinates.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (IsPoseSource(skin.transform, transform)) continue;
            if (skin.sharedMesh == null) continue;
            Matrix4x4[] poses = skin.sharedMesh.bindposes;
            for (int index = 0; index < skin.bones.Length && index < poses.Length; index++)
                if (skin.bones[index] != null && !bindWorld.ContainsKey(skin.bones[index]))
                    bindWorld[skin.bones[index]] = skin.transform.localToWorldMatrix * poses[index].inverse;
        }
        var bones = coordinates.GetComponentsInChildren<Transform>(true).Where(bone => !IsPoseSource(bone, transform)).ToArray();
        Transform head = bones.FirstOrDefault(bone => PetVmdMotion.CanonicalBone(bone.name) == "頭");
        Transform ankle = bones.FirstOrDefault(bone => PetVmdMotion.CanonicalBone(bone.name) == "足首.L");
        bodyHeight = head != null && ankle != null ? Vector3.Distance(head.position, ankle.position) : 1.5f;
        positionScale = bodyHeight / 20f;
        foreach (Transform bone in bones)
        {
            string name = PetVmdMotion.CanonicalBone(bone.name);
            var keys = CompatibleTrack(data, name);
            if (keys == null) continue;
            Quaternion rest = bone.localRotation, basis = Quaternion.Inverse(coordinates.rotation) * bone.rotation;
            Vector3 position = bone.localPosition;
            if (bindWorld.TryGetValue(bone, out Matrix4x4 world))
            {
                Matrix4x4 parent = bone.parent != null && bindWorld.TryGetValue(bone.parent, out Matrix4x4 parentBind) ? parentBind : bone.parent != null ? bone.parent.localToWorldMatrix : Matrix4x4.identity;
                Matrix4x4 local = parent.inverse * world;
                rest = local.rotation; position = local.GetColumn(3);
                basis = Quaternion.Inverse(coordinates.rotation) * world.rotation;
            }
            bindings.Add(new Binding { bone = bone, keys = keys, restLocal = rest, restPosition = position, modelBasis = basis,
                rootTranslation = name == "センター" || name == "グルーブ" || name == "全ての親" });
        }
    }

    private void Update()
    {
        // Remove our preceding frame before Animator/retarget sampling. Unanimated
        // control, hair and root bones therefore cannot accumulate or retain offsets.
        RestoreBase();
        // Local touch reactions own the skeleton immediately, including while an
        // imported motion is still loading and could otherwise reset us to Idle.
        if (active != null && character != null && (character.IsDragging || character.IsTouchReacting || character.IsHandLeading)) StopImmediately();
    }

    private void RestoreBase()
    {
        foreach (Binding binding in bindings) if (binding.applied && binding.bone != null)
        {
            binding.bone.localRotation = binding.baseRotation;
            binding.bone.localPosition = binding.basePosition;
            binding.applied = false;
        }
    }

    private void LateUpdate()
    {
        if (active == null || motion == null) return;
        float now = Time.unscaledTime;
        if (character != null && (character.IsDragging || character.IsTouchReacting || character.IsHandLeading)) { StopImmediately(); return; }
        if (!PetActionLibrary.IsEnabled(active.id) || now - startedAt >= motion.Duration / active.speed) Stop();
        float weight = Mathf.SmoothStep(0, 1, Mathf.Clamp01((now - startedAt) / 0.32f));
        if (stoppedAt >= 0) weight *= 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((now - stoppedAt) / 0.32f));
        if (weight <= 0 && stoppedAt >= 0) { StopImmediately(); return; }
        float elapsed = Mathf.Min(motion.Duration, Mathf.Max(0, now - startedAt) * active.speed);
        foreach (Binding binding in bindings)
        {
            if (binding.bone == null) continue;
            PetVmdMotion.Sample(binding.keys, elapsed, out Vector3 position, out Quaternion delta);
            Quaternion localDelta = Quaternion.Inverse(binding.modelBasis) * delta * binding.modelBasis;
            binding.baseRotation = binding.bone.localRotation;
            binding.basePosition = binding.bone.localPosition;
            binding.applied = true;
            binding.bone.localRotation = Quaternion.Slerp(binding.baseRotation, binding.restLocal * localDelta, weight);
            if (binding.rootTranslation)
            {
                // A downloaded dance never relocates the desktop window or escapes its work area.
                Vector3 worldOffset = transform.up * Mathf.Clamp(position.y * positionScale, -bodyHeight * 0.12f, bodyHeight * 0.35f);
                Vector3 offset = binding.bone.parent != null ? binding.bone.parent.InverseTransformVector(worldOffset) : worldOffset;
                binding.bone.localPosition = Vector3.Lerp(binding.basePosition, binding.restPosition + offset, weight);
            }
        }
    }

    public void Stop() { if (motion == null) { StopImmediately(); return; } if (active != null && stoppedAt < 0) stoppedAt = Time.unscaledTime; }
    public void StopImmediately()
    {
        RestoreBase();
        version++; active = null; motion = null; stoppedAt = -1;
        bindings.Clear();
    }
    private void OnDisable() { StopImmediately(); }
    private void Fail(string message) { StopImmediately(); PlaybackError?.Invoke("动作未播放：" + message); Debug.LogWarning("动作导入播放失败：" + message); }
}
