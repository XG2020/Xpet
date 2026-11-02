using System;
using System.Collections;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class DesktopPetSettingsPageChecks
{
    [MenuItem("Desktop Pet/Run New Settings Page Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
        var cards = UnityEngine.Object.FindAnyObjectByType<PetCharacterCards>();
        if (cards == null || !cards.Ready || cards.IsBusy) throw new InvalidOperationException("Wait for character cards to load.");
        cards.StartCoroutine(Check());
    }
    private static IEnumerator Check()
    {
        var settings=UnityEngine.Object.FindFirstObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
        var tabs=UnityEngine.Object.FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        settings.Open();
        string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/settings-pages"));Directory.CreateDirectory(folder);
        foreach(string page in new[]{"角色卡","后端日志"})
        {
            tabs.ShowPage(page);yield return new WaitForSecondsRealtime(0.8f);Canvas.ForceUpdateCanvases();
            var card=(RectTransform)tabs.transform;
            foreach(Transform tab in tabs.SidebarMenu)
            {
                var bounds=RectTransformUtility.CalculateRelativeRectTransformBounds(card,tab);
                if(bounds.min.x<card.rect.xMin-1||bounds.max.x>card.rect.xMax+1)throw new InvalidOperationException("Tab extends beyond settings card: "+tab.name);
            }
            var content=tabs.FindPageContent(page);
            if(page=="角色卡" && string.IsNullOrWhiteSpace(content.Find("人物名称")?.GetComponent<TMP_InputField>()?.text))
                throw new InvalidOperationException("Active character name was not populated into the editor.");
            var scroll=content.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            foreach(RectTransform row in content)
                if(row.gameObject.activeSelf&&row.rect.height<1)throw new InvalidOperationException("Empty settings row: "+row.name);
            scroll.verticalNormalizedPosition=1;
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,page=="角色卡"?"characters.png":"logs.png"));
            yield return new WaitForSecondsRealtime(0.2f);
            if(page=="角色卡")
            {
                scroll.verticalNormalizedPosition=0;yield return new WaitForSecondsRealtime(0.2f);
                var last=content.GetChild(content.childCount-1);
                var bottom=RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport,last);
                if(bottom.min.y<scroll.viewport.rect.yMin-2)throw new InvalidOperationException("Last character-card control cannot be reached.");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder,"characters-bottom.png"));yield return new WaitForEndOfFrame();
            }
        }
        tabs.ShowPage("角色卡");
        tabs.FindPageContent("角色卡").GetComponentInParent<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition=1;
        File.WriteAllText(Path.Combine(folder,"results.txt"),"PASSED: all nine tabs fit the card; character editor and log pages have usable rows; last card controls are reachable by scrolling. Screenshots captured.\n");
        Debug.Log("NEW_SETTINGS_PAGE_CHECKS_PASSED");
    }
}
