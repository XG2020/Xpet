#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>Opt-in release smoke test; available only with an isolated loopback endpoint.</summary>
public sealed class DesktopPetRuntimeVerification : MonoBehaviour
{
    [Serializable] private sealed class Result { public bool codepage932; public bool watermarkVisible; public bool development; public string version; public string unityVersion; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartVerification()
    {
        string[] args=Environment.GetCommandLineArgs();int seconds=0;string endpoint=null;
        for(int i=1;i+1<args.Length;i++)
        {
            if(args[i]=="--xpet-verify-exit-after") int.TryParse(args[i+1],out seconds);
            if(args[i]=="--backend-base-url") endpoint=args[i+1];
        }
        if(seconds<5 || seconds>90 || !Uri.TryCreate(endpoint,UriKind.Absolute,out Uri uri)
            || uri.Scheme!="http" || uri.Host!="127.0.0.1" || uri.Port==18080 || uri.Port==18081) return;
        var runner=new GameObject("Xpet Release Verification").AddComponent<DesktopPetRuntimeVerification>();
        DontDestroyOnLoad(runner.gameObject);runner.StartCoroutine(runner.Verify(seconds));
    }
    private IEnumerator Verify(int seconds)
    {
        yield return new WaitForSecondsRealtime(2);
        bool encoding=false;
        try {encoding=Encoding.GetEncoding(932).GetString(new byte[]{0x8d,0xb6,0x91,0xab})=="左足";} catch(Exception) { }
        Debug.Log("XPET_RUNTIME_DIAGNOSTICS "+JsonUtility.ToJson(new Result {codepage932=encoding,
            watermarkVisible=UnityEngine.Rendering.Watermark.IsVisible(),development=Debug.isDebugBuild,version=Application.version,unityVersion=Application.unityVersion}));
        yield return new WaitForSecondsRealtime(seconds-2);
        if(!encoding) {Application.Quit(2);yield break;}
        var actions=FindAnyObjectByType<DesktopPetUIActions>();
        if(actions!=null) actions.Quit(); else Application.Quit(0);
    }
}
#endif
