using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using static PetSettingsWidgets;

[Serializable] public sealed class PetWorkspaceMemoryRecord
{
    public string id, title, content, perspective, source, updated_at, revision;
}
[Serializable] public sealed class PetWorkspaceMemoryResponse
{
    public string character_id, id;
    public PetWorkspaceMemoryRecord summary, document;
    public PetWorkspaceMemoryRecord[] documents;
    public bool ok;
}

/// <summary>User-edited summaries and documents, installed on the existing Memory page.</summary>
[DisallowMultipleComponent]
public sealed class PetWorkspaceMemoryPanel : MonoBehaviour
{
    [Serializable] private sealed class SummaryWrite { public string content, revision; }
    [Serializable] private sealed class DocumentWrite { public string title, content, perspective, revision; }
    [Serializable] private sealed class RevisionWrite { public string revision; }
    private TMP_Text statusText, documentHeading, perspectiveText;
    private TMP_InputField summaryInput, titleInput, documentInput;
    private UnityEngine.UI.Button deleteButton, summarySaveButton;
    private RectTransform documentList;
    private PetCharacterCards characters;
    private PetWorkspaceMemoryRecord summary, document;
    private PetWorkspaceMemoryRecord[] records = Array.Empty<PetWorkspaceMemoryRecord>();
    private UnityWebRequest activeRequest;
    private bool built, summaryDirty, documentDirty, confirmingDelete;
    private int generation;
    private string boundBackendBaseUrl;
    public bool IsBusy { get; private set; }
    public string BoundCharacterId { get; private set; }
    public string Status => statusText != null ? statusText.text : "";
    public int DocumentCount => records.Length;
    private string CurrentCharacter => characters != null && characters.Ready ? characters.ActiveId : null;

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
        if (FindFirstObjectByType<PetWorkspaceMemoryInstaller>() == null)
            new GameObject("WorkspaceMemoryInstaller").AddComponent<PetWorkspaceMemoryInstaller>();
    }

    private void OnEnable()
    {
        characters = FindFirstObjectByType<PetCharacterCards>();
        if (!built) Build();
        CheckCharacter();
        if (summary == null && !IsBusy) RefreshWorkspace();
    }
    private void OnDisable() { CancelRequests(); }
    private void Update() { CheckCharacter(); }
    private void CheckCharacter()
    {
        if (characters == null) characters = FindFirstObjectByType<PetCharacterCards>();
        string current = CurrentCharacter;
        string connection = DesktopPetPrefs.GetBackendBaseUrl();
        if (string.IsNullOrEmpty(current) || (current == BoundCharacterId && string.Equals(connection, boundBackendBaseUrl, StringComparison.OrdinalIgnoreCase))) return;
        CancelRequests(); BoundCharacterId = current; boundBackendBaseUrl = connection;
        summary = null; records = Array.Empty<PetWorkspaceMemoryRecord>();
        summaryDirty = documentDirty = false; summaryInput.SetTextWithoutNotify("");
        NewDocument(); RenderList(); RefreshWorkspace();
    }
    private void CancelRequests()
    {
        generation++;
        confirmingDelete = false;
        if (activeRequest != null) { activeRequest.Abort(); activeRequest = null; }
        StopAllCoroutines(); IsBusy = false;
        if (built) UpdateControls();
    }

    private void Build()
    {
        built = true;
        Label(transform, "工作区记忆", 22, 32).name = "WorkspaceMemoryTitle";
        Label(transform, "为当前角色保存近期状态和较长的资料。开启长期记忆后，短摘要进入对话，相关文档按需读取。编辑内容需单独保存。", 14, 64);
        statusText = Label(transform, "等待当前角色…", 14, 36); statusText.name = "WorkspaceMemoryStatus"; statusText.color = Accent;
        summaryInput = Input(transform, "协作摘要 · “我”指当前角色", "例如：我正在陪用户准备周末活动，还有哪些事项待办…", 4000, 150, true);
        summaryInput.name = "WorkspaceMemorySummary"; summaryInput.textComponent.alignment = TextAlignmentOptions.TopLeft;
        summaryInput.onValueChanged.AddListener(_ => { summaryDirty = true; SetStatus("摘要草稿尚未保存。"); });
        var summaryActions = Row("WorkspaceSummaryActions", transform);
        summarySaveButton = Button(summaryActions, "保存摘要", SaveSummary, true);
        Button(summaryActions, "放弃草稿并刷新", DiscardAndRefresh);
        Label(transform, "记忆文档", 19, 28);
        documentList = Rect("WorkspaceMemoryDocuments", transform); Vertical(documentList); Height(documentList, 42);
        documentList.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = -1;
        Button(transform, "新建文档", NewDocument);
        documentHeading = Label(transform, "新文档", 18, 28);
        titleInput = Input(transform, "文档名称", "例如：旅行计划 / 饮食偏好", 100);
        titleInput.name = "WorkspaceMemoryDocumentTitle";
        documentInput = Input(transform, "文档正文", "保存偏好、共同经历或计划；不要写入密码和密钥。", 12000, 220, true);
        documentInput.name = "WorkspaceMemoryDocumentContent"; documentInput.textComponent.alignment = TextAlignmentOptions.TopLeft;
        titleInput.onValueChanged.AddListener(_ => MarkDocumentDirty());
        documentInput.onValueChanged.AddListener(_ => MarkDocumentDirty());
        var actions = Row("WorkspaceDocumentActions", transform);
        var perspectiveButton = Button(actions, "", TogglePerspective);
        perspectiveText = perspectiveButton.GetComponentInChildren<TMP_Text>();
        Button(actions, "保存文档", SaveDocument, true);
        deleteButton = Button(actions, "删除文档", DeleteDocument);
        Label(transform, "“用户资料”的“我”代表用户，“角色自身”的“我”代表当前角色。其他角色不会读取这些文档；原来的事实记忆与聊天记录继续保留。", 13, 66);
        NewDocument(); RenderList();
    }
    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        UpdateControls();
    }
    private void UpdateControls()
    {
        if (!built || summaryInput == null || deleteButton == null) return;
        bool ready = !IsBusy && !string.IsNullOrEmpty(CurrentCharacter) && CurrentCharacter == BoundCharacterId
            && string.Equals(DesktopPetPrefs.GetBackendBaseUrl(), boundBackendBaseUrl, StringComparison.OrdinalIgnoreCase);
        foreach (var button in GetComponentsInChildren<UnityEngine.UI.Button>()) button.interactable = ready;
        titleInput.interactable = documentInput.interactable = ready;
        summaryInput.interactable = summarySaveButton.interactable = ready && summary != null;
        deleteButton.interactable = ready && document != null && !string.IsNullOrEmpty(document.id);
        deleteButton.GetComponentInChildren<TMP_Text>().text = confirmingDelete ? "再次点击删除" : "删除文档";
        perspectiveText.text = document?.perspective == "self" ? "视角：角色自身" : "视角：用户资料";
    }
    private void MarkDocumentDirty()
    {
        documentDirty = true; confirmingDelete = false; SetStatus("文档草稿尚未保存。");
    }
    private void TogglePerspective()
    {
        if (IsBusy || document == null) return;
        document.perspective = document.perspective == "self" ? "user" : "self"; MarkDocumentDirty();
    }
    private void ShowSummary(PetWorkspaceMemoryRecord record)
    {
        summary = record; summaryInput.SetTextWithoutNotify(record.content ?? ""); summaryDirty = false;
    }
    private void ShowDocument(PetWorkspaceMemoryRecord record)
    {
        document = record; titleInput.SetTextWithoutNotify(record.title ?? ""); documentInput.SetTextWithoutNotify(record.content ?? "");
        documentHeading.text = string.IsNullOrEmpty(record.id) ? "新文档" : "编辑 · " + record.title;
        documentDirty = confirmingDelete = false; UpdateControls();
    }
    private void NewDocument()
    {
        if (!built || IsBusy) return;
        if (documentDirty) { SetStatus("请先保存文档，或点击“放弃草稿并刷新”后再新建。"); return; }
        ShowDocument(new PetWorkspaceMemoryRecord { perspective = "user", revision = "" });
    }
    private void RenderList()
    {
        foreach (Transform child in documentList) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        foreach (var record in records)
        {
            var item = record;
            var row = Row("WorkspaceDocument_" + record.id, documentList, 42);
            var label = Label(row, record.title + (record.perspective == "self" ? " · 角色" : " · 用户"), 14, 28);
            label.maxVisibleLines = 1; label.overflowMode = TextOverflowModes.Ellipsis;
            var size = label.GetComponent<UnityEngine.UI.LayoutElement>(); size.minWidth = 0; size.flexibleWidth = 3;
            Button(row, "查看编辑", () => LoadDocument(item.id));
        }
        if (records.Length == 0) Label(documentList, "暂无文档，点击新建文档后显式保存。", 13, 42);
        UpdateControls();
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
    }

    public void RefreshWorkspace()
    {
        if (!built || IsBusy) return;
        if (summaryDirty || documentDirty) { SetStatus("有尚未保存的草稿；保存后刷新，或选择“放弃草稿并刷新”。"); return; }
        string identity = CurrentCharacter;
        if (string.IsNullOrEmpty(identity)) { SetStatus("等待角色卡读取完成后显示记忆。"); return; }
        BoundCharacterId = identity; StartCoroutine(Run(FetchWorkspace(identity)));
    }
    private void DiscardAndRefresh()
    {
        if (IsBusy) return;
        summaryDirty = documentDirty = false; NewDocument(); RefreshWorkspace();
    }
    private IEnumerator FetchWorkspace(string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request("/summary", "GET", null, identity, value => result = value);
        if (result?.summary == null) yield break;
        ShowSummary(result.summary);
        yield return FetchList(identity);
    }
    private IEnumerator FetchList(string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request("/documents", "GET", null, identity, value => result = value);
        if (result?.documents == null) yield break;
        records = result.documents; RenderList(); SetStatus("当前角色 · " + records.Length + " 份文档。草稿只有点击保存才会生效。");
    }
    private void LoadDocument(string id)
    {
        if (IsBusy) return;
        if (documentDirty) { SetStatus("请先保存文档，或点击“放弃草稿并刷新”后再选择。"); return; }
        StartCoroutine(Run(FetchDocument(id, BoundCharacterId)));
    }
    private IEnumerator FetchDocument(string id, string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request("/documents/" + Uri.EscapeDataString(id), "GET", null, identity, value => result = value);
        if (result?.document == null) yield break;
        ShowDocument(result.document); SetStatus("已读取“" + result.document.title + "”。编辑后点击保存文档。");
    }
    private void SaveSummary()
    {
        if (IsBusy || summary == null) return;
        string json = JsonUtility.ToJson(new SummaryWrite { content = summaryInput.text, revision = summary.revision });
        StartCoroutine(Run(SaveSummaryRoutine(json, BoundCharacterId)));
    }
    private IEnumerator SaveSummaryRoutine(string json, string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request("/summary", "PUT", json, identity, value => result = value);
        if (result?.summary != null) { ShowSummary(result.summary); SetStatus("摘要已保存。开启长期记忆后用于当前角色的对话。"); }
    }
    private void SaveDocument()
    {
        if (IsBusy || document == null) return;
        if (string.IsNullOrWhiteSpace(titleInput.text) || string.IsNullOrWhiteSpace(documentInput.text)) { SetStatus("请填写文档名称和正文。"); return; }
        var write = new DocumentWrite { title = titleInput.text.Trim(), content = documentInput.text,
            perspective = document.perspective, revision = document.revision ?? "" };
        StartCoroutine(Run(SaveDocumentRoutine(JsonUtility.ToJson(write), document.id, BoundCharacterId)));
    }
    private IEnumerator SaveDocumentRoutine(string json, string id, string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request(string.IsNullOrEmpty(id) ? "/documents" : "/documents/" + Uri.EscapeDataString(id),
            string.IsNullOrEmpty(id) ? "POST" : "PUT", json, identity, value => result = value);
        if (result?.document == null) yield break;
        ShowDocument(result.document); yield return FetchList(identity); SetStatus("文档已保存。仅在话题相关时读取。");
    }
    private void DeleteDocument()
    {
        if (IsBusy || document == null || string.IsNullOrEmpty(document.id)) return;
        if (!confirmingDelete) { confirmingDelete = true; SetStatus("再次点击删除当前文档；该操作不会删除事实记忆或聊天记录。"); return; }
        StartCoroutine(Run(DeleteRoutine(document.id, document.revision, BoundCharacterId)));
    }
    private IEnumerator DeleteRoutine(string id, string revision, string identity)
    {
        PetWorkspaceMemoryResponse result = null;
        yield return Request("/documents/" + Uri.EscapeDataString(id), "DELETE", JsonUtility.ToJson(new RevisionWrite { revision = revision }), identity, value => result = value);
        if (result == null || !result.ok) yield break;
        ShowDocument(new PetWorkspaceMemoryRecord { perspective = "user", revision = "" });
        yield return FetchList(identity); SetStatus("文档已删除。");
    }
    private IEnumerator Run(IEnumerator operation)
    {
        IsBusy = true; UpdateControls();
        var pending = new Stack<IEnumerator>(); pending.Push(operation);
        try
        {
            while (pending.Count > 0)
            {
                bool moved = false; object next = null; Exception failure = null;
                try { moved = pending.Peek().MoveNext(); if (moved) next = pending.Peek().Current; }
                catch (Exception error) { failure = error; }
                if (failure != null) { SetStatus("记忆操作未完成：" + failure.Message); break; }
                if (!moved) { (pending.Pop() as IDisposable)?.Dispose(); continue; }
                if (next is IEnumerator child) pending.Push(child);
                else yield return next;
            }
        }
        finally
        {
            while (pending.Count > 0) (pending.Pop() as IDisposable)?.Dispose();
            IsBusy = false; UpdateControls();
        }
    }
    private IEnumerator Request(string path, string method, string json, string identity, Action<PetWorkspaceMemoryResponse> receive)
    {
        string connection = DesktopPetPrefs.GetBackendBaseUrl();
        if (string.IsNullOrEmpty(identity) || identity != CurrentCharacter || !string.Equals(connection, boundBackendBaseUrl, StringComparison.OrdinalIgnoreCase))
        { SetStatus("角色或后端地址已切换，请刷新工作区记忆。"); yield break; }
        int current = generation;
        string url = connection + "/api/workspace-memory" + path + "?character_id=" + Uri.EscapeDataString(identity);
        using var request = method == "GET" ? UnityWebRequest.Get(url) : PetBackendHttpClient.JsonRequest(url, method, json ?? "{}");
        activeRequest = request; request.timeout = 12; yield return request.SendWebRequest();
        activeRequest = null;
        if (current != generation || identity != CurrentCharacter || !string.Equals(connection, DesktopPetPrefs.GetBackendBaseUrl(), StringComparison.OrdinalIgnoreCase)) yield break;
        if (request.result != UnityWebRequest.Result.Success) { SetStatus(PetBackendHttpClient.ReadError(request)); yield break; }
        PetWorkspaceMemoryResponse result = null;
        try { result = JsonUtility.FromJson<PetWorkspaceMemoryResponse>(request.downloadHandler.text); }
        catch (Exception) { SetStatus("工作区记忆响应格式不正确。"); }
        if (result != null && result.character_id == identity) receive(result);
    }
}

