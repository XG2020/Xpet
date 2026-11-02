using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Exercises the same async bundle loader used by the shipped model picker.</summary>
public static class DesktopPetModelLibraryChecks
{
    private static PetModelLibrary library;
    private static PetModelEntry[] entries;
    private static int index, phase;
    private static double next, deadline;
    private static string original, folder;
    private static bool hadPreference;
    private static string savedPreference;
    private static StringBuilder report;
    private static Transform sideActor;
    private static Quaternion frontRotation;
    [MenuItem("Desktop Pet/Run Model Library Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        library = UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>();
        if (library == null || library.IsBusy) throw new InvalidOperationException("Wait for the model library to initialize.");
        entries = library.ModelEntries.ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("No models in " + PetModelLibrary.LibraryPath + "; files=" + Directory.GetFiles(PetModelLibrary.LibraryPath, "model.json", SearchOption.AllDirectories).Length + "; " + library.Status);
        original = library.SelectedModelId;
        hadPreference = PlayerPrefs.HasKey(PetModelLibrary.SelectedModelKey);
        savedPreference = PlayerPrefs.GetString(PetModelLibrary.SelectedModelKey, PetModelLibrary.BuiltinModelId);
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/models"));
        Directory.CreateDirectory(folder); report = new StringBuilder(); index = phase = 0;
        next = EditorApplication.timeSinceStartup; deadline = next + 60;
        UnityEngine.Object.FindAnyObjectByType<SettingsPanelController>()?.Close();
        var canvas = GameObject.Find("DesktopPetCanvas"); canvas.transform.Find("ChatPanel").gameObject.SetActive(false);
        EditorApplication.update -= Tick; EditorApplication.update += Tick;
    }
    private static void Tick()
    {
        if (!EditorApplication.isPlaying) { Finish("Play mode ended."); return; }
        double now = EditorApplication.timeSinceStartup; if (now < next) return;
        next = now + 0.2;
        try {
            if (phase == 0) {
                if (index >= entries.Length) { library.SelectModel(original); phase = 7; deadline = now + 60; return; }
                library.SelectModel(entries[index].id); deadline = now + 60; phase = 1;
            } else if (phase == 1) {
                if (library.IsBusy) { if (now > deadline) throw new Exception("Model load timed out."); return; }
                if (library.SelectedModelId != entries[index].id) throw new Exception(entries[index].label + ": " + library.Status);
                var actor = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
                if (actor == null) throw new Exception("New model has no active character.");
                var mouth = typeof(PetCharacterAnimation).GetField("mouthChannels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(actor) as ICollection;
                if (mouth == null || mouth.Count == 0) throw new Exception("This model has no mapped speech mouth shape.");
                var skins = actor.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled).ToArray();
                if (skins.Length == 0 || skins.Any(s => s.sharedMaterials.Any(m => m == null || m.shader == null || m.shader.name.Contains("Error"))))
                    throw new Exception("Missing model mesh/material/shader.");
                var capsule = actor.GetComponent<CapsuleCollider>();
                if (capsule == null || Mathf.Abs(capsule.height - 1.72f) > 0.03f) throw new Exception("Model body normalization failed.");
                actor.PlayAction("idle");
                report.AppendLine("PASS load: " + entries[index].label + ", skins=" + skins.Length + ", materials=" + skins.Sum(s => s.sharedMaterials.Length));
                phase = 2; next = now + 0.9;
            } else if (phase == 2) {
                UnityEngine.Object.FindAnyObjectByType<PetSpeechBubble>().ShowMessage(entries[index].label + "：模型切换、头顶锚点和骨架已就绪。");
                phase = 3; next = now + 0.3;
            } else if (phase == 3) {
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, entries[index].id + "-idle.png")); phase = 8;
            } else if (phase == 8) {
                sideActor = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>().transform;
                frontRotation = sideActor.localRotation;
                sideActor.localRotation = frontRotation * Quaternion.Euler(0, 90, 0);
                phase = 9; next = now + 0.4;
            } else if (phase == 9) {
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, entries[index].id + "-side.png")); phase = 10;
            } else if (phase == 10) {
                sideActor.localRotation = frontRotation; sideActor = null; phase = 4;
            } else if (phase == 4) {
                UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>().PlayAction("guard"); phase = 5; next = now + 1.0;
            } else if (phase == 5) {
                var actor = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
                if (!actor.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Guard")) throw new Exception("Guard mapping failed.");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, entries[index].id + "-guard.png")); phase = 11; deadline = now + 30;
            } else if (phase == 11) {
                var actor = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
                if (actor.IsGuarding) { if (now > deadline) throw new Exception("Guard did not finish."); return; }
                actor.PlayGuard("lower_body"); phase = 12; next = now + 1.1;
            } else if (phase == 12) {
                var actor = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
                if (!actor.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Base Layer.GuardLower")) throw new Exception("Lower guard mapping failed.");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, entries[index].id + "-guard-lower.png"));
                report.AppendLine("PASS: GuardLower runtime state and clothing bones=" + actor.GetComponent<PetSecondaryMotion>().SimulatedBoneCount);
                phase = 6;
            } else if (phase == 6) { index++; phase = 0; }
            else if (phase == 7) {
                if (library.IsBusy) { if (now > deadline) throw new Exception("Restoring original model timed out."); return; }
                if (library.SelectedModelId != original) throw new Exception("Cannot restore original model.");
                report.AppendLine("PASS: original model restored."); Finish(null);
            }
        } catch (Exception error) { Finish(error.Message); }
    }
    private static void Finish(string error)
    {
        EditorApplication.update -= Tick;
        if (sideActor != null) { sideActor.localRotation = frontRotation; sideActor = null; }
        if (error != null && library != null && !library.IsBusy) library.SelectModel(original);
        if (hadPreference) PlayerPrefs.SetString(PetModelLibrary.SelectedModelKey, savedPreference);
        else PlayerPrefs.DeleteKey(PetModelLibrary.SelectedModelKey);
        PlayerPrefs.Save();
        report.Insert(0, error == null ? "PASSED\n" : "FAILED: " + error + "\n");
        File.WriteAllText(Path.Combine(folder, "model-library-checks.txt"), report.ToString());
        if (error == null) Debug.Log(report); else Debug.LogError(report);
    }
}
