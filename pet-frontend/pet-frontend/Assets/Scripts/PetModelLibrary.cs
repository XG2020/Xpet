using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public sealed class PetModelEntry
{
    public string id;
    public string label;
    public string bundle_path;
}

/// <summary>Loads prepared model bundles without moving the user's character container.</summary>
[DisallowMultipleComponent]
public sealed class PetModelLibrary : MonoBehaviour
{
    public const string BuiltinModelId = "builtin-lita";
    public const string SelectedModelKey = "desktop_pet.selected_model";
    public static string LibraryPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../../../pet-models"));
    [SerializeField] private Transform characterRoot;
    private readonly List<PetModelEntry> models = new List<PetModelEntry>();
    private GameObject builtinModel;
    private GameObject activeModel;
    public Transform ActiveModelRoot => activeModel != null ? activeModel.transform : null;
    private bool initialized;
    private GameObject retainedModel;
    private string retainedModelId;
    private bool selectionTransaction;
    public bool IsBusy { get; private set; }
    public string SelectedModelId { get; private set; } = BuiltinModelId;
    public string Status { get; private set; } = "可导入 PMX、FBX 或已准备好的 PETMODEL 模型包。";
    public bool StatusIsError { get; private set; }
    public IReadOnlyList<PetModelEntry> ModelEntries => models;
    public event Action StatusChanged;

    [Serializable] private sealed class Manifest { public string id; public string label; public string bundle_file; public string unity_version; }
    [Serializable] private sealed class ModelList { public PetModelEntry[] models; }
    [Serializable] private sealed class ImportRequest { public string path; }
    [Serializable] private sealed class ImportJob { public string job_id; public string status; public string error; public string model_id; public string message; }

    public void Configure(Transform root) { characterRoot = root; }

    private void Awake()
    {
        if (characterRoot == null) characterRoot = GameObject.Find("PetCharacterRoot")?.transform;
        if (characterRoot != null)
            foreach (Transform child in characterRoot)
                if (child.gameObject.activeSelf && child.GetComponent<PetCharacterAnimation>() != null)
                { builtinModel = child.gameObject; break; }
        activeModel = builtinModel;
        try { ScanLocalLibrary(); }
        catch (Exception error) { SetStatus("暂时无法读取本机模型库：" + error.Message, true); }
    }

    private void Start() { Run(Initialize()); }

    private IEnumerator Initialize()
    {
        if (builtinModel == null) throw new InvalidOperationException("场景中未找到内置模型，请先升级模型库设置。");
        initialized = true;
        string saved = PlayerPrefs.GetString(SelectedModelKey, BuiltinModelId);
        if (saved == BuiltinModelId) { Rebind(builtinModel); yield break; }
        if (models.Any(entry => entry.id == saved)) yield return LoadModel(saved, false);
        else SetStatus("上次的模型文件未找到，已使用内置丽塔。刷新模型库后可重新选择。", true);
    }

    public void RefreshModels() { Run(RefreshLibrary()); }
    public void SelectModel(string id) { if (!string.IsNullOrWhiteSpace(id)) Run(LoadModel(id, true)); }
    public void ImportModel(string path) { if (!string.IsNullOrWhiteSpace(path)) Run(Import(path)); }

    public void BeginSelectionTransaction()
    {
        if (IsBusy || selectionTransaction) throw new InvalidOperationException("模型正在切换，请稍后重试。");
        retainedModel = activeModel; retainedModelId = SelectedModelId; selectionTransaction = true;
    }

    public void CompleteSelectionTransaction(bool commit)
    {
        if (!selectionTransaction) return;
        var previous = retainedModel; string previousId = retainedModelId;
        selectionTransaction = false; retainedModel = null; retainedModelId = null;
        if (!commit && previous != null && previous != activeModel) CommitModel(previous, previousId, true);
        else if (commit && previous != null && previous != builtinModel && previous != activeModel) Destroy(previous);
        if (!commit) SetStatus("已保留原服饰。");
    }

    private void Run(IEnumerator operation)
    {
        if (IsBusy) { (operation as IDisposable)?.Dispose(); return; }
        StartCoroutine(RunOperation(operation));
    }

    // Flatten child routines so any IO/asset exception still clears the busy state.
    private IEnumerator RunOperation(IEnumerator operation)
    {
        IsBusy = true;
        Notify();
        var pending = new Stack<IEnumerator>();
        pending.Push(operation);
        try
        {
            while (pending.Count > 0)
            {
                IEnumerator current = pending.Peek();
                bool moved = false;
                object next = null;
                Exception failure = null;
                try { moved = current.MoveNext(); if (moved) next = current.Current; }
                catch (Exception error) { failure = error; }
                if (failure != null) { SetStatus("模型操作未完成：" + failure.Message, true); break; }
                if (!moved) { (pending.Pop() as IDisposable)?.Dispose(); continue; }
                if (next is IEnumerator child) pending.Push(child);
                else yield return next;
            }
        }
        finally
        {
            while (pending.Count > 0) (pending.Pop() as IDisposable)?.Dispose();
            IsBusy = false;
            Notify();
        }
    }

    private void ScanLocalLibrary()
    {
        models.Clear();
        models.Add(new PetModelEntry { id = BuiltinModelId, label = "内置 · 丽塔" });
        if (!Directory.Exists(LibraryPath)) return;
        IEnumerable<string> manifests = Directory.EnumerateDirectories(LibraryPath)
            .Select(folder => Path.Combine(folder, "model.json"));
        foreach (string path in manifests)
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 65536) continue;
                Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path, Encoding.UTF8));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.id) || manifest.id == BuiltinModelId
                    || string.IsNullOrWhiteSpace(manifest.bundle_file) || models.Any(item => item.id == manifest.id)) continue;
                string bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), manifest.bundle_file));
                if (!IsLibraryBundle(bundle) || !File.Exists(bundle)) continue;
                models.Add(new PetModelEntry { id = manifest.id, label = string.IsNullOrWhiteSpace(manifest.label) ? manifest.id : manifest.label,
                    bundle_path = bundle });
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { Debug.LogWarning("A model manifest could not be read: " + Path.GetFileName(Path.GetDirectoryName(path))); }
        }
        Notify();
    }

    private static bool IsLibraryBundle(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string absolute = Path.GetFullPath(path).Replace('\\', '/');
        string root = LibraryPath.Replace('\\', '/').TrimEnd('/') + "/";
        return absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (Path.GetExtension(absolute).Equals(".petmodel", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(absolute).Equals(".petraw", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerator RefreshLibrary()
    {
        SetStatus("正在刷新模型列表…");
        ScanLocalLibrary();
        using (UnityWebRequest request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + "/api/models"))
        {
            request.timeout = 8;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                SetStatus("已读取本机模型库。导入服务暂不可用：" + PetBackendHttpClient.ReadError(request), true);
                yield break;
            }
            ModelList response = JsonUtility.FromJson<ModelList>(request.downloadHandler.text);
            if (response == null || response.models == null) throw new InvalidOperationException("模型列表响应格式不正确。");
            foreach (PetModelEntry entry in response.models)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || entry.id == BuiltinModelId
                    || !IsLibraryBundle(entry.bundle_path) || !File.Exists(entry.bundle_path)) continue;
                entry.bundle_path = Path.GetFullPath(entry.bundle_path);
                if (string.IsNullOrWhiteSpace(entry.label)) entry.label = entry.id;
                int existing = models.FindIndex(item => item.id == entry.id);
                if (existing < 0) models.Add(entry); else models[existing] = entry;
            }
        }
        SetStatus("模型库已刷新 · " + models.Count + " 个模型可选。");
    }

    private IEnumerator Import(string source)
    {
        string absolute = Path.GetFullPath(source);
        if (!File.Exists(absolute)) throw new FileNotFoundException("所选模型文件不存在。");
        string extension = Path.GetExtension(absolute).ToLowerInvariant();
        if (extension != ".pmx" && extension != ".fbx" && extension != ".petmodel")
            throw new InvalidOperationException("请选择 PMX、FBX 或 PETMODEL 文件。");
        SetStatus(extension == ".petmodel" ? "正在导入模型包，原文件会保留…" : "正在导入模型与贴图，无需 Blender 或 Unity 工程…");
        ImportJob job;
        using (var request = new UnityWebRequest(DesktopPetPrefs.GetBackendBaseUrl() + "/api/models/import", UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ImportRequest { path = absolute })));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 30;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException(PetBackendHttpClient.ReadError(request));
            job = JsonUtility.FromJson<ImportJob>(request.downloadHandler.text);
        }
        if (job == null || string.IsNullOrEmpty(job.job_id)) throw new InvalidOperationException("导入服务没有返回任务编号。");
        string jobId = job.job_id;
        double timeoutAt = Time.realtimeSinceStartupAsDouble + 1800;
        while (!IsComplete(job.status))
        {
            if (IsFailed(job.status)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(job.error) ? "模型转换失败，请检查模型与贴图文件。" : job.error);
            if (Time.realtimeSinceStartupAsDouble >= timeoutAt)
                throw new TimeoutException("模型准备仍未结束。稍后刷新模型列表查看结果。");
            SetStatus(string.IsNullOrWhiteSpace(job.message) ? "正在准备模型，请稍候…（" + JobLabel(job.status) + "）" : job.message);
            yield return new WaitForSecondsRealtime(1.5f);
            using (UnityWebRequest request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + "/api/models/jobs/" + Uri.EscapeDataString(jobId)))
            {
                request.timeout = 20;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException("暂时无法查询导入结果，可稍后刷新模型库。" + PetBackendHttpClient.ReadError(request));
                job = JsonUtility.FromJson<ImportJob>(request.downloadHandler.text);
                if (job == null) throw new InvalidOperationException("模型任务响应格式不正确。");
            }
        }
        if (string.IsNullOrWhiteSpace(job.model_id)) throw new InvalidOperationException("模型已准备，但服务没有返回模型编号。");
        yield return RefreshLibrary();
        // Reimporting the identical prepared pack does not need a second copy
        // of its already loaded bundle. Raw models may have changed textures.
        yield return LoadModel(job.model_id, true, extension != ".petmodel");
    }

    private static bool IsComplete(string status) => status == "completed" || status == "succeeded" || status == "success";
    private static bool IsFailed(string status) => status == "failed" || status == "error" || status == "cancelled";
    private static string JobLabel(string status)
    {
        switch (status) { case "queued": return "等待处理"; case "converting": return "转换模型";
            case "building": return "生成模型包"; case "importing": return "导入资源"; default: return "处理中"; }
    }

    private IEnumerator LoadModel(string id, bool saveSelection, bool forceReload = false)
    {
        if (!initialized || characterRoot == null || builtinModel == null) throw new InvalidOperationException("模型库尚未准备好。");
        PetModelEntry entry = models.FirstOrDefault(item => item.id == id);
        if (entry == null) throw new FileNotFoundException("模型不在当前列表中，请刷新后重试。");
        if (id == SelectedModelId && !forceReload)
        {
            // Explicitly choosing the fallback also clears a missing/broken saved selection.
            if (saveSelection) { PlayerPrefs.SetString(SelectedModelKey, id); PlayerPrefs.Save(); }
            SetStatus("当前模型：" + entry.label);
            yield break;
        }
        SetStatus("正在加载 " + entry.label + "…");
        if (id == BuiltinModelId)
        {
            CommitModel(builtinModel, id, saveSelection);
            yield return null;
            SetStatus("已切换到 " + entry.label + " · 大小和桌面位置已保留。");
            yield break;
        }
        if (!IsLibraryBundle(entry.bundle_path) || !File.Exists(entry.bundle_path)) throw new FileNotFoundException("模型包已移动或删除，请重新导入。");
        AssetBundle bundle = null;
        GameObject staging = null;
        GameObject candidate = null;
        bool committed = false;
        try
        {
            if (Path.GetExtension(entry.bundle_path).Equals(".petraw",StringComparison.OrdinalIgnoreCase))
            {
                yield return PetRuntimeModelLoader.CreateModel(entry.bundle_path,entry.label,characterRoot,builtinModel,value=>candidate=value);
                ValidateModel(candidate);
                CommitModel(candidate,id,saveSelection);
                committed=true;
                SetStatus("已切换到 "+entry.label+" · 大小和桌面位置已保留。");
                yield break;
            }
            AssetBundleCreateRequest loading = AssetBundle.LoadFromFileAsync(entry.bundle_path);
            yield return loading;
            bundle = loading.assetBundle;
            if (bundle == null) throw new InvalidOperationException("无法读取模型包；请使用当前桌宠版本重新导入原始模型。");
            // Our pack builder uses a stable addressable name independent of the
            // generated prefab filename. Also accept one conventional external prefab.
            string[] prefabs = bundle.Contains("model") ? new[] { "model" }
                : bundle.GetAllAssetNames().Where(path => Path.GetExtension(path).Equals(".prefab", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (prefabs.Length != 1) throw new InvalidOperationException("模型包必须包含且只包含一个角色预制体，请重新导入原始模型。");
            AssetBundleRequest asset = bundle.LoadAssetAsync<GameObject>(prefabs[0]);
            yield return asset;
            GameObject prefab = asset.asset as GameObject;
            ValidateModel(prefab);
            staging = new GameObject("ModelLoadStaging");
            staging.SetActive(false);
            staging.transform.SetParent(characterRoot, false);
            candidate = Instantiate(prefab, staging.transform, false);
            candidate.name = entry.label;
            candidate.SetActive(false);
            candidate.transform.SetParent(characterRoot, false);
            // Retain this bundle only while its model instance can be used or
            // rolled back. Destroying that instance releases its own assets.
            candidate.AddComponent<PetLoadedModelBundle>().Configure(bundle);
            bundle = null;
            // The prepared prefab owns its normalization; only the container owns user size/position.
            ValidateModel(candidate);
            CommitModel(candidate, id, saveSelection);
            committed = true;
            prefab = null;
            asset = null;
            yield return null; // Allow Destroy(oldModel) to release its renderer references first.
            SetStatus("已切换到 " + entry.label + " · 大小和桌面位置已保留。");
        }
        finally
        {
            if (!committed && candidate != null) Destroy(candidate);
            if (staging != null) Destroy(staging);
            if (bundle != null) bundle.Unload(!committed);
        }
    }

    private static void ValidateModel(GameObject model)
    {
        if (model == null) throw new InvalidOperationException("模型包中的预制体无法读取。");
        Animator animator = model.GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null || model.GetComponent<PetCharacterAnimation>() == null
            || model.GetComponent<PetSecondaryMotion>() == null || model.GetComponent<Collider>() == null
            || model.transform.Find("VisualModel") == null || !model.GetComponentsInChildren<Renderer>(true).Any(renderer =>
                renderer.enabled && (renderer is SkinnedMeshRenderer skin ? skin.sharedMesh != null
                    : renderer.GetComponent<MeshFilter>()?.sharedMesh != null)))
            throw new InvalidOperationException("模型缺少桌宠动画、可见网格或触摸范围，请重新导入原始模型。");
        if (model.GetComponentsInChildren<Camera>(true).Length > 0 || model.GetComponentsInChildren<AudioListener>(true).Length > 0)
            throw new InvalidOperationException("模型包含有额外相机或音频监听器，请重新导入原始模型。");
    }

    private void CommitModel(GameObject next, string id, bool saveSelection)
    {
        GameObject previous = activeModel;
        try
        {
            if (previous != null) previous.SetActive(false);
            next.SetActive(true);
            Animator animator = next.GetComponent<Animator>();
            if (animator != null) { animator.Rebind(); animator.Update(0); }
            Rebind(next);
        }
        catch
        {
            next.SetActive(false);
            if (previous != null) { previous.SetActive(true); Rebind(previous); }
            throw;
        }
        activeModel = next;
        SelectedModelId = id;
        if (saveSelection) { PlayerPrefs.SetString(SelectedModelKey, id); PlayerPrefs.Save(); }
        if (previous != null && previous != builtinModel && previous != next && (!selectionTransaction || previous != retainedModel)) Destroy(previous);
        Notify();
    }

    private void Rebind(GameObject model)
    {
        var character = model.GetComponent<PetCharacterAnimation>();
        GetComponent<PetBackendHttpClient>()?.RebindCharacter(character);
        GetComponent<TransparentWindowController>()?.RebindCharacter(character);
        foreach (PetAnimatorBridge bridge in characterRoot.GetComponents<PetAnimatorBridge>()) bridge.RebindModel(model.transform);
        GetComponent<PetAnimatorBridge>()?.RebindModel(model.transform);
        GetComponent<PetTouchFeedback>()?.RebindModel(model.transform);
        GetComponent<PetSpeechBubble>()?.RebindModel(model.transform);
        Physics.SyncTransforms();
        GetComponent<DesktopPetAppearance>()?.RefreshPlacementBounds();
        GetComponent<PetSpeechBubble>()?.UpdatePlacement();
    }

    private void SetStatus(string message, bool error = false) { Status = message; StatusIsError = error; Notify(); }
    private void Notify() { StatusChanged?.Invoke(); }
}

public sealed class PetLoadedModelBundle : MonoBehaviour
{
    private AssetBundle bundle;
    public void Configure(AssetBundle value) { bundle=value; }
    private void OnDestroy() { if(bundle!=null) bundle.Unload(true);bundle=null; }
}
