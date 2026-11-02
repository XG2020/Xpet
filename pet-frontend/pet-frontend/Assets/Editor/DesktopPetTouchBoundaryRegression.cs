using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Checks projected touch anatomy with a disposable model and no saved affinity.</summary>
public static class DesktopPetTouchBoundaryRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [MenuItem("Desktop Pet/Run Touch Boundary Regression")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before running touch boundary regression.");
        var report = new StringBuilder();
        bool passed = false;
        Scene activeScene = SceneManager.GetActiveScene();
        Scene preview = default;
        GameObject sandbox = null;
        try
        {
            preview = EditorSceneManager.NewPreviewScene();
            sandbox = new GameObject("Touch Boundary Regression") { hideFlags = HideFlags.HideAndDontSave };
            sandbox.SetActive(false);
            SceneManager.MoveGameObjectToScene(sandbox, preview);
            CheckAffinityBoundary(report);
            CheckProjectedZones(sandbox.transform, report);
            Require(SceneManager.GetActiveScene() == activeScene, "The active scene changed.");
            passed = true;
        }
        catch (Exception error) { report.AppendLine(error.GetBaseException().Message); }
        finally
        {
            if (sandbox != null) UnityEngine.Object.DestroyImmediate(sandbox);
            if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
        }
        report.Insert(0, passed ? "PASSED\n" : "FAILED\n");
        report.AppendLine("Only inactive preview objects were used. No backend, network, or PlayerPrefs writes.");
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "touch-boundary-regression.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        if (passed) Debug.Log("DESKTOP_PET_TOUCH_BOUNDARY_REGRESSION " + report + "Report: " + path);
        else Debug.LogError("DESKTOP_PET_TOUCH_BOUNDARY_REGRESSION " + report + "Report: " + path);
    }

    private static void CheckAffinityBoundary(StringBuilder report)
    {
        foreach (string zone in new[] { "chest", "waist", "lower_body" })
        {
            Require(PetTouchFeedback.RequiresGuard(zone, 0) && PetTouchFeedback.RequiresGuard(zone, 29),
                "Sensitive touch lost its guard below affinity 30: " + zone);
            Require(!PetTouchFeedback.RequiresGuard(zone, 30) && !PetTouchFeedback.RequiresGuard(zone, 100),
                "Affinity boundary is not exactly 30: " + zone);
        }
        foreach (string zone in new[] { "head", "shoulder", "arm", "hand", "leg", null })
            Require(!PetTouchFeedback.RequiresGuard(zone, 0), "Ordinary touch wrongly requires guard: " + zone);
        foreach (int level in new[] { 0, 20, 29, 30, 69, 70, 100 })
        {
            string expected = level < 30 ? "leg_retract" : level < 70 ? "leg_shift" : "leg_sway";
            Require(PetTouchFeedback.LegTouchAction(level) == expected, "Incorrect leg action at affinity " + level);
            Require(PetTouchFeedback.ShouldGainAffinity("leg", level) == (level >= 30), "Leg affinity gain crossed the rejection boundary at " + level);
        }
        report.AppendLine("PASS: sensitive zones below 30 use Guard; leg feedback uses retract below30, shift30-69, sway70+, with no affinity gain on rejected low-affinity leg touches.");
    }

    private static void CheckProjectedZones(Transform parent, StringBuilder report)
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PetModelSetup.PrefabPath);
        AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(PetModelSetup.ModelFolder + "/PetGenerated/Idle.anim");
        Require(source != null && idle != null, "Generate the Lita prefab and Idle clip first.");
        GameObject model = UnityEngine.Object.Instantiate(source, parent, false);
        idle.SampleAnimation(model, 0);
        var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (string name in new[] { "首", "頭", "上半身", "腰", "腕.R", "腕.L", "ひじ.R", "ひじ.L", "手首.R", "手首.L" })
            Require(bones.ContainsKey(name), "Missing touch landmark: " + name);
        var viewObject = new GameObject("Touch Projection Camera") { hideFlags = HideFlags.HideAndDontSave };
        viewObject.transform.SetParent(parent, false);
        Camera view = viewObject.AddComponent<Camera>();
        view.enabled = false;
        view.orthographic = true;
        view.orthographicSize = 1.9f;
        view.pixelRect = new Rect(0, 0, 1200, 800);
        view.transform.position = new Vector3(0, 1, -4);
        view.transform.rotation = Quaternion.identity;
        var services = new GameObject("Touch Test Services") { hideFlags = HideFlags.HideAndDontSave };
        services.transform.SetParent(parent, false);
        PetTouchFeedback touch = services.AddComponent<PetTouchFeedback>();
        typeof(PetTouchFeedback).GetField("character", PrivateInstance).SetValue(touch, model.GetComponent<PetCharacterAnimation>());
        MethodInfo resolve = typeof(PetTouchFeedback).GetMethod("ResolveTouchZone", PrivateInstance);
        Require(resolve != null, "Touch zone resolver is missing.");

        foreach (float scale in new[] { 0.65f, 1f, 1.5f })
        {
            model.transform.localScale = Vector3.one * scale;
            model.transform.localPosition = new Vector3(scale * 0.13f, 0.17f, 0);
            CheckPoint("head", bones["頭"].position);
            foreach (string side in new[] { "R", "L" })
            {
                CheckPoint("shoulder", bones["腕." + side].position);
                CheckPoint("arm", bones["ひじ." + side].position);
                CheckPoint("hand", bones["手首." + side].position);
            }
            CheckPoint("chest", Vector3.Lerp(bones["上半身"].position, bones["首"].position, 0.5f));
            CheckPoint("waist", bones["上半身"].position);
            CheckPoint("lower_body", bones["腰"].position - model.transform.up * scale * 0.07f);
            CheckPoint("leg", bones["腰"].position - model.transform.up * scale * 0.38f);
        }
        report.AppendLine("PASS: projected head, both shoulders/arms/hands, chest, waist, lower body and legs at 3 model scales and translated positions.");

        void CheckPoint(string expected, Vector3 world)
        {
            Vector2 point = view.WorldToScreenPoint(world);
            string zone = (string)resolve.Invoke(touch, new object[] { view, point });
            Require(zone == expected, $"Projected {expected} became {zone} at scale {model.transform.localScale.x:F2}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
