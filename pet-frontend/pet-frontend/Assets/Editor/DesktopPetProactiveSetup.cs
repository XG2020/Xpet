using System;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Adds only the opt-in screen companion group to the existing Tools page.</summary>
public static class DesktopPetProactiveSetup
{
    private static TMP_FontAsset font;
    private static Sprite round;
    private static readonly Color Ink = new Color32(234, 241, 248, 255);
    private static readonly Color Muted = new Color32(160, 177, 194, 255);
    private static readonly Color Field = new Color32(22, 31, 45, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);

    [MenuItem("Desktop Pet/Add Proactive Screen Settings")]
    public static void Upgrade()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath) throw new InvalidOperationException("Open DesktopPet.unity first.");
        var roots = scene.GetRootGameObjects();
        var client = roots.SelectMany(root => root.GetComponentsInChildren<PetBackendHttpClient>(true)).Single();
        var api = roots.SelectMany(root => root.GetComponentsInChildren<BackendSettingsApi>(true)).Single();
        var panel = roots.SelectMany(root => root.GetComponentsInChildren<BackendSettingsPanel>(true)).Single();
        var tabs = roots.SelectMany(root => root.GetComponentsInChildren<SettingsTabsController>(true)).Single();
        var tools = tabs.FindPageContent("工具");
        if (tools == null) throw new InvalidOperationException("The Tools settings page is missing.");
        var canvas = tabs.GetComponentInParent<Canvas>(true);
        if (canvas == null || canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            throw new InvalidOperationException("The existing settings Canvas raycaster is missing.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
        round = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/DesktopPetUIRound.png");
        if (font == null) throw new InvalidOperationException("The existing UI font is missing.");
        Undo.IncrementCurrentGroup();
        int undo = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Add opt-in proactive screen companion");
        var component = Ensure<DesktopPetProactiveScreen>(client.gameObject);
        Set(component, "backendClient", client);
        Set(component, "settingsApi", api);
        Set(component, "interaction", client.GetComponent<DesktopPetInteraction>());
        Set(component, "systemController", client.GetComponent<DesktopPetSystemController>());
        Set(component, "settingsController", roots.SelectMany(root => root.GetComponentsInChildren<SettingsPanelController>(true)).Single());
        Set(component, "chatRoot", canvas.transform.Find("ChatPanel")?.gameObject);

        RectTransform group = Child("ProactiveScreenSettings", tools);
        var layout = Ensure<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        Undo.RecordObject(layout, "Configure screen companion group");
        layout.spacing = 8; layout.padding = new RectOffset(0, 0, 10, 10);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        Label("Title", group, "屏幕陪伴", 19, Ink, 30);
        RectTransform toggleRoot = Child("ProactiveScreenEnabled", group);
        Height(toggleRoot, 36);
        var toggle = Ensure<UnityEngine.UI.Toggle>(toggleRoot.gameObject);
        Paint(toggleRoot, Color.clear).raycastTarget = true;
        RectTransform box = Child("Box", toggleRoot);
        box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
        box.pivot = new Vector2(0, 0.5f); box.sizeDelta = new Vector2(22, 22);
        var background = Paint(box, Field);
        RectTransform check = Child("Check", box); Stretch(check, 5);
        var mark = Paint(check, Accent); mark.raycastTarget = false;
        Undo.RecordObject(toggle, "Configure proactive toggle");
        toggle.targetGraphic = background; toggle.graphic = mark;
        toggle.SetIsOnWithoutNotify(false);
        TMP_Text toggleLabel = Text("Label", toggleRoot, "允许大模型主动窥屏（默认关闭）", 15, Ink);
        Stretch(toggleLabel.rectTransform); toggleLabel.rectTransform.offsetMin = new Vector2(32, 0);
        toggleLabel.alignment = TextAlignmentOptions.MidlineLeft;
        Label("PrivacyHint", group, "开启后，主屏幕内容会发送到已配置的视觉模型。只在空闲时查看，模型可选择安静陪伴；画面不保存。关闭立即停止，开关自动保存。", 13, Muted, 68);
        Label("IntervalLabel", group, "查看间隔（30～900 秒，默认 90 秒）", 15, Ink, 26);
        RectTransform inputRoot = Child("ProactiveScreenInterval", group); Height(inputRoot, 42);
        var input = Ensure<TMP_InputField>(inputRoot.gameObject);
        input.targetGraphic = Paint(inputRoot, Field);
        RectTransform viewport = Child("TextViewport", inputRoot); Stretch(viewport, 10);
        Ensure<UnityEngine.UI.RectMask2D>(viewport.gameObject);
        TMP_Text value = Text("Text", viewport, "90", 16, Ink); Stretch(value.rectTransform);
        TMP_Text placeholder = Text("Placeholder", viewport, "90", 16, Muted); Stretch(placeholder.rectTransform);
        value.alignment = placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        Undo.RecordObject(input, "Configure observation interval");
        input.fontAsset = font; input.textViewport = viewport; input.textComponent = value; input.placeholder = placeholder;
        input.contentType = TMP_InputField.ContentType.IntegerNumber; input.characterLimit = 3;
        input.lineType = TMP_InputField.LineType.SingleLine; input.SetTextWithoutNotify("90");
        Label("IntervalHint", group, "修改间隔后点击保存。隐藏宠物、打开聊天或设置、录音及回复期间暂停查看。", 13, Muted, 42);
        Set(panel, "enableProactiveScreenToggle", toggle);
        Set(panel, "proactiveScreenIntervalInput", input);
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(tools);
        Undo.CollapseUndoOperations(undo);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the proactive UI setup.");
        Debug.Log("DESKTOP_PET_PROACTIVE_SETUP: Tools controls and idle-only component installed. The switch remains off; no backend or screen capture was contacted.");
    }

    [MenuItem("Desktop Pet/Validate Proactive Screen Setup")]
    public static void Validate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        var components = roots.SelectMany(root => root.GetComponentsInChildren<DesktopPetProactiveScreen>(true)).ToArray();
        if (components.Length != 1) throw new InvalidOperationException("Expected one proactive screen component.");
        var panel = roots.SelectMany(root => root.GetComponentsInChildren<BackendSettingsPanel>(true)).Single();
        var serialized = new SerializedObject(panel);
        var toggle = serialized.FindProperty("enableProactiveScreenToggle").objectReferenceValue as UnityEngine.UI.Toggle;
        var input = serialized.FindProperty("proactiveScreenIntervalInput").objectReferenceValue as TMP_InputField;
        if (toggle == null || input == null || toggle.isOn || input.text != "90")
            throw new InvalidOperationException("Proactive controls must be bound and default off / 90 seconds.");
        if (new RuntimeSettingsData().enable_proactive_screen) throw new InvalidOperationException("Settings data defaults to enabled.");
        if (toggle.GetComponentInParent<SettingsTabsController>(true)?.FindPageContent("工具") == null)
            throw new InvalidOperationException("Proactive controls are not in tabbed settings.");
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "proactive-screen-setup.txt");
        File.WriteAllText(path, "PASSED\nOne component, bound Tools controls, default off, interval 90 seconds.\nNo backend, microphone, screenshot, or PlayerPrefs access.\n", new UTF8Encoding(false));
        Debug.Log("DESKTOP_PET_PROACTIVE_VALIDATION: PASSED. " + path);
    }

    private static T Ensure<T>(GameObject target) where T : Component => target.GetComponent<T>() ?? Undo.AddComponent<T>(target);
    private static RectTransform Child(string name, Transform parent)
    {
        if (parent.Find(name) is RectTransform existing) return existing;
        var item = new GameObject(name, typeof(RectTransform)); Undo.RegisterCreatedObjectUndo(item, "Add " + name);
        item.transform.SetParent(parent, false); return (RectTransform)item.transform;
    }
    private static UnityEngine.UI.Image Paint(RectTransform root, Color color)
    {
        var image = Ensure<UnityEngine.UI.Image>(root.gameObject); Undo.RecordObject(image, "Style " + root.name);
        image.sprite = round; image.type = UnityEngine.UI.Image.Type.Sliced; image.color = color; return image;
    }
    private static TMP_Text Text(string name, Transform parent, string text, float size, Color color)
    {
        var result = Ensure<TextMeshProUGUI>(Child(name, parent).gameObject); Undo.RecordObject(result, "Configure " + name);
        result.font = font; result.fontSize = size; result.text = text; result.color = color;
        result.raycastTarget = false; result.richText = false; result.textWrappingMode = TextWrappingModes.Normal; return result;
    }
    private static void Label(string name, Transform parent, string text, float size, Color color, float height)
    { Height(Text(name, parent, text, size, color).rectTransform, height); }
    private static void Height(RectTransform target, float height)
    { var element = Ensure<UnityEngine.UI.LayoutElement>(target.gameObject); element.minHeight = element.preferredHeight = height; }
    private static void Stretch(RectTransform target, float inset = 0)
    { target.anchorMin = Vector2.zero; target.anchorMax = Vector2.one; target.offsetMin = Vector2.one * inset; target.offsetMax = Vector2.one * -inset; }
    private static void Set(UnityEngine.Object target, string name, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target); var property = serialized.FindProperty(name);
        if (property == null) throw new MissingFieldException(target.GetType().Name, name);
        property.objectReferenceValue = value; serialized.ApplyModifiedProperties();
    }
}
