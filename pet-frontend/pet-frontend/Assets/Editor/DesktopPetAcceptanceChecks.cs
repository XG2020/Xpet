using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class DesktopPetAcceptanceChecks
{
    private static int phase, deltaCount;
    private static double next, deadline;
    private static string directory, error;
    private static bool replySeen;
    private static PetBackendHttpClient client;
    private static PetCharacterAnimation character;
    private static DesktopPetAppearance appearance;
    private static DesktopPetUIActions actions;
    private static SettingsPanelController settings;
    private static SettingsTabsController tabs;
    private static RectTransform chat;
    private static Vector2 chatAnchor;
    private static Dictionary<Transform, Quaternion> knees, cloth;
    private static float kneeMotion, clothMotion;
    private static Action afterCapture;
    private static Vector3 originalScale;
    private static bool originalSpeech;
    private static string restoreModel;
    private static readonly string[] PageNames = { "外观", "对话", "语音", "记忆", "工具", "动作管理", "模型导入" };
    private static readonly string[] GestureActions = { "nod", "tilt_head", "shy", "stretch", "cheer", "look_around", "guard" };
    private static readonly string[] GestureStates = { "Nod", "TiltHead", "Shy", "Stretch", "Cheer", "LookAround", "Guard" };

    [MenuItem("Desktop Pet/Run Acceptance Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var library = UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>();
        if (library != null && library.SelectedModelId != PetModelLibrary.BuiltinModelId) {
            if (library.IsBusy) throw new InvalidOperationException("Wait for model loading to finish.");
            restoreModel = library.SelectedModelId;
            library.StartCoroutine(PrepareBuiltin(library));
            return;
        }
        CheckSseDecoder();
        client = UnityEngine.Object.FindAnyObjectByType<PetBackendHttpClient>();
        originalSpeech = client.SpeechEnabled;
        character = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
        originalScale = character.transform.parent.localScale;
        appearance = UnityEngine.Object.FindAnyObjectByType<DesktopPetAppearance>();
        actions = UnityEngine.Object.FindAnyObjectByType<DesktopPetUIActions>();
        settings = UnityEngine.Object.FindAnyObjectByType<SettingsPanelController>();
        tabs = UnityEngine.Object.FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        chat = GameObject.Find("DesktopPetCanvas").transform.Find("ChatPanel") as RectTransform;
        directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced")); Directory.CreateDirectory(directory);
        phase = 0; afterCapture = null; error = null; replySeen = false; deltaCount = 0; kneeMotion = clothMotion = 0;
        next = EditorApplication.timeSinceStartup;
        EditorApplication.update -= Tick; EditorApplication.update += Tick;
    }
    private static IEnumerator PrepareBuiltin(PetModelLibrary library)
    {
        var load = typeof(PetModelLibrary).GetMethod("LoadModel", BindingFlags.Instance | BindingFlags.NonPublic);
        yield return (IEnumerator)load.Invoke(library, new object[] { PetModelLibrary.BuiltinModelId, false, false });
        Run();
    }
    private static void Tick()
    {
        if (!EditorApplication.isPlaying) { error = "Play mode ended."; Finish(); return; }
        if (EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + 0.22;
        try {
            // CaptureScreenshot completes at the end of the render frame. Keep that state
            // intact until the next tick instead of capturing the following settings page.
            if (afterCapture != null) { var continuation = afterCapture; afterCapture = null; continuation(); return; }
            int step = phase++;
            if (step >= 13 && step < 13 + PageNames.Length)
            {
                int page = step - 13;
                if (tabs.FindPageContent(PageNames[page]) == null) throw new Exception("Missing settings page " + PageNames[page]);
                Capture("settings-" + page);
                if (page + 1 < PageNames.Length) afterCapture = () => tabs.ShowPage(page + 1);
                return;
            }
            // Keep later protocol/animation phases stable as settings pages are added.
            if (step >= 13 + PageNames.Length) step -= PageNames.Length - 5;
            if (step == 0) {
                settings.Close(); actions.OpenChat(); Canvas.ForceUpdateCanvases(); appearance.UpdateChatPlacement();
                chatAnchor = chat.anchoredPosition; character.PlayAction("happy");
            } else if (step == 1) { AssertStableChat(); character.PlayAction("wave"); }
            else if (step == 2) {
                AssertStableChat(); Capture("stable-chat");
                afterCapture = () => chat.gameObject.SetActive(false);
                var bones = character.GetComponentsInChildren<Transform>(true);
                knees = bones.Where(b => b.name.StartsWith("ひざ") || b.name.StartsWith("足首")).ToDictionary(b => b, b => b.localRotation);
                cloth = bones.Where(b => b.name.StartsWith("qz_") || b.name.StartsWith("qh_") || b.name.StartsWith("hf_")).ToDictionary(b => b, b => b.localRotation);
                if (character.GetComponent<PetSecondaryMotion>().SimulatedBoneCount == 0) throw new Exception("No cloth/hair spring bones configured.");
                character.PlayAction("walk");
            } else if (step >= 3 && step <= 10) {
                if (!character.IsWalking) throw new Exception("Walk state did not remain active.");
                foreach (var bone in knees) kneeMotion = Mathf.Max(kneeMotion, Quaternion.Angle(bone.Value, bone.Key.localRotation));
                foreach (var bone in cloth) clothMotion = Mathf.Max(clothMotion, Quaternion.Angle(bone.Value, bone.Key.localRotation));
                Capture("walk-" + (step - 3));
            } else if (step == 11) {
                if (kneeMotion < 2 || clothMotion < 0.03f) throw new Exception("Insufficient lower-body or cloth motion: " + kneeMotion + ", " + clothMotion);
                character.StopWalking();
                UnityEngine.Object.FindAnyObjectByType<PetSpeechBubble>().ShowMessage("我在这里。聊天窗收起时，回复会显示在头顶。点击气泡就能展开聊天。");
                if (client.SpeechEnabled != (PlayerPrefs.GetInt("desktop_pet.speech", 0) == 1)) throw new Exception("Speech preference mismatch.");
            } else if (step == 12) {
                var bubble = GameObject.Find("DesktopPetCanvas").transform.Find("SpeechBubble");
                if (!bubble.gameObject.activeInHierarchy) throw new Exception("Hidden-chat bubble was not displayed.");
                AssertBubblePlacement();
                Capture("bubble"); afterCapture = () => { settings.Open(); tabs.ShowPage(0); };
            } else if (step == 18) {
                settings.Close(); UnityEngine.Object.FindAnyObjectByType<PetDataPanel>().OpenMemories();
            } else if (step == 19) {
                Capture("memory-manager"); afterCapture = () => {
                    UnityEngine.Object.FindAnyObjectByType<PetDataPanel>().Close();
                    actions.OpenChat(); client.onReplyDelta.AddListener(OnDelta); client.onReplyText.AddListener(OnReply); client.onRequestError.AddListener(OnError);
                    typeof(PetBackendHttpClient).GetField("<SpeechEnabled>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, false);
                    deadline = EditorApplication.timeSinceStartup + 15; client.SendChat("你好，请打个招呼。");
                };
            } else if (step == 20) {
                if (!replySeen && error == null && EditorApplication.timeSinceStartup < deadline) { phase--; return; }
                if (error != null) throw new Exception(error);
                if (!replySeen || deltaCount == 0) throw new Exception("No final reply or deltas reached Unity.");
                Capture("stream-chat");
            } else if (step == 21) {
                chat.gameObject.SetActive(false);
                character.transform.parent.localScale = Vector3.one * 0.35f;
                UnityEngine.Object.FindAnyObjectByType<PetSpeechBubble>().ShowMessage("我在这里，陪你慢慢把今天的事情做好。");
            } else if (step >= 22 && step <= 24) {
                AssertBubblePlacement(); Capture("bubble-size-" + (step - 22));
                if (step < 24) afterCapture = () => character.transform.parent.localScale = Vector3.one * (step == 22 ? 0.65f : 1.05f);
            } else if (step >= 25 && step < 25 + GestureActions.Length * 2) {
                int index = (step - 25) / 2;
                if ((step - 25) % 2 == 0) {
                    character.transform.parent.localScale = originalScale;
                    character.PlayAction(GestureActions[index]);
                    next = EditorApplication.timeSinceStartup + 1.1;
                } else {
                    if (!character.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + GestureStates[index]))
                        throw new Exception("Gesture did not enter: " + GestureActions[index]);
                    Capture("gesture-" + GestureActions[index]);
                }
            } else Finish();
        } catch (Exception exception) { error = exception.Message; Finish(); }
    }
    private static void OnDelta(string text) { if (!string.IsNullOrEmpty(text)) deltaCount++; }
    private static void OnReply(string text) { replySeen = !string.IsNullOrWhiteSpace(text); }
    private static void OnError(string text) { error = text; }
    private static void AssertStableChat()
    {
        appearance.UpdateChatPlacement();
        if (Vector2.Distance(chatAnchor, chat.anchoredPosition) > 0.1f) throw new Exception("Chat shifted with a gesture.");
    }
    private static void Capture(string name) { ScreenCapture.CaptureScreenshot(Path.Combine(directory, name + ".png")); }
    private static void AssertBubblePlacement()
    {
        var bubble = UnityEngine.Object.FindAnyObjectByType<PetSpeechBubble>();
        bubble.UpdatePlacement();
        var root = GameObject.Find("DesktopPetCanvas").transform.Find("SpeechBubble") as RectTransform;
        var corners = new Vector3[4]; root.GetWorldCorners(corners);
        if (!bubble.TryGetHeadAnchor(out Vector2 head)) throw new Exception("Missing head anchor.");
        bool coversHead = corners[0].y < head.y && head.x > corners[0].x - 8 && head.x < corners[2].x + 8;
        if (coversHead || corners[1].y > Screen.height + 1 || corners[0].x < -1 || corners[2].x > Screen.width + 1)
            throw new Exception("Bubble covers the head or exceeds the viewport: " + corners[0] + ".." + corners[2] + ", head=" + head);
        float opacity = root.GetComponent<UnityEngine.UI.Image>().color.a;
        if (Mathf.Abs(opacity - PetSpeechBubble.BackgroundOpacity) > 0.001f) throw new Exception("Bubble opacity was overwritten.");
    }
    private static void Finish()
    {
        EditorApplication.update -= Tick;
        if (client != null) typeof(PetBackendHttpClient).GetField("<SpeechEnabled>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, originalSpeech);
        if (client != null) { client.onReplyDelta.RemoveListener(OnDelta); client.onReplyText.RemoveListener(OnReply); client.onRequestError.RemoveListener(OnError); }
        character?.StopWalking(); if (character != null) character.transform.parent.localScale = originalScale;
        settings?.Close(); if (chat != null) chat.gameObject.SetActive(false);
        string report = (error == null ? "PASSED" : "FAILED: " + error) + "\nKnee motion degrees=" + kneeMotion + "\nCloth motion degrees=" + clothMotion + "\nStream delta events=" + deltaCount;
        File.WriteAllText(Path.Combine(directory, "acceptance.txt"), report);
        if (error == null) Debug.Log("DESKTOP_PET_ACCEPTANCE " + report); else Debug.LogError(report);
        if (!string.IsNullOrEmpty(restoreModel)) {
            UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>()?.SelectModel(restoreModel);
            restoreModel = null;
        }
    }
    private static void CheckSseDecoder()
    {
        string frame = "{\"type\":\"delta\",\"text\":\"你好🙂\\n\\\"\"}";
        byte[] data = Encoding.UTF8.GetBytes("data: " + frame + "\r\n\r\n");
        var receive = typeof(PetSseDownloadHandler).GetMethod("ReceiveData", BindingFlags.Instance | BindingFlags.NonPublic);
        using var handler = new PetSseDownloadHandler();
        foreach (byte value in data) if (!(bool)receive.Invoke(handler, new object[] { new[] { value }, 1 })) throw new Exception("SSE receive failed.");
        if (!handler.TryRead(out string decoded) || decoded != frame || handler.TryRead(out _)) throw new Exception("SSE UTF-8/frame boundary decoding failed.");
    }
}
