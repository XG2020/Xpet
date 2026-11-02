using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using static PetSettingsWidgets;

[Serializable] public sealed class PetLogEntry { public long id; public string timestamp,level,source,message; }
[Serializable] public sealed class PetLogList { public PetLogEntry[] entries; public long last_id; }
public sealed class PetBackendLogsPanel : MonoBehaviour
{
    private TMP_Text output,status;
    private bool busy,errorsOnly;
    private float nextRefresh;
    private void OnEnable()
    {
        if(output==null)
        {
            Label(transform,"后端日志",22,32);
            Label(transform,"查看当前后端进程的请求状态与错误。只在本页打开时刷新，重启后清空；不会记录聊天正文或密钥。",14,48);
            var row=Row("LogActions",transform);
            Button(row,"刷新",Refresh,true);
            Button(row,"全部 / 警告错误",()=>{errorsOnly=!errorsOnly;Refresh();});
            Button(row,"复制日志",()=>GUIUtility.systemCopyBuffer=output.text);
            Button(row,"清空日志",()=>{if(!busy)StartCoroutine(Fetch(true));});
            status=Label(transform,"",14,30);status.color=Accent;
            output=Label(transform,"",13,60);output.enableWordWrapping=true;
        }
        Refresh();
    }
    private void Update(){if(Time.unscaledTime>=nextRefresh)Refresh();}
    private void OnDisable(){StopAllCoroutines();busy=false;}
    public void Refresh(){if(!busy)StartCoroutine(Fetch(false));}
    private IEnumerator Fetch(bool clear)
    {
        busy=true;nextRefresh=Time.unscaledTime+4;
        using var request=clear?PetBackendHttpClient.JsonRequest(DesktopPetPrefs.GetBackendBaseUrl()+"/api/logs","DELETE","{}")
            :UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl()+"/api/logs?limit=200");
        request.timeout=10;yield return request.SendWebRequest();busy=false;
        if(request.result!=UnityWebRequest.Result.Success){status.text=PetBackendHttpClient.ReadError(request);yield break;}
        if(clear){output.text="";status.text="日志已清空。";yield break;}
        PetLogList list=null;try{list=JsonUtility.FromJson<PetLogList>(request.downloadHandler.text);}catch(Exception){}
        if(list?.entries==null){status.text="日志响应无效。";yield break;}
        var text=new StringBuilder();int count=0;
        foreach(var entry in list.entries){if(errorsOnly&&entry.level!="ERROR"&&entry.level!="WARNING"&&entry.level!="CRITICAL")continue;count++;string time=DateTimeOffset.TryParse(entry.timestamp,out var stamp)?stamp.ToLocalTime().ToString("MM-dd HH:mm:ss"):entry.timestamp;text.Append(time).Append("  ").Append(entry.level).Append("  ").Append(entry.source).Append('\n').Append(entry.message).Append("\n\n");}
        output.text=text.Length>0?text.ToString():"暂无日志。";status.text=(errorsOnly?"警告与错误":"全部级别")+" · 显示 "+count+" 条 · 每 4 秒刷新";
    }
}
