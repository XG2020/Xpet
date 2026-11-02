using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class DesktopPetBuild
{
    [MenuItem("Desktop Pet/Set Release Version")]
    public static void SetReleaseVersion()
    {
        PlayerSettings.bundleVersion = "0.6.0";
        AssetDatabase.SaveAssets();
    }
    [MenuItem("Desktop Pet/Disable Unstable Engine Diagnostics")]
    public static void DisableUnstableEngineDiagnostics()
    {
        // Unity UUM-147746: the 6000.5 diagnostics collector can corrupt shutdown
        // after an AssetBundle has been loaded. Keep normal player logs enabled.
        var settings=Unsupported.GetSerializedAssetInterfaceSingleton("UnityConnectSettings");
        if(settings==null)throw new InvalidOperationException("UnityConnectSettings is unavailable.");
        var serialized=new SerializedObject(settings);
        var diagnostics=serialized.FindProperty("InsightsSettings.m_EngineDiagnosticsEnabled");
        if(diagnostics==null)throw new InvalidOperationException("Engine Diagnostics setting was not found.");
        diagnostics.boolValue=false;serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(settings);AssetDatabase.SaveAssets();
        Debug.Log("XPET_ENGINE_DIAGNOSTICS_DISABLED: release workaround for UUM-147746.");
    }
    [UnityEditor.Callbacks.PostProcessBuild(100)]
    public static void IncludeVmdEncoding(BuildTarget target, string executable)
    {
        if (target != BuildTarget.StandaloneWindows64) return;
        string managed = Path.Combine(Path.GetDirectoryName(executable), Path.GetFileNameWithoutExtension(executable) + "_Data/Managed");
        if (!Directory.Exists(managed)) throw new IOException("Xpet requires the Mono player managed directory.");
        string runtime = Path.Combine(EditorApplication.applicationContentsPath,"MonoBleedingEdge/lib/mono/unityjit-win32");
        foreach (string assembly in new[] { "I18N.dll", "I18N.CJK.dll" })
        {
            string source=Path.Combine(runtime,assembly), destination=Path.Combine(managed,assembly);
            if (!File.Exists(source)) throw new FileNotFoundException("Unity's runtime Shift-JIS assembly is missing.",source);
            // GetEncoding(932) loads these by name; link.xml cannot introduce an
            // otherwise unreferenced framework assembly into a Mono player.
            File.Copy(source,destination,true);
        }
    }

    [MenuItem("Desktop Pet/Prepare Windows Build")]
    public static void PrepareWindowsBuild()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        DisableUnstableEngineDiagnostics();
        // Preserve other scenes, including unsaved work, before opening only the app scene.
        for (int i = 0; i < SceneManager.sceneCount; i++) {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isDirty || scene.path == DesktopPetSceneBuilder.ScenePath) continue;
            Directory.CreateDirectory("Assets/Scenes/Snapshots");
            string path = "Assets/Scenes/Snapshots/" + scene.name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity";
            if (!EditorSceneManager.SaveScene(scene, path, true)) throw new IOException("Cannot preserve the current scene.");
        }
        EditorSceneManager.OpenScene(DesktopPetSceneBuilder.ScenePath, OpenSceneMode.Single);
        PlayerSettings.productName = "Xpet";
        PlayerSettings.companyName = "DesktopPet";
        PlayerSettings.bundleVersion = "0.6.0";
        var appIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Branding/Xpet.png");
        if (appIcon == null) throw new InvalidOperationException("Xpet application icon is missing.");
        PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Unknown, new[] { appIcon });
        var iconSizes = PlayerSettings.GetIconSizesForTargetGroup(BuildTargetGroup.Standalone);
        PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Standalone, System.Array.ConvertAll(iconSizes, _ => appIcon));
        PlayerSettings.preserveFramebufferAlpha = true;
        PlayerSettings.defaultScreenWidth = 1200;
        PlayerSettings.defaultScreenHeight = 800;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = false;
        PlayerSettings.runInBackground = true;
        PlayerSettings.useFlipModelSwapchain = false;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        foreach (string guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets/Settings" })) {
            var pipeline = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)));
            var alpha = pipeline.FindProperty("m_AllowPostProcessAlphaOutput");
            if (alpha != null) { alpha.boolValue = true; pipeline.ApplyModifiedProperties(); }
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DesktopPetSceneBuilder.ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("DESKTOP_PET_BUILD_READY: Windows x64, 1200x800, Direct3D 11, Mono.");
    }

    [MenuItem("Desktop Pet/Run Integration Smoke Test")]
    public static void RunSmokeTest()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var client = UnityEngine.Object.FindAnyObjectByType<PetBackendHttpClient>();
        var api = UnityEngine.Object.FindAnyObjectByType<BackendSettingsApi>();
        var bridge = UnityEngine.Object.FindAnyObjectByType<PetAnimatorBridge>();
        if (client == null || api == null || bridge == null) throw new InvalidOperationException("Missing app services.");
        UnityEngine.Events.UnityAction<string> reply = null;
        UnityEngine.Events.UnityAction<string> error = null;
        reply = text => {
            client.onReplyText.RemoveListener(reply); client.onRequestError.RemoveListener(error);
            bridge.PlayWave();
            Debug.Log("DESKTOP_PET_CHAT_OK: " + text);
        };
        error = text => {
            client.onReplyText.RemoveListener(reply); client.onRequestError.RemoveListener(error);
            Debug.LogError("DESKTOP_PET_CHAT_FAILED: " + text);
        };
        api.LoadSettings(settings => {
            if (!string.IsNullOrEmpty(settings.openai_api_key)) throw new InvalidOperationException("Settings exposed a secret.");
            api.SaveSettings(settings, saved => {
                if (saved.pet_name != settings.pet_name) throw new InvalidOperationException("Settings round trip failed.");
                Debug.Log("DESKTOP_PET_SETTINGS_OK");
                client.onReplyText.AddListener(reply); client.onRequestError.AddListener(error);
                client.SendChat("你好，请向我挥手。", false);
            }, message => Debug.LogError("DESKTOP_PET_SETTINGS_FAILED: " + message));
        }, message => Debug.LogError("DESKTOP_PET_SETTINGS_FAILED: " + message));
    }
}
