using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Samples disposable models and verifies weapon-only visibility and anatomical hand mounts.</summary>
public static class DesktopPetWeaponChecks
{
    [Serializable] private sealed class ModelResult
    {
        public string model;
        public int weapons, visible;
        public float gripError;
        public string[] evidence, failures;
    }
    [Serializable] private sealed class Report { public bool passed; public ModelResult[] models; }

    [MenuItem("Desktop Pet/Run Weapon Attachment Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/weapons"));
        Directory.CreateDirectory(folder);
        var paths = new List<string> { PetModelSetup.PrefabPath };
        paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)));
        var results = paths.Select(path => Check(path, folder)).ToArray();
        bool passed = results.All(r => r.failures.Length == 0);
        File.WriteAllText(Path.Combine(folder, "weapon-checks.json"), JsonUtility.ToJson(new Report { passed = passed, models = results }, true));
        if (!passed) throw new InvalidOperationException("WEAPON_ATTACHMENT_CHECKS_FAILED: " + string.Join("; ", results.SelectMany(r => r.failures.Select(f => r.model + ": " + f))));
        Debug.Log("WEAPON_ATTACHMENT_CHECKS_PASSED: " + results.Length + " models; only verified weapon meshes hide, grips follow real hand bones, transitions restore weapon roots.");
    }

    private static ModelResult Check(string path, string folder)
    {
        var result = new ModelResult { model = path };
        var failures = new List<string>();
        var preview = new PreviewRenderUtility();
        try
        {
            var model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            preview.AddSingleGO(model); model.transform.position = Vector3.zero;
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var clothing = renderers.Where(r => r.name != "Weapon").ToDictionary(r => r, r => r.enabled);
            var weapon = model.GetComponent<PetWeaponAttachment>() ?? model.AddComponent<PetWeaponAttachment>();
            weapon.Initialize(); result.weapons = weapon.WeaponCount; result.evidence = weapon.Evidence;
            bool expected = path.Contains("model-78bb813f39e75669") || path.Contains("model-a70709522cff2cbb");
            if (weapon.WeaponCount != (expected ? 1 : 0)) failures.Add("Weapon classification differs from verified source assets");
            var animator = model.GetComponent<Animator>(); animator.enabled = false;
            var idle = animator.runtimeAnimatorController.animationClips.First(c => c.name == "Idle");
            var cheer = animator.runtimeAnimatorController.animationClips.First(c => c.name == "Cheer");
            bool sword = path.Contains("model-a70709522cff2cbb");
            var showcase = sword ? animator.runtimeAnimatorController.animationClips.First(c => c.name == "HoldSword") : cheer;
            string showcaseState = sword ? "HoldSword" : "Cheer";
            idle.SampleAnimation(model, 0); weapon.EvaluateForPose("Idle", 0, false, false);
            if (weapon.VisibleWeaponCount != 0) failures.Add("Weapon is visible during Idle");
            string id = expected ? Path.GetFileName(Path.GetDirectoryName(path)) : path.Contains("ImportedPets") ? Path.GetFileName(Path.GetDirectoryName(path)) : "builtin-lita";
            Render(preview, model, Path.Combine(folder, id + "-idle.png"), false);
            cheer.SampleAnimation(model, cheer.length * .5f); weapon.EvaluateForPose("Cheer", .5f, false, false);
            if (sword && weapon.VisibleWeaponCount != 0) failures.Add("Katana is visible during ordinary Cheer");
            showcase.SampleAnimation(model, showcase.length * 0.5f); weapon.EvaluateForPose(showcaseState, 0.5f, false, false);
            result.visible = weapon.VisibleWeaponCount; result.gripError = weapon.MaximumGripError;
            if (result.visible != result.weapons) failures.Add("Verified weapon does not appear during the showcase phase");
            if (result.gripError > 0.0005f) failures.Add("Weapon grip does not coincide with the anatomical palm mount");
            if (expected)
            {
                string poseLabel = sword ? "hold-sword" : "cheer";
                Render(preview, model, Path.Combine(folder, id + "-" + poseLabel + "-front.png"), false);
                Render(preview, model, Path.Combine(folder, id + "-" + poseLabel + "-side.png"), true);
                model.transform.localScale = Vector3.one * 1.6f;
                weapon.EvaluateForPose(showcaseState, 0.5f, false, false);
                if (weapon.MaximumGripError > 0.0005f) failures.Add("Weapon mount drifts when the pet is resized");
                model.transform.localScale = Vector3.one;
                if (sword)
                {
                    var slash = animator.runtimeAnimatorController.animationClips.First(c => c.name == "DrawSlash");
                    foreach (float phase in new[] { .24f, .4f, .64f })
                    {
                        slash.SampleAnimation(model, slash.length * phase); weapon.EvaluateForPose("DrawSlash", phase, false, false);
                        if (weapon.VisibleWeaponCount != 1 || weapon.MaximumGripError > .0005f) failures.Add("Katana draw/slash grip or visibility failed");
                        Render(preview, model, Path.Combine(folder, id + "-draw-slash-" + phase.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ".png"), false);
                    }
                }
            }
            foreach (string state in new[] { "Idle", "Drag", "Guard", "GuardLower", "Speak", "Walk", "Run", "Jump", "Nod", "Think" })
            {
                weapon.EvaluateForPose(state, 0.5f, false, false);
                if (weapon.VisibleWeaponCount != 0) failures.Add("Weapon incorrectly remains visible in " + state);
            }
            weapon.EvaluateForPose("Cheer", 0.5f, true, false);
            if (weapon.VisibleWeaponCount != 0) failures.Add("Weapon remains visible during a state transition");
            foreach (var pair in clothing) if (pair.Key.enabled != pair.Value) failures.Add("A body, clothing or face renderer changed visibility: " + pair.Key.name);
        }
        catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        finally { preview.Cleanup(); }
        result.failures = failures.ToArray(); return result;
    }

    private static void Render(PreviewRenderUtility preview, GameObject model, string path, bool side)
    {
        var frozen = new List<GameObject>(); var meshes = new List<Mesh>();
        var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled).ToArray();
        Bounds bounds = new Bounds(new Vector3(0, 0.86f, 0), new Vector3(0.7f, 1.72f, 0.4f));
        foreach (var skin in skins)
        {
            var mesh = new Mesh(); skin.BakeMesh(mesh);
            Matrix4x4 conversion = model.transform.worldToLocalMatrix * PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh);
            mesh.vertices = mesh.vertices.Select(conversion.MultiplyPoint3x4).ToArray();
            mesh.normals = mesh.normals.Select(n => conversion.inverse.transpose.MultiplyVector(n).normalized).ToArray(); mesh.RecalculateBounds();
            bounds.Encapsulate(mesh.bounds);
            var copy = new GameObject("Weapon review mesh", typeof(MeshFilter), typeof(MeshRenderer));
            copy.transform.SetParent(model.transform, false); copy.GetComponent<MeshFilter>().sharedMesh = mesh;
            copy.GetComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
            skin.enabled = false; frozen.Add(copy); meshes.Add(mesh);
        }
        try
        {
            preview.camera.transform.position = bounds.center + (side ? new Vector3(3, 0.1f, -2) : new Vector3(0, 0.1f, -4));
            preview.camera.transform.LookAt(bounds.center); preview.camera.orthographic = true;
            preview.camera.orthographicSize = Mathf.Max(bounds.size.y * 0.56f, bounds.size.x * 0.67f, bounds.size.z * 0.67f);
            preview.camera.nearClipPlane = 0.01f; preview.camera.farClipPlane = 20;
            preview.camera.clearFlags = CameraClearFlags.SolidColor; preview.camera.backgroundColor = new Color(0.12f, 0.15f, 0.2f);
            preview.lights[0].intensity = 1.3f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 170, 0);
            preview.lights[1].intensity = 0.7f; preview.lights[1].transform.rotation = Quaternion.Euler(340, 340, 0); preview.ambientColor = Color.gray;
            preview.BeginStaticPreview(new Rect(0, 0, 800, 800)); preview.Render(true);
            var texture = preview.EndStaticPreview(); File.WriteAllBytes(path, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
        }
        finally
        {
            foreach (var skin in skins) skin.enabled = true;
            foreach (var copy in frozen) UnityEngine.Object.DestroyImmediate(copy);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
