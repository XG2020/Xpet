using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DesktopPetSkillBookChecks
{
    [MenuItem("Desktop Pet/Run Skill Book UI Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var settings = UnityEngine.Object.FindFirstObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
        var tabs = UnityEngine.Object.FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        if (settings == null || tabs == null) throw new InvalidOperationException("Missing settings services.");
        settings.Open(); tabs.ShowPage("工具");
        var panel = tabs.FindPageContent("工具")?.GetComponentInChildren<PetSkillBookPanel>();
        if (panel == null) throw new InvalidOperationException("Install Skill Book before testing it.");
        panel.StartCoroutine(Check(panel, tabs));
    }
    private static IEnumerator Check(PetSkillBookPanel panel, SettingsTabsController tabs)
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/skill-book")); Directory.CreateDirectory(folder);
        panel.RefreshBook(); double deadline = EditorApplication.timeSinceStartup + 20;
        while (panel.IsBusy || panel.SkillCount == 0)
        {
            if (EditorApplication.timeSinceStartup > deadline) { File.WriteAllText(Path.Combine(folder, "ui-results.txt"), "FAILED: " + panel.Status); throw new InvalidOperationException("Skill-book load failed: " + panel.Status); }
            yield return null;
        }
        Canvas.ForceUpdateCanvases();
        if (string.IsNullOrEmpty(panel.BoundCharacterId) || panel.transform.Find("SkillBookTitle") == null || panel.transform.Find("SkillMarkdown") == null)
            throw new InvalidOperationException("Skill-book editor controls are incomplete.");
        var content = tabs.FindPageContent("工具"); var scroll = content.GetComponentInParent<UnityEngine.UI.ScrollRect>();
        foreach (RectTransform row in panel.transform) if (row.gameObject.activeSelf && row.rect.height < 1) throw new InvalidOperationException("Skill-book row has zero height: " + row.name);
        scroll.verticalNormalizedPosition = 0; yield return new WaitForSecondsRealtime(.2f); Canvas.ForceUpdateCanvases();
        var last = panel.transform.Find("SkillImportTutorial") as RectTransform;
        var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, last);
        if (bounds.min.y < scroll.viewport.rect.yMin - 2) throw new InvalidOperationException("Skill-book import tutorial cannot be reached by scrolling.");
        ScreenCapture.CaptureScreenshot(Path.Combine(folder, "tools-skill-book-bottom.png")); yield return new WaitForEndOfFrame();
        File.WriteAllText(Path.Combine(folder, "ui-results.txt"), "PASSED: active-character skill metadata loaded; editor rows have nonzero height; import controls and final tutorial are reachable on Tools. Backend CRUD/import isolation tested separately.\n");
        Debug.Log("XPET_SKILL_BOOK_UI_CHECKS_PASSED");
    }
}
