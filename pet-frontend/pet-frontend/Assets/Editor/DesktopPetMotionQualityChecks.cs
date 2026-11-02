using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Measures the shipped motion clips on temporary character instances.</summary>
public static class DesktopPetMotionQualityChecks
{
    [MenuItem("Desktop Pet/Run Motion Quality Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before checking generated motion.");
        var report = new StringBuilder();
        var failures = new List<string>();
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
                    var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    Require(source != null, "Missing character: " + path);
                    instance = UnityEngine.Object.Instantiate(source);
                    instance.SetActive(false);
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, preview);
                    Check(instance, path, report);
                }
                catch (Exception error) { failures.Add(path + ": " + error.GetBaseException().Message); }
                finally { if (instance != null) UnityEngine.Object.DestroyImmediate(instance); }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("Numerical continuity, foot trajectory and clip cadence checks. Rendered motion and clothing need visual review.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "motion-quality-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_MOTION_QUALITY_CHECKS " + report);
        else Debug.LogError("PET_MOTION_QUALITY_CHECKS " + report);
    }

    private static void Check(GameObject model, string path, StringBuilder report)
    {
        var animator = model.GetComponent<Animator>();
        Require(animator != null && animator.runtimeAnimatorController != null, "No character Animator.");
        var clips = animator.runtimeAnimatorController.animationClips.Where(clip => clip != null).GroupBy(clip => clip.name).ToDictionary(group => group.Key, group => group.First());
        var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
        var tracked = new[] { "上半身", "上半身2", "頭", "腕.R", "ひじ.R", "手首.R", "腕.L", "ひじ.L", "手首.L", "足.R", "ひざ.R", "足.L", "ひざ.L" }
            .Where(bones.ContainsKey).Select(name => bones[name]).ToArray();
        var feet = new[] { "足首.R", "足首.L" }.Where(bones.ContainsKey).Select(name => bones[name]).ToArray();
        Require(feet.Length == 2, "Both ankle bones are required.");
        clips["Idle"].SampleAnimation(model, 0);
        var idleFeet = feet.Select(foot => foot.position).ToArray();
        var idleRotations = tracked.Select(bone => bone.localRotation).ToArray();
        Vector3 rootPosition = model.transform.position;
        foreach (string action in new[] { "Walk", "Run", "Jump", "HoldSword", "DrawSlash" })
        {
            Require(clips.TryGetValue(action, out AnimationClip clip), "Missing new motion: " + action);
            bool loop = action == "Walk" || action == "Run";
            Require(AnimationUtility.GetAnimationClipSettings(clip).loopTime == loop, action + " has the wrong loop setting.");
            float maximumLength = action == "Walk" ? 2.8f : action == "Run" ? 2.4f : action == "Jump" ? 5.5f : 9f;
            Require(clip.length <= maximumLength, action + " cadence is stalled: " + clip.length.ToString("F3") + " seconds.");
            clip.SampleAnimation(model, 0);
            var initial = tracked.Select(bone => bone.localRotation).ToArray();
            var previous = initial.ToArray();
            var startFeet = feet.Select(foot => foot.position).ToArray();
            float largestStep = 0, minimumY = float.PositiveInfinity, lift = 0;
            int frames = Mathf.CeilToInt(clip.length * 60);
            for (int frame = 1; frame <= frames; frame++)
            {
                float time = clip.length * frame / frames;
                clip.SampleAnimation(model, time);
                Require(Vector3.Distance(rootPosition, model.transform.position) < 0.00001f, action + " moved the character container.");
                for (int index = 0; index < tracked.Length; index++)
                {
                    Quaternion rotation = tracked[index].localRotation;
                    Require(!float.IsNaN(rotation.x + rotation.y + rotation.z + rotation.w), "Non-finite rotation in " + action);
                    largestStep = Mathf.Max(largestStep, Quaternion.Angle(previous[index], rotation));
                    previous[index] = rotation;
                }
                minimumY = Mathf.Min(minimumY, feet.Min(foot => foot.position.y));
                lift = Mathf.Max(lift, feet.Min(foot => foot.position.y) - startFeet.Min(position => position.y));
                if (action == "HoldSword" || action == "DrawSlash")
                {
                    Require(feet.Select((foot, index) => Vector3.Distance(foot.position, idleFeet[index])).Max() < 0.002f, action + " slides a planted foot.");
                    Vector3 wrist = model.transform.InverseTransformPoint(bones["手首.R"].position);
                    Vector3 head = model.transform.InverseTransformPoint(bones["頭"].position);
                    float height = bones["頭"].position.y - idleFeet.Min(position => position.y);
                    Require(wrist.y < head.y - height * 0.06f, action + " brings the sword grip into the head region.");
                }
            }
            Require(largestStep <= 8f, action + " has a sharp joint step at 60 FPS: " + largestStep.ToString("F3") + " degrees.");
            float seam = tracked.Select((bone, index) => Quaternion.Angle(loop ? initial[index] : idleRotations[index], bone.localRotation)).Max();
            Require(seam < 0.7f, action + " has a rotation discontinuity at its seam: " + seam.ToString("F3") + " degrees.");
            float footSeam = feet.Select((foot, index) => Vector3.Distance(foot.position, loop ? startFeet[index] : idleFeet[index])).Max();
            Require(footSeam < 0.002f, action + " misses its foot landing/seam: " + footSeam.ToString("F6") + " meters.");
            Require(minimumY >= idleFeet.Min(position => position.y) - 0.015f, action + " pushes an ankle below its standing level.");
            if (action == "Jump") Require(lift > 0.12f, "Jump does not visibly lift both feet.");
            if (action == "HoldSword" || action == "DrawSlash")
            {
                var controller = animator.runtimeAnimatorController as AnimatorController;
                var state = controller.layers[0].stateMachine.states.First(child => child.state.name == action).state;
                Require(state.transitions.Any(transition => transition.destinationState != null && transition.destinationState.name == "Idle" && transition.hasExitTime), action + " does not return to Idle.");
            }
            report.AppendLine($"PASS {path}/{action}: duration={clip.length:F3}s, step60fps={largestStep:F3}deg, seam={seam:F4}deg, footSeam={footSeam:F6}m, bothFeetLift={lift:F4}m.");
        }
        foreach (string action in new[] { "Wave", "Think", "Sad", "Surprised", "Speak" })
        {
            var clip = clips[action];
            foreach (float time in new[] { 0f, clip.length })
            {
                clip.SampleAnimation(model, time);
                float seam = tracked.Select((bone, index) => Quaternion.Angle(idleRotations[index], bone.localRotation)).Max();
                Require(seam < 0.7f, action + " snaps the head/body at its standing seam: " + seam.ToString("F3") + " degrees.");
            }
            report.AppendLine("PASS " + path + "/" + action + ": standing head/body seams meet Idle.");
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
