using System.Text;
using TMPro;
using UnityEngine;

/// <summary>Small, transient replies above the pet while the full chat panel is closed.</summary>
public sealed class PetSpeechBubble : MonoBehaviour
{
    [SerializeField] private PetBackendHttpClient backendClient;
    [SerializeField] private DesktopPetAppearance appearance;
    [SerializeField] private DesktopPetUIActions uiActions;
    [SerializeField] private GameObject chatPanel;
    [SerializeField] private RectTransform bubbleRoot;
    [SerializeField] private TMP_Text messageText;
    [SerializeField] private UnityEngine.UI.Button openChatButton;
    [SerializeField, Range(4f, 20f)] private float maximumVisibleSeconds = 14f;
    private readonly StringBuilder streamedReply = new StringBuilder();
    private float hideAt;
    private string message = "";
    private Transform headBone;
    private Transform characterRoot;
    private float headCrownOffset;
    private Vector2 placementVelocity;
    private bool placementReady;
    private Vector3 lastCharacterScale;
    private Vector2 lastBubbleSize;
    private bool loading;
    private float loadingStarted;
    private int loadingFrame = -1;
    private TextAlignmentOptions replyAlignment;
    private static readonly string[] LoadingFrames = {
        "<alpha=#FF>● <alpha=#88>● <alpha=#44>●",
        "<alpha=#88>● <alpha=#FF>● <alpha=#88>●",
        "<alpha=#44>● <alpha=#88>● <alpha=#FF>●",
        "<alpha=#88>● <alpha=#66>● <alpha=#88>●"
    };
    private const int MaxBubbleCharacters = 220;
    public const float BackgroundOpacity = 0.72f;
    public static string LoadingDots(float elapsed) => LoadingFrames[Mathf.FloorToInt(Mathf.Max(0, elapsed) / 0.22f) % LoadingFrames.Length];

    private void Awake()
    {
        if (backendClient == null) backendClient = GetComponent<PetBackendHttpClient>();
        if (appearance == null) appearance = GetComponent<DesktopPetAppearance>();
        if (uiActions == null) uiActions = GetComponent<DesktopPetUIActions>();
        var character = FindAnyObjectByType<PetCharacterAnimation>();
        RebindModel(character != null ? character.transform : null);
        if (bubbleRoot != null) bubbleRoot.gameObject.SetActive(false);
        if (messageText != null) replyAlignment = messageText.alignment;
    }

    public void Hide() => ClearDisplay();

    public void RebindModel(Transform model)
    {
        characterRoot = model;
        headBone = null;
        headCrownOffset = 0;
        Transform neck = null;
        if (model != null)
            foreach (Transform bone in model.GetComponentsInChildren<Transform>(true))
            {
                if (bone.name == "頭" || bone.name.Equals("Head", System.StringComparison.OrdinalIgnoreCase)) headBone = bone;
                if (bone.name == "首" || bone.name.Equals("Neck", System.StringComparison.OrdinalIgnoreCase)) neck = bone;
            }
        if (headBone != null)
        {
            // Prepared hitboxes describe the visible body. Imported skin bounds can also
            // contain distant invisible accessories and are unsuitable for head placement.
            float headY = model.InverseTransformPoint(headBone.position).y;
            float neckDistance = neck != null ? Mathf.Abs(headY - model.InverseTransformPoint(neck.position).y) : 0;
            CapsuleCollider body = model.GetComponent<CapsuleCollider>();
            bool hasBody = body != null && body.direction == 1 && body.height > 0;
            float bodyHeight = hasBody ? body.height : Mathf.Max(headY, neckDistance * 8);
            float crown = hasBody ? body.center.y + body.height * 0.5f - headY : neckDistance * 2;
            headCrownOffset = Mathf.Clamp(crown,
                bodyHeight * 0.04f, bodyHeight * 0.32f);
        }
        placementVelocity = Vector2.zero;
        placementReady = false;
        lastCharacterScale = Vector3.zero;
    }

    private void OnEnable()
    {
        if (backendClient != null)
        {
            backendClient.onChatSubmitted.AddListener(ResetForChat);
            backendClient.onReplyDelta.AddListener(AppendReply);
            backendClient.onReplyText.AddListener(ShowFinalReply);
            backendClient.onRequestError.AddListener(ShowMessage);
            backendClient.onReplyLoading.AddListener(HandleLoading);
            backendClient.onRequestCancelled.AddListener(ClearDisplay);
            backendClient.onConversationLoaded.AddListener(HandleConversationLoaded);
            if (backendClient.IsReplyLoading) HandleLoading(backendClient.ReplyLoadingMessage);
        }
        if (openChatButton != null) openChatButton.onClick.AddListener(OpenChat);
    }

    private void OnDisable()
    {
        if (backendClient != null)
        {
            backendClient.onChatSubmitted.RemoveListener(ResetForChat);
            backendClient.onReplyDelta.RemoveListener(AppendReply);
            backendClient.onReplyText.RemoveListener(ShowFinalReply);
            backendClient.onRequestError.RemoveListener(ShowMessage);
            backendClient.onReplyLoading.RemoveListener(HandleLoading);
            backendClient.onRequestCancelled.RemoveListener(ClearDisplay);
            backendClient.onConversationLoaded.RemoveListener(HandleConversationLoaded);
        }
        if (openChatButton != null) openChatButton.onClick.RemoveListener(OpenChat);
        ClearDisplay();
    }

    private void LateUpdate()
    {
        if (bubbleRoot == null) return;
        bool visible = (loading || (!string.IsNullOrEmpty(message) && (Time.unscaledTime < hideAt ||
            (backendClient != null && backendClient.SpeechSource != null && backendClient.SpeechSource.isPlaying)))) &&
            (chatPanel == null || !chatPanel.activeInHierarchy);
        if (bubbleRoot.gameObject.activeSelf != visible) {
            bubbleRoot.gameObject.SetActive(visible);
            placementReady = false;
        }
        if (visible) {
            if (loading) UpdateLoadingDots();
            UpdatePlacement();
        }
    }

    private void ResetForChat(string submitted)
    {
        ClearDisplay();
    }

    private void ClearDisplay()
    {
        loading = false;
        streamedReply.Clear();
        message = "";
        hideAt = 0;
        if (messageText != null) { messageText.richText = false; messageText.text = ""; messageText.alignment = replyAlignment; }
        if (bubbleRoot != null) bubbleRoot.gameObject.SetActive(false);
    }

    private void HandleConversationLoaded(string text) => ClearDisplay();

    private void HandleLoading(string status)
    {
        if (string.IsNullOrEmpty(status)) {
            if (loading) ClearDisplay();
            return;
        }
        ClearDisplay();
        loading = true;
        loadingStarted = Time.unscaledTime;
        loadingFrame = -1;
        ApplyBubbleColors();
        if (bubbleRoot != null) bubbleRoot.sizeDelta = new Vector2(160, 88);
        if (messageText != null) messageText.alignment = TextAlignmentOptions.Center;
        UpdateLoadingDots();
    }

    private void UpdateLoadingDots()
    {
        int frame = Mathf.FloorToInt((Time.unscaledTime - loadingStarted) / 0.22f);
        if (frame == loadingFrame || messageText == null) return;
        loadingFrame = frame;
        messageText.richText = true;
        messageText.text = LoadingDots(Time.unscaledTime - loadingStarted);
    }

    private void AppendReply(string delta)
    {
        if (loading || string.IsNullOrEmpty(delta)) return;
        // The chat client retains the full transcript; the bubble only needs its opening excerpt.
        if (streamedReply.Length <= MaxBubbleCharacters)
            streamedReply.Append(delta, 0, Mathf.Min(delta.Length, MaxBubbleCharacters + 1 - streamedReply.Length));
        SetMessage(streamedReply.ToString(), maximumVisibleSeconds);
    }

    private void ShowFinalReply(string text)
    {
        loading = false;
        streamedReply.Clear();
        ShowMessage(text);
    }

    /// <summary>Display a local message, including future user-authorized proactive messages.</summary>
    public void ShowMessage(string text)
    {
        // Local touch hints and old callbacks must not replace an in-flight reply.
        if (loading || (backendClient != null && backendClient.IsReplyLoading)) return;
        float duration = Mathf.Clamp(4f + (text?.Length ?? 0) * 0.055f, 5f, maximumVisibleSeconds);
        SetMessage(text, duration);
    }

    private void SetMessage(string text, float duration)
    {
        message = (text ?? "").Trim();
        if (message.Length > MaxBubbleCharacters) message = message.Substring(0, MaxBubbleCharacters) + "…";
        hideAt = Time.unscaledTime + duration;
        if (messageText == null) return;
        messageText.richText = false;
        messageText.alignment = replyAlignment;
        messageText.text = message;
        ApplyBubbleColors();
        if (bubbleRoot != null)
        {
            float preferred = messageText.GetPreferredValues(message, 284, 0).y;
            bubbleRoot.sizeDelta = new Vector2(320, Mathf.Clamp(preferred + 48, 88, 200));
        }
    }

    private void ApplyBubbleColors()
    {
        if (ColorUtility.TryParseHtmlString(PlayerPrefs.GetString("desktop_pet.panel_color", "#182130"), out Color background)) {
            background.a = BackgroundOpacity;
            var image = bubbleRoot != null ? bubbleRoot.GetComponent<UnityEngine.UI.Image>() : null;
            if (image != null) image.color = background;
            Color linear = background.linear;
            float luminance = 0.2126f*linear.r + 0.7152f*linear.g + 0.0722f*linear.b;
            Color ink = (luminance+0.05f)/0.05f > 1.05f/(luminance+0.05f) ? Color.black : Color.white;
            if (bubbleRoot != null) foreach (TMP_Text textLabel in bubbleRoot.GetComponentsInChildren<TMP_Text>(true)) textLabel.color = ink;
        }
    }

    public void OpenChat()
    {
        if (uiActions != null) uiActions.OpenChat();
        hideAt = 0;
        if (bubbleRoot != null) bubbleRoot.gameObject.SetActive(false);
    }

    public void UpdatePlacement()
    {
        if (bubbleRoot == null || !TryGetHeadAnchor(out Vector2 screenHead)) return;
        Vector3 currentScale = characterRoot != null ? characterRoot.lossyScale : Vector3.one;
        if (currentScale != lastCharacterScale || bubbleRoot.sizeDelta != lastBubbleSize) {
            placementReady = false;
            placementVelocity = Vector2.zero;
            lastCharacterScale = currentScale;
            lastBubbleSize = bubbleRoot.sizeDelta;
        }
        RectTransform parent = bubbleRoot.parent as RectTransform;
        if (parent == null) return;
        Canvas canvas = parent.GetComponentInParent<Canvas>();
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                screenHead, uiCamera, out Vector2 head)) return;
        const float margin = 12;
        Rect bounds = DesktopPetPanelDrag.WorkArea(parent, GetComponent<TransparentWindowController>());
        float scale = Mathf.Max(0.01f, Mathf.Min(1, (bounds.width - margin * 2) / bubbleRoot.rect.width,
            (bounds.height - margin * 2) / bubbleRoot.rect.height));
        bubbleRoot.localScale = Vector3.one * scale;
        bubbleRoot.anchorMin = bubbleRoot.anchorMax = new Vector2(0.5f, 0.5f);
        bubbleRoot.pivot = new Vector2(0.5f, 0);
        float width = bubbleRoot.rect.width * scale, height = bubbleRoot.rect.height * scale;
        Vector2 target = new Vector2(
            Mathf.Clamp(head.x, bounds.xMin + width * 0.5f + margin, bounds.xMax - width * 0.5f - margin),
            Mathf.Clamp(head.y + 16, bounds.yMin + margin, bounds.yMax - height - margin));
        // Keep the face unobstructed when a large pet leaves too little space above its head.
        if (head.y + 16 + height > bounds.yMax - margin) {
            float side = head.x > bounds.center.x ? -1 : 1;
            target.x = Mathf.Clamp(head.x + side * (width * 0.5f + 48),
                bounds.xMin + width * 0.5f + margin, bounds.xMax - width * 0.5f - margin);
        }
        bubbleRoot.anchoredPosition = placementReady
            ? Vector2.SmoothDamp(bubbleRoot.anchoredPosition, target, ref placementVelocity, 0.12f, Mathf.Infinity, Time.unscaledDeltaTime)
            : target;
        placementReady = true;
    }

    public bool TryGetHeadAnchor(out Vector2 point)
    {
        point = default;
        Camera camera = Camera.main;
        if (headBone != null && characterRoot != null && camera != null) {
            // Follow the head, with a crown offset captured from this model's own proportions.
            Vector3 screen = camera.WorldToScreenPoint(headBone.position + characterRoot.TransformVector(Vector3.up * headCrownOffset));
            if (screen.z <= 0) return false;
            point = screen;
            return true;
        }
        if (appearance == null || !appearance.TryGetStableScreenBounds(out Rect pet)) return false;
        point = new Vector2(pet.center.x, pet.yMax);
        return true;
    }
}
