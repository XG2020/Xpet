using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>One persistent row per message, with a single mutable assistant preview.</summary>
public sealed class PetChatTranscriptView : MonoBehaviour
{
    private sealed class MessageRow
    {
        public RectTransform root;
        public UnityEngine.UI.LayoutElement bubbleLayout;
        public TMP_Text sender;
        public TMP_Text body;
        public bool user;
        public bool incomplete;
    }

    private readonly List<MessageRow> rows = new List<MessageRow>();
    private UnityEngine.UI.ScrollRect scroll;
    private RectTransform content;
    private TMP_FontAsset font;
    private Material fontMaterial;
    private Sprite roundedBubble;
    private float fontSize = 17;
    private MessageRow pendingUser;
    private MessageRow pendingReply;
    private bool loading;
    private float loadingStarted;
    private int loadingFrame = -1;
    private float lastWidth;
    private bool scrollToBottom;
    private bool initialized;
    private string petName = "丽塔";
    private const int MaximumRows = 160;

    public void Initialize(UnityEngine.UI.ScrollRect target, TMP_Text plainText)
    {
        if (initialized || target == null || target.content == null || plainText == null) return;
        initialized = true;
        scroll = target;
        content = target.content;
        font = plainText.font;
        fontMaterial = plainText.fontSharedMaterial;
        roundedBubble = Resources.Load<Sprite>("DesktopPetUIRound");
        fontSize = Mathf.Clamp(plainText.fontSize, 15, 20);
        string welcome = plainText.text;
        // Keep the existing transcript for accessibility/debugging and legacy callbacks.
        plainText.gameObject.SetActive(false);
        if (!string.IsNullOrWhiteSpace(welcome)) AddRow(false, petName, welcome);
        scrollToBottom = true;
    }

    public void BeginReply(string userText, bool showUser)
    {
        if (!initialized) return;
        RemovePendingRows();
        if (showUser && !string.IsNullOrWhiteSpace(userText)) pendingUser = AddRow(true, "你", userText);
        pendingReply = AddRow(false, petName, "");
        SetLoading(true);
        scrollToBottom = true;
    }

    public void UpdateReply(string text)
    {
        if (!initialized) return;
        FollowIfNearBottom();
        loading = false;
        EnsureReply();
        SetBody(pendingReply, text, false);
    }

    public void CompleteReply(string text)
    {
        if (!initialized) return;
        UpdateReply(text);
        pendingReply = null;
        pendingUser = null;
        TrimRows();
    }

    public void FailReply(string partial)
    {
        if (!initialized) return;
        loading = false;
        if (string.IsNullOrEmpty(partial)) RemovePendingRows();
        else {
            UpdateReply(partial);
            pendingReply.incomplete = true;
            pendingReply.sender.text = petName + " · 本轮未完成";
            pendingReply = null;
            pendingUser = null;
        }
    }

    public void SetLoading(bool value)
    {
        if (!initialized) return;
        if (!value) {
            if (loading && pendingReply != null) {
                RemoveRow(pendingReply);
                pendingReply = null;
            }
            loading = false;
            return;
        }
        FollowIfNearBottom();
        EnsureReply();
        loading = true;
        loadingStarted = Time.unscaledTime;
        loadingFrame = -1;
        SetBody(pendingReply, PetSpeechBubble.LoadingDots(0), true);
    }

    public void LoadMessages(PetHistoryMessage[] messages)
    {
        if (!initialized) return;
        ClearRows();
        if (messages != null) foreach (PetHistoryMessage message in messages) {
            if (message == null || string.IsNullOrWhiteSpace(message.content)) continue;
            if (message.role == "user" && !string.IsNullOrWhiteSpace(message.interaction_zone)) continue;
            if (message.role != "user" && message.role != "assistant") continue;
            bool user = message.role == "user";
            AddRow(user, user ? "你" : petName, message.content);
        }
        if (rows.Count == 0) AddRow(false, petName, "新的聊天已开始。");
        TrimRows();
        scrollToBottom = true;
    }

    public void LoadTranscript(string text)
    {
        if (!initialized) return;
        // Legacy callers only: match explicit role lines, preserving ordinary blank lines.
        var messages = new List<PetHistoryMessage>();
        string normalized = (text ?? "").Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        PetHistoryMessage current = null;
        var body = new System.Text.StringBuilder();
        for (int index = 0; index < lines.Length; index++) {
            bool boundary = (index == 0 || lines[index - 1].Length == 0) &&
                (lines[index] == "你" || lines[index] == "丽塔" || lines[index] == "丽塔 · 本轮未完成"
                    || lines[index] == petName || lines[index] == petName + " · 本轮未完成");
            if (boundary) {
                if (current != null) { current.content = body.ToString().TrimEnd('\n'); messages.Add(current); }
                current = new PetHistoryMessage { role = lines[index] == "你" ? "user" : "assistant" };
                body.Clear();
            } else {
                if (current == null) current = new PetHistoryMessage { role = "assistant" };
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[index]);
            }
        }
        if (current != null) { current.content = body.ToString().TrimEnd('\n'); messages.Add(current); }
        LoadMessages(messages.ToArray());
    }

    private void EnsureReply()
    {
        if (pendingReply == null) pendingReply = AddRow(false, petName, "");
    }

    public void SetPetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        petName = value.Trim();
        foreach (MessageRow row in rows)
            if (!row.user && row.sender != null) row.sender.text = petName + (row.incomplete ? " · 本轮未完成" : "");
    }

    private MessageRow AddRow(bool user, string sender, string text)
    {
        var root = Rect(user ? "UserMessage" : "LitaMessage", content);
        var horizontal = root.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        horizontal.childControlWidth = true;
        horizontal.childControlHeight = true;
        horizontal.childForceExpandWidth = false;
        horizontal.childForceExpandHeight = false;
        horizontal.childAlignment = user ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
        horizontal.padding = new RectOffset(0, 0, 22, 0);
        // The name belongs to the row, above the colored bubble. Keep the bubble's
        // existing hierarchy so streaming, accessibility and history consumers are unchanged.
        TMP_Text senderLabel = Label("Sender", root, sender, 12, new Color(0.72f, 0.80f, 0.89f));
        senderLabel.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
        senderLabel.textWrappingMode = TextWrappingModes.NoWrap;
        senderLabel.overflowMode = TextOverflowModes.Ellipsis;
        senderLabel.alignment = user ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;
        senderLabel.rectTransform.anchorMin = senderLabel.rectTransform.anchorMax = new Vector2(user ? 1 : 0, 1);
        senderLabel.rectTransform.pivot = new Vector2(user ? 1 : 0, 1);
        senderLabel.rectTransform.anchoredPosition = Vector2.zero;
        senderLabel.rectTransform.sizeDelta = new Vector2(92, 18);
        if (user) AddSpacer(root);
        RectTransform bubble = Rect("MessageBubble", root);
        var image = bubble.gameObject.AddComponent<UnityEngine.UI.Image>();
        image.raycastTarget = false;
        if (roundedBubble != null) { image.sprite = roundedBubble; image.type = UnityEngine.UI.Image.Type.Sliced; }
        image.color = user ? new Color(0.16f, 0.34f, 0.52f, 1) : new Color(0.13f, 0.18f, 0.25f, 1);
        var layout = bubble.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        var vertical = bubble.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        vertical.padding = new RectOffset(12, 12, 8, 10);
        vertical.spacing = 4;
        vertical.childControlWidth = true;
        vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;
        var row = new MessageRow {
            root = root, bubbleLayout = layout, user = user,
            sender = senderLabel,
            body = Label("Message", bubble, text, fontSize, Color.white)
        };
        if (!user) AddSpacer(root);
        rows.Add(row);
        SetWidth(row);
        return row;
    }

    private TMP_Text Label(string name, Transform parent, string text, float size, Color color)
    {
        TMP_Text label = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        if (fontMaterial != null) label.fontSharedMaterial = fontMaterial;
        label.fontSize = size;
        label.color = color;
        label.richText = false;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Overflow;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.raycastTarget = false;
        label.text = text ?? "";
        return label;
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var result = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        result.SetParent(parent, false);
        return result;
    }

    private static void AddSpacer(Transform parent)
    {
        var spacer = Rect("Gutter", parent).gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        spacer.minWidth = 18;
        spacer.flexibleWidth = 1;
    }

    private void SetBody(MessageRow row, string text, bool rich)
    {
        row.body.richText = rich;
        row.body.text = text ?? "";
        SetWidth(row);
    }

    private void SetWidth(MessageRow row)
    {
        float available = Mathf.Max(120, content.rect.width - 12);
        float maximum = Mathf.Max(96, available * 0.86f);
        float desired = row.body.GetPreferredValues(row.body.text, Mathf.Infinity, 0).x + 24;
        row.bubbleLayout.preferredWidth = Mathf.Clamp(desired, 92, maximum);
        row.bubbleLayout.flexibleWidth = 0;
        row.sender.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, row.bubbleLayout.preferredWidth);
    }

    private void FollowIfNearBottom()
    {
        if (scroll == null || content == null) return;
        if (scroll.verticalNormalizedPosition <= 0.08f || scroll.viewport == null ||
            content.rect.height <= scroll.viewport.rect.height + 4) scrollToBottom = true;
    }

    private void LateUpdate()
    {
        if (!initialized) return;
        if (loading && pendingReply != null) {
            int frame = Mathf.FloorToInt((Time.unscaledTime - loadingStarted) / 0.22f);
            if (frame != loadingFrame) {
                loadingFrame = frame;
                pendingReply.body.text = PetSpeechBubble.LoadingDots(Time.unscaledTime - loadingStarted);
            }
        }
        if (Mathf.Abs(lastWidth - content.rect.width) > 0.5f) {
            lastWidth = content.rect.width;
            foreach (MessageRow row in rows) SetWidth(row);
        }
        if (scrollToBottom) {
            scrollToBottom = false;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0;
        }
    }

    private void RemovePendingRows()
    {
        loading = false;
        if (pendingReply != null) RemoveRow(pendingReply);
        if (pendingUser != null) RemoveRow(pendingUser);
        pendingReply = null;
        pendingUser = null;
    }

    private void RemoveRow(MessageRow row)
    {
        rows.Remove(row);
        if (row.root == null) return;
        row.root.gameObject.SetActive(false);
        if (Application.isPlaying) Destroy(row.root.gameObject);
        else DestroyImmediate(row.root.gameObject);
    }

    private void ClearRows()
    {
        pendingReply = pendingUser = null;
        loading = false;
        while (rows.Count > 0) RemoveRow(rows[rows.Count - 1]);
    }

    private void TrimRows()
    {
        while (rows.Count > MaximumRows) RemoveRow(rows[0]);
    }
}
