using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>Settings pages retain their own scroll position and fit inside the desktop.</summary>
[DisallowMultipleComponent]
public sealed class SettingsTabsController : MonoBehaviour
{
    [SerializeField] private RectTransform settingsCard;
    [SerializeField] private string[] pageTitles = Array.Empty<string>();
    [SerializeField] private GameObject[] pages = Array.Empty<GameObject>();
    [SerializeField] private Button[] tabButtons = Array.Empty<Button>();
    [SerializeField] private Toggle lowPowerToggle;
    [SerializeField] private Button historyButton;
    [SerializeField] private Button memoriesButton;
    [SerializeField] private int selectedPage;
    private UnityAction[] tabActions;
    private DesktopPetSystemController systemController;
    private bool bound;
    private RectTransform rightPane;
    private Vector2 lastViewportSize;
    private static readonly Color Selected = new Color32(147, 232, 205, 255);
    private static readonly Color Normal = new Color32(36, 47, 63, 255);
    public RectTransform SidebarMenu => (transform.Find("Body/Navigation/Viewport/Tabs") ?? transform.Find("Body/Tabs")) as RectTransform;
    public int SelectedPage => selectedPage;

    public void Configure(RectTransform card, string[] titles, GameObject[] pageRoots, Button[] buttons,
        Toggle lowPower, Button history, Button memories)
    {
        Unbind();
        settingsCard = card;
        pageTitles = titles;
        pages = pageRoots;
        tabButtons = buttons;
        lowPowerToggle = lowPower;
        historyButton = history;
        memoriesButton = memories;
        selectedPage = Mathf.Clamp(selectedPage, 0, Mathf.Max(0, pages.Length - 1));
        ApplySidebarLayout();
        ShowPage(selectedPage);
        FitToViewport();
        if (Application.isPlaying) Bind();
    }

    private void Awake() { ApplySidebarLayout(); }
    private void Start() { Bind(); ShowPage(selectedPage); }
    private void OnEnable() { FitToViewport(); if (bound) ShowPage(selectedPage); }
    private void OnDestroy() { Unbind(); }

    private void Bind()
    {
        if (bound) return;
        bound = true;
        tabActions = new UnityAction[tabButtons.Length];
        for (int i = 0; i < tabButtons.Length; i++)
        {
            int index = i;
            tabActions[i] = () => ShowPage(index);
            if (tabButtons[i] != null) tabButtons[i].onClick.AddListener(tabActions[i]);
        }
        systemController = FindFirstObjectByType<DesktopPetSystemController>(FindObjectsInactive.Include);
        if (lowPowerToggle != null)
        {
            lowPowerToggle.SetIsOnWithoutNotify(systemController != null && systemController.LowPower);
            lowPowerToggle.onValueChanged.AddListener(SetLowPower);
        }
        if (historyButton != null) historyButton.onClick.AddListener(OpenHistory);
        if (memoriesButton != null) memoriesButton.onClick.AddListener(OpenMemories);
    }

    private void Unbind()
    {
        if (!bound) return;
        for (int i = 0; i < tabButtons.Length; i++)
            if (tabButtons[i] != null && tabActions != null && i < tabActions.Length)
                tabButtons[i].onClick.RemoveListener(tabActions[i]);
        if (lowPowerToggle != null) lowPowerToggle.onValueChanged.RemoveListener(SetLowPower);
        if (historyButton != null) historyButton.onClick.RemoveListener(OpenHistory);
        if (memoriesButton != null) memoriesButton.onClick.RemoveListener(OpenMemories);
        bound = false;
    }

    private void LateUpdate()
    {
        if (lowPowerToggle != null && systemController != null && lowPowerToggle.isOn != systemController.LowPower)
            lowPowerToggle.SetIsOnWithoutNotify(systemController.LowPower);
    }

    public void ShowPage(int index)
    {
        if (pages.Length == 0) return;
        selectedPage = Mathf.Clamp(index, 0, pages.Length - 1);
        bool ownsSave = selectedPage < pageTitles.Length && (pageTitles[selectedPage] == "角色卡" || pageTitles[selectedPage] == "后端日志" || pageTitles[selectedPage] == "关于");
        (rightPane != null ? rightPane.Find("Footer") : transform.Find("Footer"))?.gameObject.SetActive(!ownsSave);
        (rightPane != null ? rightPane.Find("Status") : transform.Find("Status"))?.gameObject.SetActive(!ownsSave);
        for (int i = 0; i < pages.Length; i++)
        {
            bool selected = i == selectedPage;
            if (pages[i] != null) pages[i].SetActive(selected);
            if (i >= tabButtons.Length || tabButtons[i] == null) continue;
            if (tabButtons[i].targetGraphic != null) tabButtons[i].targetGraphic.color = selected ? Selected : Normal;
            TMP_Text label = tabButtons[i].GetComponentInChildren<TMP_Text>();
            if (label != null) label.color = selected ? new Color32(17, 48, 41, 255) : Color.white;
        }
        if (pages[selectedPage] != null)
            LayoutRebuilder.MarkLayoutForRebuild(pages[selectedPage].GetComponent<RectTransform>());
    }

    public void ShowPage(string title)
    {
        int index = Array.IndexOf(pageTitles, title);
        if (index >= 0) ShowPage(index);
    }

    public RectTransform FindPageContent(string title)
    {
        int index = Array.IndexOf(pageTitles, title);
        return index >= 0 && index < pages.Length && pages[index] != null
            ? pages[index].transform.Find("Viewport/Content") as RectTransform : null;
    }

    public void AddPage(string title, GameObject pageRoot, UnityEngine.UI.Button button)
    {
        if (Array.IndexOf(pageTitles, title) >= 0) return;
        Unbind();
        Array.Resize(ref pageTitles, pageTitles.Length + 1); pageTitles[pageTitles.Length - 1] = title;
        Array.Resize(ref pages, pages.Length + 1); pages[pages.Length - 1] = pageRoot;
        Array.Resize(ref tabButtons, tabButtons.Length + 1); tabButtons[tabButtons.Length - 1] = button;
        ConfigureMenuButtonSizes();
        if (Application.isPlaying) Bind();
        ShowPage(selectedPage);
    }

    public void FitToViewport()
    {
        if (settingsCard == null) settingsCard = transform as RectTransform;
        var window = settingsCard != null ? settingsCard.GetComponent<DesktopPetSettingsWindow>() : null;
        if (window != null) { window.FitToViewport(); return; }
        var parent = settingsCard != null ? settingsCard.parent as RectTransform : null;
        if (parent == null || parent.rect.width < 1 || parent.rect.height < 1) return;
        if (lastViewportSize == parent.rect.size) return;
        lastViewportSize = parent.rect.size;
        float width = Mathf.Min(720, Mathf.Max(1, parent.rect.width - 32));
        float height = Mathf.Min(740, Mathf.Max(1, parent.rect.height - 32));
        // Preserve usable controls in unusually small viewports by scaling the complete card.
        settingsCard.sizeDelta = new Vector2(Mathf.Max(360, width), Mathf.Max(360, height));
        settingsCard.localScale = Vector3.one * Mathf.Min(1, width / settingsCard.sizeDelta.x, height / settingsCard.sizeDelta.y);
        settingsCard.anchorMin = settingsCard.anchorMax = settingsCard.pivot = new Vector2(0.5f, 0.5f);
        // Placement belongs to DesktopPetAppearance / DesktopPetPanelDrag. Re-centering
        // here every frame would discard the user's title-bar drag and saved position.
    }

    /// <summary>Reuses the existing tabs, pages and bound input fields.</summary>
    public void ApplySidebarLayout()
    {
        if (settingsCard == null) settingsCard = transform as RectTransform;
        if (settingsCard == null) return;
        var tabs = settingsCard.Find("Tabs") as RectTransform;
        var pageHost = settingsCard.Find("Pages") as RectTransform;
        var body = settingsCard.Find("Body") as RectTransform;
        if (body != null) {
            rightPane = body.Find("Configuration") as RectTransform;
            ConfigureMenuButtonSizes();
            return;
        }
        if (tabs == null || pageHost == null) return;
        body = NewRect("Body", settingsCard);
        body.SetSiblingIndex(2);
        var bodySize = body.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        bodySize.minHeight = 40; bodySize.flexibleHeight = 1;
        var row = body.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        row.spacing = 16; row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = false; row.childForceExpandHeight = true;
        var navigation = NewRect("Navigation", body);
        var menuSize = navigation.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        menuSize.minWidth = menuSize.preferredWidth = 126; menuSize.flexibleWidth = 0;
        menuSize.minHeight = 0; menuSize.preferredHeight = -1; menuSize.flexibleHeight = 1;
        var viewport = NewRect("Viewport", navigation);
        viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
        viewport.offsetMin = viewport.offsetMax = Vector2.zero;
        viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        var viewportSurface = viewport.gameObject.AddComponent<UnityEngine.UI.Image>();
        viewportSurface.color = Color.clear;
        tabs.SetParent(viewport, false);
        tabs.anchorMin = new Vector2(0, 1); tabs.anchorMax = Vector2.one; tabs.pivot = new Vector2(0.5f, 1);
        tabs.anchoredPosition = Vector2.zero; tabs.sizeDelta = Vector2.zero;
        var previousLayout = tabs.GetComponent<UnityEngine.UI.LayoutGroup>();
        if (previousLayout != null && !(previousLayout is UnityEngine.UI.GridLayoutGroup)) DestroyImmediate(previousLayout);
        var menu = tabs.GetComponent<UnityEngine.UI.GridLayoutGroup>() ?? tabs.gameObject.AddComponent<UnityEngine.UI.GridLayoutGroup>();
        menu.enabled = true; menu.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount;
        menu.constraintCount = 1; menu.cellSize = new Vector2(126, 38); menu.spacing = new Vector2(0, 6);
        var fitter = tabs.GetComponent<UnityEngine.UI.ContentSizeFitter>() ?? tabs.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        var scroll = navigation.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
        scroll.content = tabs; scroll.viewport = viewport; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24;
        ConfigureMenuButtonSizes();
        if (tabButtons.Length > 7 && tabButtons[7] != null) tabButtons[7].transform.SetAsFirstSibling();
        rightPane = NewRect("Configuration", body);
        var column = rightPane.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        column.spacing = 8; column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true; column.childForceExpandHeight = false;
        var rightSize = rightPane.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        rightSize.minWidth = 240; rightSize.flexibleWidth = 1; rightSize.flexibleHeight = 1;
        pageHost.SetParent(rightPane, false);
        settingsCard.Find("Status")?.SetParent(rightPane, false);
        settingsCard.Find("Footer")?.SetParent(rightPane, false);
    }

    private void ConfigureMenuButtonSizes()
    {
        foreach (var button in tabButtons) {
            if (button == null) continue;
            var size = button.GetComponent<UnityEngine.UI.LayoutElement>() ?? button.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            size.minWidth = 0; size.preferredWidth = -1; size.flexibleWidth = 1;
            size.minHeight = size.preferredHeight = 38; size.flexibleHeight = 0;
        }
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.localScale = Vector3.one;
        return rect;
    }

    private void SetLowPower(bool value)
    {
        if (systemController == null) systemController = FindFirstObjectByType<DesktopPetSystemController>(FindObjectsInactive.Include);
        if (systemController != null) systemController.SetLowPower(value);
    }
    public void OpenHistory() => FindFirstObjectByType<PetDataPanel>(FindObjectsInactive.Include)?.OpenHistory();
    public void OpenMemories() => FindFirstObjectByType<PetDataPanel>(FindObjectsInactive.Include)?.OpenMemories();
}
