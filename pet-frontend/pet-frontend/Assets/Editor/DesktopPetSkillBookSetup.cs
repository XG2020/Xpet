using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Adds one independent skill-book section to the current Tools page.</summary>
public static class DesktopPetSkillBookSetup
{
    [MenuItem("Desktop Pet/Add Skill Book")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        Scene scene = SceneManager.GetActiveScene(); ApplyToScene(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the skill-book section.");
        Debug.Log("XPET_SKILL_BOOK_SETUP: active-character skill section installed on Tools.");
    }
    public static void ApplyToScene(Scene scene)
    {
        if (scene.path != DesktopPetSceneBuilder.ScenePath) throw new InvalidOperationException("Open DesktopPet.unity first.");
        var tabs = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<SettingsTabsController>(true)).Single();
        var tools = tabs.FindPageContent("工具");
        if (tools == null) throw new InvalidOperationException("The existing Tools page is missing.");
        if (tabs.GetComponentInParent<Canvas>(true)?.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            throw new InvalidOperationException("The existing settings Canvas raycaster is missing.");
        var group = tools.Find("SkillBook") as RectTransform;
        if (group == null)
        {
            var go = new GameObject("SkillBook", typeof(RectTransform)); Undo.RegisterCreatedObjectUndo(go, "Add skill book");
            group = (RectTransform)go.transform; group.SetParent(tools, false);
        }
        var layout = group.GetComponent<UnityEngine.UI.VerticalLayoutGroup>() ?? Undo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>(group.gameObject);
        Undo.RecordObject(layout, "Configure skill-book layout");
        layout.spacing = 8; layout.padding = new RectOffset(0, 0, 14, 14);
        layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        if (group.GetComponent<PetSkillBookPanel>() == null) Undo.AddComponent<PetSkillBookPanel>(group.gameObject);
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(tools);
    }
}
