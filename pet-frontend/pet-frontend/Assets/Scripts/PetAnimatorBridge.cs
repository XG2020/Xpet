using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class AnimatorValueMapping
{
    public string backendValue;
    public string triggerName;
    public string stateName;
}

public class PetAnimatorBridge : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private PetBackendHttpClient backendClient;
    [SerializeField] private Animator targetAnimator;
    [SerializeField] private Transform animatorSearchRoot;
    private PetCharacterAnimation character;
    private PetImportedMotionPlayer importedMotion;
    private string interactionZone;
    private string interactionLegSide = "R";

    [Header("Auto Bind")]
    [SerializeField] private bool autoFindBackendClient = true;
    [SerializeField] private bool autoFindAnimatorInChildren = true;
    [SerializeField] private bool playDefaultActionOnStart = true;

    [Header("Playback")]
    [SerializeField] private float crossFadeDuration = 0.15f;
    [SerializeField] private int actionLayerIndex;

    [Header("Fallback")]
    [SerializeField] private string defaultActionValue = "idle";

    [Header("Action Mappings")]
    [SerializeField] private AnimatorValueMapping[] actionMappings;

    [Header("Emotion Mappings")]
    [SerializeField] private AnimatorValueMapping[] emotionMappings;

    private void Awake()
    {
        AutoBindReferences();
    }

    private void OnEnable()
    {
        if (backendClient != null)
        {
            backendClient.RegisterActionBridge(this);
            backendClient.onReplyAction.AddListener(HandleAction);
            backendClient.onReplyEmotion.AddListener(HandleEmotion);
            backendClient.onChatRequestStarted.AddListener(TrackInteraction);
            backendClient.onRequestCancelled.AddListener(ClearInteraction);
        }
        BindSpeechSource();
    }

    private void OnDisable()
    {
        if (backendClient != null)
        {
            backendClient.onReplyAction.RemoveListener(HandleAction);
            backendClient.onReplyEmotion.RemoveListener(HandleEmotion);
            backendClient.onChatRequestStarted.RemoveListener(TrackInteraction);
            backendClient.onRequestCancelled.RemoveListener(ClearInteraction);
        }
        ClearInteraction();
        if (character != null) character.SetSpeechSource(null);
    }

    private void Start()
    {
        // Awake ordering is not guaranteed across the client and the character.
        BindSpeechSource();
        if (playDefaultActionOnStart)
        {
            HandleAction(defaultActionValue);
        }
    }

    [ContextMenu("Auto Bind References")]
    public void AutoBindReferences()
    {
        Transform root = animatorSearchRoot != null ? animatorSearchRoot : transform;
        character = root.GetComponentInChildren<PetCharacterAnimation>(true);
        if (autoFindBackendClient && backendClient == null)
        {
            backendClient = FindAnyObjectByType<PetBackendHttpClient>();
        }

        if (autoFindAnimatorInChildren && targetAnimator == null)
        {
            Transform searchRoot = animatorSearchRoot != null ? animatorSearchRoot : transform;
            targetAnimator = searchRoot.GetComponentInChildren<Animator>(true);
        }
        if (character != null && character.PlaybackAnimator != null) targetAnimator = character.PlaybackAnimator;
        BindSpeechSource();
    }

    private void BindSpeechSource()
    {
        if (character != null) character.SetSpeechSource(backendClient != null ? backendClient.SpeechSource : null);
    }

    public void SetBackendClient(PetBackendHttpClient client)
    {
        if (backendClient == client)
        {
            return;
        }

        if (isActiveAndEnabled && backendClient != null)
        {
            backendClient.onReplyAction.RemoveListener(HandleAction);
            backendClient.onReplyEmotion.RemoveListener(HandleEmotion);
            backendClient.onChatRequestStarted.RemoveListener(TrackInteraction);
            backendClient.onRequestCancelled.RemoveListener(ClearInteraction);
        }

        backendClient = client;
        backendClient?.RegisterActionBridge(this);

        if (isActiveAndEnabled && backendClient != null)
        {
            backendClient.onReplyAction.AddListener(HandleAction);
            backendClient.onReplyEmotion.AddListener(HandleEmotion);
            backendClient.onChatRequestStarted.AddListener(TrackInteraction);
            backendClient.onRequestCancelled.AddListener(ClearInteraction);
        }
        BindSpeechSource();
    }

    public void SetTargetAnimator(Animator animator)
    {
        if (character != null) character.SetSpeechSource(null);
        targetAnimator = animator;
        character = animator != null ? animator.GetComponent<PetCharacterAnimation>() : null;
        BindSpeechSource();
    }

    /// <summary>Bind only the selected model; inactive library models must not receive replies.</summary>
    public void RebindModel(Transform model)
    {
        importedMotion?.StopImmediately(); importedMotion = null;
        ClearInteraction();
        if (character != null) character.SetSpeechSource(null);
        animatorSearchRoot = model;
        targetAnimator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
        character = model != null ? model.GetComponentInChildren<PetCharacterAnimation>(true) : null;
        if (character != null && character.PlaybackAnimator != null) targetAnimator = character.PlaybackAnimator;
        BindSpeechSource();
        if (character != null) character.PlayAction(defaultActionValue);
    }

    private void TrackInteraction(PetChatSubmission submission)
    {
        interactionZone = submission != null && submission.IsInteraction ? submission.interaction_zone : null;
        if (interactionZone == "leg") interactionLegSide = GetComponent<PetTouchFeedback>()?.LastLegSide ?? "R";
        if (interactionZone == null && character != null && !character.IsGuarding) character.PlayEmotion("thinking");
    }

    private void ClearInteraction() { interactionZone = null; character?.PlayEmotion("neutral"); }

    public void HandleAction(string action)
    {
        if (PetActionLibrary.IsRetiredBuiltIn(action)) return;
        var behavior = backendClient != null ? backendClient.GetComponent<PetBehaviorCoordinator>() : null;
        var origin = interactionZone != null ? PetActionOrigin.LocalInteraction : PetActionOrigin.AiReply;
        if (behavior != null && !behavior.TryStartAction(action, origin)) return;
        PlayCoordinatedAction(action);
    }

    public void PlayCoordinatedAction(string action)
    {
        if (PetActionLibrary.IsRetiredBuiltIn(action)) return;
        if (character != null && (character.IsHandLeading || character.IsHandFalling)) return;
        if (interactionZone == "leg" && (action == "leg_retract" || action == "leg_shift" || action == "leg_sway") && character != null)
        {
            importedMotion?.Stop();
            if (!character.IsTouchReacting) character.PlayLegTouch(action, interactionLegSide);
            return;
        }
        bool allowTrigger = backendClient == null || backendClient.CurrentSelectedActionId != action;
        if (TryImportedMotion(action, allowTrigger)) return;
        if (character != null)
        {
            if (string.Equals(action, "guard", StringComparison.OrdinalIgnoreCase)) character.PlayGuard(interactionZone);
            else if (allowTrigger) character.PlayAction(action);
            else character.PreviewAction(action);
            return;
        }
        string normalizedValue = NormalizeValue(action);
        if (!PetActionLibrary.IsEnabled(normalizedValue)) normalizedValue = "idle";
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            normalizedValue = NormalizeValue(defaultActionValue);
        }

        if (!ApplyMapping(actionMappings, normalizedValue) && !ApplyStandingGesture(normalizedValue))
        {
            Debug.LogWarning($"No Animator mapping configured for action '{normalizedValue}'.");
        }
    }

    public void HandleEmotion(string emotion)
    {
        if (character != null && character.IsHandLeading) return;
        if (character != null) { character.SetEmotion(emotion); return; }
        string normalizedValue = NormalizeValue(emotion);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return;
        }

        if (!ApplyMapping(emotionMappings, normalizedValue))
        {
            Debug.Log($"No Animator emotion mapping configured for '{normalizedValue}', skipped.");
        }
    }

    public void PlayAction(string action)
    {
        HandleAction(action);
    }

    public void PreviewAction(string action)
    {
        if (PetActionLibrary.IsRetiredBuiltIn(action)) return;
        var behavior = backendClient != null ? backendClient.GetComponent<PetBehaviorCoordinator>() : null;
        if (behavior != null && !behavior.TryStartAction(action, PetActionOrigin.ManualPreview)) return;
        if (TryImportedMotion(action, false)) return;
        if (character != null) character.PreviewAction(action);
        else HandleAction(action);
    }

    public Transform ActiveModelRoot => animatorSearchRoot != null ? animatorSearchRoot : character != null ? character.transform : targetAnimator != null ? targetAnimator.transform : null;
    public PetBackendHttpClient BackendClient => backendClient;
    public PetCharacterAnimation Character => character;
    public Animator PlaybackAnimator => character != null && character.PlaybackAnimator != null ? character.PlaybackAnimator : targetAnimator;
    public bool HasSword => ActiveModelRoot != null && ActiveModelRoot.GetComponent<PetWeaponAttachment>() != null && ActiveModelRoot.GetComponent<PetWeaponAttachment>().HasSword;
    public List<string> GetCallableActionIds()
    {
        var available = new List<string>();
        Transform model = ActiveModelRoot;
        Animator playback = PlaybackAnimator;
        if (model == null || !model.gameObject.activeInHierarchy || playback == null || playback.runtimeAnimatorController == null) return available;
        foreach (var entry in PetActionLibrary.Entries)
        {
            if (!entry.enabled || entry.id == "drag" || entry.id == "guard") continue;
            if (entry.builtIn)
            {
                string state = string.Concat(entry.id.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
                if (playback.HasState(0, Animator.StringToHash("Base Layer." + state))) available.Add(entry.id);
            }
            else if (entry.ai_decide)
            {
                if (PetActionLibrary.IsImported(entry))
                {
                    try { if (File.Exists(PetImportedMotionPlayer.ResolveFile(entry.motion_file))) available.Add(entry.id); } catch (Exception) { }
                }
                else if (entry.steps.Any(step => PetActionLibrary.IsEnabled(step.action))) available.Add(entry.id);
            }
        }
        return available;
    }

    private bool TryImportedMotion(string action, bool allowTrigger)
    {
        var entry = PetActionLibrary.ResolveCustom(action, allowTrigger);
        if (!PetActionLibrary.IsImported(entry)) { importedMotion?.Stop(); return false; }
        Transform model = animatorSearchRoot != null ? animatorSearchRoot : character != null ? character.transform : targetAnimator != null ? targetAnimator.transform : null;
        if (model == null) return false;
        if (importedMotion == null || importedMotion.transform != model)
        {
            importedMotion?.StopImmediately();
            importedMotion = model.GetComponent<PetImportedMotionPlayer>() ?? model.gameObject.AddComponent<PetImportedMotionPlayer>();
        }
        if (character != null && (character.IsDragging || character.IsTouchReacting || character.IsHandLeading)) return true;
        importedMotion.Play(entry); return true;
    }

    public void PlayEmotion(string emotion)
    {
        HandleEmotion(emotion);
    }

    public void PlayIdle()
    {
        HandleAction("idle");
    }

    public void PlayWave()
    {
        HandleAction("wave");
    }

    public void PlayHappy()
    {
        HandleAction("happy");
    }

    public void PlayThink()
    {
        HandleAction("think");
    }

    public void PlaySad()
    {
        HandleAction("sad");
    }

    public void PlaySurprised()
    {
        HandleAction("surprised");
    }

    public void PlaySpeak()
    {
        HandleAction("speak");
    }

    public void PlayNod() { HandleAction("nod"); }
    public void PlayTiltHead() { HandleAction("tilt_head"); }
    public void PlayShy() { HandleAction("shy"); }
    public void PlayStretch() { HandleAction("stretch"); }
    public void PlayCheer() { HandleAction("cheer"); }
    public void PlayLookAround() { HandleAction("look_around"); }
    public void PlayGuard() { HandleAction("guard"); }
    public void PlayJump() { HandleAction("jump"); }
    public void PlayRun() { HandleAction("run"); }
    // Compatibility targets for UnityEvents stored by older scenes.
    public void PlayHoldSword() { }
    public void PlayDrawSlash() { }
    public void PlayLegRetract() { HandleAction("leg_retract"); }
    public void PlayLegShift() { HandleAction("leg_shift"); }
    public void PlayLegSway() { HandleAction("leg_sway"); }

    public void SetNeutralEmotion()
    {
        HandleEmotion("neutral");
    }

    public void SetHappyEmotion()
    {
        HandleEmotion("happy");
    }

    public void SetCuriousEmotion()
    {
        HandleEmotion("curious");
    }

    public void SetThinkingEmotion()
    {
        HandleEmotion("thinking");
    }

    public void SetSadEmotion()
    {
        HandleEmotion("sad");
    }

    public void SetExcitedEmotion()
    {
        HandleEmotion("excited");
    }

    private bool ApplyMapping(AnimatorValueMapping[] mappings, string normalizedValue)
    {
        if (targetAnimator == null)
        {
            Debug.LogWarning("PetAnimatorBridge is missing Animator reference.");
            return false;
        }

        AnimatorValueMapping mapping = FindMapping(mappings, normalizedValue);
        if (mapping == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(mapping.triggerName))
        {
            ResetMappedTriggers(mappings);
            targetAnimator.SetTrigger(mapping.triggerName);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mapping.stateName))
        {
            targetAnimator.CrossFadeInFixedTime(mapping.stateName, crossFadeDuration, actionLayerIndex);
            return true;
        }

        return false;
    }

    private bool ApplyStandingGesture(string value)
    {
        if (targetAnimator == null || targetAnimator.runtimeAnimatorController == null
            || actionLayerIndex < 0 || actionLayerIndex >= targetAnimator.layerCount) return false;
        string state;
        switch (value)
        {
            case "nod": state = "Nod"; break;
            case "tilt_head": state = "TiltHead"; break;
            case "shy": state = "Shy"; break;
            case "stretch": state = "Stretch"; break;
            case "cheer": state = "Cheer"; break;
            case "look_around": state = "LookAround"; break;
            case "guard": state = "Guard"; break;
            case "jump": state = "Jump"; break;
            case "run": state = "Run"; break;
            case "leg_retract": state = "LegRetract"; break;
            case "leg_shift": state = "LegShift"; break;
            case "leg_sway": state = "LegSway"; break;
            case "angry_stomp": state = "AngryStomp"; break;
            default: return false;
        }
        int hash = Animator.StringToHash(targetAnimator.GetLayerName(actionLayerIndex) + "." + state);
        if (!targetAnimator.HasState(actionLayerIndex, hash)) return false;
        targetAnimator.CrossFadeInFixedTime(hash, crossFadeDuration, actionLayerIndex, 0);
        return true;
    }

    private AnimatorValueMapping FindMapping(AnimatorValueMapping[] mappings, string normalizedValue)
    {
        if (mappings == null)
        {
            return null;
        }

        return mappings.FirstOrDefault(
            mapping => mapping != null && NormalizeValue(mapping.backendValue) == normalizedValue
        );
    }

    private void ResetMappedTriggers(AnimatorValueMapping[] mappings)
    {
        if (mappings == null || targetAnimator == null)
        {
            return;
        }

        foreach (AnimatorValueMapping mapping in mappings)
        {
            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.triggerName))
            {
                targetAnimator.ResetTrigger(mapping.triggerName);
            }
        }
    }

    private string NormalizeValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }
}
