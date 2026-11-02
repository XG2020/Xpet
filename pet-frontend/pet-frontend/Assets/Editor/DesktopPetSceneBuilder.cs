using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>Creates the runnable desktop scene without changing the original SampleScene.</summary>
public static class DesktopPetSceneBuilder
{
    public const string ScenePath = "Assets/Scenes/DesktopPet.unity";
    private const string FontPath = "Assets/Resources/DesktopPetUIFont.asset";
    private const string RoundedSpritePath = "Assets/Resources/DesktopPetUIRound.png";
    private static readonly Color Card = new Color32(24, 33, 48, 255);
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Ink = new Color32(238, 244, 251, 255);
    private static readonly Color Muted = new Color32(173, 187, 205, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);
    private static TMP_FontAsset font;
    private static Sprite round;

    [MenuItem("Desktop Pet/Build Desktop Pet Scene")]
    public static void BuildScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before building the desktop scene.");
        if (File.Exists(ScenePath))
        {
            Debug.Log("DesktopPet scene already exists. Open it to preserve your changes: " + ScenePath);
            return;
        }
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Resources");
        font = GetOrCreateFont();
        round = GetOrCreateRoundedSprite();

        // Additive creation leaves any unsaved work in the user's current scene intact.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cameraGo.tag = "MainCamera";
        Camera camera = cameraGo.GetComponent<Camera>();
        camera.transform.position = new Vector3(0.78f, 0.96f, -4f);
        camera.orthographic = true;
        camera.orthographicSize = 1.12f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 30f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.allowHDR = false;
        camera.allowMSAA = false;

        var keyLight = new GameObject("Soft Key Light", typeof(Light));
        keyLight.transform.rotation = Quaternion.Euler(32f, -22f, 0f);
        var light = keyLight.GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.color = new Color(1f, 0.96f, 0.94f);
        light.shadows = LightShadows.None;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.72f, 0.76f, 0.83f);
        RenderSettings.skybox = null;

        var services = new GameObject("AppServices");
        var api = services.AddComponent<BackendSettingsApi>();
        var client = services.AddComponent<PetBackendHttpClient>();
        services.AddComponent<TransparentWindowController>();
        services.AddComponent<DesktopPetInteraction>();
        var settingsController = services.AddComponent<SettingsPanelController>();
        var actions = services.AddComponent<DesktopPetUIActions>();
        var chatController = services.AddComponent<PetChatPanel>();
        var settingsPanel = services.AddComponent<BackendSettingsPanel>();
        Set(settingsController, "openOnStart", false);

        var characterRoot = new GameObject("PetCharacterRoot");
        characterRoot.transform.position = new Vector3(1.55f, 0, 0);
        characterRoot.transform.localScale = Vector3.one * 0.65f;
        var bridge = characterRoot.AddComponent<PetAnimatorBridge>();
        bridge.SetBackendClient(client);
        // Model placement is independent from UI creation; the importer can add it later.
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Lita/DesktopPetLita.prefab");
        if (prefab != null)
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, characterRoot.transform);
            model.transform.localPosition = Vector3.zero;
            bridge.SetTargetAnimator(model.GetComponentInChildren<Animator>());
        }
        Set(actions, "settingsController", settingsController);

        RectTransform canvasRect = Rect("DesktopPetCanvas", null);
        Canvas canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasRect.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1200, 800);
        scaler.matchWidthOrHeight = 0.5f;
        canvasRect.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        var eventSystem = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
        eventSystem.AddComponent<InputSystemUIInputModule>();
#else
        eventSystem.AddComponent<StandaloneInputModule>();
#endif
        BuildChat(canvasRect, chatController, client, actions, settingsController);
        BuildContextMenu(canvasRect, actions, settingsController);
        BuildSettings(canvasRect, settingsPanel, api, settingsController);
        ExtendCompanionSettings(canvasRect, services, characterRoot.transform, camera, settingsPanel);
        ApplySettingsPages(canvasRect, settingsPanel);

        var strings = canvasRect.GetComponentsInChildren<TMP_Text>(true).Select(item => item.text);
        string warmup = string.Join("", strings) + new string(Enumerable.Range(32, 95).Select(c => (char)c).ToArray());
        font.TryAddCharacters(warmup, out string missing);
        if (!string.IsNullOrEmpty(missing)) Debug.LogWarning("The system font could not supply these UI characters: " + missing);
        foreach (Texture2D atlas in font.atlasTextures)
            if (atlas != null && !AssetDatabase.Contains(atlas)) AssetDatabase.AddObjectToAsset(atlas, font);
        EditorUtility.SetDirty(font);
        Canvas.ForceUpdateCanvases();
        AssetDatabase.SaveAssets();
        if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Unable to save " + ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Selection.activeGameObject = canvasRect.gameObject;
        Debug.Log("DesktopPet scene is ready: " + ScenePath + ". Open it as the only scene before entering Play Mode.");
    }

    private static void BuildChat(RectTransform canvas, PetChatPanel controller, PetBackendHttpClient client,
        DesktopPetUIActions actions, SettingsPanelController settingsController)
    {
        RectTransform card = Panel("ChatPanel", canvas, Card);
        card.anchorMin = card.anchorMax = new Vector2(1, 1);
        card.pivot = new Vector2(1, 1);
        card.anchoredPosition = new Vector2(-24, -24);
        card.sizeDelta = new Vector2(456, 692);
        Vertical(card, 20, 9);
        Set(actions, "chatRoot", card.gameObject);
        Set(controller, "backendClient", client);

        RectTransform header = Row("Header", card, 44, 10);
        TMP_Text title = Text("Title", header, "丽塔  LITA", 27, Ink);
        Flexible(title.rectTransform);
        var settings = Button("Settings", header, "设置", false, 64, 36);
        UnityEventTools.AddPersistentListener(settings.onClick, settingsController.Open);
        var hide = Button("Minimize", header, "收起", false, 64, 36);
        UnityEventTools.AddPersistentListener(hide.onClick, actions.ToggleChat);
        TextFixed("Subtitle", card, "你的桌面伙伴，随时听你说。", 15, Muted, 24);

        RectTransform conversation = Scroll("Conversation", card, out RectTransform content, out UnityEngine.UI.ScrollRect scroll);
        var conversationLayout = conversation.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        conversationLayout.minHeight = 104;
        conversationLayout.flexibleHeight = 1;
        TMP_Text reply = Text("ReplyText", content, "你好，我是丽塔。\n\n想聊点什么？我会用表情和动作回应你。\n\n右键点击我，可以打开聊天、语音和设置。", 19, Ink);
        reply.richText = false;
        reply.textWrappingMode = TextWrappingModes.Normal;
        Set(controller, "replyText", reply);
        Set(controller, "conversationScroll", scroll);

        RectTransform states = Row("States", card, 24, 18);
        var action = Text("ActionText", states, "动作 · 待机", 14, Accent); Flexible(action.rectTransform);
        var emotion = Text("EmotionText", states, "心情 · 平静", 14, Muted); Flexible(emotion.rectTransform);
        Set(controller, "actionText", action); Set(controller, "emotionText", emotion);

        RectTransform options = Row("Options", card, 30, 12);
        var screenToggle = Toggle("ShareScreen", options, "本次附带屏幕", false);
        var speechToggle = Toggle("Speech", options, "朗读回复", false);
        Flexible(screenToggle.GetComponent<RectTransform>()); Flexible(speechToggle.GetComponent<RectTransform>());
        Set(controller, "shareScreenToggle", screenToggle); Set(controller, "speechToggle", speechToggle);

        TMP_InputField input = Input("MessageInput", card, "想说些什么？", 78, true);
        input.characterLimit = 2000;
        Set(controller, "messageInput", input);

        RectTransform buttons = Row("SendRow", card, 40, 10);
        var mic = Button("VoiceInput", buttons, "语音输入", false, 108, 40);
        UnityEventTools.AddPersistentListener(mic.onClick, actions.ToggleVoiceRecording);
        Set(actions, "microphoneLabel", mic.GetComponentInChildren<TMP_Text>());
        RectTransform spacer = Rect("Spacer", buttons); Flexible(spacer);
        var send = Button("Send", buttons, "发送", true, 96, 40);
        Set(controller, "sendButton", send);

        RectTransform commandRoot = Panel("SuggestedCommand", card, Field);
        Height(commandRoot, 88); Vertical(commandRoot, 8, 5);
        var commandText = TextFixed("Description", commandRoot, "", 14, Ink, 28);
        commandText.richText = false;
        RectTransform commandButtons = Row("Actions", commandRoot, 32, 8);
        var execute = Button("Execute", commandButtons, "执行建议操作", true, 160, 32);
        var dismiss = Button("Dismiss", commandButtons, "忽略", false, 66, 32);
        Set(controller, "commandRoot", commandRoot.gameObject); Set(controller, "commandText", commandText);
        Set(controller, "executeCommandButton", execute); Set(controller, "dismissCommandButton", dismiss);
        commandRoot.gameObject.SetActive(false);

        var status = TextFixed("Status", card, "Enter 发送 · Shift+Enter 换行", 13, Muted, 38);
        status.richText = false;
        status.textWrappingMode = TextWrappingModes.Normal;
        Set(controller, "statusText", status);
        CompactChat(card);
        card.gameObject.SetActive(false);
    }

    private static void BuildContextMenu(RectTransform canvas, DesktopPetUIActions actions, SettingsPanelController settingsController)
    {
        RectTransform menu = canvas.Find("ContextMenuRoot") as RectTransform;
        if (menu == null)
        {
            menu = Panel("ContextMenuRoot", canvas, Card);
            menu.anchorMin = menu.anchorMax = new Vector2(0.5f, 0.5f);
            menu.pivot = new Vector2(0, 1);
            menu.sizeDelta = new Vector2(180, 166);
            Vertical(menu, 8, 6);
            var chat = Button("Chat", menu, "聊天", false, 160, 33);
            UnityEventTools.AddPersistentListener(chat.onClick, actions.OpenChat);
            var voice = Button("VoiceInput", menu, "语音输入", false, 160, 33);
            UnityEventTools.AddPersistentListener(voice.onClick, actions.ToggleVoiceRecording);
            var settings = Button("Settings", menu, "设置", false, 160, 33);
            UnityEventTools.AddPersistentListener(settings.onClick, actions.OpenSettings);
            var quit = Button("Quit", menu, "退出", false, 160, 33);
            UnityEventTools.AddPersistentListener(quit.onClick, actions.Quit);
        }
        menu.Find("Quit")?.SetAsLastSibling();
        Set(actions, "contextMenuRoot", menu);
        Set(actions, "settingsController", settingsController);
        menu.gameObject.SetActive(false);
    }

    [MenuItem("Desktop Pet/Apply Companion UI")]
    public static void ApplyCompanionUI()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before applying the companion UI.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new InvalidOperationException("Open DesktopPet.unity before applying the companion UI.");
        GameObject[] roots = scene.GetRootGameObjects();
        Canvas canvas = roots.SelectMany(root => root.GetComponentsInChildren<Canvas>(true)).Single();
        DesktopPetUIActions actions = roots.SelectMany(root => root.GetComponentsInChildren<DesktopPetUIActions>(true)).Single();
        SettingsPanelController settings = roots.SelectMany(root => root.GetComponentsInChildren<SettingsPanelController>(true)).Single();
        RectTransform chat = canvas.transform.Find("ChatPanel") as RectTransform;
        if (chat == null) throw new InvalidOperationException("The existing ChatPanel was not found.");
        font = GetOrCreateFont();
        round = GetOrCreateRoundedSprite();
        GameObject characterRoot = roots.FirstOrDefault(root => root.name == "PetCharacterRoot");
        if (characterRoot != null)
        {
            Undo.RecordObject(characterRoot.transform, "Position desktop companion");
            characterRoot.transform.position = new Vector3(1.55f, 0, 0);
            characterRoot.transform.localScale = Vector3.one * 0.65f;
        }
        Camera camera = roots.SelectMany(root => root.GetComponentsInChildren<Camera>(true)).FirstOrDefault(item => item.CompareTag("MainCamera"));
        if (camera != null)
        {
            camera.transform.position = new Vector3(0.78f, 0.96f, -4);
            camera.orthographic = true;
            camera.orthographicSize = 1.12f;
        }
        Transform oldDock = canvas.transform.Find("DesktopDock");
        if (oldDock != null)
        {
            Undo.RecordObject(oldDock.gameObject, "Hide desktop debugging dock");
            oldDock.gameObject.SetActive(false);
        }
        CompactChat(chat);
        chat.gameObject.SetActive(false);
        Set(actions, "chatRoot", chat.gameObject);
        Set(settings, "openOnStart", false);
        settings.Close();
        BuildContextMenu((RectTransform)canvas.transform, actions, settings);
        MakeCompanionPanelsOpaque((RectTransform)canvas.transform);
        BackendSettingsPanel settingsPanel = roots.SelectMany(root => root.GetComponentsInChildren<BackendSettingsPanel>(true)).Single();
        ExtendCompanionSettings((RectTransform)canvas.transform, actions.gameObject, characterRoot != null ? characterRoot.transform : null, camera, settingsPanel);
        ApplySettingsPages((RectTransform)canvas.transform, settingsPanel);
        TMP_Text welcome = chat.Find("Conversation/Viewport/Content/ReplyText")?.GetComponent<TMP_Text>();
        if (welcome != null && welcome.text.Contains("点下方按钮"))
            welcome.text = "你好，我是丽塔。\n\n想聊点什么？我会用表情和动作回应你。\n\n右键点击我，可以打开聊天、语音和设置。";
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Unable to save the companion UI changes.");
        Debug.Log("DESKTOP_PET_COMPANION_UI_APPLIED: character-only startup, right-click menu, compact chat, no action dock.");
    }

    public static void UpgradeSettingsPages(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        Canvas canvas = roots.SelectMany(root => root.GetComponentsInChildren<Canvas>(true))
            .FirstOrDefault(item => item.transform.Find("SettingsOverlay/SettingsCard") != null);
        BackendSettingsPanel panel = roots.SelectMany(root => root.GetComponentsInChildren<BackendSettingsPanel>(true)).FirstOrDefault();
        if (canvas == null || panel == null) throw new InvalidOperationException("The existing settings UI was not found.");
        font = GetOrCreateFont();
        round = GetOrCreateRoundedSprite();
        Undo.RegisterFullObjectHierarchyUndo(canvas.gameObject, "Upgrade settings pages");
        ApplySettingsPages((RectTransform)canvas.transform, panel);
    }

    private static void ApplySettingsPages(RectTransform canvas, BackendSettingsPanel panel)
    {
        var card = canvas.Find("SettingsOverlay/SettingsCard") as RectTransform;
        if (card == null) throw new InvalidOperationException("SettingsCard was not found.");
        var overlay = card.parent.GetComponent<UnityEngine.UI.Image>();
        if (overlay != null) { overlay.color = Color.clear; overlay.raycastTarget = false; }
        var cardLayout = card.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        if (cardLayout != null) { cardLayout.padding = new RectOffset(18, 18, 16, 16); cardLayout.spacing = 8; }
        SetChildHeight(card, "Header", 36);
        SetChildHeight(card, "Hint", 22);
        SetChildHeight(card, "Status", 38);
        SetChildHeight(card, "Footer", 40);
        var subtitle = card.Find("Hint")?.GetComponent<TMP_Text>();
        if (subtitle != null) { subtitle.text = "按分类调整设置 · 后端配置修改后点击保存"; subtitle.fontSize = 13; }
        var heading = card.Find("Header/Heading")?.GetComponent<TMP_Text>();
        if (heading != null) { heading.text = "桌宠设置"; heading.fontSize = 24; }

        RectTransform tabsRoot = (card.Find("Body/Navigation/Viewport/Tabs") ?? card.Find("Body/Tabs") ?? card.Find("Tabs")) as RectTransform;
        if (tabsRoot == null) tabsRoot = Row("Tabs", card, 38, 6);
        if (tabsRoot.parent == card) tabsRoot.SetSiblingIndex(2);
        RectTransform pageHost = (card.Find("Body/Configuration/Pages") ?? card.Find("Pages")) as RectTransform;
        if (pageHost == null) pageHost = Rect("Pages", card);
        if (pageHost.parent == card) pageHost.SetSiblingIndex(3);
        var hostLayout = pageHost.GetComponent<UnityEngine.UI.LayoutElement>() ?? pageHost.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        hostLayout.minHeight = 40; hostLayout.preferredHeight = -1; hostLayout.flexibleHeight = 1;

        string[] titles = { "外观", "对话", "语音", "记忆", "工具", "动作管理", "模型导入", "角色卡", "后端日志" };
        string[] names = { "AppearancePage", "ConversationPage", "VoicePage", "MemoryPage", "ToolsPage", "ActionsPage", "ModelsPage", "CharactersPage", "LogsPage" };
        var pages = new GameObject[names.Length];
        var contents = new RectTransform[names.Length];
        var buttons = new UnityEngine.UI.Button[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            RectTransform page = pageHost.Find(names[i]) as RectTransform;
            if (page == null) page = Scroll(names[i], pageHost, out _, out _);
            Stretch(page);
            pages[i] = page.gameObject;
            contents[i] = page.Find("Viewport/Content") as RectTransform;
            var list = contents[i].GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            list.padding = new RectOffset(4, 12, 4, 12); list.spacing = 8;
            buttons[i] = tabsRoot.Find(names[i] + "Tab")?.GetComponent<UnityEngine.UI.Button>();
            if (buttons[i] == null) buttons[i] = Button(names[i] + "Tab", tabsRoot, titles[i], i == 0, 40, 38);
            var tabLayout = buttons[i].GetComponent<UnityEngine.UI.LayoutElement>();
            tabLayout.minWidth = 40; tabLayout.preferredWidth = 40; tabLayout.flexibleWidth = 1;
            var tabLabel = buttons[i].GetComponentInChildren<TMP_Text>();
            if (tabLabel != null) { tabLabel.fontSize = 14; tabLabel.textWrappingMode = TextWrappingModes.Normal; tabLabel.enableAutoSizing = true; tabLabel.fontSizeMin = 11; tabLabel.fontSizeMax = 14; }
        }

        // Move the original objects, including anonymous Hint nodes, in their original order.
        // Existing serialized input, slider and listener references remain intact.
        RectTransform legacy = card.Find("SettingsScroll/Viewport/Content") as RectTransform;
        if (legacy != null)
        {
            int section = 1;
            foreach (Transform child in legacy.Cast<Transform>().ToArray())
            {
                int destination = section;
                if (child.name == "AppearanceSettings") destination = 0;
                else if (child.name == "WakeWordSettings") destination = 2;
                else if (child.name == "VoiceServiceSettings") destination = section = 2;
                else if (child.name == "电脑操作Section") destination = section = 4;
                else if (child.name == "声音Section") destination = section = 2;
                else if (child.name == "连接Section" || child.name == "角色Section" || child.name == "模型接口Section") destination = section = 1;
                child.SetParent(contents[destination], false);
            }
            // Retain this path as a migration inbox for older setup tools.
            legacy.parent.parent.gameObject.SetActive(false);
        }
        foreach (var route in new[] { new { Name = "AppearanceSettings", Page = 0 },
            new { Name = "VoiceServiceSettings", Page = 2 }, new { Name = "WakeWordSettings", Page = 2 } })
        {
            Transform group = FindDescendant(card, route.Name);
            if (group != null && group.parent != contents[route.Page]) group.SetParent(contents[route.Page], false);
        }

        RectTransform appearanceGroup = FindDescendant(card, "AppearanceSettings") as RectTransform;
        if (appearanceGroup == null) appearanceGroup = contents[0];
        var lowPower = FindDescendant(card, "LowPowerMode")?.GetComponent<UnityEngine.UI.Toggle>();
        if (lowPower == null)
        {
            Section(appearanceGroup, "运行方式");
            lowPower = Toggle("LowPowerMode", appearanceGroup, "低功耗模式", false);
            Hint(appearanceGroup, "降低待机帧率，减少桌面资源占用。此项自动保存在本机。");
        }
        RectTransform memory = contents[3];
        RectTransform memoryPreferences = memory.Find("MemoryPreferences") as RectTransform;
        if (memoryPreferences == null)
        {
            memoryPreferences = Rect("MemoryPreferences", memory);
            Vertical(memoryPreferences, 0, 8);
            Section(memoryPreferences, "保存与记忆");
            Toggle("SaveConversationHistory", memoryPreferences, "保存聊天记录", false);
            Hint(memoryPreferences, "保存后可在聊天记录中查看。关闭后不再保存新的聊天记录。");
            Toggle("EnableLongTermMemory", memoryPreferences, "启用长期记忆", false);
            Hint(memoryPreferences, "允许丽塔记住你主动保存的信息，并在以后的对话中使用。默认关闭。");
        }
        Set(panel, "saveConversationHistoryToggle", memoryPreferences.Find("SaveConversationHistory").GetComponent<UnityEngine.UI.Toggle>());
        Set(panel, "enableLongTermMemoryToggle", memoryPreferences.Find("EnableLongTermMemory").GetComponent<UnityEngine.UI.Toggle>());
        RectTransform management = memory.Find("MemoryManagementRoot") as RectTransform;
        if (management == null)
        {
            management = Rect("MemoryManagementRoot", memory);
            Vertical(management, 0, 8);
            Section(management, "查看与管理");
        }
        var history = management.Find("History")?.GetComponent<UnityEngine.UI.Button>();
        if (history == null) history = Button("History", management, "聊天记录", false, 150, 40);
        var memories = management.Find("Memories")?.GetComponent<UnityEngine.UI.Button>();
        if (memories == null) memories = Button("Memories", management, "记忆管理", false, 150, 40);

        // Provider-specific labels make the two voice fields unambiguous.
        SetDescendantText(card, "edgeTtsVoiceInputLabel", "Edge 朗读音色");
        SetDescendantText(card, "ttsVoiceInputLabel", "OpenAI 兼容 TTS 音色");
        foreach (TMP_Text text in card.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.text == "Edge 使用系统音色名称；OpenAI 兼容服务使用其支持的 voice。")
                text.text = "按所选服务填写对应音色；GPT-SoVITS 使用下方参考音频设置。";
            if (text.text == "在聊天面板开启「朗读回复」。语音服务需要网络连接。")
                text.text = "在聊天面板开启「朗读回复」后，使用本页配置的语音服务。";
        }
        var controller = card.GetComponent<SettingsTabsController>() ?? card.gameObject.AddComponent<SettingsTabsController>();
        controller.Configure(card, titles, pages, buttons, lowPower, history, memories);
        EditorUtility.SetDirty(controller);
        EditorUtility.SetDirty(panel);
        DesktopPetModelLibrarySetup.ApplyToScene(canvas.gameObject.scene);
        DesktopPetActionSettingsSetup.ApplyToScene(canvas.gameObject.scene);
        if (panel.GetComponent<PetCharacterCards>() == null) Undo.AddComponent<PetCharacterCards>(panel.gameObject);
        if (contents[7].GetComponent<PetCharacterCardsPanel>() == null) Undo.AddComponent<PetCharacterCardsPanel>(contents[7].gameObject);
        if (contents[8].GetComponent<PetBackendLogsPanel>() == null) Undo.AddComponent<PetBackendLogsPanel>(contents[8].gameObject);
        // Existing pages keep their indices for migrations; display the character cards first.
        buttons[7].transform.SetAsFirstSibling();
        controller.ApplySidebarLayout();
        controller.ShowPage("角色卡");
    }

    private static Transform FindDescendant(Transform root, string name) => root != null
        ? root.GetComponentsInChildren<Transform>(true).FirstOrDefault(item => item != root && item.name == name) : null;

    private static void SetDescendantText(Transform root, string name, string text)
    {
        var target = FindDescendant(root, name)?.GetComponent<TMP_Text>();
        if (target != null) target.text = text;
    }

    private static void CompactChat(RectTransform chat)
    {
        DesktopPetChatLayoutUpgrade.ApplyToChat(chat);
    }

    private static void SetChildHeight(RectTransform parent, string name, float height)
    {
        RectTransform child = parent.Find(name) as RectTransform;
        if (child != null) Height(child, height);
    }

    private static void ExtendCompanionSettings(RectTransform canvas, GameObject services, Transform character, Camera camera,
        BackendSettingsPanel panel)
    {
        RectTransform settingsCard = canvas.Find("SettingsOverlay/SettingsCard") as RectTransform;
        RectTransform content = settingsCard != null ? settingsCard.Find("SettingsScroll/Viewport/Content") as RectTransform : null;
        if (content == null) throw new InvalidOperationException("The settings scroll content was not found.");
        var appearance = services.GetComponent<DesktopPetAppearance>() ?? services.AddComponent<DesktopPetAppearance>();
        Set(appearance, "characterRoot", character);
        Set(appearance, "worldCamera", camera);
        Set(appearance, "chatPanel", canvas.Find("ChatPanel") as RectTransform);
        Set(appearance, "settingsCard", settingsCard);
        Set(appearance, "contextMenuRoot", canvas.Find("ContextMenuRoot") as RectTransform);

        RectTransform appearanceGroup = FindDescendant(settingsCard, "AppearanceSettings") as RectTransform;
        if (appearanceGroup == null)
        {
            appearanceGroup = Rect("AppearanceSettings", content);
            Vertical(appearanceGroup, 0, 9);
            appearanceGroup.SetAsFirstSibling();
            Section(appearanceGroup, "外观");
            TextFixed("ModelSizeLabel", appearanceGroup, "宠物大小", 15, Ink, 24);
            var modelSize = Slider("ModelSize", appearanceGroup, 0.35f, 1.05f, 0.65f);
            Set(appearance, "modelSizeSlider", modelSize);
            TextFixed("ColorLabel", appearanceGroup, "面板背景色（#RRGGBB）", 15, Ink, 24);
            TMP_InputField color = Input("BackgroundColor", appearanceGroup, "#182130", 42, false);
            color.SetTextWithoutNotify("#182130");
            color.characterLimit = 7;
            Set(appearance, "backgroundColorInput", color);
            var status = Hint(appearanceGroup, "外观实时生效，并自动保存在本机。");
            status.name = "AppearanceStatus";
            Set(appearance, "appearanceStatus", status);
        }
        else
        {
            Set(appearance, "modelSizeSlider", appearanceGroup.Find("ModelSize").GetComponent<UnityEngine.UI.Slider>());
            Set(appearance, "backgroundColorInput", appearanceGroup.Find("BackgroundColor").GetComponent<TMP_InputField>());
            Set(appearance, "appearanceStatus", appearanceGroup.Find("AppearanceStatus").GetComponent<TMP_Text>());
        }
        // Remove an older opacity control if this scene was migrated during development.
        Transform opacityControl = appearanceGroup.Find("PanelOpacity");
        Transform opacityLabel = appearanceGroup.Find("OpacityLabel");
        if (opacityControl != null) opacityControl.gameObject.SetActive(false);
        if (opacityLabel != null) opacityLabel.gameObject.SetActive(false);

        RectTransform voiceGroup = FindDescendant(settingsCard, "VoiceServiceSettings") as RectTransform;
        if (voiceGroup == null)
        {
            Transform oldVoiceSection = FindDescendant(settingsCard, "声音Section");
            int index = oldVoiceSection != null ? oldVoiceSection.GetSiblingIndex() : content.childCount;
            voiceGroup = Rect("VoiceServiceSettings", content);
            Vertical(voiceGroup, 0, 9);
            voiceGroup.SetSiblingIndex(index);
            if (oldVoiceSection != null)
            {
                oldVoiceSection.SetParent(voiceGroup, false);
                oldVoiceSection.GetComponent<TMP_Text>().text = "语音合成 TTS";
            }
            else Section(voiceGroup, "语音合成 TTS");
            FieldInput(panel, voiceGroup, "ttsProviderInput", "TTS 服务类型", "edge / openai / gpt_sovits", 42, false);
            FieldInput(panel, voiceGroup, "ttsBaseUrlInput", "TTS 接口地址", "OpenAI 兼容 / GPT-SoVITS 服务地址", 42, false);
            Hint(voiceGroup, "地址留空使用主模型接口；填写独立地址时，请同时填写该服务的独立密钥。");
            var ttsKey = FieldInput(panel, voiceGroup, "ttsApiKeyInput", "独立 TTS 密钥", "留空保留已保存密钥", 42, false);
            ttsKey.contentType = TMP_InputField.ContentType.Password;
            var ttsKeyStatus = Hint(voiceGroup, "尚未读取 TTS 密钥状态。");
            ttsKeyStatus.name = "TtsKeyStatus";
            Set(panel, "ttsApiKeyStatusText", ttsKeyStatus);
            FieldInput(panel, voiceGroup, "ttsModelInput", "TTS 模型", "例如 gpt-4o-mini-tts", 42, false);
            Transform voiceLabel = FindDescendant(settingsCard, "ttsVoiceInputLabel");
            Transform voiceInput = FindDescendant(settingsCard, "ttsVoiceInput");
            if (voiceLabel != null) voiceLabel.SetParent(voiceGroup, false);
            if (voiceInput != null) voiceInput.SetParent(voiceGroup, false);
            else FieldInput(panel, voiceGroup, "ttsVoiceInput", "音色 / Voice", "例如 zh-CN-XiaoxiaoNeural 或 alloy", 42, false);
            Hint(voiceGroup, "Edge 使用系统音色名称；OpenAI 兼容服务使用其支持的 voice。");

            Section(voiceGroup, "语音识别 ASR");
            FieldInput(panel, voiceGroup, "asrBaseUrlInput", "独立 ASR 接口地址", "https://api.openai.com/v1", 42, false);
            Hint(voiceGroup, "地址留空使用主模型接口；独立接口不会自动使用主模型的密钥。");
            var asrKey = FieldInput(panel, voiceGroup, "asrApiKeyInput", "独立 ASR 密钥", "留空保留已保存密钥", 42, false);
            asrKey.contentType = TMP_InputField.ContentType.Password;
            var asrKeyStatus = Hint(voiceGroup, "尚未读取 ASR 密钥状态。");
            asrKeyStatus.name = "AsrKeyStatus";
            Set(panel, "asrApiKeyStatusText", asrKeyStatus);
            FieldInput(panel, voiceGroup, "asrModelInput", "ASR 模型", "例如 whisper-1", 42, false);

            Section(voiceGroup, "GPT-SoVITS");
            Hint(voiceGroup, "选择 gpt_sovits 时使用；参考音频路径应能被 GPT-SoVITS 服务访问。");
            FieldInput(panel, voiceGroup, "sovitsRefAudioInput", "参考音频路径", "D:/voices/reference.wav", 42, false);
            FieldInput(panel, voiceGroup, "sovitsPromptTextInput", "参考音频对应文本", "填写参考音频中说出的内容", 76, true);
            FieldInput(panel, voiceGroup, "sovitsPromptLangInput", "参考音频语言", "zh / en / ja", 42, false);
            FieldInput(panel, voiceGroup, "sovitsTextLangInput", "合成语言", "zh / en / ja", 42, false);
        }
        else
        {
            foreach (string field in new[] { "ttsProviderInput", "ttsBaseUrlInput", "ttsApiKeyInput", "ttsModelInput", "ttsVoiceInput",
                "asrBaseUrlInput", "asrApiKeyInput", "asrModelInput", "sovitsRefAudioInput", "sovitsPromptTextInput", "sovitsPromptLangInput", "sovitsTextLangInput" })
                Set(panel, field, voiceGroup.Find(field).GetComponent<TMP_InputField>());
            Set(panel, "ttsApiKeyStatusText", voiceGroup.Find("TtsKeyStatus").GetComponent<TMP_Text>());
            Set(panel, "asrApiKeyStatusText", voiceGroup.Find("AsrKeyStatus").GetComponent<TMP_Text>());
        }
        Transform edgeVoice = voiceGroup.Find("edgeTtsVoiceInput");
        if (edgeVoice == null)
        {
            TMP_InputField input = FieldInput(panel, voiceGroup, "edgeTtsVoiceInput", "Edge 音色", "zh-CN-XiaoxiaoNeural", 42, false);
            voiceGroup.Find("edgeTtsVoiceInputLabel").SetSiblingIndex(3);
            input.transform.SetSiblingIndex(4);
        }
        else Set(panel, "edgeTtsVoiceInput", edgeVoice.GetComponent<TMP_InputField>());
        Transform apiVoiceLabel = voiceGroup.Find("ttsVoiceInputLabel");
        if (apiVoiceLabel != null) apiVoiceLabel.GetComponent<TMP_Text>().text = "OpenAI 兼容 TTS 音色 / Voice";
        EditorUtility.SetDirty(appearance);
    }

    private static UnityEngine.UI.Slider Slider(string name, Transform parent, float min, float max, float value)
    {
        RectTransform root = Rect(name, parent);
        Height(root, 34);
        var slider = root.gameObject.AddComponent<UnityEngine.UI.Slider>();
        RectTransform track = Panel("Track", root, Field);
        track.anchorMin = new Vector2(0, 0.5f); track.anchorMax = new Vector2(1, 0.5f);
        track.sizeDelta = new Vector2(-12, 8);
        RectTransform fillArea = Rect("Fill Area", root); Stretch(fillArea, 6);
        RectTransform fill = Panel("Fill", fillArea, Accent);
        fill.anchorMin = new Vector2(0, 0.5f); fill.anchorMax = new Vector2(1, 0.5f);
        fill.sizeDelta = new Vector2(0, 8);
        RectTransform handleArea = Rect("Handle Area", root); Stretch(handleArea, 10);
        RectTransform handle = Panel("Handle", handleArea, Ink);
        handle.sizeDelta = new Vector2(20, 24);
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handle.GetComponent<UnityEngine.UI.Image>();
        slider.minValue = min; slider.maxValue = max; slider.value = value;
        slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        return slider;
    }

    private static void MakeCompanionPanelsOpaque(RectTransform canvas)
    {
        foreach (string path in new[] { "ChatPanel", "SettingsOverlay/SettingsCard", "ContextMenuRoot" })
        {
            var image = canvas.Find(path)?.GetComponent<UnityEngine.UI.Image>();
            if (image != null) { Color color = image.color; color.a = 1f; image.color = color; }
        }
        var overlay = canvas.Find("SettingsOverlay")?.GetComponent<UnityEngine.UI.Image>();
        if (overlay != null) { overlay.color = Color.clear; overlay.raycastTarget = false; }
    }

    private static void BuildSettings(RectTransform canvas, BackendSettingsPanel controller, BackendSettingsApi api,
        SettingsPanelController settingsController)
    {
        RectTransform overlay = Panel("SettingsOverlay", canvas, Color.clear, false);
        overlay.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
        Stretch(overlay);
        Set(settingsController, "panelRoot", overlay.gameObject);
        Set(controller, "settingsApi", api);

        RectTransform card = Panel("SettingsCard", overlay, Card);
        card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
        card.sizeDelta = new Vector2(690, 740);
        Vertical(card, 24, 12);
        RectTransform header = Row("Header", card, 44, 12);
        TMP_Text heading = Text("Heading", header, "让丽塔更懂你", 26, Ink); Flexible(heading.rectTransform);
        var close = Button("Close", header, "关闭", false, 72, 36);
        UnityEventTools.AddPersistentListener(close.onClick, settingsController.Close);
        TextFixed("Hint", card, "连接模型、调整性格与声音。修改后记得保存。", 15, Muted, 24);

        RectTransform scrollRoot = Scroll("SettingsScroll", card, out RectTransform content, out UnityEngine.UI.ScrollRect scroll);
        var scrollLayout = scrollRoot.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        scrollLayout.minHeight = 250;
        scrollLayout.flexibleHeight = 1;
        var list = content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        list.spacing = 9;
        list.padding = new RectOffset(4, 12, 6, 6);

        Section(content, "连接");
        FieldInput(controller, content, "backendServiceUrlInput", "本地后端地址", "http://127.0.0.1:18080", 42, false);
        Hint(content, "地址变更后先点击「重新读取」，再编辑后端设置。");
        Section(content, "角色");
        FieldInput(controller, content, "petNameInput", "宠物名称", "Lita", 42, false);
        FieldInput(controller, content, "personaInput", "性格与说话方式", "描述你希望丽塔如何与你交流…", 96, true);
        Section(content, "模型接口");
        FieldInput(controller, content, "llmProviderInput", "服务类型", "openai / ollama / mock", 42, false);
        Hint(content, "OpenAI 兼容接口填写 openai；本地模型填写 ollama；离线体验填写 mock。");
        FieldInput(controller, content, "llmModelInput", "模型名称", "例如 gpt-4.1-mini", 42, false);
        FieldInput(controller, content, "baseUrlInput", "接口地址 Base URL", "https://api.openai.com/v1", 42, false);
        TMP_InputField key = FieldInput(controller, content, "apiKeyInput", "API 密钥", "填写新密钥；留空保留已有密钥", 42, false);
        key.contentType = TMP_InputField.ContentType.Password;
        key.asteriskChar = '●';
        var keyStatus = Hint(content, "尚未读取密钥状态。");
        Set(controller, "apiKeyStatusText", keyStatus);
        Section(content, "声音");
        FieldInput(controller, content, "ttsVoiceInput", "朗读音色", "zh-CN-XiaoxiaoNeural", 42, false);
        Hint(content, "在聊天面板开启「朗读回复」。语音服务需要网络连接。");
        Section(content, "电脑操作");
        var rpa = Toggle("EnableRealRpa", content, "允许执行已确认的电脑操作", false);
        Height(rpa.GetComponent<RectTransform>(), 36);
        Set(controller, "enableRealRpaToggle", rpa);
        Hint(content, "丽塔提出操作建议后，仍需点击「执行建议操作」。");
        FieldInput(controller, content, "allowedAppsInput", "允许打开的应用（逗号分隔）", "notepad, calc, explorer, msedge", 42, false);

        TMP_Text status = TextFixed("Status", card, "后端配置会保存在本机。", 14, Muted, 42);
        status.richText = false;
        Set(controller, "statusText", status);
        RectTransform footer = Row("Footer", card, 44, 12);
        var refresh = Button("Refresh", footer, "重新读取", false, 136, 44);
        RectTransform spacer = Rect("Spacer", footer); Flexible(spacer);
        var save = Button("Save", footer, "保存设置", true, 160, 44);
        Set(controller, "saveButton", save); Set(controller, "refreshButton", refresh);
        UnityEventTools.AddPersistentListener(save.onClick, controller.Save);
        UnityEventTools.AddPersistentListener(refresh.onClick, controller.Refresh);
        overlay.gameObject.SetActive(false);
    }

    private static TMP_InputField FieldInput(BackendSettingsPanel panel, RectTransform parent, string field,
        string label, string placeholder, float height, bool multiline)
    {
        TextFixed(field + "Label", parent, label, 15, Ink, 25);
        TMP_InputField input = Input(field, parent, placeholder, height, multiline);
        Set(panel, field, input);
        return input;
    }

    private static void Section(RectTransform parent, string value)
    {
        var label = TextFixed(value + "Section", parent, value, 18, Accent, 38);
        label.alignment = TextAlignmentOptions.BottomLeft;
    }
    private static TMP_Text Hint(RectTransform parent, string value)
    {
        TMP_Text text = Text("Hint", parent, value, 13, Muted);
        text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        if (parent != null) rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        return rect;
    }

    private static RectTransform Panel(string name, Transform parent, Color color, bool rounded = true)
    {
        RectTransform rect = Rect(name, parent);
        var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
        image.color = color;
        if (rounded) { image.sprite = round; image.type = UnityEngine.UI.Image.Type.Sliced; }
        image.raycastTarget = true;
        return rect;
    }

    private static TMP_Text Text(string name, Transform parent, string value, float size, Color color)
    {
        RectTransform rect = Rect(name, parent);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = size;
        text.color = color;
        text.text = value;
        text.richText = false;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.TopLeft;
        return text;
    }
    private static TMP_Text TextFixed(string name, Transform parent, string value, float size, Color color, float height)
    {
        TMP_Text text = Text(name, parent, value, size, color);
        Height(text.rectTransform, height);
        return text;
    }

    private static UnityEngine.UI.Button Button(string name, Transform parent, string label, bool primary, float width, float height)
    {
        RectTransform rect = Panel(name, parent, primary ? Accent : Field);
        var layout = rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        layout.minWidth = width; layout.preferredWidth = width;
        layout.minHeight = height; layout.preferredHeight = height;
        var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = rect.GetComponent<UnityEngine.UI.Image>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.84f, 0.93f, 0.99f);
        colors.pressedColor = new Color(0.65f, 0.77f, 0.86f);
        colors.disabledColor = new Color(0.55f, 0.58f, 0.62f, 0.7f);
        button.colors = colors;
        TMP_Text text = Text("Label", rect, label, 16, primary ? new Color32(17, 48, 41, 255) : Ink);
        Stretch(text.rectTransform, 6);
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return button;
    }

    private static TMP_InputField Input(string name, Transform parent, string placeholder, float height, bool multiline)
    {
        RectTransform rect = Panel(name, parent, Field);
        Height(rect, height);
        TMP_InputField input = rect.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = rect.GetComponent<UnityEngine.UI.Image>();
        RectTransform viewport = Rect("Text Area", rect);
        Stretch(viewport, 12);
        viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        TMP_Text text = Text("Text", viewport, "", 17, Ink);
        Stretch(text.rectTransform);
        TMP_Text hint = Text("Placeholder", viewport, placeholder, 16, Muted);
        Stretch(hint.rectTransform);
        hint.fontStyle = FontStyles.Normal;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = hint;
        input.fontAsset = font;
        input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
        input.caretColor = Accent;
        input.customCaretColor = true;
        input.selectionColor = new Color(0.57f, 0.91f, 0.80f, 0.28f);
        input.characterLimit = multiline ? 4000 : 2048;
        PetInputPlaceholder.Install(input);
        return input;
    }

    private static UnityEngine.UI.Toggle Toggle(string name, Transform parent, string label, bool value)
    {
        RectTransform rect = Rect(name, parent);
        Height(rect, 30);
        var toggle = rect.gameObject.AddComponent<UnityEngine.UI.Toggle>();
        RectTransform box = Panel("Box", rect, Field);
        box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
        box.pivot = new Vector2(0, 0.5f);
        box.sizeDelta = new Vector2(22, 22);
        RectTransform mark = Panel("Check", box, Accent);
        Stretch(mark, 5);
        toggle.targetGraphic = box.GetComponent<UnityEngine.UI.Image>();
        toggle.graphic = mark.GetComponent<UnityEngine.UI.Image>();
        TMP_Text text = Text("Label", rect, label, 14, Ink);
        Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(31, 0);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        // A transparent root graphic lets the entire label respond to clicks.
        var hit = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
        hit.color = Color.clear;
        hit.raycastTarget = true;
        toggle.isOn = value;
        return toggle;
    }

    private static RectTransform Scroll(string name, Transform parent, out RectTransform content, out UnityEngine.UI.ScrollRect scroll)
    {
        RectTransform rect = Rect(name, parent);
        scroll = rect.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
        // The Mask needs opaque geometry to write its stencil. showMaskGraphic hides
        // that geometry; a transparent Image would alpha-clip the stencil away.
        RectTransform viewport = Panel("Viewport", rect, Color.white, false);
        Stretch(viewport);
        var mask = viewport.gameObject.AddComponent<UnityEngine.UI.Mask>();
        mask.showMaskGraphic = false;
        content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1);
        content.sizeDelta = Vector2.zero;
        var vertical = Vertical(content, 6, 8);
        vertical.childForceExpandHeight = false;
        var fitter = content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28;
        scroll.inertia = true;
        return rect;
    }

    private static UnityEngine.UI.VerticalLayoutGroup Vertical(RectTransform rect, int padding, float spacing)
    {
        var group = rect.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.spacing = spacing;
        group.childControlWidth = true; group.childControlHeight = true;
        group.childForceExpandWidth = true; group.childForceExpandHeight = false;
        return group;
    }
    private static void Horizontal(RectTransform rect, int padding, float spacing)
    {
        var group = rect.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.spacing = spacing;
        group.childControlWidth = true; group.childControlHeight = true;
        group.childForceExpandWidth = false; group.childForceExpandHeight = false;
        group.childAlignment = TextAnchor.MiddleLeft;
    }
    private static RectTransform Row(string name, Transform parent, float height, float spacing)
    {
        RectTransform rect = Rect(name, parent);
        Height(rect, height);
        Horizontal(rect, 0, spacing);
        return rect;
    }
    private static void Height(RectTransform rect, float value)
    {
        var layout = rect.GetComponent<UnityEngine.UI.LayoutElement>() ?? rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        layout.minHeight = value; layout.preferredHeight = value;
    }
    private static void Flexible(RectTransform rect)
    {
        var layout = rect.GetComponent<UnityEngine.UI.LayoutElement>() ?? rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        layout.minWidth = 0; layout.flexibleWidth = 1;
    }
    private static void Stretch(RectTransform rect, float inset = 0)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset);
    }

    private static TMP_FontAsset GetOrCreateFont()
    {
        TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        if (existing != null) return existing;
        TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 48);
        if (asset == null) asset = TMP_FontAsset.CreateFontAsset("Microsoft JhengHei", "Regular", 48);
        if (asset == null) asset = TMP_FontAsset.CreateFontAsset("Noto Sans CJK SC", "Regular", 48);
        if (asset == null) throw new InvalidOperationException("A Chinese system font is required (Microsoft YaHei or Noto Sans CJK SC).");
        asset.name = "DesktopPet UI Chinese";
        asset.isMultiAtlasTexturesEnabled = true;
        asset.material.name = "DesktopPet UI Chinese Material";
        AssetDatabase.CreateAsset(asset, FontPath);
        AssetDatabase.AddObjectToAsset(asset.material, asset);
        foreach (Texture2D atlas in asset.atlasTextures)
            if (atlas != null) AssetDatabase.AddObjectToAsset(atlas, asset);
        return asset;
    }

    private static Sprite GetOrCreateRoundedSprite()
    {
        Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath);
        if (existing != null) return existing;
        const int size = 64;
        const float radius = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
            {
                float dx = Mathf.Max(Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0);
                float dy = Mathf.Max(Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0);
                float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
        texture.SetPixels32(pixels); texture.Apply();
        File.WriteAllBytes(RoundedSpritePath, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(RoundedSpritePath, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(RoundedSpritePath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spriteBorder = new Vector4(18, 18, 18, 18);
        importer.spritePixelsPerUnit = 100;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath);
    }

    private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target);
        SerializedProperty field = serialized.FindProperty(property);
        if (field == null) throw new MissingFieldException(target.GetType().Name, property);
        field.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
    private static void Set(UnityEngine.Object target, string property, bool value)
    {
        var serialized = new SerializedObject(target);
        SerializedProperty field = serialized.FindProperty(property);
        if (field == null) throw new MissingFieldException(target.GetType().Name, property);
        field.boolValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
