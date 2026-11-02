using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Offline state and layout checks. Never starts model/TTS requests or changes preferences.</summary>
public static class DesktopPetSpeechSyncRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [MenuItem("Desktop Pet/Run Speech Sync Regression")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before running speech sync regression.");
        Scene preview = EditorSceneManager.NewPreviewScene();
        var report = new StringBuilder();
        var failures = new List<string>();
        var root = new GameObject("Speech Sync Regression") { hideFlags = HideFlags.HideAndDontSave };
        root.SetActive(false);
        SceneManager.MoveGameObjectToScene(root, preview);
        try {
            Check("Stream and speech gate", () => CheckGate(root.transform), report, failures);
            Check("Speech failure and cancellation", () => CheckFailureAndCancellation(root.transform), report, failures);
            Check("Guard reply", () => CheckGuard(root.transform), report, failures);
            Check("Leg affinity reply and submission snapshot", () => CheckLegReply(root.transform), report, failures);
            Check("Proactive cancellation ownership", () => CheckProactiveCancellation(root.transform), report, failures);
            Check("Loading bubble priority", () => CheckBubble(root.transform), report, failures);
            Check("Message rows and structured history", () => CheckRows(preview), report, failures);
        } finally {
            UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(preview);
        }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("Offline preview objects only; no model/TTS request, microphone, or PlayerPrefs mutation.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "speech-sync-regression.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("DESKTOP_PET_SPEECH_SYNC_REGRESSION " + report + "Report: " + path);
        else Debug.LogError("DESKTOP_PET_SPEECH_SYNC_REGRESSION " + report + "Report: " + path);
    }

    private static void Check(string name, Action action, StringBuilder report, List<string> failures)
    {
        try { action(); report.AppendLine("PASS: " + name); }
        catch (Exception error) { failures.Add(name + ": " + error.GetBaseException().Message); }
    }

    private static PetBackendHttpClient Client(Transform parent)
    {
        var holder = new GameObject("Inactive client");
        holder.SetActive(false);
        holder.transform.SetParent(parent, false);
        return holder.AddComponent<PetBackendHttpClient>();
    }

    private static void CheckGate(Transform parent)
    {
        PetBackendHttpClient client = Client(parent);
        string delta = "";
        int finalCount = 0;
        client.onReplyDelta.AddListener(value => delta += value);
        client.onReplyText.AddListener(value => finalCount++);
        Field(client, "<SpeechEnabled>k__BackingField", true);
        Call(client, "NotifyChatSubmitted", "你好", null);
        Call(client, "BufferReplyDelta", "第一段");
        Call(client, "BufferReplyDelta", "第二段");
        Require(client.IsReplyLoading && delta == "" && finalCount == 0, "Speech-enabled stream leaked text before audio.");
        Field(client, "pendingReply", new PetChatResponse { text = "完整回复" });
        Field(client, "speechPending", true);
        Field(client, "<IsBusy>k__BackingField", true);
        Call(client, "CompleteSpeechPreparation", new object[] { null });
        Require(finalCount == 0 && client.IsBusy, "Text was released when AudioSource was not playing.");
        Field(client, "pendingReply", null);
        Field(client, "<SpeechEnabled>k__BackingField", false);
        Call(client, "FlushReplyDeltas");
        Require(delta == "第一段第二段" && !client.IsReplyLoading, "Disabling speech did not restore the complete buffered prefix.");
        Call(client, "BufferReplyDelta", "第三段");
        Require(delta == "第一段第二段第三段", "Normal stream lost or duplicated a delta.");
    }

    private static void Prepare(PetBackendHttpClient client)
    {
        Field(client, "pendingReply", new PetChatResponse { text = "可读的完整回复" });
        Field(client, "pendingUserMessage", "用户消息");
        Field(client, "pendingReplyHasHistory", true);
        Field(client, "speechPending", true);
        Field(client, "<IsBusy>k__BackingField", true);
        Call(client, "SetReplyLoading", "正在准备语音…");
    }

    private static void CheckFailureAndCancellation(Transform parent)
    {
        PetBackendHttpClient client = Client(parent);
        int finalCount = 0, errors = 0;
        string final = "", status = "";
        client.onReplyText.AddListener(value => { finalCount++; final = value; });
        client.onRequestError.AddListener(value => errors++);
        client.onServiceStatus.AddListener(value => status = value);
        Prepare(client);
        Call(client, "CompleteSpeechPreparation", "模拟无效音频");
        Require(finalCount == 1 && final == "可读的完整回复" && errors == 0 && status.Contains("已显示文字"),
            "TTS failure did not preserve the final text or incorrectly became a model error.");
        Require(!client.IsBusy && !client.IsSpeechPending && !client.IsReplyLoading, "TTS failure left a busy/loading state.");
        Prepare(client);
        client.CancelCurrentRequest();
        Call(client, "CompleteSpeechPreparation", "旧请求延迟回调");
        Require(finalCount == 1 && !client.IsBusy && !client.IsReplyLoading && Get(client, "pendingReply") == null,
            "Cancellation published an obsolete reply or retained loading.");
        Prepare(client);
        Call(client, "OnDisable");
        Require(finalCount == 1 && !client.IsBusy && !client.IsSpeechPending && !client.IsReplyLoading,
            "Disable left pending speech or text behind.");
    }

    private static void CheckGuard(Transform parent)
    {
        PetBackendHttpClient client = Client(parent);
        string action = "", emotion = "";
        int commands = 0;
        client.onReplyAction.AddListener(value => action = value);
        client.onReplyEmotion.AddListener(value => emotion = value);
        client.onCommandProposed.AddListener(value => { if (!string.IsNullOrEmpty(value)) commands++; });
        var response = new PetChatResponse { text = "先停一下。", action = "happy", emotion = "happy",
            command = new PetCommand { type = "open_url", target = "https://example.com" } };
        Call(client, "AcceptReply", response, "摸了你的胸前。", "chest", 20);
        Require(action == "guard" && emotion == "neutral" && commands == 0 && response.command.type == "noop",
            "Low-affinity protected touch accepted a model gesture/command override.");
    }

    private static void CheckProactiveCancellation(Transform parent)
    {
        var client = Client(parent);
        int cancelled = 0;
        client.onRequestCancelled.AddListener(() => cancelled++);
        Prepare(client);
        client.CancelProactiveReply();
        Require(client.IsBusy && Get(client, "pendingReply") != null && cancelled == 0, "Screen toggle cancelled a manual reply.");
        Field(client, "proactiveReplyActive", true);
        client.CancelProactiveReply();
        Require(!client.IsBusy && !client.IsSpeechPending && !client.IsReplyLoading && Get(client, "pendingReply") == null && cancelled == 1,
            "Proactive cancellation kept stale audio/text/loading.");
        client.CancelProactiveReply();
        Require(cancelled == 1, "Repeated settings polls cancelled another reply.");
    }

    private static void CheckLegReply(Transform parent)
    {
        foreach (int affinity in new[] { 0, 20, 29, 30, 69, 70, 100 })
        {
            PetBackendHttpClient client = Client(parent);
            string action = "", emotion = "";
            client.onReplyAction.AddListener(value => action = value);
            client.onReplyEmotion.AddListener(value => emotion = value);
            Call(client, "NotifyChatSubmittedAtAffinity", "自动触碰提示", "leg", affinity);
            Require(client.CurrentSubmission.affinity == affinity && client.CurrentSubmission.interaction_zone == "leg", "Touch request lost its affinity snapshot.");
            var response = new PetChatResponse { text = "嗯，先站稳。", action = "nod", emotion = "happy", command = new PetCommand { type = "open_app", target = "calc" } };
            Call(client, "AcceptReply", response, "自动触碰提示", "leg", affinity);
            string expected = affinity < 30 ? "leg_retract" : affinity < 70 ? "leg_shift" : "leg_sway";
            Require(action == expected && response.command.type == "noop", "Leg reply did not enforce the touch-time stage at " + affinity);
            if (affinity < 30) Require(emotion == "neutral", "Rejected low-affinity leg touch kept an excited expression.");
        }
        var touchObject = new GameObject("Inactive snapshot feedback"); touchObject.SetActive(false); touchObject.transform.SetParent(parent,false);
        var touch = touchObject.AddComponent<PetTouchFeedback>();
        typeof(PetTouchFeedback).GetMethod("OnSubmitted", Private).Invoke(touch,new object[] { new PetChatSubmission { interaction_zone="leg",affinity=69 } });
        Require((int)typeof(PetTouchFeedback).GetField("pendingAffinity", Private).GetValue(touch) == 69, "Delayed fallback no longer uses the request's original affinity.");
    }

    private static void CheckBubble(Transform parent)
    {
        GameObject holder = new GameObject("Bubble callbacks");
        holder.transform.SetParent(parent, false);
        PetSpeechBubble bubble = holder.AddComponent<PetSpeechBubble>();
        RectTransform surface = Rect("Bubble", holder.transform);
        TMP_Text label = Rect("Message", surface).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
        Field(bubble, "bubbleRoot", surface);
        Field(bubble, "messageText", label);
        bubble.ShowMessage("旧回复");
        Call(bubble, "HandleLoading", "正在准备语音…");
        bubble.ShowMessage("不应覆盖圆点的本地提示");
        Call(bubble, "AppendReply", "过早的文字");
        Require((bool)Get(bubble, "loading") && !label.text.Contains("旧回复") && !label.text.Contains("本地提示")
            && !label.text.Contains("过早"), "A text callback replaced the loading indicator.");
        string a = PetSpeechBubble.LoadingDots(0), b = PetSpeechBubble.LoadingDots(0.3f);
        Require(a.Count(c => c == '●') == 3 && b.Count(c => c == '●') == 3 && a != b, "Loading is not an animated three-dot indicator.");
        Call(bubble, "HandleLoading", "");
        Call(bubble, "ShowFinalReply", "最终文字");
        Require(label.text == "最终文字" && !label.richText, "Final reply did not replace loading as plain text.");
        Call(bubble, "OnDisable");
        Require(!(bool)Get(bubble, "loading") && label.text == "" && !surface.gameObject.activeSelf, "Disable did not clear the bubble.");
    }

    private static void CheckRows(Scene preview)
    {
        GameObject holder = new GameObject("Chat row layout", typeof(RectTransform), typeof(Canvas));
        holder.hideFlags = HideFlags.HideAndDontSave;
        SceneManager.MoveGameObjectToScene(holder, preview);
        try {
            var viewport = Rect("Viewport", holder.transform);
            viewport.sizeDelta = new Vector2(350, 400);
            var scroll = viewport.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            scroll.viewport = viewport;
            RectTransform content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1); content.sizeDelta = Vector2.zero;
            var vertical = content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            vertical.childControlWidth = vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
            vertical.spacing = 8;
            content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;
            var text = Rect("PlainTranscript", content).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/DesktopPetUIFont.asset");
            text.text = ""; text.fontSize = 17;
            PetChatTranscriptView view = viewport.gameObject.AddComponent<PetChatTranscriptView>();
            view.Initialize(scroll, text);
            view.BeginReply("用户消息", true);
            view.UpdateReply("丽塔消息");
            Transform user = content.Find("UserMessage"), assistant = content.Find("LitaMessage");
            Require(user != null && assistant != null && !text.gameObject.activeSelf, "Separate user/assistant rows were not created.");
            view.UpdateReply("丽塔消息继续流式更新");
            Require(content.Find("LitaMessage") == assistant && content.Cast<Transform>().Count(t => t.gameObject.activeSelf) == 2,
                "Streaming created duplicate message rows.");
            Require(user.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().childAlignment == TextAnchor.UpperRight &&
                assistant.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().childAlignment == TextAnchor.UpperLeft,
                "The user and assistant are not on opposite sides.");
            Require(user.Find("MessageBubble").GetComponent<UnityEngine.UI.Image>().color !=
                assistant.Find("MessageBubble").GetComponent<UnityEngine.UI.Image>().color, "Message bubbles have indistinguishable backgrounds.");
            view.CompleteReply("最终消息");
            view.BeginReply("自动提示必须隐藏", false);
            view.FailReply("保留半段回复");
            Require(content.Cast<Transform>().Count(t => t.gameObject.activeSelf) == 3, "Hidden interaction prompt or partial reply handling created extra rows.");
            const string multiline = "第一行\n\n你\n这是消息内容，不是角色边界。";
            view.LoadMessages(new[] {
                new PetHistoryMessage { role = "user", content = "自动提示", interaction_zone = "head" },
                new PetHistoryMessage { role = "assistant", content = multiline },
                new PetHistoryMessage { role = "user", content = "手动消息" }
            });
            Require(content.Cast<Transform>().Count(t => t.gameObject.activeSelf) == 2 &&
                content.Find("LitaMessage/MessageBubble/Message").GetComponent<TMP_Text>().text == multiline,
                "Structured history split multiline content or exposed an interaction prompt.");
            Canvas.ForceUpdateCanvases();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            foreach (Transform row in content) if (row.gameObject.activeSelf) {
                var bubble = row.Find("MessageBubble") as RectTransform;
                Require(bubble != null && bubble.rect.width > 80 && bubble.rect.width < 350 && bubble.rect.height > 25,
                    "A message bubble has invalid layout dimensions.");
            }
        } finally { UnityEngine.Object.DestroyImmediate(holder); }
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var result = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        result.SetParent(parent, false);
        return result;
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    private static void Field(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
    private static void Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, Private).Invoke(target, arguments);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
