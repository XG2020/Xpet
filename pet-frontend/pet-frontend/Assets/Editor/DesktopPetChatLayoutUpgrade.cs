using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Rearranges existing chat controls, retaining their references and event listeners.</summary>
public static class DesktopPetChatLayoutUpgrade
{
    [MenuItem("Desktop Pet/Upgrade Chat Layout")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before upgrading the chat layout.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath) throw new InvalidOperationException("Open DesktopPet.unity before upgrading its chat layout.");
        RectTransform chat = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<RectTransform>(true))
            .FirstOrDefault(rect => rect.name == "ChatPanel");
        if (chat == null) throw new InvalidOperationException("The existing ChatPanel was not found.");
        Undo.RegisterFullObjectHierarchyUndo(chat.gameObject, "Rearrange chat composer");
        ApplyToChat(chat);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Unable to save the chat layout.");
        Debug.Log("DESKTOP_PET_CHAT_LAYOUT_READY: names outside bubbles, multiline composer and one compact toolbar.");
    }

    public static void ApplyToChat(RectTransform chat)
    {
        TMP_InputField input = Find(chat, "MessageInput")?.GetComponent<TMP_InputField>();
        RectTransform toolbar = Find(chat, "SendRow");
        var mic = Find(chat, "VoiceInput")?.GetComponent<UnityEngine.UI.Button>();
        var send = Find(chat, "Send")?.GetComponent<UnityEngine.UI.Button>();
        var screen = Find(chat, "ShareScreen")?.GetComponent<UnityEngine.UI.Toggle>();
        var speech = Find(chat, "Speech")?.GetComponent<UnityEngine.UI.Toggle>();
        RectTransform conversation = Find(chat, "Conversation");
        RectTransform header = Find(chat, "Header");
        if (input == null || toolbar == null || mic == null || send == null || screen == null || speech == null
            || conversation == null || header == null)
            throw new InvalidOperationException("The existing chat input, toolbar, toggles and header are required.");

        // Keep the established compact panel footprint and its independently draggable header.
        chat.sizeDelta = new Vector2(380, 500);
        var outer = Ensure<UnityEngine.UI.VerticalLayoutGroup>(chat.gameObject);
        outer.padding = new RectOffset(14, 14, 12, 12); outer.spacing = 8;
        outer.childControlWidth = outer.childControlHeight = true;
        outer.childForceExpandWidth = true; outer.childForceExpandHeight = false;
        Height(header, 36);
        TMP_Text title = header.Find("Title")?.GetComponent<TMP_Text>();
        if (title != null)
        {
            title.text = "丽塔"; title.fontSize = 22;
            title.enableAutoSizing = true; title.fontSizeMin = 16; title.fontSizeMax = 22;
            title.textWrappingMode = TextWrappingModes.NoWrap; title.overflowMode = TextOverflowModes.Ellipsis;
            var titleSize = Ensure<UnityEngine.UI.LayoutElement>(title.gameObject);
            titleSize.minWidth = 20; titleSize.preferredWidth = -1; titleSize.flexibleWidth = 1;
        }
        foreach (var button in header.GetComponentsInChildren<UnityEngine.UI.Button>(true)) StyleButton(button, 44, 30, 12, false);
        foreach (string name in new[] { "Subtitle", "States" })
        { RectTransform item = Find(chat, name); if (item != null) item.gameObject.SetActive(false); }

        var conversationSize = Ensure<UnityEngine.UI.LayoutElement>(conversation.gameObject);
        conversationSize.minHeight = 80; conversationSize.preferredHeight = -1; conversationSize.flexibleHeight = 1;
        TMP_Text reply = conversation.Find("Viewport/Content/ReplyText")?.GetComponent<TMP_Text>();
        if (reply != null)
        {
            reply.fontSize = 17;
            if (reply.text.StartsWith("你好，我是丽塔。", StringComparison.Ordinal))
                reply.text = "想聊点什么？\n\n发条消息，或点下方语音输入。";
        }

        RectTransform composer = chat.Find("Composer") as RectTransform;
        if (composer == null)
        {
            var created = new GameObject("Composer", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(created, "Add chat composer");
            created.transform.SetParent(chat, false); composer = (RectTransform)created.transform;
        }
        Height(composer, 142);
        var surface = Ensure<UnityEngine.UI.Image>(composer.gameObject);
        surface.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/DesktopPetUIRound.png");
        surface.type = UnityEngine.UI.Image.Type.Sliced; surface.color = new Color32(36, 47, 63, 255); surface.raycastTarget = true;
        var composerLayout = Ensure<UnityEngine.UI.VerticalLayoutGroup>(composer.gameObject);
        composerLayout.padding = new RectOffset(10, 10, 8, 8); composerLayout.spacing = 8;
        composerLayout.childControlWidth = composerLayout.childControlHeight = true;
        composerLayout.childForceExpandWidth = true; composerLayout.childForceExpandHeight = false;
        input.transform.SetParent(composer, false); input.transform.SetSiblingIndex(0);
        toolbar.SetParent(composer, false); toolbar.SetSiblingIndex(1);
        Height((RectTransform)input.transform, 84); Height(toolbar, 34);
        input.lineType = TMP_InputField.LineType.MultiLineNewline;
        input.characterLimit = 2000;
        var inputImage = input.GetComponent<UnityEngine.UI.Image>();
        if (inputImage != null) inputImage.color = Color.clear;
        if (input.textViewport != null) Stretch(input.textViewport, 4);
        if (input.textComponent != null) { input.textComponent.fontSize = 16; input.textComponent.alignment = TextAlignmentOptions.TopLeft; }
        if (input.placeholder is TMP_Text placeholder)
        { placeholder.text = "发条消息…"; placeholder.fontSize = 16; placeholder.alignment = TextAlignmentOptions.TopLeft; }

        var tools = Ensure<UnityEngine.UI.HorizontalLayoutGroup>(toolbar.gameObject);
        tools.padding = new RectOffset(); tools.spacing = 6; tools.childAlignment = TextAnchor.MiddleLeft;
        tools.childControlWidth = tools.childControlHeight = true;
        tools.childForceExpandWidth = tools.childForceExpandHeight = false;
        mic.transform.SetParent(toolbar, false); mic.transform.SetSiblingIndex(0);
        speech.transform.SetParent(toolbar, false); speech.transform.SetSiblingIndex(1);
        screen.transform.SetParent(toolbar, false); screen.transform.SetSiblingIndex(2);
        RectTransform spacer = toolbar.Find("Spacer") as RectTransform;
        if (spacer == null)
        {
            var created = new GameObject("Spacer", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(created, "Add toolbar spacing"); created.transform.SetParent(toolbar, false);
            spacer = (RectTransform)created.transform;
        }
        spacer.SetSiblingIndex(3);
        var space = Ensure<UnityEngine.UI.LayoutElement>(spacer.gameObject);
        space.minWidth = 0; space.preferredWidth = 0; space.flexibleWidth = 1;
        send.transform.SetParent(toolbar, false); send.transform.SetSiblingIndex(4);
        StyleButton(mic, 72, 34, 12, false);
        StyleToggle(speech, "朗读", 54);
        StyleToggle(screen, "本次屏幕", 78);
        StyleButton(send, 56, 34, 14, true);
        // The former options row stays available to older scene tools, but has no duplicate controls.
        RectTransform legacyOptions = chat.Find("Options") as RectTransform;
        if (legacyOptions != null) legacyOptions.gameObject.SetActive(false);

        RectTransform command = chat.Find("SuggestedCommand") as RectTransform;
        if (command != null)
        {
            Height(command, 76);
            var layout = command.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            if (layout != null) { layout.padding = new RectOffset(6, 6, 6, 6); layout.spacing = 4; }
            Height(command.Find("Description") as RectTransform, 28);
            Height(command.Find("Actions") as RectTransform, 28);
            foreach (var button in command.GetComponentsInChildren<UnityEngine.UI.Button>(true)) Height((RectTransform)button.transform, 28);
        }
        RectTransform status = chat.Find("Status") as RectTransform;
        if (status != null)
        {
            Height(status, 34);
            var text = status.GetComponent<TMP_Text>();
            if (text != null) { text.fontSize = 12; text.alignment = TextAlignmentOptions.TopLeft; text.textWrappingMode = TextWrappingModes.Normal; }
        }
        header.SetSiblingIndex(0); conversation.SetSiblingIndex(1);
        if (command != null) command.SetSiblingIndex(2);
        composer.SetSiblingIndex(command != null ? 3 : 2);
        if (status != null) status.SetSiblingIndex(command != null ? 4 : 3);

        PetChatPanel panel = chat.gameObject.scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<PetChatPanel>(true)).FirstOrDefault();
        if (panel != null)
        {
            Set(panel, "petNameTitle", title);
            Set(panel, "settingsApi", panel.GetComponent<BackendSettingsApi>());
        }
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(chat);
    }

    private static void StyleButton(UnityEngine.UI.Button button, float width, float height, float fontSize, bool primary)
    {
        var size = Ensure<UnityEngine.UI.LayoutElement>(button.gameObject);
        size.minWidth = size.preferredWidth = width; size.flexibleWidth = 0;
        size.minHeight = size.preferredHeight = height; size.flexibleHeight = 0;
        var image = button.GetComponent<UnityEngine.UI.Image>();
        if (image != null) image.color = primary ? new Color32(147, 232, 205, 255) : new Color(1, 1, 1, 0.04f);
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.fontSize = fontSize; label.enableAutoSizing = false;
            label.color = primary ? new Color32(17, 48, 41, 255) : new Color32(224, 233, 244, 255);
            label.textWrappingMode = TextWrappingModes.NoWrap; label.alignment = TextAlignmentOptions.Center;
            Stretch(label.rectTransform, 3);
        }
    }
    private static void StyleToggle(UnityEngine.UI.Toggle toggle, string label, float width)
    {
        var size = Ensure<UnityEngine.UI.LayoutElement>(toggle.gameObject);
        size.minWidth = size.preferredWidth = width; size.flexibleWidth = 0;
        size.minHeight = size.preferredHeight = 34; size.flexibleHeight = 0;
        RectTransform box = toggle.transform.Find("Box") as RectTransform;
        if (box != null) {
            box.sizeDelta = new Vector2(15, 15); box.anchoredPosition = Vector2.zero;
            var image = box.GetComponent<UnityEngine.UI.Image>();
            if (image != null) image.color = new Color32(72, 91, 115, 255);
        }
        TMP_Text text = toggle.transform.Find("Label")?.GetComponent<TMP_Text>();
        if (text != null)
        {
            text.text = label; text.fontSize = 12; text.textWrappingMode = TextWrappingModes.NoWrap;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(text.rectTransform, 0); text.rectTransform.offsetMin = new Vector2(21, 0);
        }
    }
    private static RectTransform Find(Transform root, string name) => root.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(rect => rect.name == name);
    private static T Ensure<T>(GameObject target) where T : Component => target.GetComponent<T>() ?? Undo.AddComponent<T>(target);
    private static void Height(RectTransform rect, float value)
    {
        if (rect == null) return;
        var layout = Ensure<UnityEngine.UI.LayoutElement>(rect.gameObject);
        layout.minHeight = layout.preferredHeight = value; layout.flexibleHeight = 0;
    }
    private static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.one * inset; rect.offsetMax = Vector2.one * -inset;
    }
    private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target); SerializedProperty field = serialized.FindProperty(property);
        if (field == null) throw new MissingFieldException(target.GetType().Name, property);
        field.objectReferenceValue = value; serialized.ApplyModifiedProperties();
    }
}
