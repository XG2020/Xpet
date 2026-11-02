using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class DesktopPetSettingsWindowChecks
{
    private static bool running, wasOpen, originalMaximized;
    private static int step, waitFrame;
    private static SettingsPanelController settings;
    private static DesktopPetSettingsWindow controller;
    private static RectTransform card;
    private static Vector2 originalSize, originalPosition, originalCenter, resizedSize;
    private static Vector3 cameraPosition;
    private static float cameraSize;
    private static readonly Dictionary<string, float?> floats = new Dictionary<string, float?>();
    private static int? maximized;

    [MenuItem("Desktop Pet/Run Settings Window Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play mode before running settings window checks.");
        if (running) return;
        CheckGeometry();
        settings = UnityEngine.Object.FindFirstObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
        controller = UnityEngine.Object.FindFirstObjectByType<DesktopPetSettingsWindow>(FindObjectsInactive.Include);
        Require(settings != null && controller != null, "Settings resize controller is not installed.");
        card = controller.GetComponent<RectTransform>();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        originalSize = (Vector2)typeof(DesktopPetSettingsWindow).GetField("normalSize", flags).GetValue(controller);
        originalCenter = (Vector2)typeof(DesktopPetSettingsWindow).GetField("normalCenter", flags).GetValue(controller);
        originalPosition = card.anchoredPosition;
        originalMaximized = controller.IsMaximized; wasOpen = settings.IsOpen;
        floats.Clear();
        foreach (string suffix in new[] { ".width", ".height", ".center_x", ".center_y" }) {
            string key = DesktopPetSettingsWindow.PrefsKey + suffix;
            floats[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : (float?)null;
        }
        string maxKey = DesktopPetSettingsWindow.PrefsKey + ".maximized";
        maximized = PlayerPrefs.HasKey(maxKey) ? PlayerPrefs.GetInt(maxKey) : (int?)null;
        cameraPosition = Camera.main.transform.position; cameraSize = Camera.main.orthographicSize;
        running = true; step = 0; settings.Open(); waitFrame = Time.frameCount + 8;
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
    }

    private static void Tick()
    {
        if (!running || Time.frameCount < waitFrame) return;
        try {
            Canvas.ForceUpdateCanvases();
            if (step == 0) {
                if (controller.IsMaximized) controller.ToggleMaximized();
                Require(card.Find("Header/MaximizeSettings") != null, "Maximize control is missing.");
                var handles = card.GetComponentsInChildren<DesktopPetSettingsResizeHandle>(true);
                Require(handles.Length == 8, "Expected eight edge/corner handles.");
                foreach (var handle in handles)
                    Require(handle.GetComponent<UnityEngine.UI.Image>().raycastTarget && handle.GetComponent<UnityEngine.UI.LayoutElement>().ignoreLayout,
                        "Resize handle must receive clicks without affecting layout.");
                Vector2 start = RectTransformUtility.WorldToScreenPoint(null,
                    card.TransformPoint(new Vector3(card.rect.xMax, card.rect.yMin)));
                var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
                    position = start, pressPosition = start };
                Vector2 before = card.rect.size;
                controller.BeginResize(pointer, new Vector2Int(1, -1));
                pointer.position += new Vector2(-80, 80) * card.GetComponentInParent<Canvas>().scaleFactor;
                controller.Resize(pointer); controller.EndResize();
                resizedSize = card.rect.size;
                Require(resizedSize.x < before.x - 10 || resizedSize.y < before.y - 10, "Dragging the corner did not resize settings.");
                controller.ToggleMaximized();
                Require(controller.IsMaximized, "Maximize did not change state.");
                Rect area = DesktopPetPanelDrag.WorkArea(card.parent as RectTransform);
                Require(Mathf.Abs(card.rect.width * card.localScale.x - (area.width - 16)) < 2 &&
                    Mathf.Abs(card.rect.height * card.localScale.y - (area.height - 16)) < 2, "Maximized settings do not fill the available work area.");
                step++; waitFrame = Time.frameCount + 3;
            } else if (step == 1) {
                controller.ToggleMaximized();
                Require(Vector2.Distance(card.rect.size, resizedSize) < 2, "Restore did not recover the user-selected size.");
                settings.Close(); settings.Open(); step++; waitFrame = Time.frameCount + 8;
            } else {
                Require(Vector2.Distance(card.rect.size, resizedSize) < 2, "Reopening settings lost its saved size.");
                Require(!DesktopPetSettingsWindow.AnyResizing, "Resize state was not released.");
                Require(Vector3.Distance(Camera.main.transform.position, cameraPosition) < 0.0001f &&
                    Mathf.Abs(Camera.main.orthographicSize - cameraSize) < 0.0001f, "Editor settings changed the character camera.");
                Finish(null);
            }
        } catch (Exception exception) { Finish(exception.ToString()); }
    }

    private static void CheckGeometry()
    {
        Rect area = new Rect(-600, -400, 1200, 800), original = new Rect(-360, -300, 720, 600);
        Vector2 minimum = new Vector2(540, 420);
        foreach (int x in new[] { -1, 0, 1 }) foreach (int y in new[] { -1, 0, 1 }) {
            if (x == 0 && y == 0) continue;
            foreach (float distance in new[] { -2000f, -70f, 70f, 2000f }) {
                Rect next = DesktopPetSettingsWindow.ResizeBounds(original, new Vector2(distance, distance), new Vector2Int(x, y), area, minimum);
                Require(next.width >= minimum.x && next.height >= minimum.y &&
                    next.xMin >= area.xMin && next.xMax <= area.xMax && next.yMin >= area.yMin && next.yMax <= area.yMax,
                    "Resize escaped bounds or went below the minimum.");
                Require((x < 0 ? next.xMax == original.xMax : x > 0 ? next.xMin == original.xMin : next.xMin == original.xMin && next.xMax == original.xMax) &&
                    (y < 0 ? next.yMax == original.yMax : y > 0 ? next.yMin == original.yMin : next.yMin == original.yMin && next.yMax == original.yMax),
                    "Dragging one edge moved its opposite edge.");
            }
        }
    }

    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (running && state == PlayModeStateChange.ExitingPlayMode) Finish("Play mode ended before verification completed.");
    }

    private static void Finish(string error)
    {
        running = false; EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= PlayModeChanged;
        if (settings != null && controller != null) {
            settings.Open();
            if (controller.IsMaximized != originalMaximized) controller.ToggleMaximized();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(DesktopPetSettingsWindow).GetField("normalSize", flags).SetValue(controller, originalSize);
            typeof(DesktopPetSettingsWindow).GetField("normalCenter", flags).SetValue(controller, originalCenter);
            card.anchoredPosition = originalPosition; controller.FitToViewport();
            if (!wasOpen) settings.Close();
        }
        foreach (var item in floats) { if (item.Value.HasValue) PlayerPrefs.SetFloat(item.Key, item.Value.Value); else PlayerPrefs.DeleteKey(item.Key); }
        string maxKey = DesktopPetSettingsWindow.PrefsKey + ".maximized";
        if (maximized.HasValue) PlayerPrefs.SetInt(maxKey, maximized.Value); else PlayerPrefs.DeleteKey(maxKey);
        PlayerPrefs.Save();
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/settings-window"));
        Directory.CreateDirectory(folder);
        string result = error == null ? "PASSED: eight edges/corners, opposite-edge stability, minimum/maximum bounds, actual corner drag, maximize/restore, reopen persistence and unchanged Editor camera. Native Windows projection requires standalone verification." : "FAILED: " + error;
        File.WriteAllText(Path.Combine(folder, "results.txt"), result);
        if (error == null) Debug.Log("DESKTOP_PET_SETTINGS_WINDOW " + result); else Debug.LogError(result);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
