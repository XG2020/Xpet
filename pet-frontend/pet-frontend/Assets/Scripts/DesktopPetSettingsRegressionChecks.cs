using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Explicit QA entry point for settings layout and render stability.</summary>
public sealed class DesktopPetSettingsRegressionChecks : MonoBehaviour
{
    private string output;
    private bool exitAfter;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
#endif
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartStandaloneCheck()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--xpet-settings-checks") < 0) return;
        int index = Array.IndexOf(args, "--xpet-settings-report-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        var runner = new GameObject("SettingsRegressionChecks").AddComponent<DesktopPetSettingsRegressionChecks>();
        runner.output = Path.GetFullPath(args[index + 1]); runner.exitAfter = true;
        runner.StartCoroutine(runner.Check());
    }
#if UNITY_EDITOR
    [UnityEditor.MenuItem("Desktop Pet/Run Settings Interaction Checks")]
    public static void RunEditor()
    {
        if (!UnityEditor.EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
        var runner = new GameObject("SettingsRegressionChecks").AddComponent<DesktopPetSettingsRegressionChecks>();
        runner.output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/settings-redesign"));
        runner.StartCoroutine(runner.Check());
    }
#endif
    private IEnumerator Check()
    {
        Directory.CreateDirectory(output);
        for (int i = 0; i < 8; i++) yield return null;
        var settings = FindAnyObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
        var tabs = FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        var desktop = FindAnyObjectByType<TransparentWindowController>();
        if (settings == null || tabs == null || desktop == null) { Finish("Missing settings services."); yield break; }
        bool wasOpen = settings.IsOpen;
        int originalPage = tabs.SelectedPage;
        Camera view = Camera.main;
        Vector3 cameraPosition = view.transform.position;
        float cameraSize = view.orthographicSize;
        Rect cameraRect = view.rect;
        int renderWidth = Screen.width, renderHeight = Screen.height;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        IntPtr window = FindAnyObjectByType<DesktopPetInteraction>().WindowHandle;
        NativeRect beforeNative = default;
        bool nativeAvailable = window != IntPtr.Zero && GetWindowRect(window, out beforeNative);
        if (!nativeAvailable) { Finish("Missing native window."); yield break; }
#endif
        double maximumOpenMilliseconds = 0;
        string error = null;
        for (int i = 0; i < 3; i++) {
            settings.Close();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            settings.Open(); stopwatch.Stop();
            maximumOpenMilliseconds = Math.Max(maximumOpenMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            yield return null;
            try {
                Require(Vector3.Distance(view.transform.position, cameraPosition) < .0001f &&
                    Mathf.Abs(view.orthographicSize - cameraSize) < .0001f && view.rect == cameraRect,
                    "Opening settings changed the character camera.");
                Require(Screen.width == renderWidth && Screen.height == renderHeight, "Opening settings resized the render surface.");
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
                Require(GetWindowRect(window, out NativeRect afterNative) &&
                    afterNative.right - afterNative.left == beforeNative.right - beforeNative.left &&
                    afterNative.bottom - afterNative.top == beforeNative.bottom - beforeNative.top,
                    "Opening settings resized the native window.");
#endif
            } catch (Exception exception) { error = exception.Message; break; }
        }
        if (error == null) {
            Canvas.ForceUpdateCanvases();
            try {
                var card = tabs.transform as RectTransform;
                var navigation = card.Find("Body/Navigation") as RectTransform;
                var configuration = card.Find("Body/Configuration") as RectTransform;
                Require(navigation != null && configuration != null && tabs.SidebarMenu.childCount == 10, "Missing settings navigation items.");
                Bounds left = RootBounds(card, navigation);
                Bounds right = RootBounds(card, configuration);
                Require(left.max.x < right.min.x && left.min.x >= card.rect.xMin - 1 && right.max.x <= card.rect.xMax + 1,
                    "Sidebar and configuration overlap or escape the card.");
                Require(navigation.GetComponent<UnityEngine.UI.ScrollRect>().vertical, "Sidebar cannot scroll in a short window.");
                Vector2 outside = new Vector2(4, 4);
                Require(!desktop.IsOverUI(outside), "Empty desktop is blocked by settings.");
                var blocker = new GameObject("Blocker", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                blocker.transform.SetParent(card.GetComponentInParent<Canvas>().transform, false);
                var blockRect = blocker.GetComponent<RectTransform>();
                blockRect.anchorMin = Vector2.zero; blockRect.anchorMax = Vector2.one;
                blockRect.offsetMin = blockRect.offsetMax = Vector2.zero;
                blocker.GetComponent<UnityEngine.UI.Image>().color = Color.clear;
                Canvas.ForceUpdateCanvases();
                bool blocked = desktop.IsOverUI(outside);
                Destroy(blocker);
                Require(!blocked, "Full-canvas transparent blocker captures desktop clicks.");
                var actions = FindAnyObjectByType<DesktopPetUIActions>();
                actions.OpenContextMenu(new Vector2(Screen.width * .5f, Screen.height * .5f));
                var menu = card.GetComponentInParent<Canvas>().transform.Find("ContextMenuRoot") as RectTransform;
                Require(menu.GetChild(menu.childCount - 1).name == "Quit", "Quit is not the final menu item.");
                Require(desktop.IsOverUI(RectTransformUtility.WorldToScreenPoint(null, menu.TransformPoint(menu.rect.center))),
                    "Context menu is incorrectly passed through.");
                actions.CloseContextMenu();
            } catch (Exception exception) { error = exception.Message; }
        }
        if (error == null) {
            foreach (string page in new[] { "角色卡", "外观", "对话", "语音", "记忆", "工具", "动作管理", "模型导入", "后端日志", "关于" }) {
                tabs.ShowPage(page); yield return null;
                try { Require(tabs.FindPageContent(page).gameObject.activeInHierarchy, "Page did not switch: " + page); }
                catch (Exception exception) { error = exception.Message; break; }
            }
        }
        if (error == null) {
            tabs.ShowPage("对话"); yield return null;
            var panel = FindAnyObjectByType<BackendSettingsPanel>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var provider = typeof(BackendSettingsPanel).GetField("llmProviderDropdown", flags).GetValue(panel) as TMP_Dropdown;
            var input = typeof(BackendSettingsPanel).GetField("llmModelInput", flags).GetValue(panel) as TMP_InputField;
            var baseInput = typeof(BackendSettingsPanel).GetField("baseUrlInput", flags).GetValue(panel) as TMP_InputField;
            string originalInput = input.text, originalBase = baseInput.text;
            int originalProvider = provider != null ? provider.value : 0;
            try {
                Require(provider != null && provider.options.Count == 4, "Missing protocol dropdown options.");
                foreach (string protocol in new[] { "openai", "ollama", "mock", "anthropic" }) {
                    provider.value = Array.IndexOf(new[] { "openai", "ollama", "mock", "anthropic" }, protocol);
                    Require((string)typeof(BackendSettingsPanel).GetMethod("SelectedLlmProvider", flags).Invoke(panel, null) == protocol,
                        "Dropdown selects the wrong protocol.");
                    Require(baseInput.text == originalBase, "Changing protocol erased the configured URL.");
                }
                typeof(BackendSettingsPanel).GetMethod("ApplyLlmProvider", flags).Invoke(panel, new object[] { "systemone" });
                Require(provider.value == 3, "SystemOne alias is not recognized.");
                provider.value = originalProvider;
                input.SetTextWithoutNotify("");
            } catch (Exception exception) { error = exception.Message; }
            if (error == null) {
                input.Select(); input.ActivateInputField(); yield return null;
                try { Require(!input.placeholder.enabled && input.text == "", "Focused empty input still shows its hint."); }
                catch (Exception exception) { error = exception.Message; }
                input.SetTextWithoutNotify("user-entered-value"); input.DeactivateInputField(); yield return null;
                try {
                    Require(input.text == "user-entered-value", "Focus handling erased a real value.");
                    Require(input.placeholder.color == PetInputPlaceholder.HintColor, "Placeholder is not gray.");
                } catch (Exception exception) { error = exception.Message; }
            }
            input.SetTextWithoutNotify(originalInput);
        }
        if (error == null) {
            tabs.ShowPage("对话");
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "settings.png"));
            for (int i = 0; i < 3; i++) yield return null;
        }
        tabs.ShowPage(originalPage);
        if (!wasOpen) settings.Close();
        if (error == null && Array.IndexOf(Environment.GetCommandLineArgs(), "--xpet-action-checks") >= 0)
            yield return CheckRealJump(message => error = message);
        File.WriteAllText(Path.Combine(output, "timing.txt"), "Maximum synchronous open time: " + maximumOpenMilliseconds.ToString("0.00") + " ms\n");
        Finish(error);
    }

    private IEnumerator CheckRealJump(Action<string> failed)
    {
        var client = FindAnyObjectByType<PetBackendHttpClient>();
        var bridge = client.ActionBridge;
        var cards = FindAnyObjectByType<PetCharacterCards>();
        var models = FindAnyObjectByType<PetModelLibrary>();
        float readyUntil = Time.realtimeSinceStartup + 30;
        while ((!cards.Ready || cards.IsBusy || models.IsBusy || client.CharacterSwitching) && Time.realtimeSinceStartup < readyUntil) yield return null;
        for (int i = 0; i < 5; i++) yield return null;
        if (bridge == null || !bridge.GetCallableActionIds().Contains("jump")) { failed("Loaded model does not advertise a runnable Jump state."); yield break; }
        Animator animator = bridge.PlaybackAnimator;
        Transform hips = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips == null) hips = bridge.ActiveModelRoot.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(bone => bone.name == "センター" || bone.name == "腰" || bone.name.Equals("Hips", StringComparison.OrdinalIgnoreCase));
        float baseline = hips != null ? hips.position.y : 0, rise = 0;
        string receivedAction = null, requestError = null;
        File.WriteAllText(Path.Combine(output, "capability-state.txt"), "Library/client character: " + PetActionLibrary.CharacterId + "/" + client.CharacterId + "\nActions: " + string.Join(",", bridge.GetCallableActionIds()) + "\nAnimator: " + animator.name + "\nMeasured bone: " + (hips != null ? hips.name : "none"));
        bool sawJump = false, speech = client.SpeechEnabled;
        var speechProperty = typeof(PetBackendHttpClient).GetProperty("SpeechEnabled");
        speechProperty.SetValue(client, false);
        UnityEngine.Events.UnityAction<string> action = value => receivedAction = value;
        UnityEngine.Events.UnityAction<string> failure = value => requestError = value;
        client.onReplyAction.AddListener(action); client.onRequestError.AddListener(failure);
        client.SendChat("跳一下");
        float deadline = Time.realtimeSinceStartup + 115, actionAt = -1;
        while (Time.realtimeSinceStartup < deadline && requestError == null) {
            if (receivedAction != null && actionAt < 0) actionAt = Time.realtimeSinceStartup;
            var current = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            sawJump |= current.IsName("Base Layer.Jump") || (animator.IsInTransition(0) && next.IsName("Base Layer.Jump"));
            if (hips != null) rise = Mathf.Max(rise, hips.position.y - baseline);
            if (actionAt >= 0 && Time.realtimeSinceStartup - actionAt > 3) break;
            yield return null;
        }
        client.onReplyAction.RemoveListener(action); client.onRequestError.RemoveListener(failure);
        speechProperty.SetValue(client, speech);
        File.WriteAllText(Path.Combine(output, "action-jump.txt"), "Requested: jump\nReceived: " + receivedAction + "\nAnimator Jump observed: " + sawJump + "\nHip rise: " + rise.ToString("0.000") + " m\nError: " + requestError);
        if (requestError != null) failed("Real model chat failed: " + requestError);
        else if (receivedAction != "jump" || !sawJump || (hips != null && rise < .02f)) failed("The real jump request did not produce a visible jump.");
    }
    private void Finish(string error)
    {
        string report = error == null ? "PASSED: stable camera and render dimensions; left menu/right configuration; nine page switches; protocol dropdown; gray hints and preserved input; transparent blockers ignored; context menu captures clicks and Quit is last. Native runs also verify unchanged native window dimensions."
            : "FAILED: " + error;
        File.WriteAllText(Path.Combine(output, "results.txt"), report);
        if (error == null) Debug.Log(report); else Debug.LogError(report);
        if (exitAfter) Application.Quit(error == null ? 0 : 1);
        else Destroy(gameObject);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Bounds RootBounds(RectTransform parent, RectTransform child)
    {
        var corners = new Vector3[4]; child.GetWorldCorners(corners);
        var bounds = new Bounds(parent.InverseTransformPoint(corners[0]), Vector3.zero);
        for (int i = 1; i < corners.Length; i++) bounds.Encapsulate(parent.InverseTransformPoint(corners[i]));
        return bounds;
    }
}
