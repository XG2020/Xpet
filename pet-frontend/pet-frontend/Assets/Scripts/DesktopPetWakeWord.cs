using System;
using System.Threading;
using TMPro;
using UnityEngine;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using UnityEngine.Windows.Speech;
#endif

/// <summary>Explicitly opted-in local Windows keyword listening. No remote audio loop.</summary>
public sealed class DesktopPetWakeWord : MonoBehaviour
{
    public const string EnabledPrefsKey = "desktop_pet.wake_enabled";
    public const string KeywordPrefsKey = "desktop_pet.wake_keyword";
    public const string DefaultKeyword = "丽塔";
    [SerializeField] private PetBackendHttpClient backendClient;
    [SerializeField] private DesktopPetInteraction interaction;
    [SerializeField] private PetSpeechBubble speechBubble;
    [SerializeField] private UnityEngine.UI.Toggle enabledToggle;
    [SerializeField] private TMP_InputField keywordInput;
    [SerializeField] private TMP_Text statusText;
    public bool WakeWordEnabled { get; private set; }
    public string Keyword { get; private set; } = DefaultKeyword;
    public string Status { get; private set; } = "唤醒词已关闭。";
    public bool IsListening { get; private set; }
    private int pendingWake;
    private bool recognizerUnavailable;
    private float resumeAfter;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private KeywordRecognizer recognizer;
#endif

    private void Awake()
    {
        if (backendClient == null) backendClient = GetComponent<PetBackendHttpClient>();
        if (interaction == null) interaction = GetComponent<DesktopPetInteraction>();
        if (speechBubble == null) speechBubble = GetComponent<PetSpeechBubble>();
        // Merely installing/upgrading this component never opts the user into microphone access.
        WakeWordEnabled = PlayerPrefs.GetInt(EnabledPrefsKey, 0) == 1;
        Keyword = PlayerPrefs.GetString(KeywordPrefsKey, DefaultKeyword).Trim();
        if (!IsValidKeyword(Keyword)) Keyword = DefaultKeyword;
        SyncControls();
    }

    private void OnEnable()
    {
        if (enabledToggle != null) enabledToggle.onValueChanged.AddListener(SetWakeWordEnabled);
        if (keywordInput != null) keywordInput.onEndEdit.AddListener(SetKeyword);
        SyncControls();
    }

    private void OnDisable()
    {
        if (enabledToggle != null) enabledToggle.onValueChanged.RemoveListener(SetWakeWordEnabled);
        if (keywordInput != null) keywordInput.onEndEdit.RemoveListener(SetKeyword);
        DisposeRecognizer();
    }

    private void Update()
    {
        if (!WakeWordEnabled)
        {
            StopListening();
            SetStatus("已关闭 · 开启后仅在本机监听唤醒词。");
            return;
        }
        bool occupied = interaction == null || backendClient == null || interaction.IsRecording ||
            interaction.IsTranscribing || backendClient.IsBusy || backendClient.IsSpeaking;
        if (occupied)
        {
            SuspendForInteraction();
            SetStatus("唤醒已暂停 · 正在录音、识别或回复。");
            return;
        }
        if (Interlocked.Exchange(ref pendingWake, 0) != 0)
        {
            SuspendForInteraction();
            if (interaction.StartWakeWordRecording()) speechBubble?.ShowMessage("我在听，请说话…");
            return;
        }
        if (recognizerUnavailable) return;
        if (Time.unscaledTime < resumeAfter)
        {
            SetStatus("稍后恢复本机唤醒监听。");
            return;
        }
        StartListening();
    }

    public void SetWakeWordEnabled(bool value)
    {
        WakeWordEnabled = value;
        PlayerPrefs.SetInt(EnabledPrefsKey, value ? 1 : 0);
        PlayerPrefs.Save();
        recognizerUnavailable = false;
        DisposeRecognizer();
        SyncControls();
        if (!value) SetStatus("已关闭 · 开启后仅在本机监听唤醒词。");
    }

    public void SetKeyword(string value)
    {
        string candidate = (value ?? "").Trim();
        if (!IsValidKeyword(candidate))
        {
            if (keywordInput != null) keywordInput.SetTextWithoutNotify(Keyword);
            SetStatus("请输入 1–24 个字符的简短唤醒词。");
            return;
        }
        Keyword = candidate;
        PlayerPrefs.SetString(KeywordPrefsKey, Keyword);
        PlayerPrefs.Save();
        recognizerUnavailable = false;
        DisposeRecognizer();
        SyncControls();
        SetStatus("唤醒词已保存为「" + Keyword + "」。");
    }

    private static bool IsValidKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 24) return false;
        foreach (char character in value) if (char.IsControl(character)) return false;
        return true;
    }

    /// <summary>Called before capture; also holds listening briefly after speaker playback ends.</summary>
    public void SuspendForInteraction()
    {
        StopListening();
        Interlocked.Exchange(ref pendingWake, 0);
        resumeAfter = Time.unscaledTime + 1.25f;
    }

    private void SyncControls()
    {
        if (enabledToggle != null) enabledToggle.SetIsOnWithoutNotify(WakeWordEnabled);
        if (keywordInput != null) keywordInput.SetTextWithoutNotify(Keyword);
        if (statusText != null) statusText.text = Status;
    }

    private void StartListening()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        try
        {
            if (!PhraseRecognitionSystem.isSupported)
            {
                MarkUnavailable();
                return;
            }
            if (recognizer == null)
            {
                recognizer = new KeywordRecognizer(new[] { Keyword }, ConfidenceLevel.Medium);
                recognizer.OnPhraseRecognized += OnPhraseRecognized;
            }
            if (!recognizer.IsRunning) recognizer.Start();
            IsListening = recognizer.IsRunning;
            if (IsListening) SetStatus("正在本机等待「" + Keyword + "」 · 唤醒后录音才会交给 ASR。");
            else MarkUnavailable();
        }
        catch (Exception) { MarkUnavailable(); }
#else
        recognizerUnavailable = true;
        SetStatus("唤醒词仅在 Windows 桌面版运行；编辑器中不会启动监听。");
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private void OnPhraseRecognized(PhraseRecognizedEventArgs args)
    {
        // Phrase callbacks only queue data; microphone/UI work runs on the Unity main thread.
        if (string.Equals(args.text, Keyword, StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref pendingWake, 1);
    }
#endif

    private void StopListening()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        try { if (recognizer != null && recognizer.IsRunning) recognizer.Stop(); }
        catch (Exception) { recognizerUnavailable = true; }
#endif
        IsListening = false;
    }

    private void DisposeRecognizer()
    {
        StopListening();
        Interlocked.Exchange(ref pendingWake, 0);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (recognizer != null)
        {
            recognizer.OnPhraseRecognized -= OnPhraseRecognized;
            try { recognizer.Dispose(); } catch (Exception) { }
            recognizer = null;
        }
#endif
    }

    private void MarkUnavailable()
    {
        recognizerUnavailable = true;
        DisposeRecognizer();
        SetStatus("Windows 本地语音识别不可用。请检查麦克风权限和中文语音语言包；仍可使用手动语音输入。");
    }

    private void SetStatus(string value)
    {
        if (Status == value) return;
        Status = value;
        if (statusText != null) statusText.text = value;
    }
}
