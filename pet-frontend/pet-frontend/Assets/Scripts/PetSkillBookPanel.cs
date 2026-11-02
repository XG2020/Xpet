using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using static PetSettingsWidgets;

[Serializable] public sealed class PetSkillRecord
{
    public string id, name, description, markdown;
    public bool enabled, built_in;
}
[Serializable] public sealed class PetSkillList { public string character_id; public PetSkillRecord[] skills; }
[Serializable] public sealed class PetSkillResult { public string character_id; public PetSkillRecord skill; public bool ok; }

/// <summary>Edits the active character's skill book on the existing Tools page.</summary>
public sealed class PetSkillBookPanel : MonoBehaviour
{
    [Serializable] private sealed class WriteSkill { public string name, description, markdown; public bool enabled; }
    [Serializable] private sealed class EnabledSkill { public bool enabled; }
    [Serializable] private sealed class ImportSkill { public string path; }
    private TMP_Text statusText, enabledText, editorTitle;
    private TMP_InputField nameInput, descriptionInput, markdownInput, pathInput;
    private Transform listRoot;
    private UnityEngine.UI.Button saveButton, deleteButton, enabledButton;
    private PetCharacterCards characters;
    private PetSkillRecord draft;
    private PetSkillRecord[] records = Array.Empty<PetSkillRecord>();
    private UnityWebRequest activeRequest;
    private int generation;
    private bool built;
    public bool IsBusy { get; private set; }
    public string BoundCharacterId { get; private set; }
    public int SkillCount => records.Length;
    public string Status => statusText != null ? statusText.text : "";
    private string CurrentCharacter => characters != null && characters.Ready ? characters.ActiveId : null;

    private void OnEnable()
    {
        if (!built) Build();
        characters = FindFirstObjectByType<PetCharacterCards>();
        RefreshBook();
    }
    private void OnDisable() { CancelRequests(); }
    private void Update()
    {
        if (characters == null) characters = FindFirstObjectByType<PetCharacterCards>();
        string current = CurrentCharacter;
        if (!string.IsNullOrEmpty(current) && current != BoundCharacterId)
        {
            CancelRequests(); BoundCharacterId = current; records = Array.Empty<PetSkillRecord>();
            RenderList(); NewSkill(); RefreshBook();
        }
    }
    private void CancelRequests()
    {
        generation++;
        if (activeRequest != null) { activeRequest.Abort(); activeRequest = null; }
        StopAllCoroutines(); IsBusy = false;
    }

    private void Build()
    {
        built = true;
        Label(transform, "技能书", 22, 32).name = "SkillBookTitle";
        Label(transform, "把常用规则和步骤写成技能。大模型会按需阅读当前角色已启用的技能，并结合可用动作和工具配合对话。", 14, 52);
        var top = Row("SkillBookActions", transform);
        Button(top, "新建技能", NewSkill, true);
        Button(top, "刷新技能书", RefreshBook);
        statusText = Label(transform, "正在等待当前角色…", 14, 34); statusText.name = "SkillBookStatus"; statusText.color = Accent;
        var list = Rect("SkillBookList", transform); Vertical(list); listRoot = list;
        Height(list, 44); list.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = -1;
        editorTitle = Label(transform, "新技能", 19, 28);
        nameInput = Input(transform, "技能名称", "例如：夜间陪伴", 64); nameInput.name = "SkillName";
        descriptionInput = Input(transform, "用途说明", "什么时候适合使用这项技能", 500, 72, true); descriptionInput.name = "SkillDescription";
        markdownInput = Input(transform, "技能正文", "用 Markdown 写下规则、步骤和示例…", 60000, 260, true); markdownInput.name = "SkillMarkdown";
        markdownInput.textComponent.alignment = TextAlignmentOptions.TopLeft;
        var actions = Row("SkillEditorActions", transform);
        enabledButton = Button(actions, "启用技能", ToggleEnabled);
        enabledText = enabledButton.GetComponentInChildren<TMP_Text>();
        saveButton = Button(actions, "保存技能", SaveSkill, true);
        deleteButton = Button(actions, "删除技能", DeleteSkill);
        Button(transform, "另存为新技能", CopyAsNew);
        Label(transform, "内置技能可关闭，内容可另存为新技能。自定义技能支持编辑、启用、关闭和删除，每个角色单独保存。", 13, 46);
        Label(transform, "导入技能文件", 19, 28);
        pathInput = Input(transform, "Markdown 文件路径", "选择 SKILL.md 或其他 .md 文件", 4096); pathInput.name = "SkillImportPath";
        var import = Row("SkillImportActions", transform);
        Button(import, "选择技能文件", PickFile);
        Button(import, "导入技能", ImportFile, true);
        Label(transform, "支持 UTF-8 编码的 SKILL.md 或 .md 文件，最大 64 KB。可使用 name、description 开头信息和 Markdown 正文。导入保留原文件；技能按文字指引使用，不执行文件中的程序。", 13, 72).name = "SkillImportTutorial";
        NewSkill();
    }

    private void SetStatus(string message) { if (statusText != null) statusText.text = message; UpdateControls(); }
    private void UpdateControls()
    {
        if (!built) return;
        foreach (var button in GetComponentsInChildren<UnityEngine.UI.Button>()) button.interactable = !IsBusy;
        deleteButton.interactable = !IsBusy && draft != null && !draft.built_in && !string.IsNullOrEmpty(draft.id);
        nameInput.readOnly = descriptionInput.readOnly = markdownInput.readOnly = draft != null && draft.built_in;
        enabledText.text = "技能：" + (draft != null && draft.enabled ? "已启用" : "已关闭");
    }
    private void RenderList()
    {
        foreach (Transform child in listRoot) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        foreach (var record in records)
        {
            var item = record;
            var row = Row("Skill_" + record.id, listRoot, 58);
            var text = Label(row, record.name + " · " + (record.enabled ? "启用" : "关闭") + (record.built_in ? " · 内置" : "") + "\n" + record.description, 13, 48);
            text.maxVisibleLines = 2; text.overflowMode = TextOverflowModes.Ellipsis;
            var size = text.GetComponent<UnityEngine.UI.LayoutElement>(); size.minWidth = 0; size.flexibleWidth = 3;
            Button(row, "编辑", () => LoadSkill(item.id));
        }
        if (records.Length == 0) Label(listRoot, "还没有读取到技能。点击刷新，或新建自己的技能。", 13, 44);
        UpdateControls();
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
    }
    public void NewSkill()
    {
        if (IsBusy || !built) return;
        draft = new PetSkillRecord { enabled = true };
        nameInput.SetTextWithoutNotify(""); descriptionInput.SetTextWithoutNotify(""); markdownInput.SetTextWithoutNotify("");
        editorTitle.text = "新技能"; UpdateControls();
    }
    private void CopyAsNew()
    {
        if (IsBusy || draft == null) return;
        draft = new PetSkillRecord { enabled = draft.enabled };
        editorTitle.text = "另存技能草稿"; SetStatus("填写名称后点击保存，即可创建独立技能。");
    }
    private void ShowDraft(PetSkillRecord record)
    {
        draft = record; nameInput.SetTextWithoutNotify(record.name); descriptionInput.SetTextWithoutNotify(record.description);
        markdownInput.SetTextWithoutNotify(record.markdown); editorTitle.text = record.built_in ? "内置技能 · 可关闭或另存" : "编辑技能";
        UpdateControls();
    }

    public void RefreshBook()
    {
        if (!built || IsBusy) return;
        string identity = CurrentCharacter;
        if (string.IsNullOrEmpty(identity)) { SetStatus("等待角色卡读取完成后显示技能书。"); return; }
        BoundCharacterId = identity; StartCoroutine(Run(FetchList(identity)));
    }
    private void LoadSkill(string id) { if (!IsBusy) StartCoroutine(Run(FetchSkill(id, BoundCharacterId))); }
    private IEnumerator FetchList(string identity)
    {
        PetSkillList result = null;
        yield return Request<PetSkillList>("", "GET", null, identity, value => result = value);
        if (result?.skills == null) yield break;
        records = result.skills; RenderList(); SetStatus("当前角色 · " + records.Length + " 个技能；大模型仅使用已启用的技能。");
    }
    private IEnumerator FetchSkill(string id, string identity)
    {
        PetSkillResult result = null;
        yield return Request<PetSkillResult>("/" + Uri.EscapeDataString(id), "GET", null, identity, value => result = value);
        if (result?.skill != null) { ShowDraft(result.skill); SetStatus("已读取“" + result.skill.name + "”。"); }
    }
    private void SaveSkill()
    {
        if (IsBusy || draft == null) return;
        if (string.IsNullOrWhiteSpace(nameInput.text) || string.IsNullOrWhiteSpace(markdownInput.text)) { SetStatus("请填写技能名称和正文。"); return; }
        string json = draft.built_in ? JsonUtility.ToJson(new EnabledSkill { enabled = draft.enabled })
            : JsonUtility.ToJson(new WriteSkill { name = nameInput.text.Trim(), description = descriptionInput.text.Trim(), markdown = markdownInput.text, enabled = draft.enabled });
        StartCoroutine(Run(SaveRoutine(json, draft.id, BoundCharacterId)));
    }
    private IEnumerator SaveRoutine(string json, string id, string identity)
    {
        PetSkillResult result = null;
        yield return Request<PetSkillResult>(string.IsNullOrEmpty(id) ? "" : "/" + Uri.EscapeDataString(id), string.IsNullOrEmpty(id) ? "POST" : "PUT", json, identity, value => result = value);
        if (result?.skill == null) yield break;
        ShowDraft(result.skill); yield return FetchList(identity); SetStatus("技能已保存。");
    }
    private void ToggleEnabled()
    {
        if (IsBusy || draft == null) return;
        draft.enabled = !draft.enabled; UpdateControls();
        if (string.IsNullOrEmpty(draft.id)) { SetStatus("技能开关会随新技能一起保存。"); return; }
        StartCoroutine(Run(ToggleRoutine(draft.id, draft.enabled, BoundCharacterId)));
    }
    private IEnumerator ToggleRoutine(string id, bool enabled, string identity)
    {
        PetSkillResult result = null;
        yield return Request<PetSkillResult>("/" + Uri.EscapeDataString(id), "PUT", JsonUtility.ToJson(new EnabledSkill { enabled = enabled }), identity, value => result = value);
        if (result?.skill == null) { if (draft?.id == id) draft.enabled = !enabled; UpdateControls(); yield break; }
        yield return FetchList(identity); SetStatus(enabled ? "技能已启用。" : "技能已关闭。");
    }
    private void DeleteSkill()
    {
        if (!IsBusy && draft != null && !draft.built_in && !string.IsNullOrEmpty(draft.id)) StartCoroutine(Run(DeleteRoutine(draft.id, BoundCharacterId)));
    }
    private IEnumerator DeleteRoutine(string id, string identity)
    {
        PetSkillResult result = null;
        yield return Request<PetSkillResult>("/" + Uri.EscapeDataString(id), "DELETE", "{}", identity, value => result = value);
        if (result == null || !result.ok) yield break;
        draft = new PetSkillRecord { enabled = true }; nameInput.SetTextWithoutNotify(""); descriptionInput.SetTextWithoutNotify(""); markdownInput.SetTextWithoutNotify(""); editorTitle.text = "新技能";
        yield return FetchList(identity); SetStatus("技能已删除。");
    }
    private void PickFile()
    {
        if (IsBusy) return;
        try { string path = PetSkillFileDialog.PickSkill(); if (!string.IsNullOrEmpty(path)) pathInput.SetTextWithoutNotify(path); }
        catch (Exception error) { SetStatus(error.Message); }
    }
    private void ImportFile()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(pathInput.text)) { SetStatus("请先选择 Markdown 技能文件。"); return; }
        StartCoroutine(Run(ImportRoutine(pathInput.text.Trim(), BoundCharacterId)));
    }
    private IEnumerator ImportRoutine(string path, string identity)
    {
        PetSkillResult result = null;
        yield return Request<PetSkillResult>("/import", "POST", JsonUtility.ToJson(new ImportSkill { path = path }), identity, value => result = value);
        if (result?.skill == null) yield break;
        ShowDraft(result.skill); yield return FetchList(identity); SetStatus("已导入“" + result.skill.name + "”，原文件已保留。");
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
                if (failure != null) { SetStatus("技能操作未完成：" + failure.Message); break; }
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
    private IEnumerator Request<T>(string path, string method, string json, string identity, Action<T> receive) where T : class
    {
        if (string.IsNullOrEmpty(identity) || identity != CurrentCharacter) { SetStatus("角色已切换，请刷新技能书。"); yield break; }
        int current = generation;
        string url = DesktopPetPrefs.GetBackendBaseUrl() + "/api/skill-book" + path + "?character_id=" + Uri.EscapeDataString(identity);
        using var request = method == "GET" ? UnityWebRequest.Get(url) : PetBackendHttpClient.JsonRequest(url, method, json ?? "{}");
        activeRequest = request; request.timeout = 12; yield return request.SendWebRequest();
        activeRequest = null;
        if (current != generation || identity != CurrentCharacter) yield break;
        if (request.result != UnityWebRequest.Result.Success) { SetStatus(PetBackendHttpClient.ReadError(request)); yield break; }
        T result = null;
        try { result = JsonUtility.FromJson<T>(request.downloadHandler.text); }
        catch (Exception) { SetStatus("技能书响应格式不正确。"); }
        string receivedCharacter = result is PetSkillList list ? list.character_id : result is PetSkillResult record ? record.character_id : identity;
        if (result != null && receivedCharacter == identity) receive(result);
    }
}
