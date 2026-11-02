using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Manual/opt-in wake microphone capture and user-initiated Windows file drops.</summary>
[DisallowMultipleComponent]
public sealed class DesktopPetInteraction : MonoBehaviour
{
    // Subscribers must only enqueue native data here; Unity objects are handled from Update.
    public static event Action<uint, IntPtr, IntPtr> NativeMessage;
    public IntPtr WindowHandle {
        get {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return window;
#else
            return IntPtr.Zero;
#endif
        }
    }
    public bool IsRecording { get; private set; }
    public bool IsTranscribing => transcribing;
    public bool IsPushToTalkRecording => IsRecording && pushToTalkRecording;
    public string RecordingStatus { get; private set; } = "";
    private PetBackendHttpClient client;
    private AudioClip recording;
    private string microphone;
    private float startedAt;
    private bool transcribing;
    [SerializeField, Range(0.5f, 2f)] private float wakeSilenceSeconds = 1.05f;
    [SerializeField, Range(3f, 12f)] private float wakeSpeechWaitSeconds = 8f;
    [SerializeField, Range(0.003f, 0.1f)] private float speechRmsThreshold = 0.012f;
    private bool wakeRecording;
    private bool pushToTalkRecording;
    private bool speechDetected;
    private float speechEnergySeconds;
    private float lastSpeechAt;
    private float nextVoiceCheck;
    private float[] voiceSamples;
    private UnityWebRequest activeTranscription;
    private readonly ConcurrentQueue<string> droppedFiles = new ConcurrentQueue<string>();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private IntPtr window, originalProc;
    private bool nativeDetached;
    private volatile bool nativeShuttingDown;
    private WindowProc callback;
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr h, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder file, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
#endif
    private void Start()
    {
        client = GetComponent<PetBackendHttpClient>();
        if (client == null) client = FindAnyObjectByType<PetBackendHttpClient>();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // Tool windows are excluded from Process.MainWindowHandle. Reuse the
        // actual window already resolved before Start by the desktop controller.
        window = GetComponent<TransparentWindowController>()?.NativeWindowHandle ?? IntPtr.Zero;
        if (window == IntPtr.Zero) window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (window != IntPtr.Zero) {
            callback = HandleWindowMessage;
            originalProc = SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(callback));
            if (originalProc != IntPtr.Zero) DragAcceptFiles(window, true);
            else Debug.LogWarning("Desktop window callback was not installed: " + Marshal.GetLastWin32Error());
        }
#endif
    }
    private void Update()
    {
        if (client != null && droppedFiles.TryDequeue(out string path)) client.AnalyzeFile(path);
        if (!IsRecording) return;
        if (Time.unscaledTime - startedAt >= 29f) { FinishRecording(); return; }
        if (wakeRecording) CheckWakeRecording();
    }
    public void ToggleVoiceRecording()
    {
        if (IsRecording) { FinishRecording(); return; }
        StartRecording(false);
    }

    /// <summary>Called only by the locally enabled wake recognizer; never opens the chat panel.</summary>
    public bool StartWakeWordRecording() => StartRecording(true);

    /// <summary>Called only after the opted-in shortcut is pressed.</summary>
    public bool StartPushToTalkRecording()
    {
        if (!isActiveAndEnabled || !Application.isPlaying || !StartRecording(false)) return false;
        pushToTalkRecording = true;
        ReportStatus("正在录音 · 松开组合键后识别发送（最长 30 秒）。");
        return true;
    }

    public void FinishPushToTalkRecording()
    {
        if (IsPushToTalkRecording) FinishRecording();
    }

    public void CancelPushToTalkRecording()
    {
        if (IsPushToTalkRecording) CancelRecording("按住说话已取消，本次录音未发送。");
    }

    private bool StartRecording(bool fromWakeWord)
    {
        if (client == null) client = GetComponent<PetBackendHttpClient>();
        if (client == null) { ReportStatus("语音服务尚未就绪，请稍后重试。"); return false; }
        if (GetComponent<DesktopPetPushToTalk>()?.IsCapturingShortcut == true)
        { ReportStatus("正在设置录音组合键，请先完成或取消按键设置。"); return false; }
        if (IsRecording || transcribing || client.IsBusy || client.IsSpeaking || client.IsSpeechPending || client.CharacterSwitching)
        { ReportStatus("请等待当前回复或语音完成。"); return false; }
        GetComponent<PetBehaviorCoordinator>()?.NoteActivity();
        // Stop the local keyword recognizer before opening the same input device.
        GetComponent<DesktopPetWakeWord>()?.SuspendForInteraction();
        try
        {
            if (Microphone.devices.Length == 0)
            { ReportStatus("未找到麦克风，请检查设备和系统麦克风权限。"); return false; }
            microphone = Microphone.devices[0];
            recording = Microphone.Start(microphone, false, 30, 16000);
        }
        catch (Exception) { CancelRecording("无法打开麦克风，请检查设备权限。"); return false; }
        if (recording == null) { CancelRecording("麦克风无法录音。"); return false; }
        IsRecording = true;
        wakeRecording = fromWakeWord;
        pushToTalkRecording = false;
        speechDetected = false;
        speechEnergySeconds = 0;
        voiceSamples = fromWakeWord ? new float[512 * recording.channels] : null;
        startedAt = Time.unscaledTime;
        lastSpeechAt = startedAt;
        nextVoiceCheck = startedAt + 0.25f;
        ReportStatus(fromWakeWord ? "我在听 · 说完会自动发送。" : "正在录音 · 再次点击停止（最长 30 秒）。");
        return true;
    }

    private void CheckWakeRecording()
    {
        float now = Time.unscaledTime;
        if (now < nextVoiceCheck || recording == null) return;
        nextVoiceCheck = now + 0.05f;
        int position;
        try { position = Microphone.GetPosition(microphone); }
        catch (Exception) { CancelRecording("麦克风已断开，请检查设备。"); return; }
        if (position >= 512 && recording.GetData(voiceSamples, position - 512))
        {
            double energy = 0;
            foreach (float sample in voiceSamples) energy += sample * sample;
            bool voiced = Math.Sqrt(energy / voiceSamples.Length) >= speechRmsThreshold;
            if (voiced)
            {
                speechEnergySeconds += 0.05f;
                lastSpeechAt = now;
                if (speechEnergySeconds >= 0.15f) speechDetected = true;
            }
            else if (!speechDetected) speechEnergySeconds = 0;
        }
        if (!speechDetected && now - startedAt >= wakeSpeechWaitSeconds)
        { CancelRecording("没有听到说话，已结束本次录音。"); return; }
        if (speechDetected && now - lastSpeechAt >= wakeSilenceSeconds) FinishRecording();
    }

    private void CancelRecording(string status)
    {
        try { if (!string.IsNullOrEmpty(microphone)) Microphone.End(microphone); } catch (Exception) { }
        microphone = null;
        IsRecording = false;
        wakeRecording = false;
        pushToTalkRecording = false;
        voiceSamples = null;
        if (recording != null) Destroy(recording);
        recording = null;
        if (!string.IsNullOrEmpty(status)) ReportStatus(status);
    }
    private void FinishRecording()
    {
        if (!IsRecording) return;
        int sampleCount;
        try { sampleCount = Microphone.GetPosition(microphone); Microphone.End(microphone); microphone = null; }
        catch (Exception) { CancelRecording("无法读取录音，请重试。"); return; }
        IsRecording = false;
        bool emptyWake = wakeRecording && !speechDetected;
        wakeRecording = false;
        pushToTalkRecording = false;
        voiceSamples = null;
        if (emptyWake) { CancelRecording("没有听到说话，已结束本次录音。"); return; }
        if (recording == null || sampleCount < 1600) {
            if (recording != null) Destroy(recording);
            recording = null;
            ReportStatus("录音太短，请重试。"); return;
        }
        try
        {
            float[] samples = new float[Mathf.Min(sampleCount, recording.samples) * recording.channels];
            if (!recording.GetData(samples, 0)) { CancelRecording("无法读取录音，请重试。"); return; }
            byte[] wav = EncodeWav(samples, recording.channels, recording.frequency);
            Destroy(recording); recording = null;
            StartCoroutine(Transcribe(wav));
        }
        catch (Exception) { CancelRecording("无法处理本次录音，请检查麦克风后重试。"); }
    }
    private IEnumerator Transcribe(byte[] wav)
    {
        transcribing = true;
        ReportStatus("正在识别语音…");
        string characterId = client.CharacterId;
        using var request = new UnityWebRequest(DesktopPetPrefs.GetBackendBaseUrl() + "/api/asr?character_id=" + Uri.EscapeDataString(characterId), "POST");
        activeTranscription = request;
        request.uploadHandler = new UploadHandlerRaw(wav);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "audio/wav");
        request.timeout = 90;
        yield return request.SendWebRequest();
        activeTranscription = null;
        transcribing = false;
        if (request.result != UnityWebRequest.Result.Success) { ReportStatus(PetBackendHttpClient.ReadError(request)); yield break; }
        PetSpeechRequest response = null;
        try { response = JsonUtility.FromJson<PetSpeechRequest>(request.downloadHandler.text); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(response?.text)) { ReportStatus("未识别到语音内容。"); yield break; }
        ReportStatus("识别完成：" + response.text);
        if (client.CharacterId == characterId) client.SendChat(response.text);
    }

    private void ReportStatus(string value)
    {
        RecordingStatus = value;
        if (client != null) client.onServiceStatus.Invoke(value);
    }
    public static byte[] EncodeWav(float[] samples, int channels, int frequency)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)channels); writer.Write(frequency); writer.Write(frequency * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
        foreach (float sample in samples) writer.Write((short)(Mathf.Clamp(sample, -1, 1) * short.MaxValue));
        writer.Flush(); return stream.ToArray();
    }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        IntPtr previous = originalProc;
        try
        {
            if (!nativeShuttingDown)
            {
                var subscribers = NativeMessage;
                if (subscribers != null) foreach (Action<uint, IntPtr, IntPtr> subscriber in subscribers.GetInvocationList())
                    try { subscriber(message, wParam, lParam); } catch (Exception) { }
            }
            if (message == 0x0233)
            {
                try
                {
                    if (!nativeShuttingDown && DragQueryFileW(wParam, 0xFFFFFFFF, null, 0) > 0)
                    {
                        uint length = DragQueryFileW(wParam, 0, null, 0);
                        if (length > 0 && length <= 32768)
                        {
                            var file = new StringBuilder((int)length + 1);
                            DragQueryFileW(wParam, 0, file, length + 1); droppedFiles.Enqueue(file.ToString());
                        }
                    }
                }
                finally { DragFinish(wParam); }
                return IntPtr.Zero;
            }
            if (message == 0x0082 && previous != IntPtr.Zero)
            {
                nativeShuttingDown = true;
                SetWindowLongPtr(hwnd, -4, previous); nativeDetached = true; window = IntPtr.Zero;
            }
        }
        catch (Exception) { /* Never unwind a managed exception through Win32. */ }
        return previous != IntPtr.Zero ? CallWindowProcW(previous, hwnd, message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
    }
#endif
    private void OnDisable() { ReleaseCaptureResources(); }
    private void OnApplicationQuit() { DetachNativeWindow(); ReleaseCaptureResources(); }

    private void DetachNativeWindow()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        nativeShuttingDown = true;
        if (nativeDetached || window == IntPtr.Zero || originalProc == IntPtr.Zero) return;
        if (!IsWindow(window)) { nativeDetached = true; window = IntPtr.Zero; return; }
        // Remove the managed callback while the Mono domain and Unity window are
        // still alive. OnDestroy can run after native shutdown starts pumping
        // final window messages, which is too late to leave a managed WndProc.
        DragAcceptFiles(window, false);
        SetWindowLongPtr(window, -4, originalProc);
        nativeDetached = GetWindowLongPtr(window, -4) == originalProc;
        Debug.Log("XPET_WINDOW_HOOK_RESTORED " + nativeDetached);
        if (nativeDetached)
        {
            window = IntPtr.Zero;
        }
        // Keep the previous procedure and delegate alive for an in-flight call.
#endif
    }

    private void ReleaseCaptureResources()
    {
        CancelRecording(null);
        StopAllCoroutines();
        if (activeTranscription != null) { activeTranscription.Abort(); activeTranscription.Dispose(); }
        activeTranscription = null;
        transcribing = false;
    }

    private void OnDestroy()
    {
        DetachNativeWindow();
        ReleaseCaptureResources();
    }
}
