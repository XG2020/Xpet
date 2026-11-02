using System;
using UnityEngine;

/// <summary>One owner arbitrates direct input, local feedback, model actions and rest.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(50)]
public sealed class PetBehaviorCoordinator : MonoBehaviour
{
    public const string AutoSleepKey = "desktop_pet.auto_sleep";
    public const string SleepDelayKey = "desktop_pet.sleep_delay_seconds";
    private PetBackendHttpClient client;
    private PetCharacterCards cards;
    private PetModelLibrary models;
    private SettingsPanelController settings;
    private DesktopPetSystemController system;
    private DesktopPetInteraction microphone;
    private RectTransform chat;
    private PetCharacterAnimation character;
    private PetSleepPose sleepPose;
    private PetBehaviorStateMachine policy;
    private float interactionUntil, pendingSleepUntil = -1, nextDeferredAt;
    private bool localInteraction, localActionSeen;
    private string pendingAction, pendingCharacter, pendingModel;
    private float pendingActionUntil;
    private PetSleepPhase previousPhase;
    private float wakeEyeStart = 1;
    public PetBehaviorState State { get; private set; }
    public PetSleepPhase SleepPhase => policy?.SleepPhase ?? PetSleepPhase.None;
    public bool IsSleeping => SleepPhase != PetSleepPhase.None;
    public float SleepProgress => PhaseTime;
    public bool AutoSleep => PlayerPrefs.GetInt(AutoSleepKey, 1) == 1;
    public float SleepDelaySeconds => Mathf.Clamp(PlayerPrefs.GetFloat(SleepDelayKey, 300), 30, 7200);
    private bool HasImportedMotion => client?.ActionBridge?.ActiveModelRoot?.GetComponent<PetImportedMotionPlayer>()?.IsPlaying == true;
    public bool CanRunAutonomous => Ready && !IsSleeping && pendingSleepUntil < 0 && character.IsResting && !HasImportedMotion
        && !localInteraction && !client.IsBusy && !client.IsSpeaking && !client.IsSpeechPending
        && (system == null || system.IsVisible) && (settings == null || !settings.IsOpen)
        && (microphone == null || !microphone.IsRecording && !microphone.IsTranscribing)
        && (chat == null || !chat.gameObject.activeInHierarchy);
    private bool Ready => character != null && character.gameObject.activeInHierarchy && cards != null && cards.Ready
        && !cards.IsBusy && models != null && !models.IsBusy && client != null && !client.CharacterSwitching;

    public static PetBehaviorCoordinator EnsureOn(GameObject services) => services.GetComponent<PetBehaviorCoordinator>()
        ?? services.AddComponent<PetBehaviorCoordinator>();
    private void Awake()
    {
        policy = new PetBehaviorStateMachine(Time.unscaledTime);
        client = GetComponent<PetBackendHttpClient>(); cards = GetComponent<PetCharacterCards>(); models = GetComponent<PetModelLibrary>();
        settings = GetComponent<SettingsPanelController>(); system = GetComponent<DesktopPetSystemController>(); microphone = GetComponent<DesktopPetInteraction>();
        chat = GameObject.Find("DesktopPetCanvas")?.transform.Find("ChatPanel") as RectTransform;
    }
    private void OnEnable()
    {
        if (client != null) client.onRequestCancelled.AddListener(CancelPendingActions);
    }
    private void OnDisable()
    {
        if (client != null) client.onRequestCancelled.RemoveListener(CancelPendingActions);
        ReleaseSleepPose();
    }
    private void BindCharacter()
    {
        var next = client?.ActionBridge?.Character;
        if (next == character) return;
        ReleaseSleepPose(); policy.Wake(Time.unscaledTime, true); character = next;
        pendingAction = null; localInteraction = false; pendingSleepUntil = -1;
    }
    private void Update()
    {
        BindCharacter();
        if (character == null) return;
        float now = Time.unscaledTime;
        bool direct = character.IsDragging || character.IsHandLeading || character.IsHandFalling;
        if (direct) { NoteActivity(true); localInteraction = false; }
        if (localInteraction) {
            localActionSeen |= !character.IsResting;
            if ((localActionSeen && character.IsResting && now >= interactionUntil) || now >= interactionUntil + 6) localInteraction = false;
        }
        if (system != null && !system.IsVisible) { CancelPendingActions(); State = PetBehaviorState.Hidden; return; }
        if (IsSleeping && (!Ready || !character.IsResting || HasImportedMotion || client.IsBusy || client.IsSpeaking || microphone != null && microphone.IsRecording)) Wake(true);
        policy.Tick(now, PetSleepPose.EnterDuration, PetSleepPose.WakeDuration);
        if (previousPhase != SleepPhase && SleepPhase == PetSleepPhase.None) ReleaseSleepPose();
        previousPhase = SleepPhase;
        if (pendingSleepUntil >= 0 && now >= pendingSleepUntil && Ready && character.IsResting && !localInteraction
            && !client.IsBusy && !client.IsSpeaking && !client.IsSpeechPending) {
            pendingSleepUntil = -1; EnterSleep();
        }
        if (policy.ShouldSleep(now, SleepDelaySeconds, AutoSleep, CanRunAutonomous)) EnterSleep();
        character.SetSleepPresentation(IsSleeping, PetSleepPose.ClosedEyeWeight(PoseState, PhaseTime) * (SleepPhase == PetSleepPhase.Waking ? wakeEyeStart : 1),
            PetSleepPose.YawnWeight(PoseState, PhaseTime));
        if (pendingAction != null && now >= nextDeferredAt && !direct && !character.IsGuarding && !character.IsTouchReacting && !localInteraction) {
            string action = pendingAction; pendingAction = null;
            if (Ready && now <= pendingActionUntil && client.CharacterId == pendingCharacter && models.SelectedModelId == pendingModel
                && client.ActionBridge.GetCallableActionIds().Contains(action)) client.ActionBridge.PlayCoordinatedAction(action);
        }
        State = policy.Resolve(system == null || system.IsVisible, direct, localInteraction || character.IsGuarding || character.IsTouchReacting,
            character.IsWalking, !character.IsResting || HasImportedMotion);
    }
    private void LateUpdate()
    {
        if (sleepPose != null && IsSleeping && character != null && !character.IsDragging && !character.IsHandLeading && !character.IsHandFalling) {
            var viewer = Camera.main;
            if (viewer != null) sleepPose.SetViewerDirection(-viewer.transform.forward);
            sleepPose.Apply(PoseState, PhaseTime);
        }
    }
    private string PoseState => SleepPhase == PetSleepPhase.Entering ? "SleepEnter" : SleepPhase == PetSleepPhase.Sleeping ? "SleepLoop"
        : SleepPhase == PetSleepPhase.Waking ? "WakeUp" : "";
    private float PhaseTime => policy == null ? 0 : SleepPhase == PetSleepPhase.Sleeping
        ? Mathf.Repeat((float)(Time.unscaledTime - policy.PhaseStartedAt), PetSleepPose.LoopDuration) / PetSleepPose.LoopDuration
        : (float)(Time.unscaledTime - policy.PhaseStartedAt) / Mathf.Max(.01f, PetSleepPose.Duration(PoseState));
    public void NoteActivity(bool immediate = false)
    {
        if (policy == null) return;
        policy.Activity(Time.unscaledTime); pendingSleepUntil = -1;
        if (IsSleeping) Wake(immediate);
    }
    public void Wake(bool immediate = false)
    {
        if (policy == null) return;
        if (!immediate && (SleepPhase == PetSleepPhase.Entering || SleepPhase == PetSleepPhase.Sleeping)) {
            wakeEyeStart = PetSleepPose.ClosedEyeWeight(PoseState, PhaseTime);
            sleepPose?.PrepareWake();
        }
        pendingSleepUntil = -1; policy.Wake(Time.unscaledTime, immediate);
        if (immediate) ReleaseSleepPose();
    }
    public bool ConsumeWakeTap()
    {
        if (SleepPhase == PetSleepPhase.Waking) { Wake(true); return false; }
        if (!IsSleeping && pendingSleepUntil < 0) { NoteActivity(); return false; }
        Wake(false); return true;
    }
    public void NotifyLocalInteraction()
    {
        NoteActivity(true); localInteraction = true; localActionSeen = false; interactionUntil = Time.unscaledTime + .4f;
    }
    public bool TryStartAction(string action, PetActionOrigin origin)
    {
        BindCharacter();
        if (character == null) return false;
        NoteActivity(true);
        if (action == "idle" && origin == PetActionOrigin.AiReply && client.CurrentSubmission == null && !client.IsBusy
            && (character.IsDragging || character.IsHandLeading || character.IsHandFalling)) return false;
        if (PetBehaviorStateMachine.MustDeferReply(character.IsDragging, character.IsHandLeading, character.IsHandFalling,
            character.IsGuarding || character.IsTouchReacting, localInteraction) && origin == PetActionOrigin.AiReply) {
            pendingAction = action; pendingCharacter = client.CharacterId; pendingModel = models?.SelectedModelId;
            pendingActionUntil = Time.unscaledTime + 12; nextDeferredAt = Time.unscaledTime + .3f;
            return false;
        }
        if (character.IsDragging || character.IsHandLeading || character.IsHandFalling) return false;
        if (origin == PetActionOrigin.ManualPreview) { pendingAction = null; localInteraction = false; }
        return true;
    }
    public bool RequestSleep()
    {
        BindCharacter();
        if (SleepPhase == PetSleepPhase.Entering || SleepPhase == PetSleepPhase.Sleeping) return true;
        if (SleepPhase == PetSleepPhase.Waking) Wake(true);
        if (!Ready || client.IsBusy || client.IsSpeaking || client.IsSpeechPending || character.IsDragging || character.IsHandLeading
            || character.IsHandFalling || character.IsGuarding || character.IsTouchReacting) return false;
        pendingAction = null; localInteraction = false;
        client.ActionBridge?.ActiveModelRoot?.GetComponent<PetImportedMotionPlayer>()?.StopImmediately();
        character.PreviewAction("idle");
        pendingSleepUntil = Time.unscaledTime + .35f;
        policy.Activity(Time.unscaledTime);
        return true;
    }
    private void EnterSleep()
    {
        character.GetComponent<PetMouseGaze>()?.ResetOffsets();
        sleepPose = PetSleepPose.Capture(character.transform);
        if (sleepPose == null || !sleepPose.IsValid) { sleepPose = null; policy.Activity(Time.unscaledTime); return; }
        var viewer = Camera.main;
        if (viewer != null) sleepPose.SetViewerDirection(-viewer.transform.forward);
        character.PlayEmotion("neutral"); policy.BeginSleep(Time.unscaledTime); previousPhase = SleepPhase;
        character.SetSleepPresentation(true, 0);
    }
    private void ReleaseSleepPose()
    {
        sleepPose?.Restore(); sleepPose = null;
        if (character != null) character.SetSleepPresentation(false, 0);
    }
    public void CancelPendingActions() { pendingAction = null; localInteraction = false; Wake(true); }
    public void SetAutoSleep(bool value)
    {
        PlayerPrefs.SetInt(AutoSleepKey, value ? 1 : 0); PlayerPrefs.Save();
        NoteActivity(true);
    }
    public void SetSleepDelay(float seconds)
    {
        PlayerPrefs.SetFloat(SleepDelayKey, Mathf.Clamp(seconds, 30, 7200)); PlayerPrefs.Save(); NoteActivity();
    }
    public bool TryHandleCommand(string message, out string reply)
    {
        string command = (message ?? "").Trim().Trim('。', '！', '!', '？', '?').Replace(" ", "");
        if (command == "睡觉" || command == "去睡觉" || command == "睡一会" || command == "休息一下") {
            reply = RequestSleep() ? "我先安静地歇一会儿，点我就能醒来。" : "等当前动作或对话结束，再让我休息吧。";
            return true;
        }
        if (command == "醒醒" || command == "起床" || command == "醒来" || command == "唤醒") {
            Wake(false); reply = "嗯，我醒啦。"; return true;
        }
        reply = null; NoteActivity(false); return false;
    }
}
