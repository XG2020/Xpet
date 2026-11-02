using UnityEngine;
using static PetSettingsWidgets;

/// <summary>Waits for the existing page hierarchy without modifying the scene builder.</summary>
public sealed class PetWorkspaceMemoryInstaller : MonoBehaviour
{
    private PetWorkspaceMemoryPanel panel;
    private float retryAt;
    private void Update()
    {
        if (panel != null || Time.unscaledTime < retryAt) return;
        retryAt = Time.unscaledTime + 1f;
        var tabs = FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        RectTransform parent = tabs != null ? tabs.FindPageContent("记忆") : null;
        if (parent == null) return;
        panel = parent.GetComponentInChildren<PetWorkspaceMemoryPanel>(true);
        if (panel != null) return;
        var root = Rect("WorkspaceMemory", parent); Vertical(root, 12); Height(root, 100);
        root.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = -1;
        panel = root.gameObject.AddComponent<PetWorkspaceMemoryPanel>();
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(parent);
    }
}
