using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

[Serializable] public class PetHistoryMessage { public string role; public string content; public string interaction_zone; }
[Serializable] public class PetChatSubmission {
    public string message;
    public string interaction_zone;
    public int affinity = 20;
    public bool IsInteraction => !string.IsNullOrWhiteSpace(interaction_zone);
}
[Serializable] public class PetChatSubmissionEvent : UnityEvent<PetChatSubmission> { }
[Serializable] public class PetChatRequest {
    public string character_id;
    public string message;
    public bool include_screen_context;
    public PetHistoryMessage[] conversation_history;
    public string request_id;
    public string session_id;
    public string interaction_zone;
    public int affinity;
}
[Serializable] public class PetCommand { public string type; public string target; }
[Serializable] public class PetChatResponse {
    public string character_id;
    public string text;
    public string action;
    public string action_id;
    public string emotion;
    public string skin_id;
    public PetCommand command;
    public string audio_text;
    public string mode;
    public string request_id;
    public string session_id;
}
[Serializable] public class PetStreamEvent { public string type; public string request_id; public string session_id; public string character_id; public string text; public string message; public PetChatResponse response; }
[Serializable] public class PetSavedConversation { public string id; public string title; public string updated_at; public int message_count; }
[Serializable] public class PetConversationList { public PetSavedConversation[] conversations; }
[Serializable] public class PetConversationDetail { public string session_id; public PetHistoryMessage[] messages; }
[Serializable] public class StringEvent : UnityEvent<string> { }
[Serializable] public class PetSpeechRequest { public string text; public string character_id; }
[Serializable] public class PetFileRequest { public string path; public string message; public string character_id; }
[Serializable] public class PetActionRequest { public PetCommand command; }
[Serializable] public class PetApiError { public string detail; }
[Serializable] public class PetActionResult { public string status; public string message; public string detail; public bool executed; }

public class PetBackendHttpClient : MonoBehaviour
{
    public bool CharacterSwitching { get; set; }
    public string CharacterId { get; private set; } = "default";
    public string CharacterName { get; private set; } = "丽塔";
    public void SetCharacterIdentity(string id, string name) { CharacterId = string.IsNullOrEmpty(id) ? "default" : id; CharacterName = string.IsNullOrEmpty(name) ? "丽塔" : name; }
    [SerializeField] private string backendBaseUrl = DesktopPetPrefs.DefaultBackendBaseUrl;
    [SerializeField] private string startupMessage = "你好，先做个自我介绍";
    [SerializeField] private bool sendMessageOnStart;
    public StringEvent onReplyText = new StringEvent();
    public StringEvent onReplyAction = new StringEvent();
    public StringEvent onReplyEmotion = new StringEvent();
    public StringEvent onRequestError = new StringEvent();
    public StringEvent onServiceStatus = new StringEvent();
    public StringEvent onCommandProposed = new StringEvent();
    public StringEvent onChatSubmitted = new StringEvent();
    public PetChatSubmissionEvent onChatRequestStarted = new PetChatSubmissionEvent();
    public UnityEvent onRequestCancelled = new UnityEvent();
    public StringEvent onReplyDelta = new StringEvent();
    // A nonempty value means the reply is waiting for the model or playable audio.
    // UI listeners must not display partial/final text until this becomes empty.
    public StringEvent onReplyLoading = new StringEvent();
    public StringEvent onConversationLoaded = new StringEvent();
    public bool IsBusy { get; private set; }
    public bool SpeechEnabled { get; private set; }
    public void RebindCharacter(PetCharacterAnimation character) { if (character != null) character.SetSpeechSource(speechSource); }
    private bool proactiveReplyActive;
    public bool IsSpeaking => speechPending || (speechSource != null && speechSource.isPlaying);
    public bool IsSpeechPending => speechPending;
    public AudioSource SpeechSource => speechSource;
    public string ReplyLoadingMessage { get; private set; } = "";
    public bool IsReplyLoading => !string.IsNullOrEmpty(ReplyLoadingMessage);
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    public PetCommand PendingCommand { get; private set; }
    public PetChatSubmission CurrentSubmission { get; private set; }
    public PetHistoryMessage[] LoadedConversation { get; private set; }
    public string CurrentReplyPreview => SpeechEnabled ? "" : bufferedReply.ToString();
    private readonly List<PetHistoryMessage> history = new List<PetHistoryMessage>();
    private AudioSource speechSource;
    private Coroutine speechRoutine;
    private bool speechPending;
    private string speechTempFile;
    private UnityWebRequest activeChatRequest;
    private UnityWebRequest activeSpeechRequest;
    private int requestVersion;
    private bool actionBusy;
    private int speechVersion;
    private readonly StringBuilder bufferedReply = new StringBuilder();
    private int emittedReplyCharacters;
    private PetChatResponse pendingReply;
    public string CurrentSelectedActionId => pendingReply?.action_id ?? "";
    private PetAnimatorBridge actionBridge;
    public PetAnimatorBridge ActionBridge {
        get {
            Transform model = GetComponent<PetModelLibrary>()?.ActiveModelRoot;
            bool Matches(PetAnimatorBridge candidate) => candidate != null && candidate.isActiveAndEnabled
                && candidate.BackendClient == this && (model == null || candidate.PlaybackAnimator != null
                    && candidate.PlaybackAnimator.transform.IsChildOf(model));
            if (Matches(actionBridge)) return actionBridge;
            foreach (var candidate in FindObjectsByType<PetAnimatorBridge>(FindObjectsInactive.Exclude))
                if (Matches(candidate)) { actionBridge = candidate; return candidate; }
            return null;
        }
    }
    public void RegisterActionBridge(PetAnimatorBridge bridge) { if (bridge != null && bridge.BackendClient == this) actionBridge = bridge; }
    private string pendingUserMessage;
    private string pendingInteractionZone;
    private bool pendingReplyHasHistory;
    private bool pendingActionAtPresentation;

    private void Awake() {
        RefreshBackendBaseUrl();
        SpeechEnabled = PlayerPrefs.GetInt("desktop_pet.speech", 0) == 1;
        speechSource = GetComponent<AudioSource>();
        if (speechSource == null) speechSource = gameObject.AddComponent<AudioSource>();
        speechSource.playOnAwake = false;
        speechSource.spatialBlend = 0f;
    }
    private void Start() {
        PetBehaviorCoordinator.EnsureOn(gameObject);
        if (GetComponent<PetRuntimeActionReporter>() == null) gameObject.AddComponent<PetRuntimeActionReporter>();
        if (sendMessageOnStart && !string.IsNullOrWhiteSpace(startupMessage)) SendChat(startupMessage);
    }
    public void SendChat(string message) => SendChat(message, false);
    public void SendChat(string message, bool includeScreen) {
        if (CharacterSwitching) return;
        if (IsBusy) { onServiceStatus.Invoke("请等待当前回复完成。"); return; }
        if (string.IsNullOrWhiteSpace(message)) { onRequestError.Invoke("请先输入消息。"); return; }
        if (message.Trim().Length > 2000) { onRequestError.Invoke("消息最多支持 2000 个字符。"); return; }
        var behavior = GetComponent<PetBehaviorCoordinator>();
        if (behavior != null && behavior.TryHandleCommand(message, out string localReply)) {
            StopSpeech(); IsBusy = true; NotifyChatSubmitted(message);
            pendingReply = new PetChatResponse { text = localReply, audio_text = localReply, character_id = CharacterId, mode = "local" };
            pendingUserMessage = message; pendingReplyHasHistory = true;
            if (SpeechEnabled) BeginSpeech(localReply); else PresentPendingReply();
            return;
        }
        var payload = new PetChatRequest { character_id = CharacterId, message = message.Trim(), include_screen_context = includeScreen,
            conversation_history = history.ToArray(), request_id = Guid.NewGuid().ToString("N"), session_id = SessionId };
        StartCoroutine(StreamReply(payload));
    }
    public void AnalyzeFile(string path) {
        GetComponent<PetBehaviorCoordinator>()?.NoteActivity();
        if (CharacterSwitching) return;
        if (IsBusy) { onServiceStatus.Invoke("请等待当前回复完成。"); return; }
        var payload = new PetFileRequest { character_id = CharacterId, path = path, message = "请看看这个文件，并简要介绍内容。" };
        StartCoroutine(RequestReply("/api/files/analyze", JsonUtility.ToJson(payload),
            "请分析文件：" + System.IO.Path.GetFileName(path)));
    }
    public void SendInteraction(string zone, int affinity)
    {
        if (IsBusy || CharacterSwitching) return;
        string label = PetTouchFeedback.ZoneLabel(zone);
        var payload = new PetChatRequest { character_id = CharacterId, message = "摸了你的" + label + "。", interaction_zone = zone,
            affinity = Mathf.Clamp(affinity, 0, 100), conversation_history = history.ToArray(),
            request_id = Guid.NewGuid().ToString("N"), session_id = SessionId };
        StartCoroutine(StreamReply(payload));
    }
    public void RefreshBackendBaseUrl() => backendBaseUrl = DesktopPetPrefs.GetBackendBaseUrl();
    public static UnityWebRequest JsonRequest(string url, string method, string json) {
        var request = new UnityWebRequest(url, method);
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = 90;
        return request;
    }
    public static string ReadError(UnityWebRequest request, string responseBody = null) {
        if (request.responseCode == 0) return "无法连接后端。请启动服务，并在设置中检查后端地址。";
        try {
            var error = JsonUtility.FromJson<PetApiError>(responseBody ?? request.downloadHandler.text);
            if (!string.IsNullOrWhiteSpace(error?.detail)) return error.detail;
        } catch (Exception) { }
        return "请求未完成（HTTP " + request.responseCode + "）：" + request.error;
    }
    private IEnumerator RequestReply(string endpoint, string json, string userMessage) {
        StopSpeech();
        int version = ++requestVersion;
        IsBusy = true;
        NotifyChatSubmitted(userMessage);
        RefreshBackendBaseUrl();
        DismissPendingCommand();
        using var request = JsonRequest(backendBaseUrl + endpoint, UnityWebRequest.kHttpVerbPOST, json);
        activeChatRequest = request;
        yield return request.SendWebRequest();
        if (version != requestVersion) yield break;
        activeChatRequest = null;
        if (request.result != UnityWebRequest.Result.Success) { FailReply(ReadError(request)); yield break; }
        PetChatResponse response = null;
        try { response = JsonUtility.FromJson<PetChatResponse>(request.downloadHandler.text); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(response?.text)) { FailReply("后端返回了无法读取的回复。"); yield break; }
        yield return ApplyReplySkin(response, version);
        if (version != requestVersion) yield break;
        AcceptReply(response, userMessage);
    }

    private IEnumerator StreamReply(PetChatRequest payload)
    {
        StopSpeech();
        int version = ++requestVersion;
        IsBusy = true;
        NotifyChatSubmittedAtAffinity(payload.message, payload.interaction_zone, payload.affinity);
        RefreshBackendBaseUrl();
        DismissPendingCommand();
        using var request = new UnityWebRequest(backendBaseUrl + "/api/chat/stream", "POST");
        var stream = new PetSseDownloadHandler();
        request.downloadHandler = stream;
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "text/event-stream");
        request.timeout = 120;
        activeChatRequest = request;
        var operation = request.SendWebRequest();
        PetChatResponse final = null;
        string error = null;
        do {
            if (version != requestVersion) yield break;
            while (stream.TryRead(out string frame)) {
                PetStreamEvent item = null;
                try { item = JsonUtility.FromJson<PetStreamEvent>(frame); } catch (Exception) { error = "无法读取流式回复。"; }
                if (item == null || item.request_id != payload.request_id) continue;
                if (!string.IsNullOrEmpty(item.character_id) && item.character_id != CharacterId) { error = "角色已切换，已忽略旧角色回复。"; continue; }
                if (item.type == "delta" && final == null && error == null) BufferReplyDelta(item.text);
                else if (item.type == "final") final = item.response;
                else if (item.type == "error") error = item.message ?? "回复生成失败。";
            }
            if (!operation.isDone) yield return null;
        } while (!operation.isDone);
        // The final frame is delivered before completion; drain it once more after the request finishes.
        while (stream.TryRead(out string frame)) {
            try {
                var item = JsonUtility.FromJson<PetStreamEvent>(frame);
                if (item == null || item.request_id != payload.request_id) continue;
                if (!string.IsNullOrEmpty(item.character_id) && item.character_id != CharacterId) { error = "角色已切换，已忽略旧角色回复。"; continue; }
                if (item.type == "final") final = item.response;
                else if (item.type == "delta" && final == null && error == null) BufferReplyDelta(item.text);
                else if (item.type == "error") error = item.message;
            } catch (Exception) { error = "无法读取流式回复。"; }
        }
        if (version != requestVersion) yield break;
        activeChatRequest = null;
        if (error != null || stream.Failure != null) { FailReply(error ?? stream.Failure); yield break; }
        if (request.result != UnityWebRequest.Result.Success) {
            FailReply(ReadError(request, stream.ErrorBody)); yield break;
        }
        if (string.IsNullOrWhiteSpace(final?.text)) { FailReply("连接已结束，但未收到完整回复。请重试。"); yield break; }
        if (!PetTouchFeedback.RequiresGuard(payload.interaction_zone, payload.affinity)) yield return ApplyReplySkin(final, version);
        if (version != requestVersion) yield break;
        AcceptReply(final, payload.message, payload.interaction_zone, payload.affinity);
    }

    private IEnumerator ApplyReplySkin(PetChatResponse response, int version)
    {
        if (response == null || string.IsNullOrEmpty(response.skin_id)
            || (!string.IsNullOrEmpty(response.character_id) && response.character_id != CharacterId)) yield break;
        var cards = GetComponent<PetCharacterCards>();
        if (cards == null) yield break;
        string expected = CharacterId;
        yield return cards.SwitchSkinRoutine(response.skin_id, true, () => version == requestVersion && CharacterId == expected);
    }

    private void NotifyChatSubmitted(string message, string interactionZone = null)
    {
        NotifyChatSubmittedAtAffinity(message, interactionZone, 20);
    }

    private void NotifyChatSubmittedAtAffinity(string message, string interactionZone, int affinity)
    {
        bufferedReply.Clear();
        emittedReplyCharacters = 0;
        CurrentSubmission = new PetChatSubmission { message = message, interaction_zone = interactionZone, affinity = affinity };
        onChatRequestStarted.Invoke(CurrentSubmission);
        // Keep the text-only notification for existing speech and animation listeners.
        onChatSubmitted.Invoke(message);
        SetReplyLoading(CharacterName + "正在思考…");
    }

    private void BufferReplyDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        bufferedReply.Append(delta);
        if (!SpeechEnabled) FlushReplyDeltas();
    }

    private void FlushReplyDeltas()
    {
        if (bufferedReply.Length <= emittedReplyCharacters) return;
        string delta = bufferedReply.ToString(emittedReplyCharacters, bufferedReply.Length - emittedReplyCharacters);
        emittedReplyCharacters = bufferedReply.Length;
        SetReplyLoading("");
        onReplyDelta.Invoke(delta);
    }

    private void SetReplyLoading(string message)
    {
        message ??= "";
        if (ReplyLoadingMessage == message) return;
        ReplyLoadingMessage = message;
        onReplyLoading.Invoke(message);
    }

    private void FailReply(string error)
    {
        IsBusy = false;
        CurrentSubmission = null;
        ClearPendingReply();
        bufferedReply.Clear();
        emittedReplyCharacters = 0;
        SetReplyLoading("");
        onRequestError.Invoke(error);
    }

    private void AcceptReply(PetChatResponse response, string userMessage, string interactionZone = null, int affinity = 100)
    {
        if (!string.IsNullOrEmpty(response.character_id) && response.character_id != CharacterId) { FailReply("角色已切换，已忽略旧角色回复。"); return; }
        if (PetTouchFeedback.RequiresGuard(interactionZone, affinity)) {
            response.action_id = "";
            response.action = "guard";
            response.emotion = "neutral";
            response.command = new PetCommand { type = "noop", target = "" };
        }
        else if (interactionZone == "leg") {
            response.action_id = "";
            response.action = PetTouchFeedback.LegTouchAction(affinity);
            if (affinity < 30) response.emotion = "neutral";
            response.command = new PetCommand { type = "noop", target = "" };
        }
        else if (!string.IsNullOrEmpty(response.action_id)) {
            var bridge = ActionBridge;
            if (PetActionLibrary.CharacterId != CharacterId || bridge == null || !bridge.GetCallableActionIds().Contains(response.action_id)) {
                Debug.LogWarning("XPET_ACTION_REJECTED " + response.action_id + " profile=" + PetActionLibrary.CharacterId + "/" + CharacterId + " bridge=" + (bridge != null));
                response.action_id = ""; response.action = "idle";
            }
        }
        pendingReply = response;
        pendingUserMessage = userMessage;
        pendingInteractionZone = interactionZone;
        pendingReplyHasHistory = true;
        pendingActionAtPresentation = false;
        // Protective gestures take effect immediately and are never replaced with a
        // generic speaking action when synthesis finishes.
        onReplyAction.Invoke(string.IsNullOrEmpty(response.action_id) ? response.action ?? "idle" : response.action_id);
        onReplyEmotion.Invoke(response.emotion ?? "neutral");
        if (SpeechEnabled) BeginSpeech(string.IsNullOrWhiteSpace(response.audio_text) ? response.text : response.audio_text);
        else PresentPendingReply();
    }

    private void PresentPendingReply()
    {
        PetChatResponse response = pendingReply;
        if (response == null) return;
        if (!string.IsNullOrWhiteSpace(response.session_id)) SessionId = response.session_id;
        if (pendingReplyHasHistory) {
            history.Add(new PetHistoryMessage { role = "user", content = pendingUserMessage, interaction_zone = pendingInteractionZone });
            history.Add(new PetHistoryMessage { role = "assistant", content = response.text });
            while (history.Count > 20) history.RemoveRange(0, 2);
        }
        if (pendingActionAtPresentation) {
            if (!string.IsNullOrWhiteSpace(response.action)) onReplyAction.Invoke(response.action);
            if (!string.IsNullOrWhiteSpace(response.emotion)) onReplyEmotion.Invoke(response.emotion);
        }
        ClearPendingReply();
        bufferedReply.Clear();
        emittedReplyCharacters = 0;
        IsBusy = false;
        CurrentSubmission = null;
        SetReplyLoading("");
        // In speech mode this is invoked only after AudioSource.isPlaying becomes true.
        onReplyText.Invoke(response.text);
        if (response.mode == "offline") onServiceStatus.Invoke("离线体验 · 在设置中配置模型接口后可启用 AI。");
        if (response.command != null && (response.command.type == "open_app" || response.command.type == "open_url")) {
            PendingCommand = response.command;
            onCommandProposed.Invoke(response.command.type + "：" + response.command.target);
        }
    }

    private void ClearPendingReply()
    {
        pendingReply = null;
        pendingUserMessage = null;
        pendingInteractionZone = null;
        pendingReplyHasHistory = false;
        pendingActionAtPresentation = false;
    }
    public void NewConversation()
    {
        if (IsBusy || IsSpeaking) CancelCurrentRequest();
        history.Clear(); SessionId = Guid.NewGuid().ToString("N");
        LoadedConversation = Array.Empty<PetHistoryMessage>();
        onConversationLoaded.Invoke("");
    }
    public void ListConversations(Action<PetConversationList> success, Action<string> error) => StartCoroutine(ReadConversations(success, error));
    private IEnumerator ReadConversations(Action<PetConversationList> success, Action<string> error)
    {
        string expected = CharacterId;
        using var request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + "/api/conversations?character_id=" + Uri.EscapeDataString(expected));
        request.timeout = 15; yield return request.SendWebRequest();
        if (expected != CharacterId) { error("角色已切换，请刷新列表。"); yield break; }
        if (request.result != UnityWebRequest.Result.Success) { error(ReadError(request)); yield break; }
        PetConversationList result = null;
        try { result = JsonUtility.FromJson<PetConversationList>(request.downloadHandler.text); } catch (Exception) { }
        if (result == null) error("无法读取会话列表。"); else success(result);
    }
    public void LoadConversation(string id) => StartCoroutine(ReadConversation(id));
    private IEnumerator ReadConversation(string id)
    {
        CancelCurrentRequest();
        int version = requestVersion;
        using var request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + "/api/conversations/" + Uri.EscapeDataString(id) + "?character_id=" + Uri.EscapeDataString(CharacterId));
        request.timeout = 15; yield return request.SendWebRequest();
        if (version != requestVersion) yield break;
        if (request.result != UnityWebRequest.Result.Success) { onServiceStatus.Invoke(ReadError(request)); yield break; }
        PetConversationDetail value = null;
        try { value = JsonUtility.FromJson<PetConversationDetail>(request.downloadHandler.text); } catch (Exception) { }
        if (value?.messages == null) { onServiceStatus.Invoke("无法读取会话内容。"); yield break; }
        SessionId = value.session_id;
        LoadedConversation = value.messages;
        history.Clear(); history.AddRange(value.messages);
        var text = new StringBuilder();
        foreach (var message in value.messages) {
            if (message == null || (message.role == "user" && !string.IsNullOrWhiteSpace(message.interaction_zone))) continue;
            text.Append(text.Length == 0 ? "" : "\n\n").Append(message.role == "user" ? "你\n" : CharacterName + "\n").Append(message.content);
        }
        while (history.Count > 20) history.RemoveAt(0);
        onConversationLoaded.Invoke(text.ToString());
    }
    public void SetSpeechEnabled(bool enabled) {
        SpeechEnabled = enabled;
        PlayerPrefs.SetInt("desktop_pet.speech", enabled ? 1 : 0);
        PlayerPrefs.Save();
        if (!enabled) {
            bool wasPending = speechPending;
            StopSpeech();
            if (pendingReply != null) PresentPendingReply();
            else if (activeChatRequest != null) FlushReplyDeltas();
            else if (wasPending) { IsBusy = false; SetReplyLoading(""); }
        } else if (activeChatRequest != null) {
            // If enabled midway through a stream, replace that preview with loading.
            // Keep the full buffer so disabling again restores the complete prefix.
            emittedReplyCharacters = 0;
            SetReplyLoading("丽塔正在思考…");
        }
    }
    public void CancelCurrentRequest() {
        proactiveReplyActive = false;
        ++requestVersion;
        if (activeChatRequest != null) activeChatRequest.Abort();
        activeChatRequest = null;
        IsBusy = false;
        CurrentSubmission = null;
        ClearPendingReply();
        bufferedReply.Clear();
        emittedReplyCharacters = 0;
        StopSpeech();
        SetReplyLoading("");
        DismissPendingCommand();
        onRequestCancelled.Invoke();
        onReplyAction.Invoke("idle");
        onRequestError.Invoke("已停止当前回复和语音。");
    }
    public void Speak(string text) {
        if (IsBusy || string.IsNullOrWhiteSpace(text)) return;
        proactiveReplyActive = false;
        BeginSpeech(text);
    }
    /// <summary>Present a local reply with the same text/audio gate as a model reply.</summary>
    public void SpeakReply(string text) {
        if (IsBusy || string.IsNullOrWhiteSpace(text)) return;
        proactiveReplyActive = false;
        pendingReply = new PetChatResponse { text = text };
        pendingReplyHasHistory = false;
        if (SpeechEnabled) BeginSpeech(text);
        else PresentPendingReply();
    }
    /// <summary>A local companion observation, without a fabricated user turn or chat history entry.</summary>
    public void PresentProactiveReply(string text, string action, string emotion) {
        if (IsBusy || IsSpeaking || string.IsNullOrWhiteSpace(text)) return;
        proactiveReplyActive = true;
        pendingReply = new PetChatResponse { text = text, action = action, emotion = emotion };
        pendingReplyHasHistory = false;
        pendingActionAtPresentation = true;
        if (SpeechEnabled) BeginSpeech(text);
        else PresentPendingReply();
    }
    public void CancelProactiveReply() {
        // A settings poll must never abort a newer manual conversation or recording.
        if (!proactiveReplyActive) return;
        proactiveReplyActive = false;
        ++requestVersion;
        StopSpeech(); ClearPendingReply();
        IsBusy = false;
        SetReplyLoading("");
        onRequestCancelled.Invoke();
    }
    private void BeginSpeech(string text) {
        StopSpeech();
        IsBusy = true;
        speechPending = true;
        SetReplyLoading("正在准备语音…");
        speechRoutine = StartCoroutine(SpeakCoroutine(text, speechVersion));
    }
    private IEnumerator SpeakCoroutine(string text, int version) {
        string failure = null;
        bool started = false;
        try {
        RefreshBackendBaseUrl();
        using var request = JsonRequest(backendBaseUrl + "/api/tts", UnityWebRequest.kHttpVerbPOST,
            JsonUtility.ToJson(new PetSpeechRequest { text = text, character_id = CharacterId }));
        request.timeout = 90;
        activeSpeechRequest = request;
        yield return request.SendWebRequest();
        if (version != speechVersion) yield break;
        activeSpeechRequest = null;
        if (request.result != UnityWebRequest.Result.Success) { failure = ReadError(request); yield break; }
        byte[] bytes = request.downloadHandler.data;
        bool isWav = bytes != null && bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF";
        string mime = (request.GetResponseHeader("Content-Type") ?? "").ToLowerInvariant();
        if (!isWav && !mime.Contains("mpeg") && !mime.Contains("mp3")) {
            failure = "语音接口必须返回 MP3 或 PCM WAV 音频。"; yield break;
        }
        if (bytes == null || bytes.Length < 16) { failure = "语音接口返回了空音频。"; yield break; }
        speechTempFile = Path.Combine(Application.temporaryCachePath, "lita-speech-" + Guid.NewGuid().ToString("N") + (isWav ? ".wav" : ".mp3"));
        bool saved = false;
        try { File.WriteAllBytes(speechTempFile, bytes); saved = true; }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!saved) { failure = "无法创建语音播放缓存。"; yield break; }
        using var decode = UnityWebRequestMultimedia.GetAudioClip(new Uri(speechTempFile).AbsoluteUri, isWav ? AudioType.WAV : AudioType.MPEG);
        activeSpeechRequest = decode;
        yield return decode.SendWebRequest();
        if (version != speechVersion) yield break;
        activeSpeechRequest = null;
        DeleteSpeechTemp();
        if (decode.result != UnityWebRequest.Result.Success) { failure = "语音音频格式无法播放，请检查合成服务返回格式。"; yield break; }
        var clip = DownloadHandlerAudioClip.GetContent(decode);
        if (clip == null) { failure = "未能读取语音音频。"; yield break; }
        if (speechSource == null) { Destroy(clip); failure = "语音播放器不可用。"; yield break; }
        speechSource.clip = clip;
        speechSource.Play();
        float playbackDeadline = Time.realtimeSinceStartup + 2f;
        while (!speechSource.isPlaying && Time.realtimeSinceStartup < playbackDeadline) {
            if (version != speechVersion) yield break;
            yield return null;
        }
        if (version != speechVersion) yield break;
        if (!speechSource.isPlaying) { failure = "语音未能开始播放。"; yield break; }
        started = true;
        CompleteSpeechPreparation(null);
        } finally {
            if (version == speechVersion) {
                speechPending = false;
                activeSpeechRequest = null;
                speechRoutine = null;
                DeleteSpeechTemp();
                if (!started) CompleteSpeechPreparation(failure ?? "未能完成语音合成。");
            }
        }
    }
    private void CompleteSpeechPreparation(string failure) {
        if (string.IsNullOrEmpty(failure) && (speechSource == null || !speechSource.isPlaying)) return;
        speechPending = false;
        IsBusy = false;
        SetReplyLoading("");
        bool hasText = pendingReply != null;
        PresentPendingReply();
        if (!string.IsNullOrEmpty(failure)) {
            if (speechSource != null) speechSource.Stop();
            onServiceStatus.Invoke((hasText ? "朗读暂不可用，已显示文字。" : "朗读暂不可用。") + failure);
        }
    }
    private void StopSpeech() {
        ++speechVersion;
        speechPending = false;
        if (activeSpeechRequest != null) activeSpeechRequest.Abort();
        activeSpeechRequest = null;
        if (speechRoutine != null) StopCoroutine(speechRoutine);
        speechRoutine = null;
        DeleteSpeechTemp();
        if (speechSource == null) return;
        speechSource.Stop();
        if (speechSource.clip != null) Destroy(speechSource.clip);
        speechSource.clip = null;
    }
    private void DeleteSpeechTemp() {
        if (string.IsNullOrEmpty(speechTempFile)) return;
        try { if (File.Exists(speechTempFile)) File.Delete(speechTempFile); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        speechTempFile = null;
    }
    public void DismissPendingCommand() {
        PendingCommand = null;
        onCommandProposed.Invoke("");
    }
    public void ExecutePendingCommand() {
        if (PendingCommand == null || actionBusy) return;
        var command = PendingCommand;
        DismissPendingCommand();
        StartCoroutine(ExecuteCommand(command));
    }
    private IEnumerator ExecuteCommand(PetCommand command) {
        actionBusy = true;
        RefreshBackendBaseUrl();
        using var request = JsonRequest(backendBaseUrl + "/api/actions/execute", UnityWebRequest.kHttpVerbPOST,
            JsonUtility.ToJson(new PetActionRequest { command = command }));
        yield return request.SendWebRequest();
        actionBusy = false;
        if (request.result != UnityWebRequest.Result.Success) { onServiceStatus.Invoke(ReadError(request)); yield break; }
        PetActionResult result = null;
        try { result = JsonUtility.FromJson<PetActionResult>(request.downloadHandler.text); } catch (Exception) { }
        onServiceStatus.Invoke(result?.message ?? result?.detail ?? "操作请求已处理。");
    }
    private void OnDisable() {
        proactiveReplyActive = false;
        ++requestVersion;
        if (activeChatRequest != null) activeChatRequest.Abort();
        activeChatRequest = null;
        CurrentSubmission = null;
        ClearPendingReply();
        bufferedReply.Clear();
        emittedReplyCharacters = 0;
        IsBusy = false; actionBusy = false; StopSpeech(); StopAllCoroutines();
        SetReplyLoading("");
        onRequestCancelled.Invoke();
    }
}

