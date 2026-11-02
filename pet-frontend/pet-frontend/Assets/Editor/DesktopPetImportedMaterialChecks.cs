using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class DesktopPetImportedMaterialChecks
{
    [Serializable] private sealed class Map { public string source; }
    [Serializable] private sealed class MaterialResult {
        public string path, category, texture;
        public Color beforeShade, afterShade;
        public float beforeBrightness, afterBrightness, saturation, midtoneContrast;
    }
    [Serializable] private sealed class ImageMetrics { public float luminance, saturation, clippedFraction; }
    [Serializable] private sealed class ModelResult {
        public string model, source;
        public bool corrected;
        public MaterialResult[] materials;
        public ImageMetrics before, after;
        public string[] failures;
    }
    [Serializable] private sealed class Report { public bool passed; public string comparisonOrder; public ModelResult[] models; }
    private static readonly Color Background = new Color32(30, 38, 51, 255);

    [MenuItem("Desktop Pet/Apply Imported Color Correction With Comparison")]
    public static void ApplyAndCompare()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before updating imported materials.");
        Shader shader = Shader.Find("Desktop Pet/Soft Anime");
        if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Soft Anime shader is missing or has a compile error.");
        var check = new Material(shader);
        bool supportsCorrection = check.HasProperty("_Saturation") && check.HasProperty("_MidtoneContrast");
        UnityEngine.Object.DestroyImmediate(check);
        if (!supportsCorrection) throw new InvalidOperationException("Refresh the updated Soft Anime shader first.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/materials"));
        Directory.CreateDirectory(folder);
        var paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" }).Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)).OrderBy(path => path).ToArray();
        var models = paths.Select(path => ApplyModel(path, folder)).ToArray();
        AssetDatabase.SaveAssets();
        bool passed = models.All(model => model.failures.Length == 0);
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonUtility.ToJson(new Report { passed = passed,
            comparisonOrder = "Left: previous material. Right: corrected material. Identical camera, lights, pose and original texture files.", models = models }, true));
        if (!passed) throw new InvalidOperationException("IMPORTED_MATERIAL_CHECKS_FAILED: " + string.Join("; ", models.SelectMany(model => model.failures.Select(failure => model.model + ": " + failure))));
        Debug.Log("IMPORTED_MATERIAL_CHECKS_PASSED: " + models.Count(model => model.corrected) + " Evanescia/昔涟 models corrected; " +
            models.Count(model => !model.corrected) + " other models unchanged; source textures and alpha/depth/cull settings preserved; same-light comparisons saved.");
    }

    private static ModelResult ApplyModel(string path, string output)
    {
        string assetFolder = Path.GetDirectoryName(path).Replace('\\', '/');
        var result = new ModelResult { model = path };
        var failures = new List<string>(); var materialResults = new List<MaterialResult>();
        var preview = new PreviewRenderUtility();
        Texture2D before = null, after = null, comparison = null;
        try {
            TextAsset mapAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetFolder + "/PetMaterialMap.json");
            result.source = mapAsset != null ? JsonUtility.FromJson<Map>(mapAsset.text)?.source : "";
            result.corrected = PetImportedMaterialAppearance.IsEvanescia(result.source);
            var model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            preview.AddSingleGO(model); model.transform.position = Vector3.zero;
            var animator = model.GetComponent<Animator>();
            if (animator != null) { animator.enabled = false; animator.runtimeAnimatorController?.animationClips.FirstOrDefault(clip => clip.name == "Idle")?.SampleAnimation(model, 0); }
            before = Render(preview, model); result.before = Metrics(before);
            foreach (Material material in model.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).Distinct()) {
                string materialPath = AssetDatabase.GetAssetPath(material);
                if (!materialPath.StartsWith(assetFolder + "/PetGenerated/", StringComparison.Ordinal)) continue;
                Texture texture = material.GetTexture("_BaseMap");
                Color baseColor = material.GetColor("_BaseColor");
                string[] protectedFloats = { "_AlphaClip", "_Cutoff", "_Surface", "_Cull", "_SrcBlend", "_DstBlend", "_ZWrite" };
                var protectedValues = protectedFloats.Select(material.GetFloat).ToArray();
                int queue = material.renderQueue;
                string beforeSerialized = EditorJsonUtility.ToJson(material);
                var entry = new MaterialResult { path = materialPath, texture = texture != null ? AssetDatabase.GetAssetPath(texture) : "",
                    beforeShade = material.GetColor("_ShadeColor"), beforeBrightness = material.GetFloat("_Brightness") };
                entry.category = PetImportedMaterialAppearance.Configure(material, result.source);
                entry.afterShade = material.GetColor("_ShadeColor"); entry.afterBrightness = material.GetFloat("_Brightness");
                entry.saturation = material.GetFloat("_Saturation"); entry.midtoneContrast = material.GetFloat("_MidtoneContrast");
                if (material.GetTexture("_BaseMap") != texture || material.GetColor("_BaseColor") != baseColor || material.renderQueue != queue ||
                    protectedFloats.Where((property, index) => material.GetFloat(property) != protectedValues[index]).Any())
                    failures.Add(material.name + ": texture, tint, alpha, cull or depth state changed unexpectedly.");
                if (!result.corrected && beforeSerialized != EditorJsonUtility.ToJson(material)) failures.Add("Unrelated character material changed: " + material.name);
                if (texture != null && AssetImporter.GetAtPath(entry.texture) is TextureImporter importer && !importer.sRGBTexture)
                    failures.Add("Painted texture is not imported as sRGB: " + entry.texture);
                if (result.corrected) EditorUtility.SetDirty(material);
                materialResults.Add(entry);
            }
            after = Render(preview, model); result.after = Metrics(after);
            string id = Path.GetFileName(assetFolder);
            File.WriteAllBytes(Path.Combine(output, id + "-color-before.png"), before.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(output, id + "-color-after.png"), after.EncodeToPNG());
            comparison = new Texture2D(before.width + after.width, before.height, TextureFormat.RGBA32, false);
            comparison.SetPixels(0, 0, before.width, before.height, before.GetPixels());
            comparison.SetPixels(before.width, 0, after.width, after.height, after.GetPixels()); comparison.Apply();
            File.WriteAllBytes(Path.Combine(output, id + "-color-comparison.png"), comparison.EncodeToPNG());
            if (result.after.clippedFraction > result.before.clippedFraction + 0.005f) failures.Add("Color correction introduced clipped highlights.");
        } catch (Exception exception) { failures.Add(exception.GetBaseException().Message); }
        finally {
            if (before != null) UnityEngine.Object.DestroyImmediate(before);
            if (after != null) UnityEngine.Object.DestroyImmediate(after);
            if (comparison != null) UnityEngine.Object.DestroyImmediate(comparison);
            preview.Cleanup();
        }
        result.materials = materialResults.ToArray(); result.failures = failures.ToArray(); return result;
    }

    private static ImageMetrics Metrics(Texture2D texture)
    {
        double luminance = 0, saturation = 0; int count = 0, clipped = 0;
        foreach (Color color in texture.GetPixels()) {
            if (Mathf.Abs(color.r - Background.r) + Mathf.Abs(color.g - Background.g) + Mathf.Abs(color.b - Background.b) < 0.045f) continue;
            float peak = Mathf.Max(color.r, color.g, color.b), low = Mathf.Min(color.r, color.g, color.b);
            if (peak < 0.001f) continue;
            count++; luminance += color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722;
            saturation += (peak - low) / peak; if (color.r > 0.985f && color.g > 0.985f && color.b > 0.985f) clipped++;
        }
        return new ImageMetrics { luminance = (float)(luminance / Math.Max(1, count)), saturation = (float)(saturation / Math.Max(1, count)), clippedFraction = clipped / (float)Math.Max(1, count) };
    }

    private static Texture2D Render(PreviewRenderUtility preview, GameObject model)
    {
        var frozen = new List<GameObject>(); var meshes = new List<Mesh>();
        var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>().Where(skin => skin.enabled).ToArray();
        try {
            foreach (var skin in skins) {
                var mesh = new Mesh(); skin.BakeMesh(mesh);
                Matrix4x4 matrix = model.transform.worldToLocalMatrix * PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh);
                mesh.vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                mesh.normals = mesh.normals.Select(normal => matrix.inverse.transpose.MultiplyVector(normal).normalized).ToArray(); mesh.RecalculateBounds();
                var copy = new GameObject("Frozen material preview", typeof(MeshFilter), typeof(MeshRenderer)); copy.transform.SetParent(model.transform, false);
                copy.GetComponent<MeshFilter>().sharedMesh = mesh; copy.GetComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                skin.enabled = false; frozen.Add(copy); meshes.Add(mesh);
            }
            preview.camera.transform.position = new Vector3(0, 1, -3.2f); preview.camera.transform.LookAt(new Vector3(0, 0.86f, 0));
            preview.camera.orthographic = true; preview.camera.orthographicSize = 0.97f;
            preview.camera.nearClipPlane = 0.01f; preview.camera.farClipPlane = 20;
            preview.camera.clearFlags = CameraClearFlags.SolidColor; preview.camera.backgroundColor = Background;
            preview.lights[0].intensity = 1.3f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 170, 0);
            preview.lights[1].intensity = 0.7f; preview.lights[1].transform.rotation = Quaternion.Euler(340, 340, 0); preview.ambientColor = Color.gray;
            preview.BeginStaticPreview(new Rect(0, 0, 600, 780)); preview.Render(true); return preview.EndStaticPreview();
        } finally {
            foreach (var skin in skins) skin.enabled = true;
            foreach (var copy in frozen) UnityEngine.Object.DestroyImmediate(copy);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
