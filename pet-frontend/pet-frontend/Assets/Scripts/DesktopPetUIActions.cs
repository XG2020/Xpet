using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>On-demand companion controls. The desktop starts with only the character visible.</summary>
public class DesktopPetUIActions : MonoBehaviour
{
    [SerializeField] private GameObject chatRoot;
    [SerializeField] private TMP_Text microphoneLabel;
    [SerializeField] private RectTransform contextMenuRoot;
    [SerializeField] private SettingsPanelController settingsController;
    private DesktopPetInteraction interaction;
    private DesktopPetAppearance appearance;
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    private void Start()
    {
        interaction = FindAnyObjectByType<DesktopPetInteraction>();
        appearance = FindAnyObjectByType<DesktopPetAppearance>();
        if (chatRoot != null) chatRoot.SetActive(false);
        var canvas = GameObject.Find("DesktopPetCanvas")?.transform;
        if (canvas != null) {
            var chat = canvas.Find("ChatPanel") as RectTransform;
            var settings = canvas.Find("SettingsOverlay/SettingsCard") as RectTransform;
            settings?.GetComponent<SettingsTabsController>()?.ApplySidebarLayout();
            if (settings != null)
                foreach (var page in settings.GetComponentsInChildren<PetCharacterCardsPanel>(true)) page.PrepareLayout();
            DesktopPetPanelDrag.Attach(chat, chat?.Find("Header") as RectTransform);
            DesktopPetPanelDrag.Attach(settings, settings?.Find("Header") as RectTransform);
            DesktopPetSettingsWindow.Attach(settings, settings?.Find("Header") as RectTransform);
            var label = canvas.Find("ContextMenuRoot/Settings")?.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = "设置";
        }
        CloseContextMenu();
    }

    private void Update()
    {
        if (interaction != null && microphoneLabel != null)
        {
            string value = interaction.IsRecording ? "停止录音" : "语音输入";
            if (microphoneLabel.text != value) microphoneLabel.text = value;
        }
        Vector2 pointer = Vector2.zero;
        bool rightPressed = false, leftPressed = false, escapePressed = false;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            pointer = Mouse.current.position.ReadValue();
            rightPressed = Mouse.current.rightButton.wasPressedThisFrame;
            leftPressed = Mouse.current.leftButton.wasPressedThisFrame;
        }
        escapePressed = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        pointer = Input.mousePosition;
        rightPressed = Input.GetMouseButtonDown(1);
        leftPressed = Input.GetMouseButtonDown(0);
        escapePressed = Input.GetKeyDown(KeyCode.Escape);
#endif
        if (rightPressed)
        {
            Camera camera = Camera.main;
            if (!IsOverUI(pointer) && camera != null &&
                Physics.Raycast(camera.ScreenPointToRay(pointer), out RaycastHit hit, 100f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide) &&
                hit.collider.GetComponentInParent<PetCharacterAnimation>() != null)
                OpenContextMenu(pointer);
            else CloseContextMenu();
        }
        if (contextMenuRoot != null && contextMenuRoot.gameObject.activeSelf &&
            (escapePressed || (leftPressed && !RectTransformUtility.RectangleContainsScreenPoint(
                contextMenuRoot, pointer, MenuCamera()))))
            CloseContextMenu();
    }

    private bool IsOverUI(Vector2 pointer)
    {
        if (EventSystem.current == null) return false;
        uiHits.Clear();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = pointer }, uiHits);
        return uiHits.Count > 0;
    }

    private Camera MenuCamera()
    {
        Canvas canvas = contextMenuRoot != null ? contextMenuRoot.GetComponentInParent<Canvas>() : null;
        return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    public void OpenContextMenu(Vector2 screenPosition)
    {
        if (contextMenuRoot == null) return;
        var parent = contextMenuRoot.parent as RectTransform;
        if (parent == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, screenPosition, MenuCamera(), out Vector2 point)) return;
        contextMenuRoot.Find("Quit")?.SetAsLastSibling();
        contextMenuRoot.gameObject.SetActive(true);
        contextMenuRoot.SetAsLastSibling();
        Canvas.ForceUpdateCanvases();
        Rect bounds = parent.rect;
        var desktop = GetComponent<TransparentWindowController>();
        if (desktop != null && desktop.TryGetWorkAreaInRenderPixels(out Rect work)) {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, work.min, MenuCamera(), out Vector2 low) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, work.max, MenuCamera(), out Vector2 high))
                bounds = Rect.MinMaxRect(Mathf.Max(bounds.xMin, low.x), Mathf.Max(bounds.yMin, low.y),
                    Mathf.Min(bounds.xMax, high.x), Mathf.Min(bounds.yMax, high.y));
        }
        Vector2 size = contextMenuRoot.rect.size;
        contextMenuRoot.anchoredPosition = new Vector2(
            Mathf.Clamp(point.x + 12, bounds.xMin + 8, bounds.xMax - size.x - 8),
            Mathf.Clamp(point.y - 8, bounds.yMin + size.y + 8, bounds.yMax - 8));
    }

    public void CloseContextMenu()
    {
        if (contextMenuRoot != null) contextMenuRoot.gameObject.SetActive(false);
    }
    public void DismissContextForOutsidePress(Vector2 screenPosition)
    {
        if (contextMenuRoot != null && contextMenuRoot.gameObject.activeSelf &&
            !RectTransformUtility.RectangleContainsScreenPoint(contextMenuRoot, screenPosition, MenuCamera())) CloseContextMenu();
    }

    public void OpenChat()
    {
        GetComponent<PetBehaviorCoordinator>()?.NoteActivity();
        CloseContextMenu();
        if (chatRoot == null) return;
        chatRoot.SetActive(true);
        chatRoot.transform.SetAsLastSibling();
        if (appearance != null) appearance.UpdateChatPlacement();
    }

    public void ToggleChat()
    {
        CloseContextMenu();
        if (chatRoot == null) return;
        bool open = !chatRoot.activeSelf;
        chatRoot.SetActive(open);
        if (open)
        {
            chatRoot.transform.SetAsLastSibling();
            if (appearance != null) appearance.UpdateChatPlacement();
        }
    }

    public void OpenSettings()
    {
        GetComponent<PetBehaviorCoordinator>()?.NoteActivity();
        CloseContextMenu();
        if (settingsController != null) settingsController.Open();
    }

    public void ToggleVoiceRecording()
    {
        CloseContextMenu();
        OpenChat();
        if (interaction == null) interaction = FindAnyObjectByType<DesktopPetInteraction>();
        if (interaction != null) interaction.ToggleVoiceRecording();
    }

    public void Quit()
    {
        CloseContextMenu();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
