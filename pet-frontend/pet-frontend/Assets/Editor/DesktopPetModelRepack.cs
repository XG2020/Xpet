using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Repacks already imported prefabs after animation/material fixes, without reconverting sources.</summary>
public static class DesktopPetModelRepack
{
    [MenuItem("Desktop Pet/Repack Existing Model Library")]
    public static void Repack()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        string assets = "Assets/ImportedPets";
        if (!Directory.Exists(assets)) throw new DirectoryNotFoundException("No imported pets.");
        var folders = Directory.GetDirectories(assets).Select(p => p.Replace('\\', '/')).Where(p => File.Exists(p + "/DesktopPet.prefab")).ToArray();
        foreach (string folder in folders)
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { folder + "/PetGenerated" })) {
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (material == null || !material.HasProperty("_BaseColor")) continue;
                Texture texture = material.GetTexture("_BaseMap");
                if (material.name.EndsWith("_Mat_Face", StringComparison.OrdinalIgnoreCase) && texture != null && texture.name.Contains("_Face_Color")) {
                    material.SetFloat("_AlphaClip", 0); EditorUtility.SetDirty(material);
                }
                if (material.name.StartsWith("Mat_EyeShadow_", StringComparison.OrdinalIgnoreCase)) {
                    Color tint = material.GetColor("_BaseColor"); tint.a = 0;
                    material.SetColor("_BaseColor", tint); material.SetTexture("_BaseMap", null); EditorUtility.SetDirty(material);
                }
            }
        AssetDatabase.SaveAssets();
        var bundles = folders.Select(folder => new AssetBundleBuild {
            assetBundleName = Path.GetFileName(folder) + ".petmodel",
            assetNames = new[] { folder + "/DesktopPet.prefab" }, addressableNames = new[] { "model" }
        }).ToArray();
        string output = Path.GetFullPath("Temp/DesktopPetModelImport/Repack/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var result = BuildPipeline.BuildAssetBundles(output, bundles,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("Model bundle build failed; existing library kept.");
        foreach (var bundle in bundles) {
            string id = Path.GetFileNameWithoutExtension(bundle.assetBundleName);
            string destination = Path.Combine(PetModelLibrary.LibraryPath, id, bundle.assetBundleName);
            string manifestPath = Path.Combine(PetModelLibrary.LibraryPath, id, "model.json");
            if (!File.Exists(manifestPath)) throw new IOException("Missing library metadata: " + id);
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            if ((string)manifest["id"] != id || (string)manifest["bundle_file"] != bundle.assetBundleName)
                throw new IOException("Model metadata does not match the rebuilt bundle: " + id);
            // Preserve custom metadata while recording the actual bundle build version.
            manifest["unity_version"] = Application.unityVersion;
            string pending = destination + ".pending";
            string manifestPending = manifestPath + ".pending";
            File.Copy(Path.Combine(output, bundle.assetBundleName), pending, true);
            File.WriteAllText(manifestPending, manifest.ToString());
            if (File.Exists(destination)) File.Replace(pending, destination, null);
            else File.Move(pending, destination);
            File.Replace(manifestPending, manifestPath, null);
        }
        Debug.Log("DESKTOP_PET_MODELS_REPACKED: " + bundles.Length + " model packs.");
    }
}
