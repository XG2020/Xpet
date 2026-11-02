using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Samples protective/touch motion, entry and release at desktop frame rates.</summary>
public static class DesktopPetProtectiveMotionChecks
{
    [MenuItem("Desktop Pet/Run Guard and Leg Motion Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before sampling protective clips.");
        var report = new StringBuilder(); var failures = new List<string>();
        var preview = EditorSceneManager.NewPreviewScene();
        var paths = new[] { PetModelSetup.PrefabPath }.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal))).ToArray();
        try
        {
            foreach (string path in paths)
            {
                GameObject instance = null;
                try
                {
                    instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    instance.SetActive(false); instance.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, preview);
                    var clips = instance.GetComponent<Animator>().runtimeAnimatorController.animationClips.Where(c => c != null)
                        .GroupBy(c => c.name).ToDictionary(g => g.Key, g => g.First());
                    foreach (string action in new[] { "Guard", "GuardLower", "LegRetract", "LegShift", "LegSway" })
                        foreach (int fps in new[] { 15, 30, 60 })
                        {
                            try { Check(instance, clips, action, fps, path, report); }
                            catch (Exception error) { failures.Add(path + "/" + action + "@" + fps + "fps: " + error.GetBaseException().Message); }
                        }
                }
                catch (Exception error) { failures.Add(path + ": " + error.GetBaseException().Message); }
                finally { if (instance != null) UnityEngine.Object.DestroyImmediate(instance); }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("Includes 0.18s Idle entry and 0.20s Idle release. Numerical joint/foot checks; rendered hand and clothing clearance still requires visual review.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "guard-leg-motion-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_GUARD_LEG_CHECKS " + report); else Debug.LogError("PET_GUARD_LEG_CHECKS " + report);
    }

    private static void Check(GameObject model, Dictionary<string, AnimationClip> clips, string action, int fps, string path, StringBuilder report)
    {
        Require(clips.TryGetValue(action, out AnimationClip clip), "Missing generated clip.");
        Require(clip.length <= 12f && !AnimationUtility.GetAnimationClipSettings(clip).loopTime, "Clip is stalled or loops.");
        var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        var all = model.GetComponentsInChildren<Transform>(true);
        var tracked = new[] { "頭", "上半身", "上半身2", "腕.R", "ひじ.R", "手首.R", "腕.L", "ひじ.L", "手首.L", "足.R", "ひざ.R", "足.L", "ひざ.L" }
            .Where(bones.ContainsKey).Select(name => bones[name]).ToArray();
        clips["Idle"].SampleAnimation(model, 0);
        var rest = all.Select(t => t.localRotation).ToArray(); var restPositions = all.Select(t => t.localPosition).ToArray();
        var previous = tracked.Select(t => t.rotation).ToArray(); var previousVelocity = new Vector3[tracked.Length];
        Vector3 leftFoot = bones["足首.L"].position, rightFoot = bones["足首.R"].position, root = model.transform.position;
        bool guard = action == "Guard" || action == "GuardLower";
        float speed = 0, acceleration = 0, step = 0, footDrift = 0, rightTravel = 0, frontTravel = 0;
        string accelerationBone = ""; float accelerationAt = 0;
        float duration = clip.length + 0.35f; int frames = Mathf.CeilToInt(duration * fps); float dt = duration / frames;
        var actionRotations = new Quaternion[all.Length]; var actionPositions = new Vector3[all.Length];
        for (int frame = 1; frame <= frames; frame++)
        {
            float time = frame * dt;
            SampleCycle(model, all, clip, clips["Idle"], time, actionRotations, actionPositions);
            Require(Vector3.Distance(root, model.transform.position) < 0.00001f, "Character container moved.");
            for (int index = 0; index < tracked.Length; index++)
            {
                Quaternion current = tracked[index].rotation;
                Require(!float.IsNaN(current.x + current.y + current.z + current.w), "Non-finite joint pose.");
                Quaternion delta = current * Quaternion.Inverse(previous[index]); if (delta.w < 0) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                delta.ToAngleAxis(out float angle, out Vector3 axis); if (angle > 180) angle -= 360;
                Vector3 velocity = Mathf.Abs(angle) < 0.001f || float.IsNaN(axis.x) ? Vector3.zero : axis * (angle / dt);
                speed = Mathf.Max(speed, velocity.magnitude); step = Mathf.Max(step, Mathf.Abs(angle));
                float jointAcceleration = (velocity - previousVelocity[index]).magnitude / dt;
                if (jointAcceleration > acceleration) { acceleration = jointAcceleration; accelerationBone = tracked[index].name; accelerationAt = time; }
                previous[index] = current; previousVelocity[index] = velocity;
            }
            footDrift = Mathf.Max(footDrift, Vector3.Distance(leftFoot, bones["足首.L"].position));
            rightTravel = Mathf.Max(rightTravel, Vector3.Distance(rightFoot, bones["足首.R"].position));
            Vector3 footDelta = model.transform.InverseTransformVector(bones["足首.R"].position-rightFoot);
            frontTravel = Mathf.Max(frontTravel, new Vector2(footDelta.x, footDelta.y).magnitude);
            if (guard) footDrift = Mathf.Max(footDrift, Vector3.Distance(rightFoot, bones["足首.R"].position));
        }
        report.AppendLine($"{path}/{action}@{fps}: duration={clip.length:F3}s, speed={speed:F2}deg/s, acceleration={acceleration:F1}deg/s2 ({accelerationBone}, time={accelerationAt:F4}s, phase={accelerationAt / clip.length:F4}), step={step:F3}deg, supportFootDrift={footDrift:F6}m, rightFootTravel={rightTravel:F4}m.");
        Require(speed < 155f, "A protective/touch joint moves too fast: " + speed.ToString("F2") + "deg/s.");
        Require(acceleration < 1200f, "Joint acceleration reveals a branch/velocity jump: " + acceleration.ToString("F1") + "deg/s2.");
        Require(step < 155f / fps + 0.2f, "Frame-to-frame joint jump.");
        Require(footDrift < 0.002f, "A support foot slides.");
        if (!guard)
        {
            Require(frontTravel > (action == "LegShift" ? 0.040f : 0.085f), "Leg response is too small from the front camera: " + frontTravel.ToString("F4") + "m.");
            report.AppendLine($"  Front-view foot travel={frontTravel:F4}m; approximately {frontTravel / 1.72f * 300f:F1}px at 300px character height.");
        }
        clip.SampleAnimation(model, clip.length);
        for (int index = 0; index < all.Length; index++)
        {
            Require(Quaternion.Angle(rest[index], all[index].localRotation) < 0.7f, "Does not restore Idle rotation: " + all[index].name);
            Require(Vector3.Distance(restPositions[index], all[index].localPosition) < 0.002f, "Does not restore Idle position: " + all[index].name);
        }
    }

    private static void SampleCycle(GameObject model, Transform[] all, AnimationClip action, AnimationClip idle, float time, Quaternion[] rotations, Vector3[] positions)
    {
        action.SampleAnimation(model, Mathf.Min(time, action.length));
        float weight = time <= 0.18f ? Mathf.Clamp01(time / 0.18f) : time > action.length ? 1 - Mathf.Clamp01((time - action.length) / 0.20f) : 1;
        if (weight >= 1) return;
        for (int index = 0; index < all.Length; index++) { rotations[index] = all[index].localRotation; positions[index] = all[index].localPosition; }
        idle.SampleAnimation(model, time > action.length ? time - action.length : time);
        for (int index = 0; index < all.Length; index++)
        {
            all[index].localRotation = Quaternion.Slerp(all[index].localRotation, rotations[index], weight);
            all[index].localPosition = Vector3.Lerp(all[index].localPosition, positions[index], weight);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
