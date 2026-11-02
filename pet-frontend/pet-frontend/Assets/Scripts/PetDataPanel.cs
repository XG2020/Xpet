using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;

[Serializable] public class PetMemoryItem { public int id; public string key; public string content; public string category; public string source; public string updated_at; public string perspective; }
[Serializable] public class PetMemoryList { public PetMemoryItem[] memories; }
[Serializable] public class PetMemoryWrite { public string key; public string content; public string category = "preference"; public string perspective = "user"; }

/// <summary>User-controlled saved conversations and explicit memory editing.</summary>
public sealed class PetDataPanel : MonoBehaviour
{
    private RectTransform root, rows, editor;
    private TMP_Text heading, status;
    private TMP_InputField keyInput, contentInput;
    private Button perspectiveButton;
    private bool selfMemory;
    private Button saveButton, openButton, deleteButton;
    private TMP_FontAsset font;
    private Sprite rounded;
    private PetBackendHttpClient client;
    private bool memories, busy, confirmingDelete;
    private string selected;
    private readonly List<GameObject> rowObjects = new List<GameObject>();
    private static readonly Color Background = new Color32(24, 33, 48, 255);
    private static readonly Color Field = new Color32(36, 47, 63, 255);
    private static readonly Color Ink = new Color32(238, 244, 251, 255);

    public void OpenHistory() { Open(false); }
    public void OpenMemories() { Open(true); }
    public void Close() { if (root != null) root.gameObject.SetActive(false); }
    public void ResetForCharacterSwitch() { StopAllCoroutines(); busy = false; selected = null; Close(); }
    private void Open(bool memoryMode)
    {
        if (root == null) Build();
        memories = memoryMode; selected = null; confirmingDelete = false;
        root.gameObject.SetActive(true); root.SetAsLastSibling();
        if (ColorUtility.TryParseHtmlString(PlayerPrefs.GetString("desktop_pet.panel_color", "#182130"), out Color color)) {
            color.a = 1; root.GetComponent<Image>().color = color;
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true)) {
                Color surface = color;
                for (Transform parent = text.transform.parent; parent != null; parent = parent.parent) {
                    var image = parent.GetComponent<Image>();
                    if (image != null && image.color.a > 0.1f) { surface = image.color; break; }
                    if (parent == root) break;
                }
                Color linear = surface.linear;
                float luminance = 0.2126f*linear.r + 0.7152f*linear.g + 0.0722f*linear.b;
                text.color = (luminance+0.05f)/0.05f > 1.05f/(luminance+0.05f) ? Color.black : Color.white;
            }
        }
        FindAnyObjectByType<DesktopPetUIActions>()?.CloseContextMenu();
        heading.text = memories ? "长期记忆" : "聊天记录";
        editor.gameObject.SetActive(memories); saveButton.gameObject.SetActive(memories); openButton.gameObject.SetActive(!memories);
        Refresh();
    }
    private void LateUpdate()
    {
        if (root == null || !root.gameObject.activeSelf) return;
        var parent = (RectTransform)root.parent;
        Rect area = DesktopPetPanelDrag.WorkArea(parent, GetComponent<TransparentWindowController>());
        float scale = Mathf.Min(1, (area.width - 32) / root.rect.width, (area.height - 32) / root.rect.height);
        root.localScale = Vector3.one * Mathf.Max(0.1f, scale);
    }
    private void Build()
    {
        client = GetComponent<PetBackendHttpClient>();
        var canvas = GameObject.Find("DesktopPetCanvas").transform;
        font = Resources.Load<TMP_FontAsset>("DesktopPetUIFont");
        rounded = Resources.Load<Sprite>("DesktopPetUIRound");
        root = Box("HistoryPanel", canvas, Background);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f); root.sizeDelta = new Vector2(640, 660);
        var vertical = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vertical.padding = new RectOffset(20, 20, 20, 20); vertical.spacing = 10;
        vertical.childControlHeight = vertical.childControlWidth = true; vertical.childForceExpandHeight = false;
        RectTransform top = Row(root, 40); heading = Label(top, "聊天记录", 24); Flex(heading.rectTransform);
        DesktopPetPanelDrag.Attach(root, top);
        ActionButton(top, "关闭", Close, 66);
        RectTransform tabs = Row(root, 36);
        ActionButton(tabs, "聊天记录", OpenHistory, 110); ActionButton(tabs, "长期记忆", OpenMemories, 110); ActionButton(tabs, "刷新", Refresh, 78);
        RectTransform viewport = Box("Viewport", root, Field); viewport.gameObject.AddComponent<RectMask2D>();
        var vl = viewport.gameObject.AddComponent<LayoutElement>(); vl.minHeight = 140; vl.flexibleHeight = 1;
        var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.scrollSensitivity = 30;
        rows = Rect("Rows", viewport); rows.anchorMin = new Vector2(0,1); rows.anchorMax = Vector2.one; rows.pivot = new Vector2(0.5f,1); rows.sizeDelta = Vector2.zero;
        var layout = rows.gameObject.AddComponent<VerticalLayoutGroup>(); layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandHeight = false; layout.spacing = 6; layout.padding = new RectOffset(8,8,8,8);
        var fit = rows.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport; scroll.content = rows;
        editor = Rect("MemoryEditor", root); var el = editor.gameObject.AddComponent<VerticalLayoutGroup>();
        el.childControlWidth = el.childControlHeight = true; el.childForceExpandHeight = false; el.spacing = 7;
        keyInput = Input(editor, "记忆名称，例如：饮食偏好", 36);
        contentInput = Input(editor, "仅保存你希望桌宠记住的信息", 76); contentInput.lineType = TMP_InputField.LineType.MultiLineNewline; contentInput.characterLimit = 500;
        perspectiveButton = ActionButton(editor, "视角：关于用户", () => { selfMemory = !selfMemory; UpdatePerspective(); }, 0);
        status = Label(root, "", 14); Height(status.rectTransform, 44);
        RectTransform footer = Row(root, 38);
        ActionButton(footer, "新建", NewItem, 80);
        openButton = ActionButton(footer, "打开会话", OpenSelected, 110);
        saveButton = ActionButton(footer, "保存记忆", SaveMemory, 110);
        deleteButton = ActionButton(footer, "删除选中", DeleteSelected, 126);
        GetComponent<TransparentWindowController>()?.RegisterVisiblePanel(root);
    }
    public void Refresh()
    {
        if (root == null || busy) return;
        foreach (GameObject row in rowObjects) Destroy(row); rowObjects.Clear();
        selected = null; confirmingDelete = false; deleteButton.GetComponentInChildren<TMP_Text>().text = "删除选中";
        StartCoroutine(Fetch());
    }
    private IEnumerator Fetch()
    {
        busy = true; status.text = "正在读取…";
        string expected = client.CharacterId;
        using var request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + (memories ? "/api/memories" : "/api/conversations") + "?character_id=" + Uri.EscapeDataString(expected));
        request.timeout = 15; yield return request.SendWebRequest(); busy = false;
        if (expected != client.CharacterId) yield break;
        if (request.result != UnityWebRequest.Result.Success) { status.text = PetBackendHttpClient.ReadError(request); yield break; }
        try {
            if (memories) {
                var result = JsonUtility.FromJson<PetMemoryList>(request.downloadHandler.text);
                foreach (var item in result.memories ?? Array.Empty<PetMemoryItem>()) {
                    var copy = item;
                    AddRow((item.perspective == "self" ? "[我] " : "") + item.key + "\n" + item.content, () => { selected = copy.id.ToString(); keyInput.text = copy.key; contentInput.text = copy.content; selfMemory = copy.perspective == "self"; UpdatePerspective();
                        status.text = "来源：" + copy.source; ResetDelete(); });
                }
                status.text = rowObjects.Count == 0 ? "暂无记忆。可以手动添加，或在设置中开启长期记忆后说“记住…”。" : "选择记忆可以修改或删除。";
            } else {
                var result = JsonUtility.FromJson<PetConversationList>(request.downloadHandler.text);
                foreach (var item in result.conversations ?? Array.Empty<PetSavedConversation>()) {
                    var copy = item;
                    AddRow(item.title + "  ·  " + item.message_count + " 条", () => { selected = copy.id; status.text = "已选择：" + copy.title; ResetDelete(); });
                }
                status.text = rowObjects.Count == 0 ? "暂无记录。在设置中开启“保存聊天记录”，后续完整对话才会保存在本机。" : "选择一段会话，然后打开或删除。";
            }
        } catch (Exception) { status.text = "无法读取数据，请重试。"; }
    }
    private void AddRow(string text, UnityEngine.Events.UnityAction click)
    {
        var button = ActionButton(rows, text.Length > 160 ? text.Substring(0,160) + "…" : text, click, 0);
        Height(button.GetComponent<RectTransform>(), 60); rowObjects.Add(button.gameObject);
        button.GetComponentInChildren<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;
    }
    private void NewItem()
    {
        if (busy) return;
        selected = null; ResetDelete();
        if (memories) { keyInput.text = ""; contentInput.text = ""; status.text = "填写名称和内容后保存。"; }
        else { client.NewConversation(); Close(); FindAnyObjectByType<DesktopPetUIActions>()?.OpenChat(); }
    }
    private void OpenSelected()
    {
        if (busy || string.IsNullOrEmpty(selected)) { status.text = "请先选择会话。"; return; }
        client.LoadConversation(selected); Close(); FindAnyObjectByType<DesktopPetUIActions>()?.OpenChat();
    }
    private void SaveMemory()
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(keyInput.text) || string.IsNullOrWhiteSpace(contentInput.text)) { status.text = "名称和内容不能为空。"; return; }
        string path = "/api/memories" + (string.IsNullOrEmpty(selected) ? "" : "/" + Uri.EscapeDataString(selected));
        var payload = new PetMemoryWrite { key = keyInput.text.Trim(), content = contentInput.text.Trim(), perspective = selfMemory ? "self" : "user" };
        StartCoroutine(Mutate(path, string.IsNullOrEmpty(selected) ? "POST" : "PUT", JsonUtility.ToJson(payload)));
    }
    private void DeleteSelected()
    {
        if (busy || string.IsNullOrEmpty(selected)) { status.text = "请先选择一条记录。"; return; }
        if (!confirmingDelete) { confirmingDelete = true; deleteButton.GetComponentInChildren<TMP_Text>().text = "确认删除"; status.text = "再次点击“确认删除”，只删除当前选中记录。"; return; }
        StartCoroutine(Mutate((memories ? "/api/memories/" : "/api/conversations/") + Uri.EscapeDataString(selected), "DELETE", "{}"));
    }
    private void ResetDelete() { confirmingDelete = false; deleteButton.GetComponentInChildren<TMP_Text>().text = "删除选中"; }
    private void UpdatePerspective() { if (perspectiveButton != null) perspectiveButton.GetComponentInChildren<TMP_Text>().text = selfMemory ? "视角：角色自身（我）" : "视角：关于用户"; }
    private IEnumerator Mutate(string path, string method, string json)
    {
        busy = true; string expected = client.CharacterId;
        using var request = PetBackendHttpClient.JsonRequest(DesktopPetPrefs.GetBackendBaseUrl() + path + "?character_id=" + Uri.EscapeDataString(expected), method, json);
        yield return request.SendWebRequest(); busy = false;
        if (expected != client.CharacterId) yield break;
        if (request.result != UnityWebRequest.Result.Success) { status.text = PetBackendHttpClient.ReadError(request); yield break; }
        Refresh();
    }
    private RectTransform Rect(string name, Transform parent) { var go = new GameObject(name, typeof(RectTransform)); var rt = (RectTransform)go.transform; rt.SetParent(parent,false); return rt; }
    private RectTransform Box(string name, Transform parent, Color color) { var rt = Rect(name,parent); var image = rt.gameObject.AddComponent<Image>(); image.color=color; image.sprite=rounded; image.type=Image.Type.Sliced; return rt; }
    private TMP_Text Label(Transform parent, string value, float size) { var rt=Rect("Label",parent); var text=rt.gameObject.AddComponent<TextMeshProUGUI>(); text.font=font; text.fontSize=size; text.color=Ink; text.text=value; text.richText=false; text.raycastTarget=false; return text; }
    private RectTransform Row(Transform parent,float height) { var rt=Rect("Row",parent); Height(rt,height); var layout=rt.gameObject.AddComponent<HorizontalLayoutGroup>(); layout.spacing=8; layout.childControlWidth=layout.childControlHeight=true; layout.childForceExpandWidth=layout.childForceExpandHeight=false; return rt; }
    private void Height(RectTransform rt,float value) { var l=rt.GetComponent<LayoutElement>()??rt.gameObject.AddComponent<LayoutElement>(); l.minHeight=l.preferredHeight=value; }
    private void Flex(RectTransform rt) { (rt.GetComponent<LayoutElement>()??rt.gameObject.AddComponent<LayoutElement>()).flexibleWidth=1; }
    private void Stretch(RectTransform rt,float inset=0) { rt.anchorMin=Vector2.zero;rt.anchorMax=Vector2.one;rt.offsetMin=Vector2.one*inset;rt.offsetMax=-Vector2.one*inset; }
    private Button ActionButton(Transform parent,string label,UnityEngine.Events.UnityAction click,float width) {
        var rt=Box(label,parent,Field);Height(rt,36);var l=rt.GetComponent<LayoutElement>();l.preferredWidth=width;l.flexibleWidth=width==0?1:0;
        var b=rt.gameObject.AddComponent<Button>();b.targetGraphic=rt.GetComponent<Image>();b.onClick.AddListener(click);
        var t=Label(rt,label,15);Stretch(t.rectTransform,8);t.alignment=TextAlignmentOptions.Center;return b;
    }
    private TMP_InputField Input(Transform parent,string hint,float height) {
        var rt=Box("Input",parent,Field);Height(rt,height);var input=rt.gameObject.AddComponent<TMP_InputField>();input.targetGraphic=rt.GetComponent<Image>();
        var viewport=Rect("Viewport",rt);Stretch(viewport,8);viewport.gameObject.AddComponent<RectMask2D>();
        var text=Label(viewport,"",16);Stretch(text.rectTransform);var placeholder=Label(viewport,hint,15);Stretch(placeholder.rectTransform);
        input.textViewport=viewport;input.textComponent=text;input.placeholder=placeholder;input.fontAsset=font;input.characterLimit=240;PetInputPlaceholder.Install(input);return input;
    }
}
