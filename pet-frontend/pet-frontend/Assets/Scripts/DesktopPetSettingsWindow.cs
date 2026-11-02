using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Persistent settings geometry, maximize/restore and eight resize handles.</summary>
[DisallowMultipleComponent]
public sealed class DesktopPetSettingsWindow : MonoBehaviour
{
    public const string PrefsKey = "desktop_pet.settings_window";
    public static bool AnyResizing { get; private set; }
    public bool IsMaximized { get; private set; }
    public Vector2 MinimumSize => new Vector2(540, 420);
    private RectTransform card, parent;
    private TransparentWindowController desktop;
    private Vector2 normalSize, normalCenter;
    private bool initialized, applying, resizing;
    private int readyFrame;
    private TMP_Text maximizeLabel;
    private Rect dragStartRect;
    private Vector2 dragStartPointer;
    private Vector2Int dragEdge;

    public static DesktopPetSettingsWindow Attach(RectTransform card, RectTransform header)
    {
        if (card == null || header == null) return null;
        var controller = card.GetComponent<DesktopPetSettingsWindow>() ?? card.gameObject.AddComponent<DesktopPetSettingsWindow>();
        if (!controller.initialized) controller.Initialize(card, header);
        return controller;
    }

    private void Initialize(RectTransform panel, RectTransform header)
    {
        card = panel; parent = card.parent as RectTransform;
        desktop = FindAnyObjectByType<TransparentWindowController>();
        normalSize = new Vector2(Mathf.Clamp(PlayerPrefs.GetFloat(PrefsKey + ".width", 720), 540, 7680),
            Mathf.Clamp(PlayerPrefs.GetFloat(PrefsKey + ".height", 740), 420, 4320));
        normalCenter = new Vector2(Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsKey + ".center_x", 0.5f)),
            Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsKey + ".center_y", 0.5f)));
        IsMaximized = PlayerPrefs.GetInt(PrefsKey + ".maximized", 0) == 1;
        CreateMaximizeButton(header);
        var title = header.GetComponent<DesktopPetSettingsTitleBar>() ?? header.gameObject.AddComponent<DesktopPetSettingsTitleBar>();
        title.Owner = this;
        CreateHandle("ResizeLeft", new Vector2(0, 0), new Vector2(0, 1), new Vector2(10, 0), new Vector2(0.5f, 0.5f), new Vector2Int(-1, 0));
        CreateHandle("ResizeRight", new Vector2(1, 0), new Vector2(1, 1), new Vector2(10, 0), new Vector2(0.5f, 0.5f), new Vector2Int(1, 0));
        CreateHandle("ResizeTop", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 10), new Vector2(0.5f, 0.5f), new Vector2Int(0, 1));
        CreateHandle("ResizeBottom", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 10), new Vector2(0.5f, 0.5f), new Vector2Int(0, -1));
        foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
            CreateHandle("ResizeCorner" + x + y, new Vector2(x > 0 ? 1 : 0, y > 0 ? 1 : 0),
                new Vector2(x > 0 ? 1 : 0, y > 0 ? 1 : 0), new Vector2(20, 20), new Vector2(0.5f, 0.5f), new Vector2Int(x, y));
        initialized = true;
        if (isActiveAndEnabled) OnEnable();
    }

    private void CreateMaximizeButton(RectTransform header)
    {
        var go = new GameObject("MaximizeSettings", typeof(RectTransform), typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button), typeof(UnityEngine.UI.LayoutElement));
        go.transform.SetParent(header, false);
        var layout = go.GetComponent<UnityEngine.UI.LayoutElement>();
        layout.minWidth = layout.preferredWidth = 82; layout.minHeight = layout.preferredHeight = 36;
        var surface = go.GetComponent<UnityEngine.UI.Image>(); surface.color = new Color32(36, 47, 63, 255);
        var button = go.GetComponent<UnityEngine.UI.Button>(); button.targetGraphic = surface;
        button.onClick.AddListener(ToggleMaximized);
        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(go.transform, false);
        maximizeLabel = textObject.GetComponent<TextMeshProUGUI>();
        var template = header.GetComponentInChildren<TMP_Text>();
        if (template != null) maximizeLabel.font = template.font;
        maximizeLabel.fontSize = 15; maximizeLabel.alignment = TextAlignmentOptions.Center;
        maximizeLabel.color = Color.white; maximizeLabel.raycastTarget = false;
        maximizeLabel.rectTransform.anchorMin = Vector2.zero; maximizeLabel.rectTransform.anchorMax = Vector2.one;
        maximizeLabel.rectTransform.offsetMin = maximizeLabel.rectTransform.offsetMax = Vector2.zero;
        var close = header.Find("Close");
        if (close != null) go.transform.SetSiblingIndex(close.GetSiblingIndex());
        UpdateLabel();
    }

    private void CreateHandle(string handleName, Vector2 min, Vector2 max, Vector2 size, Vector2 pivot, Vector2Int edge)
    {
        var go = new GameObject(handleName, typeof(RectTransform), typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.LayoutElement), typeof(DesktopPetSettingsResizeHandle));
        var rect = go.GetComponent<RectTransform>(); rect.SetParent(card, false);
        rect.anchorMin = min; rect.anchorMax = max; rect.pivot = pivot; rect.sizeDelta = size;
        go.GetComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
        var surface = go.GetComponent<UnityEngine.UI.Image>(); surface.color = Color.clear; surface.raycastTarget = true;
        var handle = go.GetComponent<DesktopPetSettingsResizeHandle>(); handle.Owner = this; handle.Edge = edge;
    }

    private void OnEnable()
    {
        if (!initialized) return;
        if (desktop == null) desktop = FindAnyObjectByType<TransparentWindowController>();
        // Settings share the existing render surface. Resizing the native
        // transparent window here rebuilds the swapchain and flashes the pet.
        desktop?.BeginSettingsWorkspace(card.GetComponentInParent<Canvas>());
        readyFrame = Time.frameCount + 1;
        ApplyGeometry(true);
    }

    private void OnDisable()
    {
        if (!initialized) return;
        EndResize();
        if (Time.frameCount >= readyFrame) RememberNormalGeometry();
        desktop?.EndSettingsWorkspace();
    }

    private void LateUpdate()
    {
        if (!initialized) return;
        if (Time.frameCount <= readyFrame) return;
        FitToViewport();
    }

    public void FitToViewport()
    {
        if (!initialized || applying || !isActiveAndEnabled || parent == null) return;
        ApplyGeometry(false);
    }

    private Rect Area()
    {
        Rect area = parent != null ? DesktopPetPanelDrag.WorkArea(parent, desktop) : new Rect(-360, -370, 720, 740);
        return new Rect(area.x + 8, area.y + 8, Mathf.Max(1, area.width - 16), Mathf.Max(1, area.height - 16));
    }

    private void ApplyGeometry(bool position)
    {
        if (parent == null || applying) return;
        applying = true;
        Rect area = Area();
        card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
        float scale = Mathf.Min(1, area.width / MinimumSize.x, area.height / MinimumSize.y);
        scale = Mathf.Max(0.001f, scale);
        card.localScale = Vector3.one * scale;
        Vector2 size = IsMaximized ? area.size / scale : new Vector2(
            Mathf.Min(normalSize.x, area.width / scale), Mathf.Min(normalSize.y, area.height / scale));
        card.sizeDelta = size;
        if (IsMaximized) card.anchoredPosition = area.center;
        else if (position) card.anchoredPosition = area.min + Vector2.Scale(normalCenter, area.size);
        ClampPosition();
        UpdateLabel();
        applying = false;
    }

    public void ClampPosition()
    {
        if (!initialized || parent == null) return;
        Rect area = Area();
        Vector2 half = Vector2.Scale(card.rect.size, card.localScale) * 0.5f;
        Vector2 position = card.anchoredPosition;
        position.x = Mathf.Clamp(position.x, area.xMin + Mathf.Min(half.x, area.width * 0.5f), area.xMax - Mathf.Min(half.x, area.width * 0.5f));
        position.y = Mathf.Clamp(position.y, area.yMin + Mathf.Min(half.y, area.height * 0.5f), area.yMax - Mathf.Min(half.y, area.height * 0.5f));
        card.anchoredPosition = position;
    }

    public void ToggleMaximized()
    {
        if (!initialized) return;
        EndResize();
        if (!IsMaximized) RememberNormalGeometry();
        IsMaximized = !IsMaximized;
        ApplyGeometry(true);
        Save();
    }

    public void RememberNormalGeometry()
    {
        if (!initialized) return;
        if (!IsMaximized) {
            Rect area = Area();
            normalCenter = new Vector2((card.anchoredPosition.x - area.xMin) / area.width,
                (card.anchoredPosition.y - area.yMin) / area.height);
        }
        Save();
    }

    private void Save()
    {
        PlayerPrefs.SetFloat(PrefsKey + ".width", normalSize.x); PlayerPrefs.SetFloat(PrefsKey + ".height", normalSize.y);
        PlayerPrefs.SetFloat(PrefsKey + ".center_x", normalCenter.x); PlayerPrefs.SetFloat(PrefsKey + ".center_y", normalCenter.y);
        PlayerPrefs.SetInt(PrefsKey + ".maximized", IsMaximized ? 1 : 0); PlayerPrefs.Save();
    }

    private void UpdateLabel() { if (maximizeLabel != null) maximizeLabel.text = IsMaximized ? "还原" : "最大化"; }

    public void BeginResize(PointerEventData data, Vector2Int edge)
    {
        if (!initialized || IsMaximized || data.button != PointerEventData.InputButton.Left || Time.frameCount < readyFrame) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, data.position, data.pressEventCamera, out dragStartPointer)) return;
        Vector2 size = Vector2.Scale(card.rect.size, card.localScale);
        dragStartRect = new Rect(card.anchoredPosition - size * 0.5f, size); dragEdge = edge;
        resizing = AnyResizing = true; card.SetAsLastSibling();
    }

    public void Resize(PointerEventData data)
    {
        if (!resizing || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, data.position, data.pressEventCamera, out Vector2 point)) return;
        Rect next = ResizeBounds(dragStartRect, point - dragStartPointer, dragEdge, Area(), MinimumSize * card.localScale.x);
        normalSize = next.size / card.localScale.x;
        card.sizeDelta = normalSize; card.anchoredPosition = next.center;
    }

    public void EndResize()
    {
        if (!resizing) return;
        resizing = AnyResizing = false; RememberNormalGeometry();
    }

    public static Rect ResizeBounds(Rect initial, Vector2 delta, Vector2Int edge, Rect area, Vector2 minimum)
    {
        minimum = Vector2.Min(minimum, area.size);
        float left = initial.xMin, right = initial.xMax, bottom = initial.yMin, top = initial.yMax;
        if (edge.x < 0) left = Mathf.Clamp(left + delta.x, area.xMin, right - minimum.x);
        if (edge.x > 0) right = Mathf.Clamp(right + delta.x, left + minimum.x, area.xMax);
        if (edge.y < 0) bottom = Mathf.Clamp(bottom + delta.y, area.yMin, top - minimum.y);
        if (edge.y > 0) top = Mathf.Clamp(top + delta.y, bottom + minimum.y, area.yMax);
        return Rect.MinMaxRect(left, bottom, right, top);
    }
}

public sealed class DesktopPetSettingsResizeHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public DesktopPetSettingsWindow Owner;
    public Vector2Int Edge;
    public void OnBeginDrag(PointerEventData data) => Owner?.BeginResize(data, Edge);
    public void OnDrag(PointerEventData data) => Owner?.Resize(data);
    public void OnEndDrag(PointerEventData data) => Owner?.EndResize();
    private void OnDisable() => Owner?.EndResize();
}

public sealed class DesktopPetSettingsTitleBar : MonoBehaviour, IPointerClickHandler
{
    public DesktopPetSettingsWindow Owner;
    public void OnPointerClick(PointerEventData data)
    {
        if (data.button == PointerEventData.InputButton.Left && data.clickCount == 2 &&
            (data.pointerPressRaycast.gameObject == null || data.pointerPressRaycast.gameObject.GetComponentInParent<UnityEngine.UI.Selectable>() == null))
            Owner?.ToggleMaximized();
    }
}
