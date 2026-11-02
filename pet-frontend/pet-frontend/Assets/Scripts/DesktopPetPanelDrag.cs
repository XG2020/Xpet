using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Moves one panel by its title bar without moving the character/window.</summary>
public sealed class DesktopPetPanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private RectTransform panel, header, parent;
    private Vector2 startPointer, startPosition;
    private bool dragging;
    private TransparentWindowController desktop;
    private readonly Vector3[] corners = new Vector3[4];
    public bool HasCustomPosition { get; private set; }
    private static DesktopPetPanelDrag activeDrag;
    public static bool AnyDragging => activeDrag != null || DesktopPetSettingsWindow.AnyResizing;
    private string Key => "desktop_pet.panel_position." + name;

    public static DesktopPetPanelDrag Attach(RectTransform panel, RectTransform header)
    {
        if (panel == null || header == null) return null;
        var drag = panel.GetComponent<DesktopPetPanelDrag>() ?? panel.gameObject.AddComponent<DesktopPetPanelDrag>();
        drag.panel = panel; drag.header = header; drag.parent = panel.parent as RectTransform;
        drag.desktop = FindAnyObjectByType<TransparentWindowController>();
        var hit = header.GetComponent<UnityEngine.UI.Image>() ?? header.gameObject.AddComponent<UnityEngine.UI.Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        if (PlayerPrefs.GetInt(drag.Key + ".set", 0) == 1 && drag.parent != null) {
            drag.HasCustomPosition = true;
            panel.anchoredPosition = new Vector2(PlayerPrefs.GetFloat(drag.Key + ".x") * drag.parent.rect.width,
                PlayerPrefs.GetFloat(drag.Key + ".y") * drag.parent.rect.height);
            drag.ClampToParent();
        }
        return drag;
    }
    public void OnBeginDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || header == null || parent == null) return;
        if (GetComponent<DesktopPetSettingsWindow>()?.IsMaximized == true) return;
        if (!RectTransformUtility.RectangleContainsScreenPoint(header, data.pressPosition, data.pressEventCamera)) return;
        if (data.pointerPressRaycast.gameObject != null && data.pointerPressRaycast.gameObject.GetComponentInParent<Selectable>() != null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, data.position, data.pressEventCamera, out startPointer)) return;
        dragging = true; activeDrag = this; HasCustomPosition = true; startPosition = panel.anchoredPosition;
        panel.SetAsLastSibling();
    }
    public void OnDrag(PointerEventData data)
    {
        if (!dragging || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, data.position, data.pressEventCamera, out Vector2 point)) return;
        panel.anchoredPosition = startPosition + point - startPointer;
        ClampToParent();
    }
    public void OnEndDrag(PointerEventData data)
    {
        if (!dragging) return;
        dragging = false; if (activeDrag == this) activeDrag = null; SavePosition();
        GetComponent<DesktopPetSettingsWindow>()?.RememberNormalGeometry();
    }
    private void SavePosition()
    {
        if (panel == null || parent == null || !HasCustomPosition) return;
        PlayerPrefs.SetInt(Key + ".set", 1);
        PlayerPrefs.SetFloat(Key + ".x", panel.anchoredPosition.x / Mathf.Max(1, parent.rect.width));
        PlayerPrefs.SetFloat(Key + ".y", panel.anchoredPosition.y / Mathf.Max(1, parent.rect.height));
        PlayerPrefs.Save();
    }
    public void ClampToParent()
    {
        if (panel == null || parent == null) return;
        var window = GetComponent<DesktopPetSettingsWindow>();
        if (window != null) { window.ClampPosition(); return; }
        Rect area = WorkArea(parent, desktop);
        float fit = Mathf.Max(0.05f, Mathf.Min((area.width - 16) / Mathf.Max(1, panel.rect.width), (area.height - 16) / Mathf.Max(1, panel.rect.height)));
        if (panel.localScale.x > fit) panel.localScale = Vector3.one * fit;
        panel.GetWorldCorners(corners);
        Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
        foreach (Vector3 corner in corners) { Vector2 local = parent.InverseTransformPoint(corner); min = Vector2.Min(min, local); max = Vector2.Max(max, local); }
        Vector2 offset = Vector2.zero;
        if (min.x < area.xMin + 8) offset.x = area.xMin + 8 - min.x;
        else if (max.x > area.xMax - 8) offset.x = area.xMax - 8 - max.x;
        if (min.y < area.yMin + 8) offset.y = area.yMin + 8 - min.y;
        else if (max.y > area.yMax - 8) offset.y = area.yMax - 8 - max.y;
        panel.anchoredPosition += offset;
    }
    public static Rect WorkArea(RectTransform parent, TransparentWindowController desktop = null)
    {
        Rect area = parent.rect;
        if (desktop == null) desktop = FindAnyObjectByType<TransparentWindowController>();
        if (desktop != null && desktop.TryGetWorkAreaInRenderPixels(out Rect work)) {
            var canvas = parent.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, work.min, camera, out Vector2 lo) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, work.max, camera, out Vector2 hi))
                area = Rect.MinMaxRect(Mathf.Max(area.xMin, lo.x), Mathf.Max(area.yMin, lo.y), Mathf.Min(area.xMax, hi.x), Mathf.Min(area.yMax, hi.y));
        }
        return area;
    }
    private void LateUpdate() { ClampToParent(); }
    private void OnDisable() { if (dragging) { dragging = false; if (activeDrag == this) activeDrag = null; SavePosition(); } }
}
