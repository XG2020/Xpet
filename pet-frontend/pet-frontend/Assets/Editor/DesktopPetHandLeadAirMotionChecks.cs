using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Checks suspended hand poses and a planted landing without a second jump.</summary>
public static class DesktopPetHandLeadAirMotionChecks
{
    [MenuItem("Desktop Pet/Run Hand Lead Air and Landing Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before sampling air and landing clips.");
        var report = new StringBuilder(); var failures = new List<string>();
        var preview = EditorSceneManager.NewPreviewScene();
        var paths = new[] { PetModelSetup.PrefabPath }.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal))).ToArray();
        try
        {
            foreach (string path in paths)
            {
                GameObject model = null;
                try
                {
                    model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    model.SetActive(false); model.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, preview);
                    foreach (string action in new[] { "HandLeadAirLeft", "HandLeadAirRight", "HandLeadLand" })
                    {
                        try { Check(model, action, path, report); }
                        catch (Exception error) { failures.Add(path + "/" + action + ": " + error.GetBaseException().Message); }
                    }
                }
                catch (Exception error) { failures.Add(path + ": " + error.GetBaseException().Message); }
                finally { if (model != null) UnityEngine.Object.DestroyImmediate(model); }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("60 FPS skeleton checks; window gravity and mouse-hand IK are verified by runtime interaction checks.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "hand-lead-air-landing-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_HAND_LEAD_AIR_LANDING_CHECKS " + report); else Debug.LogError("PET_HAND_LEAD_AIR_LANDING_CHECKS " + report);
    }

    private static void Check(GameObject model, string action, string path, StringBuilder report)
    {
        var controller = model.GetComponent<Animator>().runtimeAnimatorController as AnimatorController;
        Require(controller != null, "Missing generated controller.");
        var clips = controller.animationClips.Where(clip => clip != null).GroupBy(clip => clip.name).ToDictionary(group => group.Key, group => group.First());
        Require(clips.TryGetValue(action, out AnimationClip clip), "Missing generated clip.");
        bool landing = action == "HandLeadLand";
        Require(AnimationUtility.GetAnimationClipSettings(clip).loopTime == !landing, "Incorrect loop setting.");
        var state = controller.layers[0].stateMachine.states.First(child => child.state.name == action).state;
        Require(landing ? state.transitions.Any(transition => transition.hasExitTime && transition.destinationState != null && transition.destinationState.name == "Idle") : state.transitions.Length == 0,
            "Incorrect suspension lifetime / landing return.");
        Require(landing ? clip.length >= 0.8f && clip.length <= 1.1f : clip.length <= 4f, "Clip cadence is outside the requested range.");

        var all = model.GetComponentsInChildren<Transform>(true);
        var bones = all.GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
        var feet = new[] { bones["足首.R"], bones["足首.L"] };
        clips["Idle"].SampleAnimation(model, 0);
        var idleRotations = all.Select(bone => bone.localRotation).ToArray(); var idlePositions = all.Select(bone => bone.localPosition).ToArray();
        Vector3[] idleFeet = feet.Select(foot => foot.position).ToArray(); Vector3 root = model.transform.position;
        float idleCenterY = bones["センター"].position.y;
        string heldSide = action.EndsWith("Left", StringComparison.Ordinal) ? "L" : "R", freeSide = heldSide == "L" ? "R" : "L";
        clip.SampleAnimation(model, 0);
        var startRotations = all.Select(bone => bone.localRotation).ToArray(); var startPositions = all.Select(bone => bone.localPosition).ToArray();
        var previous = all.Select(bone => bone.localRotation).ToArray();
        float step = 0, footDrift = 0, compression = 0, upward = 0;
        float handRaise = landing ? 0 : float.PositiveInfinity, freeDrop = landing ? 0 : float.PositiveInfinity;
        int compressionWindows = 0; bool compressed = false;
        int frames = Mathf.CeilToInt(clip.length * 60);
        for (int frame = 1; frame <= frames; frame++)
        {
            clip.SampleAnimation(model, clip.length * frame / frames);
            Require(Vector3.Distance(root, model.transform.position) < 0.00001f, "Animation moved the character container.");
            for (int index = 0; index < all.Length; index++)
            {
                Quaternion rotation = all[index].localRotation;
                Require(!float.IsNaN(rotation.x + rotation.y + rotation.z + rotation.w), "Non-finite joint pose.");
                step = Mathf.Max(step, Quaternion.Angle(previous[index], rotation)); previous[index] = rotation;
            }
            if (landing)
            {
                footDrift = Mathf.Max(footDrift, feet.Select((foot, index) => Vector3.Distance(foot.position, idleFeet[index])).Max());
                float drop = idleCenterY - bones["センター"].position.y;
                compression = Mathf.Max(compression, drop); upward = Mathf.Max(upward, -drop);
                bool absorbing = drop > 0.02f; if (absorbing && !compressed) compressionWindows++; compressed = absorbing;
            }
            else
            {
                handRaise = Mathf.Min(handRaise, bones["手首." + heldSide].position.y - bones["腕." + heldSide].position.y);
                freeDrop = Mathf.Min(freeDrop, bones["腕." + freeSide].position.y - bones["手首." + freeSide].position.y);
            }
        }
        Require(step <= 8f, "A joint jumps more than 8 degrees at 60 FPS.");
        for (int index = 0; index < all.Length; index++)
        {
            Require(Quaternion.Angle(all[index].localRotation, landing ? idleRotations[index] : startRotations[index]) < 0.7f, "Rotation misses the loop/Idle seam: " + all[index].name);
            Require(Vector3.Distance(all[index].localPosition, landing ? idlePositions[index] : startPositions[index]) < 0.002f, "Position misses the loop/Idle seam: " + all[index].name);
        }
        if (landing)
        {
            Require(footDrift < 0.002f, "Landing slides or lifts a planted shoe.");
            Require(compression > 0.025f && compressionWindows == 1, "Landing needs one visible knee compression.");
            Require(upward < 0.002f, "Landing jumps above its resting center height.");
        }
        else Require(handRaise > 0.15f && freeDrop > 0.20f, "Suspension must raise the selected hand and let the free arm hang.");
        report.AppendLine($"PASS {path}/{action}: duration={clip.length:F3}s, step60fps={step:F3}deg, plantedFootDrift={footDrift:F6}m, compression={compression:F4}m, upward={upward:F5}m, heldRaise={handRaise:F3}m, freeDrop={freeDrop:F3}m.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
