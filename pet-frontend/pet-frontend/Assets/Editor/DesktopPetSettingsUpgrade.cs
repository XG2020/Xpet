using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Incrementally rearranges existing controls without rebuilding the user's scene.</summary>
public static class DesktopPetSettingsUpgrade
{
    [MenuItem("Desktop Pet/Upgrade Settings Pages")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before upgrading settings pages.");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath)
            throw new InvalidOperationException("Open DesktopPet.unity before upgrading settings pages.");
        DesktopPetSceneBuilder.UpgradeSettingsPages(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Unable to save settings pages.");
        Debug.Log("DESKTOP_PET_SETTINGS_PAGES_UPGRADED: appearance, conversation, voice, memory, tools, actions and model import.");
    }
}
