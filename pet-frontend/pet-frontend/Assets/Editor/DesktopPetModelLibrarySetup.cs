using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Move model controls to their own page without replacing serialized references.</summary>
public static class DesktopPetModelLibrarySetup
{
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Ink = new Color32(238, 244, 251, 255);
    private static readonly Color Muted = new Color32(173, 187, 205, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);
    private static TMP_FontAsset font;
    private static Sprite round;

    [MenuItem("Desktop Pet/Upgrade Runtime Model Library")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before upgrading the model library.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath) throw new InvalidOperationException("Open DesktopPet.unity before upgrading its model library.");
        ApplyToScene(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Unable to save the model library settings.");
        Debug.Log("DESKTOP_PET_MODEL_LIBRARY_READY: runtime import, model selection, built-in fallback and placement rebinding.");
    }

    public static void ApplyToScene(Scene scene)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        PetBackendHttpClient client = roots.SelectMany(root => root.GetComponentsInChildren<PetBackendHttpClient>(true)).Single();
        Transform character = roots.FirstOrDefault(root => root.name == "PetCharacterRoot")?.transform;
        SettingsTabsController tabs = roots.SelectMany(root => root.GetComponentsInChildren<SettingsTabsController>(true)).FirstOrDefault();
        RectTransform content = tabs != null ? tabs.FindPageContent("模型导入") : null;
        if (character == null || content == null) throw new InvalidOperationException("The existing character and model import page are required. Run Upgrade Settings Pages first.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
        round = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/DesktopPetUIRound.png");
        if (font == null) throw new InvalidOperationException("The existing DesktopPetUIFont asset was not found.");
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Add runtime model library");
        PetModelLibrary library = Ensure<PetModelLibrary>(client.gameObject);
        Set(library, "characterRoot", character);
        RectTransform group = roots.SelectMany(root => root.GetComponentsInChildren<RectTransform>(true)).FirstOrDefault(item => item.name == "ModelLibrarySettings");
        if (group == null) group = Rect("ModelLibrarySettings", content);
        else if (group.parent != content) Undo.SetTransformParent(group, content, "Move model library to its page");
        group.SetAsFirstSibling();
        var vertical = Ensure<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        vertical.spacing = 8;
        vertical.padding = new RectOffset(0, 0, 4, 12);
        vertical.childControlWidth = vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        Label("Title", group, "角色模型", 19, Ink, 28);
        TMP_Dropdown dropdown = BuildDropdown(group);
        RectTransform actions = Rect("Actions", group);
        Height(actions, 40);
        var horizontal = Ensure<UnityEngine.UI.HorizontalLayoutGroup>(actions.gameObject);
        horizontal.spacing = 8;
        horizontal.childControlWidth = horizontal.childControlHeight = true;
        horizontal.childForceExpandWidth = true; horizontal.childForceExpandHeight = false;
        var refresh = Button("Refresh", actions, "刷新列表", false);
        var select = Button("Switch", actions, "切换模型", true);
        var import = Button("Import", actions, "导入模型", false);
        Label("Hint", group, "导入准备完成后，从上方列表选择模型并点击“切换模型”。原模型与贴图不会被修改。", 13, Muted, 56);
        TMP_Text status = Label("ModelStatus", group, "可选择内置丽塔，或导入其他角色模型。", 13, Muted, 62);
        status.overflowMode = TextOverflowModes.Ellipsis;
        var panel = Ensure<PetModelSettingsPanel>(group.gameObject);
        Set(panel, "library", library); Set(panel, "modelDropdown", dropdown);
        Set(panel, "refreshButton", refresh); Set(panel, "switchButton", select); Set(panel, "importButton", import);
        Set(panel, "statusText", status);
        Label("FormatsTitle", group, "支持的文件格式", 19, Ink, 30);
        Label("PetmodelHelp", group, ".petmodel · 已准备好的桌宠角色包\n推荐使用。可直接在运行中的桌宠导入，不需要 Unity 或 Blender。包须与当前 Windows 客户端兼容；旧包失败时重新导入原始模型。", 14, Ink, 108);
        Label("FbxHelp", group, ".fbx · 带人形骨架的三维角色\n完整发行包内置 FBX 导入组件，不需要安装 Blender 或 Unity。需带可识别的人形骨骼与蒙皮，贴图使用 PNG/JPG/TGA 并放在模型目录内。当前 FBX 通用导入保留骨骼、材质与贴图，不读取原文件的面部形变。", 14, Ink, 138);
        Label("PmxHelp", group, ".pmx · MMD 角色模型\n支持 PMX 2.0/2.1，运行时直接导入网格、骨骼、材质和顶点表情，无需 Blender 或 Unity 工程。完整解压模型包，保留贴图目录与相对路径，不要只复制 PMX。骨骼动作会适配内置动作源，原 MMD IK、刚体物理与材质变形暂不执行。", 14, Ink, 152);
        Label("TutorialTitle", group, "导入教程", 19, Ink, 30);
        Label("Tutorial", group, "1. 解压模型和贴图到本机文件夹；模型源文件不超过 1 GB，网格不超过 60 万顶点。\n2. 使用完整发行包启动桌宠，确保本地后端已启动即可；无需打开任何开发工具。\n3. 点击“导入模型”，选择 .pmx、.fbx 或 .petmodel。等待转换与加载，成功后自动切换，原文件保持不变。\n4. 可从列表切回其他模型；下次启动恢复上次成功选择。也可在角色卡中将导入的模型添加为皮肤。", 14, Ink, 202);
        Label("TroubleshootingTitle", group, "导入问题", 19, Ink, 30);
        Label("Troubleshooting", group, "贴图丢失：PNG/JPG/TGA 贴图必须保存在模型目录内，保留相对路径后重新导入。\n无法导入 FBX：使用包含 FBX 导入组件的完整发行包，不要只复制客户端 EXE。\n骨架不兼容：需要头、双臂、双腿等可识别的人形骨骼；武器、纯静态物体不能独立作为角色。\n模型页不导入 VRM、GLB/GLTF、OBJ 或 ZIP；VMD 请到“动作管理”导入。衣裙物理为有限适配，切换失败会保留当前角色。", 14, Muted, 192);
        Undo.CollapseUndoOperations(undoGroup);
        EditorUtility.SetDirty(library); EditorUtility.SetDirty(panel);
    }

    internal static TMP_Dropdown BuildDropdown(RectTransform parent, string name = "ModelDropdown")
    {
        RectTransform root = Rect(name, parent);
        Height(root, 44);
        var background = Paint(root, Field);
        var dropdown = Ensure<TMP_Dropdown>(root.gameObject);
        dropdown.targetGraphic = background;
        dropdown.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        TMP_Text caption = Text("Label", root, "内置 · 丽塔", 16, Ink);
        Stretch(caption.rectTransform, 12);
        caption.rectTransform.offsetMax = new Vector2(-34, -8);
        caption.alignment = TextAlignmentOptions.MidlineLeft;
        caption.textWrappingMode = TextWrappingModes.NoWrap;
        caption.overflowMode = TextOverflowModes.Ellipsis;
        // ASCII remains available in every supported system-font fallback.
        TMP_Text arrow = Text("Arrow", root, "v", 18, Ink);
        arrow.rectTransform.anchorMin = arrow.rectTransform.anchorMax = new Vector2(1, 0.5f);
        arrow.rectTransform.pivot = new Vector2(1, 0.5f);
        arrow.rectTransform.sizeDelta = new Vector2(26, 30);
        arrow.rectTransform.anchoredPosition = new Vector2(-6, 0);
        arrow.alignment = TextAlignmentOptions.Center;

        RectTransform template = Rect("Template", root);
        template.gameObject.SetActive(true);
        template.anchorMin = new Vector2(0, 0); template.anchorMax = new Vector2(1, 0);
        template.pivot = new Vector2(0.5f, 1); template.sizeDelta = new Vector2(0, 200);
        template.anchoredPosition = new Vector2(0, -4);
        Paint(template, Field);
        var scroll = Ensure<UnityEngine.UI.ScrollRect>(template.gameObject);
        RectTransform viewport = Rect("Viewport", template);
        Stretch(viewport, 4);
        Paint(viewport, Field);
        var mask = Ensure<UnityEngine.UI.Mask>(viewport.gameObject); mask.showMaskGraphic = false;
        RectTransform content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1); content.sizeDelta = new Vector2(0, 38); content.anchoredPosition = Vector2.zero;
        // TMP_Dropdown positions and sizes cloned items itself, so this template has no layout fitter.
        RectTransform item = Rect("Item", content);
        item.anchorMin = new Vector2(0, 0.5f); item.anchorMax = new Vector2(1, 0.5f);
        item.sizeDelta = new Vector2(0, 38); item.anchoredPosition = Vector2.zero;
        var itemBackground = Paint(item, Field);
        var toggle = Ensure<UnityEngine.UI.Toggle>(item.gameObject);
        toggle.targetGraphic = itemBackground;
        RectTransform check = Rect("Check", item);
        check.anchorMin = check.anchorMax = new Vector2(0, 0.5f); check.pivot = new Vector2(0, 0.5f);
        check.sizeDelta = new Vector2(7, 20); check.anchoredPosition = new Vector2(6, 0);
        var checkImage = Paint(check, Accent); checkImage.raycastTarget = false;
        toggle.graphic = checkImage; toggle.isOn = true;
        TMP_Text label = Text("ItemLabel", item, "模型", 15, Ink);
        Stretch(label.rectTransform, 8); label.rectTransform.offsetMin = new Vector2(23, 4);
        label.alignment = TextAlignmentOptions.MidlineLeft; label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        scroll.content = content; scroll.viewport = viewport;
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24;
        dropdown.template = template; dropdown.captionText = caption; dropdown.itemText = label;
        dropdown.ClearOptions(); dropdown.AddOptions(new System.Collections.Generic.List<string> { "内置 · 丽塔" });
        template.gameObject.SetActive(false);
        return dropdown;
    }

    internal static UnityEngine.UI.Button Button(string name, Transform parent, string caption, bool primary)
    {
        RectTransform root = Rect(name, parent); Height(root, 40);
        var layout = Ensure<UnityEngine.UI.LayoutElement>(root.gameObject); layout.minWidth = 76; layout.flexibleWidth = 1;
        var image = Paint(root, primary ? Accent : Field);
        var button = Ensure<UnityEngine.UI.Button>(root.gameObject); button.targetGraphic = image;
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        TMP_Text text = Text("Label", root, caption, 14, primary ? new Color32(17, 48, 41, 255) : Ink);
        Stretch(text.rectTransform, 4); text.alignment = TextAlignmentOptions.Center;
        return button;
    }
    internal static T Ensure<T>(GameObject target) where T : Component => target.GetComponent<T>() ?? Undo.AddComponent<T>(target);
    internal static RectTransform Rect(string name, Transform parent)
    {
        if (parent.Find(name) is RectTransform existing) return existing;
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Add " + name); go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }
    internal static UnityEngine.UI.Image Paint(RectTransform root, Color color)
    {
        var image = Ensure<UnityEngine.UI.Image>(root.gameObject); Undo.RecordObject(image, "Style " + root.name);
        image.sprite = round; image.type = UnityEngine.UI.Image.Type.Sliced; image.color = color; image.raycastTarget = true;
        return image;
    }
    internal static TMP_Text Text(string name, Transform parent, string value, float size, Color color)
    {
        var text = Ensure<TextMeshProUGUI>(Rect(name, parent).gameObject); Undo.RecordObject(text, "Configure " + name);
        text.font = font; text.fontSize = size; text.color = color; text.text = value;
        text.raycastTarget = false; text.richText = false; text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }
    internal static TMP_Text Label(string name, Transform parent, string value, float size, Color color, float height)
    {
        TMP_Text text = Text(name, parent, value, size, color);
        Height(text.rectTransform, height);
        // TMP supplies preferred height at the current width, including narrow windows.
        text.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = -1;
        return text;
    }
    internal static void Height(RectTransform root, float height)
    { var layout = Ensure<UnityEngine.UI.LayoutElement>(root.gameObject); layout.minHeight = layout.preferredHeight = height; }
    internal static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.one * inset; rect.offsetMax = Vector2.one * -inset;
    }
    internal static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target); SerializedProperty field = serialized.FindProperty(property);
        if (field == null) throw new MissingFieldException(target.GetType().Name, property);
        field.objectReferenceValue = value; serialized.ApplyModifiedProperties();
    }
}
