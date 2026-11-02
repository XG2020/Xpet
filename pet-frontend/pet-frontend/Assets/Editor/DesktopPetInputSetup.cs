using System;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Adds only the opt-in microphone shortcut group to the existing voice settings page.</summary>
public static class DesktopPetInputSetup
{
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Ink = new Color32(238, 244, 251, 255);
    private static readonly Color Muted = new Color32(173, 187, 205, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);
    private static TMP_FontAsset font;
    private static Sprite round;

    [MenuItem("Desktop Pet/Add Push To Talk Settings")]
    public static void UpgradePushToTalkSettings()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before adding microphone shortcut settings.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath)
            throw new InvalidOperationException("Open the existing DesktopPet.unity scene first.");
        GameObject[] roots = scene.GetRootGameObjects();
        PetBackendHttpClient client = roots.SelectMany(root => root.GetComponentsInChildren<PetBackendHttpClient>(true)).Single();
        SettingsTabsController tabs = roots.SelectMany(root => root.GetComponentsInChildren<SettingsTabsController>(true)).Single();
        RectTransform content = tabs.FindPageContent("语音");
        if (content == null || content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>() == null)
            throw new InvalidOperationException("The existing voice settings page and its vertical layout were not found.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
        round = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/DesktopPetUIRound.png");
        if (font == null) throw new InvalidOperationException("The existing DesktopPetUIFont asset was not found.");

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Add opt-in push to talk settings");
        DesktopPetInteraction interaction = Ensure<DesktopPetInteraction>(client.gameObject);
        DesktopPetPushToTalk pushToTalk = Ensure<DesktopPetPushToTalk>(client.gameObject);
        Set(pushToTalk, "interaction", interaction);
        BuildSettings(content, pushToTalk);
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(content);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Unable to save push to talk settings.");
        Debug.Log("DESKTOP_PET_PUSH_TO_TALK_SETTINGS_ADDED: voice page only, opt-in default off, Ctrl+Alt+V default. No microphone or PlayerPrefs were accessed.");
    }

    private static void BuildSettings(RectTransform content, DesktopPetPushToTalk component)
    {
        RectTransform group = FindOrCreate("PushToTalkSettings", content);
        var vertical = Ensure<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        Undo.RecordObject(vertical, "Configure microphone shortcut layout");
        vertical.spacing = 8;
        vertical.padding = new RectOffset(0, 0, 10, 10);
        vertical.childControlWidth = vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;
        Label("Title", group, "按住说话", 19, Ink, 30);

        RectTransform toggleRoot = FindOrCreate("PushToTalkEnabled", group);
        Height(toggleRoot, 34);
        var toggle = Ensure<UnityEngine.UI.Toggle>(toggleRoot.gameObject);
        Paint(toggleRoot, Color.clear).raycastTarget = true;
        RectTransform box = FindOrCreate("Box", toggleRoot);
        box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
        box.pivot = new Vector2(0, 0.5f);
        box.sizeDelta = new Vector2(22, 22);
        box.anchoredPosition = Vector2.zero;
        var background = Paint(box, Field);
        RectTransform mark = FindOrCreate("Check", box);
        Stretch(mark, 5);
        var checkedImage = Paint(mark, Accent);
        checkedImage.raycastTarget = false;
        toggle.targetGraphic = background;
        toggle.graphic = checkedImage;
        toggle.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        toggle.SetIsOnWithoutNotify(false);
        TMP_Text toggleLabel = Text("Label", toggleRoot, "启用录音组合键（默认关闭）", 15, Ink);
        Stretch(toggleLabel.rectTransform);
        toggleLabel.rectTransform.offsetMin = new Vector2(32, 0);
        toggleLabel.alignment = TextAlignmentOptions.MidlineLeft;

        TMP_Text shortcut = Label("Shortcut", group, "当前组合键：" + DesktopPetPushToTalk.DefaultShortcut, 16, Ink, 28);
        RectTransform buttonRoot = FindOrCreate("SetShortcut", group);
        Height(buttonRoot, 42);
        var button = Ensure<UnityEngine.UI.Button>(buttonRoot.gameObject);
        button.targetGraphic = Paint(buttonRoot, Field);
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        TMP_Text buttonLabel = Text("Label", buttonRoot, "设置按键", 15, Ink);
        Stretch(buttonLabel.rectTransform, 8);
        buttonLabel.alignment = TextAlignmentOptions.Center;
        Label("Usage", group,
            "按住组合键录音，松开任一键后识别发送，最长 30 秒。宠物不在前台时也可使用；在宠物输入框内打字时暂停。", 13, Muted, 62);
        Label("KeyHelp", group,
            "设置时按 Ctrl 或 Alt + 字母、数字、F2–F12 或空格，可再加 Shift；不支持 Windows 键。组合键仍会传给当前应用。", 13, Muted, 62);
        TMP_Text status = Label("Status", group, "已关闭 · 开启后才响应录音组合键。", 13, Muted, 54);
        Set(component, "enabledToggle", toggle);
        Set(component, "setShortcutButton", button);
        Set(component, "shortcutText", shortcut);
        Set(component, "statusText", status);
    }

    [MenuItem("Desktop Pet/Run Shortcut Logic Regression")]
    public static void RunShortcutLogicRegression()
    {
        var report = new StringBuilder();
        Check(DesktopPetPushToTalk.TryParseShortcut("Ctrl+Alt+V", out var shortcut, out _), "Default chord parses.");
        Check(shortcut.ToString() == "Ctrl+Alt+V" && shortcut.VirtualKey == 'V', "Default chord round trips.");
        Check(DesktopPetPushToTalk.TryParseShortcut(" shift + alt + control + f8 ", out var edited, out _)
            && edited.ToString() == "Ctrl+Alt+Shift+F8", "Edited chord normalizes.");
        Check(DesktopPetPushToTalk.TryParseShortcut("Alt+0", out _, out _)
            && DesktopPetPushToTalk.TryParseShortcut("Ctrl+Space", out _, out _), "Digits and Space parse.");
        foreach (string invalid in new[] { "", "V", "Shift+V", "Ctrl", "Win+V", "Ctrl+Ctrl+V", "Alt+F13", "Ctrl+A+B", "Ctrl++V", "Ctrl+Esc" })
            Check(!DesktopPetPushToTalk.TryParseShortcut(invalid, out _, out _), "Reject invalid chord: " + invalid);
        Check(shortcut.Matches(true, true, true, false, false), "Complete chord matches.");
        Check(!shortcut.Matches(true, true, false, false, false) && !shortcut.Matches(false, true, true, false, false), "Main key and required modifiers must remain held.");
        Check(!shortcut.Matches(true, true, true, true, false) && !shortcut.Matches(true, true, true, false, true), "Extra modifiers and Windows prevent triggering.");
        var gate = new DesktopPetPushToTalk.HoldGate();
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None, "Enabling while held does not record.");
        gate.Step(false, true, false);
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.Start, "Fresh chord starts once.");
        gate.MarkStarted(true);
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None, "Holding does not repeat.");
        Check(gate.Step(false, false, false) == DesktopPetPushToTalk.HoldAction.Finish, "Releasing a modifier finishes.");
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None, "Partial release cannot restart recording.");
        gate.Step(false, true, false);
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.Start, "Fully released chord can start again.");
        gate.MarkStarted(false);
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None
            && gate.Step(false, false, false) == DesktopPetPushToTalk.HoldAction.None
            && gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None, "Failed start waits for full release.");
        gate.Step(false, true, false);
        gate.Step(true, false, false);
        gate.MarkStarted(true);
        Check(gate.Step(true, false, true) == DesktopPetPushToTalk.HoldAction.Finish, "Typing/configuration block finishes owned recording.");
        Check(gate.Step(true, false, false) == DesktopPetPushToTalk.HoldAction.None, "Leaving a block with held keys cannot record.");
        gate.Reset();
        Check(!gate.OwnsRecording, "Disable/quit resets ownership.");
        report.AppendLine("PASSED: shortcut parsing, exact modifier matching, enable/capture/typing release gates, single start/release, failed-start suppression, ownership reset.");
        report.AppendLine("Pure logic only. No microphone, keyboard polling, PlayerPrefs, ASR, or scene writes. Real microphone/global-key behavior still needs user testing.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "input-shortcut-regression.txt"), report.ToString(), new UTF8Encoding(false));
        Debug.Log("DESKTOP_PET_SHORTCUT_LOGIC_REGRESSION " + report);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shortcut regression: " + message);
    }

    private static T Ensure<T>(GameObject target) where T : Component
    {
        T existing = target.GetComponent<T>();
        return existing != null ? existing : Undo.AddComponent<T>(target);
    }

    private static RectTransform FindOrCreate(string name, Transform parent)
    {
        var existing = parent.Find(name) as RectTransform;
        if (existing != null) return existing;
        var child = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(child, "Add " + name);
        child.transform.SetParent(parent, false);
        return (RectTransform)child.transform;
    }

    private static UnityEngine.UI.Image Paint(RectTransform root, Color color)
    {
        var image = Ensure<UnityEngine.UI.Image>(root.gameObject);
        Undo.RecordObject(image, "Style " + root.name);
        image.sprite = round;
        image.type = UnityEngine.UI.Image.Type.Sliced;
        image.color = color;
        return image;
    }

    private static TMP_Text Text(string name, Transform parent, string value, float size, Color color)
    {
        RectTransform rect = FindOrCreate(name, parent);
        TMP_Text text = Ensure<TextMeshProUGUI>(rect.gameObject);
        Undo.RecordObject(text, "Configure " + name);
        text.font = font; text.fontSize = size; text.color = color; text.text = value;
        text.raycastTarget = false; text.richText = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }

    private static TMP_Text Label(string name, Transform parent, string value, float size, Color color, float height)
    {
        TMP_Text text = Text(name, parent, value, size, color);
        Height(text.rectTransform, height);
        return text;
    }

    private static void Height(RectTransform rect, float height)
    {
        var element = Ensure<UnityEngine.UI.LayoutElement>(rect.gameObject);
        Undo.RecordObject(element, "Size " + rect.name);
        element.minHeight = element.preferredHeight = height;
    }

    private static void Stretch(RectTransform rect, float inset = 0)
    {
        Undo.RecordObject(rect, "Stretch " + rect.name);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset);
    }

    private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target);
        SerializedProperty field = serialized.FindProperty(property);
        if (field == null) throw new MissingFieldException(target.GetType().Name, property);
        field.objectReferenceValue = value;
        serialized.ApplyModifiedProperties();
    }
}
