using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>Opt-in deterministic and live pose checks; never calls a model or touches user memory.</summary>
public sealed class PetBehaviorChecks : MonoBehaviour
{
    private string folder;
    private bool exitAfter;
    private int? autoSleep;
    private float? sleepDelay;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RunFlagged()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--xpet-behavior-checks") < 0) return;
        int index = Array.IndexOf(args, "--xpet-behavior-report-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        var runner = new GameObject("BehaviorChecks").AddComponent<PetBehaviorChecks>();
        runner.folder = Path.GetFullPath(args[index + 1]); runner.exitAfter = true;
        runner.StartCoroutine(runner.Check());
    }
#if UNITY_EDITOR
    [UnityEditor.MenuItem("Desktop Pet/Run Behavior Checks")]
    public static void RunEditor()
    {
        if (!UnityEditor.EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
        var runner = new GameObject("BehaviorChecks").AddComponent<PetBehaviorChecks>();
        runner.folder = Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/behavior"));
        runner.StartCoroutine(runner.Check());
    }
#endif
    private IEnumerator Check()
    {
        Directory.CreateDirectory(folder);
        if (exitAfter && Application.companyName != "DesktopPetQA") { Finish("QA identity is required for native preference isolation."); yield break; }
        try { CheckPolicy(); } catch (Exception error) { Finish(error.Message); yield break; }
        autoSleep = PlayerPrefs.HasKey(PetBehaviorCoordinator.AutoSleepKey) ? PlayerPrefs.GetInt(PetBehaviorCoordinator.AutoSleepKey) : (int?)null;
        sleepDelay = PlayerPrefs.HasKey(PetBehaviorCoordinator.SleepDelayKey) ? PlayerPrefs.GetFloat(PetBehaviorCoordinator.SleepDelayKey) : (float?)null;
        var client = FindAnyObjectByType<PetBackendHttpClient>();
        var settings = FindAnyObjectByType<SettingsPanelController>();
        if (client == null) { Finish("Missing chat service."); yield break; }
        var behavior = PetBehaviorCoordinator.EnsureOn(client.gameObject);
        float until = Time.realtimeSinceStartup + 30;
        while ((client.ActionBridge?.Character == null || !client.ActionBridge.Character.IsResting || !behavior.CanRunAutonomous) && Time.realtimeSinceStartup < until)
            yield return null;
        var character = client.ActionBridge?.Character;
        if (character == null || !character.IsResting) { Finish("Character did not become idle."); yield break; }
        string failure = null;
        try { CheckSampler(character); } catch (Exception error) { failure = error.Message; }
        if (failure == null) yield return DesktopPetPlacementChecks.Check(FindAnyObjectByType<TransparentWindowController>(), message => failure = message);
        if (failure == null) {
            var tabs = FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
            settings?.Open(); tabs?.ShowPage("关于"); yield return null;
            try {
                var about = tabs?.FindPageContent("关于");
                Require(about != null, "About page is missing.");
                bool author = false, link = false;
                foreach (var text in about.GetComponentsInChildren<TMPro.TMP_Text>()) { author |= text.text == PetAboutPage.AuthorName; link |= text.text == PetAboutPage.AuthorUrl; }
                Require(author && link, "Author name or GitHub link is missing.");
                tabs.ShowPage("语音");
                Require(tabs.FindPageContent("语音").GetComponentsInChildren<TMPro.TMP_Dropdown>().Length > 0, "ASR provider selector is missing.");
            } catch(Exception error) { failure=error.Message; }
            settings?.Close(); behavior.NoteActivity();
        }
        if (failure == null) {
            behavior.SetAutoSleep(true);
            settings?.Open();
            var policy = (PetBehaviorStateMachine)typeof(PetBehaviorCoordinator).GetField("policy", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(behavior);
            policy.Activity(Time.unscaledTime - 1000);
            for (int i=0;i<4;i++) yield return null;
            try { Require(!behavior.IsSleeping, "Settings interaction did not block automatic sleep."); }
            catch (Exception error) { failure=error.Message; }
            settings?.Close(); behavior.NoteActivity();
        }
        if (failure == null) {
            if (!behavior.RequestSleep()) failure = "Manual rest request was rejected while idle.";
            until = Time.realtimeSinceStartup + 8;
            while (behavior.SleepPhase != PetSleepPhase.Sleeping && Time.realtimeSinceStartup < until) yield return null;
            try {
                Require(behavior.SleepPhase == PetSleepPhase.Sleeping && character.IsSleeping, "SleepEnter did not reach SleepLoop.");
                Require(!behavior.CanRunAutonomous, "Autonomous behavior is permitted during sleep.");
            } catch (Exception error) { failure=error.Message; }
        }
        if (failure == null) {
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,"sleep.png"));
            try { Require(behavior.ConsumeWakeTap(), "First tap did not consume the wake event."); }
            catch (Exception error) { failure=error.Message; }
            until = Time.realtimeSinceStartup + 6;
            bool capturedStretch = false;
            while (behavior.IsSleeping && Time.realtimeSinceStartup < until) {
                if (!capturedStretch && behavior.SleepPhase == PetSleepPhase.Waking && behavior.SleepProgress >= .60f) {
                    try {
                        var visiblePose = (PetSleepPose)typeof(PetBehaviorCoordinator).GetField("sleepPose", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(behavior);
                        Require(visiblePose != null && visiblePose.PalmBindingCount == 2, "Live waking palm bindings are missing.");
                        Require(visiblePose.PalmViewerDot("L") >= .98f && visiblePose.PalmViewerDot("R") >= .98f, "Live stretching palms do not face the viewer.");
                        File.WriteAllText(Path.Combine(folder,"wake-palms.txt"), "left=" + visiblePose.PalmViewerDot("L").ToString("0.0000") + "; right=" + visiblePose.PalmViewerDot("R").ToString("0.0000"));
                    } catch (Exception error) { failure = error.Message; }
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder,"wake-stretch.png")); capturedStretch = true;
                }
                yield return null;
            }
            try { Require(!behavior.IsSleeping && !character.IsSleeping, "WakeUp did not restore idle."); }
            catch (Exception error) { failure=error.Message; }
        }
        if (failure == null) {
            behavior.NotifyLocalInteraction();
            client.ActionBridge.PlayCoordinatedAction("nod");
            try { Require(!behavior.TryStartAction("wave", PetActionOrigin.AiReply), "AI replaced protected local feedback."); }
            catch (Exception error) { failure=error.Message; }
            until=Time.realtimeSinceStartup+8;
            while (character.CurrentAction!="wave"&&Time.realtimeSinceStartup<until)yield return null;
            try { Require(character.CurrentAction=="wave", "Deferred reply action was not resumed after local feedback."); }
            catch(Exception error){failure=error.Message;}
        }
        behavior.CancelPendingActions(); character.PreviewAction("idle");
        if (autoSleep.HasValue) PlayerPrefs.SetInt(PetBehaviorCoordinator.AutoSleepKey,autoSleep.Value); else PlayerPrefs.DeleteKey(PetBehaviorCoordinator.AutoSleepKey);
        if (sleepDelay.HasValue)PlayerPrefs.SetFloat(PetBehaviorCoordinator.SleepDelayKey,sleepDelay.Value);else PlayerPrefs.DeleteKey(PetBehaviorCoordinator.SleepDelayKey);
        PlayerPrefs.Save();
        Finish(failure);
    }
    private static void CheckPolicy()
    {
        var state=new PetBehaviorStateMachine(0);
        Require(!state.ShouldSleep(299,300,true,true)&&state.ShouldSleep(300,300,true,true),"Idle sleep threshold is wrong.");
        Require(!state.ShouldSleep(1000,300,true,false)&&!state.ShouldSleep(1000,300,false,true),"Sleep guards are ignored.");
        state.BeginSleep(0);state.Tick(2,2.8,2.5);Require(state.SleepPhase==PetSleepPhase.Entering,"Enter completed too soon.");
        state.Tick(3,2.8,2.5);Require(state.SleepPhase==PetSleepPhase.Sleeping,"SleepLoop did not start.");
        state.Wake(4,false);state.Tick(6,2.8,2.5);Require(state.SleepPhase==PetSleepPhase.Waking,"Wake completed too soon.");
        state.Tick(7,2.8,2.5);Require(state.SleepPhase==PetSleepPhase.None&&!state.ShouldSleep(8,300,true,true),"Wake did not reset idle time.");
        foreach(int flags in new[]{1,2,4,8,16})Require(PetBehaviorStateMachine.MustDeferReply((flags&1)!=0,(flags&2)!=0,(flags&4)!=0,(flags&8)!=0,(flags&16)!=0),"A high-priority interaction was not protected.");
        Require(state.Resolve(false,false,false,false,false)==PetBehaviorState.Hidden,"Hidden state precedence is wrong.");
    }
    private static void CheckSampler(PetCharacterAnimation character)
    {
        character.GetComponent<PetMouseGaze>()?.ResetOffsets();
        var pose=PetSleepPose.Capture(character.transform);Require(pose!=null&&pose.IsValid,"Sleep sampler cannot bind the character.");
        var viewer = Camera.main;
        Require(viewer != null, "Sleep sampler requires the active viewer camera.");
        pose.SetViewerDirection(-viewer.transform.forward);
        var bones=pose.DrivenBones;var positions=new Vector3[bones.Count];var rotations=new Quaternion[bones.Count];
        for(int i=0;i<bones.Count;i++){positions[i]=bones[i].localPosition;rotations[i]=bones[i].localRotation;}
        foreach(float t in new[]{0f,.2f,.5f,.9f,1f}){
            pose.Apply("SleepEnter",t);pose.PrepareWake();
            var before=new Quaternion[bones.Count];for(int i=0;i<bones.Count;i++)before[i]=bones[i].localRotation;
            pose.Apply("WakeUp",0);
            for(int i=0;i<bones.Count;i++)Require(Quaternion.Angle(before[i],bones[i].localRotation)<.2f,"Interrupted sleep jumps on wake.");
            pose.Apply("WakeUp",1);pose.Restore();
        }
        pose.Apply("SleepLoop",.4f);var once=new Quaternion[bones.Count];for(int i=0;i<bones.Count;i++)once[i]=bones[i].localRotation;
        for(int repeat=0;repeat<20;repeat++)pose.Apply("SleepLoop",.4f);
        for(int i=0;i<bones.Count;i++)Require(Quaternion.Angle(once[i],bones[i].localRotation)<.1f,"Sleep sampling accumulates drift.");
        pose.Restore();
        Require(pose.FingerJointCount == 30 && pose.FistHandCount == 2, "The built-in model's fist joints were not mapped.");
        Require(pose.PalmBindingCount == 2, "The built-in model's palm directions were not mapped.");
        pose.Apply("WakeUp",.60f); int curled = 0;
        for (int i=0;i<bones.Count;i++) {
            string name = PetRuntimePoseRetargeter.CanonicalName(bones[i].name) ?? "";
            if ((name.StartsWith("人指") || name.StartsWith("中指") || name.StartsWith("薬指") || name.StartsWith("小指"))
                && Quaternion.Angle(rotations[i],bones[i].localRotation)>8) curled++;
        }
        Require(curled >= 20, "Stretching did not close the fingers into fists.");
        Require(pose.PalmViewerDot("L") >= .98f && pose.PalmViewerDot("R") >= .98f, "Stretching palms do not face the viewer.");
        var stretched = new Quaternion[bones.Count];for(int i=0;i<bones.Count;i++)stretched[i]=bones[i].localRotation;
        for(int repeat=0;repeat<20;repeat++)pose.Apply("WakeUp",.60f);
        for(int i=0;i<bones.Count;i++)Require(Quaternion.Angle(stretched[i],bones[i].localRotation)<.1f,"Waking hand orientation accumulates drift.");
        Require(pose.PalmViewerDot("L") >= .98f && pose.PalmViewerDot("R") >= .98f, "Repeated stretching changed palm orientation.");
        pose.Apply("WakeUp",1);
        pose.Restore();for(int i=0;i<bones.Count;i++)Require(Vector3.Distance(positions[i],bones[i].localPosition)<.00001f&&Quaternion.Angle(rotations[i],bones[i].localRotation)<.1f,"Sleep restore changed the idle baseline.");
    }
    private void Finish(string failure)
    {
        string result=failure==null?"PASSED: priority guards, idle threshold, sleep/wake phases, interrupted wake seams, fists with palms facing the viewer, no pose drift, settings guard, wake tap and deferred AI response.":"FAILED: "+failure;
        File.WriteAllText(Path.Combine(folder,"results.txt"),result);
        if(failure==null)Debug.Log(result);else Debug.LogError(result);
        if(exitAfter)Application.Quit(failure==null?0:1);else Destroy(gameObject);
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
