using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using static DesktopPetModelLibrarySetup;

/// <summary>Idempotent, reference-preserving authoring of the action settings page.</summary>
public static class DesktopPetActionSettingsSetup
{
    private static readonly Color Ink = new Color32(238, 244, 251, 255);
    private static readonly Color Muted = new Color32(173, 187, 205, 255);
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Accent = new Color32(147, 232, 205, 255);

    public static void ApplyToScene(Scene scene)
    {
        var tabs = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<SettingsTabsController>(true)).Single();
        RectTransform content = tabs.FindPageContent("动作管理");
        if (content == null) throw new InvalidOperationException("Run Upgrade Settings Pages first.");
        RectTransform group = Rect("ActionManagement", content);
        var vertical = Ensure<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        vertical.spacing = 8; vertical.padding = new RectOffset(0, 0, 4, 12);
        vertical.childControlWidth = vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        var panel = Ensure<PetActionSettingsPanel>(group.gameObject);
        Label("Title", group, "动作管理", 19, Ink, 30);
        Label("Hint", group, "动作开关与自定义组合自动保存在本机。内置默认动作不可删除；关闭待机后保持静止自然站姿。", 14, Muted, 64);
        Set(panel, "actionDropdown", BuildDropdown(group, "ActionDropdown"));
        Set(panel, "detailsText", Label("Details", group, "选择动作以管理。", 14, Ink, 100));
        Set(panel, "enabledToggle", Toggle("ActionEnabled", group, "启用此动作"));
        Label("SelectedTriggerLabel", group, "自定义动作的自动触发", 14, Ink, 24);
        Set(panel, "selectedTriggerDropdown", BuildDropdown(group, "SelectedActionTrigger"));
        Set(panel, "saveTriggerButton", Button("SaveActionTrigger", group, "保存触发方式", false));
        RectTransform manage = Row("ManageButtons", group);
        Set(panel, "previewButton", Button("Preview", manage, "预览动作", true));
        Set(panel, "deleteButton", Button("Delete", manage, "删除自定义动作", false));
        Set(panel, "statusText", Label("ActionStatus", group, "内置动作与自定义动作都可随时开启或关闭。", 14, Accent, 74));
        Label("CreateTitle", group, "添加自定义组合", 19, Ink, 30);
        Label("CreateHint", group, "把现有动作组合成新的回应，可设置每步时长、整体速度与自动触发。需要外部动作时，使用下方的动作文件导入。", 14, Muted, 64);
        Label("NameLabel", group, "新动作名称", 14, Ink, 24);
        Set(panel, "nameInput", Input("ActionName", group, "例如：问候舰长", 24));
        Label("StepLabel", group, "选择步骤动作", 14, Ink, 24);
        Set(panel, "stepDropdown", BuildDropdown(group, "StepDropdown"));
        Label("SecondsLabel", group, "此步骤持续秒数（0.5～10）", 14, Ink, 24);
        Set(panel, "secondsInput", Input("StepSeconds", group, "2", 8));
        RectTransform edit = Row("StepButtons", group);
        Set(panel, "addStepButton", Button("AddStep", edit, "加入步骤", false));
        Set(panel, "removeStepButton", Button("RemoveStep", edit, "移除最后一步", false));
        Set(panel, "draftText", Label("DraftSteps", group, "尚未添加步骤。例：挥手 2 秒 → 点头 1.5 秒。", 14, Ink, 150));
        Label("SpeedLabel", group, "整体播放速度（0.5～2 倍）", 14, Ink, 24);
        Set(panel, "speedInput", Input("ActionSpeed", group, "1", 8));
        Label("TriggerLabel", group, "自动触发方式", 14, Ink, 24);
        Set(panel, "triggerDropdown", BuildDropdown(group, "TriggerDropdown"));
        Label("TriggerHint", group, "可选“由大模型自行决定”，让模型从当前角色启用的动作目录里选择。固定触发每项只绑定一个动作；仅手动预览不会被模型自主调用。关闭的基础动作会跳过，触摸遮挡仍由交互控制。", 14, Muted, 110);
        Set(panel, "createButton", Button("CreateAction", group, "保存新动作", true));
        Label("ImportTitle", group, "导入动作文件", 19, Ink, 30);
        Label("ImportTutorial", group, "支持 VMD 0002 骨骼动作，无需安装 Blender 或 Unity。请选择与角色兼容的 MMD 骨架动作，单文件不超过 32 MB、时长不超过三分钟。\n目前播放骨骼旋转与受限上下位移，不执行文件中的相机、光照、表情、MMD IK 或物理配置；依赖 IK 的舞蹈可能需要先烘焙为 FK。FBX 动作请先由制作者导出为兼容 VMD。", 14, Muted, 190);
        Label("ImportNameLabel", group, "导入动作名称", 14, Ink, 24);
        Set(panel, "importNameInput", Input("ImportMotionName", group, "例如：礼貌行礼", 24));
        Label("ImportPathLabel", group, "本机动作文件路径", 14, Ink, 24);
        Set(panel, "importPathInput", Input("ImportMotionPath", group, "选择 .vmd 文件，或粘贴完整路径", 4096));
        Set(panel, "pickMotionButton", Button("PickMotionFile", group, "选择动作文件", false));
        Label("ImportSpeedLabel", group, "播放速度（0.5～2 倍）", 14, Ink, 24);
        Set(panel, "importSpeedInput", Input("ImportMotionSpeed", group, "1", 8));
        Label("ImportTriggerLabel", group, "自动触发方式", 14, Ink, 24);
        Set(panel, "importTriggerDropdown", BuildDropdown(group, "ImportMotionTrigger"));
        Set(panel, "importMotionButton", Button("ImportMotion", group, "导入并保存动作", true));
        UnityEditor.EditorUtility.SetDirty(panel);
    }

    private static RectTransform Row(string name, Transform parent)
    {
        var row = Rect(name, parent); Height(row, 40);
        var layout = Ensure<UnityEngine.UI.HorizontalLayoutGroup>(row.gameObject);
        layout.spacing = 8; layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        return row;
    }

    private static TMP_InputField Input(string name, Transform parent, string hint, int limit)
    {
        var root = Rect(name, parent); Height(root, 42);
        var background = Paint(root, Field);
        var input = Ensure<TMP_InputField>(root.gameObject); input.targetGraphic = background;
        var viewport = Rect("TextArea", root); Stretch(viewport, 10);
        Ensure<UnityEngine.UI.RectMask2D>(viewport.gameObject);
        var text = Text("Text", viewport, "", 16, Ink); Stretch(text.rectTransform, 0);
        var placeholder = Text("Placeholder", viewport, hint, 16, Muted); Stretch(placeholder.rectTransform, 0);
        input.textViewport = viewport; input.textComponent = text; input.placeholder = placeholder; input.fontAsset = text.font;
        input.lineType = TMP_InputField.LineType.SingleLine; input.characterLimit = limit;
        input.customCaretColor = true; input.caretColor = Accent;
        return input;
    }

    private static UnityEngine.UI.Toggle Toggle(string name, Transform parent, string caption)
    {
        var root = Rect(name, parent); Height(root, 34);
        var toggle = Ensure<UnityEngine.UI.Toggle>(root.gameObject);
        var hit = Ensure<UnityEngine.UI.Image>(root.gameObject); hit.color = Color.clear; hit.raycastTarget = true;
        var box = Rect("Box", root); box.anchorMin = box.anchorMax = new Vector2(0, 0.5f); box.pivot = new Vector2(0, 0.5f); box.sizeDelta = new Vector2(24, 24);
        toggle.targetGraphic = Paint(box, Field);
        var check = Rect("Check", box); Stretch(check, 5); toggle.graphic = Paint(check, Accent);
        var label = Text("Label", root, caption, 15, Ink); Stretch(label.rectTransform, 0); label.rectTransform.offsetMin = new Vector2(34, 0); label.alignment = TextAlignmentOptions.MidlineLeft;
        return toggle;
    }
}
