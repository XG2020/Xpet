using System.Collections;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class PetChatPanel : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private PetBackendHttpClient backendClient;
    [SerializeField] private BackendSettingsApi settingsApi;
    [SerializeField] private TMP_Text petNameTitle;
    [Header("Input")]
    [SerializeField] private TMP_InputField messageInput;
    [SerializeField] private UnityEngine.UI.Button sendButton;
    [SerializeField] private UnityEngine.UI.Toggle shareScreenToggle;
    [SerializeField] private UnityEngine.UI.Toggle speechToggle;
    [Header("Output")]
    [SerializeField] private TMP_Text replyText;
    [SerializeField] private TMP_Text actionText;
    [SerializeField] private TMP_Text emotionText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private UnityEngine.UI.ScrollRect conversationScroll;
    [SerializeField] private GameObject commandRoot;
    [SerializeField] private TMP_Text commandText;
    [SerializeField] private UnityEngine.UI.Button executeCommandButton;
    [SerializeField] private UnityEngine.UI.Button dismissCommandButton;

    private bool busy;
    private string lastSubmitted = "";
    private bool showSubmittedMessage;
    private string conversation = "";
    private string partialReply = "";
    private PetChatTranscriptView transcriptView;
    private string petName = "丽塔";
    private const int MaxVisibleCharacters = 14000;

    private void Start()
    {
        if (speechToggle != null && backendClient != null) speechToggle.SetIsOnWithoutNotify(backendClient.SpeechEnabled);
    }

    private void OnEnable()
    {
        if (settingsApi == null) settingsApi = GetComponent<BackendSettingsApi>();
        if (settingsApi != null) {
            settingsApi.SettingsSaved += ApplyIdentity;
            settingsApi.LoadSettings(ApplyIdentity);
        }
        EnsureTranscriptView();
        if (sendButton != null) sendButton.onClick.AddListener(SendFromInput);
        if (executeCommandButton != null) executeCommandButton.onClick.AddListener(ExecuteCommand);
        if (dismissCommandButton != null) dismissCommandButton.onClick.AddListener(DismissCommand);
        if (speechToggle != null)
        {
            if (backendClient != null) speechToggle.SetIsOnWithoutNotify(backendClient.SpeechEnabled);
            speechToggle.onValueChanged.AddListener(SetSpeech);
        }
        if (backendClient != null)
        {
            backendClient.onReplyText.AddListener(HandleReplyText);
            backendClient.onReplyAction.AddListener(HandleReplyAction);
            backendClient.onReplyEmotion.AddListener(HandleReplyEmotion);
            backendClient.onRequestError.AddListener(HandleRequestError);
            backendClient.onServiceStatus.AddListener(SetStatus);
            backendClient.onCommandProposed.AddListener(HandleCommandProposed);
            backendClient.onChatRequestStarted.AddListener(HandleChatSubmitted);
            backendClient.onReplyDelta.AddListener(HandleReplyDelta);
            backendClient.onReplyLoading.AddListener(HandleReplyLoading);
            backendClient.onRequestCancelled.AddListener(HandleRequestCancelled);
            backendClient.onConversationLoaded.AddListener(HandleConversationLoaded);
            if (backendClient.IsBusy) {
                HandleChatSubmitted(backendClient.CurrentSubmission);
                if (backendClient.IsReplyLoading) HandleReplyLoading(backendClient.ReplyLoadingMessage);
                else if (!string.IsNullOrEmpty(backendClient.CurrentReplyPreview)) HandleReplyDelta(backendClient.CurrentReplyPreview);
            }
        }
    }

    private void OnDisable()
    {
        if (settingsApi != null) settingsApi.SettingsSaved -= ApplyIdentity;
        if (sendButton != null) sendButton.onClick.RemoveListener(SendFromInput);
        if (executeCommandButton != null) executeCommandButton.onClick.RemoveListener(ExecuteCommand);
        if (dismissCommandButton != null) dismissCommandButton.onClick.RemoveListener(DismissCommand);
        if (speechToggle != null) speechToggle.onValueChanged.RemoveListener(SetSpeech);
        if (backendClient != null)
        {
            backendClient.onReplyText.RemoveListener(HandleReplyText);
            backendClient.onReplyAction.RemoveListener(HandleReplyAction);
            backendClient.onReplyEmotion.RemoveListener(HandleReplyEmotion);
            backendClient.onRequestError.RemoveListener(HandleRequestError);
            backendClient.onServiceStatus.RemoveListener(SetStatus);
            backendClient.onCommandProposed.RemoveListener(HandleCommandProposed);
            backendClient.onChatRequestStarted.RemoveListener(HandleChatSubmitted);
            backendClient.onReplyDelta.RemoveListener(HandleReplyDelta);
            backendClient.onReplyLoading.RemoveListener(HandleReplyLoading);
            backendClient.onRequestCancelled.RemoveListener(HandleRequestCancelled);
            backendClient.onConversationLoaded.RemoveListener(HandleConversationLoaded);
        }
        ResetPendingSubmission();
        SetBusy(false);
    }

    private void Update()
    {
        if (busy || messageInput == null || !messageInput.isFocused) return;
        bool enter = false, shift = false;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            enter = Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame;
            shift = Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif
        if (enter && !shift) SendFromInput();
    }

    public void SendFromInput()
    {
        if (busy)
        {
            if (backendClient != null) backendClient.CancelCurrentRequest();
            else SetBusy(false);
            return;
        }
        if (backendClient == null) { SetStatus("聊天服务尚未连接。"); return; }
        string message = messageInput != null ? messageInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(message)) { SetStatus("写点什么，再发送给" + petName + "。"); return; }
        if (message.Length > 2000) { SetStatus("一次最多发送 2000 个字符。"); return; }
        bool includeScreen = shareScreenToggle != null && shareScreenToggle.isOn;
        if (shareScreenToggle != null) shareScreenToggle.SetIsOnWithoutNotify(false);
        SetBusy(true);
        SetStatus(includeScreen ? "正在发送消息和本次屏幕截图…" : petName + "正在思考…");
        backendClient.SendChat(message, includeScreen);
    }

    private void HandleReplyText(string message)
    {
        EnsureTranscriptView();
        transcriptView?.CompleteReply(message);
        partialReply = "";
        AppendSubmittedMessage();
        AppendConversation(petName, message);
        if (showSubmittedMessage && messageInput != null && messageInput.text.Trim() == lastSubmitted)
            messageInput.SetTextWithoutNotify(string.Empty);
        ResetPendingSubmission();
        SetBusy(false);
        SetStatus("Enter 发送 · Shift+Enter 换行");
        if (transcriptView == null && isActiveAndEnabled) StartCoroutine(ScrollToBottom());
    }

    private void HandleChatSubmitted(PetChatSubmission submission)
    {
        partialReply = "";
        lastSubmitted = submission?.message ?? "";
        showSubmittedMessage = submission != null && !submission.IsInteraction;
        EnsureTranscriptView();
        transcriptView?.BeginReply(lastSubmitted, showSubmittedMessage);
        SetBusy(true);
        SetStatus(petName + "正在思考…");
    }
    private void HandleReplyDelta(string text)
    {
        partialReply += text;
        transcriptView?.UpdateReply(partialReply);
        if (replyText != null) {
            string pendingUser = showSubmittedMessage && !string.IsNullOrWhiteSpace(lastSubmitted) ? "你\n" + lastSubmitted + "\n\n" : "";
            replyText.text = conversation + (conversation.Length == 0 ? "" : "\n\n") + pendingUser + petName + "\n" + partialReply;
        }
        if (transcriptView == null && isActiveAndEnabled) StartCoroutine(ScrollToBottom());
    }
    private void HandleReplyLoading(string status)
    {
        bool loading = !string.IsNullOrEmpty(status);
        EnsureTranscriptView();
        transcriptView?.SetLoading(loading);
        if (!loading) return;
        partialReply = "";
        if (replyText != null) replyText.text = conversation;
        SetBusy(true);
        SetStatus(status);
    }
    private void HandleRequestCancelled() => HandleRequestError("已停止当前回复和语音。");

    private void EnsureTranscriptView()
    {
        if (transcriptView != null || conversationScroll == null || replyText == null) return;
        transcriptView = conversationScroll.GetComponent<PetChatTranscriptView>();
        if (transcriptView == null) transcriptView = conversationScroll.gameObject.AddComponent<PetChatTranscriptView>();
        transcriptView.SetPetName(petName);
        transcriptView.Initialize(conversationScroll, replyText);
    }

    private void ApplyIdentity(RuntimeSettingsData data)
    {
        if (this == null || data == null || string.IsNullOrWhiteSpace(data.pet_name)) return;
        petName = data.pet_name.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (petName.Length > 48) petName = petName.Substring(0, 48);
        if (petNameTitle != null) { petNameTitle.richText = false; petNameTitle.text = petName; }
        transcriptView?.SetPetName(petName);
    }
    private void HandleConversationLoaded(string text)
    {
        conversation = text ?? "";
        ResetPendingSubmission();
        if (replyText != null) replyText.text = conversation.Length == 0 ? "新的聊天已开始。" : conversation;
        EnsureTranscriptView();
        if (backendClient != null && backendClient.LoadedConversation != null) transcriptView?.LoadMessages(backendClient.LoadedConversation);
        else transcriptView?.LoadTranscript(conversation);
        SetBusy(false); SetStatus("Enter 发送 · Shift+Enter 换行");
        if (transcriptView == null && isActiveAndEnabled) StartCoroutine(ScrollToBottom());
    }

    private void AppendSubmittedMessage()
    {
        if (showSubmittedMessage) AppendConversation("你", lastSubmitted);
    }

    private void ResetPendingSubmission()
    {
        lastSubmitted = "";
        showSubmittedMessage = false;
        partialReply = "";
    }

    private void AppendConversation(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        conversation += (conversation.Length == 0 ? "" : "\n\n") + speaker + "\n" + text;
        if (conversation.Length > MaxVisibleCharacters)
            conversation = conversation.Substring(conversation.Length - MaxVisibleCharacters);
        if (replyText != null) replyText.text = conversation;
    }

    private IEnumerator ScrollToBottom()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        if (conversationScroll != null) conversationScroll.verticalNormalizedPosition = 0;
    }

    private void HandleReplyAction(string action)
    {
        if (actionText != null) actionText.text = "动作 · " + TranslateAction(action);
    }
    private void HandleReplyEmotion(string emotion)
    {
        if (emotionText != null) emotionText.text = "心情 · " + TranslateEmotion(emotion);
    }
    private void HandleRequestError(string error) {
        transcriptView?.FailReply(partialReply);
        if (partialReply.Length > 0) { AppendSubmittedMessage(); AppendConversation(petName + " · 本轮未完成", partialReply); }
        ResetPendingSubmission();
        SetBusy(backendClient != null && backendClient.IsBusy);
        SetStatus(error);
    }
    private void HandleCommandProposed(string description)
    {
        if (commandText != null) commandText.text = description;
        if (commandRoot != null) commandRoot.SetActive(!string.IsNullOrWhiteSpace(description));
    }
    private void ExecuteCommand()
    {
        if (backendClient != null) backendClient.ExecutePendingCommand();
        if (commandRoot != null) commandRoot.SetActive(false);
    }
    private void DismissCommand()
    {
        if (backendClient != null) backendClient.DismissPendingCommand();
        if (commandRoot != null) commandRoot.SetActive(false);
    }
    private void SetSpeech(bool value) { if (backendClient != null) backendClient.SetSpeechEnabled(value); }
    private void SetBusy(bool value)
    {
        busy = value;
        if (sendButton == null) return;
        sendButton.interactable = true;
        TMP_Text label = sendButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = value ? "停止" : "发送";
    }
    private void SetStatus(string message) { if (statusText != null) statusText.text = message; }

    private static string TranslateAction(string value)
    {
        switch (value) { case "wave": return "挥手"; case "happy": return "开心"; case "think": return "思考";
            case "sad": return "难过"; case "surprised": return "惊讶"; case "speak": return "说话";
            case "guard": return "保持距离"; default: return "待机"; }
    }
    private static string TranslateEmotion(string value)
    {
        switch (value) { case "happy": return "开心"; case "curious": return "好奇"; case "thinking": return "思考";
            case "sad": return "低落"; case "excited": return "兴奋"; default: return "平静"; }
    }
}
