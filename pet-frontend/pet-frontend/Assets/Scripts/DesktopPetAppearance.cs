using TMPro;
using UnityEngine;

/// <summary>Local appearance preferences and chat placement beside the visible character.</summary>
public class DesktopPetAppearance : MonoBehaviour
{
    private const string SizeKey = "desktop_pet.model_size";
    private const string ColorKey = "desktop_pet.panel_color";
    [SerializeField] private Transform characterRoot;
    [SerializeField] private Camera worldCamera;
    [SerializeField] private RectTransform chatPanel;
    [SerializeField] private RectTransform settingsCard;
    [SerializeField] private RectTransform contextMenuRoot;
    [SerializeField] private UnityEngine.UI.Slider modelSizeSlider;
    [SerializeField] private TMP_InputField backgroundColorInput;
    [SerializeField] private TMP_Text appearanceStatus;
    private Renderer[] characterRenderers;
    private Transform cachedCharacterRoot;
    private Bounds stableLocalBounds;
    private Bounds desktopLocalBounds;
    private bool hasStableBounds;
    private int rendererSignature;
    private float nextModelCheck;
    private Color panelColor = new Color32(24, 33, 48, 255);

    private void Awake()
    {
        if (worldCamera == null) worldCamera = Camera.main;
        RefreshPlacementBounds();
    }

    private void Start()
    {
        if (worldCamera == null) worldCamera = Camera.main;
        if (!hasStableBounds) RefreshPlacementBounds();
        float size = Mathf.Clamp(PlayerPrefs.GetFloat(SizeKey, 0.65f), 0.35f, 1.05f);
        if (characterRoot != null) characterRoot.localScale = Vector3.one * size;
        if (ColorUtility.TryParseHtmlString(PlayerPrefs.GetString(ColorKey, "#182130"), out Color saved)) panelColor = saved;
        if (modelSizeSlider != null)
        {
            modelSizeSlider.SetValueWithoutNotify(size);
            modelSizeSlider.onValueChanged.AddListener(SetModelSize);
        }
        if (backgroundColorInput != null)
        {
            backgroundColorInput.SetTextWithoutNotify("#" + ColorUtility.ToHtmlStringRGB(panelColor));
            backgroundColorInput.onEndEdit.AddListener(SetBackgroundColor);
        }
        ApplyPanelAppearance();
    }

    private void OnDestroy()
    {
        if (modelSizeSlider != null) modelSizeSlider.onValueChanged.RemoveListener(SetModelSize);
        if (backgroundColorInput != null) backgroundColorInput.onEndEdit.RemoveListener(SetBackgroundColor);
    }

    private void LateUpdate()
    {
        ClampSettingsToCanvas();
        UpdateChatPlacement();
    }

    public void ClampSettingsToCanvas()
    {
        if (settingsCard == null || !settingsCard.gameObject.activeInHierarchy) return;
        var window = settingsCard.GetComponent<DesktopPetSettingsWindow>();
        if (window != null) { window.FitToViewport(); return; }
        var parent = settingsCard.parent as RectTransform;
        if (parent == null) return;
        const float margin = 16;
        Rect bounds = DesktopPetPanelDrag.WorkArea(parent, GetComponent<TransparentWindowController>());
        float scale = Mathf.Min(1f, (bounds.width - margin * 2) / settingsCard.rect.width,
            (bounds.height - margin * 2) / settingsCard.rect.height);
        scale = Mathf.Max(0.05f, scale);
        settingsCard.localScale = Vector3.one * scale;
        settingsCard.anchorMin = settingsCard.anchorMax = new Vector2(0.5f, 0.5f);
        settingsCard.pivot = new Vector2(0.5f, 0.5f);
        float halfWidth = settingsCard.rect.width * scale * 0.5f;
        float halfHeight = settingsCard.rect.height * scale * 0.5f;
        Vector2 position = settingsCard.anchoredPosition;
        position.x = Mathf.Clamp(position.x, bounds.xMin + halfWidth + margin, bounds.xMax - halfWidth - margin);
        position.y = Mathf.Clamp(position.y, bounds.yMin + halfHeight + margin, bounds.yMax - halfHeight - margin);
        settingsCard.anchoredPosition = position;
    }

    public void SetModelSize(float value)
    {
        value = Mathf.Clamp(value, 0.35f, 1.05f);
        if (characterRoot != null) characterRoot.localScale = Vector3.one * value;
        PlayerPrefs.SetFloat(SizeKey, value); PlayerPrefs.Save();
        SetStatus("宠物大小 " + Mathf.RoundToInt(value / 0.65f * 100) + "% · 已自动保存");
        UpdateChatPlacement();
    }

    public void SetBackgroundColor(string value)
    {
        string normalized = (value ?? "").Trim();
        if (!normalized.StartsWith("#")) normalized = "#" + normalized;
        if (normalized.Length != 7 || !ColorUtility.TryParseHtmlString(normalized, out Color color))
        {
            SetStatus("请输入六位颜色，例如 #182130。");
            return;
        }
        panelColor = color;
        string html = "#" + ColorUtility.ToHtmlStringRGB(color);
        PlayerPrefs.SetString(ColorKey, html); PlayerPrefs.Save();
        if (backgroundColorInput != null) backgroundColorInput.SetTextWithoutNotify(html);
        ApplyPanelAppearance();
        SetStatus("面板颜色已自动保存。");
    }

    private void ApplyPanelAppearance()
    {
        Color color = panelColor; color.a = 1f;
        foreach (RectTransform panel in new[] { chatPanel, settingsCard, contextMenuRoot })
        {
            if (panel == null) continue;
            var image = panel.GetComponent<UnityEngine.UI.Image>();
            if (image != null) image.color = color;
            foreach (TMP_Text text in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                Color surface = NearestVisibleSurface(text.transform, panel, color);
                Color linear = surface.linear;
                float luminance = 0.2126f * linear.r + 0.7152f * linear.g + 0.0722f * linear.b;
                // Pick the larger WCAG contrast ratio, including saturated custom colors.
                float whiteContrast = 1.05f / (luminance + 0.05f);
                float blackContrast = (luminance + 0.05f) / 0.05f;
                text.color = blackContrast > whiteContrast ? Color.black : Color.white;
            }
        }
    }

    private static Color NearestVisibleSurface(Transform child, Transform panel, Color fallback)
    {
        for (Transform current = child.parent; current != null; current = current.parent)
        {
            var mask = current.GetComponent<UnityEngine.UI.Mask>();
            var image = current.GetComponent<UnityEngine.UI.Image>();
            if (image != null && image.color.a > 0.1f && (mask == null || mask.showMaskGraphic)) return image.color;
            if (current == panel) break;
        }
        return fallback;
    }

    public void UpdateChatPlacement()
    {
        if (chatPanel == null || !chatPanel.gameObject.activeInHierarchy || characterRoot == null || worldCamera == null) return;
        var manualPlacement = chatPanel.GetComponent<DesktopPetPanelDrag>();
        if (manualPlacement != null && manualPlacement.HasCustomPosition) { manualPlacement.ClampToParent(); return; }
        var parent = chatPanel.parent as RectTransform;
        if (parent == null) return;
        if (!TryGetStableWorldBounds(out Bounds bounds)) return;
        Canvas canvas = parent.GetComponentInParent<Canvas>();
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            Vector3 point = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            Vector3 screen = worldCamera.WorldToScreenPoint(point);
            if (screen.z <= 0) continue;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, uiCamera, out Vector2 local))
            { min = Vector2.Min(min, local); max = Vector2.Max(max, local); }
        }
        if (float.IsInfinity(min.x)) return;
        const float gap = 16;
        Rect screenRect = DesktopPetPanelDrag.WorkArea(parent, GetComponent<TransparentWindowController>());
        float leftSpace = Mathf.Max(0, min.x - screenRect.xMin - gap * 2);
        float rightSpace = Mathf.Max(0, screenRect.xMax - max.x - gap * 2);
        bool placeLeft = leftSpace >= 380 || (rightSpace < 380 && leftSpace >= rightSpace);
        float available = placeLeft ? leftSpace : rightSpace;
        // A very narrow desktop still keeps the panel outside the character's bounds.
        float panelWidth = Mathf.Max(1, chatPanel.rect.width);
        float panelHeight = Mathf.Max(1, chatPanel.rect.height);
        float scale = Mathf.Min(1f, available / panelWidth, (screenRect.height - gap * 2) / panelHeight);
        scale = Mathf.Max(0.001f, scale);
        chatPanel.localScale = Vector3.one * scale;
        chatPanel.anchorMin = chatPanel.anchorMax = new Vector2(0.5f, 0.5f);
        chatPanel.pivot = new Vector2(0, 1);
        float width = panelWidth * scale, height = panelHeight * scale;
        float x = placeLeft ? min.x - gap - width : max.x + gap;
        float y = Mathf.Clamp(max.y + 20, screenRect.yMin + height + gap, screenRect.yMax - gap);
        chatPanel.anchoredPosition = new Vector2(Mathf.Clamp(x, screenRect.xMin + gap, screenRect.xMax - width - gap), y);
    }

    /// <summary>Recapture only after replacing the model, never in response to a bone gesture.</summary>
    public void RefreshPlacementBounds()
    {
        cachedCharacterRoot = characterRoot;
        hasStableBounds = false;
        characterRenderers = characterRoot != null ? characterRoot.GetComponentsInChildren<Renderer>(true) : null;
        rendererSignature = ModelSignature(characterRenderers);
        if (characterRenderers == null) return;
        bool hasBody = false;
        Bounds body = default;
        foreach (CapsuleCollider collider in characterRoot.GetComponentsInChildren<CapsuleCollider>(true)) {
            if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.GetComponent<PetCharacterAnimation>() == null) continue;
            body = collider.bounds; hasBody = true; break;
        }
        foreach (Renderer renderer in characterRenderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            // Unity already resolves the FBX's 100x mesh scale and skinned root-bone
            // compensation in world bounds. Capture once, then keep a root-local cache;
            // applying the mesh transform again to baked vertices double-scales this rig.
            Bounds world = renderer.bounds;
            if (hasBody) {
                // Some PMX files include invisible alternate costumes metres away. The
                // normalized body collider is the authoritative ground/head envelope.
                Vector3 low = Vector3.Max(world.min, new Vector3(body.center.x - body.size.y * 0.60f, body.min.y, body.center.z - body.size.y * 0.60f));
                Vector3 high = Vector3.Min(world.max, new Vector3(body.center.x + body.size.y * 0.60f, body.max.y, body.center.z + body.size.y * 0.60f));
                if (low.x > high.x || low.y > high.y || low.z > high.z) continue;
                world.SetMinMax(low, high);
            }
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = characterRoot.InverseTransformPoint(Corner(world, i));
                if (!hasStableBounds) { stableLocalBounds = new Bounds(point, Vector3.zero); hasStableBounds = true; }
                else stableLocalBounds.Encapsulate(point);
            }
        }
        if (hasStableBounds)
        {
            if (hasBody) {
                Vector3 minimum = stableLocalBounds.min, maximum = stableLocalBounds.max;
                minimum.y = characterRoot.InverseTransformPoint(new Vector3(body.center.x, body.min.y, body.center.z)).y;
                maximum.y = characterRoot.InverseTransformPoint(new Vector3(body.center.x, body.max.y, body.center.z)).y;
                stableLocalBounds.SetMinMax(minimum, maximum);
            }
            desktopLocalBounds = stableLocalBounds;
            // UI can reserve arm room, but desktop boundaries use the actual foot line.
            stableLocalBounds.Expand(new Vector3(0.20f, 0, 0.04f));
        }
    }

    private void EnsurePlacementBounds()
    {
        if (characterRoot != cachedCharacterRoot) { RefreshPlacementBounds(); return; }
        if (Time.unscaledTime < nextModelCheck) return;
        nextModelCheck = Time.unscaledTime + 0.75f;
        Renderer[] current = characterRoot != null ? characterRoot.GetComponentsInChildren<Renderer>(true) : null;
        // Mesh identity/hierarchy changes invalidate the cache; animated transforms do not.
        if (!hasStableBounds || ModelSignature(current) != rendererSignature) RefreshPlacementBounds();
    }

    private static int ModelSignature(Renderer[] renderers)
    {
        unchecked
        {
            int result = 17;
            if (renderers == null) return result;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                result = result * 31 + renderer.GetEntityId().GetHashCode();
                result = result * 31 + (renderer.enabled && renderer.gameObject.activeInHierarchy ? 1 : 0);
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                result = result * 31 + (mesh != null ? mesh.GetEntityId().GetHashCode() : 0);
            }
            return result;
        }
    }

    private static Vector3 Corner(Bounds bounds, int index) => bounds.center + Vector3.Scale(bounds.extents,
        new Vector3((index & 1) == 0 ? -1 : 1, (index & 2) == 0 ? -1 : 1, (index & 4) == 0 ? -1 : 1));

    public bool TryGetStableWorldBounds(out Bounds bounds)
    {
        EnsurePlacementBounds();
        bounds = default;
        if (!hasStableBounds || characterRoot == null) return false;
        bounds = new Bounds(characterRoot.TransformPoint(Corner(stableLocalBounds, 0)), Vector3.zero);
        for (int i = 1; i < 8; i++) bounds.Encapsulate(characterRoot.TransformPoint(Corner(stableLocalBounds, i)));
        return true;
    }

    public bool TryGetStableScreenBounds(out Rect rect)
    {
        rect = default;
        if (worldCamera == null) worldCamera = Camera.main;
        if (worldCamera == null || !TryGetStableWorldBounds(out Bounds bounds)) return false;
        Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 screen = worldCamera.WorldToScreenPoint(Corner(bounds, i));
            if (screen.z <= 0) continue;
            min = Vector2.Min(min, screen); max = Vector2.Max(max, screen);
        }
        if (float.IsInfinity(min.x)) return false;
        rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        return true;
    }

    public bool TryGetDesktopScreenBounds(out Rect rect)
    {
        EnsurePlacementBounds(); rect = default;
        if (!hasStableBounds || characterRoot == null || worldCamera == null) return false;
        Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
        for (int i = 0; i < 8; i++) {
            Vector3 screen = worldCamera.WorldToScreenPoint(characterRoot.TransformPoint(Corner(desktopLocalBounds, i)));
            if (screen.z <= 0) continue;
            min = Vector2.Min(min, screen); max = Vector2.Max(max, screen);
        }
        if (float.IsInfinity(min.x)) return false;
        rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y); return true;
    }

    private void SetStatus(string message) { if (appearanceStatus != null) appearanceStatus.text = message; }
}
