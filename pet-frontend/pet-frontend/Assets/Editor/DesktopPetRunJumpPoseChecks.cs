using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Verifies visible run/jump choreography using the actual pose at multiple phases.</summary>
public static class DesktopPetRunJumpPoseChecks
{
    [MenuItem("Desktop Pet/Run Run and Jump Pose Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before checking locomotion poses.");
        var report = new StringBuilder(); var failures = new List<string>(); var preview = EditorSceneManager.NewPreviewScene();
        var paths = new[] { PetModelSetup.PrefabPath }.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal))).ToArray();
        try
        {
            foreach (string path in paths)
            {
                GameObject model = null;
                try
                {
                    model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path)); model.SetActive(false);
                    model.hideFlags = HideFlags.HideAndDontSave; UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, preview);
                    Check(model, path, report);
                }
                catch (Exception error) { failures.Add(path + ": " + error.GetBaseException().Message); }
                finally { if (model != null) UnityEngine.Object.DestroyImmediate(model); }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "run-jump-pose-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_RUN_JUMP_POSE_CHECKS " + report); else Debug.LogError("PET_RUN_JUMP_POSE_CHECKS " + report);
    }

    private static void Check(GameObject model, string path, StringBuilder report)
    {
        var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name).ToDictionary(g => g.Key, g => g.First());
        var clips = model.GetComponent<Animator>().runtimeAnimatorController.animationClips.Where(c => c != null).GroupBy(c => c.name).ToDictionary(g => g.Key, g => g.First());
        Transform root = model.transform;
        Transform torsoBase = bones.TryGetValue("下半身", out Transform lower) ? lower : bones.TryGetValue("上半身", out Transform spine) ? spine : bones["センター"];
        float Knee(string side) => Vector3.Angle(bones["ひざ." + side].position - bones["足." + side].position, bones["足首." + side].position - bones["ひざ." + side].position);
        float Wrists() => (bones["手首.R"].position.y + bones["手首.L"].position.y) * 0.5f;
        float Pitch()
        {
            Vector3 torso = root.InverseTransformVector(bones["頭"].position - torsoBase.position);
            return Mathf.Atan2(-torso.z, torso.y) * Mathf.Rad2Deg;
        }
        float ArmSeparation() => root.InverseTransformVector(bones["手首.R"].position - bones["腕.R"].position).z
            - root.InverseTransformVector(bones["手首.L"].position - bones["腕.L"].position).z;
        clips["Idle"].SampleAnimation(model, 0);
        float idleKnee = (Knee("R") + Knee("L")) * 0.5f, idleWrists = Wrists(), idlePitch = Pitch(), idleCenter = bones["センター"].position.y;
        float runSeparationStart = 0, runSeparationOpposite = 0, runPitch = 0, runLowering = 0;
        foreach (float phase in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            clips["Run"].SampleAnimation(model, clips["Run"].length * phase);
            float separation = ArmSeparation(), pitch = Pitch() - idlePitch, lowering = bones["センター"].position.y - idleCenter;
            report.AppendLine($"{path}/Run phase={phase:F2}: forwardPitch={pitch:F2}deg, centerOffset={lowering:F4}m, RminusLarmZ={separation:F4}m.");
            if (phase == 0) { runSeparationStart = separation; runPitch = pitch; runLowering = lowering; }
            if (phase == 0.5f) runSeparationOpposite = separation;
        }
        Require(runSeparationStart * runSeparationOpposite < 0 && Mathf.Min(Mathf.Abs(runSeparationStart), Mathf.Abs(runSeparationOpposite)) > 0.08f,
            "Run does not alternate its hands against the steps.");
        Require(runPitch > 3f && runLowering < -0.010f, "Run lacks visible forward lean and lowered weight.");
        float crouchKnee = 0, airKnee = 0, armRise = 0, landing = 0;
        foreach (float phase in new[] { 0f, 0.18f, 0.36f, 0.52f, 0.84f, 1f })
        {
            clips["Jump"].SampleAnimation(model, clips["Jump"].length * phase);
            float knee = (Knee("R") + Knee("L")) * 0.5f, rise = Wrists() - idleWrists, offset = bones["センター"].position.y - idleCenter;
            report.AppendLine($"{path}/Jump phase={phase:F2}: kneeFlexion={knee:F2}deg, wristRise={rise:F4}m, centerOffset={offset:F4}m.");
            armRise = Mathf.Max(armRise, rise);
            if (phase == 0.18f) crouchKnee = knee;
            if (phase == 0.52f) airKnee = knee;
            if (phase == 0.84f) landing = offset;
        }
        Require(crouchKnee > idleKnee + 10f, "Jump lacks a visible knee-loading crouch.");
        Require(armRise > 0.15f, "Jump does not swing its arms upward.");
        Require(airKnee > idleKnee + 8f, "Jump does not tuck the legs in flight.");
        Require(landing < -0.012f, "Jump lacks landing compression.");
        report.AppendLine("PASS " + path + ": alternating run, crouch, arm lift, airborne tuck, and landing compression.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
