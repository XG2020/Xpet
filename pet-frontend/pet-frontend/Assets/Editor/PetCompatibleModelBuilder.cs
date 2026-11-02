using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Creates desktop-pet assets without changing the source FBX or PMX.</summary>
public static class PetCompatibleModelBuilder
{
    private const float BodyHeight = 1.72f;
    [Serializable] private sealed class MaterialMap { public string source; public MaterialEntry[] materials; }
    [Serializable] private sealed class MaterialEntry
    {
        public int index;
        public int sourceMaterialIndex;
        public string name;
        public string texture;
        public float[] color;
        public bool doubleSided;
    }

    public static string Build(GameObject imported, string modelId, string label, string folder)
    {
        if (imported == null) throw new ArgumentNullException(nameof(imported));
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before importing a pet.");
        folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
        if (!folder.StartsWith("Assets/ImportedPets/", StringComparison.Ordinal) || folder.Contains(".."))
            throw new ArgumentException("The generated pet folder must be under Assets/ImportedPets.", nameof(folder));
        EnsureFolder(folder);
        string generated = folder + "/PetGenerated";
        EnsureFolder(generated);
        string prefabPath = folder + "/DesktopPet.prefab";
        GameObject root = new GameObject(string.IsNullOrWhiteSpace(label) ? modelId : label);
        try
        {
            GameObject model = UnityEngine.Object.Instantiate(imported);
            if (PrefabUtility.IsPartOfPrefabInstance(model))
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.name = "VisualModel";
            model.transform.SetParent(root.transform, false);
            StripHelpers(model);
            Dictionary<string, Transform> bones = Canonicalize(model);
            FaceCamera(model.transform, bones);
            SkinnedMeshRenderer[] skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(skin => skin.enabled && skin.sharedMesh != null && skin.sharedMesh.vertexCount > 0).ToArray();
            if (skins.Length == 0) throw new InvalidOperationException("The imported character has no skinned mesh.");
            MaterialMap map = ReadMaterialMap(folder);
            ConvertMaterials(model, skins, map, folder, generated);
            Bounds body = MeasureBody(root.transform, bones, skins);
            if (body.size.y < 0.001f || !Finite(body.size.y)) throw new InvalidOperationException("The skeleton has no usable body height.");
            float scale = BodyHeight / body.size.y;
            model.transform.localScale *= scale;
            model.transform.localPosition = (model.transform.localPosition - new Vector3(body.center.x, body.min.y, body.center.z)) * scale;

            Animator animator = root.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = PetModelSetup.CreateCompatibleAnimations(root.transform, generated, SafeName(modelId));
            PetCharacterAnimation character = root.AddComponent<PetCharacterAnimation>();
            character.Configure(animator, skins);
            root.AddComponent<PetSecondaryMotion>().Configure(model.transform, character);
            CapsuleCollider hitbox = root.AddComponent<CapsuleCollider>();
            hitbox.height = BodyHeight;
            hitbox.radius = Mathf.Clamp(body.size.x * scale * 0.5f, 0.16f, 0.42f);
            hitbox.center = new Vector3(0, BodyHeight * 0.5f, 0);
            hitbox.isTrigger = true;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            if (saved == null) throw new InvalidOperationException("Unity could not save the imported pet prefab.");
            AssetDatabase.SaveAssets();
            Debug.Log($"DESKTOP_PET_IMPORTED {modelId}: {skins.Length} mesh parts, {BodyHeight:F2}m body, sixteen actions; {prefabPath}");
            return prefabPath;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static void StripHelpers(GameObject model)
    {
        foreach (UniversalAdditionalCameraData item in model.GetComponentsInChildren<UniversalAdditionalCameraData>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (UniversalAdditionalLightData item in model.GetComponentsInChildren<UniversalAdditionalLightData>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Camera item in model.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Light item in model.GetComponentsInChildren<Light>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (AudioListener item in model.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Animator item in model.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Animation item in model.GetComponentsInChildren<Animation>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Joint item in model.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Rigidbody item in model.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(item);
        foreach (Collider item in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(item);
        // Only imported debug groups are hidden. Small faces, eyelashes, clothing
        // and rigid accessories remain visible regardless of their vertex counts.
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            bool helper = false;
            for (Transform t = renderer.transform; t != null && t != model.transform; t = t.parent)
            {
                string name = t.name.ToLowerInvariant();
                if (name == "rigidbodies" || name == "joints" || name == "rigid bodies" || name.StartsWith("mmd_rigid")) { helper = true; break; }
            }
            if (helper) renderer.enabled = false;
            else
            {
                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                if (renderer is SkinnedMeshRenderer skin) skin.updateWhenOffscreen = true;
            }
        }
    }

    private static Dictionary<string, Transform> Canonicalize(GameObject model)
    {
        Transform[] transforms = model.GetComponentsInChildren<Transform>(true);
        var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (Transform bone in transforms)
        {
            string canonical = CanonicalName(bone.name);
            if (string.IsNullOrEmpty(canonical)) continue;
            if (bones.TryGetValue(canonical, out Transform existing) && existing != bone)
            {
                // Prefer an already canonical node; never alias FK and D chains
                // to one Transform, which would animate a weighted leg twice.
                if (bone.name != canonical) continue;
                existing.name = existing.name + "_ImportedAux";
            }
            bone.name = canonical;
            bones[canonical] = bone;
        }
        foreach (string side in new[] { "L", "R" })
            foreach (string name in new[] { "足", "ひざ", "足首" })
                if (!bones.ContainsKey(name + "." + side) && bones.TryGetValue(name + "D." + side, out Transform deform))
                {
                    bones.Remove(name + "D." + side);
                    deform.name = name + "." + side;
                    bones.Add(deform.name, deform);
                }
        if (!bones.ContainsKey("センター"))
        {
            // A wrapper permits skeletal translation without moving the prefab's
            // desktop anchor, and retains all original skin/bind-pose references.
            Transform skeleton = bones.TryGetValue("下半身", out Transform pelvis) ? pelvis : null;
            if (skeleton == null && bones.TryGetValue("上半身", out Transform spine)) skeleton = spine.parent;
            if (skeleton == null) throw new InvalidOperationException("No pelvis/center bone was found in the imported character.");
            Transform center = new GameObject("センター").transform;
            center.SetParent(skeleton.parent, false);
            center.position = skeleton.position;
            skeleton.SetParent(center, true);
            bones.Add(center.name, center);
        }
        string[] required = { "頭", "腕.L", "ひじ.L", "手首.L", "腕.R", "ひじ.R", "手首.R", "足.L", "ひざ.L", "足首.L", "足.R", "ひざ.R", "足首.R" };
        string[] missing = required.Where(name => !bones.ContainsKey(name)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("Unsupported character skeleton; missing bones: " + string.Join(", ", missing));
        return bones;
    }

    private static string CanonicalName(string original)
    {
        string name = original.Substring(original.LastIndexOf(':') + 1);
        var body = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Root_M", "センター" }, { "Hips", "下半身" }, { "Pelvis", "下半身" }, { "Spine1_M", "上半身" },
            { "Spine2_M", "上半身2" }, { "Chest_M", "胸" }, { "Neck_M", "首" }, { "Head_M", "頭" },
            { "Spine", "上半身" }, { "Chest", "上半身2" }, { "UpperChest", "胸" }, { "Neck", "首" }, { "Head", "頭" }
        };
        if (body.TryGetValue(name, out string mapped)) return mapped;
        if (new[] { "センター", "腰", "下半身", "上半身", "上半身2", "首", "頭", "胸" }.Contains(name)) return name;
        string side = null;
        bool end = name.EndsWith("_end", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_tip", StringComparison.OrdinalIgnoreCase);
        if (end) name = name.Substring(0, name.LastIndexOf('_'));
        if (name.StartsWith("左", StringComparison.Ordinal)) { side = "L"; name = name.Substring(1); }
        else if (name.StartsWith("右", StringComparison.Ordinal)) { side = "R"; name = name.Substring(1); }
        else if (name.EndsWith(".L", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
        { side = "L"; name = name.Substring(0, name.Length - 2); }
        else if (name.EndsWith(".R", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
        { side = "R"; name = name.Substring(0, name.Length - 2); }
        if (side == null) return null;
        var limbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Scapula", "肩" }, { "Shoulder", "腕" }, { "Elbow", "ひじ" }, { "Wrist", "手首" },
            { "Hip", "足" }, { "Knee", "ひざ" }, { "Ankle", "足首" }, { "Toes", "つま先" },
            { "Arm", "腕" }, { "Forearm", "ひじ" }, { "Hand", "手首" }, { "Thigh", "足" }, { "Calf", "ひざ" }, { "Foot", "足首" }
        };
        if (limbs.TryGetValue(name, out mapped)) name = mapped;
        if (name == "肘") name = "ひじ";
        if (name == "膝") name = "ひざ";
        if (name == "腕捻") name = "腕捩";
        if (name == "手捻") name = "手捩";
        Match finger = Regex.Match(name, "^(Thumb|Index|Middle|Ring|Pinky)(?:Finger)?([1-3])$", RegexOptions.IgnoreCase);
        if (finger.Success)
        {
            string[] english = { "thumb", "index", "middle", "ring", "pinky" };
            string[] japanese = { "親指", "人指", "中指", "薬指", "小指" };
            int digit = Array.IndexOf(english, finger.Groups[1].Value.ToLowerInvariant());
            int joint = int.Parse(finger.Groups[2].Value) - (digit == 0 ? 1 : 0);
            name = japanese[digit] + (char)('０' + joint);
        }
        name = name.Replace("人差指", "人指").Replace("人差し指", "人指");
        if (name.StartsWith("親指") || name.StartsWith("人指") || name.StartsWith("中指") || name.StartsWith("薬指") || name.StartsWith("小指"))
            name = Regex.Replace(name, "[0-3]", match => ((char)('０' + int.Parse(match.Value))).ToString());
        return name + "." + side + (end ? "_end" : "");
    }

    private static void FaceCamera(Transform model, Dictionary<string, Transform> bones)
    {
        // Anatomical right appears at camera-left. Establish the facing from the
        // shoulder pair, independent of each exporter’s FBX forward-axis choice.
        Vector3 anatomicalRight = bones["腕.R"].position - bones["腕.L"].position;
        Vector3 forward = Vector3.Cross(anatomicalRight, Vector3.up);
        forward.y = 0;
        if (forward.sqrMagnitude > 0.000001f)
            model.rotation = Quaternion.FromToRotation(forward.normalized, Vector3.back) * model.rotation;
    }

    private static Bounds MeasureBody(Transform root, Dictionary<string, Transform> bones, SkinnedMeshRenderer[] skins)
    {
        Vector3 head = root.InverseTransformPoint(bones["頭"].position);
        Vector3 left = root.InverseTransformPoint(bones["足首.L"].position);
        Vector3 right = root.InverseTransformPoint(bones["足首.R"].position);
        Vector3 feet = (left + right) * 0.5f;
        float skeletonHeight = head.y - Mathf.Min(left.y, right.y);
        if (skeletonHeight < 0.0001f) throw new InvalidOperationException("The character's head is not above its feet.");
        float shoulderWidth = Vector3.Distance(bones["腕.L"].position, bones["腕.R"].position);
        float halfWidth = Mathf.Max(shoulderWidth * 0.85f, skeletonHeight * 0.22f);
        float depth = Mathf.Max(shoulderWidth * 0.6f, skeletonHeight * 0.24f);
        var soles = new List<float>();
        var crowns = new List<float>();
        var widths = new List<float>();
        foreach (SkinnedMeshRenderer skin in skins)
        {
            var baked = new Mesh();
            try
            {
                skin.BakeMesh(baked);
                Matrix4x4 bakedToWorld = BakedVertexToWorld(skin, baked);
                Vector3[] vertices = baked.vertices;
                var visible = new bool[vertices.Length];
                Material[] materials = skin.sharedMaterials;
                for (int submesh = 0; submesh < baked.subMeshCount; submesh++)
                {
                    Material material = submesh < materials.Length ? materials[submesh] : null;
                    if (material != null && material.HasProperty("_BaseColor") && material.GetColor("_BaseColor").a <= 0.01f) continue;
                    foreach (int index in baked.GetIndices(submesh)) if (index >= 0 && index < visible.Length) visible[index] = true;
                }
                for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    if (!visible[vertexIndex]) continue;
                    Vector3 point = root.InverseTransformPoint(bakedToWorld.MultiplyPoint3x4(vertices[vertexIndex]));
                    if (!Finite(point.x + point.y + point.z)) continue;
                    // AABB extrema include remote PMX ornaments/debug meshes. Use
                    // the skeletal body column and robust head/sole percentiles.
                    if (Mathf.Abs(point.x - feet.x) > halfWidth || Mathf.Abs(point.z - head.z) > depth) continue;
                    if (point.y >= feet.y - skeletonHeight * 0.18f && point.y <= feet.y + skeletonHeight * 0.05f) soles.Add(point.y);
                    if (point.y >= head.y && point.y <= head.y + skeletonHeight * 0.8f) crowns.Add(point.y);
                    if (point.y > feet.y + skeletonHeight * 0.35f && point.y < head.y) widths.Add(Mathf.Abs(point.x - feet.x));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
        }
        float bottom = soles.Count > 4 ? Percentile(soles, 0.01f) : feet.y - skeletonHeight * 0.035f;
        float top = crowns.Count > 4 ? Percentile(crowns, 0.985f) : head.y + skeletonHeight * 0.15f;
        float width = widths.Count > 4 ? Percentile(widths, 0.98f) * 2 : shoulderWidth;
        return new Bounds(new Vector3(feet.x, (bottom + top) * 0.5f, (head.z + feet.z) * 0.5f), new Vector3(width, top - bottom, depth * 2));
    }

    private static float Percentile(List<float> values, float fraction)
    {
        values.Sort();
        return values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * fraction), 0, values.Count - 1)];
    }

    /// <summary>FBX imports can bake their scale into vertices. Match the baked
    /// geometry to the renderer's real world bounds before sampling it.</summary>
    public static Matrix4x4 BakedVertexToWorld(SkinnedMeshRenderer skin, Mesh baked)
    {
        return BakedVertexToWorld(skin, baked, out _, out _, out _);
    }

    public static Matrix4x4 BakedVertexToWorld(SkinnedMeshRenderer skin, Mesh baked, out string mode, out Vector3 projectedSize, out float fitError)
    {
        Transform transform = skin.transform;
        Vector3 local = transform.localScale;
        Vector3 inverse = new Vector3(Mathf.Abs(local.x) > 0.000001f ? 1 / local.x : 1,
            Mathf.Abs(local.y) > 0.000001f ? 1 / local.y : 1, Mathf.Abs(local.z) > 0.000001f ? 1 / local.z : 1);
        Matrix4x4[] candidates = { transform.localToWorldMatrix,
            Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one), transform.localToWorldMatrix * Matrix4x4.Scale(inverse) };
        Vector3 expected = skin.bounds.size;
        float bestError = float.PositiveInfinity;
        Matrix4x4 best = candidates[0];
        int bestIndex = 0;
        projectedSize = Vector3.zero;
        for (int index = 0; index < candidates.Length; index++)
        {
            Matrix4x4 candidate = candidates[index];
            Vector3 size = TransformBounds(baked.bounds, candidate).size;
            float error = 0;
            for (int axis = 0; axis < 3; axis++)
                if (expected[axis] > 0.000001f && size[axis] > 0.000001f)
                    error += Mathf.Abs(Mathf.Log(size[axis] / expected[axis]));
            if (error < bestError) { bestError = error; best = candidate; bestIndex = index; projectedSize = size; }
        }
        mode = new[] { "localToWorld", "rotationAndTranslation", "removeLocalScale" }[bestIndex];
        fitError = bestError;
        return best;
    }

    private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
    {
        Bounds result = new Bounds(matrix.MultiplyPoint3x4(bounds.center), Vector3.zero);
        for (int corner = 0; corner < 8; corner++)
            result.Encapsulate(matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))));
        return result;
    }
    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    private static MaterialMap ReadMaterialMap(string folder)
    {
        TextAsset json = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/PetMaterialMap.json");
        return json != null ? JsonUtility.FromJson<MaterialMap>(json.text) : null;
    }

    private static void ConvertMaterials(GameObject model, SkinnedMeshRenderer[] skins, MaterialMap map, string folder, string generated)
    {
        Shader shader = Shader.Find("Desktop Pet/Soft Anime");
        if (shader == null) throw new InvalidOperationException("Assets/Shaders/PetToon.shader is required for imported pets.");
        string[] textures = AssetDatabase.FindAssets("t:Texture2D", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
        var cache = new Dictionary<string, Material>();
        int ordinal = 0;
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true).Where(renderer => renderer.enabled))
        {
            Material[] slots = renderer.sharedMaterials;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                Material source = slots[slot];
                string sourceName = source != null ? source.name : "Material" + slot;
                MaterialEntry entry = null;
                if (map != null && map.materials != null)
                {
                    // PMX has one combined skinned mesh with potentially repeated
                    // material names. Its original slot index is authoritative.
                    if (skins.Length == 1 && renderer == skins[0] && slot < map.materials.Length && map.materials.Any(item => item.index >= 0))
                        entry = map.materials.FirstOrDefault(item => item.index == slot) ?? map.materials[slot];
                    else entry = map.materials.FirstOrDefault(item => item.name == sourceName)
                        ?? map.materials.FirstOrDefault(item => item.name == Regex.Replace(sourceName, @"\.\d{3}$", ""));
                }
                string key = entry != null ? "mapped:" + Array.IndexOf(map.materials, entry) : "source:" + (source != null ? source.GetEntityId().ToString() : sourceName);
                if (!cache.TryGetValue(key, out Material replacement))
                {
                    string path = generated + "/Material_" + ordinal.ToString("D3") + "_" + SafeName(sourceName) + ".mat";
                    ordinal++;
                    replacement = AssetDatabase.LoadAssetAtPath<Material>(path);
                    bool create = replacement == null;
                    if (create) replacement = new Material(shader);
                    replacement.shader = shader;
                    replacement.shaderKeywords = Array.Empty<string>();
                    replacement.name = sourceName;
                    ConfigureMaterial(replacement, source, entry, folder, textures);
                    PetImportedMaterialAppearance.Configure(replacement, map?.source ?? model.name);
                    if (create) AssetDatabase.CreateAsset(replacement, path);
                    else EditorUtility.SetDirty(replacement);
                    cache.Add(key, replacement);
                }
                slots[slot] = replacement;
            }
            renderer.sharedMaterials = slots;
        }
    }

    private static void ConfigureMaterial(Material result, Material source, MaterialEntry entry, string folder, string[] textures)
    {
        Color color = source != null && source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor")
            : source != null && source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
        if (entry != null && entry.color != null && entry.color.Length >= 4) color = new Color(entry.color[0], entry.color[1], entry.color[2], entry.color[3]);
        Texture texture = null;
        if (entry != null && !string.IsNullOrWhiteSpace(entry.texture))
        {
            string path = entry.texture.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) path = folder + "/" + path;
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new InvalidOperationException("Imported material texture is missing: " + path);
        }
        if (texture == null && source != null)
            foreach (string property in new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_DiffuseMap" })
                if (source.HasProperty(property) && source.GetTexture(property) != null) { texture = source.GetTexture(property); break; }
        if (texture == null) texture = FindPaintedTexture(result.name, textures);
        // The supplied game FBXs store facial shader data in the painted texture's
        // alpha channel (skin pixels have alpha 0). It is not cutout opacity.
        bool faceDataAlpha = result.name.EndsWith("_Mat_Face", StringComparison.OrdinalIgnoreCase)
            && texture != null && texture.name.IndexOf("_Face_Color", StringComparison.OrdinalIgnoreCase) >= 0;
        // This separate layer requires the game's stencil/eye-shadow shader. Applying
        // the face albedo to it is incorrect and can cover the entire face.
        bool stencilEyeShadow = result.name.StartsWith("Mat_EyeShadow_", StringComparison.OrdinalIgnoreCase);
        if (stencilEyeShadow) { color.a = 0; texture = null; }
        bool transparent = color.a < 0.99f || (source != null && source.renderQueue >= (int)RenderQueue.Transparent)
            || Regex.IsMatch(result.name, "glass|trans|透明|纱", RegexOptions.IgnoreCase);
        bool doubleSided = entry != null ? entry.doubleSided : source == null || !source.HasProperty("_Cull") || source.GetFloat("_Cull") == 0;
        result.SetColor("_BaseColor", color);
        result.SetTexture("_BaseMap", texture);
        if (source != null && source.HasProperty("_MainTex"))
        { result.SetTextureScale("_BaseMap", source.GetTextureScale("_MainTex")); result.SetTextureOffset("_BaseMap", source.GetTextureOffset("_MainTex")); }
        result.SetColor("_ShadeColor", new Color(0.82f, 0.77f, 0.86f));
        result.SetFloat("_ShadeThreshold", 0.42f);
        result.SetFloat("_ShadeSoftness", 0.24f);
        result.SetFloat("_Brightness", 0.97f);
        result.SetFloat("_LightInfluence", 0.18f);
        result.SetFloat("_LightColorInfluence", 0.06f);
        result.SetFloat("_SpecularStrength", 0.015f);
        result.SetFloat("_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
        result.SetFloat("_Surface", transparent ? 1 : 0);
        result.SetFloat("_AlphaClip", transparent || faceDataAlpha ? 0 : 1);
        result.SetFloat("_Cutoff", 0.15f);
        result.SetFloat("_ZWrite", transparent ? 0 : 1);
        result.SetFloat("_SrcBlend", transparent ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
        result.SetFloat("_DstBlend", transparent ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
        result.SetOverrideTag("RenderType", transparent ? "Transparent" : "TransparentCutout");
        result.renderQueue = transparent ? (int)RenderQueue.Transparent : (int)RenderQueue.AlphaTest;
        result.SetShaderPassEnabled("DepthOnly", !transparent);
    }

    private static Texture2D FindPaintedTexture(string materialName, string[] textures)
    {
        string name = materialName.ToLowerInvariant();
        string category = name.Contains("hair") || name.Contains("髪") || name.Contains("发") ? "hair"
            : name.Contains("face") || name.Contains("eye") || name.Contains("颜") || name.Contains("脸") ? "face"
            : name.Contains("weapon") || name.Contains("武器") ? "weapon" : "body";
        string best = null;
        int bestScore = 0;
        foreach (string path in textures)
        {
            string candidate = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (Regex.IsMatch(candidate, "ramp|mask|lightmap|normal|matcap|lut|facemap|expressionmap|toon")) continue;
            int score = candidate.Contains("color") || candidate.Contains("diffuse") || candidate.Contains("albedo") || candidate.Contains("base") ? 6 : 0;
            if (candidate.Contains(category)) score += 12;
            if (name.Contains(candidate) || candidate.Contains(name)) score += 20;
            if (score <= bestScore) continue;
            bestScore = score; best = path;
        }
        return best != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(best) : null;
    }

    private static string SafeName(string value)
    {
        string result = Regex.Replace(value ?? "Pet", @"[^\p{L}\p{N}_.-]", "_");
        return string.IsNullOrWhiteSpace(result) ? "Pet" : result;
    }
}
