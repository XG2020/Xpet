using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Exercises the actual projected-leg hit path on the active model, including VMD priority.</summary>
public static class DesktopPetLegTouchChecks
{
    [MenuItem("Desktop Pet/Run Leg Touch Interaction Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying || UnityEngine.Object.FindAnyObjectByType<DesktopPetLegTouchRunner>() != null)
            throw new InvalidOperationException("Enter Play Mode and wait for any existing leg-touch check to finish.");
        if (DesktopPetPrefs.GetBackendBaseUrl() != "http://127.0.0.1:18081")
            throw new InvalidOperationException("Select the isolated QA backend at http://127.0.0.1:18081 first.");
        var runner = new GameObject("LegTouchInteractionChecks").AddComponent<DesktopPetLegTouchRunner>();
        runner.StartCoroutine(runner.Execute());
    }
}

public sealed class DesktopPetLegTouchRunner : MonoBehaviour
{
    private const string AffinityKey = "desktop_pet.affinity", GainKey = "desktop_pet.affinity_last_gain";
    private const float MinimumScreenTravel = 0.02f;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly StringBuilder report = new StringBuilder();
    private readonly Dictionary<FieldInfo, object> touchFields = new Dictionary<FieldInfo, object>();
    private readonly Dictionary<FieldInfo, object> reactionFields = new Dictionary<FieldInfo, object>();
    private readonly Dictionary<Behaviour, bool> disabled = new Dictionary<Behaviour, bool>();
    private readonly Dictionary<GameObject, bool> panelStates = new Dictionary<GameObject, bool>();
    private Dictionary<string, int> previousChoices;
    private PetTouchFeedback touch;
    private PetCharacterAnimation character;
    private PetCharacterCards cards;
    private PetImportedMotionPlayer player;
    private Animator animator;
    private Transform model, leftFoot, rightFoot, leftKnee, rightKnee;
    private Camera view;
    private GameObject canvas, quietBubble;
    private Texture2D peakImage;
    private bool hadAffinity, hadGain, canvasActive, captured, finished, createdPlayer, libraryChanged;
    private bool cardsApplying;
    private int affinity;
    private string gain, originalScope, originalActionJson, scratchKey, scratchJson, folder, fixturePath;
    private bool hadActionPrefs, hadScratchPrefs, fixtureWasNew;
    private PetActionEntry[] originalActions;
    private UnityEngine.Random.State randomState;
    private PetActionEntry importedEntry;
    private System.Threading.Tasks.Task<PetImportedMotionPlayer.Prepared> preparation;
    private float savedNextTapRemaining;

    public IEnumerator Execute()
    {
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder);
        EditorApplication.playModeStateChanged += OnPlayMode;
        var pending = new Stack<IEnumerator>(); pending.Push(CheckAll());
        string failure = null;
        try
        {
            while (pending.Count > 0 && !finished)
            {
                var sequence = pending.Peek(); bool moved = false; object next = null;
                try { moved = sequence.MoveNext(); if (moved) next = sequence.Current; }
                catch (Exception error) { failure = error.ToString(); }
                if (failure != null) break;
                if (!moved) { (pending.Pop() as IDisposable)?.Dispose(); continue; }
                if (next is IEnumerator nested) pending.Push(nested); else yield return next;
            }
        }
        finally
        {
            while (pending.Count > 0) (pending.Pop() as IDisposable)?.Dispose();
            Finish(failure);
            Destroy(gameObject);
        }
    }

    private IEnumerator CheckAll()
    {
        Require(DesktopPetPrefs.GetBackendBaseUrl() == "http://127.0.0.1:18081", "The backend must remain explicitly isolated.");
        var bridge = FindAnyObjectByType<PetAnimatorBridge>();
        model = bridge != null ? bridge.ActiveModelRoot : null;
        character = model != null ? model.GetComponent<PetCharacterAnimation>() : null;
        animator = model != null ? model.GetComponent<Animator>() : null;
        touch = FindAnyObjectByType<PetTouchFeedback>();
        view = Camera.main;
        Require(character != null && animator != null && animator.runtimeAnimatorController != null && touch != null && view != null,
            "An active character, Animator, touch controller and camera are required.");
        var client = touch.GetComponent<PetBackendHttpClient>();
        var library = touch.GetComponent<PetModelLibrary>();
        cards = touch.GetComponent<PetCharacterCards>();
        Require(client != null && !client.IsBusy && !client.IsSpeaking && (library == null || !library.IsBusy) && (cards == null || !cards.IsBusy),
            "Wait for model/card loading, chat and speech to finish.");
        Require(!character.IsDragging && !character.IsGuarding && !character.IsTouchReacting && !character.IsWalking,
            "Wait until the character is resting before running the interaction checks.");
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle") && !animator.IsInTransition(0), "The active model must be in settled Idle.");
        Require((PetCharacterAnimation)TouchField("character").GetValue(touch) == character, "Touch controller is not bound to the active model.");
        player = model.GetComponent<PetImportedMotionPlayer>();
        Require(player == null || !player.IsPlaying, "Finish the user's existing imported action before testing.");
        Transform visual = model.Find("VisualModel");
        Require(visual != null, "Active model has no visual hierarchy.");
        var deforming = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(s => s.bones).Where(b => b != null).Distinct().ToArray();
        Transform Bone(string ordinary, string deformingName) => deforming.FirstOrDefault(b => PetVmdMotion.CanonicalBone(b.name) == deformingName)
            ?? deforming.FirstOrDefault(b => PetVmdMotion.CanonicalBone(b.name) == ordinary);
        leftFoot = Bone("足首.L", "足首D.L"); rightFoot = Bone("足首.R", "足首D.R");
        leftKnee = Bone("ひざ.L", "ひざD.L"); rightKnee = Bone("ひざ.R", "ひざD.R");
        Require(leftFoot != null && rightFoot != null && leftKnee != null && rightKnee != null, "Both deforming lower-leg chains must be available.");
        CaptureAndIsolate();
        report.AppendLine("Model: " + model.name + "; feet: " + leftFoot.name + ", " + rightFoot.name + "; screen: " + Screen.width + "x" + Screen.height);
        report.AppendLine("TouchAtScreenPoint uses the real camera projection, UI gate and physics raycast. Every pose measurement occurs after WaitForEndOfFrame.");
        report.AppendLine("LIMIT: QA URL is required, but touch.client is temporarily null. This exercises local fallback and animation priority without sending chat, speech or network requests.");
        report.AppendLine("LIMIT: Actual user pointer input and native Windows hit testing are not simulated by this Editor check. Visibility threshold is 2% of projected character height.");
        yield return SampleCase("affinity-20", 20, "LegRetract", false);
        yield return SampleCase("affinity-50", 50, "LegShift", false);
        yield return SampleCase("affinity-80", 80, "LegSway", false);
        yield return SampleCase("left-affinity-20", 20, "LegRetractLeft", false, "L");
        yield return SampleCase("left-affinity-50", 50, "LegShiftLeft", false, "L");
        yield return SampleCase("left-affinity-80", 80, "LegSwayLeft", false, "L");

        byte[] bytes = CreateMotionFixture();
        string source = Path.Combine(folder, "leg-touch-priority.vmd"); File.WriteAllBytes(source, bytes);
        string hash; using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        fixturePath = PetImportedMotionPlayer.ResolveFile(hash + ".vmd"); fixtureWasNew = !File.Exists(fixturePath);
        preparation = PetImportedMotionPlayer.Prepare(source);
        while (!preparation.IsCompleted) yield return null;
        if (preparation.IsFaulted) throw preparation.Exception.GetBaseException();
        Require(PetImportedMotionPlayer.CountCompatibleBones(model, preparation.Result.motion) >= 2, "VMD priority fixture does not match the active deforming rig.");
        importedEntry = PetActionLibrary.AddImported("腿部优先级回归", preparation.Result.fileName, preparation.Result.motion.Duration, 1);
        if (player == null) { player = model.gameObject.AddComponent<PetImportedMotionPlayer>(); createdPlayer = true; }
        // This call starts async loading. The touch follows synchronously, before Load can bind.
        yield return Rest();
        Require(player.Play(importedEntry) && player.IsPlaying && player.BoundBoneCount == 0, "Could not establish the VMD loading case.");
        yield return SampleCase("vmd-loading-affinity-20", 20, "LegRetract", true);
        yield return Rest();
        Require(player.Play(importedEntry), "Could not start the VMD playback case.");
        float deadline = Time.unscaledTime + 5;
        while (player.IsPlaying && player.BoundBoneCount < 2 && Time.unscaledTime < deadline) yield return null;
        yield return new WaitForSecondsRealtime(0.5f); yield return new WaitForEndOfFrame();
        Require(player.IsPlaying && player.BoundBoneCount >= 2, "Could not establish active VMD playback.");
        yield return SampleCase("vmd-playing-affinity-80", 80, "LegSway", true);
    }

    private void CaptureAndIsolate()
    {
        hadAffinity = PlayerPrefs.HasKey(AffinityKey); affinity = PlayerPrefs.GetInt(AffinityKey, 20);
        hadGain = PlayerPrefs.HasKey(GainKey); gain = PlayerPrefs.GetString(GainKey, ""); randomState = UnityEngine.Random.state;
        foreach (string name in new[] { "client", "bubble", "nextTap", "zone", "waiting", "pendingZone", "pendingAffinity", "pressedAt" })
        { FieldInfo field = TouchField(name); touchFields.Add(field, field.GetValue(touch)); }
        savedNextTapRemaining = Mathf.Max(0, (float)TouchField("nextTap").GetValue(touch) - Time.unscaledTime);
        var choices = (Dictionary<string, int>)TouchField("previousChoices").GetValue(touch); previousChoices = new Dictionary<string, int>(choices);
        foreach (string name in new[] { "touchReactionUntil", "guardingUntil", "currentAction", "deferredState" })
        { FieldInfo field = typeof(PetCharacterAnimation).GetField(name, Hidden); reactionFields.Add(field, field.GetValue(character)); }
        originalScope = PetActionLibrary.CharacterId; originalActions = PetActionLibrary.Export();
        string actionKey = originalScope == "default" ? PetActionLibrary.PrefsKey : PetActionLibrary.PrefsKey + "." + originalScope;
        hadActionPrefs = PlayerPrefs.HasKey(actionKey); originalActionJson = PlayerPrefs.GetString(actionKey, "");
        scratchKey = PetActionLibrary.PrefsKey + ".leg_touch_verification"; hadScratchPrefs = PlayerPrefs.HasKey(scratchKey); scratchJson = PlayerPrefs.GetString(scratchKey, "");
        canvas = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(c => c.name == "DesktopPetCanvas")?.gameObject;
        canvasActive = canvas != null && canvas.activeSelf;
        if (canvas != null) foreach (Transform panel in canvas.GetComponentsInChildren<Transform>(true)) panelStates[panel.gameObject] = panel.gameObject.activeSelf;
        captured = true;
        if (cards != null) { cardsApplying = (bool)typeof(PetCharacterCards).GetField("applying", Hidden).GetValue(cards); typeof(PetCharacterCards).GetField("applying", Hidden).SetValue(cards, true); }
        // Prevent incidental mouse input, desktop repositioning and capability writes during the fixture.
        Disable(touch); Disable(touch.GetComponent<TransparentWindowController>()); Disable(touch.GetComponent<PetRuntimeActionReporter>());
        if (canvas != null) canvas.SetActive(false);
        quietBubble = new GameObject("Inactive Leg Touch Fallback Sink"); quietBubble.SetActive(false);
        TouchField("client").SetValue(touch, null); TouchField("bubble").SetValue(touch, quietBubble.AddComponent<PetSpeechBubble>());
        libraryChanged = true; PetActionLibrary.ApplyCharacter("leg_touch_verification", Array.Empty<PetActionEntry>());
    }

    private IEnumerator Rest()
    {
        player?.StopImmediately(); character.PreviewAction("idle");
        yield return new WaitForSecondsRealtime(0.45f); yield return new WaitForEndOfFrame();
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "Model did not return to Idle between cases.");
    }

    private IEnumerator SampleCase(string label, int level, string state, bool interruptVmd, string side = "R")
    {
        // Preserve the loading-case race: no yield or idle request may occur before this tap.
        if (!interruptVmd) yield return Rest();
        PlayerPrefs.SetInt(AffinityKey, level); PlayerPrefs.SetString(GainKey, "0");
        TouchField("nextTap").SetValue(touch, 0f);
        Physics.SyncTransforms();
        Vector2 click = ProjectCalf(side);
        Vector2 initialLeft = view.WorldToScreenPoint(leftFoot.position), initialRight = view.WorldToScreenPoint(rightFoot.position);
        Vector3 initialRoot = model.position;
        float screenHeight = Vector2.Distance(view.WorldToScreenPoint(model.position), view.WorldToScreenPoint(model.TransformPoint(Vector3.up * 1.72f)));
        Require(screenHeight > 40, "Character is too small for a meaningful screen-motion check.");
        AnimationClip clip = animator.runtimeAnimatorController.animationClips.FirstOrDefault(c => c != null && c.name == state);
        Require(clip != null, label + ": missing expected clip " + state);
        float start = Time.unscaledTime;
        touch.TouchAtScreenPoint(click);
        Require((string)TouchField("zone").GetValue(touch) == "leg" && character.IsTouchReacting, label + ": projected calf click did not enter a leg reaction.");
        Require(touch.LastLegSide == side, label + ": selected the opposite leg.");
        int expectedAffinity = level < 30 ? level : level + 1;
        Require(touch.Affinity == expectedAffinity, label + ": affinity gain policy changed.");
        bool seen = false, ended = false, injected = false;
        float maximumPhase = 0, peak = 0, capturedPeak = -1, peakAt = 0, samples = 0;
        string peakFoot = "";
        while (Time.unscaledTime - start < Mathf.Max(clip.length, 3f) + 1.6f)
        {
            yield return new WaitForEndOfFrame();
            float elapsed = Time.unscaledTime - start;
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0), next = animator.GetNextAnimatorStateInfo(0);
            bool currentLeg = current.IsName("Base Layer." + state);
            bool nextLeg = animator.IsInTransition(0) && next.IsName("Base Layer." + state);
            if (currentLeg || nextLeg)
            {
                seen = true;
                if (currentLeg) maximumPhase = Mathf.Max(maximumPhase, current.normalizedTime);
                Require(character.IsTouchReacting || (currentLeg && current.normalizedTime >= 0.95f), label + ": touch protection ended before the visible clip finished.");
            }
            else if (seen)
            {
                Require(maximumPhase >= 0.85f, label + ": leg motion was interrupted before its release; phase=" + maximumPhase);
                Require(current.IsName("Base Layer.Idle") || animator.IsInTransition(0) && next.IsName("Base Layer.Idle"), label + ": unexpected action replaced the leg response.");
                ended = true;
            }
            else Require(elapsed < 0.65f, label + ": Animator never entered " + state);
            if (interruptVmd && elapsed > 0.12f) Require(!player.IsPlaying, label + ": VMD loading/playback still overrides the leg touch.");
            if (!injected && !interruptVmd && elapsed > 0.35f)
            { character.PlayAction("speak"); injected = true; } // A fast ordinary reply must not cut off the local reaction.
            float left = Vector2.Distance(initialLeft, view.WorldToScreenPoint(leftFoot.position));
            float right = Vector2.Distance(initialRight, view.WorldToScreenPoint(rightFoot.position));
            float travel = Mathf.Max(left, right); samples++;
            Require(Vector3.Distance(initialRoot, model.position) < 0.001f, label + ": character container moved during a stationary reaction.");
            if (travel > peak) { peak = travel; peakAt = elapsed; peakFoot = left > right ? leftFoot.name : rightFoot.name; }
            if (travel > capturedPeak + 0.5f)
            {
                capturedPeak = travel;
                if (peakImage != null) Destroy(peakImage);
                peakImage = ScreenCapture.CaptureScreenshotAsTexture();
            }
            if (ended && !character.IsTouchReacting) break;
        }
        if (peakImage != null)
        { File.WriteAllBytes(Path.Combine(folder, "leg-touch-" + label + "-peak.png"), peakImage.EncodeToPNG()); Destroy(peakImage); peakImage = null; }
        report.AppendLine($"{label}: affinity={level}->{touch.Affinity}; state={state}; click=({click.x:F1},{click.y:F1}); phase={maximumPhase:F3}; samples={samples}; peak={peak:F2}px ({peak / screenHeight:P2} of {screenHeight:F1}px body); at={peakAt:F3}s; foot={peakFoot}; VMD stopped={(!interruptVmd || !player.IsPlaying)}.");
        Require(seen && ended && !character.IsTouchReacting, label + ": reaction did not complete and return to unprotected Idle.");
        Require(peak / screenHeight >= MinimumScreenTravel, label + ": projected foot motion is too small to see; normalized travel=" + (peak / screenHeight).ToString("F4"));
        report.AppendLine("PASS " + label + ": real calf hit, correct affinity tier, protected full motion, visible screen displacement, and Idle return.");
    }

    private Vector2 ProjectCalf(string side)
    {
        foreach (var pair in new[] { side == "L" ? new[] { leftKnee, leftFoot } : new[] { rightKnee, rightFoot } })
        {
            Vector3 screen = view.WorldToScreenPoint(Vector3.Lerp(pair[0].position, pair[1].position, 0.58f));
            if (screen.z <= 0 || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height) continue;
            if (Physics.Raycast(view.ScreenPointToRay(screen), out RaycastHit hit, 100, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)
                && hit.collider.GetComponentInParent<PetCharacterAnimation>() == character) return screen;
        }
        throw new InvalidOperationException("Neither projected visible calf hits the active character collider.");
    }

    private void Disable(Behaviour behaviour) { if (behaviour != null) { disabled.Add(behaviour, behaviour.enabled); behaviour.enabled = false; } }
    private static FieldInfo TouchField(string name) => typeof(PetTouchFeedback).GetField(name, Hidden) ?? throw new MissingFieldException(nameof(PetTouchFeedback), name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private void OnPlayMode(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingPlayMode) Finish("Play Mode ended before the leg-touch checks completed."); }
    private void OnDestroy() { if (!finished) Finish("The leg-touch runner was destroyed before completion."); }

    private void Finish(string failure)
    {
        if (finished) return; finished = true; EditorApplication.playModeStateChanged -= OnPlayMode;
        try
        {
            if (captured)
            {
                player?.StopImmediately();
                character?.PreviewAction("idle");
                foreach (var pair in reactionFields) if (character != null) pair.Key.SetValue(character, pair.Value);
                foreach (var pair in touchFields) if (touch != null) pair.Key.SetValue(touch, pair.Value);
                if (touch != null)
                {
                    TouchField("nextTap").SetValue(touch, Time.unscaledTime + savedNextTapRemaining);
                    var choices = (Dictionary<string, int>)TouchField("previousChoices").GetValue(touch); choices.Clear();
                    foreach (var pair in previousChoices) choices.Add(pair.Key, pair.Value);
                }
                if (hadAffinity) PlayerPrefs.SetInt(AffinityKey, affinity); else PlayerPrefs.DeleteKey(AffinityKey);
                if (hadGain) PlayerPrefs.SetString(GainKey, gain); else PlayerPrefs.DeleteKey(GainKey);
                if (libraryChanged)
                {
                    PetActionLibrary.ApplyCharacter(originalScope, originalActions);
                    string actionKey = originalScope == "default" ? PetActionLibrary.PrefsKey : PetActionLibrary.PrefsKey + "." + originalScope;
                    if (hadActionPrefs) PlayerPrefs.SetString(actionKey, originalActionJson); else PlayerPrefs.DeleteKey(actionKey);
                    if (hadScratchPrefs) PlayerPrefs.SetString(scratchKey, scratchJson); else PlayerPrefs.DeleteKey(scratchKey);
                    PetActionLibrary.Reload();
                }
                if (cards != null) typeof(PetCharacterCards).GetField("applying", Hidden).SetValue(cards, cardsApplying);
                foreach (var pair in panelStates) if (pair.Key != null && pair.Key != canvas) pair.Key.SetActive(pair.Value);
                if (canvas != null) canvas.SetActive(canvasActive);
                foreach (var pair in disabled) if (pair.Key != null) pair.Key.enabled = pair.Value;
                UnityEngine.Random.state = randomState; PlayerPrefs.Save();
            }
            if (createdPlayer && player != null) Destroy(player);
            if (quietBubble != null) Destroy(quietBubble);
            if (peakImage != null) Destroy(peakImage);
            if (fixtureWasNew && !string.IsNullOrEmpty(fixturePath))
            {
                string ownFixture = fixturePath;
                if (preparation != null && !preparation.IsCompleted) preparation.ContinueWith(_ => { if (File.Exists(ownFixture)) File.Delete(ownFixture); });
                else if (File.Exists(ownFixture)) File.Delete(ownFixture);
            }
            report.AppendLine("Restored affinity/gain, touch cooldown and local fallback state, action preferences/scope, enabled components, random state, and canvas visibility. Only the test-created VMD cache file was removed.");
        }
        catch (Exception error) { failure = (failure == null ? "" : failure + "\n") + "Cleanup: " + error; }
        string result = (failure == null ? "PASSED\n" : "FAILED: " + failure + "\n") + report;
        if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, "leg-touch-runtime-checks.txt"), result, new UTF8Encoding(false));
        if (failure == null) Debug.Log("PET_LEG_TOUCH_RUNTIME " + result); else Debug.LogError("PET_LEG_TOUCH_RUNTIME " + result);
    }

    private static byte[] CreateMotionFixture()
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            void Pad(byte[] value, int length) { writer.Write(value.Take(length).ToArray()); if (value.Length < length) writer.Write(new byte[length - value.Length]); }
            Pad(Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002"), 30);
            Pad(Encoding.ASCII.GetBytes("LegTouch" + Guid.NewGuid().ToString("N")), 20); writer.Write((uint)12);
            foreach (string bone in new[] { "頭", "左足", "右足" }) foreach (uint frame in new uint[] { 0, 30, 180, 240 })
            {
                Pad(Encoding.GetEncoding(932).GetBytes(bone), 15); writer.Write(frame);
                writer.Write(0f); writer.Write(0f); writer.Write(0f);
                Quaternion rotation = frame == 30 || frame == 180 ? Quaternion.AngleAxis(bone == "頭" ? 18 : 6, Vector3.forward) : Quaternion.identity;
                writer.Write(rotation.x); writer.Write(rotation.y); writer.Write(rotation.z); writer.Write(rotation.w);
                byte[] interpolation = new byte[64];
                for (int channel = 0; channel < 4; channel++) { interpolation[channel] = interpolation[channel + 4] = 20; interpolation[channel + 8] = interpolation[channel + 12] = 107; }
                writer.Write(interpolation);
            }
            return stream.ToArray();
        }
    }
}
