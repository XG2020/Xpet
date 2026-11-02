using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class DesktopPetLowerGuardChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [Serializable] private sealed class ModelResult
    {
        public string model;
        public int springBones;
        public float guardSeconds, maxWristAboveHip, maxWristSideRatio, minWristInFront;
        public float maxClothDegrees, settledClothDegrees, maxBoneLengthChange;
        public float maxTrajectoryWristAboveHip, maxTrajectoryWristCrossing, maxArmAngularSpeed;
        public string[] failures;
    }
    [Serializable] private sealed class Report { public bool passed; public ModelResult[] models; }

    [MenuItem("Desktop Pet/Run Lower Guard and Cloth Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/guard-cloth"));
        Directory.CreateDirectory(folder);
        var paths = new List<string> { PetModelSetup.PrefabPath };
        paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith("/DesktopPet.prefab")));
        var results = paths.Select(path => Check(path, folder)).ToArray();
        bool passed = results.All(r => r.failures.Length == 0);
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonUtility.ToJson(new Report { passed = passed, models = results }, true));
        if (!passed) throw new InvalidOperationException("LOWER_GUARD_CLOTH_FAILED: " + string.Join("; ", results.SelectMany(r => r.failures.Select(f => r.model + ": " + f))));
        Debug.Log("LOWER_GUARD_CLOTH_PASSED: " + results.Length + " models; lower hand placement, bounded spring response, low-frame-rate stability and pose renders.");
    }

    private static ModelResult Check(string path, string folder)
    {
        var result = new ModelResult { model = path, minWristInFront = float.PositiveInfinity };
        var failures = new List<string>();
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            preview.AddSingleGO(model);
            model.transform.position = Vector3.zero;
            var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(b => b.name).ToDictionary(g => g.Key, g => g.First());
            var animator = model.GetComponent<Animator>();
            var clips = animator.runtimeAnimatorController.animationClips;
            var idle = clips.First(c => c.name == "Idle");
            var guard = clips.First(c => c.name == "GuardLower");
            result.guardSeconds = guard.length;
            animator.enabled = false;
            var motion = model.GetComponent<PetSecondaryMotion>();
            if (motion.SimulatedBoneCount == 0) typeof(PetSecondaryMotion).GetMethod("Awake", Private).Invoke(motion, null);
            result.springBones = motion.SimulatedBoneCount;
            if (result.springBones == 0) failures.Add("No clothing/hair bones recognized");
            var reset = typeof(PetSecondaryMotion).GetMethod("ResetMotion", Private);
            var simulate = typeof(PetSecondaryMotion).GetMethod("Simulate", Private);
            var all = model.GetComponentsInChildren<Transform>(true);
            var rests = all.ToDictionary(b => b, b => b.localRotation);
            var positions = all.ToDictionary(b => b, b => b.localPosition);
            var springEntries = (System.Collections.IEnumerable)typeof(PetSecondaryMotion).GetField("springs", Private).GetValue(motion);
            var cloth = springEntries.Cast<object>().Select(s => (Transform)s.GetType().GetField("bone").GetValue(s)).ToArray();
            idle.SampleAnimation(model, 0);
            reset.Invoke(motion, null);
            string id = path.Contains("ImportedPets") ? Path.GetFileName(Path.GetDirectoryName(path)) : "builtin-lita";
            Render(preview, model, Path.Combine(folder, id + "-idle.png"), false);
            var armBones = new[] { bones["腕.R"], bones["ひじ.R"], bones["手首.R"],
                bones["腕.L"], bones["ひじ.L"], bones["手首.L"] };
            var previousArmRotations = armBones.ToDictionary(b => b, b => b.localRotation);
            float previousTime = 0;
            for (int frame = 0; frame <= Mathf.CeilToInt(guard.length * 30); frame++)
            {
                float time = Mathf.Min(frame / 30f, guard.length);
                guard.SampleAnimation(model, time);
                // Check the entire return trajectory. Endpoints and a correct
                // held pose previously missed a refinement detour that crossed
                // both arms and lifted the wrists back up to the chest.
                Vector3 frameHip = model.transform.InverseTransformPoint(
                    (bones.TryGetValue("下半身", out Transform framePelvis) ? framePelvis : bones["センター"]).position);
                foreach (string side in new[] { "R", "L" })
                {
                    Vector3 wrist = model.transform.InverseTransformPoint(bones["手首." + side].position);
                    Vector3 shoulder = model.transform.InverseTransformPoint(bones["腕." + side].position);
                    float sign = Mathf.Sign(shoulder.x - frameHip.x);
                    result.maxTrajectoryWristAboveHip = Mathf.Max(result.maxTrajectoryWristAboveHip, wrist.y - frameHip.y);
                    result.maxTrajectoryWristCrossing = Mathf.Max(result.maxTrajectoryWristCrossing, -sign * (wrist.x - frameHip.x));
                }
                foreach (Transform bone in armBones)
                {
                    if (time > previousTime)
                        result.maxArmAngularSpeed = Mathf.Max(result.maxArmAngularSpeed,
                            Quaternion.Angle(previousArmRotations[bone], bone.localRotation) / (time - previousTime));
                    previousArmRotations[bone] = bone.localRotation;
                }
                previousTime = time;
                simulate.Invoke(motion, new object[] { 1f / 30, time });
                foreach (Transform bone in cloth)
                    result.maxClothDegrees = Mathf.Max(result.maxClothDegrees, Quaternion.Angle(rests[bone], bone.localRotation));
                foreach (Transform bone in all)
                    if (bone.name != "センター") result.maxBoneLengthChange = Mathf.Max(result.maxBoneLengthChange, Vector3.Distance(positions[bone], bone.localPosition));
            }
            if (result.maxTrajectoryWristAboveHip > 0.06f) failures.Add("Lower guard trajectory lifts a wrist above the abdomen");
            if (result.maxTrajectoryWristCrossing > 0.04f) failures.Add("Lower guard trajectory crosses a wrist to the opposite side");
            if (result.maxArmAngularSpeed > 365f) failures.Add("Lower guard arm exceeds the generated angular-speed limit");
            reset.Invoke(motion, null);
            guard.SampleAnimation(model, guard.length * 0.5f);
            Vector3 hip = model.transform.InverseTransformPoint((bones.TryGetValue("下半身", out Transform pelvis) ? pelvis : bones["センター"]).position);
            float shoulderHalfWidth = Mathf.Abs(model.transform.InverseTransformPoint(bones["腕.R"].position).x
                - model.transform.InverseTransformPoint(bones["腕.L"].position).x) * 0.5f;
            foreach (string side in new[] { "R", "L" })
            {
                Vector3 wrist = model.transform.InverseTransformPoint(bones["手首." + side].position);
                Vector3 shoulder = model.transform.InverseTransformPoint(bones["腕." + side].position);
                result.maxWristAboveHip = Mathf.Max(result.maxWristAboveHip, wrist.y - hip.y);
                float sideRatio = Mathf.Abs(wrist.x - hip.x) / Mathf.Max(0.01f, shoulderHalfWidth);
                result.maxWristSideRatio = Mathf.Max(result.maxWristSideRatio, sideRatio);
                result.minWristInFront = Mathf.Min(result.minWristInFront, hip.z - wrist.z);
                Vector3 elbow = model.transform.InverseTransformPoint(bones["ひじ." + side].position);
                Vector3 palm = bones.TryGetValue("中指１." + side, out Transform middle)
                    ? (wrist + model.transform.InverseTransformPoint(middle.position)) * 0.5f
                    : wrist + (wrist - elbow) * 0.2f;
                if (palm.y > hip.y + 0.025f) failures.Add(side + " lower guard palm is above the lower abdomen");
                if (Mathf.Abs(palm.x - hip.x) > shoulderHalfWidth * 0.85f) failures.Add(side + " lower guard palm remains at the side");
                if (wrist.z > hip.z - 0.05f) failures.Add(side + " lower guard wrist is not in front of the torso");
            }
            Render(preview, model, Path.Combine(folder, id + "-guard-lower.png"), false);
            Render(preview, model, Path.Combine(folder, id + "-guard-lower-side.png"), true);
            // At 15 FPS, integrate the full elapsed frame using bounded substeps.
            // Excite the real attachment hierarchy, stop it, then verify settling.
            idle.SampleAnimation(model, 0);
            reset.Invoke(motion, null);
            for (int frame = 0; frame < 180; frame++)
            {
                model.transform.rotation = frame < 30 ? Quaternion.Euler(0, Mathf.Sin(frame * 0.3f) * 20, 0) : Quaternion.identity;
                simulate.Invoke(motion, new object[] { 1f / 15, frame / 15f });
                foreach (Transform bone in cloth)
                {
                    float angle = Quaternion.Angle(rests[bone], bone.localRotation);
                    if (float.IsNaN(angle)) throw new InvalidOperationException("Non-finite spring rotation");
                    result.maxClothDegrees = Mathf.Max(result.maxClothDegrees, angle);
                    if (frame == 179) result.settledClothDegrees = Mathf.Max(result.settledClothDegrees, angle);
                }
            }
            if (result.maxClothDegrees < 0.1f) failures.Add("Clothing never responds to motion");
            if (result.maxClothDegrees > 4.6f) failures.Add("Spring exceeds its angular limit");
            if (result.settledClothDegrees > 0.6f) failures.Add("Clothing does not settle after motion");
            if (result.maxBoneLengthChange > 0.0001f) failures.Add("Animation changes a bone length");
        }
        catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        finally { preview.Cleanup(); }
        result.failures = failures.ToArray();
        return result;
    }

    private static void Render(PreviewRenderUtility preview, GameObject model, string path, bool side)
    {
        // Static previews can resample an Animator internally. Freeze the
        // already sampled skin so the image depicts the measured pose exactly.
        var frozen = new List<GameObject>();
        var meshes = new List<Mesh>();
        var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled).ToArray();
        foreach (var skin in skins)
        {
            var mesh = new Mesh(); skin.BakeMesh(mesh);
            Matrix4x4 toLocal = model.transform.worldToLocalMatrix * PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh);
            mesh.vertices = mesh.vertices.Select(toLocal.MultiplyPoint3x4).ToArray();
            mesh.normals = mesh.normals.Select(n => toLocal.inverse.transpose.MultiplyVector(n).normalized).ToArray();
            mesh.RecalculateBounds();
            var copy = new GameObject("Pose render", typeof(MeshFilter), typeof(MeshRenderer));
            copy.transform.SetParent(model.transform, false);
            copy.GetComponent<MeshFilter>().sharedMesh = mesh;
            copy.GetComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
            skin.enabled = false; frozen.Add(copy); meshes.Add(mesh);
        }
        try
        {
        preview.camera.transform.position = side ? new Vector3(2.8f, 1, -1.5f) : new Vector3(0, 1, -3.2f);
        preview.camera.transform.LookAt(new Vector3(0, 0.86f, 0));
        preview.camera.orthographic = true;
        preview.camera.orthographicSize = 0.97f;
        preview.camera.nearClipPlane = 0.01f;
        preview.camera.farClipPlane = 20;
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = new Color(0.12f, 0.15f, 0.2f);
        preview.lights[0].intensity = 1.3f;
        preview.lights[0].transform.rotation = Quaternion.Euler(35, 170, 0);
        preview.lights[1].intensity = 0.7f;
        preview.lights[1].transform.rotation = Quaternion.Euler(340, 340, 0);
        preview.ambientColor = Color.gray;
        preview.BeginStaticPreview(new Rect(0, 0, 600, 780));
        preview.Render(true);
        var texture = preview.EndStaticPreview();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        }
        finally
        {
            foreach (var skin in skins) skin.enabled = true;
            foreach (var copy in frozen) UnityEngine.Object.DestroyImmediate(copy);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
