using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;

/// <summary>Explicit end-to-end memory checks against an isolated QA backend only.</summary>
public sealed class PetWorkspaceMemoryChecks : MonoBehaviour
{
    public const string TestUrl = "http://127.0.0.1:18081";
    [Serializable] private sealed class Health { public bool ok, real_rpa_enabled, llm_configured; public string app, mode; }
    [Serializable] private sealed class NewCard { public string id, name, persona; public string model_id = "builtin-lita"; }
    [Serializable] private sealed class SummaryWrite { public string content, revision; }
    [Serializable] private sealed class DocumentWrite { public string title, content, perspective, revision; }
    private readonly List<string> createdIds = new List<string>();
    private readonly StringBuilder evidence = new StringBuilder();
    private string output, failure, responseText, originalId;
    private bool exitAfter, finished, verifiedHealth, wasOpen;
    private int originalPage;
    private SettingsPanelController settings;
    private SettingsTabsController tabs;
    private BackendSettingsApi api;
    private PetCharacterCards cards;
    private PetWorkspaceMemoryPanel panel;
    private UnityWebRequest activeRequest;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartStandaloneCheck()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--xpet-workspace-memory-checks") < 0) return;
        int index = Array.IndexOf(args, "--xpet-workspace-memory-report-dir");
        if (index < 0 || index + 1 >= args.Length || !Path.IsPathRooted(args[index + 1]))
        {
            Debug.LogError("WORKSPACE_MEMORY_CHECKS_FAILED: Supply --xpet-workspace-memory-report-dir with an absolute path.");
            Application.Quit(1); return;
        }
        Run(args[index + 1], true);
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Desktop Pet/Run Workspace Memory Checks (Isolated Backend)")]
    public static void RunEditor()
    {
        if (!UnityEditor.EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        Run(Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/workspace-memory")), false);
    }
#endif

    public static void Run(string reportDirectory, bool quitWhenComplete = false)
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Workspace-memory checks require a running scene.");
        if (FindFirstObjectByType<PetWorkspaceMemoryChecks>() != null)
            throw new InvalidOperationException("Workspace-memory checks are already running.");
        if (!Path.IsPathRooted(reportDirectory)) throw new ArgumentException("The report directory must be absolute.");
        var runner = new GameObject("WorkspaceMemoryChecks").AddComponent<PetWorkspaceMemoryChecks>();
        runner.output = Path.GetFullPath(reportDirectory); runner.exitAfter = quitWhenComplete;
        runner.StartCoroutine(runner.RunSafe());
    }

    private IEnumerator RunSafe()
    {
        Directory.CreateDirectory(output);
        yield return Execute(Check(), message => failure = message);
        yield return Execute(Cleanup(), message => failure = (failure == null ? "" : failure + "\n") + "Cleanup stopped: " + message);
        RestoreUi(); Finish();
    }

    // Capture assertions from nested asynchronous operations and stop immediately.
    private static IEnumerator Execute(IEnumerator operation, Action<string> onFailure)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(operation);
        while (stack.Count > 0)
        {
            bool moved = false; object next = null; Exception error = null;
            try { moved = stack.Peek().MoveNext(); if (moved) next = stack.Peek().Current; }
            catch (Exception caught) { error = caught.GetBaseException(); }
            if (error != null)
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                onFailure(error.Message); yield break;
            }
            if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
            if (next is IEnumerator child) stack.Push(child);
            else yield return next;
        }
    }

    private IEnumerator Check()
    {
        RequireSafeUrl();
        yield return AssertHealthy();
        float deadline = Time.realtimeSinceStartup + 30;
        do
        {
            RequireSafeUrl();
            settings = FindFirstObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
            tabs = FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
            api = FindFirstObjectByType<BackendSettingsApi>();
            cards = FindFirstObjectByType<PetCharacterCards>();
            Require(Time.realtimeSinceStartup < deadline, "Timed out waiting for settings and character-card services.");
            yield return null;
        } while (settings == null || tabs == null || api == null || cards == null || !cards.Ready || cards.IsBusy);
        RequireSafeUrl();
        var chat = FindFirstObjectByType<PetBackendHttpClient>();
        Require(chat == null || (!chat.IsBusy && !chat.IsSpeaking), "Finish conversations and speech before running memory checks.");
        wasOpen = settings.IsOpen; originalPage = tabs.SelectedPage;
        settings.Close(); // Keep original-role memory editors inactive throughout setup and cleanup.
        RuntimeSettingsData initial = null;
        yield return Read<RuntimeSettingsData>("/api/settings", value => initial = value);
        Require(initial != null && initial.llm_provider == "offline" && !initial.enable_proactive_screen && !initial.enable_real_rpa,
            "QA settings must be offline, with proactive screen and real RPA disabled.");
        originalId = initial.active_character_id;
        Require(!string.IsNullOrEmpty(originalId), "The QA backend did not identify its active character.");
        string suffix = Guid.NewGuid().ToString("N");
        string marker = suffix.Substring(0, 8);
        string a = "qa_memory_" + suffix + "_a", b = "qa_memory_" + suffix + "_b";
        yield return CreateCharacter(a, "QA记忆角色A-" + marker);
        yield return CreateCharacter(b, "QA记忆角色B-" + marker);
        yield return Activate(a);
        settings.Open(); tabs.ShowPage("记忆");
        yield return WaitPanel(a);
        ValidateUi();
        var summaryInput = Field("WorkspaceMemorySummary");
        var titleInput = Field("WorkspaceMemoryDocumentTitle");
        var documentInput = Field("WorkspaceMemoryDocumentContent");
        Require(summaryInput.text == "" && panel.DocumentCount == 0, "A newly created QA character already has memory data.");

        string summaryOne = "当前在陪用户准备旅行。测试标记：" + marker;
        summaryInput.text = summaryOne;
        yield return Click("保存摘要", a);
        PetWorkspaceMemoryResponse saved = null;
        yield return MemoryRead("/summary", a, value => saved = value);
        Require(saved?.summary?.content == summaryOne && saved.summary.revision?.Length == 64, "The real summary Save button did not persist its input.");
        string firstRevision = saved.summary.revision;
        evidence.AppendLine("PASS: real summary input and 保存摘要 button saved text; GET readback returned the same content and revision.");

        string summaryTwo = "当前正在整理公园行程。测试标记：" + marker;
        summaryInput.text = summaryTwo;
        yield return MemoryRead("/summary", a, value => saved = value);
        Require(saved.summary.content == summaryOne, "Editing the summary auto-saved before the explicit button click.");
        yield return Click("保存摘要", a);
        yield return MemoryRead("/summary", a, value => saved = value);
        Require(saved.summary.content == summaryTwo && saved.summary.revision != firstRevision, "The second summary save did not update its revision.");
        string currentRevision = saved.summary.revision;
        yield return Write("/api/workspace-memory/summary?character_id=" + a, "PUT",
            JsonUtility.ToJson(new SummaryWrite { content = "旧版本不应提交。", revision = firstRevision }), 409);
        yield return MemoryRead("/summary", a, value => saved = value);
        Require(saved.summary.content == summaryTwo && saved.summary.revision == currentRevision, "A stale revision overwrote the saved summary.");
        evidence.AppendLine("PASS: typing alone leaves stored summary unchanged; a stale revision returns 409 and preserves the current content.");

        string summaryThree = "另一个编辑者已更新旅行安排。测试标记：" + marker;
        yield return Write("/api/workspace-memory/summary?character_id=" + a, "PUT",
            JsonUtility.ToJson(new SummaryWrite { content = summaryThree, revision = currentRevision }));
        string staleDraft = "这份前端草稿要保留。测试标记：" + marker;
        summaryInput.text = staleDraft;
        yield return Click("保存摘要", a);
        Require(summaryInput.text == staleDraft && panel.Status.Contains("已被修改"), "The real conflict response did not preserve the visible draft and explain the conflict.");
        yield return MemoryRead("/summary", a, value => saved = value);
        Require(saved.summary.content == summaryThree, "The conflicted UI save changed server memory.");
        yield return Click("放弃草稿并刷新", a);
        Require(summaryInput.text == summaryThree, "Explicit refresh did not reload the newer summary.");
        evidence.AppendLine("PASS: real summary Save button received a revision conflict, kept the draft, and explicit refresh loaded the newer version.");

        yield return Click("新建文档", a);
        string titleOne = "旅行计划-" + marker, documentOne = "我们准备周末去公园。测试标记：" + marker;
        titleInput.text = titleOne; documentInput.text = documentOne;
        yield return Click("保存文档", a);
        PetWorkspaceMemoryResponse listing = null, document = null;
        yield return MemoryRead("/documents", a, value => listing = value);
        Require(listing.documents?.Length == 1, "The real document Save button did not create one document.");
        string documentId = listing.documents[0].id;
        yield return MemoryRead("/documents/" + documentId, a, value => document = value);
        Require(document.document.title == titleOne && document.document.content == documentOne && document.document.perspective == "user",
            "New document title, content or user perspective did not match the real fields.");
        string documentRevision = document.document.revision;
        string titleTwo = "共同旅行计划-" + marker, documentTwo = "我记得用户想先去图书馆。测试标记：" + marker;
        titleInput.text = titleTwo; documentInput.text = documentTwo;
        yield return Click("视角：用户资料", a);
        yield return Click("保存文档", a);
        yield return MemoryRead("/documents/" + documentId, a, value => document = value);
        Require(document.document.title == titleTwo && document.document.content == documentTwo && document.document.perspective == "self"
            && document.document.revision != documentRevision, "Real document editing or perspective selection did not persist.");
        string editedRevision = document.document.revision;
        yield return Write("/api/workspace-memory/documents/" + documentId + "?character_id=" + a, "PUT",
            JsonUtility.ToJson(new DocumentWrite { title = titleOne, content = "旧文档不应提交。", perspective = "user", revision = documentRevision }), 409);
        yield return MemoryRead("/documents/" + documentId, a, value => document = value);
        Require(document.document.content == documentTwo && document.document.revision == editedRevision, "A stale document edit replaced the current document.");
        evidence.AppendLine("PASS: real document create/edit buttons and perspective control persisted exact title/content/self view; stale document revision returned 409 without changing data.");

        yield return Activate(b); yield return WaitPanel(b);
        Require(summaryInput.text == "" && titleInput.text == "" && documentInput.text == "" && panel.DocumentCount == 0,
            "Switching to QA-B retained QA-A's summary, editor or document list.");
        yield return ReadStatus("/api/workspace-memory/documents/" + documentId + "?character_id=" + b, 404);
        yield return Write("/api/workspace-memory/summary?character_id=" + a, "PUT",
            JsonUtility.ToJson(new SummaryWrite { content = "旧角色请求不能写入。", revision = saved.summary.revision }), 409);
        summaryInput.text = "这是另一个角色的独立摘要。测试标记：" + marker;
        yield return Click("保存摘要", b);
        yield return Activate(a); yield return WaitPanel(a);
        Require(summaryInput.text == summaryThree && panel.DocumentCount == 1, "QA-B's saved summary leaked into QA-A, or QA-A's document was lost.");
        yield return Click("查看编辑", a);
        Require(titleInput.text == titleTwo && documentInput.text == documentTwo, "The real document read button did not restore QA-A's saved document.");
        evidence.AppendLine("PASS: A→B→A binds the real panel to each role, clears old fields, rejects cross-role document IDs and stale role writes, and preserves each role's memory.");

        Canvas.ForceUpdateCanvases();
        var scroll = panel.GetComponentInParent<UnityEngine.UI.ScrollRect>();
        Require(scroll != null && scroll.viewport != null && scroll.content != null, "Memory page has no valid ScrollRect.");
        scroll.verticalNormalizedPosition = 0;
        yield return new WaitForEndOfFrame(); Canvas.ForceUpdateCanvases();
        var actions = panel.transform.Find("WorkspaceDocumentActions") as RectTransform;
        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, actions);
        Require(bounds.min.y >= scroll.viewport.rect.yMin - 3 && bounds.max.y <= scroll.viewport.rect.yMax + 3,
            "Document actions cannot be reached by scrolling to the bottom.");
        ScreenCapture.CaptureScreenshot(Path.Combine(output, "workspace-memory-ui.png"));
        yield return new WaitForEndOfFrame();
        evidence.AppendLine("PASS: workspace editor has working input/button bindings, nonzero layout, EventSystem/raycaster, and reachable document controls; captured workspace-memory-ui.png.");
    }

    private void ValidateUi()
    {
        Require(EventSystem.current != null && panel.GetComponentInParent<Canvas>()?.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null,
            "The memory editor has no EventSystem or Canvas raycaster.");
        Canvas.ForceUpdateCanvases();
        foreach (RectTransform row in panel.transform)
            if (row.gameObject.activeSelf) Require(row.rect.width > 0 && row.rect.height > 0, "Zero-size workspace UI row: " + row.name);
        foreach (string name in new[] { "WorkspaceMemorySummary", "WorkspaceMemoryDocumentTitle", "WorkspaceMemoryDocumentContent" })
        {
            var input = Field(name);
            Require(input.textComponent != null && input.textViewport != null && input.fontAsset != null,
                "Incomplete input binding: " + name);
        }
        Require(Field("WorkspaceMemorySummary").characterLimit == 4000 && Field("WorkspaceMemoryDocumentContent").characterLimit == 12000,
            "Workspace inputs do not match backend size limits.");
    }

    private TMP_InputField Field(string name)
    {
        var field = panel.transform.Find(name)?.GetComponent<TMP_InputField>();
        Require(field != null, "Missing workspace field: " + name); return field;
    }

    private IEnumerator Click(string caption, string identity)
    {
        yield return AssertHealthy();
        RequireSafeUrl();
        Require(createdIds.Contains(identity) && cards.ActiveId == identity && panel.BoundCharacterId == identity && !panel.IsBusy,
            "Refusing a UI action outside the newly created QA character.");
        var button = panel.GetComponentsInChildren<UnityEngine.UI.Button>().FirstOrDefault(value => value.GetComponentInChildren<TMP_Text>()?.text == caption);
        Require(button != null && button.interactable && button.targetGraphic != null && button.targetGraphic.raycastTarget,
            "Missing or disabled real workspace button: " + caption);
        button.onClick.Invoke();
        yield return WaitPanel(identity);
        evidence.AppendLine("UI BUTTON: " + caption + " (owned QA character, verified health.app and port 18081).");
    }

    private IEnumerator WaitPanel(string identity)
    {
        float deadline = Time.realtimeSinceStartup + 25;
        do
        {
            RequireSafeUrl();
            panel = tabs.FindPageContent("记忆")?.GetComponentInChildren<PetWorkspaceMemoryPanel>(true);
            Require(Time.realtimeSinceStartup < deadline, "Memory panel timed out: " + (panel != null ? panel.Status : "not installed"));
            yield return null;
        } while (panel == null || !panel.isActiveAndEnabled || panel.IsBusy || panel.BoundCharacterId != identity);
        Require(Field("WorkspaceMemorySummary").interactable, "The QA summary failed to load: " + panel.Status);
    }

    private IEnumerator CreateCharacter(string id, string name)
    {
        yield return Write("/api/characters", "POST", JsonUtility.ToJson(new NewCard { id = id, name = name, persona = "我是隔离测试角色，只记录本次测试资料。" }));
        var result = JsonUtility.FromJson<PetCharacterCardResult>(responseText);
        Require(result?.card?.id == id, "QA character creation returned an unexpected identity.");
        createdIds.Add(id);
    }

    private IEnumerator Activate(string identity)
    {
        Require(createdIds.Contains(identity) || identity == originalId, "Refusing activation outside this QA run.");
        yield return Write("/api/characters/" + Uri.EscapeDataString(identity) + "/activate", "POST", "{}");
        var result = JsonUtility.FromJson<PetCharacterCardResult>(responseText);
        Require(result?.settings?.active_character_id == identity, "The QA backend did not activate the expected identity.");
        RequireSafeUrl(); cards.Refresh();
        float deadline = Time.realtimeSinceStartup + 25;
        do { RequireSafeUrl(); Require(Time.realtimeSinceStartup < deadline, "Frontend character refresh timed out."); yield return null; }
        while (cards.IsBusy || !cards.Ready || cards.ActiveId != identity);
    }

    private IEnumerator MemoryRead(string path, string identity, Action<PetWorkspaceMemoryResponse> receive)
    {
        Require(createdIds.Contains(identity), "Refusing to read memory from a character not created by this check.");
        yield return Read<PetWorkspaceMemoryResponse>("/api/workspace-memory" + path + "?character_id=" + Uri.EscapeDataString(identity), value =>
        {
            Require(value != null && value.character_id == identity, "Memory response belongs to another character."); receive(value);
        });
    }

    private IEnumerator AssertHealthy()
    {
        verifiedHealth = false; RequireSafeUrl();
        yield return Read<Health>("/health", value =>
        {
            Require(value != null && value.ok && value.app == "desktop-pet-backend-qa" && value.mode == "offline" && !value.llm_configured && !value.real_rpa_enabled,
                "Safety gate rejected backend identity or mode; required desktop-pet-backend-qa, offline, RPA disabled.");
            verifiedHealth = true;
        });
        RequireSafeUrl();
    }

    private IEnumerator Write(string path, string method, string json, long expectedStatus = 200)
    {
        yield return AssertHealthy();
        RequireSafeUrl(); Require(verifiedHealth, "QA backend identity was not verified before a write.");
        yield return Send(path, method, json, expectedStatus);
    }
    private IEnumerator Read<T>(string path, Action<T> receive) where T : class
    {
        yield return Send(path, "GET", null, 200);
        T value = JsonUtility.FromJson<T>(responseText);
        Require(value != null, "Invalid JSON response at " + path); receive(value);
    }
    private IEnumerator ReadStatus(string path, long expectedStatus) { yield return Send(path, "GET", null, expectedStatus); }
    private IEnumerator Send(string path, string method, string json, long expectedStatus)
    {
        RequireSafeUrl();
        using var request = method == "GET" ? UnityWebRequest.Get(TestUrl + path) : PetBackendHttpClient.JsonRequest(TestUrl + path, method, json ?? "{}");
        activeRequest = request; request.timeout = 15; request.redirectLimit = 0;
        yield return request.SendWebRequest(); activeRequest = null;
        RequireSafeUrl();
        Require(request.responseCode == expectedStatus && (expectedStatus >= 400 || request.result == UnityWebRequest.Result.Success),
            "QA request stopped at " + method + " " + path.Split('?')[0] + ": expected " + expectedStatus + ", received " + request.responseCode + ".");
        responseText = request.downloadHandler.text;
    }
    private void RequireSafeUrl()
    {
        Require(DesktopPetPrefs.GetBackendBaseUrl() == TestUrl && (api == null || api.BackendBaseUrl == TestUrl),
            "Safety gate rejected service URL; both preferences and settings API must use http://127.0.0.1:18081. No write was permitted.");
    }

    private IEnumerator Cleanup()
    {
        if (createdIds.Count == 0) yield break;
        if (settings != null) settings.Close();
        yield return AssertHealthy();
        Require(!string.IsNullOrEmpty(originalId), "Cannot restore the QA active role without its original identity.");
        yield return Activate(originalId);
        foreach (string identity in createdIds)
            yield return Write("/api/characters/" + Uri.EscapeDataString(identity), "DELETE", "{}");
        cards.Refresh();
        evidence.AppendLine("PASS: restored the original QA active character and removed only this run's temporary cards. Disposable memory files may remain in the QA data directory.");
    }
    private void RestoreUi()
    {
        if (settings == null || tabs == null) return;
        settings.Close();
        if (!verifiedHealth || DesktopPetPrefs.GetBackendBaseUrl() != TestUrl || (api != null && api.BackendBaseUrl != TestUrl)) return;
        tabs.ShowPage(originalPage);
        var titles = (string[])typeof(SettingsTabsController).GetField("pageTitles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(tabs);
        if (wasOpen && originalPage >= 0 && originalPage < titles.Length && titles[originalPage] != "记忆") settings.Open();
        // Reopening the original Memory page would read memory not created by this run.
    }
    private void Finish()
    {
        finished = true;
        string result = (failure == null ? "PASSED\n" : "FAILED: " + failure + "\n") + evidence
            + "Safety: fixed loopback port 18081; health.app desktop-pet-backend-qa checked before each write/UI action; only newly created role memory was read or written.\n";
        File.WriteAllText(Path.Combine(output, "workspace-memory-checks.txt"), result, new UTF8Encoding(false));
        if (failure == null) Debug.Log("WORKSPACE_MEMORY_CHECKS_PASSED\n" + result); else Debug.LogError("WORKSPACE_MEMORY_CHECKS_FAILED\n" + result);
        if (exitAfter) Application.Quit(failure == null ? 0 : 1); else Destroy(gameObject);
    }
    private void OnDestroy()
    {
        if (activeRequest != null) activeRequest.Abort();
        if (finished || string.IsNullOrEmpty(output)) return;
        failure = "The scene or runner stopped before checks and safe QA cleanup completed.";
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "workspace-memory-checks.txt"), "FAILED: " + failure + "\n" + evidence, new UTF8Encoding(false));
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
