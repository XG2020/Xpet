using UnityEditor;
using UnityEngine;

/// <summary>Temporarily directs editor play tests to the isolated offline backend.</summary>
[InitializeOnLoad]
public static class DesktopPetQaSession
{
    private const string Key = "DesktopPet.QA.OriginalUrl";
    static DesktopPetQaSession() { EditorApplication.playModeStateChanged += Restore; }
    [MenuItem("Desktop Pet/Use Isolated QA Backend")]
    public static void Prepare()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new System.InvalidOperationException("Exit Play mode first.");
        if (!SessionState.GetBool(Key + ".active", false))
        {
            SessionState.SetString(Key, DesktopPetPrefs.GetBackendBaseUrl());
            SessionState.SetBool(Key + ".active", true);
            SessionState.SetBool(Key + ".had", PlayerPrefs.HasKey(DesktopPetPrefs.BackendBaseUrlPrefsKey));
        }
        DesktopPetPrefs.SetBackendBaseUrl("http://127.0.0.1:18081");
    }
    private static void Restore(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(Key + ".active", false)) return;
        if (SessionState.GetBool(Key + ".had", false)) DesktopPetPrefs.SetBackendBaseUrl(SessionState.GetString(Key, DesktopPetPrefs.DefaultBackendBaseUrl));
        else PlayerPrefs.DeleteKey(DesktopPetPrefs.BackendBaseUrlPrefsKey);
        SessionState.SetBool(Key + ".active", false); PlayerPrefs.Save();
    }
}
