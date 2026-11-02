using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class DesktopPetHandLeadRuntimeChecks
{
    [MenuItem("Desktop Pet/Run Hand Lead Runtime Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        new GameObject("HandLeadRuntimeChecks").AddComponent<DesktopPetHandLeadRuntimeRunner>().StartCheck();
    }
}

public sealed class DesktopPetHandLeadRuntimeRunner : MonoBehaviour
{
    private readonly StringBuilder report = new StringBuilder();
    private PetCharacterAnimation character;
    private TransparentWindowController window;
    private bool windowEnabled;
    public void StartCheck() { StartCoroutine(Safe()); }
    private IEnumerator Safe()
    {
        string failure = null;
        var enumerator = Check();
        while (true)
        {
            bool moved = false; object next = null;
            try { moved = enumerator.MoveNext(); if (moved) next = enumerator.Current; }
            catch (Exception error) { failure = error.ToString(); }
            if (failure != null || !moved) break;
            yield return next;
        }
        character?.EndHandLead();
        character?.CancelHandFall(); character?.PreviewAction("idle");
        if (window != null) window.enabled = windowEnabled;
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/motion-quality"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"hand-lead-runtime-checks.txt"),(failure == null ? "PASSED\n" : "FAILED: " + failure + "\n") + report);
        if (failure != null) Debug.LogError(failure); else Debug.Log("PET_HAND_LEAD_RUNTIME PASSED\n" + report);
        Destroy(gameObject);
    }
    private IEnumerator Check()
    {
        var gesture = new PetHandLeadGesture();
        gesture.Press(Vector2.zero,0,"L"); gesture.Advance(new Vector2(3,0),.15f);
        Require(!gesture.Active,"Short palm press activated hand leading."); gesture.Cancel();
        Require(!gesture.Pending && !gesture.Running,"Release retained pointer state.");
        gesture.Press(Vector2.zero,0,"R");
        float time=0,x=0;
        for(int i=0;i<25;i++) { time+=.02f;x+=2;gesture.Advance(new Vector2(x,0),time); }
        Require(gesture.Active && gesture.Side=="R" && !gesture.Running,"Slow long drag did not enter walking.");
        for(int i=0;i<25;i++) { time+=.02f;x+=8;gesture.Advance(new Vector2(x,0),time); }
        Require(gesture.Running,"Fast pull did not enter running.");
        for(int i=0;i<8;i++) { time+=.02f;x+=4;gesture.Advance(new Vector2(x,0),time); }
        Require(gesture.Running,"Walk/run threshold lacks hysteresis.");
        for(int i=0;i<30;i++) { time+=.02f;gesture.Advance(new Vector2(x,0),time); }
        Require(!gesture.Running && Mathf.Abs(gesture.Velocity)<8,"Stopped pointer did not settle.");
        report.AppendLine("PASS pointer clock: short press, 280ms hold, slow walk, fast run, hysteresis, stop and release.");
        foreach(int fps in new[]{15,30,60})
        {
            var fall=new PetHandFallMotion();fall.Begin(240);float elapsed=0;bool landed=false;
            for(int frame=0;frame<fps*2 && fall.Active;frame++) {elapsed+=1f/fps;landed=fall.Step(1f/fps);Require(fall.Height>=0,"Fall crossed its ground line.");}
            Require(landed && !fall.Active && fall.Height==0 && Mathf.Abs(elapsed-Mathf.Sqrt(480f/1800f))<=1f/fps+.001f,"Gravity landing differs across frame rates.");
        }
        report.AppendLine("PASS release gravity: bounded ground contact and consistent fall time at 15/30/60 FPS.");
        var bridge=FindAnyObjectByType<PetAnimatorBridge>();
        character=bridge?.ActiveModelRoot?.GetComponent<PetCharacterAnimation>();
        Require(character!=null && !character.IsDragging && !character.IsTouchReacting,"Wait for the active model to rest.");
        window=FindAnyObjectByType<TransparentWindowController>();
        if(window!=null) {windowEnabled=window.enabled;window.enabled=false;}
        var animator=character.GetComponent<Animator>();
        var touch=FindAnyObjectByType<PetTouchFeedback>();
        Vector3 origin=character.transform.position;
        foreach(string side in new[]{"L","R"})
        {
            character.BeginHandLead(side); yield return new WaitForSecondsRealtime(.35f); yield return new WaitForEndOfFrame();
            string suffix=side=="L"?"Left":"Right";
            Require(character.IsHandLeading && character.HandLeadSide==side,"Hand side was not captured.");
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadIdle"+suffix),"Missing held-hand idle pose.");
            character.UpdateHandLead(1,110,false); yield return new WaitForSecondsRealtime(.4f); yield return new WaitForEndOfFrame();
            Require(character.IsWalking && !character.IsRunning && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadWalk"+suffix),"Slow hand lead has wrong state.");
            float phase=animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            character.PreviewAction("angry_stomp");
            for(int i=0;i<12;i++) { character.UpdateHandLead(1,110,false); yield return new WaitForEndOfFrame(); }
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadWalk"+suffix)
                && animator.GetCurrentAnimatorStateInfo(0).normalizedTime>phase+.05f,"Repeated updates/reply restarted the held-hand cycle.");
            character.UpdateHandLead(-1,320,true); yield return new WaitForSecondsRealtime(.4f); yield return new WaitForEndOfFrame();
            Require(character.IsRunning && character.WalkingDirection<0 && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadRun"+suffix),"Fast/reversed hand lead has wrong state.");
            character.UpdateHandLead(-1,0,false); yield return new WaitForSecondsRealtime(.35f); yield return new WaitForEndOfFrame();
            Require(!character.IsWalking && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadIdle"+suffix),"Stationary held hand did not stand still.");
            string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/motion-quality")); Directory.CreateDirectory(folder);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,"hand-lead-"+side+".png"));
            var armBones=character.transform.Find("VisualModel").GetComponentsInChildren<Transform>(true);
            var upper=armBones.First(b=>b.name=="腕."+side);var elbow=armBones.First(b=>b.name=="ひじ."+side);var hand=armBones.First(b=>b.name=="手首."+side);
            float reach=Vector3.Distance(upper.position,elbow.position)+Vector3.Distance(elbow.position,hand.position);
            Vector2 shoulder=Camera.main.WorldToScreenPoint(upper.position);
            float pixels=Vector2.Distance(shoulder,Camera.main.WorldToScreenPoint(upper.position+Camera.main.transform.right*reach));
            Vector2 pullDirection=new Vector2(side=="L" ? .65f : -.65f,.76f).normalized;
            character.SetHandLeadPointer(shoulder+pullDirection*pixels*.55f,false,.2f);
            yield return new WaitForSecondsRealtime(.4f);yield return new WaitForEndOfFrame();
            var ik=character.GetComponent<PetHandLeadArm>();
            Require(ik!=null && ik.ActiveSide==side,"The selected arm IK did not activate.");
            float bentAngle=ik.ElbowAngle;
            character.SetHandLeadPointer(shoulder+Vector2.up*pixels*1.15f,true,1);
            character.UpdateHandLead(1,0,false);
            yield return new WaitForSecondsRealtime(.5f);yield return new WaitForEndOfFrame();
            report.AppendLine($"IK {side}: near={bentAngle:F2}deg, far={ik.ElbowAngle:F2}deg, error={ik.TargetError:F4}m, reach={reach:F4}m, active={ik.ActiveSide}, held={character.IsHandLeading}, pointer={character.HandLeadHasPointer}, air={character.HandLeadAirborne}, state={animator.GetCurrentAnimatorStateInfo(0).fullPathHash}.");
            Require(ik.ElbowAngle>bentAngle+15 && ik.ElbowAngle>172,"Overhead suspension did not nearly straighten the selected arm.");
            Vector2 armScreen=(Vector2)Camera.main.WorldToScreenPoint(hand.position)-(Vector2)Camera.main.WorldToScreenPoint(upper.position);
            Require(armScreen.y>pixels*.85f && Mathf.Abs(armScreen.x)<pixels*.15f,"Suspended wrist is not above its shoulder.");
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadAir"+suffix),"Upward lift did not enter suspended pose.");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,"hand-lift-"+side+".png"));
            character.BeginHandFall();yield return new WaitForSecondsRealtime(.2f);
            Require(character.IsHandFalling && character.IsTouchReacting && !character.IsHandLeading,"Release did not protect the falling pose.");
            character.PlayHandLanding();yield return new WaitForSecondsRealtime(.35f);yield return new WaitForEndOfFrame();
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HandLeadLand"),"Ground contact did not use the landing cushion.");
            yield return new WaitForSecondsRealtime(1);yield return new WaitForEndOfFrame();
            Require(!character.IsHandLeading && !character.IsWalking && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"),"Release did not restore Idle.");
            var wrist=character.transform.Find("VisualModel").GetComponentsInChildren<Transform>(true).First(b=>b.name=="手首."+side);
            Vector2 palm=Camera.main.WorldToScreenPoint(wrist.position);
            Require(touch.TryGetHandSide(palm,out string selected) && selected==side,"Palm hit test returned the opposite hand.");
            report.AppendLine("PASS " + side + ": palm side, held idle, walk/run and turn, uninterrupted cycle, blocked reply, mouse-angle IK, extended arm, suspended pose, release/fall/cushioned landing and unchanged actor position.");
        }
        Require(Vector3.Distance(origin,character.transform.position)<.0001f,"Animation API moved the actor container; only the pointer controller should move it.");
        character.PreviewAction("angry_stomp");
        float stompDuration=animator.runtimeAnimatorController.animationClips.First(c=>c.name=="AngryStomp").length;
        yield return new WaitForSecondsRealtime(stompDuration*.4f); yield return new WaitForEndOfFrame();
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.AngryStomp") && character.FacialExpression.Emotion=="angry","Stomp lost its angry expression during the pose.");
        ScreenCapture.CaptureScreenshot(Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/motion-quality/angry-stomp-runtime.png")));
        yield return new WaitForSecondsRealtime(stompDuration*.65f+.4f); yield return new WaitForEndOfFrame();
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle") && character.FacialExpression.Emotion=="neutral","Stomp did not relax its expression after returning to Idle.");
        report.AppendLine("PASS angry stomp: playable public action, angry expression retained through the fist pose, neutral Idle after completion.");
        report.AppendLine("LIMIT: shared pointer clock and live Animator tested; native Windows window movement is checked separately.");
    }
    private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
}
