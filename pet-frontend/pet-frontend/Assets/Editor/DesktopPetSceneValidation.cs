using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>Checks saved scene wiring before a build or Play Mode smoke test.</summary>
public static class DesktopPetSceneValidation
{
    [Serializable]
    private class Report
    {
        public bool passed;
        public string scene;
        public int loadedScenes;
        public int textElements;
        public int buttons;
        public int renderers;
        public string[] errors;
    }

    [MenuItem("Desktop Pet/Validate Desktop Pet Scene")]
    public static void ValidateScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var errors = new List<string>();
        if (scene.path != DesktopPetSceneBuilder.ScenePath) errors.Add("The active scene is not DesktopPet.unity.");
        if (SceneManager.sceneCount != 1) errors.Add("Open DesktopPet as the only scene before running or building.");
        var objects = roots.SelectMany(root => root.GetComponentsInChildren<Transform>(true)).ToArray();
        foreach (Transform transform in objects)
            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                errors.Add("Missing script: " + transform.name);

        CheckReferences(One<PetChatPanel>(roots, errors), errors, "backendClient", "messageInput", "sendButton",
            "shareScreenToggle", "speechToggle", "replyText", "actionText", "emotionText", "statusText",
            "conversationScroll", "commandRoot", "commandText", "executeCommandButton", "dismissCommandButton");
        CheckReferences(One<BackendSettingsPanel>(roots, errors), errors, "settingsApi", "backendServiceUrlInput",
            "petNameInput", "personaInput", "llmProviderInput", "llmModelInput", "apiKeyInput", "baseUrlInput",
            "enableRealRpaToggle", "allowedAppsInput", "ttsVoiceInput", "statusText", "apiKeyStatusText",
            "saveButton", "refreshButton", "edgeTtsVoiceInput", "ttsProviderInput", "ttsBaseUrlInput", "ttsApiKeyInput", "ttsModelInput",
            "asrBaseUrlInput", "asrApiKeyInput", "asrModelInput", "sovitsRefAudioInput", "sovitsPromptTextInput",
            "sovitsPromptLangInput", "sovitsTextLangInput", "ttsApiKeyStatusText", "asrApiKeyStatusText");
        CheckReferences(One<SettingsPanelController>(roots, errors), errors, "panelRoot");
        CheckReferences(One<PetActionSettingsPanel>(roots, errors), errors, "actionDropdown", "enabledToggle", "previewButton", "deleteButton", "detailsText", "statusText", "nameInput", "stepDropdown", "secondsInput", "addStepButton", "removeStepButton", "draftText", "speedInput", "triggerDropdown", "createButton");
        var tabs = One<SettingsTabsController>(roots, errors);
        if (tabs != null)
        {
            foreach (string title in new[] { "外观", "对话", "语音", "记忆", "工具", "动作管理", "模型导入" })
                if (tabs.FindPageContent(title) == null) errors.Add("Missing settings page: " + title);
            if (tabs.FindPageContent("外观")?.Find("ModelLibrarySettings") != null) errors.Add("Model controls must be on the dedicated import page.");
            if (tabs.FindPageContent("模型导入")?.Find("ModelLibrarySettings/Tutorial") == null) errors.Add("Missing model import tutorial.");
        }
        CheckReferences(One<DesktopPetUIActions>(roots, errors), errors, "chatRoot", "microphoneLabel", "contextMenuRoot", "settingsController");
        CheckReferences(One<DesktopPetAppearance>(roots, errors), errors, "characterRoot", "worldCamera", "chatPanel",
            "settingsCard", "contextMenuRoot", "modelSizeSlider", "backgroundColorInput", "appearanceStatus");
        CheckReferences(One<PetAnimatorBridge>(roots, errors), errors, "backendClient", "targetAnimator");
        One<PetBackendHttpClient>(roots, errors);
        One<BackendSettingsApi>(roots, errors);
        One<DesktopPetInteraction>(roots, errors);
        One<TransparentWindowController>(roots, errors);
        One<EventSystem>(roots, errors);
        var canvas = One<Canvas>(roots, errors);
        if (canvas != null && canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            errors.Add("Canvas is missing GraphicRaycaster.");
        if (canvas != null)
        {
            Transform oldDock = canvas.transform.Find("DesktopDock");
            if (oldDock != null && oldDock.gameObject.activeSelf) errors.Add("The obsolete action dock must remain disabled.");
            Transform states = canvas.transform.Find("ChatPanel/States");
            if (states != null && states.gameObject.activeSelf) errors.Add("Chat must not display action or emotion debug states.");
            foreach (string path in new[] { "ChatPanel", "SettingsOverlay/SettingsCard", "ContextMenuRoot" })
            {
                var image = canvas.transform.Find(path)?.GetComponent<UnityEngine.UI.Image>();
                if (image != null && image.color.a < 0.999f) errors.Add("Companion panel must be opaque for color-key transparency: " + path);
            }
            var overlay = canvas.transform.Find("SettingsOverlay")?.GetComponent<UnityEngine.UI.Image>();
            if (overlay != null && (overlay.color.a != 0 || overlay.raycastTarget))
                errors.Add("Settings backdrop must remain clear and must not intercept desktop input.");
            Transform menu = canvas.transform.Find("ContextMenuRoot");
            if (menu == null) errors.Add("The companion context menu is missing.");
            else
                foreach (string item in new[] { "Chat", "VoiceInput", "Settings", "Quit", "History", "LowPower", "HidePet" })
                    if (menu.Find(item)?.GetComponent<UnityEngine.UI.Button>() == null)
                        errors.Add("Missing context menu button: " + item);
            if (!Application.isPlaying)
            {
                Transform chat = canvas.transform.Find("ChatPanel");
                Transform settings = canvas.transform.Find("SettingsOverlay");
                if (chat != null && chat.gameObject.activeSelf) errors.Add("Chat must be hidden for character-only startup.");
                if (menu != null && menu.gameObject.activeSelf) errors.Add("The context menu must be hidden at startup.");
                if (settings != null && settings.gameObject.activeSelf) errors.Add("Settings must be hidden at startup.");
            }
        }

        var textElements = All<TMP_Text>(roots);
        var uniqueFonts = textElements.Select(text => text.font).Distinct().ToArray();
        if (uniqueFonts.Any(font => font == null)) errors.Add("A UI text element is missing its font.");
        foreach (TMP_FontAsset font in uniqueFonts.Where(font => font != null))
            if (!font.HasCharacter('丽', true, true) || !font.HasCharacter('设', true, true))
                errors.Add("Font cannot render Chinese UI: " + font.name);
        foreach (TMP_InputField input in All<TMP_InputField>(roots))
            if (input.textComponent == null || input.textViewport == null || input.targetGraphic == null)
                errors.Add("Input is not fully configured: " + input.name);
        foreach (UnityEngine.UI.ScrollRect scroll in All<UnityEngine.UI.ScrollRect>(roots))
        {
            if (scroll.content == null || scroll.viewport == null)
                errors.Add("Scroll view is missing Content or Viewport: " + scroll.name);
            else
            {
                var mask = scroll.viewport.GetComponent<UnityEngine.UI.Mask>();
                var image = scroll.viewport.GetComponent<UnityEngine.UI.Image>();
                if (mask != null && (image == null || image.color.a < 0.01f))
                    errors.Add("Scroll mask cannot write its stencil because its Image is transparent: " + scroll.name);
            }
        }

        var renderers = All<SkinnedMeshRenderer>(roots);
        if (renderers.Length == 0 || renderers.All(renderer => renderer.sharedMesh == null))
            errors.Add("The actual Lita model is not present in the scene.");
        Animator animator = All<Animator>(roots).FirstOrDefault();
        if (animator == null || animator.runtimeAnimatorController == null)
            errors.Add("The character has no Animator Controller.");

        var report = new Report
        {
            passed = errors.Count == 0,
            scene = scene.path,
            loadedScenes = SceneManager.sceneCount,
            textElements = textElements.Length,
            buttons = All<UnityEngine.UI.Button>(roots).Length,
            renderers = renderers.Length,
            errors = errors.ToArray()
        };
        Debug.Log("DESKTOP_PET_VALIDATION " + JsonUtility.ToJson(report));
        if (!report.passed) throw new InvalidOperationException(string.Join("\n", errors));
    }

    [MenuItem("Desktop Pet/Repair Scroll Masks")]
    public static void RepairScrollMasks()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath)
            throw new InvalidOperationException("Open DesktopPet.unity before repairing its scroll masks.");
        int repaired = 0;
        foreach (var scroll in All<UnityEngine.UI.ScrollRect>(scene.GetRootGameObjects()))
        {
            if (scroll.viewport == null) continue;
            var mask = scroll.viewport.GetComponent<UnityEngine.UI.Mask>();
            var image = scroll.viewport.GetComponent<UnityEngine.UI.Image>();
            if (mask == null || image == null) continue;
            Undo.RecordObjects(new UnityEngine.Object[] { image, mask }, "Repair desktop pet scroll mask");
            image.color = Color.white;
            mask.showMaskGraphic = false;
            EditorUtility.SetDirty(image);
            EditorUtility.SetDirty(mask);
            repaired++;
        }
        if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(scene);
        Canvas.ForceUpdateCanvases();
        Debug.Log("DESKTOP_PET_SCROLL_MASKS_REPAIRED " + repaired);
    }

    private static T[] All<T>(GameObject[] roots) where T : Component
    {
        return roots.SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
    }

    private static T One<T>(GameObject[] roots, List<string> errors) where T : Component
    {
        T[] found = All<T>(roots);
        if (found.Length != 1) errors.Add("Expected one " + typeof(T).Name + ", found " + found.Length + ".");
        return found.FirstOrDefault();
    }

    private static void CheckReferences(UnityEngine.Object target, List<string> errors, params string[] names)
    {
        if (target == null) return;
        var serialized = new SerializedObject(target);
        foreach (string name in names)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null || property.objectReferenceValue == null)
                errors.Add(target.GetType().Name + "." + name + " is not assigned.");
        }
    }
}
