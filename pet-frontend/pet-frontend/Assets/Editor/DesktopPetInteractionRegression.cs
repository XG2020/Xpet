using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Offline Edit Mode checks against disposable objects in a preview scene.</summary>
public static class DesktopPetInteractionRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [MenuItem("Desktop Pet/Run Interaction Regression")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before running interaction regression.");
        var report = new StringBuilder();
        var failures = new List<string>();
        Scene activeScene = SceneManager.GetActiveScene();
        Scene preview = default;
        GameObject sandbox = null;
        try
        {
            preview = EditorSceneManager.NewPreviewScene();
            sandbox = new GameObject("Interaction Regression") { hideFlags = HideFlags.HideAndDontSave };
            sandbox.SetActive(false);
            SceneManager.MoveGameObjectToScene(sandbox, preview);
            RunCase("Idle breathing", () => CheckIdle(sandbox.transform, report), report, failures);
            foreach (string action in new[] { "Nod", "TiltHead", "Shy", "Stretch", "Cheer", "LookAround" })
                RunCase(action, () => CheckStandingGesture(sandbox.transform, action, report), report, failures);
            RunCase("Chat submission visibility", () => CheckChat(sandbox.transform, report), report, failures);
        }
        catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        finally
        {
            if (sandbox != null) UnityEngine.Object.DestroyImmediate(sandbox);
            if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
        }
        if (SceneManager.GetActiveScene() != activeScene) failures.Add("The active scene changed.");
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("Only disposable preview objects were sampled. No backend client or PlayerPrefs were used.");
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "interaction-regression.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("DESKTOP_PET_INTERACTION_REGRESSION " + report + "Report: " + path);
        else Debug.LogError("DESKTOP_PET_INTERACTION_REGRESSION " + report + "Report: " + path);
    }

    private static void RunCase(string name, Action check, StringBuilder report, List<string> failures)
    {
        try { check(); report.AppendLine("PASS: " + name); }
        catch (Exception error) { failures.Add(name + ": " + error.GetBaseException().Message); }
    }

    private static void CheckIdle(Transform parent, StringBuilder report)
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PetModelSetup.PrefabPath);
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(PetModelSetup.ModelFolder + "/PetGenerated/Idle.anim");
        Require(source != null && clip != null, "Generate the Lita prefab and Idle clip first.");
        Require(Mathf.Abs(clip.length - 4f) < 0.001f && AnimationUtility.GetAnimationClipSettings(clip).loopTime,
            "Idle must be a four-second loop.");
        GameObject model = UnityEngine.Object.Instantiate(source, parent, false);
        model.hideFlags = HideFlags.HideAndDontSave;
        var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        string[] steadyNames = { "腰", "下半身", "上半身", "上半身2", "首", "頭" };
        string[] footNames = { "足首.R", "足首.L", "足首D.R", "足首D.L" };
        foreach (string name in steadyNames.Concat(footNames).Append("センター"))
            Require(bones.ContainsKey(name), "Missing bone: " + name);
        Transform center = bones["センター"];
        clip.SampleAnimation(model, 0);
        Vector3 firstCenter = model.transform.InverseTransformPoint(center.position);
        var rotations = steadyNames.ToDictionary(name => name, name => bones[name].rotation);
        var feet = footNames.ToDictionary(name => name, name => bones[name].position);
        var footRotations = footNames.ToDictionary(name => name, name => bones[name].rotation);
        float minY = firstCenter.y, maxY = firstCenter.y, lateral = 0, tilt = 0, footDrift = 0, footTilt = 0;
        // Include interpolated frames as well as every generated key and the loop seam.
        for (int frame = 0; frame <= 120; frame++)
        {
            clip.SampleAnimation(model, frame / 30f);
            Vector3 position = model.transform.InverseTransformPoint(center.position);
            minY = Mathf.Min(minY, position.y); maxY = Mathf.Max(maxY, position.y);
            lateral = Mathf.Max(lateral, Mathf.Abs(position.x - firstCenter.x), Mathf.Abs(position.z - firstCenter.z));
            foreach (string name in steadyNames) tilt = Mathf.Max(tilt, Quaternion.Angle(rotations[name], bones[name].rotation));
            foreach (string name in footNames)
            {
                footDrift = Mathf.Max(footDrift, Vector3.Distance(feet[name], bones[name].position));
                footTilt = Mathf.Max(footTilt, Quaternion.Angle(footRotations[name], bones[name].rotation));
            }
        }
        report.AppendLine($"Idle: 121 samples, vertical range={maxY - minY:F6} m, lateral drift={lateral:F6} m, torso/head rotation={tilt:F4} deg, foot drift={footDrift:F6} m, foot rotation={footTilt:F4} deg.");
        Require(lateral < 0.0001f, "Idle center moves sideways or forwards.");
        Require(Mathf.Abs((maxY - minY) - 0.004f) < 0.0003f, "Idle breathing is not approximately 4 mm.");
        Require(tilt < 0.1f, "Idle torso/head rotation changes.");
        Require(footDrift < 0.001f && footTilt < 0.1f, "Idle feet do not stay planted.");
        Require(Vector3.Distance(firstCenter, model.transform.InverseTransformPoint(center.position)) < 0.0001f,
            "Idle center does not meet at the loop seam.");
    }

    private static void CheckStandingGesture(Transform parent, string action, StringBuilder report)
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PetModelSetup.PrefabPath);
        string folder = PetModelSetup.ModelFolder + "/PetGenerated/";
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + action + ".anim");
        AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + "Idle.anim");
        Require(source != null && clip != null && idle != null, "Generate all gesture clips first.");
        Require(clip.length >= 2.5f && clip.length <= 4f && !AnimationUtility.GetAnimationClipSettings(clip).loopTime,
            "Standing gesture must be a non-looping 2.5–4 second clip.");
        var controller = source.GetComponent<Animator>().runtimeAnimatorController as AnimatorController;
        Require(controller != null, "Missing generated Animator controller.");
        AnimatorState state = controller.layers[0].stateMachine.states.FirstOrDefault(child => child.state.name == action).state;
        Require(state != null && state.motion == clip && state.transitions.Any(transition => transition.destinationState != null
            && transition.destinationState.name == "Idle" && transition.hasExitTime && Mathf.Abs(transition.exitTime - 1) < 0.001f),
            "Gesture does not play its clip and automatically return to Idle.");
        GameObject model = UnityEngine.Object.Instantiate(source, parent, false);
        model.hideFlags = HideFlags.HideAndDontSave;
        Transform[] transforms = model.GetComponentsInChildren<Transform>(true);
        var bones = transforms.GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First());
        string[] feet = { "足首.R", "足首.L", "足首D.R", "足首D.L" };
        string[] hands = { "手首.R", "手首.L" };
        foreach (string name in feet.Concat(hands).Concat(new[] { "センター", "腰", "下半身", "頭", "上半身2" }))
            Require(bones.ContainsKey(name), "Missing bone: " + name);
        idle.SampleAnimation(model, 0);
        Quaternion[] idleRotations = transforms.Select(bone => bone.localRotation).ToArray();
        Vector3 idleCenter = bones["センター"].position;
        clip.SampleAnimation(model, 0);
        for (int index = 0; index < transforms.Length; index++)
            Require(Quaternion.Angle(idleRotations[index], transforms[index].localRotation) < 0.15f, "Gesture does not start in the relaxed Idle pose.");
        Require(Vector3.Distance(idleCenter, bones["センター"].position) < 0.0001f, "Gesture starts with a pelvis jump.");
        Vector3 firstCenter = model.transform.InverseTransformPoint(bones["センター"].position);
        var footPositions = feet.ToDictionary(name => name, name => bones[name].position);
        var handPositions = hands.ToDictionary(name => name, name => bones[name].position);
        Quaternion head = bones["頭"].rotation, chest = bones["上半身2"].rotation;
        Quaternion waist = bones["腰"].rotation, pelvis = bones["下半身"].rotation;
        float lateral = 0, footDrift = 0, headMotion = 0, chestMotion = 0, handMotion = 0, pelvisTilt = 0;
        for (int frame = 0; frame <= 120; frame++)
        {
            clip.SampleAnimation(model, clip.length * frame / 120f);
            Vector3 center = model.transform.InverseTransformPoint(bones["センター"].position);
            lateral = Mathf.Max(lateral, Mathf.Abs(center.x - firstCenter.x), Mathf.Abs(center.z - firstCenter.z));
            foreach (string name in feet) footDrift = Mathf.Max(footDrift, Vector3.Distance(footPositions[name], bones[name].position));
            foreach (string name in hands) handMotion = Mathf.Max(handMotion, Vector3.Distance(handPositions[name], bones[name].position));
            headMotion = Mathf.Max(headMotion, Quaternion.Angle(head, bones["頭"].rotation));
            chestMotion = Mathf.Max(chestMotion, Quaternion.Angle(chest, bones["上半身2"].rotation));
            pelvisTilt = Mathf.Max(pelvisTilt, Quaternion.Angle(waist, bones["腰"].rotation), Quaternion.Angle(pelvis, bones["下半身"].rotation));
        }
        float seamRotation = 0;
        for (int index = 0; index < transforms.Length; index++)
            seamRotation = Mathf.Max(seamRotation, Quaternion.Angle(idleRotations[index], transforms[index].localRotation));
        report.AppendLine($"{action}: {clip.length:F2} s, 121 samples, head={headMotion:F3} deg, chest={chestMotion:F3} deg, hands={handMotion:F4} m, lateral={lateral:F6} m, foot drift={footDrift:F6} m, pelvis tilt={pelvisTilt:F4} deg, seam={seamRotation:F4} deg.");
        Require(headMotion > 1f, "Gesture has no visible head expression.");
        Require(action == "Nod" || action == "LookAround" ? chestMotion > 0.3f : handMotion > 0.03f,
            "Gesture is missing its coordinated upper-body/hand movement.");
        Require(lateral < 0.0001f && pelvisTilt < 0.1f && footDrift < 0.001f, "Gesture moves the standing pelvis or feet.");
        Require(seamRotation < 0.15f && Vector3.Distance(idleCenter, bones["センター"].position) < 0.0001f,
            "Gesture does not finish in the relaxed Idle pose.");
    }

    private static void CheckChat(Transform parent, StringBuilder report)
    {
        GameObject holder = Child("Chat callback test", parent);
        PetChatPanel panel = holder.AddComponent<PetChatPanel>();
        TMP_Text output = Child("Reply text", holder.transform).AddComponent<TextMeshProUGUI>();
        Field("replyText").SetValue(panel, output);
        const string prompt = "摸了你的头部。";
        string expected = "";

        Submit(panel, prompt, "head");
        Call(panel, "HandleReplyDelta", "自动回应");
        Equal(output.text, "丽塔\n自动回应", "Automatic touch prompt leaked into the streaming transcript.");
        Call(panel, "HandleReplyText", "自动回应。");
        expected = "丽塔\n自动回应。";
        Equal(output.text, expected, "Automatic touch prompt leaked into the final transcript.");
        CheckReset(panel);

        Submit(panel, prompt);
        Call(panel, "HandleReplyDelta", "手动回应");
        Equal(output.text, expected + "\n\n你\n" + prompt + "\n\n丽塔\n手动回应", "Identical manually submitted text was hidden.");
        Call(panel, "HandleReplyText", "手动回应。");
        expected += "\n\n你\n" + prompt + "\n\n丽塔\n手动回应。";
        Equal(output.text, expected, "Manual submission was missing or duplicated on completion.");
        CheckReset(panel);

        Submit(panel, "没有收到回复的消息");
        Call(panel, "HandleRequestError", "模拟网络失败");
        Equal(output.text, expected, "Failure without deltas changed the committed transcript.");
        CheckReset(panel);
        Call(panel, "HandleReplyText", "后续独立回复。");
        expected += "\n\n丽塔\n后续独立回复。";
        Equal(output.text, expected, "A failed or previous submission was repeated by a later reply.");

        Submit(panel, "中断的手动消息");
        Call(panel, "HandleReplyDelta", "半段手动回复");
        Call(panel, "HandleRequestError", "模拟中断");
        expected += "\n\n你\n中断的手动消息\n\n丽塔 · 本轮未完成\n半段手动回复";
        Equal(output.text, expected, "Interrupted manual reply was missing or duplicated.");
        CheckReset(panel);
        Call(panel, "HandleRequestError", "再次报告同一失败");
        Equal(output.text, expected, "Repeated failure duplicated the partial reply.");

        Submit(panel, prompt, "head");
        Call(panel, "HandleReplyDelta", "半段自动回复");
        Equal(output.text, expected + "\n\n丽塔\n半段自动回复", "Previous manual text leaked into an automatic interaction.");
        Call(panel, "HandleRequestError", "模拟互动中断");
        expected += "\n\n丽塔 · 本轮未完成\n半段自动回复";
        Equal(output.text, expected, "Interrupted interaction exposed its automatic prompt.");
        CheckReset(panel);
        Submit(panel, "下一轮正常消息");
        Call(panel, "HandleReplyText", "下一轮正常回复。");
        expected += "\n\n你\n下一轮正常消息\n\n丽塔\n下一轮正常回复。";
        Equal(output.text, expected, "A previous round was duplicated after recovery.");
        CheckReset(panel);
        report.AppendLine("Chat: automatic prompts hidden in deltas/finals/failures; identical manual text visible; pending state cleared; interrupted and following rounds do not duplicate text.");
    }

    private static GameObject Child(string name, Transform parent)
    {
        var child = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        child.transform.SetParent(parent, false);
        return child;
    }

    private static void Submit(PetChatPanel panel, string message, string zone = null)
    {
        Call(panel, "HandleChatSubmitted", new PetChatSubmission { message = message, interaction_zone = zone });
    }

    private static void Call(PetChatPanel panel, string name, object value)
    {
        MethodInfo method = typeof(PetChatPanel).GetMethod(name, PrivateInstance);
        Require(method != null, "Missing chat callback: " + name);
        method.Invoke(panel, new[] { value });
    }

    private static FieldInfo Field(string name)
    {
        FieldInfo field = typeof(PetChatPanel).GetField(name, PrivateInstance);
        Require(field != null, "Missing chat field: " + name);
        return field;
    }

    private static void CheckReset(PetChatPanel panel)
    {
        Require((string)Field("lastSubmitted").GetValue(panel) == "" && (string)Field("partialReply").GetValue(panel) == ""
            && !(bool)Field("showSubmittedMessage").GetValue(panel) && !(bool)Field("busy").GetValue(panel),
            "Chat pending state survived completion or failure.");
    }

    private static void Equal(string actual, string expected, string message) { Require(actual == expected, message); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
