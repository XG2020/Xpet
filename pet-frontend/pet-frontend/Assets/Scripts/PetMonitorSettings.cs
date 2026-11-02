using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Adds the local display preference to existing settings scenes.</summary>
[DisallowMultipleComponent]
public sealed class PetMonitorSettings : MonoBehaviour
{
    private TransparentWindowController window;
    private RectTransform controls;
    private TMP_Dropdown dropdown;
    private TMP_Text hint;
    private readonly List<string> monitorIds = new List<string>();
    private float retryAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= SceneLoaded;
        SceneManager.sceneLoaded += SceneLoaded;
        InstallScene();
    }
    private static void SceneLoaded(Scene scene, LoadSceneMode mode) { InstallScene(); }
    private static void InstallScene()
    {
        var controller = FindFirstObjectByType<TransparentWindowController>(FindObjectsInactive.Include);
        if (controller != null && controller.GetComponent<PetMonitorSettings>() == null)
            controller.gameObject.AddComponent<PetMonitorSettings>();
    }

    private void OnEnable()
    {
        window = GetComponent<TransparentWindowController>();
        if (window != null) window.MonitorPlacementChanged += Refresh;
        if (dropdown != null) { dropdown.onValueChanged.AddListener(SelectMonitor); Refresh(); }
    }
    private void OnDisable()
    {
        if (window != null) window.MonitorPlacementChanged -= Refresh;
        if (dropdown != null) dropdown.onValueChanged.RemoveListener(SelectMonitor);
    }
    private void Start() { TryBuild(); }
    private void Update()
    {
        if (controls != null || Time.unscaledTime < retryAt) return;
        retryAt = Time.unscaledTime + 1f;
        TryBuild();
    }

    private void TryBuild()
    {
        var tabs = FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        RectTransform parent = tabs != null ? tabs.FindPageContent("外观") : null;
        if (parent == null) return;
        controls = parent.Find("MonitorSettings") as RectTransform;
        if (controls != null)
        {
            dropdown = controls.Find("ResidentMonitorDropdown")?.GetComponent<TMP_Dropdown>();
            hint = controls.Find("Hint")?.GetComponent<TMP_Text>();
            if (dropdown != null) { dropdown.onValueChanged.AddListener(SelectMonitor); Refresh(); }
            return;
        }
        TMP_Dropdown template = null;
        foreach (TMP_Dropdown candidate in tabs.GetComponentsInChildren<TMP_Dropdown>(true))
            if (candidate.template != null && !candidate.IsExpanded) { template = candidate; break; }
        if (template == null) return;
        TMP_FontAsset font = parent.GetComponentInChildren<TMP_Text>(true)?.font;
        var root = new GameObject("MonitorSettings", typeof(RectTransform), typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.VerticalLayoutGroup), typeof(UnityEngine.UI.LayoutElement));
        controls = (RectTransform)root.transform;
        controls.SetParent(parent, false);
        var surface = root.GetComponent<UnityEngine.UI.Image>();
        surface.color = new Color32(24, 33, 48, 255);
        surface.raycastTarget = false;
        var column = root.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        column.spacing = 8;
        column.padding = new RectOffset(12, 12, 12, 12);
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        var size = root.GetComponent<UnityEngine.UI.LayoutElement>();
        size.minHeight = size.preferredHeight = 160;
        Label("Title", controls, "驻留显示器", 18, 25, font);
        dropdown = Instantiate(template, controls, false);
        dropdown.name = "ResidentMonitorDropdown";
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        var dropdownSize = dropdown.GetComponent<UnityEngine.UI.LayoutElement>()
            ?? dropdown.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        dropdownSize.ignoreLayout = false;
        dropdownSize.minWidth = 0; dropdownSize.preferredWidth = -1; dropdownSize.flexibleWidth = 1;
        dropdownSize.minHeight = dropdownSize.preferredHeight = 40; dropdownSize.flexibleHeight = 0;
        dropdown.transform.localScale = Vector3.one;
        dropdown.transform.localRotation = Quaternion.identity;
        dropdown.gameObject.SetActive(true);
        hint = Label("Hint", controls, "", 13, 48, font);
        dropdown.onValueChanged.AddListener(SelectMonitor);
        Refresh();
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(parent);
    }

    private static TMP_Text Label(string name, Transform parent, string text, int fontSize, int height, TMP_FontAsset font)
    {
        var item = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(UnityEngine.UI.LayoutElement));
        item.transform.SetParent(parent, false);
        var label = item.GetComponent<TextMeshProUGUI>();
        label.font = font; label.text = text; label.fontSize = fontSize;
        label.color = Color.white; label.raycastTarget = false; label.richText = false;
        label.textWrappingMode = TextWrappingModes.Normal;
        var size = item.GetComponent<UnityEngine.UI.LayoutElement>();
        size.minHeight = size.preferredHeight = height;
        return label;
    }

    private void Refresh()
    {
        if (dropdown == null) return;
        if (dropdown.IsExpanded) dropdown.Hide();
        monitorIds.Clear();
        var labels = new List<string>();
        if (window != null)
            foreach (TransparentWindowController.ResidentMonitor monitor in window.ResidentMonitorOptions)
            { monitorIds.Add(monitor.Id); labels.Add(monitor.Label); }
        if (labels.Count == 0) labels.Add("Windows 桌面模式中可选择");
        dropdown.ClearOptions(); dropdown.AddOptions(labels);
        int selected = window != null ? monitorIds.IndexOf(window.ResidentMonitorId) : -1;
        dropdown.SetValueWithoutNotify(Mathf.Max(0, selected));
        dropdown.RefreshShownValue();
        dropdown.interactable = window != null && window.SupportsMonitorPlacement && monitorIds.Count > 0;
        if (hint != null) hint.text = dropdown.interactable
            ? "拖拽或牵手后自动记住位置，下次启动恢复。显示器断开时会回到主屏。选择立即生效并保存。"
            : "Windows 桌面模式会记住宠物位置；连接多块显示器后可在这里选择驻留屏幕。";
    }

    private void SelectMonitor(int index)
    {
        if (window == null || index < 0 || index >= monitorIds.Count) return;
        if (!window.SetResidentMonitor(monitorIds[index]) && hint != null)
            hint.text = "这块显示器暂时不可用，请选择当前已连接的显示器。";
    }
}
