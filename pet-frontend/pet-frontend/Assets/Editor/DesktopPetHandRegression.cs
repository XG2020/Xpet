using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Samples generated hands and the boundary reaction on disposable preview objects.</summary>
public static class DesktopPetHandRegression
{
    [MenuItem("Desktop Pet/Run Hand and Guard Regression")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before sampling hand clips.");
        var report = new StringBuilder();
        var failures = new List<string>();
        var preview = EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PetModelSetup.PrefabPath);
            Require(source != null, "Generate Lita assets first.");
            foreach (string action in new[] { "Idle", "Wave", "Happy", "Think", "Cheer", "Shy", "Stretch", "Drag", "Guard", "GuardLower" })
            {
                GameObject model = null;
                try
                {
                    model = UnityEngine.Object.Instantiate(source);
                    model.SetActive(false);
                    model.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, preview);
                    CheckHands(model, action, report);
                    if (action == "Guard") CheckGuard(model, report);
                    report.AppendLine("PASS: " + action);
                }
                catch (Exception error) { failures.Add(action + ": " + error.GetBaseException().Message); }
                finally { if (model != null) UnityEngine.Object.DestroyImmediate(model); }
            }
        }
        catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("Numerical clip checks only; inspect the rendered palms, clothing clearance, and transition quality separately.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "hand-guard-regression.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("DESKTOP_PET_HAND_GUARD_REGRESSION " + report);
        else Debug.LogError("DESKTOP_PET_HAND_GUARD_REGRESSION " + report);
    }

    private static AnimationClip Clip(string action)
    {
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(PetModelSetup.ModelFolder + "/PetGenerated/" + action + ".anim");
        Require(clip != null, "Missing generated clip: " + action);
        return clip;
    }

    private static Dictionary<string, Transform> Bones(GameObject model)
    {
        return model.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
    }

    private static void CheckHands(GameObject model, string action, StringBuilder report)
    {
        AnimationClip clip = Clip(action);
        Dictionary<string, Transform> bones = Bones(model);
        var fingerNames = new List<string>();
        foreach (string side in new[] { "R", "L" })
            foreach (string finger in new[] { "人指", "中指", "薬指", "小指", "親指" })
                for (int joint = 0; joint < 3; joint++) fingerNames.Add(finger + (char)('０' + joint + (finger == "親指" ? 0 : 1)) + "." + side);
        var bindingPaths = new HashSet<string>(AnimationUtility.GetCurveBindings(clip)
            .Where(binding => binding.type == typeof(Transform) && binding.propertyName == "m_LocalRotation.w").Select(binding => binding.path));
        foreach (string name in fingerNames)
            Require(bones.ContainsKey(name) && bindingPaths.Contains(AnimationUtility.CalculateTransformPath(bones[name], model.transform)),
                "Finger joint has no animation curve: " + name);
        var trackedNames = fingerNames.Concat(new[] { "手首.R", "手首.L", "手捩.R", "手捩.L", "手捩1.R", "手捩2.R", "手捩3.R", "手捩1.L", "手捩2.L", "手捩3.L" }).ToArray();
        clip.SampleAnimation(model, 0);
        var previous = trackedNames.ToDictionary(name => name, name => bones[name].localRotation);
        float maxStep = 0, maxWristBend = 0, maxStepTime = 0;
        string maxStepBone = "";
        for (int frame = 0; frame <= 120; frame++)
        {
            clip.SampleAnimation(model, clip.length * frame / 120f);
            foreach (string name in trackedNames)
            {
                Quaternion current = bones[name].localRotation;
                Require(!float.IsNaN(current.x + current.y + current.z + current.w), "Non-finite hand rotation: " + name);
                float step = Quaternion.Angle(previous[name], current);
                if (step > maxStep) { maxStep = step; maxStepBone = name; maxStepTime = clip.length * frame / 120f; }
                previous[name] = current;
            }
            foreach (string side in new[] { "R", "L" })
            {
                Vector3 forward = bones["中指１." + side].position - bones["手首." + side].position;
                Vector3 forearm = bones["手首." + side].position - bones["ひじ." + side].position;
                maxWristBend = Mathf.Max(maxWristBend, Vector3.Angle(forward, forearm));
            }
        }
        report.AppendLine($"{action} continuity: largest step={maxStep:F3} deg, bone={maxStepBone}, time={maxStepTime:F4} s (previous={maxStepTime - clip.length / 120f:F4} s).");
        Require(maxStep < 40f, $"A hand/finger/twist joint snaps between samples: {maxStepBone}, {maxStepTime:F4} s, {maxStep:F3} deg.");
        Require(maxWristBend < 55f, "A wrist bends more than 55 degrees from its forearm.");
        clip.SampleAnimation(model, clip.length * 0.5f);
        float minimumPalmDot = 1;
        foreach (string side in new[] { "R", "L" })
        {
            float sign = side == "R" ? -1 : 1;
            Vector3 forward = (bones["中指１." + side].position - bones["手首." + side].position).normalized;
            Vector3 spread = bones["人指１." + side].position - bones["小指１." + side].position;
            Vector3 palm = Vector3.Cross(forward, spread).normalized * -sign;
            Vector3 expected = model.transform.TransformDirection(new Vector3(-sign, 0.04f, -0.12f));
            if (action == "Wave" && side == "R" || action == "Stretch") expected = -model.transform.forward;
            else if (action == "Cheer" || action == "Happy" || action == "Think" && side == "R") expected = model.transform.TransformDirection(new Vector3(-sign * 0.35f, 0, 1));
            else if (action == "Shy") expected = model.transform.TransformDirection(new Vector3(-sign * 0.2f, 0, 1));
            else if (action == "Guard" || action == "GuardLower") expected = model.transform.forward;
            else if (action == "Drag") expected = model.transform.TransformDirection(new Vector3(-sign * 0.35f, -0.45f, -0.5f));
            expected = Vector3.ProjectOnPlane(expected, forward).normalized;
            minimumPalmDot = Mathf.Min(minimumPalmDot, Vector3.Dot(palm, expected));
            Transform first = bones["中指１." + side], second = bones["中指２." + side], third = bones["中指３." + side];
            float curl = Vector3.Angle(second.position - first.position, third.position - second.position);
            Require(curl > 2f, "Middle finger remains completely rigid: " + side);
        }
        Require(minimumPalmDot > 0.93f, "Palm faces away from its intended gesture direction.");
        report.AppendLine($"{action}: 30 finger joints keyed; palm agreement={minimumPalmDot:F4}, wrist bend={maxWristBend:F2} deg, largest sample step={maxStep:F2} deg.");
    }

    private static void CheckGuard(GameObject model, StringBuilder report)
    {
        AnimationClip clip = Clip("Guard"), idle = Clip("Idle");
        Require(clip.length >= 2.999f && !AnimationUtility.GetAnimationClipSettings(clip).loopTime,
            "Guard must be a one-shot at least three seconds long, including any retiming needed for smooth hands.");
        var controller = model.GetComponent<Animator>().runtimeAnimatorController as AnimatorController;
        AnimatorState state = controller.layers[0].stateMachine.states.First(child => child.state.name == "Guard").state;
        Require(state.motion == clip && state.transitions.Any(transition => transition.destinationState != null
            && transition.destinationState.name == "Idle" && transition.hasExitTime && Mathf.Abs(transition.exitTime - 1) < 0.001f),
            "Guard does not automatically return to Idle.");
        var bones = Bones(model);
        Transform[] all = model.GetComponentsInChildren<Transform>(true);
        idle.SampleAnimation(model, 0);
        var idleRotations = all.ToDictionary(bone => bone, bone => bone.localRotation);
        Vector3 idleCenter = bones["センター"].position;
        Vector3 rootPosition = model.transform.position;
        Quaternion rootRotation = model.transform.rotation;
        string[] feet = { "足首.R", "足首.L", "足首D.R", "足首D.L" };
        var footPositions = feet.ToDictionary(name => name, name => bones[name].position);
        var footRotations = feet.ToDictionary(name => name, name => bones[name].rotation);
        float drift = 0, footTilt = 0, retreat = 0;
        for (int frame = 0; frame <= 120; frame++)
        {
            clip.SampleAnimation(model, clip.length * frame / 120f);
            Require(Vector3.Distance(rootPosition, model.transform.position) < 0.00001f && Quaternion.Angle(rootRotation, model.transform.rotation) < 0.01f,
                "Guard moved the character root.");
            retreat = Mathf.Max(retreat, Vector3.Dot(bones["センター"].position - idleCenter, model.transform.forward));
            foreach (string name in feet)
            {
                drift = Mathf.Max(drift, Vector3.Distance(footPositions[name], bones[name].position));
                footTilt = Mathf.Max(footTilt, Quaternion.Angle(footRotations[name], bones[name].rotation));
            }
            if (frame == 0 || frame == 120)
            {
                Require(Vector3.Distance(idleCenter, bones["センター"].position) < 0.0001f, "Guard has a pelvis jump at its Idle seam.");
                foreach (Transform bone in all) Require(Quaternion.Angle(idleRotations[bone], bone.localRotation) < 0.15f, "Guard does not meet Idle: " + bone.name);
            }
        }
        Require(Mathf.Abs(retreat - 0.065f) < 0.001f, "Guard is missing its 6.5 cm skeleton retreat.");
        Require(drift < 0.001f && footTilt < 0.1f, "Guard feet slide or rotate.");
        clip.SampleAnimation(model, clip.length * 0.5f);
        Vector3 right = model.transform.InverseTransformPoint(bones["手首.R"].position);
        Vector3 left = model.transform.InverseTransformPoint(bones["手首.L"].position);
        Vector3 chest = model.transform.InverseTransformPoint(bones["上半身2"].position);
        Require(Mathf.Abs(right.x) < 0.18f && Mathf.Abs(left.x) < 0.18f && right.y - left.y > 0.1f,
            "Guard forearms do not cover two staggered torso levels.");
        Require(right.z < chest.z - 0.05f && left.z < chest.z - 0.05f, "Guard hands are not in front of the torso.");
        report.AppendLine($"Guard: root fixed, retreat={retreat:F4} m, foot drift={drift:F6} m, foot tilt={footTilt:F4} deg; hands R={right}, L={left}; Idle seams match.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
