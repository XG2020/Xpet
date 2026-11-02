using System;
using System.Linq;
using TMPro;
using UnityEngine;

public class BackendSettingsPanel : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private BackendSettingsApi settingsApi;
    [Header("Connection")]
    [SerializeField] private TMP_InputField backendServiceUrlInput;
    [Header("Form")]
    [SerializeField] private TMP_InputField petNameInput;
    [SerializeField] private TMP_InputField personaInput;
    [SerializeField] private TMP_InputField llmProviderInput;
    [SerializeField] private TMP_Dropdown llmProviderDropdown;
    [SerializeField] private TMP_InputField llmModelInput;
    [SerializeField] private TMP_InputField apiKeyInput;
    [SerializeField] private TMP_InputField baseUrlInput;
    [SerializeField] private UnityEngine.UI.Toggle enableRealRpaToggle;
    [SerializeField] private UnityEngine.UI.Toggle saveConversationHistoryToggle;
    [SerializeField] private UnityEngine.UI.Toggle enableLongTermMemoryToggle;
    [SerializeField] private UnityEngine.UI.Toggle enableProactiveScreenToggle;
    [SerializeField] private TMP_InputField proactiveScreenIntervalInput;
    [SerializeField] private TMP_InputField allowedAppsInput;
    [SerializeField] private TMP_InputField ttsVoiceInput;
    [SerializeField] private TMP_InputField edgeTtsVoiceInput;
    [SerializeField] private TMP_InputField ttsProviderInput;
    [SerializeField] private TMP_InputField ttsBaseUrlInput;
    [SerializeField] private TMP_InputField ttsApiKeyInput;
    [SerializeField] private TMP_InputField ttsModelInput;
    [SerializeField] private TMP_InputField asrBaseUrlInput;
    [SerializeField] private TMP_InputField asrApiKeyInput;
    [SerializeField] private TMP_InputField asrModelInput;
    [SerializeField] private TMP_InputField sovitsRefAudioInput;
    [SerializeField] private TMP_InputField sovitsPromptTextInput;
    [SerializeField] private TMP_InputField sovitsPromptLangInput;
    [SerializeField] private TMP_InputField sovitsTextLangInput;
    [SerializeField] private TMP_Text ttsApiKeyStatusText;
    [SerializeField] private TMP_Text asrApiKeyStatusText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text apiKeyStatusText;
    [SerializeField] private UnityEngine.UI.Button saveButton;
    [SerializeField] private UnityEngine.UI.Button refreshButton;
    private UnityEngine.UI.Button testConnectionButton;

    private bool busy;
    private TMP_Dropdown asrProviderDropdown;
    private TMP_InputField windowsAsrLanguageInput;
    public void BindAsrControls(TMP_Dropdown dropdown, TMP_InputField language)
    {
        asrProviderDropdown = dropdown; windowsAsrLanguageInput = language;
        ApplyAsrProvider(settingsApi != null ? settingsApi.Current : null);
    }
    private void ApplyAsrProvider(RuntimeSettingsData data)
    {
        if (asrProviderDropdown != null) asrProviderDropdown.SetValueWithoutNotify(data != null && data.asr_provider == "windows" ? 1 : 0);
        if (windowsAsrLanguageInput != null) windowsAsrLanguageInput.SetTextWithoutNotify(data?.windows_asr_language ?? "zh-CN");
    }
    private bool loaded;
    private string legacyTtsVoice = "zh-CN-XiaoxiaoNeural";
    private bool savedConversationHistory;
    private bool savedLongTermMemory;
    private bool savedProactiveScreen;
    private int savedProactiveInterval = 90;
    private bool proactiveSaving;
    private bool? queuedProactiveToggle;
    private static readonly string[] LlmProviders = { "openai", "ollama", "mock", "anthropic" };

    private void Start()
    {
        EnsureLlmProviderDropdown();
        EnsureConnectionTestButton();
        if (settingsApi != null) settingsApi.SettingsSaved += ApplySettings;
        if (enableProactiveScreenToggle != null) enableProactiveScreenToggle.onValueChanged.AddListener(SetProactiveScreen);
        if (apiKeyInput != null) apiKeyInput.contentType = TMP_InputField.ContentType.Password;
        if (ttsApiKeyInput != null) ttsApiKeyInput.contentType = TMP_InputField.ContentType.Password;
        if (asrApiKeyInput != null) asrApiKeyInput.contentType = TMP_InputField.ContentType.Password;
        if (settingsApi != null && backendServiceUrlInput != null)
            backendServiceUrlInput.SetTextWithoutNotify(settingsApi.BackendBaseUrl);
        Refresh();
    }

    public void Refresh()
    {
        if (proactiveSaving) { SetStatus("请等待主动看屏幕开关保存完成。"); return; }
        if (busy || !ValidateConnection()) return;
        SetBusy(true);
        SetStatus("正在读取设置…");
        settingsApi.LoadSettings(data =>
        {
            ApplySettings(data);
            SetBusy(false);
            SetStatus("设置已读取。修改后点击保存。");
        }, HandleError);
    }

    public void Save() => SaveAndTest(false);

    public void TestConnection() => SaveAndTest(true);

    private void EnsureLlmProviderDropdown()
    {
        if (llmProviderDropdown == null && llmProviderInput != null)
        {
            var modelPage = FindFirstObjectByType<PetModelSettingsPanel>(FindObjectsInactive.Include);
            var template = modelPage != null ? modelPage.GetComponentInChildren<TMP_Dropdown>(true) : null;
            if (template == null)
                template = FindObjectsByType<TMP_Dropdown>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(dropdown => dropdown.template != null);
            if (template == null) return;
            llmProviderDropdown = Instantiate(template, llmProviderInput.transform.parent);
            llmProviderDropdown.name = "LlmProviderDropdown";
            var sourceRect = llmProviderInput.GetComponent<RectTransform>();
            var dropdownRect = llmProviderDropdown.GetComponent<RectTransform>();
            dropdownRect.anchorMin = sourceRect.anchorMin;
            dropdownRect.anchorMax = sourceRect.anchorMax;
            dropdownRect.pivot = sourceRect.pivot;
            dropdownRect.sizeDelta = sourceRect.sizeDelta;
            dropdownRect.anchoredPosition = sourceRect.anchoredPosition;
            dropdownRect.localScale = sourceRect.localScale;
            dropdownRect.localRotation = sourceRect.localRotation;
            var sourceLayout = llmProviderInput.GetComponent<UnityEngine.UI.LayoutElement>();
            if (sourceLayout != null)
            {
                var layout = llmProviderDropdown.GetComponent<UnityEngine.UI.LayoutElement>()
                    ?? llmProviderDropdown.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.ignoreLayout = sourceLayout.ignoreLayout;
                layout.minWidth = sourceLayout.minWidth;
                layout.minHeight = sourceLayout.minHeight;
                layout.preferredWidth = sourceLayout.preferredWidth;
                layout.preferredHeight = sourceLayout.preferredHeight;
                layout.flexibleWidth = sourceLayout.flexibleWidth;
                layout.flexibleHeight = sourceLayout.flexibleHeight;
                layout.layoutPriority = sourceLayout.layoutPriority;
            }
        }
        if (llmProviderDropdown == null) return;
        // Replacing the event also removes persistent listeners copied from the template.
        llmProviderDropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        llmProviderDropdown.ClearOptions();
        llmProviderDropdown.AddOptions(new System.Collections.Generic.List<string> { "OpenAI 兼容", "Ollama", "离线体验", "Anthropic / SystemOne" });
        ApplyLlmProvider(llmProviderInput != null ? llmProviderInput.text : "openai");
        llmProviderDropdown.onValueChanged.AddListener(SetLlmProvider);
        if (llmProviderInput != null)
        {
            llmProviderDropdown.transform.SetSiblingIndex(llmProviderInput.transform.GetSiblingIndex());
            // Keep the serialized legacy binding for existing scenes; only the dropdown is visible.
            llmProviderInput.gameObject.SetActive(false);
        }
        llmProviderDropdown.gameObject.SetActive(true);
    }

    private void ApplyLlmProvider(string provider)
    {
        if (llmProviderDropdown == null) return;
        string value = (provider ?? "").Trim().ToLowerInvariant();
        if (value == "openai-compatible") value = "openai";
        if (value == "offline") value = "mock";
        if (value == "systemone") value = "anthropic";
        llmProviderDropdown.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(LlmProviders, value)));
        llmProviderDropdown.RefreshShownValue();
        UpdateBaseUrlHint();
    }

    private void SetLlmProvider(int index)
    {
        if (llmProviderInput != null && index >= 0 && index < LlmProviders.Length)
            llmProviderInput.SetTextWithoutNotify(LlmProviders[index]);
        UpdateBaseUrlHint();
    }

    private string SelectedLlmProvider()
    {
        if (llmProviderDropdown != null)
            return LlmProviders[Mathf.Clamp(llmProviderDropdown.value, 0, LlmProviders.Length - 1)];
        return TextValue(llmProviderInput, "openai").ToLowerInvariant();
    }

    private void UpdateBaseUrlHint()
    {
        if (baseUrlInput == null || !(baseUrlInput.placeholder is TMP_Text hint)) return;
        switch (SelectedLlmProvider())
        {
            case "anthropic":
                hint.text = "https://api.anthropic.com（通常使用根 URL）";
                break;
            case "ollama":
                hint.text = "http://127.0.0.1:11434/v1";
                break;
            case "mock":
                hint.text = "离线体验无需接口地址";
                break;
            default:
                hint.text = "https://api.openai.com/v1（通常包含 /v1）";
                break;
        }
    }

    private void EnsureConnectionTestButton()
    {
        if (refreshButton == null || saveButton == null) return;
        Transform parent = refreshButton.transform.parent;
        var existing = parent.Find("TestConnection");
        testConnectionButton = existing != null ? existing.GetComponent<UnityEngine.UI.Button>()
            : Instantiate(refreshButton, parent);
        if (testConnectionButton == null) return;
        testConnectionButton.name = "TestConnection";
        testConnectionButton.transform.SetSiblingIndex(refreshButton.transform.GetSiblingIndex() + 1);
        // The copied button may carry a persistent refresh listener.
        testConnectionButton.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        testConnectionButton.onClick.AddListener(TestConnection);
        var label = testConnectionButton.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = "测试连接";
        foreach (var button in new[] { refreshButton, testConnectionButton, saveButton }) {
            var size = button.GetComponent<UnityEngine.UI.LayoutElement>();
            if (size == null) continue;
            size.minWidth = 88; size.preferredWidth = 104; size.flexibleWidth = 1;
        }
    }

    private void SaveAndTest(bool testConnection)
    {
        if (GetComponent<PetCharacterCards>()?.IsBusy == true) { SetStatus("正在切换角色，请稍后保存。"); return; }
        if (proactiveSaving) { SetStatus("请等待主动看屏幕开关保存完成。"); return; }
        if (busy || !ValidateConnection()) return;
        if (!loaded)
        {
            SetStatus("请先连接后端并读取设置，再进行保存。");
            return;
        }
        if (string.IsNullOrWhiteSpace(petNameInput.text))
        {
            SetStatus("请填写宠物名称。");
            return;
        }
        if (!TryProactiveInterval(out int proactiveInterval)) return;
        var payload = new RuntimeSettingsData
        {
            active_character_id = settingsApi.Current?.active_character_id ?? "default",
            model_id = GetComponent<PetModelLibrary>()?.SelectedModelId ?? PetModelLibrary.BuiltinModelId,
            actions = PetActionLibrary.Export(),
            pet_name = petNameInput.text.Trim(),
            pet_persona = personaInput.text.Trim(),
            llm_provider = SelectedLlmProvider(),
            llm_model = llmModelInput.text.Trim(),
            openai_api_key = apiKeyInput.text.Trim(),
            openai_base_url = baseUrlInput.text.Trim(),
            enable_real_rpa = enableRealRpaToggle != null && enableRealRpaToggle.isOn,
            save_conversation_history = saveConversationHistoryToggle != null ? saveConversationHistoryToggle.isOn : savedConversationHistory,
            enable_long_term_memory = enableLongTermMemoryToggle != null ? enableLongTermMemoryToggle.isOn : savedLongTermMemory,
            enable_proactive_screen = enableProactiveScreenToggle != null ? enableProactiveScreenToggle.isOn : savedProactiveScreen,
            proactive_screen_interval_seconds = proactiveInterval,
            allowed_apps = SplitApps(allowedAppsInput.text),
            default_tts_voice = TextValue(edgeTtsVoiceInput, legacyTtsVoice),
            tts_provider = TextValue(ttsProviderInput, "edge"),
            tts_base_url = TextValue(ttsBaseUrlInput),
            tts_api_key = TextValue(ttsApiKeyInput),
            tts_model = TextValue(ttsModelInput),
            tts_voice = TextValue(ttsVoiceInput),
            asr_base_url = TextValue(asrBaseUrlInput),
            asr_provider = asrProviderDropdown != null ? (asrProviderDropdown.value == 1 ? "windows" : "openai") : settingsApi.Current?.asr_provider ?? "openai",
            windows_asr_language = windowsAsrLanguageInput != null ? windowsAsrLanguageInput.text.Trim() : settingsApi.Current?.windows_asr_language ?? "zh-CN",
            asr_api_key = TextValue(asrApiKeyInput),
            asr_model = TextValue(asrModelInput),
            gpt_sovits_ref_audio_path = TextValue(sovitsRefAudioInput),
            gpt_sovits_prompt_text = TextValue(sovitsPromptTextInput),
            gpt_sovits_prompt_lang = TextValue(sovitsPromptLangInput, "zh"),
            gpt_sovits_text_lang = TextValue(sovitsTextLangInput, "zh")
        };
        SetBusy(true);
        SetStatus("正在保存设置…");
        settingsApi.SaveSettings(payload, data =>
        {
            ApplySettings(data);
            if (testConnection)
            {
                SetStatus("设置已保存，正在等待模型回复以验证连接…");
                settingsApi.TestModelConnection(message => { SetBusy(false); SetStatus(message); },
                    message => { SetBusy(false); SetStatus("设置已保存；连接测试失败：" + message); });
            }
            else
            {
                SetBusy(false);
                SetStatus("设置已保存。点击「测试连接」验证模型是否能回复。");
            }
        }, HandleError);
    }

    public void ApplyBackendServiceUrl() { ValidateConnection(); }

    private bool ValidateConnection()
    {
        if (settingsApi == null)
        {
            SetStatus("设置服务未连接，请重新打开应用。");
            return false;
        }
        string value = backendServiceUrlInput != null ? backendServiceUrlInput.text.Trim() : settingsApi.BackendBaseUrl;
        if (!DesktopPetPrefs.IsValidBackendUrl(value))
        {
            SetStatus("后端地址需使用 http:// 或 https://，且不含账号、查询参数或片段。");
            return false;
        }
        if (!string.Equals(settingsApi.BackendBaseUrl.TrimEnd('/'), value.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            loaded = false;
        settingsApi.SetBackendBaseUrl(value);
        if (backendServiceUrlInput != null) backendServiceUrlInput.SetTextWithoutNotify(settingsApi.BackendBaseUrl);
        return true;
    }

    private void ApplySettings(RuntimeSettingsData data)
    {
        ApplyAsrProvider(data);
        if (data == null) { HandleError("设置响应为空，请重试。"); return; }
        loaded = true;
        savedConversationHistory = data.save_conversation_history;
        savedLongTermMemory = data.enable_long_term_memory;
        ApplyProactiveSettings(data);
        legacyTtsVoice = string.IsNullOrWhiteSpace(data.default_tts_voice) ? "zh-CN-XiaoxiaoNeural" : data.default_tts_voice;
        Put(edgeTtsVoiceInput, legacyTtsVoice);
        if (petNameInput != null) petNameInput.SetTextWithoutNotify(data.pet_name ?? "");
        if (personaInput != null) personaInput.SetTextWithoutNotify(data.pet_persona ?? "");
        if (llmProviderInput != null) llmProviderInput.SetTextWithoutNotify(data.llm_provider ?? "");
        ApplyLlmProvider(data.llm_provider);
        if (llmModelInput != null) llmModelInput.SetTextWithoutNotify(data.llm_model ?? "");
        // A saved secret is never returned to the form.
        if (apiKeyInput != null) apiKeyInput.SetTextWithoutNotify(string.Empty);
        if (apiKeyStatusText != null)
            apiKeyStatusText.text = data.openai_api_key_configured ? "密钥已保存。留空会保留已有密钥。" : "尚未配置密钥。可以先使用离线对话。";
        if (baseUrlInput != null) baseUrlInput.SetTextWithoutNotify(data.openai_base_url ?? "");
        if (enableRealRpaToggle != null) enableRealRpaToggle.SetIsOnWithoutNotify(data.enable_real_rpa);
        if (saveConversationHistoryToggle != null) saveConversationHistoryToggle.SetIsOnWithoutNotify(data.save_conversation_history);
        if (enableLongTermMemoryToggle != null) enableLongTermMemoryToggle.SetIsOnWithoutNotify(data.enable_long_term_memory);
        if (allowedAppsInput != null) allowedAppsInput.SetTextWithoutNotify(string.Join(", ", data.allowed_apps ?? Array.Empty<string>()));
        Put(ttsVoiceInput, data.tts_voice);
        Put(ttsProviderInput, data.tts_provider);
        Put(ttsBaseUrlInput, data.tts_base_url);
        Put(ttsModelInput, data.tts_model);
        Put(asrBaseUrlInput, data.asr_base_url);
        Put(asrModelInput, data.asr_model);
        Put(sovitsRefAudioInput, data.gpt_sovits_ref_audio_path);
        Put(sovitsPromptTextInput, data.gpt_sovits_prompt_text);
        Put(sovitsPromptLangInput, data.gpt_sovits_prompt_lang);
        Put(sovitsTextLangInput, data.gpt_sovits_text_lang);
        Put(ttsApiKeyInput, "");
        Put(asrApiKeyInput, "");
        if (ttsApiKeyStatusText != null) ttsApiKeyStatusText.text = data.tts_api_key_configured ? "TTS 密钥已保存。留空保留。" : "尚未设置独立 TTS 密钥。";
        if (asrApiKeyStatusText != null) asrApiKeyStatusText.text = data.asr_api_key_configured ? "ASR 密钥已保存。留空保留。" : "尚未设置独立 ASR 密钥。";
    }

    private static string TextValue(TMP_InputField input, string fallback = "") => input != null ? input.text.Trim() : fallback;

    private void ApplyProactiveSettings(RuntimeSettingsData data)
    {
        savedProactiveScreen = data.enable_proactive_screen;
        savedProactiveInterval = Mathf.Clamp(data.proactive_screen_interval_seconds > 0 ? data.proactive_screen_interval_seconds : 90, 30, 900);
        if (enableProactiveScreenToggle != null) enableProactiveScreenToggle.SetIsOnWithoutNotify(savedProactiveScreen);
        Put(proactiveScreenIntervalInput, savedProactiveInterval.ToString());
    }

    private bool TryProactiveInterval(out int value)
    {
        value = savedProactiveInterval;
        if (proactiveScreenIntervalInput == null) return true;
        if (int.TryParse(proactiveScreenIntervalInput.text, out value) && value >= 30 && value <= 900) return true;
        SetStatus("查看间隔请填写 30～900 秒。");
        return false;
    }

    private void SetProactiveScreen(bool enabled)
    {
        if (!enabled) FindAnyObjectByType<DesktopPetProactiveScreen>()?.PauseImmediately();
        if (proactiveSaving || busy) {
            queuedProactiveToggle = enabled;
            SetStatus(enabled ? "设置保存完成后将开启主动看屏幕。" : "主动看屏幕已暂停，正在保存关闭状态。"); return;
        }
        if (!loaded || settingsApi == null) {
            if (enableProactiveScreenToggle != null) enableProactiveScreenToggle.SetIsOnWithoutNotify(false);
            SetStatus("请先读取后端设置。"); return;
        }
        // Closing is always available, even while the interval field is incomplete.
        int interval = savedProactiveInterval;
        if (enabled && !TryProactiveInterval(out interval)) {
            enableProactiveScreenToggle.SetIsOnWithoutNotify(false); return;
        }
        proactiveSaving = true;
        settingsApi.SaveProactiveSettings(enabled, interval, data => {
            proactiveSaving = false;
            if (queuedProactiveToggle.HasValue) { FlushProactiveToggle(); return; }
            ApplyProactiveSettings(data);
            if (enabled) FindAnyObjectByType<DesktopPetProactiveScreen>()?.AcceptOptInSave(data);
            SetStatus(enabled ? "主动看屏幕已开启。空闲时按间隔查看，可以随时关闭。" : "主动看屏幕已关闭。");
        }, message => {
            proactiveSaving = false;
            if (enableProactiveScreenToggle != null) enableProactiveScreenToggle.SetIsOnWithoutNotify(false);
            FindAnyObjectByType<DesktopPetProactiveScreen>()?.PauseImmediately();
            SetStatus("主动看屏幕已在本机暂停。" + message);
            FlushProactiveToggle();
        });
    }

    private void FlushProactiveToggle()
    {
        if (!queuedProactiveToggle.HasValue || proactiveSaving || busy) return;
        bool value = queuedProactiveToggle.Value;
        queuedProactiveToggle = null;
        SetProactiveScreen(value);
    }

    private void OnDestroy()
    {
        if (settingsApi != null) settingsApi.SettingsSaved -= ApplySettings;
        if (enableProactiveScreenToggle != null) enableProactiveScreenToggle.onValueChanged.RemoveListener(SetProactiveScreen);
        if (llmProviderDropdown != null) llmProviderDropdown.onValueChanged.RemoveListener(SetLlmProvider);
    }
    private static void Put(TMP_InputField input, string value) { if (input != null) input.SetTextWithoutNotify(value ?? ""); }

    private static string[] SplitApps(string input)
    {
        return (input ?? "").Split(',', '，', ';', '\n')
            .Select(item => item.Trim()).Where(item => item.Length > 0).Distinct().ToArray();
    }
    private void SetBusy(bool value)
    {
        busy = value;
        if (saveButton != null) saveButton.interactable = !value;
        if (refreshButton != null) refreshButton.interactable = !value;
        if (testConnectionButton != null) testConnectionButton.interactable = !value;
        if (llmProviderDropdown != null) llmProviderDropdown.interactable = !value;
        if (!value) FlushProactiveToggle();
    }
    private void HandleError(string message) { SetBusy(false); SetStatus(message); }
    private void SetStatus(string message) { if (statusText != null) statusText.text = message; }
}
