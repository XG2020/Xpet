using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Checks the actual five meshes in disposable previews and renders face contact sheets.</summary>
public static class DesktopPetFacialExpressionChecks
{
    private static readonly string[] Expressions = { "neutral", "happy", "curious", "thinking", "sad", "surprised", "shy", "angry" };
    [Serializable] private sealed class ExpressionResult { public string emotion; public float vertexDelta; }
    [Serializable] private sealed class ModelResult { public string model; public string[] channels; public ExpressionResult[] expressions; public string[] failures; }
    [Serializable] private sealed class Report { public bool passed; public string contactSheetOrder; public ModelResult[] models; }

    [MenuItem("Desktop Pet/Run Facial Expression Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before facial preview checks.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/expressions"));
        Directory.CreateDirectory(folder);
        var paths = new List<string> { PetModelSetup.PrefabPath };
        if (AssetDatabase.IsValidFolder("Assets/ImportedPets"))
            paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)));
        UnityEngine.Random.State randomState = UnityEngine.Random.state;
        ModelResult[] results;
        try { results = paths.Select(path => CheckModel(path, folder)).ToArray(); }
        finally { UnityEngine.Random.state = randomState; }
        bool passed = results.All(result => result.failures.Length == 0);
        var report = new Report { passed = passed, contactSheetOrder = "Top: neutral, happy, curious, thinking. Bottom: sad, surprised, shy, angry.", models = results };
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonUtility.ToJson(report, true));
        if (!passed) throw new InvalidOperationException("FACIAL_EXPRESSION_CHECKS_FAILED: " + string.Join("; ", results.SelectMany(r => r.failures.Select(f => r.model + ": " + f))));
        Debug.Log("FACIAL_EXPRESSION_CHECKS_PASSED: " + results.Length + " actual models; expression deformation, smooth transitions, blink ownership, speech coexistence and face contact sheets.");
    }

    private static ModelResult CheckModel(string path, string folder)
    {
        var result = new ModelResult { model = path };
        var failures = new List<string>();
        var measured = new List<ExpressionResult>();
        var preview = new PreviewRenderUtility();
        Texture2D sheet = null;
        try {
            var model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            preview.AddSingleGO(model); model.transform.position = Vector3.zero;
            var animator = model.GetComponent<Animator>();
            if (animator != null) {
                animator.enabled = false;
                AnimationClip idle = animator.runtimeAnimatorController?.animationClips.FirstOrDefault(clip => clip.name == "Idle");
                idle?.SampleAnimation(model, 0);
            }
            var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(skin => skin.sharedMesh != null).ToArray();
            var mixer = new PetFacialExpression(skins);
            result.channels = mixer.DescribeChannels();
            if (mixer.ChannelCount == 0) throw new InvalidOperationException("No expression channels mapped.");
            mixer.Sample("neutral");
            var baseline = BakeFaceVertices(skins);
            float[] neutralWeights = Weights(skins), happyWeights = null;
            sheet = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
            string id = path.Contains("ImportedPets") ? Path.GetFileName(Path.GetDirectoryName(path)) : "builtin-lita";
            for (int i = 0; i < Expressions.Length; i++) {
                string emotion = Expressions[i]; mixer.Sample(emotion);
                float change = Difference(baseline, BakeFaceVertices(skins));
                measured.Add(new ExpressionResult { emotion = emotion, vertexDelta = change });
                if (emotion != "neutral" && change < 0.00005f) failures.Add(emotion + " did not deform the visible model.");
                if (emotion == "happy") happyWeights = Weights(skins);
                foreach (float value in Weights(skins)) if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 100)
                    throw new InvalidOperationException("Expression produced an invalid shape weight.");
                Texture2D tile = RenderFace(preview, model);
                sheet.SetPixels((i % 4) * 320, (1 - i / 4) * 360, 320, 360, tile.GetPixels());
                UnityEngine.Object.DestroyImmediate(tile);
            }
            sheet.Apply(); File.WriteAllBytes(Path.Combine(folder, id + "-expressions.png"), sheet.EncodeToPNG());
            mixer.Reset(); mixer.SetEmotion("happy", 0); mixer.Tick(0.02f, 0.02f, 0, false);
            float[] transition = Weights(skins);
            if (Distance(transition, happyWeights) < 0.001f || transition.Max() <= 0) failures.Add("Emotion transition snapped or failed to start.");
            mixer.Tick(1, 1, 0, false);
            if (Distance(Weights(skins), happyWeights) > 0.1f) failures.Add("Emotion transition did not settle.");
            for (int i = 0; i < 60; i++) mixer.Tick(8 + i * 0.05f, 0.05f, 0, false);
            // Sample deterministically after the runtime timer expiry; random eyelids are
            // intentionally independent from the emotional profile being verified here.
            if (mixer.Emotion != "neutral") failures.Add("Expression did not return to neutral after its hold.");
            mixer.Sample("happy", 0, 0);
            var mouthChannels = new List<(SkinnedMeshRenderer, int)>();
            foreach (var skin in skins) for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) {
                string name = skin.sharedMesh.GetBlendShapeName(i); string shortName = name.Substring(name.LastIndexOf('.') + 1);
                if (shortName == "あ" || name.EndsWith("_Mouth_00_A", StringComparison.OrdinalIgnoreCase)) {
                    skin.SetBlendShapeWeight(i, 52); mouthChannels.Add((skin, i));
                }
            }
            float[] beforeSpeechMix = Weights(skins);
            mixer.Sample("happy", 0, 52);
            if (mouthChannels.Count == 0 || mouthChannels.Any(channel => Mathf.Abs(channel.Item1.GetBlendShapeWeight(channel.Item2) - 52) > 0.01f))
                failures.Add("Expression overwrote the independent speech A shape.");
            if (Distance(beforeSpeechMix, Weights(skins)) < 0.1f) failures.Add("Speaking did not reduce conflicting mouth expression.");
            foreach (var channel in mouthChannels) channel.Item1.SetBlendShapeWeight(channel.Item2, 0);
            mixer.Sample("surprised", 1);
            if (!Weights(skins).Any(weight => weight >= 99)) failures.Add("Blink did not close the mapped eyelids.");
            mixer.Reset();
            if (Weights(skins).Any(weight => weight != 0)) failures.Add("Face reset left stale expression channels.");
        } catch (Exception exception) { failures.Add(exception.GetBaseException().Message); }
        finally { if (sheet != null) UnityEngine.Object.DestroyImmediate(sheet); preview.Cleanup(); }
        result.expressions = measured.ToArray(); result.failures = failures.ToArray(); return result;
    }

    private static float[] Weights(SkinnedMeshRenderer[] skins)
    {
        var values = new List<float>();
        foreach (var skin in skins) for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) values.Add(skin.GetBlendShapeWeight(i));
        return values.ToArray();
    }
    private static float Distance(float[] a, float[] b)
    {
        float result = 0; for (int i = 0; i < a.Length; i++) result = Mathf.Max(result, Mathf.Abs(a[i] - b[i])); return result;
    }
    private static Vector3[] BakeFaceVertices(SkinnedMeshRenderer[] skins)
    {
        var vertices = new List<Vector3>();
        foreach (var skin in skins.Where(skin => skin.enabled && skin.gameObject.activeInHierarchy && skin.sharedMesh.blendShapeCount > 0)) {
            var mesh = new Mesh();
            try { skin.BakeMesh(mesh); Matrix4x4 matrix = PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh); vertices.AddRange(mesh.vertices.Select(matrix.MultiplyPoint3x4)); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        return vertices.ToArray();
    }
    private static float Difference(Vector3[] a, Vector3[] b)
    {
        float result = 0; for (int i = 0; i < a.Length; i++) result = Mathf.Max(result, Vector3.Distance(a[i], b[i])); return result;
    }

    private static Texture2D RenderFace(PreviewRenderUtility preview, GameObject model)
    {
        var frozen = new List<GameObject>(); var meshes = new List<Mesh>();
        var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>().Where(skin => skin.enabled).ToArray();
        try {
            foreach (var skin in skins) {
                var mesh = new Mesh(); skin.BakeMesh(mesh);
                Matrix4x4 matrix = model.transform.worldToLocalMatrix * PetCompatibleModelBuilder.BakedVertexToWorld(skin, mesh);
                mesh.vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                mesh.normals = mesh.normals.Select(normal => matrix.inverse.transpose.MultiplyVector(normal).normalized).ToArray(); mesh.RecalculateBounds();
                var copy = new GameObject("Frozen face preview", typeof(MeshFilter), typeof(MeshRenderer)); copy.transform.SetParent(model.transform, false);
                copy.GetComponent<MeshFilter>().sharedMesh = mesh; copy.GetComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                skin.enabled = false; frozen.Add(copy); meshes.Add(mesh);
            }
            Transform head = model.GetComponentsInChildren<Transform>().FirstOrDefault(bone => bone.name == "頭" || bone.name == "Head");
            Vector3 center = (head != null ? head.position : new Vector3(0, 1.5f, 0)) + Vector3.up * 0.065f;
            preview.camera.transform.position = center + Vector3.back * 2;
            preview.camera.transform.LookAt(center);
            preview.camera.orthographic = true; preview.camera.orthographicSize = 0.26f;
            preview.camera.nearClipPlane = 0.01f; preview.camera.farClipPlane = 20;
            preview.camera.clearFlags = CameraClearFlags.SolidColor; preview.camera.backgroundColor = new Color32(30, 38, 51, 255);
            preview.lights[0].intensity = 1.3f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 170, 0);
            preview.lights[1].intensity = 0.7f; preview.lights[1].transform.rotation = Quaternion.Euler(340, 340, 0); preview.ambientColor = Color.gray;
            preview.BeginStaticPreview(new Rect(0, 0, 320, 360)); preview.Render(true); return preview.EndStaticPreview();
        } finally {
            foreach (var skin in skins) skin.enabled = true;
            foreach (var copy in frozen) UnityEngine.Object.DestroyImmediate(copy);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
