using System;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class DesktopPetCoreSetup
{
    [MenuItem("Desktop Pet/Upgrade Core Services")]
    public static void Upgrade()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != DesktopPetSceneBuilder.ScenePath) throw new InvalidOperationException("Open DesktopPet scene first.");
        var services = GameObject.Find("AppServices");
        var data = services.GetComponent<PetDataPanel>() ?? services.AddComponent<PetDataPanel>();
        var system = services.GetComponent<DesktopPetSystemController>() ?? services.AddComponent<DesktopPetSystemController>();
        if (services.GetComponent<PetTouchFeedback>() == null) services.AddComponent<PetTouchFeedback>();
        var menu = GameObject.Find("DesktopPetCanvas").transform.Find("ContextMenuRoot") as RectTransform;
        Add(menu, "History", "聊天记录", data.OpenHistory);
        Add(menu, "LowPower", "低功耗模式", system.ToggleLowPower);
        Add(menu, "HidePet", "隐藏宠物", system.ToggleVisibility);
        menu.Find("Quit")?.SetAsLastSibling();
        menu.sizeDelta = new Vector2(180, 16 + menu.childCount * 33 + (menu.childCount - 1) * 6);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("Desktop core ready: streaming/history, touch, tray and low power.");
    }
    private static void Add(RectTransform menu, string name, string label, UnityEngine.Events.UnityAction action)
    {
        var item = menu.Find(name);
        if (item == null) {
            var template = menu.Find("Chat");
            item = UnityEngine.Object.Instantiate(template.gameObject, menu).transform; item.name = name;
        }
        var button = item.GetComponent<Button>(); button.onClick = new Button.ButtonClickedEvent();
        var services = GameObject.Find("AppServices").GetComponent<DesktopPetUIActions>();
        UnityEventTools.AddPersistentListener(button.onClick, services.CloseContextMenu);
        UnityEventTools.AddPersistentListener(button.onClick, action);
        item.GetComponentInChildren<TMP_Text>(true).text = label;
    }
}
