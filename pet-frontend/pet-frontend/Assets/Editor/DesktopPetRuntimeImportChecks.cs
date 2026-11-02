using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Exercises portable imported models using the shipped loader and
/// pose retargeter, independent of Editor model import and backend permissions.</summary>
public static class DesktopPetRuntimeImportChecks
{
    [MenuItem("Desktop Pet/Run Portable Model Import Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var library=UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>();
        if(library==null||library.IsBusy) throw new InvalidOperationException("Wait for the model library.");
        var runner=new GameObject("PortableModelImportChecks").AddComponent<DesktopPetRuntimeImportRunner>();
        runner.StartCoroutine(runner.Execute(library));
    }
}

public sealed class DesktopPetRuntimeImportRunner : MonoBehaviour
{
    private readonly StringBuilder report=new StringBuilder();
    private readonly Dictionary<GameObject,bool> originalActive=new Dictionary<GameObject,bool>();
    private GameObject candidate;
    private GameObject staging;
    private readonly Dictionary<Behaviour,bool> originalEnabled=new Dictionary<Behaviour,bool>();
    private Camera captureCamera;
    private Vector3 cameraPosition;
    private Quaternion cameraRotation;
    private bool cameraOrthographic;
    private float cameraSize;
    private string folder;
    public IEnumerator Execute(PetModelLibrary library)
    {
        folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../verification/runtime-import"));
        Directory.CreateDirectory(folder);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        GameObject template=(GameObject)typeof(PetModelLibrary).GetField("builtinModel",flags).GetValue(library);
        Transform parent=(Transform)typeof(PetModelLibrary).GetField("characterRoot",flags).GetValue(library);
        foreach(Transform child in parent) originalActive[child.gameObject]=child.gameObject.activeSelf;
        foreach(TransparentWindowController controller in UnityEngine.Object.FindObjectsByType<TransparentWindowController>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        { originalEnabled[controller]=controller.enabled;controller.enabled=false; }
        var canvas=GameObject.Find("DesktopPetCanvas");
        if(canvas!=null) {originalActive[canvas]=canvas.activeSelf;canvas.SetActive(false);}
        staging=new GameObject("PortableModelCaptureStage");
        captureCamera=Camera.main;
        if(captureCamera!=null)
        {
            cameraPosition=captureCamera.transform.position;cameraRotation=captureCamera.transform.rotation;
            cameraOrthographic=captureCamera.orthographic;cameraSize=captureCamera.orthographicSize;
            captureCamera.transform.position=new Vector3(0,.92f,-4);captureCamera.transform.rotation=Quaternion.identity;
            captureCamera.orthographic=true;captureCamera.orthographicSize=1.12f;
        }
        var operation=CheckAll(template,staging.transform);
        var pending=new Stack<IEnumerator>();pending.Push(operation);
        string failure=null;
        try
        {
            while(pending.Count>0)
            {
                var current=pending.Peek();bool moved=false;object next=null;
                try {moved=current.MoveNext();if(moved) next=current.Current;}
                catch(Exception error) {failure=error.Message;}
                if(failure!=null) break;
                if(!moved) {(pending.Pop() as IDisposable)?.Dispose();continue;}
                if(next is IEnumerator child) pending.Push(child);else yield return next;
            }
        }
        finally
        {
            while(pending.Count>0) (pending.Pop() as IDisposable)?.Dispose();
            if(candidate!=null) Destroy(candidate);
            if(staging!=null) Destroy(staging);
            if(captureCamera!=null)
            {
                captureCamera.transform.position=cameraPosition;captureCamera.transform.rotation=cameraRotation;
                captureCamera.orthographic=cameraOrthographic;captureCamera.orthographicSize=cameraSize;
            }
            foreach(var original in originalActive) if(original.Key!=null) original.Key.SetActive(original.Value);
            foreach(var original in originalEnabled) if(original.Key!=null) original.Key.enabled=original.Value;
            report.Insert(0,failure==null?"PASSED\n":"FAILED: "+failure+"\n");
            File.WriteAllText(Path.Combine(folder,"portable-model-checks.txt"),report.ToString());
            if(failure==null) Debug.Log(report.ToString());else Debug.LogError(report.ToString());
            Destroy(gameObject);
        }
    }
    private IEnumerator CheckAll(GameObject template,Transform parent)
    {
        foreach(string kind in new[]{"pmx-lita","fbx-art"})
        {
            string path=Path.Combine(folder,kind,"model.petraw");
            if(!File.Exists(path)) throw new Exception("Missing portable fixture: "+path);
            double started=Time.realtimeSinceStartupAsDouble;
            yield return PetRuntimeModelLoader.CreateModel(path,"Runtime "+kind,parent,template,value=>candidate=value);
            foreach(var original in originalActive) original.Key.SetActive(false);
            candidate.SetActive(true);
            yield return null;
            var character=candidate.GetComponent<PetCharacterAnimation>();
            var retargeter=candidate.GetComponent<PetRuntimePoseRetargeter>();
            var skins=candidate.transform.Find("VisualModel").GetComponentsInChildren<SkinnedMeshRenderer>().Where(s=>s.enabled).ToArray();
            if(character==null||retargeter==null||retargeter.MappedBoneCount<20||skins.Length==0) throw new Exception(kind+": missing runtime rig/mesh.");
            if(skins.Any(s=>s.sharedMesh==null||s.sharedMaterials.Any(m=>m==null||m.shader==null||m.shader.name.Contains("Error")))) throw new Exception(kind+": missing shader/material.");
            if(Mathf.Abs(candidate.GetComponent<CapsuleCollider>().height-1.72f)>0.01f) throw new Exception(kind+": body height incorrect.");
            var transforms=candidate.transform.Find("VisualModel").GetComponentsInChildren<Transform>();
            Transform left=transforms.First(t=>t.name=="足首.L"),right=transforms.First(t=>t.name=="足首.R"),head=transforms.First(t=>t.name=="頭"),knee=transforms.First(t=>t.name=="ひざ.L");
            character.PreviewAction("idle");yield return new WaitForSecondsRealtime(.6f);
            Vector3 facing=PetRuntimeModelLoader.MeasureFaceForward(candidate);
            float frontDot=Vector3.Dot(facing,Vector3.back);
            if(frontDot<0.85f) throw new Exception(kind+": the posed face points away from the front camera: "+facing);
            var weapon=candidate.GetComponent<PetWeaponAttachment>();
            if(weapon!=null&&weapon.VisibleWeaponCount!=0) throw new Exception(kind+": weapon must be hidden during Idle.");
            if(captureCamera!=null)
            {
                Vector3 centre=captureCamera.WorldToViewportPoint(candidate.transform.TransformPoint(new Vector3(0,.86f,0)));
                if(Mathf.Abs(centre.x-.5f)>.05f||centre.y<.3f||centre.y>.7f) throw new Exception(kind+": model capture is not centered.");
            }
            float ankleY=(candidate.transform.InverseTransformPoint(left.position).y+candidate.transform.InverseTransformPoint(right.position).y)*.5f;
            Quaternion restingKnee=knee.rotation;
            if(!Finite(head.position)||candidate.transform.InverseTransformPoint(head.position).y<ankleY+.7f) throw new Exception(kind+": invalid humanoid pose.");
            float armDrop=candidate.transform.InverseTransformPoint(transforms.First(t=>t.name=="腕.L").position).y-candidate.transform.InverseTransformPoint(transforms.First(t=>t.name=="手首.L").position).y;
            report.AppendLine(kind+" pose diagnostics:\n"+retargeter.DescribePose());
            if(armDrop<.25f) throw new Exception(kind+": idle arms did not drop naturally: "+armDrop);
            report.AppendLine("PASS "+kind+": load "+(Time.realtimeSinceStartupAsDouble-started).ToString("F2")+"s; meshes="+skins.Length+"; mapped bones="+retargeter.MappedBoneCount+"; arm drop="+armDrop.ToString("F3")+"; face toward camera dot="+frontDot.ToString("F4")+"; idle weapons="+(weapon!=null?weapon.VisibleWeaponCount:0));
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,kind+"-front.png"));yield return new WaitForSecondsRealtime(.2f);
            Quaternion front=candidate.transform.localRotation;candidate.transform.localRotation=front*Quaternion.Euler(0,90,0);
            yield return new WaitForSecondsRealtime(.2f);ScreenCapture.CaptureScreenshot(Path.Combine(folder,kind+"-side.png"));yield return new WaitForSecondsRealtime(.2f);
            candidate.transform.localRotation=front;
            if(kind=="pmx-lita")
            {
                int morphs=skins.Sum(s=>s.sharedMesh.blendShapeCount);
                if(morphs<10) throw new Exception("PMX vertex expressions were not imported.");
                character.SetEmotion("happy");yield return new WaitForSecondsRealtime(.4f);
                if(!skins.Any(s=>Enumerable.Range(0,s.sharedMesh.blendShapeCount).Any(i=>s.GetBlendShapeWeight(i)>1))) throw new Exception("PMX expression mapping is inactive.");
                report.AppendLine("PASS PMX facial morph channels="+morphs);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder,"pmx-lita-happy.png"));yield return new WaitForSecondsRealtime(.2f);
            }
            character.PreviewAction("run");yield return new WaitForSecondsRealtime(.65f);
            if(!character.IsRunning||Quaternion.Angle(restingKnee,knee.rotation)<3) throw new Exception(kind+": Run did not retarget to the imported knee.");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,kind+"-run.png"));yield return new WaitForSecondsRealtime(.2f);
            character.StopWalking();yield return new WaitForSecondsRealtime(.5f);
            if(candidate.transform.position.sqrMagnitude>.00001f) throw new Exception(kind+": Run moved the isolated capture actor.");
            character.PreviewAction("jump");yield return new WaitForSecondsRealtime(.68f);
            if(candidate.transform.InverseTransformPoint(head.position).y<ankleY+.8f) throw new Exception(kind+": Jump produced an invalid skeleton.");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder,kind+"-jump.png"));yield return new WaitForSecondsRealtime(2.2f);
            character.PreviewAction("idle");yield return new WaitForSecondsRealtime(.5f);
            float afterY=(candidate.transform.InverseTransformPoint(left.position).y+candidate.transform.InverseTransformPoint(right.position).y)*.5f;
            if(Mathf.Abs(afterY-ankleY)>.055f) throw new Exception(kind+": Jump failed to restore feet: "+(afterY-ankleY));
            report.AppendLine("PASS "+kind+": Run knee rotation, Jump and return; foot return error="+Mathf.Abs(afterY-ankleY).ToString("F4"));
            Destroy(candidate);candidate=null;yield return null;
        }
    }
    private static bool Finite(Vector3 p)=>!float.IsNaN(p.x+p.y+p.z)&&!float.IsInfinity(p.x+p.y+p.z);
}
