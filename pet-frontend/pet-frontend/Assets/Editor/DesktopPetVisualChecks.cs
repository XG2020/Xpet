using System;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>Runs real scene layout checks and captures the Game view without touching other desktop apps.</summary>
public static class DesktopPetVisualChecks
{
    private static int stage;
    private static double next;
    private static string directory;
    private static DesktopPetAppearance appearance;
    private static DesktopPetUIActions actions;
    private static SettingsPanelController settings;
    private static PetCharacterAnimation character;
    private static float oldSize;
    private static string oldColor;
    private static string failure;

    [MenuItem("Desktop Pet/Run Visual Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        EditorApplication.update -= Tick;
        appearance = UnityEngine.Object.FindAnyObjectByType<DesktopPetAppearance>();
        actions = UnityEngine.Object.FindAnyObjectByType<DesktopPetUIActions>();
        settings = UnityEngine.Object.FindAnyObjectByType<SettingsPanelController>();
        character = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
        if (appearance == null || actions == null || settings == null || character == null) throw new InvalidOperationException("Missing visual components.");
        oldSize = PlayerPrefs.GetFloat("desktop_pet.model_size", 0.65f);
        oldColor = PlayerPrefs.GetString("desktop_pet.panel_color", "#182130");
        directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification"));
        Directory.CreateDirectory(directory);
        stage = 0; failure = null; next = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) { Finish(); return; }
        if (EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + 0.7;
        try {
            switch (stage++) {
                case 0:
                    settings.Close(); appearance.SetModelSize(0.65f); actions.OpenChat(); break;
                case 1:
                    CheckChat(); ScreenCapture.CaptureScreenshot(Path.Combine(directory, "chat.png"));
                    break;
                case 2:
                    settings.Open(); appearance.SetBackgroundColor("#E7EDF5"); break;
                case 3:
                    CheckSettings(); ScreenCapture.CaptureScreenshot(Path.Combine(directory, "settings-light.png")); break;
                case 4:
                    appearance.SetBackgroundColor("#182130"); break;
                case 5:
                    ScreenCapture.CaptureScreenshot(Path.Combine(directory, "settings-dark.png"));
                    settings.Close(); appearance.SetModelSize(1.05f); break;
                case 6:
                    CheckChat(); character.SetDragging(true, 5); break;
                case 7:
                    if (!character.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Drag")) throw new InvalidOperationException("Drag animation did not start.");
                    ScreenCapture.CaptureScreenshot(Path.Combine(directory, "drag.png")); break;
                case 8:
                    character.SetDragging(false, 0); appearance.SetModelSize(0.35f); break;
                case 9:
                    CheckChat(); break;
                default:
                    Finish(); break;
            }
        } catch (Exception error) { failure = error.Message; Finish(); }
    }

    private static RectTransform Panel(string path)
    {
        var canvas = GameObject.Find("DesktopPetCanvas");
        return canvas.transform.Find(path) as RectTransform;
    }

    private static Rect ScreenRect(RectTransform rect)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity), max = -min;
        foreach (var corner in corners) { Vector2 p = RectTransformUtility.WorldToScreenPoint(null, corner); min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static void CheckChat()
    {
        Canvas.ForceUpdateCanvases(); appearance.UpdateChatPlacement();
        Rect chat = ScreenRect(Panel("ChatPanel"));
        Bounds bounds = character.GetComponentInChildren<SkinnedMeshRenderer>().bounds;
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity), max = -min;
        for (int i = 0; i < 8; i++) {
            Vector3 p = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
            Vector2 screen = Camera.main.WorldToScreenPoint(p); min = Vector2.Min(min, screen); max = Vector2.Max(max, screen);
        }
        if (chat.Overlaps(Rect.MinMaxRect(min.x, min.y, max.x, max.y))) throw new InvalidOperationException("Chat overlaps character.");
        CheckInside(chat);
        if (Panel("ChatPanel/States").gameObject.activeSelf) throw new InvalidOperationException("Debug states are visible.");
    }

    private static void CheckSettings()
    {
        appearance.ClampSettingsToCanvas(); Canvas.ForceUpdateCanvases();
        CheckInside(ScreenRect(Panel("SettingsOverlay/SettingsCard")));
        var overlay = Panel("SettingsOverlay").GetComponent<UnityEngine.UI.Image>();
        if (overlay.color.a != 0 || overlay.raycastTarget) throw new InvalidOperationException("Fullscreen overlay still mixes with the color key.");
        if (Panel("SettingsOverlay/SettingsCard").GetComponent<UnityEngine.UI.Image>().color.a != 1) throw new InvalidOperationException("Settings panel is translucent.");
    }

    private static void CheckInside(Rect rect)
    {
        if (rect.xMin < -1 || rect.yMin < -1 || rect.xMax > Screen.width + 1 || rect.yMax > Screen.height + 1)
            throw new InvalidOperationException("A panel is outside the viewport.");
    }

    private static void Finish()
    {
        EditorApplication.update -= Tick;
        if (appearance != null) { appearance.SetModelSize(oldSize); appearance.SetBackgroundColor(oldColor); }
        if (settings != null) settings.Close();
        if (character != null) character.SetDragging(false, 0);
        var chat = GameObject.Find("DesktopPetCanvas")?.transform.Find("ChatPanel");
        if (chat != null) chat.gameObject.SetActive(false);
        string result = failure == null ? "PASSED: chat avoids character at min/default/max tested sizes; panels in viewport; overlay transparent; backgrounds opaque; drag state invoked." : "FAILED: " + failure;
        File.WriteAllText(Path.Combine(directory, "visual-checks.txt"), result);
        if (failure == null) Debug.Log("DESKTOP_PET_VISUAL_CHECKS " + result); else Debug.LogError(result);
    }
}
