using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Checks side-specific touches, held-hand locomotion and a fist-clenching stomp.</summary>
public static class DesktopPetSideAndLeadMotionChecks
{
    private static readonly string[] Actions =
    {
        "LegRetractLeft", "LegShiftLeft", "LegSwayLeft", "HandLeadIdleLeft", "HandLeadIdleRight",
        "HandLeadWalkLeft", "HandLeadWalkRight", "HandLeadRunLeft", "HandLeadRunRight", "AngryStomp"
    };

    [MenuItem("Desktop Pet/Run Side and Hand Lead Motion Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before sampling side and hand-lead clips.");
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
                    foreach (string action in Actions)
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
        report.AppendLine("Samples at 60 FPS. Checks side selection, support feet, loop seams, held/free arm travel, closed fists and Idle restoration. Clothing and rendered grip still require visual review.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "side-hand-lead-motion-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_SIDE_HAND_LEAD_CHECKS " + report); else Debug.LogError("PET_SIDE_HAND_LEAD_CHECKS " + report);
    }

    private static void Check(GameObject model, string action, string path, StringBuilder report)
    {
        var animator = model.GetComponent<Animator>();
        Require(animator != null && animator.runtimeAnimatorController != null, "Missing character Animator.");
        var clips = animator.runtimeAnimatorController.animationClips.Where(clip => clip != null)
            .GroupBy(clip => clip.name).ToDictionary(group => group.Key, group => group.First());
        Require(clips.TryGetValue(action, out AnimationClip clip), "Missing generated clip.");
        var controller = animator.runtimeAnimatorController as AnimatorController;
        Require(controller != null, "Expected generated AnimatorController.");
        var state = controller.layers[0].stateMachine.states.First(child => child.state.name == action).state;
        bool lead = action.StartsWith("HandLead", StringComparison.Ordinal), moving = lead && !action.StartsWith("HandLeadIdle", StringComparison.Ordinal);
        bool leftLeg = action.StartsWith("Leg", StringComparison.Ordinal), stomp = action == "AngryStomp";
        bool running = action.StartsWith("HandLeadRun", StringComparison.Ordinal);
        Require(AnimationUtility.GetAnimationClipSettings(clip).loopTime == lead, "Incorrect loop setting.");
        Require(lead ? state.transitions.Length == 0 : state.transitions.Any(transition => transition.hasExitTime && transition.destinationState != null && transition.destinationState.name == "Idle"), "Incorrect loop lifetime / Idle return.");
        Require(clip.length <= (moving ? running ? 2.4f : 2.8f : 12f), "The generated motion cadence is stalled.");
        Require(AnimationUtility.GetCurveBindings(clip).All(binding => binding.type == typeof(Transform)), "Clip unexpectedly animates object/weapon visibility.");
        Require(AnimationUtility.GetObjectReferenceCurveBindings(clip).Length == 0, "Clip unexpectedly changes an object reference.");

        var all = model.GetComponentsInChildren<Transform>(true);
        var bones = all.GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
        var tracked = new[] { "頭", "上半身", "上半身2", "肩.R", "肩.L", "腕.R", "ひじ.R", "手首.R", "腕.L", "ひじ.L", "手首.L", "足.R", "ひざ.R", "足.L", "ひざ.L" }
            .Where(bones.ContainsKey).Select(name => bones[name]).ToArray();
        var feet = new[] { bones["足首.R"], bones["足首.L"] };
        clips["Idle"].SampleAnimation(model, 0);
        var idleRotations = all.Select(bone => bone.localRotation).ToArray();
        var idlePositions = all.Select(bone => bone.localPosition).ToArray();
        var idleFeet = feet.Select(foot => foot.position).ToArray();
        string heldSide = action.EndsWith("Left", StringComparison.Ordinal) ? "L" : "R", freeSide = heldSide == "L" ? "R" : "L";
        Transform heldWrist = bones["手首." + heldSide], freeWrist = bones["手首." + freeSide];
        Vector3 idleHeldWrist = model.transform.InverseTransformPoint(heldWrist.position), rootPosition = model.transform.position;

        clip.SampleAnimation(model, 0);
        var initialRotations = all.Select(bone => bone.localRotation).ToArray();
        var initialPositions = all.Select(bone => bone.localPosition).ToArray();
        var previous = tracked.Select(bone => bone.rotation).ToArray();
        var previousVelocity = new Vector3[tracked.Length];
        Vector3 heldMinimum = heldWrist.position, heldMaximum = heldMinimum, freeMinimum = freeWrist.position, freeMaximum = freeMinimum;
        float step = 0, speed = 0, acceleration = 0, supportDrift = 0, frontTravel = 0, stompLift = 0;
        float minimumHeldReach = float.PositiveInfinity, minAnkleY = float.PositiveInfinity, maximumLowestFoot = 0;
        int liftWindows = 0; bool lifted = false;
        int frames = Mathf.CeilToInt(clip.length * 60f); float dt = clip.length / frames;
        for (int frame = 1; frame <= frames; frame++)
        {
            clip.SampleAnimation(model, frame * dt);
            Require(Vector3.Distance(rootPosition, model.transform.position) < 0.00001f, "Character container moved.");
            for (int index = 0; index < tracked.Length; index++)
            {
                Quaternion current = tracked[index].rotation;
                Require(!float.IsNaN(current.x + current.y + current.z + current.w), "Non-finite joint pose.");
                Quaternion delta = current * Quaternion.Inverse(previous[index]);
                if (delta.w < 0) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                delta.ToAngleAxis(out float angle, out Vector3 axis); if (angle > 180) angle -= 360;
                Vector3 velocity = Mathf.Abs(angle) < 0.001f || float.IsNaN(axis.x) ? Vector3.zero : axis * (angle / dt);
                speed = Mathf.Max(speed, velocity.magnitude); step = Mathf.Max(step, Mathf.Abs(angle));
                acceleration = Mathf.Max(acceleration, (velocity - previousVelocity[index]).magnitude / dt);
                previous[index] = current; previousVelocity[index] = velocity;
            }
            heldMinimum = Vector3.Min(heldMinimum, heldWrist.position); heldMaximum = Vector3.Max(heldMaximum, heldWrist.position);
            freeMinimum = Vector3.Min(freeMinimum, freeWrist.position); freeMaximum = Vector3.Max(freeMaximum, freeWrist.position);
            minimumHeldReach = Mathf.Min(minimumHeldReach, idleHeldWrist.z - model.transform.InverseTransformPoint(heldWrist.position).z);
            minAnkleY = Mathf.Min(minAnkleY, feet.Min(foot => foot.position.y));
            maximumLowestFoot = Mathf.Max(maximumLowestFoot, feet.Select((foot, index) => foot.position.y - idleFeet[index].y).Min());
            if (leftLeg)
            {
                supportDrift = Mathf.Max(supportDrift, Vector3.Distance(feet[0].position, idleFeet[0]));
                Vector3 delta = model.transform.InverseTransformVector(feet[1].position - idleFeet[1]);
                frontTravel = Mathf.Max(frontTravel, new Vector2(delta.x, delta.y).magnitude);
            }
            if (stomp)
            {
                supportDrift = Mathf.Max(supportDrift, Vector3.Distance(feet[1].position, idleFeet[1]));
                float height = feet[0].position.y - idleFeet[0].y;
                stompLift = Mathf.Max(stompLift, height);
                bool above = height > 0.03f;
                if (above && !lifted) liftWindows++;
                lifted = above;
            }
            if (lead && !moving) supportDrift = Mathf.Max(supportDrift, feet.Select((foot, index) => Vector3.Distance(foot.position, idleFeet[index])).Max());
        }
        Require(step <= 8f, "A joint jumps more than 8 degrees at 60 FPS: " + step.ToString("F3"));
        Require(minAnkleY >= idleFeet.Min(position => position.y) - 0.015f, "An ankle goes below standing level.");
        for (int index = 0; index < all.Length; index++)
        {
            Require(Quaternion.Angle(all[index].localRotation, lead ? initialRotations[index] : idleRotations[index]) < 0.7f, "Rotation misses loop/Idle seam: " + all[index].name);
            Require(Vector3.Distance(all[index].localPosition, lead ? initialPositions[index] : idlePositions[index]) < 0.002f, "Position misses loop/Idle seam: " + all[index].name);
        }
        if (leftLeg)
        {
            Require(supportDrift < 0.002f, "The untouched right foot slides.");
            Require(frontTravel > (action == "LegShiftLeft" ? 0.040f : 0.085f), "Left-leg response is too small from the front camera.");
            Require(speed < 155f && acceleration < 1200f, "Left-leg path exceeds the existing touch speed/acceleration limits.");
        }
        if (lead)
        {
            Require(minimumHeldReach > 0.10f, "The selected hand does not reach forward.");
            if (moving)
            {
                Require(maximumLowestFoot < 0.012f, "Both supporting shoes lift during led locomotion.");
                float heldTravel = (heldMaximum - heldMinimum).magnitude, freeTravel = (freeMaximum - freeMinimum).magnitude;
                Require(freeTravel > 0.09f, "The free arm does not swing naturally.");
                Require(heldTravel < freeTravel * 0.55f + 0.025f, "The held hand swings like the free hand.");
            }
            else Require(supportDrift < 0.002f, "Held idle slides a planted shoe.");
        }
        if (stomp)
        {
            Require(clip.length <= 4.5f, "A single angry stomp is stretched into a slow motion.");
            Require(supportDrift < 0.002f, "Stomp slides its left support foot.");
            Require(stompLift > 0.09f && liftWindows == 1, "Stomp must visibly lift and plant the right foot exactly once.");
            clip.SampleAnimation(model, clip.length * 0.40f);
            foreach (string side in new[] { "R", "L" })
            {
                Transform[] fingers = new[] { "人指１", "中指１", "薬指１", "小指１" }.Select(name => name + "." + side)
                    .Where(bones.ContainsKey).Select(name => bones[name]).ToArray();
                Require(fingers.Length >= 2, "Cannot verify fist without articulated fingers: " + side);
                Require(fingers.All(finger => Quaternion.Angle(finger.localRotation, idleRotations[Array.IndexOf(all, finger)]) > 20f), "A hand does not close into a fist: " + side);
                Require(Quaternion.Angle(bones["ひじ." + side].localRotation, idleRotations[Array.IndexOf(all, bones["ひじ." + side])]) > 4f, "The arm does not tense with the fist: " + side);
            }
            clip.SampleAnimation(model, clip.length);
        }
        report.AppendLine($"PASS {path}/{action}: duration={clip.length:F3}s, step60fps={step:F3}deg, speed={speed:F2}deg/s, acceleration={acceleration:F1}deg/s2, supportDrift={supportDrift:F6}m, frontLegTravel={frontTravel:F4}m, stompLift={stompLift:F4}m, heldTravel={(heldMaximum - heldMinimum).magnitude:F4}m, freeTravel={(freeMaximum - freeMinimum).magnitude:F4}m.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
