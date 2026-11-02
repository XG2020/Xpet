using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>Explicit visual loading checks of the supplied three new caches; no source model writes.</summary>
[DefaultExecutionOrder(60)]
public sealed class PetNewModelChecks : MonoBehaviour
{
    private string output, libraryRoot;
    private PetSleepPose sampledPose;
    private string poseState;
    private float poseTime;
    private string deformationFailure;
    private readonly StringBuilder report = new StringBuilder();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RunFlagged()
    {
        var args=Environment.GetCommandLineArgs();if(Array.IndexOf(args,"--xpet-new-model-checks")<0)return;
        int root=Array.IndexOf(args,"--xpet-model-source-root"),dir=Array.IndexOf(args,"--xpet-new-model-report-dir");
        if(root<0||dir<0||root+1>=args.Length||dir+1>=args.Length)return;
        var runner=new GameObject("NewModelChecks").AddComponent<PetNewModelChecks>();
        runner.output=Path.GetFullPath(args[dir+1]);runner.libraryRoot=Path.GetFullPath(args[root+1]);runner.StartCoroutine(runner.Check());
    }
    private void LateUpdate(){if(!string.IsNullOrEmpty(poseState))sampledPose?.Apply(poseState,poseTime);}
    private IEnumerator Check()
    {
        Directory.CreateDirectory(output);
        if(Application.companyName!="DesktopPetQA"){Finish("Isolated QA identity required.");yield break;}
        var client=FindAnyObjectByType<PetBackendHttpClient>();float until=Time.realtimeSinceStartup+30;
        while((client?.ActionBridge?.Character==null||!client.ActionBridge.Character.IsResting)&&Time.realtimeSinceStartup<until)yield return null;
        GameObject template=client?.ActionBridge?.Character?.gameObject;
        if(template==null){Finish("Bundled pose template not ready.");yield break;}
        string error=null;GameObject model=null;
        var parent=new GameObject("NewModelPreview").transform;
        parent.position=template.transform.parent.position+Vector3.left*1.5f;parent.localScale=template.transform.parent.localScale;
        foreach(var id in new[]{"model-42e48f48384f4e2d","model-c9a21a3b645e0f79","model-be2cae3865b73f1c"}){
            poseState=null;sampledPose=null;poseTime=0;
            string path=Path.Combine(libraryRoot,id,"model.petraw");
            IEnumerator loading=null;
            try{loading=PetRuntimeModelLoader.CreateModel(path,id,parent,template,value=>model=value);}catch(Exception ex){error=ex.Message;}
            while(error==null&&loading!=null){bool more=false;object current=null;try{more=loading.MoveNext();if(more)current=loading.Current;}catch(Exception ex){error=ex.Message;}if(!more)break;yield return current;}
            if(error!=null)break;
            model.SetActive(true);var character=model.GetComponent<PetCharacterAnimation>();character.PreviewAction("idle");
            yield return new WaitForSecondsRealtime(.5f);
            try{
                var owned=model.GetComponent<PetRuntimeModelResources>();
                float expected=id.Contains("c9a21")?1.64968f:id.Contains("be2ca")?.99989f:1.72f;
                if(Mathf.Abs(owned.TargetHeightMeters-expected)>.03f)throw new InvalidOperationException("Height profile was not retained: "+id);
                if(character.PlaybackAnimator==null||!character.PlaybackAnimator.HasState(0,Animator.StringToHash("Base Layer.Jump")))throw new InvalidOperationException("Shared animation controller is missing.");
                var renderers=model.GetComponentsInChildren<SkinnedMeshRenderer>();
                if(renderers.Length==0||renderers.Any(renderer=>renderer.sharedMesh==null||renderer.sharedMesh.vertexCount==0))throw new InvalidOperationException("Mesh parts did not load.");
                report.AppendLine(id+": height="+owned.TargetHeightMeters.ToString("0.000")+"m; rig="+owned.RigSource+"; parts="+renderers.Length+"; morphs="+renderers.Sum(renderer=>renderer.sharedMesh.blendShapeCount));
            }catch(Exception ex){error=ex.Message;}
            if(error!=null)break;
            CaptureDeformation(model,id,"idle");
            ScreenCapture.CaptureScreenshot(Path.Combine(output,id+"-idle.png"));yield return null;
            foreach(string state in new[]{"walk","jump","nod","wave"}){
                character.PreviewAction(state);yield return new WaitForSecondsRealtime(.35f);
                CaptureDeformation(model,id,state);
                ScreenCapture.CaptureScreenshot(Path.Combine(output,id+"-"+state+".png"));yield return null;
            }
            character.PreviewAction("idle");yield return new WaitForSecondsRealtime(.4f);
            sampledPose=PetSleepPose.Capture(model.transform);
            if(!sampledPose.IsValid){error="Sleep pose cannot bind "+id;break;}
            if(Camera.main!=null)sampledPose.SetViewerDirection(-Camera.main.transform.forward);
            poseState="SleepLoop";poseTime=.25f;yield return null;
            CaptureDeformation(model,id,"sleep");
            ScreenCapture.CaptureScreenshot(Path.Combine(output,id+"-sleep.png"));yield return null;
            sampledPose.PrepareWake();poseState="WakeUp";poseTime=.60f;yield return null;
            CaptureDeformation(model,id,"wake");
            ScreenCapture.CaptureScreenshot(Path.Combine(output,id+"-wake.png"));yield return null;
            sampledPose.Restore();sampledPose=null;poseState=null;Destroy(model);model=null;yield return null;
        }
        sampledPose?.Restore();sampledPose=null;if(model!=null)Destroy(model);Destroy(parent.gameObject);Finish(error??deformationFailure);
    }
    private void CaptureDeformation(GameObject model,string id,string state)
    {
        var measured=PetRuntimeSkinDeformationDiagnostics.Capture(model);
        File.WriteAllText(Path.Combine(output,id+"-"+state+"-deformation.json"),JsonUtility.ToJson(measured,true));
        float p99=measured.materials.Max(item=>item.edgeRatioP99);
        int stretched=measured.materials.Sum(item=>item.stretchedEdges);
        int severe=measured.materials.Sum(item=>item.severeEdges);
        report.AppendLine("  "+state+": edge p99="+p99.ToString("0.000")+"; stretched edges="+stretched+"; severe tears="+severe+"; upper-arm gaps="+measured.openUpperArmSeams);
        if(id!="model-42e48f48384f4e2d" && severe>0 && deformationFailure==null)
            deformationFailure=id+" "+state+" has "+severe+" severely stretched mesh edges.";
        if(id!="model-42e48f48384f4e2d" && measured.openUpperArmSeams>0 && deformationFailure==null)
            deformationFailure=id+" "+state+" has separated upper-arm seams.";
    }
    private void Finish(string error){string text=error==null?"PASSED\n"+report:"FAILED: "+error+"\n"+report;File.WriteAllText(Path.Combine(output,"results.txt"),text);if(error==null)Debug.Log(text);else Debug.LogError(text);Application.Quit(error==null?0:1);}
}
/// <summary>Read-only diagnostics for a currently posed runtime model. Call Capture
/// from an existing QA session; this helper neither samples clips nor changes bones.</summary>
public static class PetRuntimeSkinDeformationDiagnostics
{
    [Serializable] public sealed class Report
    {
        public string model, rigProfile, pose;
        public int openUpperArmSeams;
        public int separatedArmGarmentContacts;
        public float maxUpperArmSeparationMeters;
        public MaterialResult[] materials;
        public BoneResult[] bones;
    }
    [Serializable] public sealed class MaterialResult
    {
        public string renderer, material;
        public int vertices, measuredEdges, stretchedEdges, severeEdges;
        public float meanDisplacementMeters, maxDisplacementMeters;
        public float edgeRatioP95, edgeRatioP99, maxEdgeRatio;
        public Vector3 bindMin, bindMax, posedMin, posedMax;
        public EdgeResult[] worstEdges;
    }
    [Serializable] public sealed class EdgeResult
    {
        public int first, second;
        public float bindLengthMeters, posedLengthMeters, ratio;
        public Vector3 bindFirst, bindSecond, posedFirst, posedSecond;
        public string firstInfluences, secondInfluences;
    }
    [Serializable] public sealed class BoneResult
    {
        public string bone, parent;
        public float worldAngleFromBind, distanceFromBindMeters, segmentLengthChangeMeters;
        public Vector3 bindPosition, currentPosition, localScale;
    }

    public static Report Capture(GameObject model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var baselines = new Dictionary<Transform, Matrix4x4>();
        foreach (SkinnedMeshRenderer skin in skins) {
            if (skin.sharedMesh == null) continue;
            Matrix4x4[] poses = skin.sharedMesh.bindposes;
            for (int i = 0; i < Mathf.Min(skin.bones.Length, poses.Length); i++)
                if (skin.bones[i] != null && !baselines.ContainsKey(skin.bones[i]))
                    baselines.Add(skin.bones[i], skin.transform.localToWorldMatrix * poses[i].inverse);
        }
        var boneResults = new List<BoneResult>();
        foreach (var baseline in baselines) {
            Transform bone = baseline.Key;
            Vector3 rest = baseline.Value.GetColumn(3);
            float lengthChange = 0;
            if (bone.parent != null && baselines.TryGetValue(bone.parent, out Matrix4x4 parent))
                lengthChange = Mathf.Abs(Vector3.Distance(bone.position, bone.parent.position)
                    - Vector3.Distance(rest, (Vector3)parent.GetColumn(3)));
            boneResults.Add(new BoneResult {
                bone = bone.name, parent = bone.parent != null ? bone.parent.name : "",
                worldAngleFromBind = Quaternion.Angle(bone.rotation, baseline.Value.rotation),
                distanceFromBindMeters = Vector3.Distance(bone.position, rest), segmentLengthChangeMeters = lengthChange,
                bindPosition = rest, currentPosition = bone.position, localScale = bone.localScale
            });
        }
        var materialResults = new List<MaterialResult>();
        int upperArmSeams=0,garmentContacts=0;float upperArmSeparation=0;
        foreach (SkinnedMeshRenderer skin in skins) {
            if (skin.sharedMesh == null) continue;
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try {
                // Unity's default stores renderer scale in baked coordinates.
                // Compensate it so bind and baked vertices share local space.
                skin.BakeMesh(baked, true);
                Vector3[] bind = skin.sharedMesh.vertices, current = baked.vertices;
                if (bind.Length != current.Length) throw new InvalidOperationException("BakeMesh vertex count changed: " + skin.name);
                MeasureUpperArmSeams(skin,bind,current,baselines,ref upperArmSeams,ref garmentContacts,ref upperArmSeparation);
                BoneWeight[] weights = skin.sharedMesh.boneWeights;
                Material[] materials = skin.sharedMaterials;
                for (int submesh = 0; submesh < skin.sharedMesh.subMeshCount; submesh++) {
                    int[] triangles = skin.sharedMesh.GetTriangles(submesh);
                    var selected = new HashSet<int>(triangles);
                    var result = new MaterialResult {
                        renderer = skin.name, material = submesh < materials.Length && materials[submesh] != null ? materials[submesh].name : "",
                        vertices = selected.Count
                    };
                    bool first = true;
                    foreach (int index in selected) {
                        Vector3 before = skin.transform.TransformPoint(bind[index]), after = skin.transform.TransformPoint(current[index]);
                        float distance = Vector3.Distance(before, after);
                        result.meanDisplacementMeters += distance;
                        result.maxDisplacementMeters = Mathf.Max(result.maxDisplacementMeters, distance);
                        if (first) { result.bindMin = result.bindMax = before; result.posedMin = result.posedMax = after; first = false; }
                        else {
                            result.bindMin = Vector3.Min(result.bindMin, before); result.bindMax = Vector3.Max(result.bindMax, before);
                            result.posedMin = Vector3.Min(result.posedMin, after); result.posedMax = Vector3.Max(result.posedMax, after);
                        }
                    }
                    result.meanDisplacementMeters /= Mathf.Max(1, selected.Count);
                    var seen = new HashSet<ulong>(); var ratios = new List<float>(); var worst = new List<EdgeResult>();
                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                        for (int edge = 0; edge < 3; edge++) {
                            int a = triangles[i + edge], b = triangles[i + (edge + 1) % 3];
                            int low = Mathf.Min(a, b), high = Mathf.Max(a, b);
                            if (!seen.Add(((ulong)(uint)low << 32) | (uint)high)) continue;
                            float originalLength = skin.transform.TransformVector(bind[a] - bind[b]).magnitude;
                            // Tiny tessellation edges are dominated by float rounding.
                            if (originalLength < 0.0001f) continue;
                            float posedLength = skin.transform.TransformVector(current[a] - current[b]).magnitude;
                            float ratio = posedLength / originalLength;
                            ratios.Add(ratio); result.maxEdgeRatio = Mathf.Max(result.maxEdgeRatio, ratio);
                            if (ratio > 1.35f) result.stretchedEdges++;
                            if (ratio > 8f && posedLength > .05f) result.severeEdges++;
                            if (worst.Count == 12 && ratio <= worst.Min(item => item.ratio)) continue;
                            worst.Add(new EdgeResult {
                                first = a, second = b, bindLengthMeters = originalLength, posedLengthMeters = posedLength, ratio = ratio,
                                bindFirst = skin.transform.TransformPoint(bind[a]), bindSecond = skin.transform.TransformPoint(bind[b]),
                                posedFirst = skin.transform.TransformPoint(current[a]), posedSecond = skin.transform.TransformPoint(current[b]),
                                firstInfluences = Influences(skin, weights, a), secondInfluences = Influences(skin, weights, b)
                            });
                            if (worst.Count > 12) worst.Remove(worst.OrderBy(item => item.ratio).First());
                        }
                    ratios.Sort(); result.measuredEdges = ratios.Count;
                    result.edgeRatioP95 = Percentile(ratios, 0.95f); result.edgeRatioP99 = Percentile(ratios, 0.99f);
                    result.worstEdges = worst.OrderByDescending(item => item.ratio).ToArray();
                    materialResults.Add(result);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
        }
        return new Report {
            model = model.name, rigProfile = model.GetComponent<PetRuntimeModelResources>()?.RigProfile,
            pose = model.GetComponent<PetRuntimePoseRetargeter>()?.DescribePose(),
            openUpperArmSeams=upperArmSeams,maxUpperArmSeparationMeters=upperArmSeparation,
            separatedArmGarmentContacts=garmentContacts,
            materials = materialResults.ToArray(), bones = boneResults.OrderBy(item => item.bone).ToArray()
        };
    }

    private static void MeasureUpperArmSeams(SkinnedMeshRenderer skin,Vector3[] bind,Vector3[] current,
        Dictionary<Transform,Matrix4x4> baselines,ref int open,ref int contacts,ref float maximum)
    {
        if(bind.Length<5000)return;
        var segments=new List<Vector3[]>();
        foreach(string side in new[]{"L","R"}){
            var upper=baselines.Keys.FirstOrDefault(b=>PetRuntimePoseRetargeter.CanonicalName(b.name)=="腕."+side);
            var elbow=baselines.Keys.FirstOrDefault(b=>PetRuntimePoseRetargeter.CanonicalName(b.name)=="ひじ."+side);
            if(upper!=null&&elbow!=null)segments.Add(new[]{(Vector3)baselines[upper].GetColumn(3),(Vector3)baselines[elbow].GetColumn(3)});
        }
        var boneSides=new int[skin.bones.Length];
        for(int b=0;b<boneSides.Length;b++){
            string key=PetRuntimePoseRetargeter.CanonicalName(skin.bones[b].name)??"";
            if(key.StartsWith("肩")||key.StartsWith("腕")||key.StartsWith("ひじ")||key.StartsWith("手首"))
                boneSides[b]=key.EndsWith(".L")?1:key.EndsWith(".R")?2:0;
        }
        var weights=skin.sharedMesh.boneWeights;
        int Side(int vertex){
            BoneWeight w=weights[vertex];float left=0,right=0;
            int[] indices={w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3};float[] strength={w.weight0,w.weight1,w.weight2,w.weight3};
            for(int at=0;at<4;at++)if(indices[at]>=0&&indices[at]<boneSides.Length){if(boneSides[indices[at]]==1)left+=strength[at];else if(boneSides[indices[at]]==2)right+=strength[at];}
            return left>.35f?1:right>.35f?2:0;
        }
        var firstByPosition=new Dictionary<Vector3Int,List<int>>();
        for(int i=0;i<bind.Length;i++){
            Vector3 p=skin.transform.TransformPoint(bind[i]);bool near=false;
            foreach(var segment in segments){
                Vector3 direction=segment[1]-segment[0];
                float t=Vector3.Dot(p-segment[0],direction)/Mathf.Max(.000001f,direction.sqrMagnitude);
                if(t>.08f&&t<.92f&&Vector3.Distance(p,segment[0]+direction*t)<.045f){near=true;break;}
            }
            if(!near)continue;
            var key=new Vector3Int(Mathf.RoundToInt(bind[i].x*1000000),Mathf.RoundToInt(bind[i].y*1000000),Mathf.RoundToInt(bind[i].z*1000000));
            if(!firstByPosition.TryGetValue(key,out var previous)){firstByPosition.Add(key,new List<int>{i});continue;}
            int side=Side(i);
            foreach(int first in previous){
                float distance=skin.transform.TransformVector(current[i]-current[first]).magnitude;
                if(distance<=.015f)continue;
                // The two sides of an intentionally separated sleeve/garment
                // contact move apart. A UV/normal seam within the same arm must
                // remain closed and is checked independently.
                if(side!=0&&Side(first)==side){maximum=Mathf.Max(maximum,distance);open++;}
                else contacts++;
            }
            previous.Add(i);
        }
    }

    private static float Percentile(List<float> values, float fraction) => values.Count == 0 ? 0
        : values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * fraction), 0, values.Count - 1)];
    private static string Influences(SkinnedMeshRenderer skin, BoneWeight[] weights, int vertex)
    {
        if (vertex >= weights.Length) return "";
        BoneWeight value = weights[vertex];
        int[] indices = { value.boneIndex0, value.boneIndex1, value.boneIndex2, value.boneIndex3 };
        float[] amounts = { value.weight0, value.weight1, value.weight2, value.weight3 };
        var result = new List<string>();
        for (int i = 0; i < 4; i++) if (amounts[i] > 0 && indices[i] >= 0 && indices[i] < skin.bones.Length)
            result.Add((skin.bones[indices[i]] != null ? skin.bones[indices[i]].name : "missing") + ":" + amounts[i].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(",", result);
    }
}

