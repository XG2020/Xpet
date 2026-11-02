using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>Opt-in hold-to-record shortcut. Polls only the configured chord outside explicit key setup.</summary>
[DisallowMultipleComponent]
public sealed class DesktopPetPushToTalk : MonoBehaviour
{
    public const string EnabledPrefsKey = "desktop_pet.push_to_talk_enabled";
    public const string ShortcutPrefsKey = "desktop_pet.push_to_talk_shortcut";
    public const string DefaultShortcut = "Ctrl+Alt+V";
    [SerializeField] private DesktopPetInteraction interaction;
    [SerializeField] private UnityEngine.UI.Toggle enabledToggle;
    [SerializeField] private UnityEngine.UI.Button setShortcutButton;
    [SerializeField] private TMP_Text shortcutText;
    [SerializeField] private TMP_Text statusText;
    public bool PushToTalkEnabled { get; private set; }
    public bool IsCapturingShortcut { get; private set; }
    public string ShortcutText => shortcut.ToString();
    public string Status { get; private set; } = "按住说话已关闭。";
    private Shortcut shortcut;
    private readonly HoldGate hold = new HoldGate();
    private bool captureArmed;
    private bool waitForTranscription;
    private float captureStartedAt;
    private static readonly int[] CaptureKeys = MakeCaptureKeys();

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
#endif

    public readonly struct Shortcut
    {
        public readonly int VirtualKey;
        public readonly bool Ctrl, Alt, Shift;
        public Shortcut(int virtualKey, bool ctrl, bool alt, bool shift)
        { VirtualKey = virtualKey; Ctrl = ctrl; Alt = alt; Shift = shift; }
        public bool Matches(bool main, bool ctrl, bool alt, bool shift, bool windows)
            => main && !windows && Ctrl == ctrl && Alt == alt && Shift == shift;
        public override string ToString()
            => (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + KeyName(VirtualKey);
    }

    public enum HoldAction { None, Start, Finish }

    /// <summary>Pure edge/release logic; a failed start never retries while keys remain held.</summary>
    public sealed class HoldGate
    {
        private bool requireRelease = true;
        private bool wasHeld;
        public bool OwnsRecording { get; private set; }
        public HoldAction Step(bool chordHeld, bool allReleased, bool blocked)
        {
            if (blocked)
            {
                bool finish = OwnsRecording;
                Reset();
                return finish ? HoldAction.Finish : HoldAction.None;
            }
            if (requireRelease)
            {
                if (allReleased) { requireRelease = false; wasHeld = false; }
                return HoldAction.None;
            }
            if (!chordHeld)
            {
                bool finish = OwnsRecording;
                OwnsRecording = false;
                wasHeld = false;
                // A primary key held through release cannot reopen the microphone
                // when a modifier is pressed again: the complete chord must release.
                if (finish) requireRelease = true;
                return finish ? HoldAction.Finish : HoldAction.None;
            }
            if (wasHeld) return HoldAction.None;
            wasHeld = true;
            return HoldAction.Start;
        }
        public void MarkStarted(bool accepted) { OwnsRecording = accepted; if (!accepted) requireRelease = true; }
        public void Reset() { requireRelease = true; wasHeld = false; OwnsRecording = false; }
    }

    public static bool TryParseShortcut(string value, out Shortcut result, out string error)
    {
        result = default;
        error = "请使用 Ctrl 或 Alt，搭配字母、数字、F2–F12 或空格；可再加 Shift。";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 48) return false;
        bool ctrl = false, alt = false, shift = false;
        int primary = 0;
        foreach (string part in value.Split('+'))
        {
            string token = part.Trim().ToUpperInvariant();
            if (token == "CTRL" || token == "CONTROL") { if (ctrl) return false; ctrl = true; }
            else if (token == "ALT") { if (alt) return false; alt = true; }
            else if (token == "SHIFT") { if (shift) return false; shift = true; }
            else
            {
                if (primary != 0) return false;
                if (token.Length == 1 && (token[0] >= 'A' && token[0] <= 'Z' || token[0] >= '0' && token[0] <= '9')) primary = token[0];
                else if (token == "SPACE" || token == "空格") primary = 0x20;
                else if (token.StartsWith("F", StringComparison.Ordinal) && int.TryParse(token.Substring(1), out int function)
                    && function >= 2 && function <= 12) primary = 0x70 + function - 1;
                else { error = "不支持这个按键；Windows 键、Esc 和单独的修饰键不能用于录音。"; return false; }
            }
        }
        if (primary == 0 || !ctrl && !alt) return false;
        result = new Shortcut(primary, ctrl, alt, shift);
        error = null;
        return true;
    }

    private void Awake()
    {
        if (interaction == null) interaction = GetComponent<DesktopPetInteraction>();
        PushToTalkEnabled = PlayerPrefs.GetInt(EnabledPrefsKey, 0) == 1;
        if (!TryParseShortcut(PlayerPrefs.GetString(ShortcutPrefsKey, DefaultShortcut), out shortcut, out _))
            TryParseShortcut(DefaultShortcut, out shortcut, out _);
        if (PushToTalkEnabled) Application.runInBackground = true;
        SyncControls();
        SetStatus(ReadyStatus());
    }

    private void OnEnable()
    {
        enabledToggle?.onValueChanged.AddListener(SetPushToTalkEnabled);
        setShortcutButton?.onClick.AddListener(BeginShortcutCapture);
        hold.Reset();
        SyncControls();
    }

    private void OnDisable()
    {
        enabledToggle?.onValueChanged.RemoveListener(SetPushToTalkEnabled);
        setShortcutButton?.onClick.RemoveListener(BeginShortcutCapture);
        CancelOwnedRecording();
        IsCapturingShortcut = false;
    }

    private void OnApplicationQuit() { CancelOwnedRecording(); }
    private void OnApplicationPause(bool paused) { if (paused) CancelOwnedRecording(); }

    public void SetPushToTalkEnabled(bool value)
    {
        PushToTalkEnabled = value;
        PlayerPrefs.SetInt(EnabledPrefsKey, value ? 1 : 0);
        PlayerPrefs.Save();
        CancelOwnedRecording();
        if (value) Application.runInBackground = true;
        SyncControls();
        SetStatus(ReadyStatus());
    }

    public void BeginShortcutCapture()
    {
        if (IsCapturingShortcut) { EndCapture("已取消设置，保留原组合键。"); return; }
        if (interaction != null && interaction.IsRecording)
        { SetStatus("请先结束当前录音，再设置组合键。"); return; }
        CancelOwnedRecording();
        IsCapturingShortcut = true;
        captureArmed = false;
        captureStartedAt = Time.unscaledTime;
        EventSystem.current?.SetSelectedGameObject(null);
        SyncControls();
        SetStatus("先松开按键，再按新的组合键；Esc 取消。配置期间不会开始录音。");
    }

    private void Update()
    {
        if (IsCapturingShortcut) { PollCapture(); return; }
        if (!PushToTalkEnabled) return;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        bool main = ReadKey(shortcut.VirtualKey), ctrl = ReadKey(0x11), alt = ReadKey(0x12), shift = ReadKey(0x10);
        bool windows = ReadKey(0x5B) || ReadKey(0x5C);
        bool held = shortcut.Matches(main, ctrl, alt, shift, windows);
        bool released = !main && (!shortcut.Ctrl || !ctrl) && (!shortcut.Alt || !alt) && (!shortcut.Shift || !shift);
        bool blocked = interaction == null || !interaction.isActiveAndEnabled || IsTypingInPetInput();
        HoldAction action = hold.Step(held, released, blocked);
        if (action == HoldAction.Start)
        {
            bool accepted = interaction.StartPushToTalkRecording();
            hold.MarkStarted(accepted);
            SetStatus(interaction.RecordingStatus);
        }
        else if (action == HoldAction.Finish)
        {
            interaction?.FinishPushToTalkRecording();
            waitForTranscription = interaction != null && interaction.IsTranscribing;
            SetStatus(interaction != null ? interaction.RecordingStatus : "语音服务不可用。");
        }
        if (hold.OwnsRecording && interaction != null && !interaction.IsPushToTalkRecording)
        {
            // Manual stop or the duration limit has already finished this capture.
            hold.Reset();
            waitForTranscription = interaction.IsTranscribing;
            SetStatus(interaction.RecordingStatus);
        }
        if (waitForTranscription && interaction != null)
        {
            SetStatus(interaction.RecordingStatus);
            if (!interaction.IsTranscribing) waitForTranscription = false;
        }
#endif
    }

    private static bool IsTypingInPetInput()
    {
        if (!Application.isFocused || EventSystem.current == null) return false;
        GameObject selected = EventSystem.current.currentSelectedGameObject;
        return selected != null && (selected.GetComponentInParent<TMP_InputField>() != null
            || selected.GetComponentInParent<UnityEngine.UI.InputField>() != null);
    }

    private void PollCapture()
    {
        if (!Application.isFocused || setShortcutButton == null || !setShortcutButton.gameObject.activeInHierarchy)
        { EndCapture("已取消设置，保留原组合键。"); return; }
        if (Time.unscaledTime - captureStartedAt > 20f || ReadKey(0x1B))
        { EndCapture("已取消设置，保留原组合键。"); return; }
        bool ctrl = ReadKey(0x11), alt = ReadKey(0x12), shift = ReadKey(0x10);
        bool windows = ReadKey(0x5B) || ReadKey(0x5C);
        int primary = 0, count = 0;
        // This scan runs only while the user explicitly configures a key and the
        // settings button is visible/focused. Normal polling never scans other keys.
        foreach (int candidate in CaptureKeys) if (ReadKey(candidate)) { primary = candidate; count++; }
        if (!captureArmed)
        {
            if (!ctrl && !alt && !shift && !windows && count == 0) captureArmed = true;
            return;
        }
        if (windows) { SetStatus("不支持 Windows 键，请松开后改用 Ctrl 或 Alt。Esc 取消。"); captureArmed = false; return; }
        if (count == 0) return;
        if (count != 1 || !ctrl && !alt)
        { SetStatus("请按 Ctrl 或 Alt + 一个主键；可再加 Shift。Esc 取消。"); captureArmed = false; return; }
        var candidateShortcut = new Shortcut(primary, ctrl, alt, shift);
        if (!TryParseShortcut(candidateShortcut.ToString(), out shortcut, out string error))
        { SetStatus(error); captureArmed = false; return; }
        PlayerPrefs.SetString(ShortcutPrefsKey, shortcut.ToString());
        PlayerPrefs.Save();
        EndCapture("已保存 " + shortcut + "；松开全部按键后即可使用。");
    }

    private void EndCapture(string message)
    {
        IsCapturingShortcut = false;
        hold.Reset();
        SyncControls();
        SetStatus(message);
    }

    private void CancelOwnedRecording()
    {
        interaction?.CancelPushToTalkRecording();
        hold.Reset();
        waitForTranscription = false;
    }

    private void SyncControls()
    {
        enabledToggle?.SetIsOnWithoutNotify(PushToTalkEnabled);
        if (shortcutText != null) shortcutText.text = "当前组合键：" + shortcut;
        TMP_Text buttonText = setShortcutButton != null ? setShortcutButton.GetComponentInChildren<TMP_Text>(true) : null;
        if (buttonText != null) buttonText.text = IsCapturingShortcut ? "取消设置" : "设置按键";
    }

    private string ReadyStatus()
    {
        if (!PushToTalkEnabled) return "已关闭 · 开启后才响应录音组合键。";
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return "按住 " + shortcut + " 说话，松开任一按键后识别发送。";
#else
        return "组合键已保存；全局按住说话仅在 Windows 桌面版运行。";
#endif
    }

    private void SetStatus(string value)
    {
        Status = value;
        if (statusText != null) statusText.text = value;
    }

    private static int[] MakeCaptureKeys()
    {
        var keys = new List<int>();
        for (int value = 'A'; value <= 'Z'; value++) keys.Add(value);
        for (int value = '0'; value <= '9'; value++) keys.Add(value);
        for (int value = 0x71; value <= 0x7B; value++) keys.Add(value);
        keys.Add(0x20);
        return keys.ToArray();
    }

    private static string KeyName(int value)
    {
        if (value >= 'A' && value <= 'Z' || value >= '0' && value <= '9') return ((char)value).ToString();
        if (value >= 0x71 && value <= 0x7B) return "F" + (value - 0x70 + 1);
        return value == 0x20 ? "Space" : "";
    }

    private static bool ReadKey(int value)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(value) & 0x8000) != 0;
#elif ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return false;
        switch (value)
        {
            case 0x11: return keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            case 0x12: return keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            case 0x10: return keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            case 0x5B: return keyboard.leftMetaKey.isPressed;
            case 0x5C: return keyboard.rightMetaKey.isPressed;
            case 0x1B: return keyboard.escapeKey.isPressed;
        }
        string name = value >= '0' && value <= '9' ? "Digit" + (char)value : KeyName(value);
        return Enum.TryParse(name, out Key key) && keyboard[key].isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (value >= 'A' && value <= 'Z') return Input.GetKey((KeyCode)(value - 'A' + (int)KeyCode.A));
        if (value >= '0' && value <= '9') return Input.GetKey((KeyCode)(value - '0' + (int)KeyCode.Alpha0));
        if (value >= 0x71 && value <= 0x7B) return Input.GetKey((KeyCode)(value - 0x70 + (int)KeyCode.F1));
        switch (value)
        {
            case 0x11: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            case 0x12: return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            case 0x10: return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            case 0x5B: return Input.GetKey(KeyCode.LeftWindows);
            case 0x5C: return Input.GetKey(KeyCode.RightWindows);
            case 0x1B: return Input.GetKey(KeyCode.Escape);
            case 0x20: return Input.GetKey(KeyCode.Space);
        }
        return false;
#else
        return false;
#endif
    }
}
