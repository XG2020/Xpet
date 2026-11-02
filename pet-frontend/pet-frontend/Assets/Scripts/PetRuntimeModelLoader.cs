using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Loads the portable backend mesh format. File parsing and vertex
/// remapping run on a worker; Unity resources are created in separate frames.</summary>
public static class PetRuntimeModelLoader
{
    private sealed class MaterialData { public string name, texture; public Color color; public bool doubleSided; public int count; public byte[] image; }
    private sealed class BoneData { public string name; public int parent; public Vector3 position; public Quaternion rotation; }
    private sealed class MorphData { public string name; public int[] indices; public Vector3[] offsets; }
    private sealed class PartData
    {
        public Vector3[] vertices, normals; public Vector2[] uv; public BoneWeight[] weights;
        public int[] triangles, bones; public MaterialData material; public List<MorphData> morphs = new List<MorphData>();
    }
    [Serializable]
    private sealed class ModelManifest
    {
        public string source_type, rig_source, rig_profile;
        public float target_height_m;
        public float[] mesh_forward;
        public string[] adaptation_limitations;
    }
    private sealed class ModelData
    {
        public BoneData[] bones; public Vector3 meshForward; public bool sourceInMeters;
        public float targetHeight; public string rigSource, rigProfile; public string[] adaptationLimitations;
        public List<PartData> parts = new List<PartData>();
    }

    public static IEnumerator CreateModel(string path, string label, Transform parent, GameObject poseTemplate, Action<GameObject> completed)
    {
        var task = Task.Run(() => Read(path));
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted) throw new InvalidOperationException("模型读取失败：" + task.Exception.GetBaseException().Message);
        ModelData data = task.Result;
        GameObject root = new GameObject(label); root.SetActive(false); root.transform.SetParent(parent, false);
        var owned = root.AddComponent<PetRuntimeModelResources>();
        owned.RigSource = data.rigSource; owned.RigProfile = data.rigProfile;
        owned.AdaptationLimitations = data.adaptationLimitations ?? Array.Empty<string>();
        bool delivered = false;
        try
        {
            Transform visual = new GameObject("VisualModel").transform; visual.SetParent(root.transform, false);
            var transforms = new Transform[data.bones.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i] = new GameObject(PetRuntimePoseRetargeter.CanonicalName(data.bones[i].name) ?? data.bones[i].name).transform;
                transforms[i].SetParent(visual, false);
                transforms[i].position = visual.TransformPoint(data.bones[i].position);
                transforms[i].rotation = visual.rotation * data.bones[i].rotation;
                if (i % 100 == 0) yield return null;
            }
            for (int i = 0; i < transforms.Length; i++) if (data.bones[i].parent >= 0) transforms[i].SetParent(transforms[data.bones[i].parent], true);
            var body = transforms.GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            string[] required = { "頭", "腕.L", "ひじ.L", "手首.L", "腕.R", "ひじ.R", "手首.R", "足.L", "ひざ.L", "足首.L", "足.R", "ひざ.R", "足首.R" };
            string[] missing = required.Where(name => !body.ContainsKey(name)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("目前支持可识别的人形骨骼，模型缺少：" + string.Join("、", missing));

            var skins = new List<SkinnedMeshRenderer>();
            var textures = new Dictionary<string,Texture2D>(StringComparer.OrdinalIgnoreCase);
            Shader shader = poseTemplate.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && m.shader != null && m.shader.name == "Desktop Pet/Soft Anime").Select(m => m.shader).FirstOrDefault()
                ?? Shader.Find("Desktop Pet/Soft Anime") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("发行包缺少模型渲染着色器。");
            foreach (PartData part in data.parts)
            {
                var item = new GameObject(IsWeapon(part.material.name) ? "Weapon" : part.material.name);
                item.transform.SetParent(visual, false);
                var skin = item.AddComponent<SkinnedMeshRenderer>();
                var mesh = new Mesh { name = label + " · " + part.material.name, indexFormat = IndexFormat.UInt32 };
                owned.Add(mesh);
                mesh.vertices = part.vertices; mesh.normals = part.normals; mesh.uv = part.uv; mesh.boneWeights = part.weights;
                skin.bones = part.bones.Select(index => transforms[index]).ToArray();
                mesh.bindposes = skin.bones.Select(b => b.worldToLocalMatrix * item.transform.localToWorldMatrix).ToArray();
                mesh.triangles = part.triangles;
                yield return null;
                foreach (MorphData morph in part.morphs)
                {
                    var offsets = new Vector3[part.vertices.Length];
                    for (int i = 0; i < morph.indices.Length; i++) offsets[morph.indices[i]] = morph.offsets[i];
                    mesh.AddBlendShapeFrame(morph.name, 100, offsets, null, null);
                    yield return null;
                }
                mesh.RecalculateBounds();
                var material = new Material(shader) { name = part.material.name };
                owned.Add(material);
                bool helper = IsHelper(part.material.name);
                Color tint = part.material.image != null ? Color.white : part.material.color;
                if (helper) tint.a = 0;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
                if (material.HasProperty("_Cull")) material.SetFloat("_Cull", part.material.doubleSided ? 0 : 2);
                if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0.12f);
                // Game face textures can use alpha as a packed shading mask.
                bool face=part.material.name.IndexOf("face",StringComparison.OrdinalIgnoreCase)>=0||part.material.name.Contains("顔")||part.material.name.Contains("脸");
                if (face && !helper && material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip",0);
                if (material.HasProperty("_ShadeColor")) material.SetColor("_ShadeColor", new Color(0.71f,0.67f,0.72f,1));
                if (part.material.image != null)
                {
                    if(!textures.TryGetValue(part.material.texture,out var texture))
                    {
                        texture = new Texture2D(2,2,TextureFormat.RGBA32, true, false) { name = part.material.texture };
                        owned.Add(texture);
                        if (!ImageConversion.LoadImage(texture,part.material.image,true)) throw new InvalidOperationException("无法解码贴图："+part.material.texture);
                        texture.wrapMode = TextureWrapMode.Repeat;
                        textures.Add(part.material.texture,texture);
                    }
                    if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap",texture);
                    else material.mainTexture = texture;
                    part.material.image=null;
                }
                skin.sharedMesh = mesh; skin.sharedMaterial = material; skin.rootBone = transforms.FirstOrDefault(t => t.name == "センター") ?? transforms[0];
                skin.updateWhenOffscreen = true; skin.shadowCastingMode = ShadowCastingMode.Off; skin.receiveShadows = false; skin.enabled = !helper;
                skins.Add(skin);
                yield return null;
            }
            // Normalize using the body column, excluding remote weapons and decorations.
            Vector3 anatomicalRight = body["腕.R"].position - body["腕.L"].position;
            Vector3 forward = data.meshForward.sqrMagnitude>0.5f ? visual.TransformDirection(data.meshForward) : Vector3.Cross(anatomicalRight, Vector3.up); forward.y = 0;
            if (forward.sqrMagnitude > 1e-8f) visual.rotation = Quaternion.FromToRotation(forward.normalized, Vector3.back) * visual.rotation;
            float ankles = (body["足首.L"].position.y + body["足首.R"].position.y) * 0.5f;
            float skeletonHeight = body["頭"].position.y - ankles;
            if (skeletonHeight <= 1e-6f) throw new InvalidOperationException("骨骼坐标不是站立人形，无法确定人物高度。");
            var soles = new List<float>(); var crowns = new List<float>(); var headSurface = new List<float>();
            float sourceBottom = float.PositiveInfinity, sourceTop = float.NegativeInfinity;
            float width = Vector3.Distance(body["腕.L"].position,body["腕.R"].position);
            foreach (PartData part in data.parts)
            {
                if (IsWeapon(part.material.name) || part.material.color.a < 0.01f || IsHelper(part.material.name)) continue;
                bool decoration = IsDecoration(part.material.name);
                bool anatomicalHead = !decoration && (IsFace(part.material.name) || IsHair(part.material.name));
                foreach (Vector3 p in part.vertices)
                {
                    Vector3 point = visual.TransformPoint(p);
                    sourceBottom = Mathf.Min(sourceBottom, point.y); sourceTop = Mathf.Max(sourceTop, point.y);
                    if (decoration) continue;
                    if (Mathf.Abs(point.x)>Mathf.Max(width,skeletonHeight*0.3f) || Mathf.Abs(point.z)>skeletonHeight*0.35f) continue;
                    if (point.y>ankles-skeletonHeight*0.18f && point.y<ankles+skeletonHeight*0.05f) soles.Add(point.y);
                    if (point.y>=body["頭"].position.y && point.y<body["頭"].position.y+skeletonHeight*0.5f) {
                        crowns.Add(point.y);
                        if (anatomicalHead) headSurface.Add(point.y);
                    }
                }
            }
            soles.Sort(); crowns.Sort();
            float bottom = soles.Count>4 ? soles[Mathf.Clamp(Mathf.RoundToInt((soles.Count-1)*0.01f),0,soles.Count-1)] : ankles-skeletonHeight*0.035f;
            // Hair/face material columns identify the crown without counting a
            // PMX halo or wings. Unknown material sets retain the body fallback.
            float top = headSurface.Count>4 ? headSurface.Max()
                : crowns.Count>4 ? crowns[Mathf.Clamp(Mathf.RoundToInt((crowns.Count-1)*0.985f),0,crowns.Count-1)] : ankles+skeletonHeight*1.15f;
            if (data.sourceInMeters) { bottom = sourceBottom; top = sourceTop; }
            if (float.IsInfinity(bottom) || float.IsInfinity(top) || top-bottom<=1e-6f) throw new InvalidDataException("模型主体高度无效。");
            float height = data.targetHeight>0 ? data.targetHeight : top-bottom;
            if (!IsTargetHeight(height)) throw new InvalidDataException("模型目标身高必须在0.2到4米之间。");
            // glTF uses meters. Preserve authored limb lengths and adult/child
            // scale; the manifest supplies the collider/interaction height.
            float scale = data.sourceInMeters ? 1 : height / (top-bottom);
            owned.TargetHeightMeters = height; owned.SourceHeight = top-bottom;
            Vector3 feet = (body["足首.L"].position+body["足首.R"].position)*0.5f;
            visual.localPosition = -new Vector3(feet.x,bottom,feet.z)*scale; visual.localScale = Vector3.one*scale;
            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = poseTemplate.GetComponent<Animator>().runtimeAnimatorController;
            animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var retargeter = root.AddComponent<PetRuntimePoseRetargeter>();
            retargeter.Configure(poseTemplate, visual, data.rigProfile);
            retargeter.ApplyInitialPose();
            // A generic source rig can have a different bind-axis convention.
            // Verify the skinned facial surface after the initial retarget, and
            // align the whole actor once, preserving the imported skeleton.
            Vector3 posedForward=MeasureFaceForward(root);
            if(posedForward.sqrMagnitude>0.5f)
            {
                Vector3 worldForward=posedForward;
                root.transform.rotation=Quaternion.FromToRotation(worldForward,Vector3.back)*root.transform.rotation;
            }
            var character = root.AddComponent<PetCharacterAnimation>(); character.Configure(animator, skins.ToArray());
            root.AddComponent<PetWeaponAttachment>();
            root.AddComponent<PetSecondaryMotion>().Configure(visual,character);
            var collider = root.AddComponent<CapsuleCollider>(); collider.height = height;
            collider.radius = Mathf.Clamp(width*scale*0.5f,0.16f,0.42f); collider.center = new Vector3(0,height*0.5f,0); collider.isTrigger = true;
            completed(root); delivered = true;
        }
        finally { if (!delivered && root != null) UnityEngine.Object.Destroy(root); }
    }

    private static bool IsWeapon(string name) => name.IndexOf("_Mat_Weapon",StringComparison.OrdinalIgnoreCase)>=0;
    private static bool IsFace(string name) => name.IndexOf("face",StringComparison.OrdinalIgnoreCase)>=0||name.Contains("顔")||name.Contains("脸");
    private static bool IsHair(string name) => name.IndexOf("hair",StringComparison.OrdinalIgnoreCase)>=0||name.Contains("髪")||name.Contains("头发");
    private static bool IsHelper(string name) => name.IndexOf("eyeshadow",StringComparison.OrdinalIgnoreCase)>=0;
    private static bool IsDecoration(string name) => name.IndexOf("halo",StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf("wing",StringComparison.OrdinalIgnoreCase)>=0
        ||name.Contains("光环")||name.Contains("光輪")||name.Contains("头环")||name.Contains("翼")||name.Contains("翅膀");
    private static bool IsTargetHeight(float height) => !float.IsNaN(height)&&!float.IsInfinity(height)&&height>=0.2f&&height<=4;
    private static ModelManifest ReadManifest(string folder, string filename)
    {
        string path = Path.Combine(folder, filename);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length>128*1024) throw new InvalidDataException("模型说明文件过大："+filename);
        try { return JsonUtility.FromJson<ModelManifest>(File.ReadAllText(path, Encoding.UTF8)); }
        catch (ArgumentException error) { throw new InvalidDataException("模型说明文件无效："+filename, error); }
    }
    private static void ReadMetadata(string path, ModelData data)
    {
        string folder = Path.GetDirectoryName(path);
        ModelManifest manifest = ReadManifest(folder, "model.json"), orientation = ReadManifest(folder, "orientation.json");
        string sourceType = !string.IsNullOrEmpty(manifest?.source_type) ? manifest.source_type : orientation?.source_type;
        data.sourceInMeters = string.Equals(sourceType, "glb", StringComparison.OrdinalIgnoreCase) || string.Equals(sourceType, "gltf", StringComparison.OrdinalIgnoreCase);
        float target = manifest != null && manifest.target_height_m!=0 ? manifest.target_height_m : orientation != null ? orientation.target_height_m : 0;
        if (target!=0 && !IsTargetHeight(target)) throw new InvalidDataException("模型目标身高必须在0.2到4米之间。");
        data.targetHeight = target!=0 ? target : data.sourceInMeters ? 0 : 1.72f;
        data.rigSource = !string.IsNullOrEmpty(manifest?.rig_source) ? manifest.rig_source : orientation?.rig_source;
        data.rigProfile = !string.IsNullOrEmpty(manifest?.rig_profile) ? manifest.rig_profile : orientation?.rig_profile;
        data.adaptationLimitations = manifest?.adaptation_limitations ?? orientation?.adaptation_limitations;
        float[] forward = orientation?.mesh_forward ?? manifest?.mesh_forward;
        if (forward!=null && forward.Length==3 && forward.All(value=>!float.IsNaN(value)&&!float.IsInfinity(value)&&Mathf.Abs(value)<=1.001f)) {
            Vector3 direction = new Vector3(forward[0],0,forward[2]);
            if (direction.sqrMagnitude>0.5f) data.meshForward = direction.normalized;
        }
    }
    public static Vector3 MeasureFaceForward(GameObject model)
    {
        Transform visual=model.transform.Find("VisualModel");
        if(visual==null) return Vector3.zero;
        foreach(SkinnedMeshRenderer skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if(!skin.sharedMaterials.Any(m=>m!=null&&IsFace(m.name))) continue;
            var baked=new Mesh();
            try
            {
                skin.BakeMesh(baked);
                Vector3 sum=Vector3.zero;Vector3[] normals=baked.normals;
                foreach(Vector3 normal in normals)
                {
                    Vector3 worldNormal=skin.transform.TransformDirection(normal);
                    if(worldNormal.sqrMagnitude>1e-10f) sum+=worldNormal.normalized;
                }
                if(normals.Length>0) sum/=normals.Length;
                sum.y=0;
                if(sum.sqrMagnitude>0.0225f) return sum.normalized;
            }
            finally { UnityEngine.Object.Destroy(baked); }
        }
        return Vector3.zero;
    }
    private static int Count(BinaryReader r, int maximum) { int value = r.ReadInt32(); if(value<0 || value>maximum) throw new InvalidDataException("模型资源数量超限。"); return value; }
    private static string Text(BinaryReader r) { int length=Count(r,1048576); byte[] bytes=r.ReadBytes(length); if(bytes.Length!=length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); }
    private static float Float(BinaryReader r) { float value=r.ReadSingle(); if(float.IsNaN(value)||float.IsInfinity(value)||Math.Abs(value)>1e8) throw new InvalidDataException("模型坐标无效。"); return value; }
    private static Vector3 V3(BinaryReader r) => new Vector3(Float(r),Float(r),Float(r));
    private static ModelData Read(string path)
    {
        if(new FileInfo(path).Length>256L*1024*1024) throw new InvalidDataException("模型缓存不能超过256 MB。");
        using(var r=new BinaryReader(File.OpenRead(path)))
        {
            if(Encoding.ASCII.GetString(r.ReadBytes(8))!="PETRAW1\0") throw new InvalidDataException("模型缓存版本不兼容。");
            int count=Count(r,600000);
            var vertices=new Vector3[count]; var normals=new Vector3[count]; var uv=new Vector2[count]; var weights=new BoneWeight[count];
            for(int i=0;i<count;i++)
            {
                vertices[i]=V3(r); normals[i]=V3(r); uv[i]=new Vector2(Float(r),Float(r));
                weights[i]=new BoneWeight { boneIndex0=r.ReadInt32(),boneIndex1=r.ReadInt32(),boneIndex2=r.ReadInt32(),boneIndex3=r.ReadInt32(),weight0=Float(r),weight1=Float(r),weight2=Float(r),weight3=Float(r) };
            }
            int[] indices=new int[Count(r,3000000)];
            for(int i=0;i<indices.Length;i++) { indices[i]=r.ReadInt32(); if(indices[i]<0||indices[i]>=count) throw new InvalidDataException("模型网格索引无效。"); }
            var materials=new MaterialData[Count(r,4096)]; int total=0;
            var textureBytes=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<materials.Length;i++)
            {
                var material=new MaterialData { name=Text(r),color=new Color(Float(r),Float(r),Float(r),Float(r)),doubleSided=r.ReadByte()!=0,texture=Text(r),count=Count(r,3000000) };
                if(material.count%3!=0) throw new InvalidDataException("模型面索引无效。"); total+=material.count;
                if(!string.IsNullOrEmpty(material.texture))
                {
                    string folder=Path.GetDirectoryName(path); string image=Path.GetFullPath(Path.Combine(folder,material.texture));
                    if(!image.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                        || (Path.GetExtension(image)!=".png"&&Path.GetExtension(image)!=".jpg"&&Path.GetExtension(image)!=".jpeg")) throw new InvalidDataException("模型贴图路径无效。");
                    if(new FileInfo(image).Length>64L*1024*1024) throw new InvalidDataException("模型贴图过大。");
                    if(!textureBytes.TryGetValue(image,out material.image)) { material.image=File.ReadAllBytes(image);textureBytes.Add(image,material.image); }
                }
                materials[i]=material;
            }
            if(total!=indices.Length) throw new InvalidDataException("模型材质与网格不一致。");
            var data=new ModelData { bones=new BoneData[Count(r,4096)] };
            ReadMetadata(path, data);
            for(int i=0;i<data.bones.Length;i++) data.bones[i]=new BoneData { name=Text(r),parent=r.ReadInt32(),position=V3(r),rotation=new Quaternion(Float(r),Float(r),Float(r),Float(r)) };
            for(int i=0;i<data.bones.Length;i++)
            {
                int parent=data.bones[i].parent; if(parent < -1) throw new InvalidDataException("模型骨骼父级无效。"); var seen=new HashSet<int>{i};
                while(parent>=0) { if(parent>=data.bones.Length||!seen.Add(parent)) throw new InvalidDataException("模型骨骼层级无效。"); parent=data.bones[parent].parent; }
            }
            for(int i=0;i<weights.Length;i++)
            {
                BoneWeight w=weights[i]; int[] bi={w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3}; float[] bw={w.weight0,w.weight1,w.weight2,w.weight3};
                float sum=0;
                for(int j=0;j<4;j++) { if(bw[j]<0||bw[j]>1.001f||(bw[j]>0&&(bi[j]<0||bi[j]>=data.bones.Length))) throw new InvalidDataException("模型骨骼权重无效。"); sum+=bw[j]; }
                if(Math.Abs(sum-1)>0.02f) throw new InvalidDataException("模型骨骼权重未归一化。");
            }
            var morphs=new List<MorphData>(); int morphValues=0;
            for(int i=0,n=Count(r,1024);i<n;i++)
            {
                string name=Text(r); int nValues=Count(r,4000000); morphValues+=nValues;
                if(morphValues>4000000) throw new InvalidDataException("模型表情数量超限。");
                var morph=new MorphData { name=name,indices=new int[nValues],offsets=new Vector3[nValues] };
                for(int j=0;j<nValues;j++) { morph.indices[j]=r.ReadInt32(); morph.offsets[j]=V3(r); if(morph.indices[j]<0||morph.indices[j]>=count) throw new InvalidDataException("模型表情索引无效。"); }
                morphs.Add(morph);
            }
            int start=0;
            foreach(MaterialData material in materials)
            {
                var remap=new Dictionary<int,int>(); var bones=new Dictionary<int,int>(); var selected=new List<int>(); var tris=new int[material.count];
                for(int i=0;i<material.count;i++) { int old=indices[start+i]; if(!remap.TryGetValue(old,out int next)) { next=remap.Count; remap.Add(old,next); selected.Add(old); } tris[i]=next; }
                start+=material.count;
                if(selected.Count==0) continue;
                var part=new PartData { vertices=selected.Select(i=>vertices[i]).ToArray(),normals=selected.Select(i=>normals[i]).ToArray(),uv=selected.Select(i=>uv[i]).ToArray(),weights=new BoneWeight[selected.Count],triangles=tris,material=material };
                for(int i=0;i<selected.Count;i++)
                {
                    BoneWeight w=weights[selected[i]];
                    int Map(int b,float weight) { if(weight<=0) return 0; if(!bones.TryGetValue(b,out int mapped)) { mapped=bones.Count; bones.Add(b,mapped); } return mapped; }
                    w.boneIndex0=Map(w.boneIndex0,w.weight0);w.boneIndex1=Map(w.boneIndex1,w.weight1);w.boneIndex2=Map(w.boneIndex2,w.weight2);w.boneIndex3=Map(w.boneIndex3,w.weight3);part.weights[i]=w;
                }
                part.bones=bones.OrderBy(k=>k.Value).Select(k=>k.Key).ToArray();
                foreach(MorphData source in morphs)
                {
                    var mi=new List<int>(); var offsets=new List<Vector3>();
                    for(int i=0;i<source.indices.Length;i++) if(remap.TryGetValue(source.indices[i],out int target)) { mi.Add(target); offsets.Add(source.offsets[i]); }
                    if(mi.Count>0) part.morphs.Add(new MorphData { name=source.name,indices=mi.ToArray(),offsets=offsets.ToArray() });
                }
                data.parts.Add(part);
                if(data.meshForward.sqrMagnitude<0.5f&&IsFace(material.name))
                {
                    Vector3 sum=Vector3.zero;foreach(Vector3 normal in part.normals) sum+=normal;
                    sum/=part.normals.Length;sum.y=0;
                    if(sum.sqrMagnitude>0.0225f) data.meshForward=sum.normalized;
                }
            }
            return data;
        }
    }
}

/// <summary>Runtime meshes, textures and materials are not assets: release them
/// when the imported model is removed, without a whole-project unused-asset scan.</summary>
public sealed class PetRuntimeModelResources : MonoBehaviour
{
    private readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();
    public float TargetHeightMeters { get; internal set; }
    public float SourceHeight { get; internal set; }
    public string RigSource { get; internal set; }
    public string RigProfile { get; internal set; }
    public string[] AdaptationLimitations { get; internal set; } = Array.Empty<string>();
    public void Add(UnityEngine.Object resource) { resources.Add(resource); }
    private void OnDestroy() { foreach (UnityEngine.Object resource in resources) if (resource != null) Destroy(resource); resources.Clear(); }
}
