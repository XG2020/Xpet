using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Add the interaction components to the existing scene without rebuilding its UI.</summary>
public static class DesktopPetInteractionSetup
{
    private static readonly Color Card = new Color32(24, 33, 48, 255);
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Ink = new Color32(238, 244, 251, 255);
    private static readonly Color Muted = new Color32(173, 187, 205, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);
    private static TMP_FontAsset font;
    private static Sprite round;

    [MenuItem("Desktop Pet/Upgrade Interaction UX")]
    public static void UpgradeInteractionUX()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before upgrading the interaction UI.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath)
            throw new InvalidOperationException("Open the existing DesktopPet.unity scene first.");
        GameObject[] roots = scene.GetRootGameObjects();
        PetBackendHttpClient client = roots.SelectMany(root => root.GetComponentsInChildren<PetBackendHttpClient>(true)).Single();
        Canvas canvas = roots.SelectMany(root => root.GetComponentsInChildren<Canvas>(true))
            .FirstOrDefault(item => item.name == "DesktopPetCanvas");
        Transform character = roots.FirstOrDefault(root => root.name == "PetCharacterRoot")?.transform;
        RectTransform chat = canvas != null ? canvas.transform.Find("ChatPanel") as RectTransform : null;
        RectTransform settings = canvas != null ? canvas.transform.Find("SettingsOverlay/SettingsCard") as RectTransform : null;
        RectTransform content = settings != null ? settings.GetComponent<SettingsTabsController>()?.FindPageContent("语音") : null;
        if (content == null && settings != null) content = settings.Find("SettingsScroll/Viewport/Content") as RectTransform;
        if (canvas == null || character == null || chat == null || content == null)
            throw new InvalidOperationException("The existing character/chat/settings hierarchy was not found.");
        DesktopPetUIActions actions = client.GetComponent<DesktopPetUIActions>();
        if (actions == null) throw new InvalidOperationException("The existing DesktopPetUIActions component was not found.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
        round = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/DesktopPetUIRound.png");
        if (font == null) throw new InvalidOperationException("The existing DesktopPetUIFont asset was not found.");

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Upgrade desktop pet interaction UX");
        DesktopPetInteraction interaction = Ensure<DesktopPetInteraction>(client.gameObject);
        DesktopPetAppearance appearance = Ensure<DesktopPetAppearance>(client.gameObject);
        PetSpeechBubble bubble = Ensure<PetSpeechBubble>(client.gameObject);
        DesktopPetWakeWord wake = Ensure<DesktopPetWakeWord>(client.gameObject);
        Camera camera = roots.SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .FirstOrDefault(item => item.CompareTag("MainCamera"));
        Set(appearance, "characterRoot", character);
        Set(appearance, "worldCamera", camera);
        Set(appearance, "chatPanel", chat);
        Set(appearance, "settingsCard", settings);
        Set(appearance, "contextMenuRoot", canvas.transform.Find("ContextMenuRoot"));
        Set(bubble, "backendClient", client);
        Set(bubble, "appearance", appearance);
        Set(bubble, "uiActions", actions);
        Set(bubble, "chatPanel", chat.gameObject);
        Set(wake, "backendClient", client);
        Set(wake, "interaction", interaction);
        Set(wake, "speechBubble", bubble);
        BuildBubble((RectTransform)canvas.transform, bubble);
        BuildWakeSettings(content, wake);
        // A parent canvas raycaster is required by the bubble and the existing controls.
        Ensure<UnityEngine.UI.GraphicRaycaster>(canvas.gameObject);
        appearance.RefreshPlacementBounds();
        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Unable to save the interaction upgrade.");
        Debug.Log("DESKTOP_PET_INTERACTION_UX_UPGRADED: stable placement, local opt-in wake settings, hidden-chat speech bubble. Microphone was not opened.");
    }

    private static void BuildBubble(RectTransform canvas, PetSpeechBubble component)
    {
        RectTransform root = FindOrCreate("SpeechBubble", canvas);
        Undo.RecordObject(root, "Configure speech bubble");
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0);
        root.sizeDelta = new Vector2(320, 120);
        root.anchoredPosition = new Vector2(0, 200);
        Color bubbleColor = Card; bubbleColor.a = PetSpeechBubble.BackgroundOpacity;
        UnityEngine.UI.Image image = Paint(root, bubbleColor);
        image.raycastTarget = true;
        var button = Ensure<UnityEngine.UI.Button>(root.gameObject);
        button.targetGraphic = image;
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        var colors = button.colors;
        colors.normalColor = Color.white; colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f);
        button.colors = colors;
        TMP_Text text = Text("Message", root, "", 17, Ink);
        Stretch(text.rectTransform, 18);
        text.rectTransform.offsetMin = new Vector2(18, 32);
        text.rectTransform.offsetMax = new Vector2(-18, -14);
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.richText = false;
        TMP_Text hint = Text("OpenChatHint", root, "点击查看聊天", 12, Muted);
        hint.rectTransform.anchorMin = new Vector2(0, 0);
        hint.rectTransform.anchorMax = new Vector2(1, 0);
        hint.rectTransform.pivot = new Vector2(0.5f, 0);
        hint.rectTransform.sizeDelta = new Vector2(-36, 22);
        hint.rectTransform.anchoredPosition = new Vector2(0, 7);
        hint.alignment = TextAlignmentOptions.MidlineRight;
        Set(component, "bubbleRoot", root);
        Set(component, "messageText", text);
        Set(component, "openChatButton", button);
        Undo.RecordObject(root.gameObject, "Hide speech bubble on startup");
        root.gameObject.SetActive(false);
    }

    private static void BuildWakeSettings(RectTransform content, DesktopPetWakeWord wake)
    {
        // Search descendants too: settings pagination may move this group into the voice page.
        RectTransform group = content.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(item => item.name == "WakeWordSettings");
        if (group == null)
        {
            RectTransform voicePage = content.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(item => item.name == "VoicePage");
            group = FindOrCreate("WakeWordSettings", voicePage != null ? voicePage : content);
        }
        var vertical = Ensure<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        Undo.RecordObject(vertical, "Configure wake settings layout");
        vertical.spacing = 8;
        vertical.padding = new RectOffset(0, 0, 10, 10);
        vertical.childControlWidth = true; vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        Label("Title", group, "语音唤醒", 19, Ink, 30);
        RectTransform toggleRoot = FindOrCreate("WakeEnabled", group);
        Height(toggleRoot, 32);
        var toggle = Ensure<UnityEngine.UI.Toggle>(toggleRoot.gameObject);
        var hit = Paint(toggleRoot, Color.clear);
        hit.raycastTarget = true;
        RectTransform box = FindOrCreate("Box", toggleRoot);
        box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
        box.pivot = new Vector2(0, 0.5f);
        box.sizeDelta = new Vector2(22, 22);
        var background = Paint(box, Field);
        RectTransform mark = FindOrCreate("Check", box);
        Stretch(mark, 5);
        var markImage = Paint(mark, Accent);
        markImage.raycastTarget = false;
        toggle.targetGraphic = background;
        toggle.graphic = markImage;
        toggle.SetIsOnWithoutNotify(false);
        TMP_Text toggleLabel = Text("Label", toggleRoot, "开启本机唤醒词监听（默认关闭）", 15, Ink);
        Stretch(toggleLabel.rectTransform);
        toggleLabel.rectTransform.offsetMin = new Vector2(32, 0);
        toggleLabel.alignment = TextAlignmentOptions.MidlineLeft;
        Label("KeywordLabel", group, "唤醒词", 15, Ink, 24);
        TMP_InputField input = KeywordInput(group);
        Label("PrivacyHint", group,
            "本机识别唤醒词；唤醒后才开始录音并发送至你配置的 ASR。停顿约 1 秒自动发送，回复期间暂停监听。", 13, Muted, 60);
        TMP_Text status = Label("WakeStatus", group, "已关闭 · 可随时在这里开启或关闭。", 13, Muted, 54);
        Set(wake, "enabledToggle", toggle);
        Set(wake, "keywordInput", input);
        Set(wake, "statusText", status);
    }

    private static TMP_InputField KeywordInput(RectTransform parent)
    {
        RectTransform root = FindOrCreate("WakeKeyword", parent);
        Height(root, 42);
        var background = Paint(root, Field);
        var input = Ensure<TMP_InputField>(root.gameObject);
        input.targetGraphic = background;
        RectTransform viewport = FindOrCreate("TextViewport", root);
        Stretch(viewport, 10);
        Ensure<UnityEngine.UI.RectMask2D>(viewport.gameObject);
        TMP_Text text = Text("Text", viewport, "", 16, Ink);
        Stretch(text.rectTransform);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.richText = false;
        TMP_Text placeholder = Text("Placeholder", viewport, DesktopPetWakeWord.DefaultKeyword, 16, Muted);
        Stretch(placeholder.rectTransform);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.fontAsset = font;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 24;
        input.SetTextWithoutNotify(DesktopPetWakeWord.DefaultKeyword);
        input.customCaretColor = true;
        input.caretColor = Accent;
        input.selectionColor = new Color(0.57f, 0.91f, 0.8f, 0.28f);
        return input;
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
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Add " + name);
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
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

    private static void Height(RectTransform rect, float value)
    {
        var element = Ensure<UnityEngine.UI.LayoutElement>(rect.gameObject);
        Undo.RecordObject(element, "Size " + rect.name);
        element.minHeight = element.preferredHeight = value;
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
