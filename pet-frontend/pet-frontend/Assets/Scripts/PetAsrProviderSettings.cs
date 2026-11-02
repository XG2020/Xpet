using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using static PetSettingsWidgets;

public sealed class PetAsrProviderSettings : MonoBehaviour
{
    [Serializable] private sealed class Recognizer { public string id,name; public string[] languages; }
    [Serializable] private sealed class Status { public bool windows_available,windows_language_available; public string windows_error;public Recognizer[] recognizers; }
    private TMP_Text status;
    private bool built;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install(){new GameObject("AsrProviderSettingsInstaller").AddComponent<PetAsrProviderSettings>();}
    private IEnumerator Start()
    {
        for(int i=0;i<120;i++) {
            var tabs=FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
            var panel=FindAnyObjectByType<BackendSettingsPanel>();
            var voice=tabs?.FindPageContent("语音");
            if(voice!=null&&panel!=null){Build(tabs,voice,panel);yield break;}
            yield return null;
        }
        Destroy(gameObject);
    }
    private void Build(SettingsTabsController tabs,RectTransform parent,BackendSettingsPanel panel)
    {
        var group=Rect("AsrProviderSettings",parent);Vertical(group);group.SetAsFirstSibling();
        Label(group,"语音识别引擎",19,30);
        TMP_Dropdown template=null;
        foreach(var candidate in tabs.GetComponentsInChildren<TMP_Dropdown>(true))if(candidate.template!=null){template=candidate;break;}
        if(template==null){Destroy(group.gameObject);Destroy(gameObject);return;}
        var dropdown=Instantiate(template,group);dropdown.name="AsrProviderDropdown";dropdown.onValueChanged=new TMP_Dropdown.DropdownEvent();dropdown.ClearOptions();
        dropdown.AddOptions(new System.Collections.Generic.List<string>{"OpenAI 兼容识别","Windows 本地识别（SAPI）"});
        var language=Input(group,"本地识别语言","zh-CN",32);language.SetTextWithoutNotify("zh-CN");
        Label(group,"Windows 模式只使用已安装的本地识别引擎，识别失败不会转到云端。云端接口与密钥仍在下面配置。修改后点击保存设置。",14,56);
        Button(group,"检查本地引擎",()=>StartCoroutine(LoadStatus()));
        status=Label(group,"",14,45);
        panel.BindAsrControls(dropdown,language);built=true;
        StartCoroutine(LoadStatus());
    }
    private IEnumerator LoadStatus()
    {
        if(!built)yield break;
        status.text="正在检查本地识别引擎…";
        using var request=UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl()+"/api/speech/recognizers");request.timeout=8;
        yield return request.SendWebRequest();
        if(request.result!=UnityWebRequest.Result.Success){status.text=PetBackendHttpClient.ReadError(request);yield break;}
        Status value=null;try{value=JsonUtility.FromJson<Status>(request.downloadHandler.text);}catch(Exception){}
        status.text=value?.windows_available==true?"本地引擎可用："+(value.recognizers!=null&&value.recognizers.Length>0?value.recognizers[0].name:"")
            :value?.windows_error??"没有找到本地识别引擎。";
    }
}
