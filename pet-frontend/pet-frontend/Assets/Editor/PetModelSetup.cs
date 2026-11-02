using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>Builds a clean desktop-pet prefab from the user's already converted Lita FBX.</summary>
public static class PetModelSetup
{
    public const string ModelFolder = "Assets/Models/Lita";
    public const string PrefabPath = ModelFolder + "/DesktopPetLita.prefab";
    private const string GeneratedFolder = ModelFolder + "/PetGenerated";
    private static readonly string[] GeneratedActions = { "Idle", "Wave", "Happy", "Think", "Sad", "Surprised", "Speak", "Drag", "Walk", "Run", "Jump",
        "Nod", "TiltHead", "Shy", "Stretch", "Cheer", "LookAround", "Guard", "GuardLower", "HoldSword", "DrawSlash", "LegRetract", "LegShift", "LegSway",
        "LegRetractLeft", "LegShiftLeft", "LegSwayLeft", "HandLeadIdleLeft", "HandLeadIdleRight", "HandLeadWalkLeft", "HandLeadWalkRight",
        "HandLeadRunLeft", "HandLeadRunRight", "AngryStomp", "HandLeadAirLeft", "HandLeadAirRight", "HandLeadLand" };

    [Serializable] private sealed class MaterialMap { public MaterialEntry[] materials; }
    [Serializable] private sealed class MaterialEntry
    {
        public string name;
        public string texture;
        public float[] color;
        public bool doubleSided;
    }

    [MenuItem("Desktop Pet/Build Lita Assets")]
    public static void BuildLitaAssets()
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelFolder + "/lita.fbx");
        TextAsset metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(ModelFolder + "/PetMaterialMap.json");
        if (source == null || metadata == null)
            throw new InvalidOperationException("Lita FBX or PMX material metadata is missing.");
        if (!AssetDatabase.IsValidFolder(GeneratedFolder))
            AssetDatabase.CreateFolder(ModelFolder, "PetGenerated");

        MaterialMap map = JsonUtility.FromJson<MaterialMap>(metadata.text);
        var materials = CreateMaterials(map);
        var root = new GameObject("Lita");
        try
        {
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.name = "VisualModel";
            model.transform.SetParent(root.transform, false);
            // This FBX faces +Z. The desktop camera views the pet from -Z.
            // Rotate before generating poses so gesture directions remain camera-facing.
            model.transform.localRotation = Quaternion.Euler(0, 180, 0) * model.transform.localRotation;

            // Blender's MMD export contains debugging rigid bodies, cameras and lights.
            // None of those are part of the rendered, skinned character.
            RemoveImportedRendering(model, false);
            foreach (AudioListener listener in model.GetComponentsInChildren<AudioListener>(true))
                UnityEngine.Object.DestroyImmediate(listener);
            foreach (Animator oldAnimator in model.GetComponentsInChildren<Animator>(true))
                UnityEngine.Object.DestroyImmediate(oldAnimator);
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                var skin = renderer as SkinnedMeshRenderer;
                if (skin == null || skin.sharedMesh == null || skin.sharedMesh.vertexCount < 10000)
                {
                    renderer.enabled = false;
                    continue;
                }
                Material[] slots = skin.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    string originalName = slots[i] != null ? slots[i].name : "";
                    if (materials.TryGetValue(originalName, out Material replacement)) slots[i] = replacement;
                    else if (i < map.materials.Length) slots[i] = materials[map.materials[i].name];
                    else throw new InvalidOperationException("Unknown Lita material slot: " + originalName);
                }
                skin.sharedMaterials = slots;
                skin.updateWhenOffscreen = true;
                skin.shadowCastingMode = ShadowCastingMode.Off;
                skin.receiveShadows = false;
            }

            var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(x => x.enabled).ToArray();
            if (skins.Length == 0) throw new InvalidOperationException("The FBX did not contain a skinned Lita mesh.");
            RemoveUnreferencedHelpers(model);
            Bounds bounds = CombinedBounds(skins);
            if (bounds.size.y < 0.001f) throw new InvalidOperationException("Lita bounds are empty.");
            model.transform.localScale *= 1.72f / bounds.size.y;
            bounds = CombinedBounds(skins);
            model.transform.position -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

            Animator animator = root.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = CreateAnimations(root.transform);
            PetCharacterAnimation character = root.AddComponent<PetCharacterAnimation>();
            character.Configure(animator, skins);
            root.AddComponent<PetSecondaryMotion>().Configure(model.transform, character);
            var hitbox = root.AddComponent<CapsuleCollider>();
            hitbox.height = 1.72f;
            hitbox.radius = 0.26f;
            hitbox.center = new Vector3(0, 0.86f, 0);
            hitbox.isTrigger = true;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log("Lita ready: " + PrefabPath + "; " + skins.Length + " skinned mesh, " + materials.Count + " URP materials, " + GeneratedActions.Length + " animated states and skirt/hair motion.");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [MenuItem("Desktop Pet/Update Lita Animations Only")]
    public static void UpdateLitaAnimations()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before updating Lita animations.");
        GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            RemoveImportedRendering(contents, false);
            Animator animator = contents.GetComponent<Animator>();
            if (animator == null) animator = contents.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = CreateAnimations(contents.transform);
            PetCharacterAnimation character = contents.GetComponent<PetCharacterAnimation>();
            if (character == null) character = contents.AddComponent<PetCharacterAnimation>();
            character.Configure(animator, contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(skin => skin.enabled).ToArray());
            PetSecondaryMotion secondary = contents.GetComponent<PetSecondaryMotion>();
            if (secondary == null) secondary = contents.AddComponent<PetSecondaryMotion>();
            secondary.Configure(contents.transform.Find("VisualModel"), character);
            PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        AssetDatabase.SaveAssets();
        Debug.Log("Lita movement updated: " + GeneratedActions.Length + " states including guarded retreat, sword gestures, articulated hands, breathing Idle, looping Walk/Run/Drag, and bounded skirt/hair springs. No scene was changed.");
    }

    [MenuItem("Desktop Pet/Update Lita Movement")]
    public static void UpdateLitaMovement() { UpdateLitaAnimations(); }

    [MenuItem("Desktop Pet/Rebuild Imported Animations Only")]
    public static void RebuildImportedAnimationsOnly()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before rebuilding imported animations.");
        string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)).OrderBy(path => path).ToArray();
        foreach (string path in paths)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                string folder = Path.GetDirectoryName(path).Replace('\\', '/');
                Animator animator = contents.GetComponent<Animator>();
                if (animator == null) throw new InvalidOperationException("Imported pet has no Animator: " + path);
                // CreateCompatibleAnimations re-bakes all measurements each call;
                // nothing is shared between characters or retained from Play Mode.
                animator.runtimeAnimatorController = CreateCompatibleAnimations(contents.transform, folder + "/PetGenerated", Path.GetFileName(folder));
                PetCharacterAnimation character = contents.GetComponent<PetCharacterAnimation>();
                if (character != null) character.Configure(animator, contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(skin => skin.enabled).ToArray());
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("PET_IMPORTED_ANIMATIONS_REBUILT " + paths.Length + " prefabs; see each PetGenerated/ArmClearanceReport.json. Model bundles were not rebuilt.");
    }

    [MenuItem("Desktop Pet/Update Sword Animations Only")]
    public static void UpdateSwordAnimationsOnly()
    {
        UpdateSelectedAnimations(new HashSet<string>(new[] { "HoldSword", "DrawSlash" }));
    }

    [MenuItem("Desktop Pet/Update Guard and Leg Animations Only")]
    public static void UpdateGuardAndLegAnimationsOnly()
    {
        UpdateSelectedAnimations(new HashSet<string>(new[] { "Guard", "GuardLower", "LegRetract", "LegShift", "LegSway", "Run", "Jump" }));
    }

    [MenuItem("Desktop Pet/Update Run and Jump Animations Only")]
    public static void UpdateRunAndJumpAnimationsOnly() => UpdateSelectedAnimations(new HashSet<string>(new[] { "Run", "Jump" }));

    [MenuItem("Desktop Pet/Update Leg Animations Only")]
    public static void UpdateLegAnimationsOnly() => UpdateSelectedAnimations(new HashSet<string>(new[] { "LegRetract", "LegShift", "LegSway" }));

    [MenuItem("Desktop Pet/Update Side and Hand Lead Animations Only")]
    public static void UpdateSideAndHandLeadAnimationsOnly() => UpdateSelectedAnimations(new HashSet<string>(new[]
    {
        "LegRetractLeft", "LegShiftLeft", "LegSwayLeft", "HandLeadIdleLeft", "HandLeadIdleRight", "HandLeadWalkLeft", "HandLeadWalkRight",
        "HandLeadRunLeft", "HandLeadRunRight", "AngryStomp"
    }));

    [MenuItem("Desktop Pet/Update Angry Stomp Only")]
    public static void UpdateAngryStompOnly() => UpdateSelectedAnimations(new HashSet<string>(new[] { "AngryStomp" }));

    [MenuItem("Desktop Pet/Update Hand Lead Air and Landing Only")]
    public static void UpdateHandLeadAirAndLandingOnly() => UpdateSelectedAnimations(new HashSet<string>(new[] { "HandLeadAirLeft", "HandLeadAirRight", "HandLeadLand" }));

    [MenuItem("Desktop Pet/Update Sword and Lower Guard Animations Only")]
    public static void UpdateSwordAndLowerGuardAnimationsOnly() => UpdateSelectedAnimations(new HashSet<string>(new[] { "HoldSword", "DrawSlash", "GuardLower" }));

    private static void UpdateSelectedAnimations(HashSet<string> selection)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before updating selected animations.");
        var paths = new[] { PrefabPath }.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal))).OrderBy(path => path).ToArray();
        foreach (string path in paths)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                bool compatible = path != PrefabPath;
                string folder = compatible ? Path.GetDirectoryName(path).Replace('\\', '/') + "/PetGenerated" : GeneratedFolder;
                string name = compatible ? Path.GetFileName(Path.GetDirectoryName(path)) + "Actions" : "LitaActions";
                var animator = contents.GetComponent<Animator>();
                if (animator == null) throw new InvalidOperationException("No existing character Animator: " + path);
                animator.runtimeAnimatorController = CreateAnimations(contents.transform, folder, name, compatible, selection);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("PET_SELECTED_ANIMATIONS_UPDATED " + paths.Length + " models; " + string.Join(", ", selection) + ". Other clips were retained; rebuild bundles separately.");
    }

    [MenuItem("Desktop Pet/Apply Lita Soft Anime Materials")]
    public static void ApplyLitaSoftAnimeMaterials()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before updating Lita materials.");
        TextAsset metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(ModelFolder + "/PetMaterialMap.json");
        if (metadata == null) throw new InvalidOperationException("Lita PMX material metadata is missing.");
        MaterialMap map = JsonUtility.FromJson<MaterialMap>(metadata.text);
        Dictionary<string, Material> materials = CreateMaterials(map);
        GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            RemoveImportedRendering(contents, false);
            foreach (SkinnedMeshRenderer skin in contents.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skin.enabled || skin.sharedMesh == null) continue;
                Material[] slots = skin.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    string originalName = slots[i] == null ? "" : slots[i].name;
                    if (materials.TryGetValue(originalName, out Material replacement)) slots[i] = replacement;
                    else if (i < map.materials.Length) slots[i] = materials[map.materials[i].name];
                    else throw new InvalidOperationException("Unknown Lita material slot: " + originalName);
                }
                skin.sharedMaterials = slots;
                skin.shadowCastingMode = ShadowCastingMode.Off;
                skin.receiveShadows = false;
            }
            PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        AssetDatabase.SaveAssets();
        Debug.Log("Lita soft anime materials applied: " + materials.Count + " painted-texture materials. No scene was changed.");
    }

    [MenuItem("Desktop Pet/Repair Imported Model Cameras")]
    public static void RepairImportedModelCameras()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before repairing imported model cameras.");
        // Patch only this generated prefab; do not reset any instance's materials or transforms.
        GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            RemoveImportedRendering(contents, false);
            if (contents.GetComponentsInChildren<Camera>(true).Length != 0)
                throw new InvalidOperationException("The Lita prefab still contains a camera.");
            PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }

        Scene scene = SceneManager.GetSceneByPath("Assets/Scenes/DesktopPet.unity");
        if (!scene.IsValid() || !scene.isLoaded)
            throw new InvalidOperationException("Prefab repaired. Open Assets/Scenes/DesktopPet.unity and rerun to repair its loaded instances.");
        GameObject characterRoot = scene.GetRootGameObjects().FirstOrDefault(go => go.name == "PetCharacterRoot");
        if (characterRoot == null) throw new InvalidOperationException("Generated PetCharacterRoot was not found.");
        foreach (PetCharacterAnimation character in characterRoot.GetComponentsInChildren<PetCharacterAnimation>(true))
        {
            string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(character.gameObject);
            if (source == PrefabPath) RemoveImportedRendering(character.gameObject, true);
        }
        Camera[] cameras = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Camera>(true)).ToArray();
        if (cameras.Length != 1 || cameras[0].gameObject.name != "Main Camera")
            throw new InvalidOperationException("DesktopPet requires exactly one Main Camera; found " + cameras.Length + ". Other scene cameras were left untouched.");
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save DesktopPet.unity.");
        AssetDatabase.SaveAssets();
        Debug.Log("Lita prefab and DesktopPet scene repaired. Exactly one Main Camera remains.");
    }

    private static void RemoveImportedRendering(GameObject model, bool recordUndo)
    {
        SkinnedMeshRenderer[] skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var bones = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer skin in skins)
        {
            foreach (Transform bone in skin.bones) if (bone != null) bones.Add(bone);
            if (skin.rootBone != null) bones.Add(skin.rootBone);
        }
        GameObject[] importedObjects = model.GetComponentsInChildren<Camera>(true).Select(camera => camera.gameObject)
            .Concat(model.GetComponentsInChildren<Light>(true).Select(light => light.gameObject)).Distinct().ToArray();
        foreach (GameObject imported in importedObjects)
        {
            if (imported == null) continue;
            bool preserveTransform = imported == model || imported.GetComponentInChildren<SkinnedMeshRenderer>(true) != null
                || bones.Any(bone => bone == imported.transform || bone.IsChildOf(imported.transform));
            if (!preserveTransform)
            {
                DestroyImported(imported, recordUndo);
                continue;
            }
            // URP data uses RequireComponent(Camera/Light). Remove dependents first;
            // otherwise Unity refuses to remove the underlying Camera or Light.
            foreach (UniversalAdditionalCameraData data in imported.GetComponents<UniversalAdditionalCameraData>()) DestroyImported(data, recordUndo);
            foreach (UniversalAdditionalLightData data in imported.GetComponents<UniversalAdditionalLightData>()) DestroyImported(data, recordUndo);
            foreach (Camera camera in imported.GetComponents<Camera>()) { camera.enabled = false; DestroyImported(camera, recordUndo); }
            foreach (Light light in imported.GetComponents<Light>()) { light.enabled = false; DestroyImported(light, recordUndo); }
            foreach (AudioListener listener in imported.GetComponents<AudioListener>()) DestroyImported(listener, recordUndo);
        }
        if (model.GetComponentsInChildren<Camera>(true).Length != 0)
            throw new InvalidOperationException("Removing imported cameras failed. The model will not be saved with a rogue camera.");
    }

    private static void DestroyImported(UnityEngine.Object target, bool recordUndo)
    {
        if (recordUndo) Undo.DestroyObjectImmediate(target);
        else UnityEngine.Object.DestroyImmediate(target);
    }

    private static void RemoveUnreferencedHelpers(GameObject model)
    {
        // MMD debug geometry is safe to remove only when no skinned mesh depends on it.
        SkinnedMeshRenderer[] allSkins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var referencedBones = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer skin in allSkins)
        {
            foreach (Transform bone in skin.bones) if (bone != null) referencedBones.Add(bone);
            if (skin.rootBone != null) referencedBones.Add(skin.rootBone);
        }
        Transform[] helpers = model.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name == "rigidbodies" || t.name == "joints").ToArray();
        foreach (Transform helper in helpers)
        {
            if (helper == null || helper.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) continue;
            if (referencedBones.Any(bone => bone == helper || bone.IsChildOf(helper))) continue;
            UnityEngine.Object.DestroyImmediate(helper.gameObject);
        }
    }

    private static Bounds CombinedBounds(SkinnedMeshRenderer[] skins)
    {
        Bounds result = skins[0].bounds;
        for (int i = 1; i < skins.Length; i++) result.Encapsulate(skins[i].bounds);
        return result;
    }

    private static Dictionary<string, Material> CreateMaterials(MaterialMap map)
    {
        Shader shader = Shader.Find("Desktop Pet/Soft Anime");
        if (shader == null) throw new InvalidOperationException("Import Assets/Shaders/PetToon.shader before creating Lita materials.");
        if (!AssetDatabase.IsValidFolder(GeneratedFolder)) AssetDatabase.CreateFolder(ModelFolder, "PetGenerated");
        var result = new Dictionary<string, Material>();
        foreach (MaterialEntry entry in map.materials)
        {
            string assetPath = GeneratedFolder + "/" + entry.name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            bool create = material == null;
            if (create) material = new Material(shader);
            material.shader = shader;
            material.shaderKeywords = Array.Empty<string>();
            material.name = entry.name;
            Color color = new Color(entry.color[0], entry.color[1], entry.color[2], entry.color[3]);
            material.SetColor("_BaseColor", color);
            Texture2D texture = string.IsNullOrEmpty(entry.texture) ? null :
                AssetDatabase.LoadAssetAtPath<Texture2D>(ModelFolder + "/" + entry.texture);
            if (!string.IsNullOrEmpty(entry.texture) && texture == null)
                throw new InvalidOperationException("Texture is missing: " + entry.texture);
            material.SetTexture("_BaseMap", texture);
            bool skinMaterial = entry.name == "脸" || entry.name.StartsWith("皮肤", StringComparison.Ordinal);
            bool faceMaterial = entry.texture == "脸.png" || entry.name.StartsWith("眼", StringComparison.Ordinal);
            bool hairMaterial = entry.name.StartsWith("头发", StringComparison.Ordinal);
            // Keep the original painted color/alpha and texture assignments. Skin gets
            // a warmer, softer shadow without the additive shine of a lit PBR shader.
            material.SetColor("_ShadeColor", skinMaterial ? new Color(0.86f, 0.73f, 0.76f)
                : faceMaterial ? new Color(0.91f, 0.87f, 0.9f)
                : hairMaterial ? new Color(0.76f, 0.72f, 0.81f) : new Color(0.73f, 0.69f, 0.81f));
            material.SetFloat("_ShadeThreshold", faceMaterial ? 0.35f : skinMaterial ? 0.42f : 0.46f);
            material.SetFloat("_ShadeSoftness", faceMaterial ? 0.3f : skinMaterial ? 0.26f : 0.17f);
            material.SetFloat("_Brightness", skinMaterial ? 0.93f : faceMaterial ? 0.97f : 0.96f);
            material.SetFloat("_LightInfluence", 0.18f);
            material.SetFloat("_LightColorInfluence", 0.06f);
            material.SetFloat("_SpecularStrength", skinMaterial || faceMaterial ? 0 : hairMaterial ? 0.025f : 0.015f);
            material.SetFloat("_Cull", entry.doubleSided ? 0 : 2);
            bool transparent = color.a < 0.99f;
            material.SetFloat("_Surface", transparent ? 1 : 0);
            material.SetFloat("_AlphaClip", transparent ? 0 : 1);
            material.SetFloat("_Cutoff", 0.2f);
            material.SetFloat("_ZWrite", transparent ? 0 : 1);
            material.SetFloat("_SrcBlend", transparent ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
            material.SetFloat("_DstBlend", transparent ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            material.SetOverrideTag("RenderType", transparent ? "Transparent" : "TransparentCutout");
            material.renderQueue = transparent ? (int)RenderQueue.Transparent : (int)RenderQueue.AlphaTest;
            material.SetShaderPassEnabled("DepthOnly", !transparent);
            if (create) AssetDatabase.CreateAsset(material, assetPath);
            else EditorUtility.SetDirty(material);
            result.Add(entry.name, material);
        }
        return result;
    }

    private static RuntimeAnimatorController CreateAnimations(Transform root)
    {
        return CreateAnimations(root, GeneratedFolder, "LitaActions", false);
    }

    /// <summary>Bakes the same action library against a canonical imported skeleton.</summary>
    public static RuntimeAnimatorController CreateCompatibleAnimations(Transform root, string generatedFolder, string modelId)
    {
        return CreateAnimations(root, generatedFolder, modelId + "Actions", true);
    }

    private static RuntimeAnimatorController CreateAnimations(Transform root, string generatedFolder, string controllerName, bool compatible, ISet<string> onlyActions = null)
    {
        if (!AssetDatabase.IsValidFolder(generatedFolder))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(generatedFolder).Replace('\\', '/'), Path.GetFileName(generatedFolder));
        string controllerPath = generatedFolder + "/" + controllerName + ".controller";
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        if (onlyActions != null && !machine.states.Any(child => child.state.name == "Idle"))
            throw new InvalidOperationException("Generate the complete character animations before updating selected states.");
        foreach (ChildAnimatorState child in machine.states)
            if (onlyActions == null || onlyActions.Contains(child.state.name)) machine.RemoveState(child.state);
        var bones = new Dictionary<string, Transform>();
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            if (!bones.ContainsKey(transform.name)) bones.Add(transform.name, transform);
        // The exported MMD rig has independent FK and deformation (D) leg chains.
        // Drive both: moving just 足/ひざ leaves the mesh weighted to 足D/ひざD stiff.
        string[] names = { "センター", "腰", "下半身", "上半身", "上半身2", "首", "頭", "腕.R", "ひじ.R", "手首.R", "腕.L", "ひじ.L", "手首.L",
            "足.R", "ひざ.R", "足首.R", "足.L", "ひざ.L", "足首.L", "足D.R", "ひざD.R", "足首D.R", "足D.L", "ひざD.L", "足首D.L" };
        Dictionary<string, HandRig> hands = CreateHandRigs(bones, compatible);
        foreach (HandRig hand in hands.Values)
        {
            hand.restLocalRotation = hand.hand.localRotation;
            foreach (FingerJoint joint in hand.joints) joint.restRotation = joint.bone.localRotation;
        }
        ArmClearance clearance = CreateArmClearance(root, bones, hands, compatible);
        var clearanceReport = new ArmClearanceReport { model = root.name, bodyVertexCount = clearance.bodyVertexCount,
            solverVersion = "relaxed-arms-v12-sword-key-poses",
            gapMeters = clearance.gap, bakes = clearance.bakes.ToArray(), surfaces = clearance.surfaces.Select(surface => surface.Measurement).ToArray(),
            limbs = hands.Select(pair => new LimbMeasurement { side = pair.Key, upperRadius = pair.Value.upperRadius, forearmRadius = pair.Value.sleeveRadius,
                palmRadius = pair.Value.palmRadius, upperSamples = pair.Value.upperSampleCount, forearmSamples = pair.Value.forearmSampleCount,
                palmSamples = pair.Value.palmSampleCount, neutralUpperDirection = pair.Value.neutralUpperDirection,
                neutralForearmDirection = pair.Value.neutralForearmDirection, neutralHandResidual = pair.Value.neutralHandResidual,
                upperProbeEnabled = pair.Value.upperProbeEnabled, forearmProbeEnabled = pair.Value.forearmProbeEnabled,
                measurementStatus = pair.Value.measurementStatus, neutralForearmCorrection = pair.Value.neutralForearmCorrection }).ToArray() };
        var actionReports = new List<ActionClearanceReport>();
        string reportPath = Path.Combine(generatedFolder, "ArmClearanceReport.json");
        if (onlyActions != null && File.Exists(reportPath))
        {
            var previousReport = JsonUtility.FromJson<ArmClearanceReport>(File.ReadAllText(reportPath));
            if (previousReport?.actions != null) actionReports.AddRange(previousReport.actions.Where(report => !onlyActions.Contains(report.action)));
        }
        foreach (HandRig hand in hands.Values)
        {
            hand.restLocalRotation = hand.hand.localRotation;
            foreach (FingerJoint joint in hand.joints) joint.restRotation = joint.bone.localRotation;
        }
        var driven = names.Where(bones.ContainsKey).Select(name => bones[name])
            .Concat(hands.Values.SelectMany(hand => hand.DrivenBones)).Where(bone => bone != null).Distinct().ToArray();
        if (!bones.ContainsKey("腕.R") || !bones.ContainsKey("ひじ.R") || !bones.ContainsKey("頭"))
            throw new InvalidOperationException("Expected MMD arm/head bones are absent in the Lita FBX.");
        foreach (string required in compatible ? new[] { "センター", "足.R", "ひざ.R", "足首.R", "足.L", "ひざ.L", "足首.L" }
            : new[] { "センター", "腰", "下半身", "足D.R", "ひざD.R", "足首D.R", "足D.L", "ひざD.L", "足首D.L" })
            if (!bones.ContainsKey(required)) throw new InvalidOperationException("Lita movement requires deformation bone: " + required);
        var rest = driven.ToDictionary(bone => bone, bone => bone.localRotation);
        var restPositions = driven.ToDictionary(bone => bone, bone => bone.localPosition);
        Transform center = bones["センター"];
        AnimatorState idle = onlyActions != null ? machine.states.First(child => child.state.name == "Idle").state : null;
        var stateList = new List<AnimatorState>();
        foreach (string action in GeneratedActions.Where(action => onlyActions == null || onlyActions.Contains(action)))
        {
            float duration = action == "Walk" ? 1.05f : action == "Idle" ? 4f : action == "Think" ? 3.5f : action == "Drag" ? 1.6f : 2.8f;
            switch (action)
            {
                case "Nod": duration = 2.6f; break;
                case "Run": duration = 0.8f; break;
                case "Jump": duration = 2.2f; break;
                case "HoldSword": duration = 4.2f; break;
                case "DrawSlash": duration = 3.6f; break;
                case "TiltHead": duration = 3f; break;
                case "Guard": case "GuardLower": duration = 4.2f; break;
                case "LegRetract": case "LegRetractLeft": duration = 2.8f; break;
                case "LegShift": case "LegShiftLeft": duration = 2.8f; break;
                case "LegSway": case "LegSwayLeft": duration = 3.4f; break;
                case "HandLeadIdleLeft": case "HandLeadIdleRight": duration = 4f; break;
                case "HandLeadWalkLeft": case "HandLeadWalkRight": duration = 1.05f; break;
                case "HandLeadRunLeft": case "HandLeadRunRight": duration = 0.8f; break;
                case "HandLeadAirLeft": case "HandLeadAirRight": duration = 2.4f; break;
                case "HandLeadLand": duration = 1f; break;
                case "AngryStomp": duration = 3f; break;
                case "Cheer": duration = 3.2f; break;
                case "Shy": duration = 3.4f; break;
                case "Stretch": case "LookAround": duration = 3.8f; break;
            }
            string clipPath = generatedFolder + "/" + action + ".anim";
            AnimationClip savedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            bool create = savedClip == null;
            // Bake into a temporary object so a failed clock/pose cannot clear
            // an existing generated asset in memory or on the next SaveAssets.
            AnimationClip clip = new AnimationClip();
            clip.name = action;
            clip.frameRate = 30;
            var curves = driven.ToDictionary(bone => bone, bone => new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() });
            var positionCurves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            foreach (HandRig hand in hands.Values) { hand.hasPreviousTwist = false; hand.hasPreviousPalmBlend = false; hand.hasPreviousPoleTurn = false; }
            if (IsGuardAction(action))
            {
                foreach (Transform bone in driven) { bone.localRotation = rest[bone]; bone.localPosition = restPositions[bone]; }
                PoseLowerBody(root, bones, action, 0.5f, compatible);
                if (IsGuardAction(action)) PoseGuard(root, bones, hands, clearance, action, 0.5f, true);
                else PoseSword(root, bones, hands, clearance, action, 0.5f, true);
                foreach (Transform bone in driven) { bone.localRotation = rest[bone]; bone.localPosition = restPositions[bone]; }
                foreach (HandRig hand in hands.Values) { hand.hasPreviousTwist = false; hand.hasPreviousPalmBlend = false; hand.hasPreviousPoleTurn = false; }
            }
            SwordKeyPose[] swordPoses = null;
            if (IsSwordAction(action)) swordPoses = CreateSwordKeyPoses(root, bones, hands, clearance, action, driven, rest, restPositions, compatible, duration, out duration);
            Transform[] checkedArms = new[] { bones["腕.R"], bones["ひじ.R"], bones["腕.L"], bones["ひじ.L"], bones["手首.R"], bones["手首.L"] }
                .Concat(hands.Values.SelectMany(hand => hand.DrivenBones)).Where(bone => bone != null).Distinct().ToArray();
            var previousArms = checkedArms.ToDictionary(bone => bone, bone => bone.localRotation);
            var previousDriven = driven.ToDictionary(bone => bone, bone => bone.localRotation);
            var previousWorld = driven.ToDictionary(bone => bone, bone => bone.rotation);
            var angularPeaks = driven.ToDictionary(bone => bone, bone => 0f);
            var actionReport = new ActionClearanceReport { action = action, nominalDurationSeconds = duration, angularSpeedLimit = 360 };
            var progressCurve = new AnimationCurve();
            int frames = Mathf.Max(60, Mathf.CeilToInt(duration * 60f));
            float sampleTime = 0;
            for (int frame = 0; frame <= frames; frame++)
            {
                foreach (Transform bone in driven) { bone.localRotation = rest[bone]; bone.localPosition = restPositions[bone]; }
                float progress = frame / (float)frames;
                if (swordPoses != null) SampleSwordKeyPoses(root, driven, center, swordPoses, progress);
                else Pose(root, bones, hands, action, progress, compatible, clearance);
                float penetration = ApplyArmClearance(root, bones, hands, clearance, action, progress);
                foreach (Transform bone in driven)
                {
                    if (frame > 0)
                    {
                        float speed = Quaternion.Angle(previousWorld[bone], bone.rotation) / (duration / frames);
                        if (speed > actionReport.maxAuthoredWorldSpeedDegreesPerSecond)
                        { actionReport.maxAuthoredWorldSpeedDegreesPerSecond = speed; actionReport.worldSpeedBone = bone.name; actionReport.worldSpeedAtProgress = progress; }
                    }
                    previousWorld[bone] = bone.rotation;
                }
                foreach (string side in new[] { "R", "L" })
                    actionReport.maxAuthoredHandResidualMeters = Mathf.Max(actionReport.maxAuthoredHandResidualMeters,
                        ArmPenetration(bones["腕." + side], hands[side], clearance, false, true, true).magnitude);
                if (frame > 0)
                {
                    // Component-linear quaternion curves normalize to nlerp.
                    // A hand also inherits shoulder, elbow and twist rotations. Bound
                    // the sum along each chain, rather than each local joint alone.
                    foreach (Transform bone in driven) {
                        float angle = Quaternion.Angle(previousDriven[bone], bone.localRotation);
                        angularPeaks[bone] = 4 * Mathf.Tan(angle * Mathf.Deg2Rad * 0.25f) * Mathf.Rad2Deg;
                    }
                    float maximumChainPeak = 0;
                    foreach (Transform bone in driven) {
                        float chain = 0;
                        for (Transform current = bone; current != null && current != root; current = current.parent)
                            if (angularPeaks.TryGetValue(current, out float peak)) chain += peak;
                        maximumChainPeak = Mathf.Max(maximumChainPeak, chain);
                    }
                    float nominalSeconds = duration / frames;
                    float minimumSeconds = maximumChainPeak / 360f * 1.01f;
                    if (minimumSeconds > nominalSeconds) actionReport.stretchedSegments++;
                    // Keep the authored clock uniform. Refinement retimes the
                    // final complete skeleton once, with gradual cadence changes.
                    sampleTime += nominalSeconds;
                }
                if (frame == frames / 2)
                {
                    actionReport.midRightWrist = root.InverseTransformPoint(bones["手首.R"].position);
                    actionReport.midLeftWrist = root.InverseTransformPoint(bones["手首.L"].position);
                }
                if (penetration > actionReport.maxResidualMeters) { actionReport.maxResidualMeters = penetration; actionReport.residualAtSeconds = sampleTime;
                    actionReport.residualProbe = clearance.residualProbe; actionReport.residualPoint = clearance.residualPoint; }
                foreach (Transform arm in checkedArms)
                {
                    float jump = Quaternion.Angle(previousArms[arm], arm.localRotation);
                    if (frame > 0 && jump > actionReport.maxAdjacentDegrees)
                    { actionReport.maxAdjacentDegrees = jump; actionReport.jumpBone = arm.name; actionReport.jumpAtSeconds = sampleTime; }
                    previousArms[arm] = arm.localRotation;
                }
                for (int axis = 0; axis < 3; axis++) positionCurves[axis].AddKey(sampleTime, center.localPosition[axis]);
                progressCurve.AddKey(sampleTime, progress);
                foreach (Transform bone in driven)
                {
                    Quaternion q = bone.localRotation;
                    if (Quaternion.Dot(previousDriven[bone], q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    previousDriven[bone] = q;
                    AnimationCurve[] c = curves[bone];
                    float time = sampleTime;
                    c[0].AddKey(time, q.x); c[1].AddKey(time, q.y); c[2].AddKey(time, q.z); c[3].AddKey(time, q.w);
                }
            }
            RefineHandClearanceKeys(root, bones, hands, clearance, driven, curves, center, positionCurves, progressCurve, action, actionReport);
            float bakedDuration = positionCurves[0].keys.Last().time;
            if (bakedDuration > 30f || float.IsNaN(bakedDuration) || float.IsInfinity(bakedDuration))
                throw new InvalidOperationException($"Motion clock failed for {root.name}/{action}: {bakedDuration:F3}s. Previous asset was not saved.");
            actionReport.sampleCount = positionCurves[0].length;
            actionReport.actualDurationSeconds = sampleTime;
            actionReport.maxAuthoredAdjacentDegrees = actionReport.maxAdjacentDegrees;
            actionReport.maxAuthoredResidualMeters = actionReport.maxResidualMeters;
            actionReports.Add(actionReport);
            foreach (Transform bone in driven)
            {
                string path = AnimationUtility.CalculateTransformPath(bone, root);
                string[] components = { "x", "y", "z", "w" };
                for (int axis = 0; axis < 4; axis++)
                {
                    if (IsContinuousAction(action)) SetContinuousTangents(curves[bone][axis], IsLoopingAction(action));
                    else SetLinearTangents(curves[bone][axis]);
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + components[axis]), curves[bone][axis]);
                }
            }
            for (int axis = 0; axis < 3; axis++)
            {
                if (IsContinuousAction(action)) SetContinuousTangents(positionCurves[axis], IsLoopingAction(action));
                else SetLinearTangents(positionCurves[axis]);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(center, root), typeof(Transform), "m_LocalPosition." + "xyz"[axis]), positionCurves[axis]);
            }
            clip.EnsureQuaternionContinuity();
            UnwrapEulerPreviewCurves(clip);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = IsLoopingAction(action);
            settings.loopBlend = settings.loopTime;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            VerifyRetimedClip(root, bones, hands, clearance, driven, clip, actionReport);
            if (create) AssetDatabase.CreateAsset(clip, clipPath);
            else
            {
                EditorUtility.CopySerialized(clip, savedClip);
                UnityEngine.Object.DestroyImmediate(clip);
                clip = savedClip;
                EditorUtility.SetDirty(clip);
            }
            AssetDatabase.SaveAssetIfDirty(clip);
            actionReport.residualCategory = actionReport.residualProbe != null && (actionReport.residualProbe.Contains("forearm") || actionReport.residualProbe.Contains("upper-arm") || actionReport.residualProbe.Contains("elbow"))
                ? "conservative clothing/limb capsule against visible-mesh envelope; rendered mesh intersection not established"
                : "palm/finger probe against visible-mesh envelope";
            string clearanceMessage = $"PET_ARM_CLEARANCE {root.name}/{action}: residual={actionReport.maxResidualMeters:F4}m at {actionReport.residualAtSeconds:F3}s; adjacent={actionReport.maxAdjacentDegrees:F2}deg ({actionReport.jumpBone}); duration={actionReport.actualDurationSeconds:F3}s; speed={actionReport.maxAngularSpeedDegreesPerSecond:F2}deg/s.";
            if (actionReport.maxResidualMeters > 0.002f || actionReport.maxAdjacentDegrees > 12.05f
                || actionReport.maxAngularSpeedDegreesPerSecond > 360.5f) Debug.LogWarning(clearanceMessage);
            else Debug.Log(clearanceMessage);
            AnimatorState state = machine.AddState(action);
            state.motion = clip;
            state.writeDefaultValues = false;
            stateList.Add(state);
            if (action == "Idle") { machine.defaultState = state; idle = state; }
        }
        // The runtime owns held/moving interactions. Their loops remain active
        // until a release or speed change; one-shot reactions return to Idle.
        foreach (AnimatorState state in stateList.Where(state => state != idle && !IsLoopingAction(state.name)))
        {
            var transition = state.AddTransition(idle);
            transition.hasExitTime = true;
            transition.exitTime = 1;
            transition.hasFixedDuration = true;
            transition.duration = 0.2f;
        }
        foreach (Transform bone in driven) { bone.localRotation = rest[bone]; bone.localPosition = restPositions[bone]; }
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssetIfDirty(controller);
        clearanceReport.actions = actionReports.OrderBy(report => Array.IndexOf(GeneratedActions, report.action)).ToArray();
        File.WriteAllText(reportPath, JsonUtility.ToJson(clearanceReport, true));
        return controller;
    }

    private static void SetLinearTangents(AnimationCurve curve)
    {
        Keyframe[] keys = curve.keys;
        for (int index = 0; index < keys.Length; index++)
        {
            Keyframe key = keys[index];
            key.inTangent = index > 0 ? (key.value - keys[index - 1].value) / Mathf.Max(0.000001f, key.time - keys[index - 1].time) : 0;
            key.outTangent = index + 1 < keys.Length ? (keys[index + 1].value - key.value) / Mathf.Max(0.000001f, keys[index + 1].time - key.time) : 0;
            key.weightedMode = WeightedMode.None;
            keys[index] = key;
        }
        curve.keys = keys;
    }

    private static void SetContinuousTangents(AnimationCurve curve, bool loop = false)
    {
        var keys = curve.keys;
        for (int index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            float tangent = 0;
            if (index > 0 && index + 1 < keys.Length)
            {
                float before = (key.value - keys[index - 1].value) / (key.time - keys[index - 1].time);
                float after = (keys[index + 1].value - key.value) / (keys[index + 1].time - key.time);
                // A common tangent removes the velocity step at each baked key;
                // the monotone bound prevents overshooting a safe endpoint.
                if (before * after > 0) tangent = Mathf.Sign(before) * Mathf.Min((Mathf.Abs(before) + Mathf.Abs(after)) * 0.5f, 3 * Mathf.Min(Mathf.Abs(before), Mathf.Abs(after)));
            }
            else if (loop && keys.Length > 2)
            {
                float before = (keys[0].value - keys[keys.Length - 2].value) / (keys[keys.Length - 1].time - keys[keys.Length - 2].time);
                float after = (keys[1].value - keys[0].value) / (keys[1].time - keys[0].time);
                if (before * after > 0) tangent = Mathf.Sign(before) * Mathf.Min((Mathf.Abs(before) + Mathf.Abs(after)) * 0.5f, 3 * Mathf.Min(Mathf.Abs(before), Mathf.Abs(after)));
            }
            key.inTangent = key.outTangent = tangent; key.weightedMode = WeightedMode.None; keys[index] = key;
        }
        curve.keys = keys;
    }

    private static void RefineHandClearanceKeys(Transform root, Dictionary<string, Transform> bones,
        Dictionary<string, HandRig> hands, ArmClearance profile, Transform[] driven,
        Dictionary<Transform, AnimationCurve[]> curves, Transform center, AnimationCurve[] positions,
        AnimationCurve progress, string action, ActionClearanceReport report)
    {
        if (IsContinuousAction(action))
        {
            // Continuous protective/touch paths are authored against one elbow
            // corridor. A per-frame nearest-surface detour would reintroduce
            // branch flips. Their complete paths are sampled by regression.
            // Observe the actual world rotation of every deforming bone. Summing
            // absolute local twist/counter-twist rates double-counts rotations
            // that cancel in the wrist and can stretch a normal pose to 30s.
            float limit = IsRunJumpAction(action) || IsHandLeadLocomotion(action) || action == "AngryStomp" || action == "HandLeadLand" ? 300f : action == "DrawSlash" ? 180f : 135f;
            float scale = Mathf.Max(1, report.maxAuthoredWorldSpeedDegreesPerSecond / limit);
            Debug.Log($"PET_CONTINUOUS_CLOCK {root.name}/{action}: rawWorldSpeed={report.maxAuthoredWorldSpeedDegreesPerSecond:F2}deg/s, bone={report.worldSpeedBone}, progress={report.worldSpeedAtProgress:F4}, scale={scale:F3}.");
            foreach (AnimationCurve curve in curves.Values.SelectMany(value => value).Concat(positions).Append(progress))
            {
                var keys = curve.keys;
                for (int index = 0; index < keys.Length; index++) { var key = keys[index]; key.time *= scale; keys[index] = key; }
                curve.keys = keys;
            }
            report.angularSpeedLimit = limit;
            return;
        }
        // Safe endpoints do not imply a safe quaternion interpolation: the wrist
        // can cut through a hip corner between them. Insert corrected interior
        // poses, then retime the COMPLETE joint chain so the detour stays smooth.
        var allCurves = curves.Values.SelectMany(value => value).Concat(positions).Append(progress).ToArray();
        void Linearize() { foreach (AnimationCurve curve in allCurves) SetLinearTangents(curve); }
        void Sample(float time)
        {
            foreach (Transform bone in driven)
            {
                var c = curves[bone];
                bone.localRotation = new Quaternion(c[0].Evaluate(time), c[1].Evaluate(time), c[2].Evaluate(time), c[3].Evaluate(time)).normalized;
            }
            center.localPosition = new Vector3(positions[0].Evaluate(time), positions[1].Evaluate(time), positions[2].Evaluate(time));
        }
        Linearize();
        for (int pass = 0; pass < 8; pass++)
        {
            var times = positions[0].keys;
            int inserted = 0;
            for (int index = 1; index < times.Length; index++)
            {
                // Contact corrections below a 240 Hz interval cannot represent
                // useful runtime motion and amplify rounding errors on retiming.
                if (times[index].time - times[index - 1].time <= 1f / 240f) continue;
                // Refinement fixes contact introduced by interpolation. It must
                // not repeatedly chase the conservative contact already present
                // in an authored endpoint: doing so rewrites the intended pose.
                float endpointResidual = 0;
                foreach (float endpoint in new[] { times[index - 1].time, times[index].time })
                {
                    Sample(endpoint);
                    foreach (string side in new[] { "R", "L" })
                        endpointResidual = Mathf.Max(endpointResidual,
                            ArmPenetration(bones["腕." + side], hands[side], profile, false, true, true).magnitude);
                }
                float worstTime = 0, worstResidual = 0.0007f;
                for (int quarter = 1; quarter <= 3; quarter++)
                {
                    float time = Mathf.Lerp(times[index - 1].time, times[index].time, quarter / 4f);
                    Sample(time);
                    foreach (string side in new[] { "R", "L" })
                    {
                        float residual = ArmPenetration(bones["腕." + side], hands[side], profile, false, true, true).magnitude;
                        if (residual > worstResidual && residual > endpointResidual + 0.0007f)
                        { worstResidual = residual; worstTime = time; }
                    }
                }
                if (worstTime == 0) continue;
                Sample(worstTime);
                var sampled = driven.ToDictionary(bone => bone, bone => bone.localRotation);
                float phase = progress.Evaluate(worstTime);
                foreach (string side in new[] { "R", "L" })
                {
                    HandRig hand = hands[side]; Transform upper = bones["腕." + side];
                    float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
                    float length = Vector3.Distance(upper.position, hand.elbow.position) + Vector3.Distance(hand.elbow.position, hand.hand.position);
                    float weight = ArmGestureWeight(action, side, phase);
                    ApplyHandClearance(root, upper, hand, profile, action, phase, weight, sign, length);
                }
                float correctedResidual = 0;
                foreach (string side in new[] { "R", "L" })
                    correctedResidual = Mathf.Max(correctedResidual,
                        ArmPenetration(bones["腕." + side], hands[side], profile, false, true, true).magnitude);
                // An unreachable envelope is diagnostic evidence, not a reason
                // to keep inserting ever larger detours into the same interval.
                if (correctedResidual >= worstResidual - 0.0001f) continue;
                // Preserve the authored IK branch. Replacing an interior pose
                // with a different elbow/twist solution can clear one point yet
                // make both neighboring interpolations collide or snap.
                if (driven.Any(bone => Quaternion.Angle(sampled[bone], bone.localRotation) > 2f)) continue;
                foreach (Transform bone in driven)
                {
                    Quaternion q = bone.localRotation;
                    if (Quaternion.Dot(sampled[bone], q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    curves[bone][0].AddKey(worstTime, q.x); curves[bone][1].AddKey(worstTime, q.y);
                    curves[bone][2].AddKey(worstTime, q.z); curves[bone][3].AddKey(worstTime, q.w);
                }
                for (int axis = 0; axis < 3; axis++) positions[axis].AddKey(worstTime, center.localPosition[axis]);
                progress.AddKey(worstTime, phase); inserted++;
            }
            report.refinementInsertedKeys += inserted;
            Linearize();
            if (inserted == 0) break;
        }
        // Reparameterize without speeding up either the original movement or
        // the inserted joint chain; all bones and the body share one timeline.
        var timeline = positions[0].keys;
        var retimed = new float[timeline.Length];
        var interval = new float[timeline.Length];
        var peaks = new Dictionary<Transform, float>();
        // AnimationCurve.keys returns a copy. Cache it once instead of copying
        // every complete key array for every bone and segment in this loop.
        var cachedKeys = curves.ToDictionary(pair => pair.Key, pair => pair.Value.Select(curve => curve.keys).ToArray());
        for (int index = 1; index < timeline.Length; index++)
        {
            foreach (Transform bone in driven)
            {
                var c = cachedKeys[bone];
                Quaternion a = new Quaternion(c[0][index - 1].value, c[1][index - 1].value, c[2][index - 1].value, c[3][index - 1].value).normalized;
                Quaternion b = new Quaternion(c[0][index].value, c[1][index].value, c[2][index].value, c[3][index].value).normalized;
                peaks[bone] = 4 * Mathf.Tan(Quaternion.Angle(a, b) * Mathf.Deg2Rad * 0.25f) * Mathf.Rad2Deg;
            }
            float chainPeak = 0;
            foreach (Transform bone in driven)
            {
                float sum = 0;
                for (Transform current = bone; current != null && current != root; current = current.parent)
                    if (peaks.TryGetValue(current, out float peak)) sum += peak;
                chainPeak = Mathf.Max(chainPeak, sum);
            }
            interval[index] = Mathf.Max(timeline[index].time - timeline[index - 1].time, chainPeak / 360f * 1.01f);
        }
        // Keep timing local to each segment. Propagating a rate multiplier from
        // one tiny refinement interval over neighboring authored intervals can
        // inflate a short gesture to hours even though every pose remains safe.
        for (int index = 1; index < interval.Length; index++) retimed[index] = retimed[index - 1] + interval[index];
        foreach (AnimationCurve curve in allCurves)
        {
            var keys = curve.keys;
            for (int index = 0; index < keys.Length; index++) { var key = keys[index]; key.time = retimed[index]; keys[index] = key; }
            curve.keys = keys; SetLinearTangents(curve);
        }
    }

    private static void VerifyRetimedClip(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands,
        ArmClearance clearance, Transform[] driven, AnimationClip clip, ActionClearanceReport report)
    {
        report.maxAdjacentDegrees = 0;
        report.jumpBone = "";
        var previous = driven.ToDictionary(bone => bone, bone => bone.localRotation);
        float previousTime = 0;
        int samples = Mathf.CeilToInt(clip.length * 30);
        for (int index = 0; index <= samples; index++)
        {
            float time = Mathf.Min(index / 30f, clip.length);
            clip.SampleAnimation(root.gameObject, time);
            foreach (Transform bone in driven)
            {
                float angle = Quaternion.Angle(previous[bone], bone.localRotation);
                if (index > 0 && angle > report.maxAdjacentDegrees)
                { report.maxAdjacentDegrees = angle; report.jumpBone = bone.name; report.jumpAtSeconds = time; }
                if (index > 0 && time > previousTime + 0.000001f)
                    report.maxAngularSpeedDegreesPerSecond = Mathf.Max(report.maxAngularSpeedDegreesPerSecond, angle / (time - previousTime));
                previous[bone] = bone.localRotation;
            }
            foreach (string side in new[] { "R", "L" })
            {
                HandRig hand = hands[side];
                float handResidual = ArmPenetration(bones["腕." + side], hand, clearance, false, true, true).magnitude;
                if (handResidual > report.maxHandResidualMeters)
                {
                    report.maxHandResidualMeters = handResidual;
                    report.handResidualAtSeconds = time;
                    report.handResidualProbe = side + ":" + hand.penetrationProbe;
                }
                float residual = ArmPenetration(bones["腕." + side], hand, clearance).magnitude;
                report.max30FpsResidualMeters = Mathf.Max(report.max30FpsResidualMeters, residual);
                if (residual > report.maxResidualMeters)
                {
                    report.maxResidualMeters = residual; report.residualAtSeconds = time;
                    report.residualProbe = side + ":" + hand.penetrationProbe; report.residualPoint = hand.penetrationPoint;
                }
            }
            previousTime = time;
        }
        report.playbackSampleCount = samples + 1;
        report.actualDurationSeconds = clip.length;
        report.max30FpsAdjacentDegrees = report.maxAdjacentDegrees;
    }

    private static void UnwrapEulerPreviewCurves(AnimationClip clip)
    {
        // Unity also creates Euler curves for Editor sampling of quaternion clips.
        // Those preview values can wrap from -180 to +180 even while the runtime
        // quaternion curve is continuous (Lita's index-finger base crosses this
        // boundary during Stretch). Keep each equivalent Euler angle near its
        // predecessor; leave the quaternion animation and existing tangents intact.
        var serialized = new SerializedObject(clip);
        SerializedProperty previews = serialized.FindProperty("m_EulerEditorCurves");
        if (previews == null || !previews.isArray)
            throw new InvalidOperationException("This Unity version does not expose animation Euler preview curves.");
        bool changed = false;
        for (int curveIndex = 0; curveIndex < previews.arraySize; curveIndex++)
        {
            SerializedProperty keys = previews.GetArrayElementAtIndex(curveIndex).FindPropertyRelative("curve.m_Curve");
            if (keys == null || !keys.isArray || keys.arraySize == 0) continue;
            float previous = keys.GetArrayElementAtIndex(0).FindPropertyRelative("value").floatValue;
            for (int keyIndex = 1; keyIndex < keys.arraySize; keyIndex++)
            {
                SerializedProperty value = keys.GetArrayElementAtIndex(keyIndex).FindPropertyRelative("value");
                float continuous = previous + Mathf.DeltaAngle(previous, value.floatValue);
                if (Mathf.Abs(continuous - value.floatValue) > 180f) { value.floatValue = continuous; changed = true; }
                previous = continuous;
            }
        }
        if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Pose(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, string action, float t, bool compatible = false, ArmClearance clearance = null)
    {
        float sway = Mathf.Sin(t * Mathf.PI * 2);
        PoseLowerBody(root, bones, action, t, compatible);
        if (IsGuardAction(action)) { PoseGuard(root, bones, hands, clearance, action, t); return; }
        if (IsRunJumpAction(action)) { PoseRunJump(root, bones, hands, action, t); return; }
        if (IsHandLeadAir(action)) { PoseHandLeadAir(root, bones, hands, action, t); return; }
        if (action == "HandLeadLand") { PoseHandLeadLanding(root, bones, hands, t); return; }
        if (IsHandLeadAction(action)) { PoseHandLead(root, bones, hands, action, t); return; }
        if (action == "AngryStomp") { PoseAngryStomp(root, bones, hands, t); return; }
        if (IsLegAction(action))
        {
            float weight = TouchWindow(t);
            RotateWorld(bones, "頭", root.right, (LegActionKind(action) == "LegRetract" ? 5f : 2f) * weight);
            foreach (string side in new[] { "R", "L" })
            {
                Aim(bones["腕." + side], hands[side].elbow, root.TransformDirection(hands[side].neutralUpperDirection));
                Aim(hands[side].elbow, hands[side].hand, root.TransformDirection(hands[side].neutralForearmDirection));
                PoseHand(root, hands[side], "Idle", t, 0, Mathf.Sign(root.InverseTransformPoint(bones["腕." + side].position).x), hands[side].hand.position - hands[side].elbow.position);
            }
            return;
        }
        if (action == "HoldSword" || action == "DrawSlash")
        {
            PoseSword(root, bones, hands, clearance, action, t);
            return;
        }
        if (IsStandingGesture(action))
        {
            PoseStandingGesture(root, bones, hands, action, t, compatible);
            return;
        }
        // Idle is a quiet breath: keep the torso upright and the gaze steady.
        float expression = action == "Walk" || action == "Run" || action == "Drag" ? 1 : GestureWindow(t, 0.24f, 0.72f);
        if (action != "Idle")
        {
            RotateWorld(bones, "上半身2", root.right, sway * expression);
            RotateWorld(bones, "頭", root.up, sway * 2.0f * expression);
        }
        // The FBX is in an A pose. Relax both arms before adding the selected gesture.
        foreach (string side in new[] { "R", "L" })
        {
            if (!bones.TryGetValue("腕." + side, out Transform upper) || !bones.TryGetValue("ひじ." + side, out Transform elbow) || !bones.TryGetValue("手首." + side, out Transform hand)) continue;
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            Aim(upper, elbow, root.TransformDirection(hands[side].neutralUpperDirection));
            Aim(elbow, hand, root.TransformDirection(hands[side].neutralForearmDirection));
            if (action == "Walk" || action == "Run")
            {
                float stride = Mathf.Cos(t * Mathf.PI * 2 + (side == "L" ? Mathf.PI : 0));
                bool run = action == "Run";
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * (run ? 0.28f : 0.19f), -1, stride * (run ? 0.38f : 0.22f))));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * 0.09f, run ? -0.5f : -1, (run ? -0.72f : -0.28f) + stride * 0.16f)));
            }
            if (action == "Jump")
            {
                float lift = GestureWindow(t, 0.27f, 0.63f);
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * (0.2f + lift * 0.28f), -1, -lift * 0.20f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * 0.08f, -1, -0.12f - lift * 0.35f)));
            }
            if (side == "R" && (action == "Wave" || action == "Surprised"))
            {
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * 0.9f, -0.14f, -0.15f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * (0.18f + 0.16f * Mathf.Sin(t * Mathf.PI * 8)), 1, -0.12f)));
            }
            if (action == "Happy")
            {
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * 0.65f, -0.4f, -0.2f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * 0.18f, 0.9f, -0.15f)));
            }
            if (side == "R" && action == "Think")
            {
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * 0.2f, -0.6f, -0.7f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(-sign * 0.1f, 1, -0.13f)));
            }
            if (action == "Speak" && side == "L")
            {
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * 0.3f, -0.8f, -0.25f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * 0.2f, -0.1f + sway * 0.13f, -1)));
            }
            if (action == "Drag")
            {
                // Lift the elbows into a gentle carried pose, with a small continuous
                // counter-swing even when the mouse is held still.
                Aim(upper, elbow, root.TransformDirection(new Vector3(sign * 0.7f, -0.45f + sway * sign * 0.09f, -0.18f)));
                Aim(elbow, hand, root.TransformDirection(new Vector3(sign * 0.22f, -0.25f + sway * sign * 0.12f, -0.55f)));
            }
            // Bridge-style responses depart from and return to the same quiet
            // stance. Capture world directions before aiming either segment so
            // an exporter's mirrored local axes/roll cannot change the result.
            float gesture = ArmGestureWeight(action, side, t);
            Vector3 desiredUpper = elbow.position - upper.position;
            Vector3 desiredForearm = hand.position - elbow.position;
            Aim(upper, elbow, Vector3.Slerp(root.TransformDirection(hands[side].neutralUpperDirection).normalized, desiredUpper.normalized, gesture));
            Aim(elbow, hand, Vector3.Slerp(root.TransformDirection(hands[side].neutralForearmDirection).normalized, desiredForearm.normalized, gesture));
            PoseHand(root, hands[side], action, t, gesture, sign, hand.position - elbow.position);
        }
        if (action == "Think") RotateWorld(bones, "頭", root.forward, -8 * expression);
        if (action == "Sad") { RotateWorld(bones, "頭", root.right, 12 * expression); RotateWorld(bones, "上半身", root.right, 4 * expression); }
        if (action == "Happy") RotateWorld(bones, "頭", root.forward, sway * 6 * expression);
        if (action == "Surprised") RotateWorld(bones, "頭", root.right, -8 * expression);
        if (action == "Drag")
        {
            RotateWorld(bones, "上半身", root.forward, sway * 2.4f);
            RotateWorld(bones, "頭", root.forward, -sway * 4f);
            RotateWorld(bones, "頭", root.right, -4f);
        }
        if (action == "Walk" || action == "Run")
        {
            RotateWorld(bones, "上半身", root.up, -sway * (action == "Run" ? 4f : 3f));
            if (action == "Run") RotateWorld(bones, "上半身2", root.right, 5f);
            RotateWorld(bones, "頭", root.forward, -sway * 1.5f);
        }
        if (action == "Jump") RotateWorld(bones, "上半身2", root.right, 5f * GesturePulse(t, 0, 0.16f, 0.3f));
    }

    private static void PoseLowerBody(Transform root, Dictionary<string, Transform> bones, string action, float t, bool compatible = false)
    {
        float phase = t * Mathf.PI * 2;
        float sway = Mathf.Sin(phase);
        bool idle = action == "Idle" || action == "HandLeadIdleLeft" || action == "HandLeadIdleRight";
        bool planted = idle || IsStandingGesture(action) || action == "Wave" || action == "Happy" || action == "Think" || action == "Sad" || action == "Surprised" || action == "Speak";
        bool running = action == "Run" || action == "HandLeadRunLeft" || action == "HandLeadRunRight";
        bool walking = action == "Walk" || action == "HandLeadWalkLeft" || action == "HandLeadWalkRight" || running;
        bool jumping = action == "Jump";
        bool carried = action == "Drag";
        bool suspended = IsHandLeadAir(action), landing = action == "HandLeadLand";
        bool legReaction = IsLegAction(action);
        bool stomping = action == "AngryStomp";
        string touchedSide = LegActionSide(action), legKind = LegActionKind(action);
        float touchWeight = legReaction ? TouchWindow(t) : 0;
        float stompWeight = stomping ? AngryWindow(t) : 0;
        float stompLift = stomping ? StompLift(t) : 0;
        float strideScale = 1;
        if (compatible && bones.TryGetValue("足.R", out Transform hip) && bones.TryGetValue("ひざ.R", out Transform shin)
            && bones.TryGetValue("足首.R", out Transform foot))
            strideScale = Mathf.Clamp((Vector3.Distance(hip.position, shin.position) + Vector3.Distance(shin.position, foot.position)) / 0.82f, 0.3f, 1.35f);
        var anklePositions = new Dictionary<Transform, Vector3>();
        var ankleRotations = new Dictionary<Transform, Quaternion>();
        foreach (string side in new[] { "R", "L" })
            foreach (string suffix in new[] { "", "D" })
                if (bones.TryGetValue("足首" + suffix + "." + side, out Transform ankle))
                {
                    anklePositions.Add(ankle, ankle.position);
                    ankleRotations.Add(ankle, ankle.rotation);
                }

        // Move the skeleton's center, not the prefab root. Idle breathes vertically
        // by just 4 mm over its four-second loop, with both shoes planted by IK.
        // A slight resting knee bend keeps the full breath within leg reach.
        float bob = walking ? -0.018f + 0.006f * Mathf.Cos(phase * 2) : -0.005f - 0.003f * (1 - Mathf.Cos(phase * 2));
        if (planted) bob = -0.006f + 0.002f * Mathf.Cos(phase);
        if (carried) bob = sway * 0.006f;
        if (suspended) bob = -0.004f + sway * 0.003f;
        if (landing) bob = -0.004f - 0.046f * LandingAbsorption(t);
        float flight = jumping ? Mathf.Sin(SmoothPhase(Mathf.InverseLerp(0.28f, 0.76f, t)) * Mathf.PI) : 0;
        flight *= flight;
        if (jumping) bob = -0.004f - 0.105f * SmoothPulse(t, 0, 0.18f, 0.32f) + 0.20f * flight - 0.075f * SmoothPulse(t, 0.73f, 0.84f, 1);
        if (running) bob = -0.045f + 0.010f * Mathf.Cos(phase * 2);
        if (action == "Happy") bob -= (1 - Mathf.Cos(phase * 2)) * 0.007f;
        float lateral = planted ? 0 : sway * (walking ? 0.012f : 0.01f);
        if (legReaction) { lateral = (touchedSide == "R" ? 1 : -1) * (legKind == "LegRetract" ? 0.034f : 0.028f) * touchWeight; bob -= 0.014f * touchWeight; }
        if (stomping)
        {
            // Transfer weight onto the planted left shoe, lift once, then absorb
            // the right-foot contact in the knees without translating the root.
            lateral = 0.022f * stompWeight;
            bob -= 0.014f * stompWeight + 0.026f * SmoothPulse(t, 0.51f, 0.60f, 0.78f);
        }
        float retreat = 0;
        if (action == "Guard" || action == "GuardLower")
        {
            // Shift the skeleton's weight back and soften the knees. Both shoes
            // stay at their original world positions through the leg IK below.
            float guard = GuardWindow(t);
            retreat = 0.065f * guard;
            lateral = 0.012f * guard;
            bob -= 0.022f * guard;
        }
        bones["センター"].position += root.TransformVector(new Vector3(lateral, bob, retreat) * strideScale);
        if (!planted)
        {
            RotateWorld(bones, "腰", root.forward, -sway * (walking ? 1.8f : 1.3f));
            RotateWorld(bones, "下半身", root.up, sway * (walking ? 3f : 0.75f));
        }

        foreach (string side in new[] { "R", "L" })
        {
            float legPhase = phase + (side == "L" ? Mathf.PI : 0);
            float swing = Mathf.Max(0, -Mathf.Sin(legPhase));
            foreach (string suffix in new[] { "", "D" })
            {
                if (!bones.TryGetValue("足" + suffix + "." + side, out Transform upper)
                    || !bones.TryGetValue("ひざ" + suffix + "." + side, out Transform knee)
                    || !bones.TryGetValue("足首" + suffix + "." + side, out Transform ankle)) continue;
                float sideSign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
                // The pet is usually only a few hundred screen pixels tall.
                // Include visible lift/lateral travel, not mostly camera-depth
                // movement which made the former 7 mm shift almost invisible.
                Vector3 touchOffset = side != touchedSide ? Vector3.zero : legKind == "LegRetract" ? new Vector3(sideSign * 0.040f, 0.100f, 0.080f) * touchWeight
                    : legKind == "LegShift" ? new Vector3(sideSign * 0.025f, 0.045f, 0.040f) * touchWeight
                    : new Vector3(sideSign * (0.075f + 0.030f * sway), 0.075f, 0.025f * sway) * touchWeight;
                Vector3 footOffset = legReaction ? touchOffset : stomping ? (side == "R" ? new Vector3(sideSign * 0.012f, 0.125f, -0.018f) * stompLift : Vector3.zero)
                    : suspended ? new Vector3(0, 0.040f + 0.006f * Mathf.Sin(legPhase), 0.025f + 0.012f * Mathf.Cos(legPhase))
                    : walking ? new Vector3(0, (running ? 0.095f : 0.055f) * swing * swing, -(running ? 0.155f : 0.105f) * Mathf.Cos(legPhase))
                    : jumping ? new Vector3(0, 0.20f * flight + 0.045f * SmoothPulse(t, 0.35f, 0.52f, 0.68f), 0.025f * SmoothPulse(t, 0.35f, 0.52f, 0.68f))
                    : carried ? new Vector3(0, 0.055f + 0.014f * Mathf.Sin(legPhase), 0.03f + 0.025f * Mathf.Cos(legPhase)) : Vector3.zero;
                SolveLeg(upper, knee, ankle, anklePositions[ankle] + root.TransformVector(footOffset * strideScale), -root.forward);
                // Preserve the original high-heel foot orientation. A small toe-off
                // during swing adds articulation without rotating shoes into the floor.
                ankle.rotation = Quaternion.AngleAxis(legReaction && side == touchedSide ? -touchWeight * 4f : stomping && side == "R" ? -stompLift * 4f : suspended ? -4f - Mathf.Sin(legPhase) : walking ? -swing * (running ? 11f : 7f) : jumping ? -flight * 5f : carried ? -7f : 0, root.right) * ankleRotations[ankle];
            }
        }
    }

    private static bool IsStandingGesture(string action)
    {
        return IsLegAction(action) || action == "AngryStomp" || action == "HandLeadLand" || action == "HoldSword" || action == "DrawSlash" || action == "Nod" || action == "TiltHead" || action == "Shy"
            || action == "Stretch" || action == "Cheer" || action == "LookAround" || action == "Guard" || action == "GuardLower";
    }

    private static bool IsGuardAction(string action) => action == "Guard" || action == "GuardLower";
    private static string LegActionKind(string action) => action.EndsWith("Left", StringComparison.Ordinal) ? action.Substring(0, action.Length - 4) : action;
    private static string LegActionSide(string action) => action.EndsWith("Left", StringComparison.Ordinal) ? "L" : "R";
    private static bool IsLegAction(string action) => LegActionKind(action) == "LegRetract" || LegActionKind(action) == "LegShift" || LegActionKind(action) == "LegSway";
    private static bool IsSwordAction(string action) => action == "HoldSword" || action == "DrawSlash";
    private static bool IsRunJumpAction(string action) => action == "Run" || action == "Jump";
    private static bool IsHandLeadAction(string action) => action.StartsWith("HandLead", StringComparison.Ordinal);
    private static bool IsHandLeadAir(string action) => action == "HandLeadAirLeft" || action == "HandLeadAirRight";
    private static bool IsHandLeadLocomotion(string action) => action.StartsWith("HandLeadWalk", StringComparison.Ordinal) || action.StartsWith("HandLeadRun", StringComparison.Ordinal);
    private static bool IsLoopingAction(string action) => action == "Idle" || action == "Drag" || action == "Walk" || action == "Run" || IsHandLeadAction(action) && action != "HandLeadLand";
    private static bool IsContinuousAction(string action) => IsGuardAction(action) || IsLegAction(action) || IsSwordAction(action) || IsRunJumpAction(action) || IsHandLeadAction(action) || action == "AngryStomp";
    private static float SmoothPhase(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
    private static float GuardWindow(float t) => SmoothPhase(t / 0.36f) * (1 - SmoothPhase((t - 0.67f) / 0.33f));
    private static float GuardHandWindow(float t, string side) => SmoothPhase(t / (side == "R" ? 0.40f : 0.43f)) * (1 - SmoothPhase((t - (side == "R" ? 0.67f : 0.65f)) / (side == "R" ? 0.33f : 0.35f)));
    private static float TouchWindow(float t) => SmoothPhase(t / 0.30f) * (1 - SmoothPhase((t - 0.62f) / 0.38f));
    private static float AngryWindow(float t) => SmoothPhase(t / 0.24f) * (1 - SmoothPhase((t - 0.68f) / 0.32f));
    private static float StompLift(float t) => SmoothPhase((t - 0.17f) / 0.21f) * (1 - SmoothPhase((t - 0.40f) / 0.15f));
    private static float LandingAbsorption(float t) => SmoothPulse(t, 0, 0.28f, 1);
    private static float SmoothPulse(float t, float start, float peak, float end) => t <= peak ? SmoothPhase((t - start) / (peak - start)) : 1 - SmoothPhase((t - peak) / (end - peak));
    private static Vector3 PelvisPoint(Dictionary<string, Transform> bones)
    {
        if (bones.TryGetValue("下半身", out Transform pelvis)) return pelvis.position;
        if (bones.TryGetValue("足.R", out Transform right) && bones.TryGetValue("足.L", out Transform left)) return (right.position + left.position) * 0.5f;
        return bones["センター"].position;
    }

    private static void PoseRunJump(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, string action, float t)
    {
        bool running = action == "Run";
        float phase = t * Mathf.PI * 2, stride = Mathf.Sin(phase);
        float crouch = SmoothPulse(t, 0, 0.18f, 0.32f);
        float lift = SmoothPhase((t - 0.16f) / 0.28f) * (1 - SmoothPhase((t - 0.67f) / 0.27f));
        float air = Mathf.Sin(SmoothPhase(Mathf.InverseLerp(0.28f, 0.76f, t)) * Mathf.PI); air *= air;
        RotateWorld(bones, "上半身2", root.right, running ? -10f + Mathf.Cos(phase * 2) : -10f * crouch - 3f * air);
        if (running) { RotateWorld(bones, "上半身", root.up, -stride * 4f); RotateWorld(bones, "頭", root.right, 4f); }
        else RotateWorld(bones, "頭", root.right, 3f * crouch);
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            float swing = Mathf.Cos(phase + (side == "L" ? Mathf.PI : 0));
            Vector3 upperDirection, lowerDirection;
            if (running)
            {
                // The hand swings against the same-side advancing foot. Both
                // wrists travel through front and rear, with a relaxed elbow.
                upperDirection = new Vector3(sign * 0.22f, -1, swing * 0.55f);
                lowerDirection = new Vector3(sign * 0.08f, -0.70f, -0.30f + swing * 0.42f);
            }
            else
            {
                Vector3 anticipation = hand.neutralUpperDirection + new Vector3(0, 0, crouch * 0.28f);
                upperDirection = Vector3.Slerp(anticipation.normalized, new Vector3(sign * 0.38f, -0.35f, -0.70f).normalized, lift);
                lowerDirection = Vector3.Slerp(hand.neutralForearmDirection.normalized, new Vector3(sign * 0.12f, 0.55f, -0.78f).normalized, lift);
            }
            Aim(upper, hand.elbow, root.TransformDirection(upperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(lowerDirection));
            PoseHand(root, hand, "Idle", t, 0, sign, hand.hand.position - hand.elbow.position);
            Vector3 fingers = hand.hand.TransformDirection(hand.fingerAxis), palm = hand.hand.TransformDirection(hand.palmAxis);
            hand.hand.localRotation = hand.restLocalRotation; OrientHand(hand, fingers, palm);
        }
    }

    private static void PoseHandLead(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, string action, float t)
    {
        bool moving = IsHandLeadLocomotion(action), running = action.StartsWith("HandLeadRun", StringComparison.Ordinal);
        string heldSide = action.EndsWith("Left", StringComparison.Ordinal) ? "L" : "R";
        float phase = t * Mathf.PI * 2, stride = Mathf.Sin(phase);
        RotateWorld(bones, "上半身2", root.right, moving ? running ? -8f + Mathf.Cos(phase * 2) : -3f : -1.5f);
        if (moving) RotateWorld(bones, "上半身", root.up, -stride * (running ? 2.5f : 1.5f));
        RotateWorld(bones, "頭", root.right, moving ? running ? 3f : 1f : 0);
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            bool held = side == heldSide;
            Vector3 upperDirection = hand.neutralUpperDirection, lowerDirection = hand.neutralForearmDirection;
            if (held)
            {
                // A relaxed bent elbow reaches forward to the user's hand. Its
                // pose is shared across idle/walk/run and never swings with gait.
                upperDirection = new Vector3(sign * 0.22f, -0.62f, -0.84f);
                lowerDirection = new Vector3(sign * 0.055f, -0.06f, -1f);
            }
            else if (moving)
            {
                float swing = Mathf.Cos(phase + (side == "L" ? Mathf.PI : 0));
                // Use the running arm corridor already validated by Run. The
                // walking version keeps the same opposite-leg rhythm, smaller.
                upperDirection = new Vector3(sign * (running ? 0.22f : 0.19f), -1, swing * (running ? 0.55f : 0.30f));
                lowerDirection = new Vector3(sign * 0.08f, running ? -0.70f : -1, (running ? -0.30f : -0.20f) + swing * (running ? 0.42f : 0.25f));
            }
            Aim(upper, hand.elbow, root.TransformDirection(upperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(lowerDirection));
            PoseCurledRelaxedHand(root, hand, t, sign, held ? 1 : 0, new Vector3(20, 30, 20), new Vector3(12, 20, 13));
        }
    }

    private static void PoseHandLeadAir(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, string action, float t)
    {
        string heldSide = action.EndsWith("Left", StringComparison.Ordinal) ? "L" : "R";
        float heldSign = Mathf.Sign(root.InverseTransformPoint(bones["腕." + heldSide].position).x);
        float sway = Mathf.Sin(t * Mathf.PI * 2);
        RotateWorld(bones, "上半身2", root.right, -2f);
        RotateWorld(bones, "上半身", root.forward, heldSign * 2f + sway * 0.9f);
        RotateWorld(bones, "頭", root.forward, -heldSign * 2f);
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            bool held = side == heldSide;
            // The raised forearm leaves elbow bend for the runtime mouse IK.
            // The other hand hangs beside the body with a small pendulum sway.
            Vector3 upperDirection = held ? new Vector3(sign * 0.20f, 0.44f, -0.86f)
                : hand.neutralUpperDirection + new Vector3(0, 0, sway * 0.035f);
            Vector3 lowerDirection = held ? new Vector3(sign * 0.06f, 0.88f, -0.47f)
                : hand.neutralForearmDirection + new Vector3(0, 0, sway * 0.055f);
            Aim(upper, hand.elbow, root.TransformDirection(upperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(lowerDirection));
            PoseCurledRelaxedHand(root, hand, t, sign, held ? 1 : 0, new Vector3(20, 30, 20), new Vector3(12, 20, 13));
        }
    }

    private static void PoseHandLeadLanding(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, float t)
    {
        float absorb = LandingAbsorption(t);
        RotateWorld(bones, "上半身2", root.right, -8f * absorb);
        RotateWorld(bones, "頭", root.right, 4f * absorb);
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            Vector3 upperDirection = Vector3.Slerp(hand.neutralUpperDirection.normalized, new Vector3(sign * 0.24f, -1, -0.15f).normalized, absorb);
            Vector3 lowerDirection = Vector3.Slerp(hand.neutralForearmDirection.normalized, new Vector3(sign * 0.10f, -0.92f, -0.30f).normalized, absorb);
            Aim(upper, hand.elbow, root.TransformDirection(upperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(lowerDirection));
            PoseCurledRelaxedHand(root, hand, t, sign, 0, Vector3.zero, Vector3.zero);
        }
    }

    private static void PoseAngryStomp(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, float t)
    {
        float weight = AngryWindow(t), contact = SmoothPulse(t, 0.49f, 0.59f, 0.75f);
        RotateWorld(bones, "上半身2", root.right, -weight * 4f - contact * 1.5f);
        RotateWorld(bones, "頭", root.right, weight * 6f + contact);
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            // Tighten the shoulder line through already-animated upper arms.
            // Adding shoulder-only bindings here would leave them raised when
            // this one-shot is interrupted by a preserved older clip.
            Vector3 tightUpper = new Vector3(sign * (0.25f + contact * 0.035f), -1 + contact * 0.14f, -0.13f);
            Vector3 tightForearm = new Vector3(sign * 0.10f, -0.82f + contact * 0.07f, -0.42f);
            Aim(upper, hand.elbow, root.TransformDirection(Vector3.Slerp(hand.neutralUpperDirection.normalized, tightUpper.normalized, weight)));
            Aim(hand.elbow, hand.hand, root.TransformDirection(Vector3.Slerp(hand.neutralForearmDirection.normalized, tightForearm.normalized, weight)));
            // Both fists close during anticipation, squeeze at foot contact,
            // then release on the same smooth window as the shoulders.
            PoseCurledRelaxedHand(root, hand, t, sign, weight,
                new Vector3(42, 56, 36) + contact * new Vector3(6, 8, 4), new Vector3(18, 26, 20));
        }
    }

    private static void PoseCurledRelaxedHand(Transform root, HandRig hand, float t, float sign, float weight, Vector3 curl, Vector3 thumb)
    {
        // Keep the proven relaxed palm and twist transport while changing only
        // finger flexion. This avoids a palm-axis flip during a short squeeze.
        PoseHand(root, hand, "Idle", t, 0, sign, hand.hand.position - hand.elbow.position);
        foreach (FingerJoint joint in hand.joints)
        {
            Vector3 neutral = joint.thumb ? new Vector3(8, 12, 9) : new Vector3(9, 16, 11);
            float degrees = Mathf.Lerp(neutral[joint.joint], (joint.thumb ? thumb : curl)[joint.joint], weight) * joint.curlScale;
            joint.bone.localRotation = joint.restRotation * Quaternion.AngleAxis(degrees, joint.curlAxis);
        }
        hand.poseWeight = weight;
        Vector3 fingers = hand.hand.TransformDirection(hand.fingerAxis), palm = hand.hand.TransformDirection(hand.palmAxis);
        hand.hand.localRotation = hand.restLocalRotation; OrientHand(hand, fingers, palm);
    }

    private static void PoseGuard(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands,
        ArmClearance clearance, string action, float t, bool measureRoll = false)
    {
        float body = GuardWindow(t);
        RotateWorld(bones, "上半身2", root.right, body * 5f);
        RotateWorld(bones, "上半身2", root.up, body * 7f);
        RotateWorld(bones, "頭", root.up, body * 12f);
        RotateWorld(bones, "頭", root.right, -body * 3f);
        RotateWorld(bones, "頭", root.forward, -body * 2f);
        Vector3 hip = root.InverseTransformPoint(PelvisPoint(bones));
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side]; Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            float upperLength = Vector3.Distance(upper.position, hand.elbow.position), lowerLength = Vector3.Distance(hand.elbow.position, hand.hand.position);
            float length = upperLength + lowerLength;
            Aim(upper, hand.elbow, root.TransformDirection(hand.neutralUpperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(hand.neutralForearmDirection));
            Quaternion neutralUpperRotation = upper.localRotation, neutralElbowRotation = hand.elbow.localRotation;
            Vector3 neutral = hand.hand.position;
            Vector3 shoulder = root.InverseTransformPoint(upper.position);
            float torso = Mathf.Max(0.12f, shoulder.y - hip.y);
            bool lowerGuard = action == "GuardLower";
            Vector3 cover = root.TransformPoint(new Vector3(sign * hand.palmRadius * (lowerGuard ? 1.5f : 1.8f),
                lowerGuard ? hip.y - torso * (side == "R" ? 0.04f : 0.12f) : hip.y + torso * (side == "R" ? 0.80f : 0.40f), hip.z));
            cover = clearance.ChestFrontTarget(root, cover, hand.palmRadius + hand.fingerLength * 0.38f, sign);
            Vector3 controlA = neutral + root.TransformDirection(new Vector3(sign * length * 0.18f, length * 0.18f, 0));
            Vector3 controlB = cover + root.TransformDirection(new Vector3(sign * length * 0.20f, -length * 0.08f, 0));
            // A single Bezier corridor takes the hand outward/front before it
            // folds inward. No nearest-side/forearm shell is reselected per frame.
            float front = Mathf.Min(root.InverseTransformPoint(cover).z, root.InverseTransformPoint(neutral).z);
            Vector3 a = root.InverseTransformPoint(controlA); a.z = front;
            Vector3 b = root.InverseTransformPoint(controlB); b.z = front;
            controlA = root.TransformPoint(a); controlB = root.TransformPoint(b);
            float weight = GuardHandWindow(t, side), inverse = 1 - weight;
            Vector3 target = neutral * (inverse * inverse * inverse) + controlA * (3 * inverse * inverse * weight)
                + controlB * (3 * inverse * weight * weight) + cover * (weight * weight * weight);
            target = LimitProtectiveReach(upper, neutral, target, weight, length);
            Vector3 neutralElbow = hand.elbow.position - upper.position;
            if (weight > 0.000001f) SolveArm(upper, hand, target, StableArmPole(root, upper, hand, neutral, neutralElbow, target, weight, sign), clearance);
            if (lowerGuard)
            {
                // Near the exact standing seam a nearly extended IK elbow has
                // an ill-conditioned sqrt derivative. Settle in joint space
                // with a C2 blend instead of solving that cusp to the last frame.
                float settle = SmoothPhase(weight / 0.50f);
                upper.localRotation = Quaternion.Slerp(neutralUpperRotation, upper.localRotation, settle);
                hand.elbow.localRotation = Quaternion.Slerp(neutralElbowRotation, hand.elbow.localRotation, settle);
            }
            PoseFixedGripHand(root, hand, action, weight, sign, measureRoll);
        }
    }

    private static Vector3 StableArmPole(Transform root, Transform upper, HandRig hand, Vector3 neutralWrist, Vector3 neutralElbow,
        Vector3 target, float weight, float sign)
    {
        Vector3 neutralReach = (neutralWrist - upper.position).normalized, reach = (target - upper.position).normalized;
        Vector3 neutralPole = Vector3.ProjectOnPlane(neutralElbow, neutralReach).normalized;
        if (neutralPole.sqrMagnitude < 0.1f) neutralPole = Vector3.ProjectOnPlane(root.TransformDirection(new Vector3(sign, 0, 0.2f)), neutralReach).normalized;
        Vector3 transported = Quaternion.FromToRotation(neutralReach, reach) * neutralPole;
        Vector3 anatomical = Vector3.ProjectOnPlane(root.TransformDirection(new Vector3(sign, -0.40f, -0.25f)), reach).normalized;
        float turn = Vector3.SignedAngle(transported, anatomical, reach);
        if (hand.hasPreviousPoleTurn) turn = hand.previousPoleTurn + Mathf.DeltaAngle(hand.previousPoleTurn, turn);
        hand.previousPoleTurn = turn; hand.hasPreviousPoleTurn = true;
        turn = Mathf.Clamp(turn, -120, 120);
        return Quaternion.AngleAxis(turn * SmoothPhase(weight), reach) * transported;
    }

    private static Vector3 LimitProtectiveReach(Transform upper, Vector3 neutralWrist, Vector3 target, float weight, float length)
    {
        Vector3 reach = target - upper.position;
        float neutralDistance = Vector3.Distance(upper.position, neutralWrist);
        float maximum = Mathf.Lerp(neutralDistance, Mathf.Min(neutralDistance, length * 0.94f), SmoothPhase(weight));
        float smoothing = length * 0.012f * weight;
        // Smooth minimum, with its radius vanishing at the exact Idle seam.
        // Retain elbow bend instead of crossing the full-extension sqrt cusp.
        float distance = (reach.magnitude + maximum - Mathf.Sqrt((reach.magnitude - maximum) * (reach.magnitude - maximum) + smoothing * smoothing)) * 0.5f;
        return upper.position + reach.normalized * Mathf.Max(0.0001f, distance);
    }

    private static void PoseFixedGripHand(Transform root, HandRig hand, string action, float weight, float sign, bool measureRoll)
    {
            hand.hand.localRotation = hand.restLocalRotation;
            Vector3 forearm = (hand.hand.position - hand.elbow.position).normalized;
            Vector3 neutralForearm = root.TransformDirection(hand.neutralForearmDirection).normalized;
            Vector3 neutralPalm = Vector3.ProjectOnPlane(root.TransformDirection(new Vector3(-sign, 0.04f, -0.12f)), neutralForearm).normalized;
            // Parallel-transport the resting palm as the elbow turns. Projecting
            // an arbitrary global palm axis onto each forearm can vanish and
            // switch sides midway through the protective fold.
            Vector3 startPalm = Quaternion.FromToRotation(neutralForearm, forearm) * neutralPalm;
            if (measureRoll)
            {
                Vector3 intended = IsGuardAction(action) ? root.forward : root.TransformDirection(new Vector3(-sign * 0.42f, 0, -0.78f));
                Vector3 endPalm = Vector3.ProjectOnPlane(intended, forearm).normalized;
                hand.guardRoll = Vector3.SignedAngle(startPalm, endPalm, forearm);
            }
            Vector3 palm = Quaternion.AngleAxis(hand.guardRoll * weight, forearm) * startPalm;
            hand.poseWeight = weight;
            foreach (FingerJoint joint in hand.joints)
            {
                Vector3 neutralCurl = joint.thumb ? new Vector3(8, 12, 9) : new Vector3(9, 16, 11);
                Vector3 coverCurl = IsGuardAction(action) ? joint.thumb ? new Vector3(10, 16, 11) : new Vector3(12, 22, 14)
                    : joint.thumb ? new Vector3(20, 30, 22) : sign < 0 ? new Vector3(48, 65, 42) : new Vector3(36, 48, 32);
                joint.bone.localRotation = joint.restRotation * Quaternion.AngleAxis(Mathf.Lerp(neutralCurl[joint.joint], coverCurl[joint.joint], weight) * joint.curlScale, joint.curlAxis);
            }
            OrientHand(hand, forearm, palm);
    }

    private static void PoseSword(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands,
        ArmClearance clearance, string action, float t, bool measureRoll = false, int snapshotStage = -1)
    {
        bool holdSword = action == "HoldSword";
        float gesture = ArmGestureWeight(action, "R", t);
        float draw = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.18f, 0.40f, t));
        float slash = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.44f, 0.64f, t));
        if (snapshotStage >= 0) { gesture = 1; draw = snapshotStage >= 1 ? 1 : 0; slash = snapshotStage >= 2 ? 1 : 0; }
        RotateWorld(bones, "上半身2", root.right, (holdSword ? 1.5f : 2.5f) * gesture);
        RotateWorld(bones, "上半身2", root.up, (holdSword ? -3f : 4f - 8f * slash) * gesture);
        RotateWorld(bones, "頭", root.up, -2f * gesture);

        Transform rightShoulder = bones["腕.R"];
        Vector3 right = root.InverseTransformPoint(rightShoulder.position);
        float rightSign = Mathf.Sign(right.x);
        Vector3 hip = root.InverseTransformPoint(PelvisPoint(bones));
        float rightLength = Vector3.Distance(rightShoulder.position, hands["R"].elbow.position) + Vector3.Distance(hands["R"].elbow.position, hands["R"].hand.position);
        Vector3 rightHold = new Vector3(right.x + rightSign * rightLength * 0.08f, right.y - rightLength * 0.10f, hip.z - rightLength * 0.40f);
        Vector3 drawStart = new Vector3(right.x * 0.80f, hip.y + rightLength * 0.09f, hip.z - rightLength * 0.38f);
        Vector3 drawHigh = new Vector3(right.x + rightSign * rightLength * 0.15f, right.y - rightLength * 0.06f, hip.z - rightLength * 0.43f);
        Vector3 slashEnd = new Vector3(right.x + rightSign * rightLength * 0.34f, hip.y - rightLength * 0.02f, hip.z - rightLength * 0.44f);

        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side];
            Transform upper = bones["腕." + side];
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            float upperLength = Vector3.Distance(upper.position, hand.elbow.position), lowerLength = Vector3.Distance(hand.elbow.position, hand.hand.position);
            float length = upperLength + lowerLength;
            Aim(upper, hand.elbow, root.TransformDirection(hand.neutralUpperDirection));
            Aim(hand.elbow, hand.hand, root.TransformDirection(hand.neutralForearmDirection));
            float weight = ArmGestureWeight(action, side, t);
            if (snapshotStage >= 0) weight = side == "L" && snapshotStage >= 2 ? 0 : 1;
            Vector3 neutral = hand.hand.position;
            Vector3 neutralElbow = hand.elbow.position - upper.position;
            Vector3 target;
            if (side == "R") target = holdSword ? rightHold : Vector3.Lerp(Vector3.Lerp(drawStart, drawHigh, draw), slashEnd, slash);
            else target = holdSword ? rightHold + new Vector3(-rightSign * length * 0.13f, -length * 0.19f, -length * 0.02f)
                : new Vector3(sign * length * 0.12f, hip.y + length * 0.08f, hip.z - length * 0.40f);
            Vector3 desired = root.TransformPoint(target);
            if (clearance != null) desired = clearance.ChestFrontTarget(root, desired, hand.palmRadius + hand.fingerLength * 0.18f, sign);
            if (weight > 0.0001f)
            {
                // Both wrists approach in front of the chest. The left supports
                // below the right hilt grip; the right remains outside the head
                // silhouette and never traces a sword path around the face.
                Vector3 arc = root.TransformDirection(new Vector3(sign * length * 0.10f, 0, -length * 0.12f)) * Mathf.Sin(weight * Mathf.PI);
                Vector3 targetWrist = Vector3.Lerp(neutral, desired, weight) + arc;
                targetWrist = LimitProtectiveReach(upper, neutral, targetWrist, weight, length);
                SolveArm(upper, hand, targetWrist, StableArmPole(root, upper, hand, neutral, neutralElbow, targetWrist, weight, sign), clearance);
            }
            PoseFixedGripHand(root, hand, action, weight, sign, measureRoll);
        }
    }

    private sealed class SwordKeyPose
    {
        public float phase;
        public Quaternion[] rotations;
        public Quaternion[] worldRotations;
        public int[] order;
        public Vector3 center;
    }

    private static SwordKeyPose[] CreateSwordKeyPoses(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands,
        ArmClearance clearance, string action, Transform[] driven, Dictionary<Transform, Quaternion> rest,
        Dictionary<Transform, Vector3> restPositions, bool compatible, float nominalSeconds, out float totalSeconds)
    {
        void Reset()
        {
            foreach (Transform bone in driven) { bone.localRotation = rest[bone]; bone.localPosition = restPositions[bone]; }
            foreach (HandRig hand in hands.Values) { hand.hasPreviousTwist = hand.hasPreviousPalmBlend = hand.hasPreviousPoleTurn = false; }
        }
        SwordKeyPose Capture(float phase, float poseTime, int stage)
        {
            Reset();
            if (stage < 0)
            {
                Pose(root, bones, hands, "Idle", 0, compatible, clearance);
                ApplyArmClearance(root, bones, hands, clearance, "Idle", 0);
            }
            else
            {
                // IK and palm fitting run once for each explicit safe key pose;
                // interpolation never recomputes branches between these poses.
                PoseLowerBody(root, bones, action, poseTime, compatible);
                PoseSword(root, bones, hands, clearance, action, poseTime, true, stage);
                Reset();
                PoseLowerBody(root, bones, action, poseTime, compatible);
                PoseSword(root, bones, hands, clearance, action, poseTime, false, stage);
            }
            return new SwordKeyPose { phase = phase, rotations = driven.Select(bone => bone.localRotation).ToArray(),
                worldRotations = driven.Select(bone => Quaternion.Inverse(root.rotation) * bone.rotation).ToArray(), center = bones["センター"].localPosition };
        }
        SwordKeyPose[] poses;
        if (action == "HoldSword")
        {
            var idle = Capture(0, 0, -1); var hold = Capture(0.32f, 0.5f, 1);
            poses = new[] { idle, hold, new SwordKeyPose { phase = 0.70f, rotations = hold.rotations, worldRotations = hold.worldRotations, center = hold.center },
                new SwordKeyPose { phase = 1, rotations = idle.rotations, worldRotations = idle.worldRotations, center = idle.center } };
        }
        else poses = new[] { Capture(0, 0, -1), Capture(0.22f, 0.22f, 0), Capture(0.44f, 0.44f, 1), Capture(0.64f, 0.64f, 2), Capture(1, 1, -1) };
        int Depth(Transform bone) { int depth = 0; for (Transform at = bone; at != null && at != root; at = at.parent) depth++; return depth; }
        int[] order = Enumerable.Range(0, driven.Length).OrderBy(index => Depth(driven[index])).ToArray();
        float[] times = new float[poses.Length];
        // A deliberate slash has a faster cadence than lifting a held sword.
        // Keep the same C2 easing and bound every actual bone orientation,
        // including fingers, without giving all four strokes the hold's tempo.
        float strokeWorldSpeed = action == "DrawSlash" ? 175f : 120f;
        for (int index = 1; index < poses.Length; index++)
        {
            float maximumAngle = Enumerable.Range(0, driven.Length).Max(bone => Quaternion.Angle(poses[index - 1].worldRotations[bone], poses[index].worldRotations[bone]));
            // Quintic easing peaks at 1.875. Give each stroke its own smooth
            // duration instead of slowing every other stroke for one long turn.
            float seconds = Mathf.Max(nominalSeconds * (poses[index].phase - poses[index - 1].phase), maximumAngle * 1.875f / strokeWorldSpeed);
            times[index] = times[index - 1] + seconds;
        }
        totalSeconds = times.Last();
        for (int index = 0; index < poses.Length; index++) { poses[index].phase = times[index] / totalSeconds; poses[index].order = order; }
        Reset();
        return poses;
    }

    private static void SampleSwordKeyPoses(Transform root, Transform[] driven, Transform center, SwordKeyPose[] poses, float phase)
    {
        int index = 0;
        while (index + 2 < poses.Length && phase > poses[index + 1].phase) index++;
        var a = poses[index]; var b = poses[index + 1];
        float weight = SmoothPhase((phase - a.phase) / (b.phase - a.phase));
        // Blend each actual bone orientation, then let Unity derive its local
        // joint rotation from the already blended parent. This avoids adding a
        // wrist's compensating twist to an interpolated forearm roll twice.
        foreach (int bone in a.order) driven[bone].rotation = root.rotation * Quaternion.Slerp(a.worldRotations[bone], b.worldRotations[bone], weight);
        center.localPosition = Vector3.Lerp(a.center, b.center, weight);
    }

    private static Vector3 RelaxedForearmDirection(float sign)
    {
        // A soft elbow, wrist below it and palm toward the thigh. This is a
        // direction in character space, never an assumed bone-local Euler axis.
        return new Vector3(sign * 0.035f, -1, -0.12f);
    }

    private static float ArmGestureWeight(string action, string side, float t)
    {
        switch (action)
        {
            case "Walk": case "Run": case "Drag": return 1;
            case "Jump": return GestureWindow(t, 0.27f, 0.63f);
            case "HoldSword": return SmoothPhase(t / 0.34f) * (1 - SmoothPhase((t - 0.70f) / 0.30f));
            case "DrawSlash": return SmoothPhase(t / 0.30f) * (1 - SmoothPhase((t - (side == "R" ? 0.80f : 0.50f)) / (side == "R" ? 0.20f : 0.50f)));
            case "Wave": case "Surprised": case "Think":
                return side == "R" ? GestureWindow(t, 0.24f, 0.72f) : 0;
            case "Speak": return side == "L" ? GestureWindow(t, 0.24f, 0.72f) : 0;
            case "Happy": return GestureWindow(t, 0.24f, 0.72f);
            case "TiltHead": return side == "R" ? GestureWindow(t, 0.32f, 0.68f) : 0;
            case "Shy": return GestureWindow(t, side == "R" ? 0.28f : 0.33f, 0.7f);
            case "Stretch": return GestureWindow(t, 0.35f, 0.71f);
            case "Cheer": return GestureWindow(t, 0.34f, 0.66f);
            case "Guard": case "GuardLower": return GuardHandWindow(t, side);
            default: return 0;
        }
    }

    private static void PoseStandingGesture(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, string action, float t, bool compatible = false)
    {
        // Every gesture starts and ends in the same relaxed pose. Smooth windows
        // separate anticipation, the held expression, and release without a loop.
        float hold = GestureWindow(t, 0.24f, 0.74f);
        float headPitch = 0, headYaw = 0, headRoll = 0, chestPitch = 0, chestYaw = 0;
        float nod = GesturePulse(t, 0.1f, 0.24f, 0.42f) + 0.65f * GesturePulse(t, 0.43f, 0.57f, 0.76f);
        float pump = GesturePulse(t, 0.22f, 0.32f, 0.44f) + 0.75f * GesturePulse(t, 0.47f, 0.58f, 0.71f);
        switch (action)
        {
            case "Nod":
                headPitch = nod * 10f; chestPitch = nod * 0.8f;
                break;
            case "TiltHead":
                headRoll = hold * 11f;
                headPitch = -hold * 1.5f;
                headYaw = GesturePulse(t, 0.27f, 0.45f, 0.69f) * 3f;
                break;
            case "Shy":
                headPitch = hold * 7f + GesturePulse(t, 0.4f, 0.51f, 0.68f) * 2f;
                headYaw = -GesturePulse(t, 0.2f, 0.4f, 0.76f) * 6f;
                chestPitch = hold * 1.6f;
                break;
            case "Stretch":
                hold = GestureWindow(t, 0.35f, 0.71f);
                headPitch = 2f * GesturePulse(t, 0, 0.12f, 0.25f) - hold * 4f;
                chestPitch = -hold * 2.5f - GesturePulse(t, 0.43f, 0.55f, 0.68f) * 0.8f;
                break;
            case "Cheer":
                hold = GestureWindow(t, 0.34f, 0.66f);
                headPitch = -hold * 2f + pump * 2.5f;
                chestPitch = -hold * 0.8f;
                break;
            case "LookAround":
                float glance = -GesturePulse(t, 0.08f, 0.25f, 0.52f) + GesturePulse(t, 0.43f, 0.64f, 0.91f);
                chestYaw = glance * 2.5f;
                headYaw = glance * 15.5f;
                headPitch = -GesturePulse(t, 0.12f, 0.29f, 0.47f) * 1.5f;
                break;
            case "Guard": case "GuardLower":
                hold = GestureWindow(t, 0.18f, 0.73f);
                chestPitch = hold * 7f;
                chestYaw = hold * 9f;
                headYaw = hold * 19f;
                headPitch = -hold * 5f;
                headRoll = -hold * 4f;
                break;
        }
        RotateWorld(bones, "上半身2", root.right, chestPitch);
        RotateWorld(bones, "上半身2", root.up, chestYaw);
        RotateWorld(bones, "頭", root.right, headPitch);
        RotateWorld(bones, "頭", root.up, headYaw);
        RotateWorld(bones, "頭", root.forward, headRoll);

        foreach (string side in new[] { "R", "L" })
        {
            if (!bones.TryGetValue("腕." + side, out Transform upper)
                || !bones.TryGetValue("ひじ." + side, out Transform elbow)
                || !bones.TryGetValue("手首." + side, out Transform hand)) continue;
            float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
            Vector3 relaxedUpper = hands[side].neutralUpperDirection;
            Vector3 relaxedLower = hands[side].neutralForearmDirection;
            Vector3 raisedUpper = relaxedUpper, raisedLower = relaxedLower;
            float weight = hold;
            switch (action)
            {
                case "TiltHead":
                    if (side == "R")
                    {
                        raisedUpper = new Vector3(sign * 0.3f, -0.9f, -0.3f);
                        raisedLower = new Vector3(sign * 0.3f, 0.1f, -1f);
                        weight = GestureWindow(t, 0.32f, 0.68f);
                    }
                    break;
                case "Shy":
                    // Keep each wrist on its own side and in front of the chest.
                    // The forearms lift forward rather than crossing through it.
                    raisedUpper = new Vector3(sign * 0.22f, -0.8f, -0.55f);
                    raisedLower = new Vector3(-sign * 0.22f, 0.92f, -0.36f);
                    weight = GestureWindow(t, side == "R" ? 0.28f : 0.33f, 0.7f);
                    break;
                case "Stretch":
                    // Raise through the outside of the silhouette into an open V.
                    raisedUpper = new Vector3(sign * 0.58f, 0.84f, -0.08f);
                    raisedLower = new Vector3(sign * 0.26f, 1f, -0.08f);
                    break;
                case "Cheer":
                    raisedUpper = new Vector3(sign * 0.62f, -0.44f + pump * 0.15f, -0.24f);
                    raisedLower = new Vector3(sign * 0.14f, 0.96f, -0.17f);
                    break;
                case "GuardLower":
                    // Both hands settle in front of the lower abdomen, below
                    // the waist; elbow bend stays soft and the shoulders stay low.
                    raisedUpper = new Vector3(sign * 0.22f, -0.94f, -0.25f);
                    raisedLower = new Vector3(-sign * 0.50f, -0.77f, -0.30f);
                    weight = GestureWindow(t, side == "R" ? 0.17f : 0.21f, 0.73f);
                    break;
                case "Guard":
                    // Staggered forearms cover the upper chest and abdomen. The
                    // elbows move forward first, keeping the hands outside the mesh.
                    raisedUpper = new Vector3(sign * 0.16f, -0.86f, -0.48f);
                    raisedLower = side == "R" ? new Vector3(-sign * 0.68f, 0.60f, -0.25f)
                        : new Vector3(-sign * 0.82f, -0.28f, -0.24f);
                    weight = GestureWindow(t, side == "R" ? 0.17f : 0.21f, 0.73f);
                    break;
            }
            Aim(upper, elbow, root.TransformDirection(Vector3.Slerp(relaxedUpper.normalized, raisedUpper.normalized, weight)));
            Aim(elbow, hand, root.TransformDirection(Vector3.Slerp(relaxedLower.normalized, raisedLower.normalized, weight)));
            PoseHand(root, hands[side], action, t, weight, sign, hand.position - elbow.position);
        }
    }

    private sealed class FingerJoint
    {
        public Transform bone;
        public Vector3 curlAxis;
        public int joint;
        public float curlScale;
        public bool thumb;
        public Quaternion restRotation;
    }

    private sealed class HandRig
    {
        public Transform hand, elbow, twist;
        public Transform[] twistSegments;
        public Vector3 fingerAxis, palmAxis;
        public FingerJoint[] joints;
        public bool hasPreviousTwist;
        public float previousTwist;
        public bool hasPreviousPalmBlend;
        public float previousPalmBlend;
        public float poseWeight;
        public float guardRoll;
        public float previousPoleTurn;
        public bool hasPreviousPoleTurn;
        public Quaternion restLocalRotation;
        public float palmRadius, fingerLength, sleeveRadius;
        public float upperRadius;
        public int upperSampleCount, forearmSampleCount, palmSampleCount;
        public Vector3 neutralUpperDirection;
        public Vector3 neutralForearmDirection;
        public float neutralHandResidual;
        public bool upperProbeEnabled = true, forearmProbeEnabled = true;
        public string measurementStatus = "accepted";
        public float neutralForearmCorrection;
        public string penetrationProbe;
        public Vector3 penetrationPoint;
        public IEnumerable<Transform> DrivenBones
        {
            get { return joints.Select(joint => joint.bone).Concat(twistSegments).Append(twist); }
        }
    }

    private static Dictionary<string, HandRig> CreateHandRigs(Dictionary<string, Transform> bones, bool compatible = false)
    {
        var result = new Dictionary<string, HandRig>();
        foreach (string side in new[] { "R", "L" })
        {
            if (compatible)
            {
                result.Add(side, CreateCompatibleHandRig(bones, side));
                continue;
            }
            Func<string, Transform> required = name => bones.TryGetValue(name + "." + side, out Transform bone) ? bone
                : throw new InvalidOperationException("Lita hand animation requires bone: " + name + "." + side);
            Transform wrist = required("手首");
            // The Lita FBX points finger bones along local Y, but each imported
            // wrist has its own roll. Measure the real metacarpal plane instead
            // of assuming a shared X/Y/Z wrist axis. Mirrored winding is explicit.
            Vector3 finger = (required("中指１").position - wrist.position).normalized;
            Vector3 spread = required("人指１").position - required("小指１").position;
            Vector3 palm = Vector3.Cross(finger, spread).normalized * (side == "R" ? 1f : -1f);
            if (palm.sqrMagnitude < 0.5f) throw new InvalidOperationException("Degenerate Lita palm plane: " + side);
            var hand = new HandRig
            {
                hand = wrist, elbow = required("ひじ"), twist = required("手捩"),
                twistSegments = new[] { required("手捩1"), required("手捩2"), required("手捩3") },
                fingerAxis = wrist.InverseTransformDirection(finger),
                palmAxis = wrist.InverseTransformDirection(palm)
            };
            var joints = new List<FingerJoint>();
            string[] fingers = { "人指", "中指", "薬指", "小指", "親指" };
            for (int digit = 0; digit < fingers.Length; digit++)
                for (int joint = 0; joint < 3; joint++)
                {
                    bool thumb = digit == 4;
                    // MMD uses full-width digits for fingers, ASCII for twist bones.
                    string name = fingers[digit] + (char)('０' + (thumb ? joint : joint + 1));
                    Transform bone = required(name);
                    Transform child;
                    // End-bone names put the side before _end, unlike normal joints.
                    if (joint == 2)
                    {
                        if (!bones.TryGetValue(name + "." + side + "_end", out child))
                            throw new InvalidOperationException("Missing Lita fingertip: " + name + "." + side);
                    }
                    else child = required(fingers[digit] + (char)('０' + (thumb ? joint + 1 : joint + 2)));
                    Vector3 towardPalm = palm;
                    if (thumb) towardPalm = (required("中指１").position - child.position).normalized + palm * 0.45f;
                    Vector3 axis = Vector3.Cross((child.position - bone.position).normalized, towardPalm).normalized;
                    joints.Add(new FingerJoint { bone = bone, curlAxis = bone.InverseTransformDirection(axis), joint = joint,
                        curlScale = thumb ? 1 : 0.85f + digit * 0.1f, thumb = thumb });
                }
            hand.joints = joints.ToArray();
            result.Add(side, hand);
        }
        return result;
    }

    private static HandRig CreateCompatibleHandRig(Dictionary<string, Transform> bones, string side)
    {
        Transform Find(string name) { return bones.TryGetValue(name + "." + side, out Transform found) ? found : null; }
        Transform wrist = Find("手首"), elbow = Find("ひじ");
        if (wrist == null || elbow == null) throw new InvalidOperationException("Imported model has no wrist/elbow on " + side);
        Transform middle = Find("中指１"), index = Find("人指１"), pinky = Find("小指１");
        Vector3 finger = middle != null ? middle.position - wrist.position : wrist.position - elbow.position;
        Vector3 spread = index != null && pinky != null ? index.position - pinky.position : wrist.right;
        Vector3 palm = Vector3.Cross(finger.normalized, spread).normalized * (side == "R" ? 1 : -1);
        if (palm.sqrMagnitude < 0.5f) palm = Vector3.ProjectOnPlane(wrist.forward, finger).normalized;
        if (palm.sqrMagnitude < 0.5f) palm = Vector3.ProjectOnPlane(wrist.up, finger).normalized;
        var result = new HandRig { hand = wrist, elbow = elbow, twist = Find("手捩"),
            twistSegments = new[] { Find("手捩1"), Find("手捩2"), Find("手捩3") }.Where(t => t != null).ToArray(),
            fingerAxis = wrist.InverseTransformDirection(finger.normalized), palmAxis = wrist.InverseTransformDirection(palm) };
        var joints = new List<FingerJoint>();
        string[] digits = { "人指", "中指", "薬指", "小指", "親指" };
        for (int digit = 0; digit < digits.Length; digit++)
            for (int joint = 0; joint < 3; joint++)
            {
                bool thumb = digit == 4;
                string name = digits[digit] + (char)('０' + joint + (thumb ? 0 : 1));
                Transform bone = Find(name);
                if (bone == null) continue;
                Transform child = joint < 2 ? Find(digits[digit] + (char)('０' + joint + (thumb ? 1 : 2))) : null;
                if (child == null) bones.TryGetValue(name + "." + side + "_end", out child);
                if (child == null && bone.childCount > 0) child = bone.GetChild(0);
                Vector3 direction = child != null ? child.position - bone.position : bone.position - bone.parent.position;
                Vector3 towardPalm = thumb && middle != null ? (middle.position - bone.position).normalized + palm * 0.45f : palm;
                Vector3 axis = Vector3.Cross(direction.normalized, towardPalm).normalized;
                if (axis.sqrMagnitude < 0.5f) continue;
                joints.Add(new FingerJoint { bone = bone, curlAxis = bone.InverseTransformDirection(axis), joint = joint,
                    curlScale = thumb ? 1 : 0.85f + digit * 0.1f, thumb = thumb });
            }
        result.joints = joints.ToArray();
        return result;
    }

    private static void PoseHand(Transform root, HandRig hand, string action, float t, float weight, float sign, Vector3 forearm, bool final = false)
    {
        hand.poseWeight = weight;
        Vector3 relaxedPalm = root.TransformDirection(new Vector3(-sign, 0.04f, -0.12f));
        Vector3 fingers = forearm.normalized;
        Vector3 palm = relaxedPalm;
        Vector3 relaxedCurl = new Vector3(9, 16, 11), curl = relaxedCurl;
        Vector3 relaxedThumb = new Vector3(8, 12, 9), thumb = relaxedThumb;
        float wave = Mathf.Sin(t * Mathf.PI * 8f);
        switch (action)
        {
            case "Wave": case "Surprised":
                if (sign < 0)
                {
                    fingers = root.TransformDirection(new Vector3(sign * (0.1f + wave * 0.24f), 1, -0.02f));
                    palm = -root.forward;
                    curl = new Vector3(3, 6, 4);
                    thumb = new Vector3(2, 5, 4);
                }
                break;
            case "Happy": case "Cheer":
                palm = root.TransformDirection(new Vector3(-sign * 0.35f, 0, 1));
                curl = new Vector3(46, 60, 36);
                thumb = new Vector3(18, 28, 22);
                break;
            case "Think":
                if (sign < 0)
                {
                    palm = root.TransformDirection(new Vector3(-sign * 0.35f, 0, 1));
                    curl = new Vector3(24, 34, 20);
                    thumb = new Vector3(10, 16, 12);
                }
                break;
            case "Speak": case "TiltHead":
                if (action == "Speak" ? sign > 0 : sign < 0)
                {
                    palm = root.up;
                    curl = new Vector3(8, 13, 8);
                }
                break;
            case "Shy":
                palm = root.TransformDirection(new Vector3(-sign * 0.2f, 0, 1));
                curl = new Vector3(18, 28, 18);
                thumb = new Vector3(12, 18, 12);
                break;
            case "Stretch":
                palm = -root.forward;
                curl = new Vector3(4, 7, 4);
                thumb = new Vector3(3, 7, 5);
                break;
            case "Drag":
                palm = root.TransformDirection(new Vector3(-sign * 0.35f, -0.45f, -0.5f));
                curl = new Vector3(18, 30, 20);
                thumb = new Vector3(12, 18, 13);
                break;
            case "HoldSword": case "DrawSlash":
                // A closed right grip and a softer supporting left grip. The
                // attachment chooses the blade axis; palm roll stays anatomical.
                palm = root.TransformDirection(new Vector3(-sign * 0.42f, 0, -0.78f));
                curl = sign < 0 ? new Vector3(48, 65, 42) : new Vector3(36, 48, 32);
                thumb = new Vector3(20, 30, 22);
                break;
            case "GuardLower":
                fingers = root.TransformDirection(new Vector3(-sign * 0.6f, -0.8f, 0));
                palm = root.forward;
                curl = new Vector3(8, 15, 10);
                thumb = new Vector3(8, 12, 8);
                break;
            case "Guard":
                fingers = root.TransformDirection(new Vector3(-sign, sign < 0 ? 0.12f : -0.1f, 0.04f));
                palm = root.forward;
                curl = new Vector3(12, 22, 14);
                thumb = new Vector3(10, 16, 11);
                break;
        }
        Vector3 startFinger = forearm.normalized;
        Vector3 endFinger = Vector3.RotateTowards(startFinger, fingers.normalized, 42 * Mathf.Deg2Rad, 0);
        fingers = Vector3.Slerp(startFinger, endFinger, weight).normalized;
        Vector3 startPalm = Vector3.ProjectOnPlane(relaxedPalm, fingers).normalized;
        Vector3 endPalm = Vector3.ProjectOnPlane(palm, fingers).normalized;
        if (endPalm.sqrMagnitude < 0.5f) endPalm = startPalm;
        float palmTurn = Vector3.SignedAngle(startPalm, endPalm, fingers);
        if (hand.hasPreviousPalmBlend) palmTurn = hand.previousPalmBlend + Mathf.DeltaAngle(hand.previousPalmBlend, palmTurn);
        if (final) { hand.previousPalmBlend = palmTurn; hand.hasPreviousPalmBlend = true; }
        // Interpolate pronation on one continuous angular branch. Quaternion
        // shortest-path interpolation can itself flip when the palms oppose.
        palm = Quaternion.AngleAxis(palmTurn * weight, fingers) * startPalm;
        // Preview the palm for collision probes. Forearm twist is distributed
        // exactly once, after the complete torso/head pose and arm correction.
        PreviewHand(hand, fingers, palm);
        curl = Vector3.Lerp(relaxedCurl, curl, weight);
        thumb = Vector3.Lerp(relaxedThumb, thumb, weight);
        foreach (FingerJoint joint in hand.joints)
        {
            float degrees = (joint.thumb ? thumb : curl)[joint.joint] * joint.curlScale;
            joint.bone.localRotation = joint.restRotation * Quaternion.AngleAxis(degrees, joint.curlAxis);
        }
    }

    [Serializable] private sealed class ArmClearanceReport
    {
        public string model;
        public string solverVersion;
        public string method = "Neutral shoulders stay within 12 degrees of down; lower arms are fitted to visible hip/skirt slices using wrist, palm and finger probes. Hand safety is active on every frame, independently of weighted sleeve clearance. Envelopes are conservative; inspect the rendered mesh as well.";
        public int bodyVertexCount;
        public float gapMeters;
        public BakeMeasurement[] bakes;
        public SurfaceMeasurement[] surfaces;
        public ActionClearanceReport[] actions;
        public LimbMeasurement[] limbs;
    }
    [Serializable] private sealed class LimbMeasurement
    {
        public string side;
        public float upperRadius, forearmRadius, palmRadius;
        public int upperSamples, forearmSamples, palmSamples;
        public Vector3 neutralUpperDirection;
        public Vector3 neutralForearmDirection;
        public float neutralHandResidual;
        public bool upperProbeEnabled, forearmProbeEnabled;
        public string measurementStatus;
        public float neutralForearmCorrection;
    }
    [Serializable] private sealed class BakeMeasurement
    {
        public string renderer, coordinateMode;
        public Vector3 bakedSize, rendererWorldSize, calibratedWorldSize;
        public float logScaleError;
    }
    [Serializable] private sealed class SurfaceMeasurement
    {
        public string anchor;
        public Vector3 min, max;
    }
    [Serializable] private sealed class ActionClearanceReport
    {
        public string action, jumpBone;
        public string worldSpeedBone;
        public float maxAuthoredWorldSpeedDegreesPerSecond, worldSpeedAtProgress;
        public int sampleCount;
        public float maxResidualMeters, residualAtSeconds, maxAdjacentDegrees, jumpAtSeconds;
        public string residualProbe;
        public Vector3 residualPoint;
        public Vector3 midRightWrist, midLeftWrist;
        public float nominalDurationSeconds, actualDurationSeconds, angularSpeedLimit;
        public int stretchedSegments, playbackSampleCount;
        public float maxAuthoredAdjacentDegrees, maxAuthoredResidualMeters, max30FpsAdjacentDegrees, max30FpsResidualMeters, maxAngularSpeedDegreesPerSecond;
        public float maxHandResidualMeters;
        public float maxAuthoredHandResidualMeters, handResidualAtSeconds;
        public int refinementInsertedKeys;
        public string handResidualProbe;
        public string residualCategory;
    }

    private sealed class BodyFrontSurface
    {
        private const int Columns = 18, Rows = 20;
        public Transform anchor;
        public Matrix4x4 localToRest, restToLocal;
        public float minX, maxX, minY, maxY;
        private float minZ, maxZ;
        public SurfaceMeasurement Measurement { get { return new SurfaceMeasurement { anchor = anchor.name,
            min = new Vector3(minX, minY, minZ), max = new Vector3(maxX, maxY, maxZ) }; } }
        private readonly float[,] front = new float[Columns, Rows];
        private readonly float[,] back = new float[Columns, Rows];

        public BodyFrontSurface(Transform root, Transform bone, List<Vector3> points)
        {
            anchor = bone;
            localToRest = root.worldToLocalMatrix * bone.localToWorldMatrix;
            restToLocal = localToRest.inverse;
            minX = points.Min(p => p.x); maxX = points.Max(p => p.x);
            minY = points.Min(p => p.y); maxY = points.Max(p => p.y);
            minZ = points.Min(p => p.z); maxZ = points.Max(p => p.z);
            for (int x = 0; x < Columns; x++) for (int y = 0; y < Rows; y++)
            { front[x, y] = float.PositiveInfinity; back[x, y] = float.NegativeInfinity; }
            foreach (Vector3 point in points)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(minX, maxX, point.x) * (Columns - 1)), 0, Columns - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(minY, maxY, point.y) * (Rows - 1)), 0, Rows - 1);
                front[x, y] = Mathf.Min(front[x, y], point.z);
                back[x, y] = Mathf.Max(back[x, y], point.z);
            }
        }

        public Vector3 ProjectForward(Vector3 world, float radius, float gap, bool frontOnly = false)
        {
            Vector3 point = localToRest.MultiplyPoint3x4(anchor.InverseTransformPoint(world));
            float dx = Mathf.Max((maxX - minX) / (Columns - 1), 0.0001f);
            float dy = Mathf.Max((maxY - minY) / (Rows - 1), 0.0001f);
            if (point.x < minX - radius - gap || point.x > maxX + radius + gap || point.y < minY - radius - gap || point.y > maxY + radius + gap) return Vector3.zero;
            int centerX = Mathf.RoundToInt((point.x - minX) / dx), centerY = Mathf.RoundToInt((point.y - minY) / dy);
            int rangeX = Mathf.CeilToInt((radius + gap) / dx) + 1, rangeY = Mathf.CeilToInt((radius + gap) / dy) + 1;
            float nearestFront = float.PositiveInfinity, farthestBack = float.NegativeInfinity;
            for (int x = Mathf.Max(0, centerX - rangeX); x <= Mathf.Min(Columns - 1, centerX + rangeX); x++)
                for (int y = Mathf.Max(0, centerY - rangeY); y <= Mathf.Min(Rows - 1, centerY + rangeY); y++)
                {
                    if (Mathf.Abs(minX + x * dx - point.x) > radius + gap + dx || Mathf.Abs(minY + y * dy - point.y) > radius + gap + dy) continue;
                    nearestFront = Mathf.Min(nearestFront, front[x, y]);
                    farthestBack = Mathf.Max(farthestBack, back[x, y]);
                }
            if (float.IsInfinity(nearestFront) || point.z <= nearestFront - radius - gap
                || point.z >= farthestBack + radius + gap) return Vector3.zero;
            Vector3 safe = point;
            safe.z = nearestFront - radius - gap;
            if (!frontOnly)
            {
                // At a silhouette edge, a millimetre of overlap requires a
                // millimetre of lateral correction, not a sudden full chest-depth
                // push. Front-target construction deliberately bypasses this.
                float outsideX = point.x < (minX + maxX) * 0.5f ? minX - radius - gap : maxX + radius + gap;
                if (Mathf.Abs(outsideX - point.x) < Mathf.Abs(safe.z - point.z))
                    safe = new Vector3(outsideX, point.y, point.z);
                float outsideY = point.y < (minY + maxY) * 0.5f ? minY - radius - gap : maxY + radius + gap;
                if (Mathf.Abs(outsideY - point.y) < Vector3.Distance(safe, point))
                    safe = new Vector3(point.x, outsideY, point.z);
                if (Mathf.Abs(farthestBack + radius + gap - point.z) < Vector3.Distance(safe, point))
                    safe = new Vector3(point.x, point.y, farthestBack + radius + gap);
            }
            return anchor.TransformPoint(restToLocal.MultiplyPoint3x4(safe)) - world;
        }
    }

    private sealed class ArmClearance
    {
        public readonly List<BodyFrontSurface> surfaces = new List<BodyFrontSurface>();
        public readonly List<BakeMeasurement> bakes = new List<BakeMeasurement>();
        public int bodyVertexCount;
        public float gap;
        public bool adaptiveSegments;
        public string residualProbe;
        public Vector3 residualPoint;
        public Vector3 ChestFrontTarget(Transform root, Vector3 target, float radius, float side)
        {
            Vector3 local = root.InverseTransformPoint(target);
            float front = float.PositiveInfinity, halfWidth = 0;
            foreach (BodyFrontSurface surface in surfaces)
            {
                SurfaceMeasurement measurement = surface.Measurement;
                if (measurement.anchor == "頭") continue;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 rest = new Vector3((corner & 1) == 0 ? measurement.min.x : measurement.max.x,
                        (corner & 2) == 0 ? measurement.min.y : measurement.max.y,
                        (corner & 4) == 0 ? measurement.min.z : measurement.max.z);
                    Vector3 world = surface.anchor.TransformPoint(surface.restToLocal.MultiplyPoint3x4(rest));
                    Vector3 point = root.InverseTransformPoint(world);
                    front = Mathf.Min(front, point.z);
                    halfWidth = Mathf.Max(halfWidth, Mathf.Abs(point.x));
                }
            }
            if (!float.IsInfinity(front)) local.z = front - radius - gap;
            // Keep the authored chest-space lateral target. The widest skirt
            // slice must not move a chest-covering hand outside the torso.
            return root.TransformPoint(local);
        }
        public Vector3 Push(Vector3 point, float radius, bool frontOnly = false)
        {
            Vector3 result = Vector3.zero;
            foreach (BodyFrontSurface surface in surfaces)
            {
                Vector3 push = surface.ProjectForward(point, radius, gap, frontOnly);
                if (push.sqrMagnitude > result.sqrMagnitude) result = push;
            }
            return result;
        }
    }

    private static ArmClearance CreateArmClearance(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands, bool adaptiveSegments)
    {
        var result = new ArmClearance { adaptiveSegments = adaptiveSegments };
        Transform pelvis = bones.TryGetValue("下半身", out Transform lower) ? lower : bones["センター"];
        Transform chest = bones.TryGetValue("上半身2", out Transform upperChest) ? upperChest : pelvis;
        Transform head = bones["頭"];
        Vector3 hip = root.InverseTransformPoint(pelvis.position);
        Vector3 headPoint = root.InverseTransformPoint(head.position);
        Vector3 left = root.InverseTransformPoint(bones["腕.L"].position), right = root.InverseTransformPoint(bones["腕.R"].position);
        float shoulderY = (left.y + right.y) * 0.5f;
        float shoulderWidth = Vector3.Distance(left, right);
        float torsoHeight = Mathf.Max(0.12f, shoulderY - hip.y);
        float headBottom = bones.TryGetValue("首", out Transform neck) ? root.InverseTransformPoint(neck.position).y : shoulderY;
        float skeletonHeight = headPoint.y - root.InverseTransformPoint(bones["足首.R"].position).y;
        result.gap = Mathf.Clamp(skeletonHeight * 0.012f, 0.009f, 0.023f);
        var visiblePoints = new List<Vector3>();
        var torsoPoints = new List<Vector3>();
        var headPoints = new List<Vector3>();
        var sleeveSamples = new Dictionary<string, List<float>> { { "L", new List<float>() }, { "R", new List<float>() } };
        var upperSamples = new Dictionary<string, List<float>> { { "L", new List<float>() }, { "R", new List<float>() } };
        var palmSamples = new Dictionary<string, List<float>> { { "L", new List<float>() }, { "R", new List<float>() } };
        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!skin.enabled || skin.sharedMesh == null) continue;
            if (adaptiveSegments && (skin.name.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) >= 0 || skin.name.Contains("武器"))) continue;
            Mesh mesh = new Mesh();
            try
            {
                skin.BakeMesh(mesh);
                Matrix4x4 toWorld = PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh, out string coordinateMode, out Vector3 calibratedSize, out float fitError);
                result.bakes.Add(new BakeMeasurement { renderer = skin.name, coordinateMode = coordinateMode,
                    bakedSize = mesh.bounds.size, rendererWorldSize = skin.bounds.size, calibratedWorldSize = calibratedSize, logScaleError = fitError });
                Debug.Log($"PET_BAKE_CALIBRATION {root.name}/{skin.name}: {coordinateMode}; rawY={mesh.bounds.size.y:F4}, rendererY={skin.bounds.size.y:F4}, calibratedY={calibratedSize.y:F4}, logScaleError={fitError:F4}.");
                Vector3[] vertices = mesh.vertices;
                bool[] visible = new bool[vertices.Length];
                Material[] materials = skin.sharedMaterials;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = submesh < materials.Length ? materials[submesh] : null;
                    if (material != null && material.HasProperty("_BaseColor") && material.GetColor("_BaseColor").a <= 0.01f) continue;
                    foreach (int index in mesh.GetIndices(submesh)) if (index >= 0 && index < visible.Length) visible[index] = true;
                }
                BoneWeight[] weights = skin.sharedMesh.isReadable ? skin.sharedMesh.boneWeights : Array.Empty<BoneWeight>();
                Transform[] skinBones = skin.bones;
                int[] armSide = skinBones.Select(bone => bone == null ? 0 : bone == bones["腕.L"] || bone.IsChildOf(bones["腕.L"]) ? 1
                    : bone == bones["腕.R"] || bone.IsChildOf(bones["腕.R"]) ? 2 : 0).ToArray();
                for (int index = 0; index < vertices.Length; index++)
                {
                    if (!visible[index]) continue;
                    Vector3 world = toWorld.MultiplyPoint3x4(vertices[index]);
                    Vector3 point = root.InverseTransformPoint(world);
                    if (float.IsNaN(point.x + point.y + point.z) || float.IsInfinity(point.x + point.y + point.z)) continue;
                    int side = 0;
                    Transform weightedBone = null;
                    if (index < weights.Length)
                    {
                        BoneWeight w = weights[index];
                        float maxWeight = w.weight0; int boneIndex = w.boneIndex0;
                        if (w.weight1 > maxWeight) { maxWeight = w.weight1; boneIndex = w.boneIndex1; }
                        if (w.weight2 > maxWeight) { maxWeight = w.weight2; boneIndex = w.boneIndex2; }
                        if (w.weight3 > maxWeight) { maxWeight = w.weight3; boneIndex = w.boneIndex3; }
                        if (boneIndex >= 0 && boneIndex < armSide.Length) { side = armSide[boneIndex]; weightedBone = skinBones[boneIndex]; }
                    }
                    if (side > 0)
                    {
                        string key = side == 1 ? "L" : "R";
                        HandRig hand = hands[key];
                        if (adaptiveSegments)
                        {
                            bool atHand = weightedBone == hand.hand || weightedBone != null && weightedBone.IsChildOf(hand.hand);
                            bool atForearm = weightedBone == hand.elbow || weightedBone != null && weightedBone.IsChildOf(hand.elbow);
                            if (atHand)
                            {
                                Vector3 finger = hand.hand.TransformDirection(hand.fingerAxis);
                                float length = hand.joints.Length > 0 ? hand.joints.Max(joint => Vector3.Distance(hand.hand.position, joint.bone.position)) : 0.1f;
                                AddRadialSample(palmSamples[key], world, hand.hand.position, hand.hand.position + finger * length, 0.12f, 0.60f);
                            }
                            else if (atForearm) AddRadialSample(sleeveSamples[key], world, hand.elbow.position, hand.hand.position, 0.18f, 0.82f);
                            else AddRadialSample(upperSamples[key], world, bones["腕." + key].position, hand.elbow.position, 0.22f, 0.82f);
                            continue;
                        }
                        Vector3 segment = hand.hand.position - hand.elbow.position;
                        float along = Vector3.Dot(world - hand.elbow.position, segment) / Mathf.Max(segment.sqrMagnitude, 0.000001f);
                        if (along > 0.05f && along < 0.9f) sleeveSamples[key].Add(Vector3.Distance(world, hand.elbow.position + segment * along));
                        continue;
                    }
                    visiblePoints.Add(point);
                    if (adaptiveSegments)
                    {
                        bool headWeighted = weightedBone != null && (weightedBone == head || weightedBone.IsChildOf(head));
                        bool accessory = false;
                        for (Transform ancestor = weightedBone; ancestor != null && ancestor != root; ancestor = ancestor.parent)
                        {
                            string name = ancestor.name.ToLowerInvariant();
                            if (name.Contains("weapon") || name.Contains("武器") || name.Contains("weaponroot")) { accessory = true; break; }
                        }
                        if (!accessory && !headWeighted) torsoPoints.Add(point);
                        if (!accessory && (headWeighted || weightedBone == null)) headPoints.Add(point);
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        float bodyRadius = shoulderWidth * 0.57f;
        float depth = Mathf.Max(shoulderWidth * 0.75f, torsoHeight * 0.65f);
        float split = hip.y + torsoHeight * 0.5f;
        List<Vector3> bodyGeometry = adaptiveSegments && torsoPoints.Count > 8 ? torsoPoints : visiblePoints;
        List<Vector3> headGeometry = adaptiveSegments && headPoints.Count > 8 ? headPoints : visiblePoints;
        AddSurface(result, root, pelvis, bodyGeometry.Where(p => Mathf.Abs(p.x - hip.x) <= bodyRadius
            && p.y >= hip.y - torsoHeight * 0.18f && p.y <= split + torsoHeight * 0.08f && Mathf.Abs(p.z - hip.z) <= depth));
        AddSurface(result, root, chest, bodyGeometry.Where(p => Mathf.Abs(p.x - hip.x) <= bodyRadius
            && p.y >= split - torsoHeight * 0.08f && p.y <= headBottom && Mathf.Abs(p.z - hip.z) <= depth));
        float headRadius = Mathf.Max(shoulderWidth * 0.9f, skeletonHeight * 0.22f);
        AddSurface(result, root, head, headGeometry.Where(p => Mathf.Abs(p.x - headPoint.x) <= headRadius
            && p.y >= headBottom && p.y <= headPoint.y + skeletonHeight * 0.65f && Mathf.Abs(p.z - headPoint.z) <= headRadius));
        // Hanging palms are BELOW the former torso-only envelope. Measure the
        // visible hip/thigh/skirt at their own height, without shoulder-width
        // clipping or one wide skirt bound extending all the way to the chest.
        float hangingBottom = hands.Min(pair => root.InverseTransformPoint(bones["腕." + pair.Key].position).y
            - Vector3.Distance(bones["腕." + pair.Key].position, pair.Value.elbow.position)
            - Vector3.Distance(pair.Value.elbow.position, pair.Value.hand.position)
            - (pair.Value.joints.Length > 0 ? pair.Value.joints.Max(j => Vector3.Distance(pair.Value.hand.position, j.bone.position)) : 0.12f));
        float sliceHeight = Mathf.Max(0.025f, skeletonHeight * 0.028f);
        for (float bottom = hangingBottom - result.gap; bottom < hip.y + torsoHeight * 0.12f; bottom += sliceHeight)
        {
            float from = bottom, to = bottom + sliceHeight;
            AddSurface(result, root, pelvis, bodyGeometry.Where(p => p.y >= from && p.y <= to
                && Mathf.Abs(p.z - hip.z) <= depth));
        }
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side];
            float armLength = Vector3.Distance(bones["腕." + side].position, hand.elbow.position) + Vector3.Distance(hand.elbow.position, hand.hand.position);
            float upperLength = Vector3.Distance(bones["腕." + side].position, hand.elbow.position);
            float lowerLength = Vector3.Distance(hand.elbow.position, hand.hand.position);
            float span = bones.TryGetValue("人指１." + side, out Transform index) && bones.TryGetValue("小指１." + side, out Transform pinky)
                ? Vector3.Distance(index.position, pinky.position) : armLength * 0.1f;
            hand.palmRadius = Mathf.Clamp(span * 0.52f, armLength * 0.045f, armLength * 0.13f);
            hand.fingerLength = hand.joints.Length > 0 ? hand.joints.Max(joint => Vector3.Distance(hand.hand.position, joint.bone.position)) : armLength * 0.22f;
            List<float> samples = sleeveSamples[side];
            samples.Sort();
            hand.sleeveRadius = samples.Count > 8 ? samples[Mathf.RoundToInt((samples.Count - 1) * 0.88f)] : armLength * 0.075f;
            hand.sleeveRadius = Mathf.Clamp(hand.sleeveRadius, armLength * 0.045f, armLength * 0.19f);
            hand.upperRadius = hand.sleeveRadius * 0.8f;
            float sign = Mathf.Sign(root.InverseTransformPoint(bones["腕." + side].position).x);
            hand.neutralUpperDirection = new Vector3(sign * 0.14f, -1, 0.015f);
            hand.neutralForearmDirection = RelaxedForearmDirection(sign);
            if (adaptiveSegments)
            {
                hand.upperRadius = RadiusPercentile(upperSamples[side], upperLength * 0.12f);
                hand.sleeveRadius = RadiusPercentile(samples, lowerLength * 0.13f);
                if (palmSamples[side].Count > 8) hand.palmRadius = Mathf.Max(span * 0.42f, RadiusPercentile(palmSamples[side], hand.palmRadius));
                hand.upperSampleCount = upperSamples[side].Count; hand.forearmSampleCount = samples.Count; hand.palmSampleCount = palmSamples[side].Count;
                // Fit only inside a relaxed 6–12 degree cone. The old 0.65
                // outward component permitted a 40 degree A-pose, especially
                // when a wide skirt/cuff inflated the conservative body shell.
                float shoulderX = Mathf.Abs(root.InverseTransformPoint(bones["腕." + side].position).x);
                float outward = Mathf.Clamp((bodyRadius + hand.upperRadius + result.gap - shoulderX) / Mathf.Max(upperLength * 0.72f, 0.0001f), Mathf.Sin(6 * Mathf.Deg2Rad), Mathf.Sin(12 * Mathf.Deg2Rad));
                hand.neutralUpperDirection = new Vector3(sign * outward, -Mathf.Sqrt(1 - outward * outward), 0.015f);
                var rejected = new List<string>();
                // Imported cuffs/ribbons can share wrist skin weights. Their
                // radial envelope must not become a body-sized palm sphere;
                // knuckle span bounds the palm, while each finger remains probed.
                float palmLimit = Mathf.Max(span * 0.65f, armLength * 0.055f);
                if (hand.palmRadius > palmLimit)
                {
                    rejected.Add($"palm radius {hand.palmRadius:F4}m exceeds knuckle-scaled {palmLimit:F4}m; wrist-weighted garment excluded, finger probes retained");
                    hand.palmRadius = palmLimit;
                }
                if (hand.upperRadius > upperLength * 0.60f)
                {
                    hand.upperProbeEnabled = false;
                    hand.neutralUpperDirection = new Vector3(sign * 0.14f, -1, 0.015f);
                    rejected.Add($"upper-arm radius {hand.upperRadius:F4}m exceeds anatomical segment scale {upperLength:F4}m; treated as attached garment");
                }
                Vector3 neutralElbow = bones["腕." + side].position + root.TransformDirection(hand.neutralUpperDirection.normalized) * upperLength;
                Vector3 neutralWrist = neutralElbow + root.TransformDirection(RelaxedForearmDirection(sign).normalized) * lowerLength;
                hand.neutralForearmCorrection = Mathf.Max(result.Push(Vector3.Lerp(neutralElbow, neutralWrist, 0.35f), hand.sleeveRadius).magnitude,
                    result.Push(Vector3.Lerp(neutralElbow, neutralWrist, 0.7f), hand.sleeveRadius).magnitude);
                // A long ribbon/cape can be weighted to an elbow and project into
                // this axial range. It is not a cylinder with a body-sized radius.
                // Reject that uncertain capsule instead of distorting an otherwise
                // neutral arm; retain the independently measured palm and fingers.
                if (hand.sleeveRadius > lowerLength * 0.60f || hand.neutralForearmCorrection > lowerLength * 0.30f)
                {
                    hand.forearmProbeEnabled = false;
                    rejected.Add($"forearm radius {hand.sleeveRadius:F4}m / segment {lowerLength:F4}m demands {hand.neutralForearmCorrection:F4}m neutral displacement; garment capsule rejected, hand/finger probes retained");
                }
                if (rejected.Count > 0)
                {
                    hand.measurementStatus = string.Join("; ", rejected);
                    Debug.LogWarning("PET_ANATOMICAL_MEASUREMENT_REJECTED " + root.name + "/" + side + ": " + hand.measurementStatus);
                }
            }
        }
        foreach (string side in new[] { "R", "L" }) FitRelaxedHandClearance(root, bones["腕." + side], hands[side], result);
        result.bodyVertexCount = visiblePoints.Count;
        Debug.Log($"PET_ARM_SURFACES {root.name}: {visiblePoints.Count} visible body vertices, {result.surfaces.Count} chest/head surfaces, gap {result.gap:F3}m.");
        return result;
    }

    private static void FitRelaxedHandClearance(Transform root, Transform upper, HandRig hand, ArmClearance profile)
    {
        float sign = Mathf.Sign(root.InverseTransformPoint(upper.position).x);
        var saved = upper.GetComponentsInChildren<Transform>(true).ToDictionary(bone => bone, bone => bone.localRotation);
        Vector3 bestUpper = hand.neutralUpperDirection, bestLower = hand.neutralForearmDirection;
        float bestScore = float.PositiveInfinity, bestResidual = float.PositiveInfinity;
        try
        {
            // Search the smallest relaxed adjustment. Shoulders stay down;
            // a modest lower-arm angle lets palms clear the hips and clothing.
            // Imported characters use the user's in-game reference: a relaxed
            // open stance with a nearly continuous shoulder/elbow/wrist line.
            // Do not pin the upper arm to the ribs and splay only the forearm.
            int minimumUpper = profile.adaptiveSegments ? 16 : 8;
            int maximumUpper = profile.adaptiveSegments ? 28 : 12;
            for (int upperDegrees = minimumUpper; upperDegrees <= maximumUpper; upperDegrees += 2)
                for (int outward = profile.adaptiveSegments ? 12 : 2; outward <= 28; outward += 2)
                    for (int forward = 7; forward <= 27; forward += 5)
                    {
                        Vector3 upperDirection = new Vector3(sign * Mathf.Tan(upperDegrees * Mathf.Deg2Rad), -1, 0.015f);
                        Vector3 lowerDirection = new Vector3(sign * Mathf.Tan(outward * Mathf.Deg2Rad), -1, -Mathf.Tan(forward * Mathf.Deg2Rad));
                        if (Vector3.Angle(Vector3.down, lowerDirection) > 32
                            || Vector3.Angle(upperDirection, lowerDirection) > (profile.adaptiveSegments ? 16 : 28)) continue;
                        foreach (var pair in saved) pair.Key.localRotation = pair.Value;
                        Aim(upper, hand.elbow, root.TransformDirection(upperDirection));
                        Aim(hand.elbow, hand.hand, root.TransformDirection(lowerDirection));
                        PoseHand(root, hand, "Idle", 0, 0, sign, hand.hand.position - hand.elbow.position);
                        float residual = ArmPenetration(upper, hand, profile, false, true, true).magnitude;
                        float postureCost = profile.adaptiveSegments
                            ? (upperDegrees - 22) * (upperDegrees - 22) * 3f + (outward - 20) * (outward - 20)
                                + (forward - 7) * (forward - 7) * 2f
                            : upperDegrees * upperDegrees * 2f + outward * outward + (forward - 7) * (forward - 7) * 2f;
                        float score = residual > 0.0005f ? 100000 + residual * 1000000 + postureCost : postureCost;
                        if (score >= bestScore) continue;
                        bestScore = score; bestResidual = residual; bestUpper = upperDirection; bestLower = lowerDirection;
                    }
        }
        finally { foreach (var pair in saved) pair.Key.localRotation = pair.Value; }
        hand.neutralUpperDirection = bestUpper;
        hand.neutralForearmDirection = bestLower;
        hand.neutralHandResidual = bestResidual;
        Debug.Log($"PET_RELAXED_HAND_FIT {root.name}/{sign}: upper={Vector3.Angle(Vector3.down, bestUpper):F2}, forearm={Vector3.Angle(Vector3.down, bestLower):F2}, handResidual={bestResidual:F5}m.");
    }

    private static void AddRadialSample(List<float> samples, Vector3 point, Vector3 start, Vector3 end, float from, float to)
    {
        Vector3 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared < 0.000001f) return;
        float along = Vector3.Dot(point - start, segment) / lengthSquared;
        if (along < from || along > to) return;
        samples.Add(Vector3.Distance(point, start + segment * along));
    }

    private static float RadiusPercentile(List<float> samples, float fallback)
    {
        if (samples.Count < 9) return fallback;
        samples.Sort();
        return Mathf.Max(0.003f, samples[Mathf.RoundToInt((samples.Count - 1) * 0.90f)]);
    }

    private static void AddSurface(ArmClearance profile, Transform root, Transform anchor, IEnumerable<Vector3> points)
    {
        List<Vector3> sample = points.ToList();
        if (sample.Count > 8) profile.surfaces.Add(new BodyFrontSurface(root, anchor, sample));
    }

    private static float ApplyArmClearance(Transform root, Dictionary<string, Transform> bones, Dictionary<string, HandRig> hands,
        ArmClearance profile, string action, float t)
    {
        if (IsContinuousAction(action))
        {
            float measured = 0;
            foreach (string side in new[] { "R", "L" })
            {
                HandRig hand = hands[side];
                if (IsLegAction(action))
                {
                    Vector3 fingers = hand.hand.TransformDirection(hand.fingerAxis), palm = hand.hand.TransformDirection(hand.palmAxis);
                    hand.hand.localRotation = hand.restLocalRotation; OrientHand(hand, fingers, palm);
                }
                float penetration = ArmPenetration(bones["腕." + side], hand, profile).magnitude;
                if (penetration > measured) { measured = penetration; profile.residualProbe = side + ":" + hand.penetrationProbe; profile.residualPoint = hand.penetrationPoint; }
            }
            return measured;
        }
        float residual = 0;
        foreach (string side in new[] { "R", "L" })
        {
            HandRig hand = hands[side];
            Transform upper = bones["腕." + side];
            Vector3 shoulder = upper.position;
            float sign = Mathf.Sign(root.InverseTransformPoint(shoulder).x);
            float armLength = Vector3.Distance(shoulder, hand.elbow.position) + Vector3.Distance(hand.elbow.position, hand.hand.position);
            float gestureWeight = ArmGestureWeight(action, side, t);
            Quaternion authoredUpper = upper.localRotation, authoredElbow = hand.elbow.localRotation;
            if (gestureWeight > 0.0001f)
            {
                bool lowerGuard = action == "GuardLower";
                bool chestGesture = action == "Guard" || lowerGuard || action == "Shy" || action == "Cheer" || action == "TiltHead" && side == "R";
                // Neutral arms never enter IK. A folded chest gesture needs a
                // stable anatomical elbow plane: deriving it from an almost
                // straight authored arm can flip branches while the wrist rises.
                Vector3 initialPole = hand.elbow.position - shoulder;
                if (chestGesture || action == "Think" && side == "R" || action == "Walk" || action == "Run" || action == "Jump" || action == "HoldSword" || action == "DrawSlash")
                    initialPole = root.TransformDirection(new Vector3(sign, -0.35f, -0.15f));
                if (profile.adaptiveSegments && action == "Stretch")
                {
                    PrepareWideHeadStretch(root, upper, hand, profile, sign);
                    initialPole = hand.elbow.position - shoulder;
                }
                Vector3 target = hand.hand.position;
                // Evaluate the same anatomical elbow plane on every frame, including
                // frames without contact, so first contact cannot switch IK branches.
                SolveArm(upper, hand, target, initialPole, profile);
                PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position);
                float weight = action == "Guard" || lowerGuard ? GestureWindow(t, side == "R" ? 0.17f : 0.21f, 0.73f)
                    : action == "Shy" ? GestureWindow(t, side == "R" ? 0.28f : 0.33f, 0.7f)
                    : action == "Cheer" ? GestureWindow(t, 0.34f, 0.66f)
                    : action == "TiltHead" && side == "R" ? GestureWindow(t, 0.32f, 0.68f)
                    : action == "Think" && side == "R" ? gestureWeight : 0;
                if (weight > 0)
                {
                    Transform pelvis = bones.TryGetValue("下半身", out Transform lower) ? lower : bones["センター"];
                    Vector3 hip = root.InverseTransformPoint(pelvis.position);
                    Vector3 upperPoint = root.InverseTransformPoint(shoulder);
                    float torsoHeight = Mathf.Max(0.12f, upperPoint.y - hip.y);
                    float y = action == "Guard" ? hip.y + torsoHeight * (side == "R" ? 0.76f : 0.36f) : hip.y + torsoHeight * 0.8f;
                    if (lowerGuard) y = hip.y - torsoHeight * (side == "R" ? 0.04f : 0.12f);
                    if (action == "Guard" && side == "R") y += torsoHeight * 0.05f * weight * weight;
                    if (action == "Cheer") y += torsoHeight * 0.1f * (GesturePulse(t, 0.22f, 0.32f, 0.44f) + GesturePulse(t, 0.47f, 0.58f, 0.71f));
                    if (action == "Think") y = bones.TryGetValue("首", out Transform neck) ? root.InverseTransformPoint(neck.position).y - hand.fingerLength * 0.48f : upperPoint.y;
                    Vector3 desired = root.TransformPoint(new Vector3(sign * Mathf.Abs(upperPoint.x) * 0.62f, y, hip.z));
                    if (lowerGuard) desired = root.TransformPoint(new Vector3(sign * hand.palmRadius * 1.25f, y, hip.z));
                    // Query the actual painted surface at the target, including its
                    // deformation from the current torso/head rotation.
                    desired += profile.Push(desired, hand.palmRadius + hand.fingerLength * 0.18f, true);
                    if (chestGesture && !lowerGuard) desired = profile.ChestFrontTarget(root, desired, hand.palmRadius + hand.fingerLength * 0.10f, sign);
                    float upperLength = Vector3.Distance(shoulder, hand.elbow.position);
                    float lowerLength = Vector3.Distance(hand.elbow.position, hand.hand.position);
                    Vector3 neutral = shoulder + root.TransformDirection(hand.neutralUpperDirection.normalized) * upperLength
                        + root.TransformDirection(hand.neutralForearmDirection.normalized) * lowerLength;
                    // The hand first travels around the outer/front silhouette, then
                    // folds inward. The same smooth arc is followed on release.
                    float arc = Mathf.Sin(weight * Mathf.PI);
                    target = Vector3.Lerp(neutral, desired, weight)
                        + root.TransformDirection(new Vector3(sign * armLength * 0.16f, 0, -armLength * 0.22f)) * arc;
                    SolveArm(upper, hand, target, initialPole, profile);
                    PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position);
                }
                // The lower pose deliberately brings forearms near the abdomen.
                // Sleeve capsules overestimate that contact and push both hands
                // back out to the hips. Its measured palm/finger surface checks
                // below remain active throughout the entire trajectory.
                // Locomotion uses an authored elbow corridor. Oversized sleeve
                // capsules used to flip its almost straight elbow and stretch a
                // one-second walk into twenty seconds. Actual hand probes below
                // remain active; clothing estimates do not steer these arms.
                bool locomotion = action == "Walk" || action == "Run" || action == "Jump" || action == "HoldSword" || action == "DrawSlash";
                for (int iteration = 0; iteration < (lowerGuard || locomotion ? 0 : 10); iteration++)
                {
                    bool shoulderMoved = false;
                    if (profile.adaptiveSegments)
                    {
                        Vector3 upperCorrection = ArmPenetration(upper, hand, profile, true, false);
                        if (upperCorrection.sqrMagnitude > 0.00000025f)
                        {
                            Vector3 current = hand.elbow.position - shoulder;
                            Vector3 desired = current + upperCorrection / 0.72f;
                            if (desired.sqrMagnitude > 0.000001f)
                            {
                                // Rotate the shoulder towards a clear elbow corridor.
                                // Preserve both segment lengths and carry the lower arm
                                // along; never try to fix a biceps by shoving its wrist.
                                Quaternion correctionRotation = Quaternion.FromToRotation(current, desired);
                                upper.rotation = Quaternion.Slerp(Quaternion.identity, correctionRotation, 0.85f) * upper.rotation;
                                initialPole = hand.elbow.position - shoulder;
                                target = hand.hand.position;
                                shoulderMoved = true;
                                PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position);
                            }
                        }
                    }
                    Vector3 correction = ArmPenetration(upper, hand, profile, !profile.adaptiveSegments, true);
                    if (profile.adaptiveSegments && correction.sqrMagnitude < 0.00000025f && shoulderMoved) continue;
                    if (correction.sqrMagnitude < 0.00000025f) break;
                    target = hand.hand.position + Vector3.ClampMagnitude(correction, armLength * 0.22f);
                    if (Vector3.Distance(shoulder, target) > armLength - 0.0001f)
                        target += root.TransformDirection(new Vector3(sign * armLength * 0.035f, armLength * 0.04f, 0));
                    SolveArm(upper, hand, target, initialPole, profile);
                    PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position);
                }
                // Clothing capsules are only conservative estimates. Fade their
                // corrections with the gesture, so they cannot override the user's
                // natural resting stance or pop the elbows out at first contact.
                upper.localRotation = Quaternion.Slerp(authoredUpper, upper.localRotation, gestureWeight);
                hand.elbow.localRotation = Quaternion.Slerp(authoredElbow, hand.elbow.localRotation, gestureWeight);
            }
            // Palm/finger safety is not a sleeve estimate and must remain active
            // during idle and gesture transitions, after the garment blend.
            PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position);
            ApplyHandClearance(root, upper, hand, profile, action, t, gestureWeight, sign, armLength);
            PoseHand(root, hand, action, t, hand.poseWeight, sign, hand.hand.position - hand.elbow.position, true);
            Vector3 fingers = hand.hand.TransformDirection(hand.fingerAxis);
            Vector3 palm = hand.hand.TransformDirection(hand.palmAxis);
            // The preview used no twist. Restore the wrist basis, then distribute
            // the final pronation once so no correction can double-apply a roll.
            hand.hand.localRotation = hand.restLocalRotation;
            OrientHand(hand, fingers, palm);
            float armResidual = ArmPenetration(upper, hand, profile).magnitude;
            if (armResidual > residual) { residual = armResidual; profile.residualProbe = side + ":" + hand.penetrationProbe; profile.residualPoint = hand.penetrationPoint; }
        }
        return residual;
    }

    private static void ApplyHandClearance(Transform root, Transform upper, HandRig hand,
        ArmClearance profile, string action, float t, float gestureWeight, float sign, float armLength)
    {
        // Authoring and interpolation refinement must obey the same route and
        // resting-pose constraint. A front-only refinement used to lift and
        // cross the arms while a lower guard was returning to its open stance.
        Quaternion fittedUpper = upper.localRotation, fittedElbow = hand.elbow.localRotation;
        if (!profile.adaptiveSegments || gestureWeight > 0.0001f)
            for (int iteration = 0; iteration < 24; iteration++)
            {
                Vector3 nearest = ArmPenetration(upper, hand, profile, false, true, true);
                Vector3 forward = ArmPenetration(upper, hand, profile, false, true, true, true);
                Vector3 correction = Vector3.Lerp(nearest, forward, gestureWeight * gestureWeight);
                if (correction.sqrMagnitude < 0.00000025f) break;
                Vector3 pole = action == "Walk" || action == "Run" || action == "Jump" || action == "HoldSword" || action == "DrawSlash"
                    ? root.TransformDirection(new Vector3(sign, -0.35f, -0.15f)) : hand.elbow.position - upper.position;
                Vector3 target = hand.hand.position + Vector3.ClampMagnitude(correction + correction.normalized * 0.003f, armLength * 0.12f);
                SolveArm(upper, hand, target, pole, profile);
                PoseHand(root, hand, action, t, gestureWeight, sign, hand.hand.position - hand.elbow.position);
            }
        if (profile.adaptiveSegments)
        {
            upper.localRotation = Quaternion.Slerp(fittedUpper, upper.localRotation, gestureWeight);
            hand.elbow.localRotation = Quaternion.Slerp(fittedElbow, hand.elbow.localRotation, gestureWeight);
        }
        PoseHand(root, hand, action, t, gestureWeight, sign, hand.hand.position - hand.elbow.position);
    }

    private static Vector3 ArmPenetration(Transform upper, HandRig hand, ArmClearance profile, bool includeUpper = true, bool includeDistal = true, bool handsOnly = false, bool frontOnly = false)
    {
        Vector3 worst = Vector3.zero;
        void Probe(Vector3 point, float radius, string name)
        {
            // Use a consistent forward route for hands. Nearest-side switching
            // between adjacent fingers can otherwise oscillate at a hip corner.
            Vector3 push = profile.Push(point, radius, frontOnly);
            if (push.sqrMagnitude > worst.sqrMagnitude) { worst = push; hand.penetrationProbe = name; hand.penetrationPoint = point; }
        }
        if (includeUpper && !handsOnly)
        {
            // The shoulder/body attachment is continuous skin, not an intruding
            // object. Probe the distal biceps and elbow beyond that joint region.
            if (!profile.adaptiveSegments || hand.upperProbeEnabled)
                Probe(Vector3.Lerp(upper.position, hand.elbow.position, profile.adaptiveSegments ? 0.72f : 0.65f), hand.upperRadius, "upper-arm");
            if (!profile.adaptiveSegments || hand.upperProbeEnabled || hand.forearmProbeEnabled)
                Probe(hand.elbow.position, profile.adaptiveSegments ? hand.upperProbeEnabled ? hand.forearmProbeEnabled ? Mathf.Min(hand.upperRadius, hand.sleeveRadius) : hand.upperRadius : hand.sleeveRadius : hand.sleeveRadius, "elbow");
        }
        if (!includeDistal) return worst;
        if (!handsOnly && (!profile.adaptiveSegments || hand.forearmProbeEnabled))
        {
            Probe(Vector3.Lerp(hand.elbow.position, hand.hand.position, 0.35f), hand.sleeveRadius, "forearm-35");
            Probe(Vector3.Lerp(hand.elbow.position, hand.hand.position, 0.7f), hand.sleeveRadius * 0.85f, "forearm-70");
        }
        Probe(hand.hand.position, hand.palmRadius, "wrist");
        Probe(hand.hand.position + hand.hand.TransformDirection(hand.fingerAxis) * hand.fingerLength * 0.4f, hand.palmRadius, "palm");
        foreach (FingerJoint joint in hand.joints)
        {
            Probe(joint.bone.position, hand.palmRadius * 0.32f, joint.bone.name);
            if (joint.joint == 2)
            {
                Vector3 tip = joint.bone.childCount > 0 ? joint.bone.GetChild(0).position
                    : joint.bone.position + (joint.bone.position - joint.bone.parent.position) * 0.65f;
                Probe(tip, hand.palmRadius * 0.28f, joint.bone.name + "_tip");
            }
        }
        return worst;
    }

    private static void PrepareWideHeadStretch(Transform root, Transform upper, HandRig hand, ArmClearance profile, float sign)
    {
        if (!hand.upperProbeEnabled) return;
        BodyFrontSurface head = profile.surfaces.FirstOrDefault(surface => surface.anchor.name == "頭");
        if (head == null) return;
        SurfaceMeasurement measurement = head.Measurement;
        float headBottom = float.PositiveInfinity, halfWidth = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 rest = new Vector3((corner & 1) == 0 ? measurement.min.x : measurement.max.x,
                (corner & 2) == 0 ? measurement.min.y : measurement.max.y, (corner & 4) == 0 ? measurement.min.z : measurement.max.z);
            Vector3 point = root.InverseTransformPoint(head.anchor.TransformPoint(head.restToLocal.MultiplyPoint3x4(rest)));
            headBottom = Mathf.Min(headBottom, point.y); halfWidth = Mathf.Max(halfWidth, Mathf.Abs(point.x));
        }
        Vector3 shoulder = root.InverseTransformPoint(upper.position), elbow = root.InverseTransformPoint(hand.elbow.position);
        float length = Vector3.Distance(shoulder, elbow);
        if (length < 0.0001f) return;
        float probeHeight = Mathf.Lerp(shoulder.y, elbow.y, 0.72f);
        float blend = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(headBottom - hand.upperRadius * 2, headBottom + hand.upperRadius, probeHeight));
        Vector3 direction = (elbow - shoulder).normalized;
        float required = Mathf.Clamp((halfWidth + hand.upperRadius + profile.gap - Mathf.Abs(shoulder.x)) / (length * 0.72f), 0, 0.97f);
        float outward = Mathf.Lerp(Mathf.Abs(direction.x), Mathf.Max(Mathf.Abs(direction.x), required), blend);
        Vector2 yz = new Vector2(direction.y, direction.z).normalized * Mathf.Sqrt(Mathf.Max(0, 1 - outward * outward));
        Aim(upper, hand.elbow, root.TransformDirection(new Vector3(sign * outward, yz.x, yz.y)));
    }

    private static void SolveArm(Transform upper, HandRig hand, Vector3 target, Vector3 originalPole, ArmClearance profile)
    {
        Vector3 shoulder = upper.position;
        float upperLength = Vector3.Distance(shoulder, hand.elbow.position), lowerLength = Vector3.Distance(hand.elbow.position, hand.hand.position);
        Vector3 reach = target - shoulder;
        if (upperLength < 0.00001f || lowerLength < 0.00001f || reach.sqrMagnitude < 0.000001f) return;
        // Do not force a percentage bend when an almost straight arm first
        // touches the clearance shell: that creates a discontinuous elbow snap.
        float distance = Mathf.Clamp(reach.magnitude, Mathf.Abs(upperLength - lowerLength) + 0.00001f, upperLength + lowerLength - 0.00001f);
        Vector3 direction = reach.normalized;
        Vector3 pole = Vector3.ProjectOnPlane(originalPole, direction).normalized;
        if (pole.sqrMagnitude < 0.5f) pole = Vector3.ProjectOnPlane(hand.elbow.position - shoulder, direction).normalized;
        if (pole.sqrMagnitude < 0.5f) return;
        float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2 * distance);
        float height = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - along * along));
        Vector3 wristTarget = shoulder + direction * distance;
        Vector3 bestElbow = shoulder + direction * along + pole * height;
        Aim(upper, hand.elbow, bestElbow - shoulder);
        Aim(hand.elbow, hand.hand, wristTarget - hand.elbow.position);
    }

    private static void PreviewHand(HandRig hand, Vector3 fingers, Vector3 palm)
    {
        Vector3 forearm = (hand.hand.position - hand.elbow.position).normalized;
        fingers = Vector3.RotateTowards(forearm, fingers.normalized, 42 * Mathf.Deg2Rad, 0);
        palm = Vector3.ProjectOnPlane(palm, fingers).normalized;
        if (palm.sqrMagnitude < 0.5f) palm = Vector3.ProjectOnPlane(hand.hand.TransformDirection(hand.palmAxis), fingers).normalized;
        hand.hand.rotation = Quaternion.LookRotation(fingers, palm)
            * Quaternion.Inverse(Quaternion.LookRotation(hand.fingerAxis, hand.palmAxis));
    }

    private static void OrientHand(HandRig hand, Vector3 fingers, Vector3 palm)
    {
        fingers.Normalize();
        palm = Vector3.ProjectOnPlane(palm, fingers).normalized;
        // Spread pronation/supination along the exported forearm twist chain,
        // rather than putting the entire roll into a visibly broken wrist.
        Vector3 forearm = (hand.hand.position - hand.elbow.position).normalized;
        Vector3 currentPalm = Vector3.ProjectOnPlane(hand.hand.TransformDirection(hand.palmAxis), forearm);
        Vector3 targetPalm = Vector3.ProjectOnPlane(palm, forearm);
        float twist = Vector3.SignedAngle(currentPalm, targetPalm, forearm);
        // SignedAngle wraps at +/-180. The end orientation is equivalent after a
        // full turn, but its fractional twist-bone rotations are not. Unwrap each
        // sampled trajectory before distributing it so raised arms cannot snap.
        if (hand.hasPreviousTwist) twist = hand.previousTwist + Mathf.DeltaAngle(hand.previousTwist, twist);
        hand.previousTwist = twist;
        hand.hasPreviousTwist = true;
        for (int index = 0; index < hand.twistSegments.Length; index++)
        {
            Transform segment = hand.twistSegments[index];
            segment.rotation = Quaternion.AngleAxis(twist * (index + 1) / 4f, forearm) * segment.rotation;
        }
        if (hand.twist != null) hand.twist.rotation = Quaternion.AngleAxis(twist, forearm) * hand.twist.rotation;
        hand.hand.rotation = Quaternion.LookRotation(fingers, palm)
            * Quaternion.Inverse(Quaternion.LookRotation(hand.fingerAxis, hand.palmAxis));
    }

    private static float GestureWindow(float t, float enterEnd, float exitStart)
    {
        return Mathf.SmoothStep(0, 1, t / enterEnd) * (1 - Mathf.SmoothStep(0, 1, (t - exitStart) / (1 - exitStart)));
    }

    private static float GesturePulse(float t, float start, float peak, float end)
    {
        return t <= peak ? Mathf.SmoothStep(0, 1, (t - start) / (peak - start))
            : 1 - Mathf.SmoothStep(0, 1, (t - peak) / (end - peak));
    }

    private static void SolveLeg(Transform upper, Transform knee, Transform ankle, Vector3 target, Vector3 bendDirection)
    {
        float upperLength = Vector3.Distance(upper.position, knee.position);
        float lowerLength = Vector3.Distance(knee.position, ankle.position);
        Vector3 reach = target - upper.position;
        if (upperLength < 0.00001f || lowerLength < 0.00001f || reach.sqrMagnitude < 0.000001f) return;
        float distance = Mathf.Clamp(reach.magnitude, Mathf.Abs(upperLength - lowerLength) + 0.0001f, upperLength + lowerLength - 0.0001f);
        Vector3 direction = reach.normalized;
        Vector3 bend = Vector3.ProjectOnPlane(bendDirection, direction).normalized;
        if (bend.sqrMagnitude < 0.001f) return;
        float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2 * distance);
        float outward = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - along * along));
        Vector3 kneeTarget = upper.position + direction * along + bend * outward;
        Aim(upper, knee, kneeTarget - upper.position);
        Aim(knee, ankle, target - knee.position);
    }

    private static void Aim(Transform bone, Transform child, Vector3 direction)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude > 0.000001f) bone.rotation = Quaternion.FromToRotation(current, direction) * bone.rotation;
    }

    private static void RotateWorld(Dictionary<string, Transform> bones, string name, Vector3 axis, float degrees)
    {
        if (bones.TryGetValue(name, out Transform bone)) bone.Rotate(axis, degrees, Space.World);
    }
}
