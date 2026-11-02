using System.Collections;
using TMPro;
using UnityEngine;
using static PetSettingsWidgets;

public sealed class PetAboutPage : MonoBehaviour
{
    public const string AuthorName = "XG.GM";
    public const string AuthorUrl = "https://github.com/XG2020";
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install() { new GameObject("AboutPageInstaller").AddComponent<PetAboutPage>(); }
    private IEnumerator Start()
    {
        for(int i=0;i<120;i++) {
            var tabs=FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
            if(tabs!=null&&tabs.SidebarMenu!=null&&tabs.transform.Find("Body/Configuration/Pages") is RectTransform host) {
                if(tabs.FindPageContent("关于")==null) Build(tabs,host);
                Destroy(gameObject);yield break;
            }
            yield return null;
        }
        Destroy(gameObject);
    }
    private static void Build(SettingsTabsController tabs,RectTransform host)
    {
        var page=Rect("AboutPage",host);Stretch(page);
        var viewport=Rect("Viewport",page);Stretch(viewport);viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        var hit=viewport.gameObject.AddComponent<UnityEngine.UI.Image>();hit.color=Color.clear;
        var content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.anchoredPosition=Vector2.zero;content.sizeDelta=Vector2.zero;
        Vertical(content,8);var fitter=content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();fitter.verticalFit=UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        var scroll=page.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();scroll.content=content;scroll.viewport=viewport;scroll.horizontal=false;scroll.vertical=true;scroll.movementType=UnityEngine.UI.ScrollRect.MovementType.Clamped;
        Label(content,"Xpet 3D 桌面宠物",25,42);
        Label(content,"版本 "+Application.version,15,28);
        Label(content,"本地桌面陪伴 · 角色、动作、语音与工作区记忆",15,48);
        Label(content,"作者",18,34);
        Button(content,AuthorName,()=>Application.OpenURL(AuthorUrl),true);
        Label(content,AuthorUrl,14,28);
        var template=tabs.SidebarMenu.GetChild(0).GetComponent<UnityEngine.UI.Button>();
        var tab=Instantiate(template,tabs.SidebarMenu);tab.name="AboutPageTab";tab.onClick=new UnityEngine.UI.Button.ButtonClickedEvent();
        var caption=tab.GetComponentInChildren<TMP_Text>();if(caption!=null)caption.text="关于";
        tabs.AddPage("关于",page.gameObject,tab);
    }
}
