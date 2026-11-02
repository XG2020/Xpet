using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Plays the generated MMD-bone animation clips and adds subtle face animation.</summary>
[DisallowMultipleComponent]
public sealed class PetCharacterAnimation : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private SkinnedMeshRenderer[] faceRenderers;
    [SerializeField] private float crossFadeSeconds = 0.18f;
    [SerializeField, Range(0, 20)] private float maximumDragLean = 10f;
    [SerializeField, Range(1, 10)] private float walkSeconds = 4f;
    [SerializeField, Range(35, 90)] private float walkFacingAngle = 68f;
    private Transform visualModel;
    private PetSecondaryMotion secondaryMotion;
    private Quaternion visualRestRotation;
    private bool dragging;
    private float dragLean;
    private float dragLeanVelocity;
    private float dragLeanTarget;
    private string deferredState;
    private float deferredStateAt;
    private float speakingUntil;
    private AudioSource speechSource;
    private bool speechSourceAssigned;
    private readonly float[] speechSamples = new float[256];
    private float speechMouthWeight;
    private float guardingUntil;
    private float touchReactionUntil;
    private float walkingUntil;
    private float walkingDirection = 1f;
    private float facingAngle;
    private float facingVelocity;
    private readonly List<FaceChannel> mouthChannels = new List<FaceChannel>();
    private PetFacialExpression facialExpression;
    private Coroutine customActionRoutine;
    private string customActionId;
    private float customActionSpeed = 1;
    private bool playingCustomStep;
    private string currentAction = "idle";
    public string CurrentAction => currentAction;
    public Animator PlaybackAnimator => animator != null ? animator : GetComponent<Animator>();
    public bool IsSleeping { get; private set; }
    private float sleepEyeClosure;
    private float sleepYawn;
    public bool IsResting => animator != null && animator.runtimeAnimatorController != null && !dragging && !IsHandLeading
        && !IsHandFalling && !IsWalking && !IsTouchReacting && customActionRoutine == null
        && GetComponent<PetImportedMotionPlayer>()?.IsPlaying != true
        && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle") && !animator.IsInTransition(0);
    public void SetSleepPresentation(bool sleeping, float eyes, float yawn = 0)
    { IsSleeping = sleeping; sleepEyeClosure = sleeping ? Mathf.Clamp01(eyes) : 0; sleepYawn = sleeping ? Mathf.Clamp01(yawn) : 0; }
    private string handLeadSide = "R";
    private float handLeadPace = 1;
    private readonly PetFastHandReaction fastHandReaction = new PetFastHandReaction();
    private float handLeadSpeed, handLeadInputAt;
    private bool stompActive;
    private bool stompSeen;
    private struct FaceChannel { public SkinnedMeshRenderer renderer; public int index; }

    public bool IsWalking { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsHandLeading { get; private set; }
    public bool IsHandFalling { get; private set; }
    public bool HandLeadAirborne { get; private set; }
    public Vector2 HandLeadPointer { get; private set; }
    public float HandLeadTension { get; private set; }
    public bool HandLeadHasPointer { get; private set; }
    public string HandLeadSide => handLeadSide;
    public PetFastHandReaction.Pose HandFastReactionPose => fastHandReaction.CurrentPose;
    public bool HandFastReactionTriggered => fastHandReaction.TriggeredThisLead;
    public float DesktopTravelSpeed => IsRunning ? 125f : 55f;
    public float WalkingDirection { get { return walkingDirection; } }
    public bool IsDragging { get { return dragging; } }
    public bool IsGuarding { get { return !dragging && Time.unscaledTime < guardingUntil; } }
    public bool IsTouchReacting => !dragging && (IsHandFalling || Time.unscaledTime < touchReactionUntil);
    public PetFacialExpression FacialExpression => facialExpression;
    public Quaternion VisualRestRotation => visualRestRotation;

    public void Configure(Animator target, SkinnedMeshRenderer[] renderers)
    {
        animator = target;
        faceRenderers = renderers;
    }

    private void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        secondaryMotion = GetComponent<PetSecondaryMotion>();
        visualModel = transform.Find("VisualModel");
        if (visualModel != null) visualRestRotation = visualModel.localRotation;
        if (faceRenderers == null || faceRenderers.Length == 0) faceRenderers = GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (SkinnedMeshRenderer skin in faceRenderers)
        {
            if (skin == null || skin.sharedMesh == null) continue;
            for (int index = 0; index < skin.sharedMesh.blendShapeCount; index++)
            {
                string name = skin.sharedMesh.GetBlendShapeName(index);
                // FBX exporters can prefix each shape with the mesh's name.
                string shortName = name.Substring(name.LastIndexOf('.') + 1);
                var channel = new FaceChannel { renderer = skin, index = index };
                string normalized = shortName.Replace("_", "").Replace("-", "").ToLowerInvariant();
                if (shortName == "あ" || normalized == "aa" || normalized == "a"
                    || normalized == "visemeaa" || normalized == "mouthopen" || normalized == "jawopen" || normalized == "moutha"
                    || shortName.EndsWith("_Mouth_00_A", StringComparison.OrdinalIgnoreCase)) mouthChannels.Add(channel);
            }
        }
        facialExpression = new PetFacialExpression(faceRenderers);
        // Existing model bundles gain weapon lifetime/hand mounts without
        // rewriting their source meshes or animation bindings.
        if (GetComponent<PetWeaponAttachment>() == null) gameObject.AddComponent<PetWeaponAttachment>();
        if (GetComponent<PetHandLeadArm>() == null) gameObject.AddComponent<PetHandLeadArm>();
    }

    private void OnDisable()
    {
        IsSleeping = false; sleepEyeClosure = sleepYawn = 0;
        CancelCustomAction();
        facialExpression?.Reset(); Set(mouthChannels, 0);
        dragging = false;
        IsHandLeading = false; handLeadPace = 1; stompActive = false;
        fastHandReaction.Cancel(); handLeadSpeed = 0;
        IsHandFalling = HandLeadAirborne = HandLeadHasPointer = false;
        IsWalking = false;
        IsRunning = false;
        walkingUntil = 0;
        guardingUntil = 0;
        touchReactionUntil = 0;
        speakingUntil = 0;
        speechMouthWeight = 0;
        facingAngle = facingVelocity = 0;
        dragLean = dragLeanVelocity = dragLeanTarget = 0;
        deferredState = null;
        if (visualModel != null) visualModel.localRotation = visualRestRotation;
    }

    private void Update()
    {
        float now = Time.unscaledTime;
        if (customActionId != null && !PetActionLibrary.IsEnabled(customActionId))
        {
            CancelCustomAction(); guardingUntil = touchReactionUntil = 0; StopWalking(); CrossFade("Idle");
        }
        if (currentAction != "idle" && !PetActionLibrary.IsEnabled(currentAction))
        {
            if (IsHandLeading) EndHandLead();
            guardingUntil = touchReactionUntil = 0; IsWalking = false; IsRunning = false; walkingUntil = 0; deferredState = null;
            CrossFade("Idle");
        }
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            bool resting = animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle") && !animator.IsInTransition(0);
            if (stompActive)
            {
                if (animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.AngryStomp")
                    || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName("Base Layer.AngryStomp"))) stompSeen = true;
                else if (stompSeen && resting) { stompActive = false; facialExpression?.SetEmotion("neutral", now); }
            }
            if (resting && !PetActionLibrary.IsEnabled("idle"))
            {
                animator.Play("Base Layer.Idle", 0, 0);
                animator.speed = 0;
            }
            else animator.speed = IsHandLeading ? handLeadPace : customActionId != null ? customActionSpeed : 1;
            GetComponent<PetRuntimePoseRetargeter>()?.SetPlaybackSpeed(animator.speed);
        }
        if (IsWalking && !IsHandLeading && now >= walkingUntil) StopWalking();
        UpdateSpeechMouth(now);
        if (!dragging && deferredState != null && now >= deferredStateAt)
        {
            StartState(deferredState);
            deferredState = null;
        }
    }

    private void LateUpdate()
    {
        // Both pointer controllers supply the same speed. Keep the short overlay's
        // clock independent of Animator pace and require fresh grounded input.
        fastHandReaction.Step(Time.unscaledTime - handLeadInputAt <= .15f ? handLeadSpeed : 0,
            walkingDirection, IsRunning, IsHandLeading && HandLeadHasPointer && !HandLeadAirborne, Time.unscaledTime);
        // Animator sampling has finished; facial morphs and speech now share a single
        // final write so neither clips nor blink updates erase the current emotion.
        facialExpression?.Tick(Time.unscaledTime, Time.unscaledDeltaTime, speechMouthWeight, IsGuarding, sleepEyeClosure, sleepYawn);
        Set(mouthChannels, Mathf.Max(speechMouthWeight, sleepYawn * 22));
        if (visualModel == null) return;
        dragLean = Mathf.SmoothDampAngle(dragLean, dragging && PetActionLibrary.IsEnabled("drag") ? dragLeanTarget : 0,
            ref dragLeanVelocity, dragging ? 0.12f : 0.22f, Mathf.Infinity, Time.unscaledDeltaTime);
        facingAngle = Mathf.SmoothDampAngle(facingAngle, IsWalking ? -walkingDirection * walkFacingAngle : 0,
            ref facingVelocity, 0.2f, Mathf.Infinity, Time.unscaledDeltaTime);
        // The generated clips animate bones, leaving this visual-only lean independent
        // of the window position, hitbox, and the user's character-root transform.
        visualModel.localRotation = Quaternion.AngleAxis(dragLean, Vector3.forward)
            * Quaternion.AngleAxis(facingAngle, Vector3.up) * visualRestRotation;
    }

    /// <summary>Call every drag frame with horizontal pointer movement in screen pixels,
    /// then once with false on release. Positive movement is toward screen right.</summary>
    public void SetDragging(bool isDragging, float horizontalDelta)
    {
        if (isDragging) IsHandFalling = false;
        if (isDragging && IsHandLeading) EndHandLead();
        if (float.IsNaN(horizontalDelta) || float.IsInfinity(horizontalDelta)) horizontalDelta = 0;
        float pixelsPerSecond = horizontalDelta / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        dragLeanTarget = Mathf.Clamp(-pixelsPerSecond * 0.014f, -maximumDragLean, maximumDragLean);
        if (secondaryMotion != null) secondaryMotion.SetDesktopMotion(isDragging, Mathf.Clamp(pixelsPerSecond, -1800, 1800));
        if (dragging == isDragging) return;
        dragging = isDragging;
        if (dragging)
        {
            CancelCustomAction();
            guardingUntil = 0;
            touchReactionUntil = 0;
            IsWalking = false;
            IsRunning = false;
            walkingUntil = 0;
            if (deferredState == "Walk" || deferredState == "Run" || deferredState == "Jump" || deferredState == "Guard" || deferredState?.StartsWith("Leg", StringComparison.Ordinal) == true) deferredState = null;
            CrossFade("Drag");
        }
        else
        {
            dragLeanTarget = 0;
            CrossFade("Idle");
            // An AI response can arrive while the user is carrying the pet. Keep its
            // latest gesture, but allow the release to blend through Idle first.
            deferredStateAt = Time.unscaledTime + Mathf.Max(0.3f, crossFadeSeconds);
        }
    }

    public void PlayAction(string action) { PlayRequestedAction(action, true); }

    /// <summary>Preview the selected library entry without its automatic response replacement.</summary>
    public void PreviewAction(string action) { PlayRequestedAction(action, false); }

    private void PlayRequestedAction(string action, bool allowTrigger)
    {
        if (IsHandLeading || IsHandFalling) return;
        if (animator == null || animator.runtimeAnimatorController == null) return;
        string normalized = string.IsNullOrWhiteSpace(action) ? "idle" : action.Trim().ToLowerInvariant();
        if (PetActionLibrary.IsRetiredBuiltIn(normalized)) return;
        // A quick model/voice response must not restart the local protective or
        // leg response while its continuous entry/release is still in progress.
        if (allowTrigger && IsTouchReacting) return;
        if (!playingCustomStep)
        {
            CancelCustomAction();
            bool legResponse = normalized == "leg_retract" || normalized == "leg_shift" || normalized == "leg_sway";
            var custom = legResponse ? null : PetActionLibrary.ResolveCustom(normalized, allowTrigger);
            if (custom != null && !dragging && !IsGuarding)
            {
                customActionId = custom.id;
                customActionSpeed = custom.speed;
                customActionRoutine = StartCoroutine(PlayCustomAction(custom));
                return;
            }
        }
        if (!PetActionLibrary.IsEnabled(normalized)) normalized = "idle";
        string state;
        switch (normalized)
        {
            case "wave": state = "Wave"; break;
            case "happy": state = "Happy"; break;
            case "think": state = "Think"; break;
            case "sad": state = "Sad"; break;
            case "surprised": state = "Surprised"; break;
            case "speak": state = "Speak"; break;
            case "walk": state = "Walk"; break;
            case "run": state = "Run"; break;
            case "jump": state = "Jump"; break;
            case "leg_retract": state = "LegRetract"; break;
            case "leg_shift": state = "LegShift"; break;
            case "leg_sway": state = "LegSway"; break;
            case "angry_stomp": state = "AngryStomp"; break;
            case "nod": state = "Nod"; break;
            case "tilt_head": state = "TiltHead"; break;
            case "shy": state = "Shy"; break;
            case "stretch": state = "Stretch"; break;
            case "cheer": state = "Cheer"; break;
            case "look_around": state = "LookAround"; break;
            case "guard": state = "Guard"; break;
            default: state = "Idle"; break;
        }
        if (dragging)
        {
            if (state == "Guard") return;
            deferredState = state == "Idle" ? null : state;
            return;
        }
        // Let the short boundary reaction finish, including when a text/voice
        // response arrives immediately after the local touch. Do not restart it.
        if (IsGuarding) return;
        deferredState = null;
        StartState(state);
    }

    private void StartState(string state)
    {
        if (!PetActionLibrary.IsEnabled(ActionForState(state))) state = "Idle";
        float guardDuration = 3f;
        bool guard = state == "Guard" || state == "GuardLower";
        bool touch = guard || state.StartsWith("Leg", StringComparison.Ordinal);
        stompActive = state == "AngryStomp";
        stompSeen = false;
        if (touch && animator != null && animator.runtimeAnimatorController != null)
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                if (clip != null && clip.name == state) { guardDuration = Mathf.Max(guardDuration, clip.length); break; }
        guardingUntil = guard && HasState(state) ? Time.unscaledTime + guardDuration : 0;
        touchReactionUntil = touch && HasState(state) ? Time.unscaledTime + guardDuration + crossFadeSeconds : 0;
        facialExpression?.SetAction(state, Time.unscaledTime);
        IsWalking = (state == "Walk" || state == "Run") && HasState(state);
        IsRunning = IsWalking && state == "Run";
        walkingUntil = IsWalking ? Time.unscaledTime + (IsRunning ? 3f : walkSeconds) : 0;
        CrossFade(IsWalking ? state : state == "Walk" || state == "Run" ? "Idle" : state);
    }

    /// <summary>Choose a screen direction without restarting the current step or its timer.</summary>
    public void WalkDirection(float direction)
    {
        if (!float.IsNaN(direction) && !float.IsInfinity(direction) && Mathf.Abs(direction) > 0.001f)
            walkingDirection = direction < 0 ? -1f : 1f;
    }

    /// <summary>Stop native window travel and blend back to the standing pose.</summary>
    public void StopWalking()
    {
        if (IsHandLeading) { EndHandLead(); return; }
        bool wasWalking = IsWalking;
        IsWalking = false;
        IsRunning = false;
        walkingUntil = 0;
        if (deferredState == "Walk" || deferredState == "Run") deferredState = null;
        if (wasWalking && !dragging) CrossFade("Idle");
    }

    private bool HasState(string state)
    {
        return animator != null && animator.runtimeAnimatorController != null
            && animator.HasState(0, Animator.StringToHash("Base Layer." + state));
    }

    private void CrossFade(string state)
    {
        if (animator == null || animator.runtimeAnimatorController == null) return;
        if (!PetActionLibrary.IsEnabled(ActionForState(state))) state = "Idle";
        currentAction = ActionForState(state);
        int hash = Animator.StringToHash("Base Layer." + state);
        if (!animator.HasState(0, hash)) return;
        animator.speed = customActionId != null ? customActionSpeed : 1;
        GetComponent<PetRuntimePoseRetargeter>()?.CrossFade(state, crossFadeSeconds, animator.speed);
        if (state == "Idle" && !PetActionLibrary.IsEnabled("idle"))
        {
            animator.Play(hash, 0, 0);
            animator.Update(0);
            animator.speed = 0;
        }
        else
        {
            // Repeated speech/action updates should not rewind the same gesture.
            var current = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            bool continuous = state == "Idle" || state == "Speak" || state == "Walk" || state == "Run" || state == "Drag" || state == "Guard" || state == "GuardLower" || state.StartsWith("Leg", StringComparison.Ordinal) || state.StartsWith("HandLead", StringComparison.Ordinal);
            if (!continuous || !current.IsName("Base Layer." + state) && (!animator.IsInTransition(0) || !next.IsName("Base Layer." + state)))
                animator.CrossFadeInFixedTime(hash, crossFadeSeconds, 0, 0);
        }
    }

    private static string ActionForState(string state) => state.StartsWith("HandLead", StringComparison.Ordinal) ? (state.Contains("Run") ? "run" : "walk")
        : state.StartsWith("LegRetract", StringComparison.Ordinal) ? "leg_retract" : state.StartsWith("LegShift", StringComparison.Ordinal) ? "leg_shift"
        : state.StartsWith("LegSway", StringComparison.Ordinal) ? "leg_sway" : state == "AngryStomp" ? "angry_stomp"
        : state == "GuardLower" ? "guard" : state == "TiltHead" ? "tilt_head" : state == "LookAround" ? "look_around" : state == "HoldSword" ? "hold_sword" : state == "DrawSlash" ? "draw_slash" : state.ToLowerInvariant();

    public void PlayLegTouch(string action, string side)
    {
        if (dragging || IsHandLeading || IsHandFalling || IsGuarding) return;
        string state = action == "leg_retract" ? "LegRetract" : action == "leg_shift" ? "LegShift" : "LegSway";
        if (side == "L") state += "Left";
        // Do not silently lift the other leg if an old model lacks this variant.
        if (!HasState(state)) return;
        CancelCustomAction(); GetComponent<PetImportedMotionPlayer>()?.StopImmediately();
        deferredState = null; StartState(state);
    }

    public void BeginHandLead(string side)
    {
        string chosen = side == "L" ? "L" : "R";
        if (!HasState("HandLeadIdle" + (chosen == "L" ? "Left" : "Right"))) return;
        CancelCustomAction(); GetComponent<PetImportedMotionPlayer>()?.StopImmediately();
        dragging = false; guardingUntil = touchReactionUntil = 0; deferredState = null;
        handLeadSide = chosen; IsHandLeading = true;
        fastHandReaction.Begin(Time.unscaledTime); handLeadSpeed = 0;
        IsHandFalling = HandLeadAirborne = HandLeadHasPointer = false;
        facialExpression?.SetEmotion("neutral", Time.unscaledTime);
        UpdateHandLead(walkingDirection, 0, false);
    }

    public void UpdateHandLead(float direction, float speed, bool running)
    {
        if (!IsHandLeading) return;
        if (float.IsNaN(speed) || float.IsInfinity(speed)) speed = 0;
        speed = Mathf.Clamp(speed, 0, 700);
        handLeadSpeed = speed; handLeadInputAt = Time.unscaledTime;
        bool moving = speed > 8 && !HandLeadAirborne;
        IsWalking = moving; IsRunning = moving && running;
        if (moving) WalkDirection(direction);
        walkingUntil = float.PositiveInfinity;
        handLeadPace = moving ? Mathf.Clamp(speed / (IsRunning ? 280f : 110f), .65f, 1.65f) : 1;
        string state = "HandLead" + (HandLeadAirborne ? "Air" : moving ? (IsRunning ? "Run" : "Walk") : "Idle") + (handLeadSide == "L" ? "Left" : "Right");
        if (!HasState(state) || !PetActionLibrary.IsEnabled(ActionForState(state))) { EndHandLead(); return; }
        CrossFade(state);
        secondaryMotion?.SetDesktopMotion(moving, direction * speed);
    }

    public void EndHandLead()
    {
        fastHandReaction.Cancel(); handLeadSpeed = 0;
        if (!IsHandLeading) return;
        IsHandLeading = false; IsWalking = IsRunning = false; handLeadPace = 1;
        HandLeadHasPointer = HandLeadAirborne = false;
        walkingUntil = 0; deferredState = null; dragLeanTarget = 0;
        secondaryMotion?.SetDesktopMotion(false, 0);
        CrossFade("Idle");
    }

    public void SetHandLeadPointer(Vector2 pointer, bool airborne, float tension)
    {
        if (!IsHandLeading || float.IsNaN(pointer.x+pointer.y) || float.IsInfinity(pointer.x+pointer.y)) return;
        HandLeadPointer=pointer;HandLeadAirborne=airborne;HandLeadTension=Mathf.Clamp01(tension);HandLeadHasPointer=true;
        if (airborne) fastHandReaction.CancelReaction();
    }
    /// <summary>Used when the visible rig changes; an old hand reaction must not carry over.</summary>
    public void CancelFastHandReaction() => fastHandReaction.Cancel();
    public void BeginHandFall()
    {
        fastHandReaction.Cancel(); handLeadSpeed = 0;
        IsHandLeading=false;HandLeadHasPointer=HandLeadAirborne=false;IsHandFalling=true;
        IsWalking=IsRunning=false;handLeadPace=1;walkingUntil=guardingUntil=touchReactionUntil=0;deferredState=null;
        GetComponent<PetImportedMotionPlayer>()?.StopImmediately();CrossFade("Drag");
    }
    public void CancelHandFall() { if(IsHandFalling) {IsHandFalling=false;CrossFade("Idle");} }
    public void PlayHandLanding()
    {
        fastHandReaction.Cancel(); handLeadSpeed = 0;
        IsHandFalling=false;IsHandLeading=false;HandLeadHasPointer=HandLeadAirborne=false;
        StartState(HasState("HandLeadLand") ? "HandLeadLand" : "Idle");
        touchReactionUntil=Time.unscaledTime+1.15f;
    }

    private IEnumerator PlayCustomAction(PetActionEntry entry)
    {
        // Start on the next frame so cancellation always owns the coroutine handle.
        yield return null;
        foreach (var step in entry.steps)
        {
            if (!PetActionLibrary.IsEnabled(entry.id)) break;
            if (!PetActionLibrary.IsEnabled(step.action)) continue;
            playingCustomStep = true;
            PlayAction(step.action);
            playingCustomStep = false;
            float until = Time.unscaledTime + step.seconds;
            while (Time.unscaledTime < until && PetActionLibrary.IsEnabled(entry.id) && PetActionLibrary.IsEnabled(step.action)) yield return null;
        }
        customActionRoutine = null; customActionId = null; customActionSpeed = 1;
        guardingUntil = 0; StopWalking(); CrossFade("Idle");
    }

    private void CancelCustomAction()
    {
        if (customActionRoutine != null) StopCoroutine(customActionRoutine);
        customActionRoutine = null; customActionId = null; customActionSpeed = 1; playingCustomStep = false;
        if (animator != null) animator.speed = 1;
    }

    public void PlayEmotion(string emotion)
    {
        facialExpression?.SetEmotion(IsGuarding ? "angry" : emotion, Time.unscaledTime);
    }

    public void SetEmotion(string emotion) { PlayEmotion(emotion); }
    public void SpeakText(string text) { speakingUntil = Time.unscaledTime + Mathf.Clamp((text ?? "").Length * 0.12f, 1.5f, 12f); }
    public void SetSpeechSource(AudioSource source)
    {
        speechSource = source;
        speechSourceAssigned = source != null;
        speakingUntil = 0;
        speechMouthWeight = 0;
        Set(mouthChannels, 0);
    }

    private void UpdateSpeechMouth(float now)
    {
        if (!speechSourceAssigned)
        {
            speechMouthWeight = now < speakingUntil ? (0.25f + Mathf.Abs(Mathf.Sin(now * 13))) * 28 : 0;
            return;
        }
        if (speechSource == null || !speechSource.isPlaying)
        {
            speechMouthWeight = 0;
            return;
        }
        speechSource.GetOutputData(speechSamples, 0);
        float squareSum = 0;
        for (int index = 0; index < speechSamples.Length; index++) squareSum += speechSamples[index] * speechSamples[index];
        float rms = Mathf.Sqrt(squareSum / speechSamples.Length);
        float target = Mathf.Clamp01((rms - 0.003f) * 14f) * 65f;
        speechMouthWeight = Mathf.MoveTowards(speechMouthWeight, target, Time.unscaledDeltaTime * 400f);
    }
    private static void Set(List<FaceChannel> channels, float weight)
    {
        foreach (FaceChannel channel in channels) if (channel.renderer != null) channel.renderer.SetBlendShapeWeight(channel.index, weight);
    }
    public void PlayIdle() { PlayAction("idle"); }
    public void PlayWave() { PlayAction("wave"); }
    public void PlayHappy() { PlayAction("happy"); }
    public void PlayThink() { PlayAction("think"); }
    public void PlayNod() { PlayAction("nod"); }
    public void PlayTiltHead() { PlayAction("tilt_head"); }
    public void PlayShy() { PlayAction("shy"); }
    public void PlayStretch() { PlayAction("stretch"); }
    public void PlayCheer() { PlayAction("cheer"); }
    public void PlayLookAround() { PlayAction("look_around"); }
    public void PlayGuard() { PlayAction("guard"); }
    public void PlayRun() { PlayAction("run"); }
    public void PlayJump() { PlayAction("jump"); }
    // Serialized events from older scenes remain valid, with no retired playback.
    public void PlayHoldSword() { }
    public void PlayDrawSlash() { }
    public void PlayLegRetract() { PlayAction("leg_retract"); }
    public void PlayLegShift() { PlayAction("leg_shift"); }
    public void PlayLegSway() { PlayAction("leg_sway"); }
    public void PlayGuard(string touchedZone)
    {
        if (dragging || IsHandLeading || IsGuarding) return;
        CancelCustomAction();
        deferredState = null;
        // One library preference governs both protective poses. Old imported
        // packages without the lower variant retain their compatible Guard.
        StartState(touchedZone == "lower_body" && HasState("GuardLower") ? "GuardLower" : "Guard");
    }
}
