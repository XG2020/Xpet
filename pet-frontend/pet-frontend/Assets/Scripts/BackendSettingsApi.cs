using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class RuntimeSettingsData
{
    public string active_character_id = "default";
    public string model_id;
    public PetCharacterSkin[] skins;
    public string active_skin_id = "default";
    public bool allow_ai_skin_switch = true;
    public PetActionEntry[] actions;
    public string pet_name;
    public string pet_persona;
    public string llm_provider;
    public string llm_model;
    public string openai_api_key;
    public bool openai_api_key_configured;
    public bool clear_openai_api_key;
    public string openai_base_url;
    public bool enable_real_rpa;
    public string[] allowed_apps;
    public string default_tts_voice;
    public bool save_conversation_history;
    public bool enable_long_term_memory;
    public bool enable_proactive_screen;
    public int proactive_screen_interval_seconds = 90;
    public string tts_provider;
    public string tts_base_url;
    public string tts_api_key;
    public bool tts_api_key_configured;
    public bool clear_tts_api_key;
    public string tts_model;
    public string tts_voice;
    public string asr_base_url;
    public string asr_provider = "openai";
    public string windows_asr_language = "zh-CN";
    public string asr_api_key;
    public bool asr_api_key_configured;
    public bool clear_asr_api_key;
    public string asr_model;
    public string gpt_sovits_ref_audio_path;
    public string gpt_sovits_prompt_text;
    public string gpt_sovits_prompt_lang;
    public string gpt_sovits_text_lang;
}

public class BackendSettingsApi : MonoBehaviour
{
    [SerializeField] private string backendBaseUrl = DesktopPetPrefs.DefaultBackendBaseUrl;

    public string BackendBaseUrl => backendBaseUrl;
    public event Action<RuntimeSettingsData> SettingsSaved;
    public RuntimeSettingsData Current { get; private set; }
    private int generation;
    public void InvalidateRequests() { generation++; }
    public void PublishSettings(RuntimeSettingsData data) { Current = data; SettingsSaved?.Invoke(data); }

    [Serializable] private class ProactiveSettingsUpdate {
        public bool enable_proactive_screen;
        public int proactive_screen_interval_seconds;
    }

    public void SaveProactiveSettings(bool enabled, int interval, Action<RuntimeSettingsData> onSuccess, Action<string> onError)
    {
        StartCoroutine(SaveSettingsJson(JsonUtility.ToJson(new ProactiveSettingsUpdate {
            enable_proactive_screen = enabled, proactive_screen_interval_seconds = Mathf.Clamp(interval, 30, 900)
        }), onSuccess, onError));
    }

    private void Awake()
    {
        backendBaseUrl = DesktopPetPrefs.GetBackendBaseUrl();
    }

    public void SetBackendBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!string.Equals(backendBaseUrl?.TrimEnd('/'), value.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) InvalidateRequests();
        DesktopPetPrefs.SetBackendBaseUrl(value);
        backendBaseUrl = DesktopPetPrefs.GetBackendBaseUrl();
    }

    public void LoadSettings(Action<RuntimeSettingsData> onSuccess, Action<string> onError = null)
    {
        StartCoroutine(LoadSettingsCoroutine(onSuccess, onError));
    }

    public void SaveSettings(RuntimeSettingsData payload, Action<RuntimeSettingsData> onSuccess, Action<string> onError = null)
    {
        StartCoroutine(SaveSettingsCoroutine(payload, onSuccess, onError));
    }

    [Serializable] private class ConnectionTestResult {
        public bool ok;
        public string model;
        public float elapsed_seconds;
        public string message;
    }

    public void TestModelConnection(Action<string> onSuccess, Action<string> onError)
    {
        StartCoroutine(TestModelConnectionCoroutine(onSuccess, onError));
    }

    private IEnumerator TestModelConnectionCoroutine(Action<string> onSuccess, Action<string> onError)
    {
        int version = generation;
        using var request = PetBackendHttpClient.JsonRequest(backendBaseUrl.TrimEnd('/') + "/api/llm/test", "POST", "{}");
        request.timeout = 110;
        yield return request.SendWebRequest();
        if (version != generation) { onError?.Invoke("角色或连接已更新，请重新测试。"); yield break; }
        if (request.result != UnityWebRequest.Result.Success) { onError?.Invoke(PetBackendHttpClient.ReadError(request)); yield break; }
        ConnectionTestResult result = null;
        try { result = JsonUtility.FromJson<ConnectionTestResult>(request.downloadHandler.text); } catch (Exception) { }
        if (result == null || !result.ok) { onError?.Invoke("模型连接测试没有返回有效结果。"); yield break; }
        onSuccess?.Invoke("连接成功：" + result.model + "（" + result.elapsed_seconds.ToString("0.0") + " 秒）。");
    }

    private IEnumerator LoadSettingsCoroutine(Action<RuntimeSettingsData> onSuccess, Action<string> onError)
    {
        int version = generation;
        string url = backendBaseUrl.TrimEnd('/') + "/api/settings";
        using var request = UnityWebRequest.Get(url);
        request.timeout = 15;
        yield return request.SendWebRequest();
        if (version != generation) { onError?.Invoke("角色或连接已更新，请重新读取设置。"); yield break; }

        if (request.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke(PetBackendHttpClient.ReadError(request));
            yield break;
        }

        RuntimeSettingsData data = null;
        try { data = JsonUtility.FromJson<RuntimeSettingsData>(request.downloadHandler.text); } catch (Exception) { }
        if (data == null)
        {
            onError?.Invoke("后端设置解析失败。");
            yield break;
        }

        Current = data;
        onSuccess?.Invoke(data);
    }

    private IEnumerator SaveSettingsCoroutine(RuntimeSettingsData payload, Action<RuntimeSettingsData> onSuccess, Action<string> onError)
    {
        yield return SaveSettingsJson(JsonUtility.ToJson(payload), onSuccess, onError);
    }

    private IEnumerator SaveSettingsJson(string json, Action<RuntimeSettingsData> onSuccess, Action<string> onError)
    {
        int version = generation;
        string url = backendBaseUrl.TrimEnd('/') + "/api/settings";

        using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = 15;

        yield return request.SendWebRequest();
        if (version != generation) { onError?.Invoke("角色或连接已更新，请重新读取设置。"); yield break; }

        if (request.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke(PetBackendHttpClient.ReadError(request));
            yield break;
        }

        RuntimeSettingsData data = null;
        try { data = JsonUtility.FromJson<RuntimeSettingsData>(request.downloadHandler.text); } catch (Exception) { }
        if (data == null)
        {
            onError?.Invoke("保存结果解析失败。");
            yield break;
        }

        PublishSettings(data);
        onSuccess?.Invoke(data);
    }
}
