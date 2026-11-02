using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Samples the actual generated clips without changing saved prefabs or the user's scene.</summary>
public static class DesktopPetRelaxedPoseChecks
{
    [Serializable] private sealed class Report { public bool passed; public ModelResult[] models; }
    [Serializable] private sealed class ModelResult
    {
        public string prefab;
        public int samples;
        public float maxIdleUpperFromDown, maxIdleForearmFromDown, maxIdleElbowFlexion;
        public float minIdleWristDropRatio = 1, maxIdleWristDrift, maxReturnUpperFromDown, maxReturnForearmFromDown;
        public float minReturnWristDropRatio = 1;
        public float maxLengthChange;
        public string[] failures;
    }

    [MenuItem("Desktop Pet/Run Relaxed Arm Pose Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        var paths = new List<string> { PetModelSetup.PrefabPath };
        if (AssetDatabase.IsValidFolder("Assets/ImportedPets"))
            paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)));
        var results = paths.Select(CheckModel).ToArray();
        bool passed = results.All(result => result.failures.Length == 0);
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../..", "verification", "models"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "relaxed-arm-pose-checks.json"),
            JsonUtility.ToJson(new Report { passed = passed, models = results }, true));
        if (!passed) throw new InvalidOperationException("RELAXED_ARM_CHECKS_FAILED: "
            + string.Join("; ", results.Where(result => result.failures.Length > 0).Select(result => result.prefab + ": " + string.Join(", ", result.failures))));
        Debug.Log("RELAXED_ARM_CHECKS_PASSED: " + results.Length + " models; actual clip samples verify downward arms, soft elbows, low wrists, fixed lengths and gesture return.");
    }

    private static ModelResult CheckModel(string path)
    {
        var result = new ModelResult { prefab = path };
        var failures = new List<string>();
        GameObject model = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Transform root = model.transform;
            var bones = root.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
            Animator animator = model.GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null) throw new InvalidOperationException("Missing controller");
            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips.Distinct().ToArray();
            AnimationClip idle = clips.FirstOrDefault(clip => clip.name == "Idle");
            if (idle == null) throw new InvalidOperationException("Missing Idle clip");
            var lengths = new Dictionary<string, Vector2>();
            var wrists = new Dictionary<string, Vector3>();
            foreach (string side in new[] { "L", "R" })
            {
                Transform shoulder = bones["腕." + side], elbow = bones["ひじ." + side], wrist = bones["手首." + side];
                lengths[side] = new Vector2(Vector3.Distance(shoulder.position, elbow.position), Vector3.Distance(elbow.position, wrist.position));
            }
            for (int frame = 0; frame <= 120; frame++)
            {
                idle.SampleAnimation(model, idle.length * frame / 120f);
                foreach (string side in new[] { "L", "R" })
                {
                    Vector3 shoulder = bones["腕." + side].position, elbow = bones["ひじ." + side].position, wrist = bones["手首." + side].position;
                    Vector3 upper = elbow - shoulder, forearm = wrist - elbow;
                    result.maxIdleUpperFromDown = Mathf.Max(result.maxIdleUpperFromDown, Vector3.Angle(-root.up, upper));
                    result.maxIdleForearmFromDown = Mathf.Max(result.maxIdleForearmFromDown, Vector3.Angle(-root.up, forearm));
                    result.maxIdleElbowFlexion = Mathf.Max(result.maxIdleElbowFlexion, Vector3.Angle(upper, forearm));
                    result.minIdleWristDropRatio = Mathf.Min(result.minIdleWristDropRatio,
                        Vector3.Dot(shoulder - wrist, root.up) / Mathf.Max(0.0001f, upper.magnitude + forearm.magnitude));
                    Vector3 relativeWrist = root.InverseTransformVector(wrist - shoulder);
                    if (frame == 0) wrists[side] = relativeWrist;
                    else result.maxIdleWristDrift = Mathf.Max(result.maxIdleWristDrift, Vector3.Distance(wrists[side], relativeWrist));
                    result.maxLengthChange = Mathf.Max(result.maxLengthChange, Mathf.Abs(upper.magnitude - lengths[side].x), Mathf.Abs(forearm.magnitude - lengths[side].y));
                    result.samples++;
                }
            }
            // All response gestures should release their hands back down. Walk
            // and Drag are continuous loops whose lifetime the runtime owns.
            foreach (AnimationClip clip in clips.Where(clip => clip.name != "Walk" && clip.name != "Drag"))
                foreach (float time in new[] { 0f, clip.length })
                {
                    clip.SampleAnimation(model, time);
                    foreach (string side in new[] { "L", "R" })
                    {
                        Vector3 upper = bones["ひじ." + side].position - bones["腕." + side].position;
                        Vector3 forearm = bones["手首." + side].position - bones["ひじ." + side].position;
                        result.maxReturnUpperFromDown = Mathf.Max(result.maxReturnUpperFromDown, Vector3.Angle(-root.up, upper));
                        result.maxReturnForearmFromDown = Mathf.Max(result.maxReturnForearmFromDown, Vector3.Angle(-root.up, forearm));
                        result.minReturnWristDropRatio = Mathf.Min(result.minReturnWristDropRatio,
                            Vector3.Dot(-upper - forearm, root.up) / Mathf.Max(0.0001f, upper.magnitude + forearm.magnitude));
                        result.samples++;
                    }
                }
            bool imported = path.StartsWith("Assets/ImportedPets/", StringComparison.Ordinal);
            // User's Star Rail reference has an open upper arm with a soft,
            // nearly aligned elbow. Keeping it against the ribs is not the goal.
            if (result.maxIdleUpperFromDown > (imported ? 29f : 13f)) failures.Add("Idle upper arm leaves the relaxed open stance");
            if (result.maxIdleForearmFromDown > 33f) failures.Add("Idle forearm exceeds the relaxed hand-clearance cone");
            if (result.maxIdleElbowFlexion > (imported ? 17f : 29f)) failures.Add("Idle elbow is too bent");
            if (result.minIdleWristDropRatio < (imported ? 0.86f : 0.90f)) failures.Add("Idle wrist is held too high");
            if (result.maxIdleWristDrift > 0.0005f) failures.Add("Idle breath changes the arm pose");
            if (result.maxReturnUpperFromDown > (imported ? 32f : 18f)) failures.Add("Response gesture does not return its hands down");
            if (result.maxReturnForearmFromDown > 35f || result.minReturnWristDropRatio < (imported ? 0.84f : 0.89f))
                failures.Add("Response gesture returns its shoulder but leaves the wrist raised");
            if (result.maxLengthChange > 0.0001f) failures.Add("Arm segment length changed");
        }
        catch (Exception error) { failures.Add(error.Message); }
        finally { PrefabUtility.UnloadPrefabContents(model); }
        result.failures = failures.ToArray();
        return result;
    }
}
