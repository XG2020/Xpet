using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using UnityEngine;

/// <summary>Uses the bundled, animated Lita skeleton as a hidden pose source.
/// Rotation deltas are applied in model space to the imported bind skeleton;
/// retargeting does not require runtime-generated Mecanim assets or an Editor.</summary>
[DefaultExecutionOrder(10)]
public sealed class PetRuntimePoseRetargeter : MonoBehaviour
{
    private Animator sourceAnimator;
    private Transform sourceRoot, targetVisual;
    private Quaternion visualRestInOwner;
    private Vector3 sourceSide = Vector3.right, targetSide = Vector3.right;
    private Vector3 sourceForward = Vector3.back, targetForward = Vector3.back;
    private bool preserveAuthoredIdle;
    private string rigProfile;
    private string lastState = "Idle";
    private readonly List<Binding> bindings = new List<Binding>();
    private readonly List<PositionBinding> positions = new List<PositionBinding>();
    private sealed class Binding
    {
        public Transform source, target, sourceEnd; public Quaternion sourceRest, targetRest;
        public Vector3 sourceRestDirection, targetRestDirection, targetRestLocalPosition, targetRestLocalScale;
        public int depth;
    }
    private sealed class PositionBinding { public Transform source,target; public Vector3 sourceRest,targetRest; public float scale; }
    public int MappedBoneCount => bindings.Count;
    public void ApplyInitialPose() { ApplyPose(); }
    public string DescribePose() => DescribePose(false);
    public string DescribePose(bool includeAllBindings)
    {
        if (sourceRoot == null || targetVisual == null || sourceAnimator == null) return "Runtime retargeter has not been configured.";
        var text=new StringBuilder();
        text.AppendLine("rigProfile="+rigProfile+", rotationBaseline="+(preserveAuthoredIdle?"source Idle relative to authored target bind":"source bind"));
        text.AppendLine("mappedBones="+bindings.Count+", translationBindings="+string.Join(",",positions.Select(p=>p.target.name))
            +", secondarySpringBones="+(GetComponent<PetSecondaryMotion>()?.SimulatedBoneCount ?? 0));
        text.AppendLine("sourceRoot="+sourceRoot.rotation.eulerAngles+", targetRoot="+transform.rotation.eulerAngles+", visual="+targetVisual.localRotation.eulerAngles+", sourceScale="+sourceRoot.lossyScale);
        text.AppendLine("sourceSide="+sourceSide.ToString("F4")+", targetSide="+targetSide.ToString("F4")+", sourceForward="+sourceForward.ToString("F4")+", targetForward="+targetForward.ToString("F4"));
        text.AppendLine("sourceAnimator: enabled="+sourceAnimator.enabled+", active="+sourceAnimator.gameObject.activeInHierarchy+", state="+sourceAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash+", time="+sourceAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime+", clips="+string.Join(",",sourceAnimator.runtimeAnimatorController.animationClips.Select(c=>c.name)));
        string[] main={"センター","下半身","上半身","上半身2","胸","首","頭","肩.L","腕.L","ひじ.L","手首.L","肩.R","腕.R","ひじ.R","手首.R","足.L","ひざ.L","足首.L","足.R","ひざ.R","足首.R"};
        Quaternion visualDelta=targetVisual.rotation*Quaternion.Inverse(transform.rotation*visualRestInOwner);
        foreach(var b in bindings.Where(b=>includeAllBindings || main.Contains(b.target.name)))
        {
            Quaternion now=Quaternion.Inverse(sourceRoot.rotation)*b.source.rotation;
            Quaternion delta=MapRotation(now*Quaternion.Inverse(b.sourceRest));
            string swing="rotation";
            if(b.sourceEnd!=null) {
                Vector3 direction=sourceRoot.InverseTransformDirection(b.sourceEnd.position-b.source.position).normalized;
                delta=preserveAuthoredIdle ? MapRotation(Quaternion.FromToRotation(b.sourceRestDirection,direction))
                    : Quaternion.FromToRotation(b.targetRestDirection,MapDirection(direction));
                swing="segment, srcRestDirection="+b.sourceRestDirection.ToString("F4")+", srcNowDirection="+direction.ToString("F4")+", dstRestDirection="+b.targetRestDirection.ToString("F4");
            }
            text.AppendLine(b.target.name+": source="+b.source.name+", parent="+(b.target.parent!=null?b.target.parent.name:"")
                +", srcRest="+b.sourceRest.eulerAngles+", srcNow="+now.eulerAngles+", appliedDelta="+delta.eulerAngles
                +", appliedAngle="+Quaternion.Angle(Quaternion.identity,delta).ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +", dstRest="+b.targetRest.eulerAngles+", dstNow="+b.target.rotation.eulerAngles
                +", dstAngleFromBind="+Quaternion.Angle(visualDelta*transform.rotation*b.targetRest,b.target.rotation).ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +", srcOwnerPos="+sourceRoot.InverseTransformPoint(b.source.position).ToString("F4")+", dstOwnerPos="+transform.InverseTransformPoint(b.target.position).ToString("F4")
                +", localPositionChange="+(b.target.localPosition-b.targetRestLocalPosition).ToString("F6")
                +", localScale="+b.target.localScale.ToString("F6")+", scaleChange="+(b.target.localScale-b.targetRestLocalScale).ToString("F6")+", "+swing);
        }
        foreach(var p in positions) text.AppendLine("translation "+p.target.name+": sourceOffset="+(p.source.localPosition-p.sourceRest).ToString("F6")+", targetOffset="+(p.target.localPosition-p.targetRest).ToString("F6")+", meterScale="+p.scale.ToString("F6",System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    public void Configure(GameObject template, Transform visual, string profile = null)
    {
        rigProfile = profile ?? "";
        // These GLBs have their own clothing and resting arm silhouette. An
        // MMD bind-to-Idle correction must not become their initial deformation.
        preserveAuthoredIdle = rigProfile == "whale-adult" || rigProfile == "whale-child-basic-v1";
        targetVisual = visual;
        visualRestInOwner=Quaternion.Inverse(transform.rotation)*visual.rotation;
        // Copy transforms only: no clone scripts, renderers, colliders or other
        // runtime owners are instantiated or allowed to execute.
        GameObject proxy = new GameObject("__BundledPoseSource"); proxy.transform.SetParent(transform,false);
        proxy.transform.localPosition = Vector3.zero; proxy.transform.localRotation = Quaternion.identity; proxy.transform.localScale = Vector3.one;
        var copies = new Dictionary<Transform,Transform> { {template.transform,proxy.transform} };
        var needed=new HashSet<Transform>{template.transform};
        foreach(SkinnedMeshRenderer skin in template.transform.Find("VisualModel").GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            foreach(Transform bone in skin.bones.Concat(new[]{skin.transform}))
                for(Transform at=bone;at!=null&&at!=template.transform;at=at.parent) needed.Add(at);
        }
        void CopyChildren(Transform original,Transform parent)
        {
            foreach (Transform child in original)
            {
                if(!needed.Contains(child)) continue;
                Transform copy=new GameObject(child.name).transform;copy.SetParent(parent,false);
                copy.localPosition=child.localPosition;copy.localRotation=child.localRotation;copy.localScale=child.localScale;
                copies.Add(child,copy);CopyChildren(child,copy);
            }
        }
        CopyChildren(template.transform,proxy.transform);
        sourceRoot = proxy.transform; sourceAnimator = proxy.AddComponent<Animator>();
        sourceAnimator.runtimeAnimatorController=template.GetComponent<Animator>()?.runtimeAnimatorController;
        if (sourceAnimator.runtimeAnimatorController == null) throw new InvalidOperationException("内置动作骨架缺失。");
        sourceAnimator.enabled = false;
        Transform sourceVisual = proxy.transform.Find("VisualModel");
        var templateCharacter=template.GetComponent<PetCharacterAnimation>();
        if(templateCharacter!=null) sourceVisual.localRotation=templateCharacter.VisualRestRotation;
        var bindRotations = new Dictionary<Transform,Quaternion>();
        var bindPositions = new Dictionary<Transform,Vector3>();
        foreach (SkinnedMeshRenderer skin in template.transform.Find("VisualModel").GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Matrix4x4[] bindposes = skin.sharedMesh != null ? skin.sharedMesh.bindposes : Array.Empty<Matrix4x4>();
            for (int i=0;i<Mathf.Min(skin.bones.Length,bindposes.Length);i++)
                if (skin.bones[i]!=null && copies.TryGetValue(skin.bones[i],out Transform copy) && !bindRotations.ContainsKey(copy))
                {
                    Matrix4x4 world = copies[skin.transform].localToWorldMatrix * bindposes[i].inverse;
                    bindRotations.Add(copy,world.rotation);
                    bindPositions.Add(copy,world.GetColumn(3));
                }
        }
        // Sampling Idle on the transform-only skeleton is valid at runtime and
        // makes translation baselines independent of the template's current action.
        AnimationClip idle=sourceAnimator.runtimeAnimatorController.animationClips.FirstOrDefault(c=>c!=null&&c.name=="Idle");
        if(idle!=null) idle.SampleAnimation(proxy,0);
        var source = sourceVisual.GetComponentsInChildren<Transform>(true).Select(t => new { key = CanonicalName(t.name), bone = t })
            .Where(t => t.key != null).GroupBy(t => t.key).ToDictionary(g => g.Key,g => g.First().bone);
        var targets = visual.GetComponentsInChildren<Transform>(true);
        // Both loaders face the viewer, but PMX and FBX can disagree on the
        // handedness of their anatomical left/right axis. Keep that reflection
        // when converting directions and rotations; a quaternion alone loses it.
        if (source.TryGetValue("腕.L", out Transform sourceLeft) && source.TryGetValue("腕.R", out Transform sourceRight))
        {
            sourceSide = Vector3.ProjectOnPlane(sourceRoot.InverseTransformDirection(sourceRight.position-sourceLeft.position), Vector3.up).normalized;
            Transform targetLeft = targets.First(t => CanonicalName(t.name)=="腕.L");
            Transform targetRight = targets.First(t => CanonicalName(t.name)=="腕.R");
            targetSide = Vector3.ProjectOnPlane(transform.InverseTransformDirection(targetRight.position-targetLeft.position), Vector3.up).normalized;
            sourceForward = Vector3.Cross(sourceSide, Vector3.up).normalized;
            targetForward = Vector3.Cross(targetSide, Vector3.up).normalized;
            if (Vector3.Dot(sourceForward, Vector3.back)<0) sourceForward=-sourceForward;
            if (Vector3.Dot(targetForward, Vector3.back)<0) targetForward=-targetForward;
        }
        foreach (Transform target in targets)
        {
            string key = CanonicalName(target.name);
            if (key == null || !source.TryGetValue(key, out Transform bone)) continue;
            int depth = 0; for (Transform at=target;at!=null&&at!=visual;at=at.parent) depth++;
            Quaternion rest = preserveAuthoredIdle ? bone.rotation
                : bindRotations.TryGetValue(bone,out var bindRotation) ? bindRotation : bone.rotation;
            var binding=new Binding { source=bone,target=target,sourceRest=Quaternion.Inverse(sourceRoot.rotation)*rest,
                targetRest=Quaternion.Inverse(transform.rotation)*target.rotation,targetRestLocalPosition=target.localPosition,
                targetRestLocalScale=target.localScale,depth=depth };
            // MMD/FBX bind matrices can carry reflections. Quaternion extraction
            // loses their handedness and can raise an otherwise lowered arm.
            // Transfer each arm segment's measured swing directly instead.
            string endKey=key=="腕.L"?"ひじ.L":key=="腕.R"?"ひじ.R":key=="ひじ.L"?"手首.L":key=="ひじ.R"?"手首.R":null;
            Transform targetEnd=endKey!=null?targets.FirstOrDefault(t=>CanonicalName(t.name)==endKey):null;
            if(targetEnd!=null&&source.TryGetValue(endKey,out Transform end))
            {
                binding.sourceEnd=end;
                binding.sourceRestDirection=sourceRoot.InverseTransformDirection(end.position-bone.position).normalized;
                binding.targetRestDirection=transform.InverseTransformDirection(targetEnd.position-target.position).normalized;
            }
            bindings.Add(binding);
        }
        bindings.Sort((a,b)=>a.depth.CompareTo(b.depth));
        var srcHead = source.ContainsKey("頭") ? source["頭"] : null;
        var dstHead = targets.FirstOrDefault(t=>CanonicalName(t.name)=="頭");
        float scale = srcHead != null && dstHead != null ? Mathf.Max(0.1f,dstHead.position.y-transform.position.y)/Mathf.Max(0.1f,srcHead.position.y-sourceRoot.position.y) : 1;
        foreach (string key in new[] { "センター", "上半身", "上半身2", "胸", "頭" })
        {
            // Whole-rig travel is compatible across profiles. Translating
            // individual torso/head pivots changes the imported skin lengths.
            if (preserveAuthoredIdle && key != "センター") continue;
            Transform target = targets.FirstOrDefault(t=>CanonicalName(t.name)==key);
            if (target!=null && source.TryGetValue(key,out Transform bone))
                positions.Add(new PositionBinding { source=bone,target=target,sourceRest=bone.localPosition,targetRest=target.localPosition,scale=scale });
        }
        proxy.SetActive(true); sourceAnimator.enabled = true;
        sourceAnimator.applyRootMotion = false; sourceAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        sourceAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
    }

    public void CrossFade(string state, float duration, float speed)
    {
        if (sourceAnimator == null) return;
        int hash=Animator.StringToHash("Base Layer."+state);
        if (!sourceAnimator.HasState(0,hash)) return;
        sourceAnimator.speed=speed;
        var current=sourceAnimator.GetCurrentAnimatorStateInfo(0);
        bool same=current.fullPathHash==hash || (sourceAnimator.IsInTransition(0) && sourceAnimator.GetNextAnimatorStateInfo(0).fullPathHash==hash);
        if (same && lastState==state) return;
        lastState=state;
        sourceAnimator.CrossFadeInFixedTime(hash,duration,0);
    }
    public void SetPlaybackSpeed(float speed)
    {
        if(sourceAnimator==null) return;
        if(speed<=0 && lastState=="Idle") { sourceAnimator.Play("Base Layer.Idle",0,0); sourceAnimator.Update(0); }
        sourceAnimator.speed=speed;
    }
    private void OnEnable() { if (sourceAnimator != null) { lastState="Idle"; sourceAnimator.Play("Base Layer.Idle",0,0); sourceAnimator.Update(0); ApplyPose(); } }
    private void LateUpdate() { ApplyPose(); }
    private Vector3 MapDirection(Vector3 value) => targetSide*Vector3.Dot(value,sourceSide)
        + Vector3.up*value.y + targetForward*Vector3.Dot(value,sourceForward);
    private Vector3 UnmapDirection(Vector3 value) => sourceSide*Vector3.Dot(value,targetSide)
        + Vector3.up*value.y + sourceForward*Vector3.Dot(value,targetForward);
    private Quaternion MapRotation(Quaternion value) => Quaternion.LookRotation(
        MapDirection(value*UnmapDirection(Vector3.forward)), MapDirection(value*UnmapDirection(Vector3.up)));
    private void ApplyPose()
    {
        if (sourceRoot == null || targetVisual == null) return;
        Quaternion sourceInverse = Quaternion.Inverse(sourceRoot.rotation);
        Quaternion visualDelta=targetVisual.rotation*Quaternion.Inverse(transform.rotation*visualRestInOwner);
        foreach (Binding binding in bindings)
        {
            Quaternion delta=MapRotation(sourceInverse*binding.source.rotation*Quaternion.Inverse(binding.sourceRest));
            if(binding.sourceEnd!=null)
            {
                Vector3 currentDirection=sourceRoot.InverseTransformDirection(binding.sourceEnd.position-binding.source.position).normalized;
                delta=preserveAuthoredIdle
                    ? MapRotation(Quaternion.FromToRotation(binding.sourceRestDirection,currentDirection))
                    : Quaternion.FromToRotation(binding.targetRestDirection,MapDirection(currentDirection));
            }
            binding.target.rotation=visualDelta*transform.rotation*delta*binding.targetRest;
        }
        foreach (PositionBinding binding in positions)
        {
            // Clip translations are in source-bone parent coordinates. Convert that
            // small offset through both parents, preserving imported limb lengths.
            Vector3 offset=binding.source.localPosition-binding.sourceRest;
            Vector3 world=binding.source.parent.TransformVector(offset)*binding.scale;
            world=visualDelta*transform.TransformDirection(MapDirection(sourceRoot.InverseTransformDirection(world)));
            binding.target.localPosition=binding.targetRest+binding.target.parent.InverseTransformVector(world);
        }
    }

    public static string CanonicalName(string original)
    {
        if (string.IsNullOrEmpty(original)) return null;
        string name=original.Substring(original.LastIndexOf(':')+1).Trim();
        string compact=name.Replace("_", "").Replace("-", "").Replace(" ", "");
        var body = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            {"RootM","センター"},{"Root","センター"},{"Hips","下半身"},{"Pelvis","下半身"},{"Spine1M","上半身"},{"Spine2M","上半身2"},
            {"ChestM","胸"},{"NeckM","首"},{"HeadM","頭"},{"Spine","上半身"},{"Spine1","上半身"},{"Spine01","上半身"},
            {"Spine2","上半身2"},{"Spine02","上半身2"},{"Spine3","胸"},{"Spine03","胸"},{"Chest","上半身2"},{"UpperChest","胸"},{"Neck","首"},{"Head","頭"}
        };
        if(body.TryGetValue(compact,out string mapped)) return mapped;
        if(new[]{"センター","腰","下半身","上半身","上半身2","首","頭","胸"}.Contains(name)) return name;
        string side=null;
        bool end=name.EndsWith("_end",StringComparison.OrdinalIgnoreCase)||name.EndsWith("_tip",StringComparison.OrdinalIgnoreCase);
        if(end) name=name.Substring(0,name.LastIndexOf('_'));
        if(name.StartsWith("左",StringComparison.Ordinal)) { side="L";name=name.Substring(1); }
        else if(name.StartsWith("右",StringComparison.Ordinal)) { side="R";name=name.Substring(1); }
        else if(name.EndsWith(".L",StringComparison.OrdinalIgnoreCase)||name.EndsWith("_L",StringComparison.OrdinalIgnoreCase)) {side="L";name=name.Substring(0,name.Length-2);}
        else if(name.EndsWith(".R",StringComparison.OrdinalIgnoreCase)||name.EndsWith("_R",StringComparison.OrdinalIgnoreCase)) {side="R";name=name.Substring(0,name.Length-2);}
        else if(name.StartsWith("Left",StringComparison.OrdinalIgnoreCase)) {side="L";name=name.Substring(4);}
        else if(name.StartsWith("Right",StringComparison.OrdinalIgnoreCase)) {side="R";name=name.Substring(5);}
        else if(name.StartsWith("L_",StringComparison.OrdinalIgnoreCase)||name.StartsWith("L.",StringComparison.OrdinalIgnoreCase)) {side="L";name=name.Substring(2);}
        else if(name.StartsWith("R_",StringComparison.OrdinalIgnoreCase)||name.StartsWith("R.",StringComparison.OrdinalIgnoreCase)) {side="R";name=name.Substring(2);}
        else if(name.EndsWith("_Left",StringComparison.OrdinalIgnoreCase)) {side="L";name=name.Substring(0,name.Length-5);}
        else if(name.EndsWith("_Right",StringComparison.OrdinalIgnoreCase)) {side="R";name=name.Substring(0,name.Length-6);}
        if(side==null) return null;
        name=name.Trim('_','.',' ','-');
        compact=name.Replace("_", "").Replace("-", "").Replace(" ", "");
        var limbs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Scapula","肩"},{"Clavicle","肩"},{"Shoulder","腕"},{"Elbow","ひじ"},{"Wrist","手首"},{"Hip","足"},{"Knee","ひざ"},{"Ankle","足首"},{"Toes","つま先"},
            {"Arm","腕"},{"UpperArm","腕"},{"Forearm","ひじ"},{"LowerArm","ひじ"},{"Hand","手首"},{"Thigh","足"},{"UpperLeg","足"},
            {"Calf","ひざ"},{"Shin","ひざ"},{"LowerLeg","ひざ"},{"Foot","足首"},{"UpLeg","足"},{"Leg","ひざ"},{"Toe","つま先"},{"ToeBase","つま先"},{"Eye","目"}
        };
        if(limbs.TryGetValue(compact,out mapped)) name=mapped;
        name=name.Replace("肘","ひじ").Replace("膝","ひざ").Replace("腕捻","腕捩").Replace("手捻","手捩");
        Match finger=Regex.Match(compact,"^(Thumb|Index|Middle|Ring|Pinky)(?:Finger)?([1-3])$",RegexOptions.IgnoreCase);
        if(finger.Success)
        {
            string[] english={"thumb","index","middle","ring","pinky"}, japanese={"親指","人指","中指","薬指","小指"};
            int digit=Array.IndexOf(english,finger.Groups[1].Value.ToLowerInvariant());int joint=int.Parse(finger.Groups[2].Value)-(digit==0?1:0);
            name=japanese[digit]+(char)('０'+joint);
        }
        name=name.Replace("人差指","人指").Replace("人差し指","人指");
        if(name.StartsWith("親指")||name.StartsWith("人指")||name.StartsWith("中指")||name.StartsWith("薬指")||name.StartsWith("小指"))
            name=Regex.Replace(name,"[0-3]",m=>((char)('０'+int.Parse(m.Value))).ToString());
        return name+"."+side+(end?"_end":"");
    }
}
