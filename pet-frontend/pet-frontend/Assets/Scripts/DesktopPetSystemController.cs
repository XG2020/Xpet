using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Windows tray recovery and an explicit desktop frame-rate preference.</summary>
public sealed class DesktopPetSystemController : MonoBehaviour
{
    public bool IsVisible { get; private set; } = true;
    public bool LowPower { get; private set; }
    private DesktopPetUIActions actions;
    private TransparentWindowController windowController;
    private readonly ConcurrentQueue<int> events = new ConcurrentQueue<int>();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private const uint TrayMessage = 0x8000 + 77;
    private const int HotkeyId = 0x4C50;
    private IntPtr window;
    private bool registered, hotkeyRegistered;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData {
        public uint size; public IntPtr window; public uint id, flags, callback; public IntPtr icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public uint state, stateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info;
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string infoTitle;
        public uint infoFlags; public Guid guid; public IntPtr balloon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int x, y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string path, uint index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    private NotifyIconData iconData;
    private IntPtr applicationIcon;
#endif
    private IEnumerator Start()
    {
        LowPower = PlayerPrefs.GetInt("desktop_pet.low_power", 0) == 1;
        actions = GetComponent<DesktopPetUIActions>();
        windowController = GetComponent<TransparentWindowController>();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        var interaction = GetComponent<DesktopPetInteraction>();
        for (int i = 0; i < 120 && interaction != null && interaction.WindowHandle == IntPtr.Zero; i++) yield return null;
        window = interaction != null ? interaction.WindowHandle : IntPtr.Zero;
        if (window == IntPtr.Zero) yield break;
        DesktopPetInteraction.NativeMessage += OnNativeMessage;
        applicationIcon = ExtractIcon(IntPtr.Zero, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, 0);
        iconData = new NotifyIconData { size = (uint)Marshal.SizeOf<NotifyIconData>(), window = window, id = 1,
            flags = 1 | 2 | 4, callback = TrayMessage, icon = applicationIcon.ToInt64() > 1 ? applicationIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            tip = "Xpet · 双击显隐 · Ctrl+Alt+P", info = "", infoTitle = "" };
        registered = Shell_NotifyIcon(0, ref iconData);
        if (registered) { iconData.version = 4; Shell_NotifyIcon(4, ref iconData); }
        hotkeyRegistered = RegisterHotKey(window, HotkeyId, 0x4000 | 1 | 2, 0x50);
#else
        yield return null;
#endif
    }
    private void Update()
    {
        int fps = !IsVisible ? 5 : windowController != null && windowController.IsDragging ? 60
            : LowPower || GetComponent<PetBehaviorCoordinator>()?.IsSleeping == true ? 15 : 30;
        if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
        while (events.TryDequeue(out int code)) {
            if (code == 1) ToggleVisibility();
            else if (code == 2) ShowTrayMenu();
        }
    }
    public void ToggleLowPower() => SetLowPower(!LowPower);
    public void SetLowPower(bool value) { LowPower = value; PlayerPrefs.SetInt("desktop_pet.low_power", value ? 1 : 0); PlayerPrefs.Save(); }
    public void ToggleVisibility() => SetVisible(!IsVisible);
    public void SetVisible(bool visible)
    {
        GetComponent<PetBehaviorCoordinator>()?.NoteActivity(true);
        IsVisible = visible;
        if (!visible) {
            GetComponent<PetBackendHttpClient>()?.CancelCurrentRequest();
            actions?.CloseContextMenu();
        }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (window != IntPtr.Zero) ShowWindow(window, visible ? 4 : 0);
#endif
    }
    public void OpenChat()
    {
        SetVisible(true);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (window != IntPtr.Zero) SetForegroundWindow(window);
#endif
        actions?.OpenChat();
    }
    public void OpenSettings()
    {
        SetVisible(true);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (window != IntPtr.Zero) SetForegroundWindow(window);
#endif
        actions?.OpenSettings();
    }
    private void ShowTrayMenu()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try {
            AppendMenu(menu, 0, (UIntPtr)1, IsVisible ? "隐藏宠物" : "显示宠物");
            AppendMenu(menu, 0, (UIntPtr)2, "聊天");
            AppendMenu(menu, 0, (UIntPtr)3, "设置");
            AppendMenu(menu, LowPower ? 8u : 0u, (UIntPtr)4, "低功耗模式");
            AppendMenu(menu, 0x800, UIntPtr.Zero, "");
            AppendMenu(menu, 0, (UIntPtr)5, "退出");
            GetCursorPos(out Point cursor); SetForegroundWindow(window);
            uint command = TrackPopupMenu(menu, 0x100 | 0x2, cursor.x, cursor.y, 0, window, IntPtr.Zero);
            PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            switch (command) { case 1: ToggleVisibility(); break; case 2: OpenChat(); break;
                case 3: OpenSettings(); break; case 4: ToggleLowPower(); break; case 5: Application.Quit(); break; }
        } finally { DestroyMenu(menu); }
#endif
    }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private void OnNativeMessage(uint message, IntPtr w, IntPtr l)
    {
        if (message == 0x0312 && w.ToInt32() == HotkeyId) events.Enqueue(1);
        if (message == TrayMessage) {
            int code = (int)(l.ToInt64() & 0xFFFF);
            if (code == 0x0203 || code == 0x0401) events.Enqueue(1);
            else if (code == 0x0205 || code == 0x007B) events.Enqueue(2);
        }
    }
#endif
    private void OnApplicationQuit() { ReleaseNativeResources(); }
    private void OnDestroy() { ReleaseNativeResources(); }
    private void ReleaseNativeResources()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        DesktopPetInteraction.NativeMessage -= OnNativeMessage;
        if (registered) Shell_NotifyIcon(2, ref iconData);
        if (hotkeyRegistered) UnregisterHotKey(window, HotkeyId);
        if (applicationIcon.ToInt64() > 1) DestroyIcon(applicationIcon);
        registered = hotkeyRegistered = false; applicationIcon = IntPtr.Zero;
#endif
    }
}
