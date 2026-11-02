using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Data, playback and page regression, preserving the user's saved action preferences.</summary>
public static class DesktopPetActionManagementChecks
{
    private static bool hadPrefs, running;
    private static string savedPrefs, folder;
    private static PetCharacterAnimation character;
    private static SettingsPanelController settings;
    private static RectTransform draggedPanel;
    private static DesktopPetPanelDrag panelDrag;
    private static Vector2 savedPanelPosition, expectedPanelPosition;
    private static bool savedCustomPosition, panelPositionSaved, testedPanelDrag;
    private static string panelPrefsKey;
    private static bool hadPanelSet, hadPanelX, hadPanelY;
    private static int savedPanelSet;
    private static float savedPanelX, savedPanelY;
    [Serializable] private sealed class SavedActionFixture { public int version = 1; public PetActionEntry[] entries; }

    [MenuItem("Desktop Pet/Run Action Management Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        if (running) throw new InvalidOperationException("Action checks are already running.");
        character = UnityEngine.Object.FindAnyObjectByType<PetCharacterAnimation>();
        if (character == null) throw new InvalidOperationException("Wait for the active character to load.");
        running = true; testedPanelDrag = false;
        hadPrefs = PlayerPrefs.HasKey(PetActionLibrary.PrefsKey);
        savedPrefs = PlayerPrefs.GetString(PetActionLibrary.PrefsKey, "");
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/action-management"));
        Directory.CreateDirectory(folder);
        EditorApplication.playModeStateChanged += OnPlayMode;
        character.StartCoroutine(RunSafe(Check()));
    }

    private static IEnumerator RunSafe(IEnumerator checks)
    {
        while (true)
        {
            object next = null;
            bool moved = false;
            string failure = null;
            try { moved = checks.MoveNext(); if (moved) next = checks.Current; }
            catch (Exception error) { failure = error.ToString(); }
            if (failure != null) { Finish(failure); yield break; }
            if (!moved) { Finish(null); yield break; }
            yield return next;
        }
    }

    private static IEnumerator Check()
    {
        var legacyChecks = CheckRetiredActionCompatibility();
        while (legacyChecks.MoveNext()) yield return legacyChecks.Current;
        PlayerPrefs.DeleteKey(PetActionLibrary.PrefsKey); PetActionLibrary.Reload();
        Require(PetActionLibrary.Entries.Count == PetActionLibrary.BuiltInIds.Length, "Expected all built-ins.");
        foreach (string id in PetActionLibrary.BuiltInIds)
        {
            bool blocked = false;
            try { PetActionLibrary.Delete(id); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked, "Built-in deleted: " + id);
        }
        bool invalidBlocked = false;
        try { PetActionLibrary.Add("Invalid", new[] { new PetActionStep { action = "nod", seconds = float.NaN } }, 1); }
        catch (ArgumentException) { invalidBlocked = true; }
        Require(invalidBlocked, "Invalid duration accepted.");
        var custom = PetActionLibrary.Add("回归组合", new[] { new PetActionStep { action = "nod", seconds = 0.8f }, new PetActionStep { action = "happy", seconds = 0.8f } }, 1.25f, "wave");
        PetActionLibrary.Reload();
        Require(PetActionLibrary.Find(custom.id)?.steps.Length == 2, "Custom steps did not persist.");
        Require(PetActionLibrary.ResolveCustom("wave")?.id == custom.id, "Custom trigger did not persist.");
        var animator = character.GetComponent<Animator>();
        var bridge = UnityEngine.Object.FindAnyObjectByType<PetAnimatorBridge>();
        bridge.PreviewAction("wave");
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Wave"), "Built-in preview was replaced by its custom trigger.");
        bridge.PlayWave();
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Nod"), "Automatic custom trigger did not play.");
        character.PlayAction(custom.id);
        yield return new WaitForSecondsRealtime(0.45f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Nod"), "Custom first step did not play.");
        Require(Mathf.Abs(animator.speed - 1.25f) < 0.01f, "Custom speed was not applied.");
        yield return new WaitForSecondsRealtime(0.75f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Happy"), "Custom second step did not play.");
        PetActionLibrary.SetEnabled(custom.id, false);
        yield return new WaitForSecondsRealtime(0.3f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "Disabling custom action did not stop it.");
        PetActionLibrary.SetEnabled("walk", false); character.PlayAction("walk");
        Require(!character.IsWalking, "Disabled walk moved the character.");
        PetActionLibrary.SetEnabled("guard", false); character.PlayAction("guard");
        Require(!character.IsGuarding, "Disabled guard still played.");
        character.PlayGuard("lower_body");
        Require(!character.IsGuarding, "Disabled lower guard still played.");
        PetActionLibrary.SetEnabled("guard", true);
        character.PlayGuard("lower_body");
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.GuardLower"), "Lower body touch did not select lower guard.");
        character.PlayAction("speak");
        yield return new WaitForSecondsRealtime(0.2f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.GuardLower"), "Reply interrupted lower guard.");
        PetActionLibrary.SetEnabled("guard", false);
        yield return null;
        Require(!character.IsGuarding, "Disabling lower guard did not stop it.");
        PetActionLibrary.SetEnabled("guard", true);
        var backend = UnityEngine.Object.FindAnyObjectByType<PetBackendHttpClient>();
        backend.onChatRequestStarted.Invoke(new PetChatSubmission { interaction_zone = "lower_body" });
        bridge.HandleAction("guard");
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.GuardLower"), "Delayed reply lost its lower-body zone.");
        PetActionLibrary.SetEnabled("guard", false);
        yield return null;
        PetActionLibrary.SetEnabled("guard", true);
        backend.onChatRequestStarted.Invoke(new PetChatSubmission { message = "ordinary conversation" });
        bridge.HandleAction("guard");
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Guard"), "Ordinary conversation retained a stale touch zone.");
        backend.onRequestCancelled.Invoke();
        PetActionLibrary.SetEnabled("guard", false);
        yield return null;
        PetActionLibrary.SetEnabled("drag", false); character.SetDragging(true, 25);
        yield return new WaitForSecondsRealtime(0.3f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "Disabled drag animation played.");
        character.SetDragging(false, 0);
        PetActionLibrary.SetEnabled("idle", false); character.PlayAction("idle");
        yield return null;
        Require(animator.speed == 0 && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "Disabled idle must keep a static standing pose.");
        PetActionLibrary.SetEnabled("idle", true);
        yield return null;
        Require(animator.speed > 0, "Re-enabling idle did not resume animation.");
        PetActionLibrary.SetEnabled("wave", false);
        UnityEngine.Object.FindAnyObjectByType<PetAnimatorBridge>().PlayWave();
        yield return new WaitForSecondsRealtime(0.3f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "Bridge ignored the disabled action.");
        PetActionLibrary.Reload(); Require(!PetActionLibrary.IsEnabled("wave"), "Disabled preference did not persist.");
        PetActionLibrary.Delete(custom.id); Require(PetActionLibrary.Find(custom.id) == null, "Custom deletion failed.");
        var tabs = UnityEngine.Object.FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        Require(tabs.FindPageContent("动作管理") != null && tabs.FindPageContent("模型导入") != null, "Missing settings pages.");
        Require(tabs.FindPageContent("外观").Find("ModelLibrarySettings") == null, "Model import remains on Appearance.");
        Require(tabs.FindPageContent("模型导入").Find("ModelLibrarySettings/Tutorial") != null, "Import tutorial missing.");
        settings = UnityEngine.Object.FindAnyObjectByType<SettingsPanelController>(); settings.Open();
        tabs.ShowPage("动作管理");
        // Reopening the page must not register any button listener twice.
        tabs.ShowPage("外观"); tabs.ShowPage("动作管理");
        var group = tabs.FindPageContent("动作管理").Find("ActionManagement");
        var select = Control<TMP_Dropdown>(group, "ActionDropdown");
        var enabled = Control<UnityEngine.UI.Toggle>(group, "ActionEnabled");
        var delete = Control<UnityEngine.UI.Button>(group, "ManageButtons/Delete");
        select.value = 0;
        Require(!delete.interactable, "Built-in delete button must be disabled.");
        enabled.isOn = false; Require(!PetActionLibrary.IsEnabled("idle"), "Enable toggle was not wired.");
        enabled.isOn = true; Require(PetActionLibrary.IsEnabled("idle"), "Enable toggle did not restore the built-in.");
        Control<TMP_InputField>(group, "ActionName").text = "界面回归组合";
        Control<TMP_InputField>(group, "StepSeconds").text = "0.8";
        Control<TMP_InputField>(group, "ActionSpeed").text = "1";
        var stepDropdown = Control<TMP_Dropdown>(group, "StepDropdown");
        Require(!stepDropdown.options.Any(option => option.text == "抱刀" || option.text == "拔刀斩"), "Retired actions remain in the composition picker.");
        stepDropdown.value = stepDropdown.options.FindIndex(option => option.text == PetActionLibrary.Label("nod"));
        Control<UnityEngine.UI.Button>(group, "StepButtons/AddStep").onClick.Invoke();
        Control<UnityEngine.UI.Button>(group, "CreateAction").onClick.Invoke();
        var uiAction = PetActionLibrary.Entries.SingleOrDefault(entry => entry.label == "界面回归组合");
        Require(uiAction != null && uiAction.steps.Length == 1, "UI creation failed or one click added duplicate steps.");
        Require(delete.interactable && select.value == PetActionLibrary.Entries.ToList().IndexOf(uiAction), "Created action was not selected for management.");
        Control<UnityEngine.UI.Button>(group, "ManageButtons/Preview").onClick.Invoke();
        yield return new WaitForSecondsRealtime(0.35f);
        Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Nod"),
            "Preview button did not play Nod: step=" + uiAction.steps[0].action
            + ", selected=" + select.value + ", status=" + Control<TMP_Text>(group, "ActionStatus").text
            + ", current=" + animator.GetCurrentAnimatorStateInfo(0).fullPathHash
            + ", next=" + animator.GetNextAnimatorStateInfo(0).fullPathHash);
        enabled.isOn = false;
        yield return new WaitForSecondsRealtime(0.3f);
        Require(!PetActionLibrary.IsEnabled(uiAction.id) && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"), "UI disable did not stop preview.");
        delete.onClick.Invoke();
        Require(PetActionLibrary.Find(uiAction.id) == null && !delete.interactable, "UI deletion failed or the built-in delete button became enabled.");
        Canvas.ForceUpdateCanvases();
        BeginPanelDragCheck(tabs.transform as RectTransform);
        yield return null; yield return null;
        if (panelPositionSaved)
        {
            Require(Vector2.Distance(draggedPanel.anchoredPosition, expectedPanelPosition) < 0.5f, "Settings title-bar position was overwritten on a later frame.");
            testedPanelDrag = true; RestorePanelPosition();
        }
        foreach (string title in new[] { "动作管理", "模型导入" })
        {
            tabs.ShowPage(title); Canvas.ForceUpdateCanvases();
            yield return new WaitForSecondsRealtime(0.15f);
            if (title == "模型导入")
            {
                var picker = Control<TMP_Dropdown>(tabs.FindPageContent(title), "ModelLibrarySettings/ModelDropdown");
                var models = UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>();
                Require(picker.options.Count == models.ModelEntries.Count && picker.options.Count > 0,
                    "Model picker did not populate the current library.");
                Require(picker.captionText != null && !string.IsNullOrWhiteSpace(picker.captionText.text),
                    "Model picker caption is blank.");
            }
            CheckPageLayout(tabs.FindPageContent(title));
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, title == "动作管理" ? "actions.png" : "models.png"));
            yield return new WaitForEndOfFrame();
            var scroll = tabs.FindPageContent(title).GetComponentInParent<UnityEngine.UI.ScrollRect>();
            scroll.verticalNormalizedPosition = 0;
            yield return new WaitForSecondsRealtime(0.15f);
            var finalRow = scroll.content.GetChild(0).GetChild(scroll.content.GetChild(0).childCount - 1) as RectTransform;
            var finalBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, finalRow);
            Require(finalBounds.min.y >= scroll.viewport.rect.yMin - 2, "The final controls cannot be reached by scrolling: " + title);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, title == "动作管理" ? "actions-bottom.png" : "models-bottom.png"));
            yield return new WaitForEndOfFrame();
            scroll.verticalNormalizedPosition = 1;
        }
    }

    private static IEnumerator CheckRetiredActionCompatibility()
    {
        var legacy = new[] {
            new PetActionEntry { id = "hold_sword", label = "抱刀", builtIn = true },
            new PetActionEntry { id = "draw_slash", label = "拔刀斩", builtIn = true },
            new PetActionEntry { id = "custom_legacy", label = "历史组合", trigger = "wave",
                steps = new[] { new PetActionStep { action = "nod" }, new PetActionStep { action = "draw_slash" } } },
            new PetActionEntry { id = "custom_legacy_vmd", label = "历史导入", motion_file = new string('a', 64) + ".vmd",
                source_format = "vmd", duration = 1, trigger = "hold_sword" }
        };
        PlayerPrefs.SetString(PetActionLibrary.PrefsKey, JsonUtility.ToJson(new SavedActionFixture { entries = legacy }));
        PetActionLibrary.Reload();
        Require(!PetActionLibrary.BuiltInIds.Contains("hold_sword") && !PetActionLibrary.BuiltInIds.Contains("draw_slash"), "Retired built-ins remain registered.");
        foreach (var item in legacy)
        {
            Require(PetActionLibrary.Find(item.id) == null && !PetActionLibrary.IsEnabled(item.id), "Retired record remains exposed: " + item.id);
            Require(PetActionLibrary.ResolveCustom(item.id) == null, "Retired record can still resolve: " + item.id);
        }
        PetActionLibrary.SetEnabled("wave", false); // Exercise a normal save after legacy loading.
        var exported = PetActionLibrary.Export();
        foreach (var item in legacy)
            Require(exported.Any(record => record.id == item.id && JsonUtility.ToJson(record) == JsonUtility.ToJson(item)), "Legacy data was removed or altered: " + item.id);
        bool blocked = false;
        try { PetActionLibrary.Add("旧步骤不可新增", new[] { new PetActionStep { action = "draw_slash" } }, 1); }
        catch (ArgumentException) { blocked = true; }
        Require(blocked, "New composition accepted a retired action.");
        blocked = false;
        try { PetActionLibrary.Add("历史触发不可重复", new[] { new PetActionStep { action = "nod" } }, 1, "wave"); }
        catch (ArgumentException) { blocked = true; }
        Require(blocked, "A hidden legacy action's trigger was reused, invalidating export.");
        var bridge = UnityEngine.Object.FindAnyObjectByType<PetAnimatorBridge>();
        Require(!bridge.GetCallableActionIds().Any(id => legacy.Any(item => item.id == id)), "Runtime advertises retired actions.");
        var animator = character.PlaybackAnimator;
        character.PlayAction("idle");
        yield return new WaitForSecondsRealtime(0.35f);
        foreach (var item in legacy)
        {
            bridge.PreviewAction(item.id);
            character.PreviewAction(item.id);
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.HoldSword")
                && !animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.DrawSlash"), "Retired action played: " + item.id);
        }
    }

    private static T Control<T>(Transform group, string path) where T : Component
    {
        T component = group.Find(path)?.GetComponent<T>();
        Require(component != null, "Missing UI control: " + path); return component;
    }

    private static void CheckPageLayout(RectTransform content)
    {
        var scroll = content.GetComponentInParent<UnityEngine.UI.ScrollRect>();
        Require(scroll.vertical && scroll.content == content && scroll.viewport != null, "Settings page scroll references are invalid.");
        Require(content.rect.height > scroll.viewport.rect.height, "Long settings page did not grow its scroll content.");
        var group = content.GetChild(0) as RectTransform;
        float previousBottom = float.PositiveInfinity;
        foreach (RectTransform row in group)
        {
            Require(row.rect.width > 1 && row.rect.height > 1, "Settings row has no usable size: " + row.name);
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(group, row);
            Require(bounds.max.y <= previousBottom + 1, "Settings rows overlap: " + row.name);
            previousBottom = bounds.min.y;
            var label = row.GetComponent<TMP_Text>();
            if (label != null)
                Require(label.preferredHeight <= row.rect.height + 2, "Settings text is clipped vertically: " + row.name);
        }
    }

    private static void BeginPanelDragCheck(RectTransform panel)
    {
        var parent = panel.parent as RectTransform;
        if (parent == null || DesktopPetPanelDrag.WorkArea(parent).width - panel.rect.width * panel.localScale.x < 48) return;
        draggedPanel = panel; panelDrag = panel.GetComponent<DesktopPetPanelDrag>();
        Require(panelDrag != null, "Settings title bar is missing its drag controller.");
        savedPanelPosition = panel.anchoredPosition; savedCustomPosition = panelDrag.HasCustomPosition;
        panelPrefsKey = "desktop_pet.panel_position." + panel.name;
        hadPanelSet = PlayerPrefs.HasKey(panelPrefsKey + ".set"); savedPanelSet = PlayerPrefs.GetInt(panelPrefsKey + ".set");
        hadPanelX = PlayerPrefs.HasKey(panelPrefsKey + ".x"); savedPanelX = PlayerPrefs.GetFloat(panelPrefsKey + ".x");
        hadPanelY = PlayerPrefs.HasKey(panelPrefsKey + ".y"); savedPanelY = PlayerPrefs.GetFloat(panelPrefsKey + ".y");
        panelPositionSaved = true;
        var header = panel.Find("Header") as RectTransform;
        Require(header != null, "Settings title bar is missing.");
        Vector2 start = RectTransformUtility.WorldToScreenPoint(null, header.TransformPoint(header.rect.center));
        float shift = Mathf.Abs(savedPanelPosition.x) < 50 ? 24 : -Mathf.Sign(savedPanelPosition.x) * 24;
        Vector2 delta = RectTransformUtility.WorldToScreenPoint(null, parent.TransformPoint(new Vector3(shift, 0, 0)))
            - RectTransformUtility.WorldToScreenPoint(null, parent.TransformPoint(Vector3.zero));
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = start, pressPosition = start,
            pointerPressRaycast = new RaycastResult { gameObject = header.gameObject } };
        panelDrag.OnBeginDrag(pointer); pointer.position += delta; panelDrag.OnDrag(pointer); panelDrag.OnEndDrag(pointer);
        expectedPanelPosition = panel.anchoredPosition;
        Require(panelDrag.HasCustomPosition && Vector2.Distance(expectedPanelPosition, savedPanelPosition) > 5, "Title-bar drag did not move the settings card.");
    }

    private static void RestorePanelPosition()
    {
        if (!panelPositionSaved) return;
        panelPositionSaved = false;
        if (hadPanelSet) PlayerPrefs.SetInt(panelPrefsKey + ".set", savedPanelSet); else PlayerPrefs.DeleteKey(panelPrefsKey + ".set");
        if (hadPanelX) PlayerPrefs.SetFloat(panelPrefsKey + ".x", savedPanelX); else PlayerPrefs.DeleteKey(panelPrefsKey + ".x");
        if (hadPanelY) PlayerPrefs.SetFloat(panelPrefsKey + ".y", savedPanelY); else PlayerPrefs.DeleteKey(panelPrefsKey + ".y");
        PlayerPrefs.Save();
        if (panelDrag != null) typeof(DesktopPetPanelDrag).GetField("<HasCustomPosition>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panelDrag, savedCustomPosition);
        if (draggedPanel != null) draggedPanel.anchoredPosition = savedPanelPosition;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void OnPlayMode(PlayModeStateChange change) { if (running && change == PlayModeStateChange.ExitingPlayMode) Finish("Play mode ended before checks completed."); }
    private static void Finish(string error)
    {
        if (!running) return; running = false;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        RestorePanelPosition();
        if (hadPrefs) PlayerPrefs.SetString(PetActionLibrary.PrefsKey, savedPrefs); else PlayerPrefs.DeleteKey(PetActionLibrary.PrefsKey);
        PlayerPrefs.Save(); PetActionLibrary.Reload();
        if (character != null) { character.SetDragging(false, 0); character.StopWalking(); character.PlayAction("idle"); }
        settings?.Close();
        string result = error == null ? "PASSED: immutable built-ins, validation, custom persistence, selected-entry preview, automatic triggers, multi-step playback, speed, disabling current custom/walk/guard/drag/idle/bridge, actual UI create/preview/toggle/delete wiring, independent scrollable settings pages and text layout.\nSettings title-bar drag across frames: " + (testedPanelDrag ? "PASSED" : "SKIPPED: insufficient horizontal space.") : "FAILED: " + error;
        File.WriteAllText(Path.Combine(folder, "results.txt"), result);
        if (error == null) Debug.Log("DESKTOP_PET_ACTION_MANAGEMENT " + result); else Debug.LogError(result);
    }
}
